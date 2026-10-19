// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using ImGuiNET;

namespace Despada.ImGui.Hook;

/// <summary>The window libraries' key codes to ImGui keys, and the names the config binds the toggle key by.</summary>
internal static class InputKeys
{
    private const uint Sc = 1u << 30;   // SDLK_SCANCODE_MASK: SDL keys without a character are their scancode with this bit

    /// <summary>
    /// SDL: by keycode (what the layout says the key is), and when that has no ImGui key, by the
    /// physical scancode: on a German or French layout the bracket keys carry ü or ^, and the
    /// chords on them must still work.
    /// </summary>
    public static ImGuiKey FromSdl(uint key, uint scancode)
    {
        var k = FromSdlKeycode(key);
        if (k != ImGuiKey.None) return k;
        return scancode switch
        {
            45 => ImGuiKey.Minus, 46 => ImGuiKey.Equal, 47 => ImGuiKey.LeftBracket, 48 => ImGuiKey.RightBracket,
            49 or 50 => ImGuiKey.Backslash, 51 => ImGuiKey.Semicolon, 52 => ImGuiKey.Apostrophe, 53 => ImGuiKey.GraveAccent,
            54 => ImGuiKey.Comma, 55 => ImGuiKey.Period, 56 => ImGuiKey.Slash,
            _ => ImGuiKey.None,
        };
    }

    public static ImGuiKey FromSdlKeycode(uint key)
    {
        if (key >= 'a' && key <= 'z') return ImGuiKey.A + (int)(key - 'a');
        if (key >= '0' && key <= '9') return ImGuiKey._0 + (int)(key - '0');
        return key switch
        {
            0x0d => ImGuiKey.Enter, 0x1b => ImGuiKey.Escape, 0x08 => ImGuiKey.Backspace, 0x09 => ImGuiKey.Tab, 0x20 => ImGuiKey.Space,
            0x27 => ImGuiKey.Apostrophe, 0x2c => ImGuiKey.Comma, 0x2d => ImGuiKey.Minus, 0x2e => ImGuiKey.Period, 0x2f => ImGuiKey.Slash,
            0x3b => ImGuiKey.Semicolon, 0x3d => ImGuiKey.Equal, 0x5b => ImGuiKey.LeftBracket, 0x5c => ImGuiKey.Backslash,
            0x5d => ImGuiKey.RightBracket, 0x60 => ImGuiKey.GraveAccent, 0x7f => ImGuiKey.Delete,
            Sc | 0x39 => ImGuiKey.CapsLock,
            >= (Sc | 0x3a) and <= (Sc | 0x45) => ImGuiKey.F1 + (int)(key - (Sc | 0x3a)),
            Sc | 0x46 => ImGuiKey.PrintScreen, Sc | 0x47 => ImGuiKey.ScrollLock, Sc | 0x48 => ImGuiKey.Pause,
            Sc | 0x49 => ImGuiKey.Insert, Sc | 0x4a => ImGuiKey.Home, Sc | 0x4b => ImGuiKey.PageUp,
            Sc | 0x4d => ImGuiKey.End, Sc | 0x4e => ImGuiKey.PageDown,
            Sc | 0x4f => ImGuiKey.RightArrow, Sc | 0x50 => ImGuiKey.LeftArrow, Sc | 0x51 => ImGuiKey.DownArrow, Sc | 0x52 => ImGuiKey.UpArrow,
            Sc | 0x53 => ImGuiKey.NumLock, Sc | 0x54 => ImGuiKey.KeypadDivide, Sc | 0x55 => ImGuiKey.KeypadMultiply,
            Sc | 0x56 => ImGuiKey.KeypadSubtract, Sc | 0x57 => ImGuiKey.KeypadAdd, Sc | 0x58 => ImGuiKey.KeypadEnter,
            >= (Sc | 0x59) and <= (Sc | 0x61) => ImGuiKey.Keypad1 + (int)(key - (Sc | 0x59)),
            Sc | 0x62 => ImGuiKey.Keypad0, Sc | 0x63 => ImGuiKey.KeypadDecimal, Sc | 0x65 => ImGuiKey.Menu, Sc | 0x67 => ImGuiKey.KeypadEqual,
            >= (Sc | 0x68) and <= (Sc | 0x73) => ImGuiKey.F13 + (int)(key - (Sc | 0x68)),
            Sc | 0x76 => ImGuiKey.Menu,
            Sc | 0xe0 => ImGuiKey.LeftCtrl, Sc | 0xe1 => ImGuiKey.LeftShift, Sc | 0xe2 => ImGuiKey.LeftAlt, Sc | 0xe3 => ImGuiKey.LeftSuper,
            Sc | 0xe4 => ImGuiKey.RightCtrl, Sc | 0xe5 => ImGuiKey.RightShift, Sc | 0xe6 => ImGuiKey.RightAlt, Sc | 0xe7 => ImGuiKey.RightSuper,
            _ => ImGuiKey.None,
        };
    }

    /// <summary>GLFW: keys are the US layout's, by position (GLFW_KEY_*).</summary>
    public static ImGuiKey FromGlfw(int key)
    {
        if (key >= 'A' && key <= 'Z') return ImGuiKey.A + (key - 'A');
        if (key >= '0' && key <= '9') return ImGuiKey._0 + (key - '0');
        return key switch
        {
            32 => ImGuiKey.Space, 39 => ImGuiKey.Apostrophe, 44 => ImGuiKey.Comma, 45 => ImGuiKey.Minus, 46 => ImGuiKey.Period, 47 => ImGuiKey.Slash,
            59 => ImGuiKey.Semicolon, 61 => ImGuiKey.Equal, 91 => ImGuiKey.LeftBracket, 92 => ImGuiKey.Backslash, 93 => ImGuiKey.RightBracket, 96 => ImGuiKey.GraveAccent,
            256 => ImGuiKey.Escape, 257 => ImGuiKey.Enter, 258 => ImGuiKey.Tab, 259 => ImGuiKey.Backspace, 260 => ImGuiKey.Insert, 261 => ImGuiKey.Delete,
            262 => ImGuiKey.RightArrow, 263 => ImGuiKey.LeftArrow, 264 => ImGuiKey.DownArrow, 265 => ImGuiKey.UpArrow,
            266 => ImGuiKey.PageUp, 267 => ImGuiKey.PageDown, 268 => ImGuiKey.Home, 269 => ImGuiKey.End,
            280 => ImGuiKey.CapsLock, 281 => ImGuiKey.ScrollLock, 282 => ImGuiKey.NumLock, 283 => ImGuiKey.PrintScreen, 284 => ImGuiKey.Pause,
            >= 290 and <= 313 => ImGuiKey.F1 + (key - 290),
            >= 320 and <= 329 => ImGuiKey.Keypad0 + (key - 320),
            330 => ImGuiKey.KeypadDecimal, 331 => ImGuiKey.KeypadDivide, 332 => ImGuiKey.KeypadMultiply, 333 => ImGuiKey.KeypadSubtract,
            334 => ImGuiKey.KeypadAdd, 335 => ImGuiKey.KeypadEnter, 336 => ImGuiKey.KeypadEqual,
            340 => ImGuiKey.LeftShift, 341 => ImGuiKey.LeftCtrl, 342 => ImGuiKey.LeftAlt, 343 => ImGuiKey.LeftSuper,
            344 => ImGuiKey.RightShift, 345 => ImGuiKey.RightCtrl, 346 => ImGuiKey.RightAlt, 347 => ImGuiKey.RightSuper, 348 => ImGuiKey.Menu,
            _ => ImGuiKey.None,
        };
    }

    // The toggle key is bound by name in the config. Only keys that never type or edit text:
    // the toggle wins over text fields, so a letter or Enter would become unusable in them.
    private static readonly (string Name, ImGuiKey Key)[] Bindable =
    [
        ("Delete", ImGuiKey.Delete), ("Insert", ImGuiKey.Insert), ("PageUp", ImGuiKey.PageUp), ("PageDown", ImGuiKey.PageDown),
        ("Pause", ImGuiKey.Pause), ("ScrollLock", ImGuiKey.ScrollLock), ("PrintScreen", ImGuiKey.PrintScreen),
        .. Enumerable.Range(1, 24).Select(i => ($"F{i}", ImGuiKey.F1 + (i - 1))),
    ];

    public static ImGuiKey? ByName(string name)
    {
        foreach (var (n, k) in Bindable) if (n.Equals(name, StringComparison.OrdinalIgnoreCase)) return k;
        return null;
    }

    public static string? NameOf(ImGuiKey key)
    {
        foreach (var (n, k) in Bindable) if (k == key) return n;
        return null;
    }
}
