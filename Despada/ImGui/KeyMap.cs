// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using ImGuiNET;

namespace Despada.ImGui;
// The engine's Keyboard.Key values shift whenever a key is inserted, so the table is built from
// the enum's names at first use (the numbers below are only the fallback when the type is missing).
internal static class KeyMap
{
    private static ImGuiKey[]? _table;
    private static bool[]?     _mouse;
    private static int[]?      _button;

    private static ImGuiKey[] Table  => _table ??= Build();
    private static bool[]     Mouse  { get { _ = Table; return _mouse!; } }
    private static int[]      Button { get { _ = Table; return _button!; } }

    public static ImGuiKey ToImGuiKey(object robustKey)
    {
        var idx = Convert.ToInt32(robustKey);
        var t = Table;
        return idx >= 0 && idx < t.Length ? t[idx] : ImGuiKey.None;
    }

    public static bool IsMouseKey(object robustKey)
    {
        var idx = Convert.ToInt32(robustKey);
        var m = Mouse;
        return idx >= 0 && idx < m.Length && m[idx];
    }

    public static int MouseKeyToImGuiButton(object robustKey)
    {
        var idx = Convert.ToInt32(robustKey);
        var b = Button;
        return idx >= 0 && idx < b.Length ? b[idx] : -1;
    }

    // Robust's names that differ from ImGui's.
    private static readonly Dictionary<string, ImGuiKey> Named = new()
    {
        ["Control"] = ImGuiKey.LeftCtrl, ["Shift"] = ImGuiKey.LeftShift, ["Alt"] = ImGuiKey.LeftAlt,
        ["LSystem"] = ImGuiKey.LeftSuper, ["RSystem"] = ImGuiKey.RightSuper,
        ["LBracket"] = ImGuiKey.LeftBracket, ["RBracket"] = ImGuiKey.RightBracket, ["SemiColon"] = ImGuiKey.Semicolon,
        ["BackSlash"] = ImGuiKey.Backslash, ["Tilde"] = ImGuiKey.GraveAccent, ["Return"] = ImGuiKey.Enter,
        ["NumpadEnter"] = ImGuiKey.KeypadEnter, ["BackSpace"] = ImGuiKey.Backspace,
        ["NumpadAdd"] = ImGuiKey.KeypadAdd, ["NumpadSubtract"] = ImGuiKey.KeypadSubtract,
        ["NumpadDivide"] = ImGuiKey.KeypadDivide, ["NumpadMultiply"] = ImGuiKey.KeypadMultiply, ["NumpadDecimal"] = ImGuiKey.KeypadDecimal,
        ["Left"] = ImGuiKey.LeftArrow, ["Right"] = ImGuiKey.RightArrow, ["Up"] = ImGuiKey.UpArrow, ["Down"] = ImGuiKey.DownArrow,
        ["CapsLock"] = ImGuiKey.CapsLock, ["ScrollLock"] = ImGuiKey.ScrollLock, ["Pause"] = ImGuiKey.Pause,
    };

    private static ImGuiKey[] Build()
    {
        var type = Despada.Core.Engine.KeyboardKey.Type;
        if (type is null || !type.IsEnum) return Fallback();
        var names  = Enum.GetNames(type);
        var values = Enum.GetValues(type);
        var max = 0;
        for (var i = 0; i < names.Length; i++) max = Math.Max(max, Convert.ToInt32(values.GetValue(i)));
        var t = new ImGuiKey[max + 1];
        var m = new bool[max + 1];
        var b = new int[max + 1];
        Array.Fill(t, ImGuiKey.None);
        Array.Fill(b, -1);
        for (var i = 0; i < names.Length; i++)
        {
            var name = names[i];
            var v    = Convert.ToInt32(values.GetValue(i));
            if (name.StartsWith("Mouse", StringComparison.Ordinal))
            {
                m[v] = true;
                b[v] = name switch { "MouseLeft" => 0, "MouseRight" => 1, "MouseMiddle" => 2, "MouseButton4" => 3, "MouseButton5" => 4, _ => -1 };
                continue;
            }
            t[v] = Resolve(name);
        }
        _mouse = m; _button = b;
        return t;
    }

    private static ImGuiKey Resolve(string name)
    {
        if (Named.TryGetValue(name, out var k)) return k;
        if (name.Length == 1 && char.IsAsciiLetterUpper(name[0])) return ImGuiKey.A + (name[0] - 'A');
        if (name.StartsWith("NumpadNum", StringComparison.Ordinal) && name.Length == 10 && char.IsAsciiDigit(name[9])) return ImGuiKey.Keypad0 + (name[9] - '0');
        if (name.StartsWith("Num", StringComparison.Ordinal) && name.Length == 4 && char.IsAsciiDigit(name[3])) return ImGuiKey._0 + (name[3] - '0');
        if (name.Length is 2 or 3 && name[0] == 'F' && int.TryParse(name.AsSpan(1), out var f) && f is >= 1 and <= 24) return ImGuiKey.F1 + (f - 1);
        return Enum.TryParse<ImGuiKey>(name, out var same) ? same : ImGuiKey.None;   // Escape, Menu, Comma, Period, Apostrophe, Slash, Equal, Space, Tab, PageUp…, Minus, Insert, Delete, Home, End
    }

    // Keyboard.Key as of RobustToolbox 292, used only when the enum can't be read.
    private static ImGuiKey[] Fallback()
    {
        var names = new[] { "Unknown", "MouseLeft", "MouseRight", "MouseMiddle", "MouseButton4", "MouseButton5", "MouseButton6", "MouseButton7", "MouseButton8", "MouseButton9",
            "A","B","C","D","E","F","G","H","I","J","K","L","M","N","O","P","Q","R","S","T","U","V","W","X","Y","Z",
            "Num0","Num1","Num2","Num3","Num4","Num5","Num6","Num7","Num8","Num9",
            "NumpadNum0","NumpadNum1","NumpadNum2","NumpadNum3","NumpadNum4","NumpadNum5","NumpadNum6","NumpadNum7","NumpadNum8","NumpadNum9",
            "Escape","Control","Shift","Alt","LSystem","RSystem","Menu","LBracket","RBracket","SemiColon","Comma","Period","Apostrophe","Slash","BackSlash","Tilde","Equal","Space","Return","NumpadEnter","BackSpace","Tab","PageUp","PageDown","End","Home","Insert","Delete","Minus","NumpadAdd","NumpadSubtract","NumpadDivide","NumpadMultiply","NumpadDecimal",
            "Left","Right","Up","Down",
            "F1","F2","F3","F4","F5","F6","F7","F8","F9","F10","F11","F12","F13","F14","F15","F16","F17","F18","F19","F20","F21","F22","F23","F24",
            "Pause","World1","CapsLock","ScrollLock","Help","Stop","Again","Props","Undo","Cut","Copy","Open","Paste","Find" };
        var t = new ImGuiKey[names.Length];
        var m = new bool[names.Length];
        var b = new int[names.Length];
        Array.Fill(b, -1);
        for (var v = 0; v < names.Length; v++)
        {
            if (names[v].StartsWith("Mouse", StringComparison.Ordinal))
            {
                m[v] = true;
                b[v] = v <= 5 ? v - 1 : -1;
                continue;
            }
            t[v] = Resolve(names[v]);
        }
        _mouse = m; _button = b;
        return t;
    }
}
