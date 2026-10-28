// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using Despada.Core;
using Despada.ImGui;
using Despada.ImGui.Hook;

namespace Despada.Modules.Protect;

/// <summary>
/// Keeps Despada out of every screenshot the client itself takes — F2 through Content's
/// ScreenshotHook, an admin's screengrab, <c>IClyde.ScreenshotAsync</c> — all of which end in the
/// single <c>Clyde.Screenshot</c> call that queues the frame.
///
/// Installed once and for good: switching the Protection feature off does not take this away, so a
/// capture can never catch the menu, the toasts or a HUD feature.
///
/// With the stream window up (<see cref="OverlayWindow"/>) nothing of ours is in the game's
/// framebuffer in the first place, so the frame is copied as it is — no flicker. Without it our
/// drawing goes down until the copy has happened.
/// </summary>
internal static class ScreenshotGuard
{
    /// <summary>Our drawing stays down this long after a capture is queued (the copy happens next render).</summary>
    private const int HideMs = 500;

    /// <summary>Captures are announced at most once per this many milliseconds.</summary>
    private const long NotifyEveryMs = 3000;

    private static long _lastNotify;

    /// <summary>The last capture the guard saw, shown in the Protection tab.</summary>
    public static DateTime? LastCapture { get; private set; }

    /// <summary>Who asked for it: "F2", "admin screengrab" or "client request".</summary>
    public static string LastCaptureBy { get; private set; } = "";

    /// <summary>Hooks <c>Clyde.Screenshot</c> under the infrastructure owner, so it outlives the feature.</summary>
    public static void Install()
    {
        if (!Hooks.Prefix("core", Engine.ClydeScreenshot, nameof(Prefix), typeof(ScreenshotGuard)))
            MarseyLogger.Warn("[ScreenshotGuard] Clyde.Screenshot could not be hooked — captures are not guarded");
    }

    /// <summary>
    /// <c>Clyde.Screenshot</c>: the queue, on the thread that asked. The frame is copied on the next
    /// render, so our drawing being down from here on covers it; rate-limited notification.
    /// </summary>
    private static void Prefix()
    {
        // The stream window keeps Despada out of the game's framebuffer entirely: the capture is
        // safe as it is and the overlay must not flicker for it.
        if (!OverlayWindow.Ready)
            ImGuiRenderer.SuppressDrawing(HideMs);

        var (user, admin) = DetectCause();
        LastCapture   = DateTime.Now;
        LastCaptureBy = admin ? "admin screengrab" : user ? "F2" : "client request";

        bool notify;
        try { notify = Features.GetSetting("protect", "notify", true); }
        catch { notify = false; }
        if (!notify) return;

        var now = Environment.TickCount64;
        if (now - _lastNotify < NotifyEveryMs) return;
        _lastNotify = now;

        ImGuiRenderer.ShowToast(
            "Screenshot detected",
            admin ? "Admin screengrab — Despada is out of the picture."
                  : user ? "F2 — Despada is out of the picture."
                         : "Client capture — Despada is out of the picture.", 3f);
    }

    /// <summary>
    /// Who asked, from the call stack (as Cerberus did): the keybind that fires F2 sits under
    /// Content's ScreenshotHook, an admin's capture under their capture/photo system.
    /// </summary>
    private static (bool User, bool Admin) DetectCause()
    {
        try
        {
            var trace  = new StackTrace(1, false);
            var frames = Math.Min(trace.FrameCount, 20);
            var user   = false;
            var admin  = false;

            for (var i = 0; i < frames; i++)
            {
                var method = trace.GetFrame(i)?.GetMethod();
                if (method?.DeclaringType?.FullName is not { } type) continue;
                var name = method.Name;

                if (type.Contains("ScreenshotHook")
                    || name.Contains("KeyBindDown") || name.Contains("OnUIKeyBindStateChanged")
                    || name.Contains("SetBindState")
                    || (type.Contains("InputManager") && name.Contains("KeyBind")))
                    user = true;

                if (type.Contains("CaptureSystem") || type.Contains("Photo")
                    || name.Contains("RequestCaptureScreen") || name.Contains("CaptureScreenRequest"))
                    admin = true;
            }

            return (user, admin);
        }
        catch { return (false, false); }
    }
}