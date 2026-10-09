// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Concurrent;
using System.Numerics;
using System.Reflection;
using Despada.Config;
using Despada.Core;
using Despada.ImGui.UserInterface;
using HarmonyLib;
using ImGuiNET;

namespace Despada.ImGui.Hook;

public static class ImGuiInputHook
{
    private static readonly ConcurrentQueue<Action<ImGuiIOPtr>> _ioQueue = new();

    public static void EnqueueIO(Action<ImGuiIOPtr> action) => _ioQueue.Enqueue(action);

    public static volatile bool WantCaptureMouse = false;
    public static volatile bool WantCaptureKeyboard = false;
    /// <summary>An ImGui text field has focus (WantCaptureKeyboard also counts a panel's claim).</summary>
    public static volatile bool WantTextInput = false;

    // A panel that works the keyboard without a text field (the network list with its hotkeys)
    // claims it for every frame it is focused; the claim applies from the next frame on.
    private static bool _claimNext;
    private static bool _claimed;

    /// <summary>
    /// Keeps keys from the game, as an active text field does: right away (the next input batch,
    /// which runs before the next render) and as long as it is claimed every frame.
    /// </summary>
    public static void ClaimKeyboard()
    {
        _claimNext = true;
        if (ImGuiRenderer.OverlayVisible) WantCaptureKeyboard = true;
    }

    public static void FlushEvents()
    {
        if (!ImGuiRenderer.OverlayVisible)
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
        _gameHeld.Clear();
        _claimNext = _claimed = false;
        WantCaptureMouse    = false;
        WantCaptureKeyboard = false;
        WantTextInput       = false;
    }

    public static void SnapshotWantCapture()
    {
        _claimed   = _claimNext && ImGuiRenderer.OverlayVisible;
        _claimNext = false;
        if (!ImGuiRenderer.OverlayVisible)
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

    /// <summary>One prefix on every input entry point; a missing one only loses that kind of input.</summary>
    public static void Install()
    {
        // Without its input hooks the overlay can be neither used nor closed: give everything back
        // to the game for the rest of the session.
        Hooks.Tripped += (owner, reason) =>
        {
            if (owner == "input") ImGuiRenderer.Disable($"input hooks shut off: {reason}");
        };

        foreach (var target in new[] { Engine.InputKeyDown, Engine.InputKeyUp, Engine.UiMouseMove,
                                       Engine.UiMouseWheel, Engine.GameTextEntered })
            Hooks.Prefix("input", target, nameof(Prefix), typeof(ImGuiInputHook));
    }

    private static bool Prefix(MethodBase __originalMethod, object[] __args)
    {
        try
        {
            return __originalMethod.Name switch
            {
                "KeyDown" => OnKeyDown(__args[0]),
                "KeyUp" => OnKeyUp(__args[0]),
                "MouseMove" => OnMouseMove(__args[0]),
                "MouseWheel" => OnMouseWheel(__args[0]),
                "TextEntered" => OnTextEntered(__args[0]),
                _ => true
            };
        }
        catch (Exception ex)
        {
            // Repeated failures remove the input hooks (game input works as without Despada).
            Hooks.Fail("input", __originalMethod.Name, ex);
            return true;
        }
    }

    // Keys/buttons whose key-down went to the game: the matching key-up must go there too,
    // even if the overlay opened or ImGui grabbed focus in between.
    private static readonly HashSet<byte> _gameHeld = new();

    private static string? _toggleKeyName;
    private static byte    _toggleKey;

    // Resolved by name from the config: the engine's Keyboard.Key values shift whenever a key is inserted.
    private static byte ToggleKey
    {
        get
        {
            var name = DespadaConfig.Current.ToggleKey;
            if (name != _toggleKeyName)
            {
                _toggleKey     = ResolveKey(name);
                _toggleKeyName = name;
            }
            return _toggleKey;
        }
    }

    // Settings "press a key" capture: the next non-repeat keyboard key goes here instead of anywhere
    // else. Receives the Keyboard.Key name, or null when cancelled (Escape or CancelKeyCapture).
    private static Action<string?>? _keyCapture;

    public static void BeginKeyCapture(Action<string?> onKey) => _keyCapture = onKey;

    public static void CancelKeyCapture()
    {
        var onKey = _keyCapture;
        _keyCapture = null;
        onKey?.Invoke(null);
    }

    // The toggle key always wins over text input, so only keys that never type or edit text can be
    // bound to it (letters, Enter, arrows, modifiers... would become unusable in text fields).
    private static bool IsBindable(object key)
    {
        var k = KeyMap.ToImGuiKey(key);
        return k is >= ImGuiKey.F1 and <= ImGuiKey.F24
            or ImGuiKey.Insert or ImGuiKey.Delete or ImGuiKey.PageUp or ImGuiKey.PageDown
            or ImGuiKey.Pause or ImGuiKey.ScrollLock or ImGuiKey.PrintScreen;
    }

    private static byte ResolveKey(string name)
    {
        var keyType = Engine.KeyboardKey.Type;
        if (keyType is not null && Enum.TryParse(keyType, name, out var value))
            return Convert.ToByte(value);

        const string fallbackName = DespadaSettings.DefaultToggleKey;
        if (keyType is not null && name != fallbackName && Enum.TryParse(keyType, fallbackName, out var fallback))
        {
            MarseyLogger.Warn($"[ImGuiInputHook] Keyboard.Key.{name} not found, using {fallbackName}.");
            return Convert.ToByte(fallback);
        }

        // Keyboard.Key.Delete at the time of writing (see KeyMap); only hit if the enum can't be found.
        MarseyLogger.Warn($"[ImGuiInputHook] Keyboard.Key.{name} not found, falling back to 83.");
        return 83;
    }

    private static bool OnKeyDown(object args)
    {
        var key = Read(args, "Key");
        if (key is null) return true;

        var keyByte = Convert.ToByte(key);

        if (_keyCapture is not null && ImGuiRenderer.OverlayVisible && !KeyMap.IsMouseKey(key))
        {
            if (Read(args, "IsRepeat") is true) return false;
            var name = Enum.GetName(key.GetType(), key);
            if (name == "Escape")
            {
                CancelKeyCapture();
                return false;
            }
            if (name is null || !IsBindable(key))
                return false; // swallowed, still waiting

            var onKey = _keyCapture;
            _keyCapture = null;
            onKey(name);
            return false;
        }

        if (keyByte == ToggleKey)
        {
            if (ImGuiRenderer.Failed) return true;

            if (Read(args, "IsRepeat") is not true)
            {
                ImGuiRenderer.OverlayVisible = !ImGuiRenderer.OverlayVisible;
                MarseyLogger.Info($"[ImGuiInputHook] Overlay → {ImGuiRenderer.OverlayVisible}");
            }
            // The toggle key belongs to the overlay: neither the game nor ImGui sees it.
            return false;
        }

        if (!ImGuiRenderer.OverlayVisible)
        {
            _gameHeld.Add(keyByte);
            return true;
        }

        var imKey = KeyMap.IsMouseKey(key) ? ImGuiKey.None : KeyMap.ToImGuiKey(key);

        // A paste is coming (Ctrl+V / Ctrl+Shift+V): fetch the system clipboard now.
        if (imKey == ImGuiKey.LeftCtrl && WantTextInput) Clipboard.Refresh();

        // Alt+key chords (session/tab keys, Alt+B/F/D): the character must not be typed too, in the
        // console or any other field that owns the keyboard.
        // AltGr (291: KeyEventArgs.AltGr; null on older engines) types characters on many layouts.
        if (WantCaptureKeyboard && IsAltChordKey(imKey) && Read(args, "AltGr") is not true
            && Convert.ToBoolean(Read(args, "Alt") ?? false) && !Convert.ToBoolean(Read(args, "Control") ?? false))
            _suppressText = true;

        // Esc in the console only closes its suggestion list: ImGui would deactivate the field and
        // the keyboard would go back to the game.
        if (imKey == ImGuiKey.Escape && ConsolePanel.InputActive)
        {
            ConsolePanel.RequestEscape();
            return false;
        }

        // ImGui always sees the event so its state stays consistent; the game gets it
        // unless ImGui wants this kind of input (hovered window / active text field).
        var isMouse = EnqueueKey(args, key, down: true);
        if (isMouse ? WantCaptureMouse : WantCaptureKeyboard)
            return false;

        _gameHeld.Add(keyByte);
        return true;
    }

    private static bool OnKeyUp(object args)
    {
        var key = Read(args, "Key");
        if (key is null) return true;

        var keyByte = Convert.ToByte(key);

        // The chord's text event (if SDL sends one at all) comes before the key-up.
        _suppressText = false;

        if (keyByte == ToggleKey && !ImGuiRenderer.Failed)
            return false;

        if (ImGuiRenderer.OverlayVisible)
            EnqueueKey(args, key, down: false);

        if (_gameHeld.Remove(keyByte))
            return true;

        return !ImGuiRenderer.OverlayVisible;
    }

    // Queues the key/button for ImGui. Returns true if it is a mouse button.
    private static bool EnqueueKey(object args, object key, bool down)
    {
        if (KeyMap.IsMouseKey(key))
        {
            var btnIdx = KeyMap.MouseKeyToImGuiButton(key);
            if (btnIdx >= 0)
                _ioQueue.Enqueue(io => io.AddMouseButtonEvent(btnIdx, down));
            return true;
        }

        // Kept as int: a closure field of ImGui.NET's enum type would make this compiler-generated
        // class need ImGui.NET at type-load time (Assembly.GetTypes() before our resolver exists).
        var imKey = (int)KeyMap.ToImGuiKey(key);
        var ctrl  = Convert.ToBoolean(Read(args, "Control") ?? false);
        var shift = Convert.ToBoolean(Read(args, "Shift") ?? false);
        var alt   = Convert.ToBoolean(Read(args, "Alt") ?? false);
        var super = Convert.ToBoolean(Read(args, "System") ?? false);

        _ioQueue.Enqueue(io =>
        {
            io.AddKeyEvent(ImGuiKey.ModCtrl, ctrl);
            io.AddKeyEvent(ImGuiKey.ModShift, shift);
            io.AddKeyEvent(ImGuiKey.ModAlt, alt);
            io.AddKeyEvent(ImGuiKey.ModSuper, super);
            if (imKey != (int)ImGuiKey.None)
                io.AddKeyEvent((ImGuiKey)imKey, down);
        });
        return false;
    }

    private static bool OnMouseMove(object args)
    {
        if (!ImGuiRenderer.OverlayVisible) return true;

        var screenCoords = Read(args, "Position");
        Vector2 pos = default;

        if (screenCoords is not null)
        {
            var innerPos = Read(screenCoords, "Position");
            if (innerPos is Vector2 v) pos = v;
        }

        var virtualPos = pos / UiScale.K;
        _ioQueue.Enqueue(io => io.AddMousePosEvent(virtualPos.X, virtualPos.Y));
        return !WantCaptureMouse;
    }

    private static bool OnMouseWheel(object args)
    {
        if (!ImGuiRenderer.OverlayVisible) return true;

        var delta = Read(args, "Delta");
        Vector2 d = default;
        if (delta is Vector2 v) d = v;

        _ioQueue.Enqueue(io => io.AddMouseWheelEvent(d.X, d.Y));

        return !WantCaptureMouse;
    }

    // Set by an Alt chord key-down; drops the one text event that key produces. One-shot rather than
    // "Alt held", so AltGr text on other keys still types.
    private static bool _suppressText;

    private static bool IsAltChordKey(ImGuiKey k) =>
        k is ImGuiKey.T or ImGuiKey.C or ImGuiKey.R or ImGuiKey.B or ImGuiKey.F or ImGuiKey.D
          or ImGuiKey.LeftBracket or ImGuiKey.RightBracket
          or >= ImGuiKey._1 and <= ImGuiKey._9;

    private static bool OnTextEntered(object args)
    {
        if (!ImGuiRenderer.OverlayVisible) return true;
        if (_suppressText)
        {
            _suppressText = false;
            return false;
        }

        var textObj = Read(args, "Text");

        string? captured = null;

        if (textObj is string s)
            captured = s;
        else if (textObj is char c)
            captured = c.ToString();

        if (string.IsNullOrEmpty(captured)) return !WantCaptureKeyboard;

        var cap = captured;
        _ioQueue.Enqueue(io =>
        {
            foreach (var rune in cap.EnumerateRunes())
                io.AddInputCharacter((uint)rune.Value);
        });

        return !WantCaptureKeyboard;
    }

    private static readonly ConcurrentDictionary<(Type, string), FieldInfo?> _fieldCache = new();
    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo?> _propCache = new();

    private static object? Read(object obj, string name)
        => ReadProperty(obj, name) ?? ReadField(obj, name);

    private static object? ReadField(object obj, string name)
    {
        var type = obj.GetType();
        var fi = _fieldCache.GetOrAdd((type, name), k =>
            k.Item1.GetField(k.Item2,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        return fi?.GetValue(obj);
    }

    private static object? ReadProperty(object obj, string name)
    {
        var type = obj.GetType();
        var pi = _propCache.GetOrAdd((type, name), k =>
            k.Item1.GetProperty(k.Item2,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        return pi?.GetValue(obj);
    }
}

public static class ImGuiSdl3TextInputStopPatch
{
    private static bool _loggedBlock;

    public static void Install()
        => Hooks.Prefix("input", Engine.Sdl3TextInputStop, nameof(Prefix), typeof(ImGuiSdl3TextInputStopPatch));

    private static bool Prefix()
    {
        // Only for a real text field: a panel's keyboard claim needs no SDL text input.
        if (ImGuiRenderer.OverlayVisible && ImGuiInputHook.WantTextInput)
        {
            if (!_loggedBlock)
            {
                _loggedBlock = true;
                MarseyLogger.Info("[ImGuiSdl3TextInputStopPatch] Blocking TextInputStop — ImGui has focus.");
            }
            return false;
        }
        _loggedBlock = false;
        return true;
    }
}