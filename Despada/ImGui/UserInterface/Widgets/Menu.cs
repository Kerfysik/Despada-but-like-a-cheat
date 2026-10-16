// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using ImGuiNET;

namespace Despada.ImGui.UserInterface;

// The menu: a Pop surface with a surface1 outline, radius 12, 6 px padding; rows of 34
// with a 22 px icon slot at the left (an icon, or a coloured dot for a rule action), hairlines
// between groups, a muted caption for a group. One widget for the packet / field / selection
// context menus, tab overflow, combo lists and the "/" commands.
public static partial class Widgets
{
    public const float MenuRowH = 34f;
    public const float MenuRound = 12f;
    private const float MenuPad = 6f;
    private const float MenuRowPadX = 10f;
    private const float MenuIconSlot = 22f;
    private const float MenuIconGap = 12f;
    private const float MenuMinW = 240f;

    private static float _menuRowW;
    private static readonly Stack<float> _menuRowWStack = new();

    /// <summary>
    /// Begins a menu popup (ImGui popup under the hood: open it with <c>ImGui.OpenPopup(id)</c>).
    /// False when it is closed; draw items and call <see cref="EndMenu"/> only when true.
    /// </summary>
    public static bool BeginMenu(string id, float minWidth = MenuMinW)
    {
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, MenuRound);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(MenuPad, MenuPad));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0f, 0f));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, Theme.P.Pop);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Theme.P.Surface1);
        ImGuiNET.ImGui.SetNextWindowSizeConstraints(new Vector2(minWidth, 0f), new Vector2(float.MaxValue, float.MaxValue));
        var open = ImGuiNET.ImGui.BeginPopup(id, ImGuiWindowFlags.NoMove);
        if (!open)
        {
            ImGuiNET.ImGui.PopStyleColor(2);
            ImGuiNET.ImGui.PopStyleVar(4);
            return false;
        }
        _menuRowWStack.Push(_menuRowW);
        // A fresh open starts from the minimum: the popup's remembered size would otherwise only ever grow.
        _menuRowW = ImGuiNET.ImGui.IsWindowAppearing()
            ? minWidth - MenuPad * 2f
            : MathF.Max(minWidth - MenuPad * 2f, ImGuiNET.ImGui.GetContentRegionAvail().X);
        return true;
    }

    public static void EndMenu()
    {
        ImGuiNET.ImGui.EndPopup();
        _menuRowW = _menuRowWStack.Count > 0 ? _menuRowWStack.Pop() : 0f;
        ImGuiNET.ImGui.PopStyleColor(2);
        ImGuiNET.ImGui.PopStyleVar(4);
    }

    /// <summary>
    /// A row: an icon or a coloured dot in the left slot, the label; disabled rows are muted and
    /// inert. Two rows with the same text need distinct ids: "resend##2" shows "resend".
    /// </summary>
    public static bool MenuItem(string label, Icon icon = Icon.None, Vector4? dot = null, bool enabled = true, bool closeOnPick = true)
    {
        var pos   = ImGuiNET.ImGui.GetCursorScreenPos();
        var text  = DisplayLabel(label);
        var w     = MathF.Max(_menuRowW, ImGuiNET.ImGui.CalcTextSize(text).X + MenuRowPadX * 2f + MenuIconSlot + MenuIconGap);
        var dl    = ImGuiNET.ImGui.GetWindowDrawList();
        var lineH = ImGuiNET.ImGui.GetTextLineHeight();

        ImGuiNET.ImGui.PushID(label);
        var clicked = ImGuiNET.ImGui.InvisibleButton("##mi", new Vector2(w, MenuRowH)) && enabled;
        var hovered = enabled && ImGuiNET.ImGui.IsItemHovered();
        var hot     = Animate(ImGuiNET.ImGui.GetItemID(), hovered, 28f);
        ImGuiNET.ImGui.PopID();

        if (hot > 0.01f)
            dl.AddRectFilled(pos, pos + new Vector2(w, MenuRowH), Theme.ToU32(Theme.WithAlpha(Theme.RaisedHot, Theme.RaisedHot.W * 0.55f * hot)), 7f);

        var cy = pos.Y + MenuRowH * 0.5f;
        var sx = pos.X + MenuRowPadX + MenuIconSlot * 0.5f;
        if (dot is { } d)
            dl.AddCircleFilled(new Vector2(sx, cy), 4.5f, Theme.ToU32(enabled ? d : Theme.WithAlpha(d, 0.45f)));
        else if (icon != Icon.None)
            DrawIcon(dl, new Vector2(sx, cy), 18f, icon, enabled ? Theme.P.Sub : Theme.WithAlpha(Theme.P.Muted, 0.6f));
        dl.AddText(new Vector2(pos.X + MenuRowPadX + MenuIconSlot + MenuIconGap, cy - lineH * 0.5f),
            Theme.ToU32(enabled ? Theme.P.Fg : Theme.WithAlpha(Theme.P.Muted, 0.7f)), text);

        if (clicked && closeOnPick) ImGuiNET.ImGui.CloseCurrentPopup();
        return clicked;
    }

    /// <summary>A muted caption over a group (the type a rule is for, a session's name).</summary>
    public static void MenuCaption(string text)
    {
        var pos = ImGuiNET.ImGui.GetCursorScreenPos();
        var dl  = ImGuiNET.ImGui.GetWindowDrawList();
        var h   = 30f;
        dl.AddText(new Vector2(pos.X + MenuRowPadX + 4f, pos.Y + (h - ImGuiNET.ImGui.GetTextLineHeight()) * 0.5f), Theme.ToU32(Theme.P.Muted), text);
        ImGuiNET.ImGui.Dummy(new Vector2(MathF.Max(_menuRowW, ImGuiNET.ImGui.CalcTextSize(text).X + MenuRowPadX * 2f + 4f), h));
    }

    /// <summary>A hairline between groups.</summary>
    public static void MenuSeparator()
    {
        var pos = ImGuiNET.ImGui.GetCursorScreenPos();
        Gfx.HLine(ImGuiNET.ImGui.GetWindowDrawList(), pos.X + 8f, pos.X + _menuRowW - 8f, pos.Y + 4f, Theme.ToU32(Theme.Hairline));
        ImGuiNET.ImGui.Dummy(new Vector2(_menuRowW, 9f));
    }
}
