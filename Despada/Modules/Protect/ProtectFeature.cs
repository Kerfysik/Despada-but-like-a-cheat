// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Despada.Core;
using Despada.Core.Bind;
using Despada.ImGui;
using Despada.ImGui.Hook;
using Despada.ImGui.UserInterface;

namespace Despada.Modules.Protect;

/// <summary>
/// Keeps Despada out of screen capture, three ways:
///
/// <list type="bullet">
/// <item><b>Stream</b> (the default): Despada draws into <see cref="OverlayWindow"/>, a separate
/// window laid over the game's, so the game window carries only the game. Streams and every other
/// capture see the game and never the menu or ESP, while the player still sees both.</item>
/// <item><b>Hide the game window</b>: the game window itself is excluded from capture
/// (<see cref="StreamProof"/>); nothing of it reaches a capture, the game included.</item>
/// <item><b>Off</b>: capture programs see the window as it is.</item>
/// </list>
///
/// Screenshots the client takes itself are handled separately and always, by
/// <see cref="ScreenshotGuard"/>.
/// </summary>
internal sealed class ProtectFeature : Feature
{
    public override string Id => "protect";

    public override string Title => "Anti-screengrab";

    public override string Description => "keeps Despada out of screenshots and screen captures";

    public override string? Tab => "Protection";

    public override bool EnabledByDefault => true;

    public override IReadOnlyList<Binding> Requires => [Engine.ClydeScreenshot];

    private const int ModeOff    = 0;
    private const int ModeHidden = 1;
    private const int ModeStream = 2;

    /// <summary>The window handle is re-checked every this many ticks: a fullscreen toggle makes a new one.</summary>
    private const int HandleCheckTicks = 60;

    private static bool _notify = true;

    // Stream-proof state; the tick and the enable/disable path are both on the game thread.
    private int   _mode = ModeStream;
    private nint  _appliedTo;
    private int   _ticks;
    private bool  _gaveUp;

    protected internal override void OnEnable()
    {
        _notify    = Setting("notify", true);
        _mode      = Math.Clamp(Setting("mode", ModeStream), ModeOff, ModeStream);
        _ticks     = 0;
        _gaveUp    = false;
        _appliedTo = 0;
        ImGuiRenderer.ResumeDrawing();

        OverlayWindow.SetWanted(_mode == ModeStream);
        Lifecycle.OnTick(Id, Tick);
        Log.Info(Id, _mode switch
        {
            ModeOff    => "capture programs see the window as it is; client screenshots still leave Despada out",
            ModeHidden => "the game window is excluded from capture outside the game",
            _          => "stream window: capture sees the game and none of Despada",
        });
    }

    protected internal override void OnDisable()
    {
        OverlayWindow.SetWanted(false);
        OverlayWindow.Sync();   // closes the window now, the tick is dropped right after this
        ClearWindow();
        ImGuiRenderer.ResumeDrawing();
        _gaveUp = false;
        _ticks  = 0;
    }

    // --- the window ---------------------------------------------------------------------------

    private void Tick()
    {
        OverlayWindow.SetWanted(_mode == ModeStream);
        OverlayWindow.Sync();

        if (_mode == ModeOff)
        {
            ClearWindow();
            return;
        }

        // Stream mode, window working: the game window stays IN capture (that is what a stream
        // shows) and nothing of ours is in its frames.
        if (_mode == ModeStream && OverlayWindow.Ready)
        {
            ClearWindow();
            return;
        }

        // Mode "hide", or stream mode that fell back (the failure already put the window out of
        // capture straight away; this keeps it that way across window changes).
        if (_gaveUp || ++_ticks % HandleCheckTicks != 0) return;

        var hwnd = StreamProof.WindowHandle();
        if (hwnd == 0 || hwnd == _appliedTo) return;

        if (StreamProof.Apply(hwnd))
        {
            if (_appliedTo == 0) Log.Info(Id, "the game window is excluded from screen capture outside the game");
            _appliedTo = hwnd;
        }
        else if (!_gaveUp)
        {
            _gaveUp = true;
            Log.Warn(Id, $"window capture exclusion is off: {StreamProof.Why}");
        }
    }

    private void ClearWindow()
    {
        if (_appliedTo == 0) return;
        StreamProof.Clear(_appliedTo);
        _appliedTo = 0;
        _gaveUp    = false;
    }

    // --- the tab ------------------------------------------------------------------------------

    public override void DrawTab()
    {
        Widgets.BeginSection("Anti-screengrab");
        {
            var notify = _notify;
            if (Widgets.CheckboxRow("Say when a capture is detected", ref notify))
            {
                _notify = notify;
                SetSetting("notify", notify);
            }

            var mode = _mode;
            if (Widgets.Combo("Window capture", ref mode,
                    [
                        "Off - the window is captured as it is",
                        "Hide the game window from capture",
                        "Stream - the overlay in its own window",
                    ]))
            {
                _mode = mode;
                SetSetting("mode", mode);
                OverlayWindow.SetWanted(mode == ModeStream);
                OverlayWindow.Sync();
                if (mode != ModeHidden) ClearWindow();
            }

            Widgets.MutedText(_mode switch
            {
                ModeOff => "Programs outside the game see the window as it is. The client's own screenshots still leave Despada out.",
                ModeStream when OverlayWindow.Ready =>
                    "Streams and captures of the game window show the game, never the menu or ESP: the overlay is drawn by a separate window they can't see.",
                ModeStream when OverlayWindow.Failed =>
                    $"The stream window is off ({OverlayWindow.Why}); the game window is hidden from capture instead.",
                ModeStream => "Preparing the stream window...",
                ModeHidden when _appliedTo != 0 =>
                    "Discord, OBS, Game Bar and the snipping tool get nothing of the window. F2 screenshots still work, without Despada in them.",
                ModeHidden when _gaveUp => $"The window can't be excluded: {StreamProof.Why}",
                _ => "Waiting for the game window...",
            });

            if (ScreenshotGuard.LastCapture is { } at)
                Widgets.MutedText($"Last capture: {at:HH:mm:ss} - {ScreenshotGuard.LastCaptureBy}");
        }
        Widgets.EndSection();
    }
}