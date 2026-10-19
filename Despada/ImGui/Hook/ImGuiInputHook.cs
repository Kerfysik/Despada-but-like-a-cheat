// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Concurrent;
using System.Numerics;
using Despada.Config;
using Despada.Core;
using Despada.ImGui.UserInterface;
using ImGuiNET;

namespace Despada.ImGui.Hook;

/// <summary>
/// Input for the overlay, taken from the window library itself, below the engine: an event filter
/// on SDL3, or callbacks in front of the engine's on GLFW, whichever the engine's window runs on.
/// What the overlay takes never reaches the engine; everything else passes untouched. No engine
/// method is patched for input, so a key the content "presses" through the input manager is none
/// of our business, and no fork's input code can get between us and the keyboard.
///
/// The backends (<see cref="SdlInput"/>, <see cref="GlfwInput"/>) only translate their events and
/// call the policy here. It runs on the window library's thread, which is the engine's window
/// thread by default: only volatile reads, writes into the queue, and posts to the game thread.
/// </summary>
public static unsafe class ImGuiInputHook
{
    private static readonly ConcurrentQueue<Action<ImGuiIOPtr>> _ioQueue = new();

    public static void EnqueueIO(Action<ImGuiIOPtr> action) => _ioQueue.Enqueue(action);

    public static volatile bool WantCaptureMouse = false;
    public static volatile bool WantCaptureKeyboard = false;
    /// <summary>An ImGui text field has focus (WantCaptureKeyboard also counts a panel's claim).</summary>
    public static volatile bool WantTextInput = false;

    // Keyboard policy. An active text field takes every key. Otherwise the shown panel declares,
    // every frame, the keys it listens for (Want: key + modifier combinations) and only those are
    // kept from the game; the rest, WASD included, play on with the overlay open. Declarations
    // apply from the next input batch, which runs before the next render. ClaimKeyboard is the
    // blunt form: every key, for a panel that really needs them all.
    private static bool _claimNext;
    private static bool _claimed;
    // Keyed by (int)ImGuiKey, a byte of Mod bits: no ImGui.NET value type in a field (type load).
    // A published table is never written to again: the input thread reads it.
    private static Dictionary<int, byte> _wantNext = new();
    private static volatile Dictionary<int, byte> _want = new();

    public static void ClaimKeyboard()
    {
        _claimNext = true;
        if (ImGuiRenderer.OverlayVisible) WantCaptureKeyboard = true;
    }

    /// <summary>
    /// The panel listens for this key with these modifier combinations (from the next input batch,
    /// for one frame: declare it every frame it is shown). Modifier keys themselves always reach
    /// the game.
    /// </summary>
    public static void Want(ImGuiKey key, Mod mods = Mod.None)
    {
        var k = (int)key;
        _wantNext[k] = (byte)(_wantNext.GetValueOrDefault(k) | (byte)mods);
    }

    private static bool Wants(ImGuiKey key, bool ctrl, bool shift, bool alt)
    {
        if (key == ImGuiKey.None || !_want.TryGetValue((int)key, out var m)) return false;
        var bit = 1 << ((ctrl ? 1 : 0) | (shift ? 2 : 0) | (alt ? 4 : 0));
        return (m & bit) != 0;
    }

    // A key the game would also turn into text (chat): its text event is dropped when the key is.
    private static bool ProducesText(ImGuiKey k) =>
        k is >= ImGuiKey.A and <= ImGuiKey.Z or >= ImGuiKey._0 and <= ImGuiKey._9
          or >= ImGuiKey.Apostrophe and <= ImGuiKey.GraveAccent or ImGuiKey.Space
          or >= ImGuiKey.Keypad0 and <= ImGuiKey.KeypadEqual;

    /// <summary>Hands the queued input to ImGui; hidden (the caller's one read of the flag for the frame), the queue is just emptied.</summary>
    public static void FlushEvents(bool visible)
    {
        if (!visible)
        {
            while (_ioQueue.TryDequeue(out _)) { }
            return;
        }
        var io = ImGuiNET.ImGui.GetIO();
        while (_ioQueue.TryDequeue(out var action))
            action(io);
    }

    public static void Reset()
    {
        while (_ioQueue.TryDequeue(out _)) { }
        lock (_gameHeld) _gameHeld.Clear();
        _claimNext = _claimed = false;
        _wantNext = new Dictionary<int, byte>();
        _want = new Dictionary<int, byte>();
        WantCaptureMouse    = false;
        WantCaptureKeyboard = false;
        WantTextInput       = false;
    }

    public static void SnapshotWantCapture(bool visible)
    {
        _claimed   = _claimNext && visible;
        _claimNext = false;
        _want = _wantNext;
        _wantNext = new Dictionary<int, byte>();
        if (!visible)
        {
            WantCaptureMouse = false;
            WantCaptureKeyboard = false;
            WantTextInput = false;
            return;
        }
        var io = ImGuiNET.ImGui.GetIO();
        WantCaptureMouse = io.WantCaptureMouse;
        // io.WantCaptureKeyboard is also true while any item is active (dragging a window,
        // holding a slider...), which would freeze the game's keyboard. Only an active text
        // field really owns the keyboard; ImGui still receives every key either way.
        WantTextInput       = io.WantTextInput;
        WantCaptureKeyboard = io.WantTextInput || _claimed;
    }

    /// <summary>The overlay just opened: ImGui gets the pointer where it is, not where it last was while shown.</summary>
    public static void OnShown()
    {
        if (_lastMouse is { } p) _ioQueue.Enqueue(io => io.AddMousePosEvent(p.X, p.Y));
    }

    // ── which backend ────────────────────────────────────────────────────────────────────────

    private static bool _gaveUp;
    private static string? _lastWhy;
    private static int _faults;
    private static long _nextTry;   // a failed look for the library is repeated once a second, not every frame

    public static bool Installed => SdlInput.Installed || GlfwInput.Installed;

    /// <summary>
    /// Puts the backend on the engine's window library, by the kind of its windowing. Called at
    /// start and again each frame until the window exists and the library is bound.
    /// </summary>
    public static void Install()
    {
        if (Installed || _gaveUp || SdlInput.Asked || GlfwInput.Asked) return;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now < _nextTry) return;
        _nextTry = now + System.Diagnostics.Stopwatch.Frequency;
        var kind = WindowingKind();
        string? why;
        if (kind is null) why = "the engine has no window yet";
        else if (kind.Contains("Sdl3", StringComparison.Ordinal)) { if (SdlInput.Install(out why)) return; }
        else if (kind.Contains("Glfw", StringComparison.Ordinal)) { if (GlfwInput.Install(out why)) return; }
        else { _gaveUp = true; ImGuiRenderer.Disable($"input: unknown windowing {kind}"); return; }
        if (why is null) return;
        if (SdlInput.GaveUp || GlfwInput.GaveUp) { _gaveUp = true; ImGuiRenderer.Disable($"input: {why}"); return; }
        if (why != _lastWhy) { _lastWhy = why; MarseyLogger.Info($"[ImGuiInputHook] waiting: {why}"); }
    }

    /// <summary>The type name of Clyde's windowing implementation ("…Sdl3WindowingImpl", "…GlfwWindowingImpl"); null before it exists.</summary>
    internal static string? WindowingKind() => Windowing()?.GetType().Name;

    internal static object? Windowing()
    {
        try
        {
            var clyde = Engine.ClydeManager.Instance;
            if (clyde is null) return null;
            for (var t = clyde.GetType(); t is not null; t = t.BaseType)
                if (HarmonyLib.AccessTools.Field(t, "_windowing")?.GetValue(clyde) is { } w) return w;
        }
        catch { }
        return null;
    }

    /// <summary>Clyde's main window registration (a Sdl3WindowReg / GlfwWindowReg), or null.</summary>
    internal static object? MainWindowReg()
    {
        try
        {
            var clyde = Engine.ClydeManager.Instance;
            if (clyde is null) return null;
            for (var t = clyde.GetType(); t is not null; t = t.BaseType)
                if (HarmonyLib.AccessTools.Field(t, "_mainWindow")?.GetValue(clyde) is { } w) return w;
        }
        catch { }
        return null;
    }

    /// <summary>A native pointer field of the registration (SDL_Window*, GLFWwindow*), whatever shape reflection gives it.</summary>
    internal static nint PointerField(object reg, string name)
    {
        try
        {
            var v = HarmonyLib.AccessTools.Field(reg.GetType(), name)?.GetValue(reg);
            return v switch
            {
                nint p => p,
                System.Reflection.Pointer ptr => (nint)System.Reflection.Pointer.Unbox(ptr),
                _ => 0,
            };
        }
        catch { return 0; }
    }

    /// <summary>
    /// Runs work on the engine's window thread (Clyde._windowing.RunOnWindowThread; inline when the
    /// engine has none). The window libraries' own calls belong there: setting SDL's filter takes a
    /// lock the engine's event watch holds while it waits for the game thread, and GLFW's calls
    /// are main-thread only.
    /// </summary>
    internal static bool RunOnEngineWindowThread(Action work)
    {
        try
        {
            var windowing = Windowing();
            if (windowing is null) return false;
            var m = HarmonyLib.AccessTools.Method(windowing.GetType(), "RunOnWindowThread", [typeof(Action)]);
            if (m is null) return false;
            m.Invoke(windowing, [work]);
            return true;
        }
        catch (Exception ex)
        {
            MarseyLogger.Warn($"[ImGuiInputHook] RunOnWindowThread: {ex.Message}");
            return false;
        }
    }

    /// <summary>A backend's handler threw: logged; after three, the backend goes and the overlay with it.</summary>
    internal static void Fault(Exception ex)
    {
        try
        {
            if (++_faults <= 3) MarseyLogger.Warn($"[ImGuiInputHook] {ex}");
            if (_faults == 3) Lifecycle.Post(() =>
            {
                SdlInput.Uninstall();
                GlfwInput.Uninstall();
                _gaveUp = true;
                ImGuiRenderer.Disable("input: 3 failures");
            });
        }
        catch { }
    }

    // ── the policy, backend-neutral ──────────────────────────────────────────────────────────

    // Keys and buttons whose press went to the game: the matching release must go there too,
    // even if the overlay opened or ImGui grabbed focus in between. Keys by their physical id
    // (the backend's scancode: a keycode can change under a held key when the layout does);
    // buttons tagged apart.
    private static readonly HashSet<uint> _gameHeld = new();
    private const uint ButtonTag = 0x8000_0000u;

    internal const int ModCtrl = 1, ModShift = 2, ModAlt = 4, ModSuper = 8, ModAltGr = 16;

    private static string? _toggleName;
    private static int     _toggle = (int)ImGuiKey.Delete;   // an int: no ImGui.NET value type in a field (type load)

    // Resolved by name from the config; an unknown or unbindable name falls back to Delete.
    private static ImGuiKey ToggleKey
    {
        get
        {
            var name = DespadaConfig.Current.ToggleKey;
            if (name != _toggleName)
            {
                _toggleName = name;
                var k = InputKeys.ByName(name);
                _toggle = (int)(k ?? InputKeys.ByName(DespadaSettings.DefaultToggleKey)!.Value);
                if (k is null) MarseyLogger.Warn($"[ImGuiInputHook] toggle key '{name}' is not bindable, using {DespadaSettings.DefaultToggleKey}.");
            }
            return (ImGuiKey)_toggle;
        }
    }

    // Settings "press a key" capture: the next non-repeat keyboard key goes here instead of anywhere
    // else. Receives the key's name, or null when cancelled (Escape or CancelKeyCapture). The
    // callback runs on the game thread.
    private static Action<string?>? _keyCapture;

    public static void BeginKeyCapture(Action<string?> onKey) => _keyCapture = onKey;

    public static void CancelKeyCapture()
    {
        var onKey = Interlocked.Exchange(ref _keyCapture, null);
        onKey?.Invoke(null);
    }

    /// <summary>A key went down or up. True: the game gets it too.</summary>
    internal static bool Key(ImGuiKey imKey, uint physical, int mods, bool down, bool repeat)
    {
        bool ctrl = (mods & ModCtrl) != 0, shift = (mods & ModShift) != 0, alt = (mods & ModAlt) != 0,
             super = (mods & ModSuper) != 0, altGr = (mods & ModAltGr) != 0;
        // A modifier key speaks for its own modifier: the event's mods field is the state before
        // the key changed (GLFW reports Shift still set on Shift's release and clear on its press),
        // and ImGui would hold the modifier until the next key event says otherwise. Both sides
        // are kept apart, so letting go of one Shift while the other is held keeps Shift.
        var side = ModBit(imKey);
        if (side != 0)
        {
            _modKeys = down ? _modKeys | side : _modKeys & ~side;
            shift = (_modKeys & 0x03) != 0; ctrl = (_modKeys & 0x0C) != 0; alt = (_modKeys & 0x30) != 0; super = (_modKeys & 0xC0) != 0;
        }

        if (!down)
        {
            // The chord's text event (if the library sends one at all) comes before the key-up.
            _suppressText = false;
            // A key the game saw go down: its release goes there whatever it is now (even the toggle).
            bool held;
            lock (_gameHeld) held = _gameHeld.Remove(physical);
            if (imKey == ToggleKey && !held) return false;
            if (ImGuiRenderer.OverlayVisible) EnqueueKey(imKey, ctrl, shift, alt, super, false);
            return held || !ImGuiRenderer.OverlayVisible;
        }

        if (_keyCapture is not null && ImGuiRenderer.OverlayVisible)
        {
            if (repeat) return false;
            var name = imKey == ImGuiKey.Escape ? null : InputKeys.NameOf(imKey);
            if (imKey != ImGuiKey.Escape && name is null) return false;   // swallowed, still waiting
            if (Interlocked.Exchange(ref _keyCapture, null) is { } capture) Lifecycle.Post(() => capture(name));
            return false;
        }

        if (imKey == ToggleKey)
        {
            if (!repeat)
            {
                ImGuiRenderer.OverlayVisible = !ImGuiRenderer.OverlayVisible;
                MarseyLogger.Info($"[ImGuiInputHook] Overlay → {ImGuiRenderer.OverlayVisible}");
            }
            return false;   // the toggle key belongs to the overlay: neither the game nor ImGui sees it
        }

        if (!ImGuiRenderer.OverlayVisible)
        {
            lock (_gameHeld) _gameHeld.Add(physical);   // its release goes to the game even if the overlay opens meanwhile
            return true;
        }

        // A paste is coming (Ctrl+V / Ctrl+Shift+V): fetch the system clipboard now (on the game thread).
        if (imKey == ImGuiKey.LeftCtrl && WantTextInput) Lifecycle.Post(Clipboard.Refresh);

        // Alt+key chords (session/tab keys, Alt+B/F/D): the character must not be typed too, in the
        // console or any other field that owns the keyboard. AltGr types characters on many layouts.
        if (WantCaptureKeyboard && IsAltChordKey(imKey) && alt && !ctrl && !altGr)
            _suppressText = true;

        // Esc in the console only closes its suggestion list: ImGui would deactivate the field and
        // the keyboard would go back to the game.
        if (imKey == ImGuiKey.Escape && ConsolePanel.InputActive)
        {
            ConsolePanel.RequestEscape();
            return false;
        }

        // ImGui always sees the event so its state stays consistent; the game gets it unless
        // ImGui wants this kind of input: an active text field, a claim, or a key the shown
        // panel declared (Want). A modifier pressed with the pointer over the overlay belongs
        // to it too: it is about to modify a click there, not the game.
        EnqueueKey(imKey, ctrl, shift, alt, super, true);
        if (WantCaptureKeyboard) return false;
        if (WantCaptureMouse && IsModifier(imKey)) return false;
        if (Wants(imKey, ctrl, shift, alt))
        {
            if (!ctrl && ProducesText(imKey)) _suppressText = true;
            return false;
        }

        lock (_gameHeld) _gameHeld.Add(physical);
        return true;
    }

    private static bool IsModifier(ImGuiKey k) => ModBit(k) != 0;

    // Which modifier keys are down, one bit per key (input thread only).
    private static int _modKeys;

    private static int ModBit(ImGuiKey k) => k switch
    {
        ImGuiKey.LeftShift => 0x01, ImGuiKey.RightShift => 0x02, ImGuiKey.LeftCtrl  => 0x04, ImGuiKey.RightCtrl  => 0x08,
        ImGuiKey.LeftAlt   => 0x10, ImGuiKey.RightAlt   => 0x20, ImGuiKey.LeftSuper => 0x40, ImGuiKey.RightSuper => 0x80,
        _ => 0,
    };

    private static void EnqueueKey(ImGuiKey imKey, bool ctrl, bool shift, bool alt, bool super, bool down)
    {
        // Kept as int: a closure field of ImGui.NET's enum type would make this compiler-generated
        // class need ImGui.NET at type-load time (Assembly.GetTypes() before our resolver exists).
        var k = (int)imKey;
        _ioQueue.Enqueue(io =>
        {
            io.AddKeyEvent(ImGuiKey.ModCtrl, ctrl);
            io.AddKeyEvent(ImGuiKey.ModShift, shift);
            io.AddKeyEvent(ImGuiKey.ModAlt, alt);
            io.AddKeyEvent(ImGuiKey.ModSuper, super);
            if (k != (int)ImGuiKey.None) io.AddKeyEvent((ImGuiKey)k, down);
        });
    }

    /// <summary>A mouse button; <paramref name="imGuiIndex"/> is ImGui's (0 left, 1 right, 2 middle, 3, 4) or -1. True: the game gets it too.</summary>
    internal static bool Button(int imGuiIndex, byte which, bool down)
    {
        var tag = ButtonTag | which;
        if (down)
        {
            if (!ImGuiRenderer.OverlayVisible)
            {
                lock (_gameHeld) _gameHeld.Add(tag);
                return true;
            }
            if (imGuiIndex >= 0) _ioQueue.Enqueue(io => io.AddMouseButtonEvent(imGuiIndex, true));
            if (WantCaptureMouse) return false;
            lock (_gameHeld) _gameHeld.Add(tag);
            return true;
        }
        if (ImGuiRenderer.OverlayVisible && imGuiIndex >= 0) _ioQueue.Enqueue(io => io.AddMouseButtonEvent(imGuiIndex, false));
        bool held;
        lock (_gameHeld) held = _gameHeld.Remove(tag);
        return held || !ImGuiRenderer.OverlayVisible;
    }

    private static Vector2? _lastMouse;

    /// <summary>
    /// The pointer, in framebuffer pixels. Motion always goes on to the game as well: the engine's
    /// own pointer position (aim, facing) must keep following the mouse while it crosses a panel;
    /// what the game may not have is the clicks and the wheel.
    /// </summary>
    internal static void Move(float px, float py)
    {
        var pos = new Vector2(px, py) / UiScale.K;
        _lastMouse = pos;
        if (ImGuiRenderer.OverlayVisible) _ioQueue.Enqueue(io => io.AddMousePosEvent(pos.X, pos.Y));
    }

    /// <summary>The wheel, in ImGui's convention (x positive scrolls left, y positive up). True: the game gets it too.</summary>
    internal static bool Wheel(float x, float y)
    {
        if (!ImGuiRenderer.OverlayVisible) return true;
        _ioQueue.Enqueue(io => io.AddMouseWheelEvent(x, y));
        return !WantCaptureMouse;
    }

    internal static void Focus(bool gained)
    {
        if (!gained) _modKeys = 0;   // the libraries release every key on focus loss; so do we
        if (ImGuiRenderer.OverlayVisible) _ioQueue.Enqueue(io => io.AddFocusEvent(gained));
    }

    // Set by an Alt chord key-down; drops the one text event that key produces. One-shot rather than
    // "Alt held", so AltGr text on other keys still types.
    private static bool _suppressText;

    private static bool IsAltChordKey(ImGuiKey k) =>
        k is ImGuiKey.T or ImGuiKey.C or ImGuiKey.R or ImGuiKey.B or ImGuiKey.F or ImGuiKey.D
          or ImGuiKey.LeftBracket or ImGuiKey.RightBracket
          or >= ImGuiKey._1 and <= ImGuiKey._9;

    /// <summary>Text typed. True: the game gets it too.</summary>
    internal static bool Text(string? text)
    {
        if (!ImGuiRenderer.OverlayVisible) return true;
        if (_suppressText)
        {
            _suppressText = false;
            return false;
        }
        if (string.IsNullOrEmpty(text)) return !WantCaptureKeyboard;
        var cap = text;
        _ioQueue.Enqueue(io =>
        {
            foreach (var rune in cap.EnumerateRunes())
                io.AddInputCharacter((uint)rune.Value);
        });
        return !WantCaptureKeyboard;
    }
}

/// <summary>
/// Modifier combinations a key is wanted with (<see cref="ImGuiInputHook.Want"/>); one bit per
/// combination, so they add up: <c>Mod.None | Mod.Shift</c> is the key plain and with Shift.
/// </summary>
[Flags]
public enum Mod : byte
{
    None         = 1 << 0,
    Ctrl         = 1 << 1,
    Shift        = 1 << 2,
    CtrlShift    = 1 << 3,
    Alt          = 1 << 4,
    CtrlAlt      = 1 << 5,
    ShiftAlt     = 1 << 6,
    CtrlShiftAlt = 1 << 7,
    Any          = 0xFF
}
