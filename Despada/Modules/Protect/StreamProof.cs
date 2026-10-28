// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using Despada.ImGui.Hook;

namespace Despada.Modules.Protect;

/// <summary>
/// Keeps the game window out of screen capture by other programs: SetWindowDisplayAffinity with
/// WDA_EXCLUDEFROMCAPTURE (Windows 10 2004+), which the snipping tool, the Xbox Game Bar, Discord's
/// and OBS' window/desktop capture all respect — they get nothing, while what is on screen stays
/// exactly as it is for the player. The game's own F2 screenshot is a GL readback of the
/// framebuffer and is unaffected by this, so screenshots still come out, just without us.
/// </summary>
internal static class StreamProof
{
    private const uint WdaExcludeFromCapture = 0x11;
    private const uint WdaNone               = 0x00;

    private const int Win10_2004 = 19041;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    /// <summary>Why the last <see cref="Apply"/> returned false.</summary>
    public static string? Why { get; private set; }

    /// <summary>
    /// The game window's HWND: SDL's own property on the engine's SDL window (what the input hook
    /// already keeps), the process' main window as a fallback for engines on GLFW.
    /// </summary>
    public static nint WindowHandle()
    {
        try
        {
            if (Sdl3.Bind() && ImGuiInputHook.MainWindowReg() is { } reg)
            {
                var hwnd = Sdl3.Win32Hwnd(ImGuiInputHook.PointerField(reg, "Sdl3Window"));
                if (hwnd != 0) return hwnd;
            }
        }
        catch { /* fall through to the process' own window */ }

        try { using var proc = Process.GetCurrentProcess(); return proc.MainWindowHandle; }
        catch { return 0; }
    }

    /// <summary>Excludes <paramref name="hwnd"/> from capture; false with <see cref="Why"/> when the OS can't.</summary>
    public static bool Apply(nint hwnd)
    {
        Why = null;
        if (!OperatingSystem.IsWindows()) { Why = "Windows only"; return false; }
        if (Environment.OSVersion.Version.Build < Win10_2004) { Why = "needs Windows 10 2004 or newer"; return false; }
        if (hwnd == 0) { Why = "the game window isn't up yet"; return false; }

        try
        {
            if (!SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture))
            {
                Why = $"SetWindowDisplayAffinity failed (error {Marshal.GetLastWin32Error()})";
                return false;
            }
            return true;
        }
        catch (Exception ex) { Why = ex.Message; return false; }
    }

    /// <summary>Puts the window back into capture (the feature was switched off).</summary>
    public static void Clear(nint hwnd)
    {
        if (hwnd == 0 || !OperatingSystem.IsWindows()) return;
        try { SetWindowDisplayAffinity(hwnd, WdaNone); }
        catch { }
    }
}
