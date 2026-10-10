// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using ImGuiNET;

namespace Despada.ImGui.UserInterface;

public enum ButtonKind
{
    Primary,    // the accent gradient, ink text: the one action of a view (send)
    Secondary,  // raised surface with a hairline and a top highlight
    Ghost,      // text only; fills on hover
    Danger,     // red text and a red hairline
}

public static partial class Widgets
{
    public const float ButtonH = 36f;
    public const float ButtonRound = 8f;
    private const float ButtonPadX = 14f;
    private const float ButtonIconGap = 8f;
    private const float ButtonIconSize = 18f;

    /// <summary>
    /// A button in the v2 language: height 36, radius 8, optional icon before the label. Hover
    /// brightens, the fill shrinks to 96 % while pressed (the label and icon stay put).
    /// </summary>
    public static bool Button(string id, string label, ButtonKind kind = ButtonKind.Secondary, Icon icon = Icon.None, float width = 0f)
    {
        var textW = label.Length == 0 ? 0f : ImGuiNET.ImGui.CalcTextSize(label).X;
        var iconW = icon == Icon.None ? 0f : ButtonIconSize + (textW > 0f ? ButtonIconGap : 0f);
        var w = width > 0f ? width : ButtonPadX * 2f + iconW + textW;
        var pos = ImGuiNET.ImGui.GetCursorScreenPos();
        var clicked = ImGuiNET.ImGui.InvisibleButton(id, new Vector2(w, ButtonH));
        DrawButton(ImGuiNET.ImGui.GetItemID(), pos, new Vector2(w, ButtonH), label, kind, icon,
            ImGuiNET.ImGui.IsItemHovered(), ImGuiNET.ImGui.IsItemActive(), null);
        return clicked;
    }

    /// <summary>A square icon button, 36 by default; <paramref name="color"/> tints the icon (a red trash, a blue arrow).</summary>
    public static bool IconButton(string id, Icon icon, ButtonKind kind = ButtonKind.Secondary, Vector4? color = null, float size = ButtonH)
    {
        var pos = ImGuiNET.ImGui.GetCursorScreenPos();
        var clicked = ImGuiNET.ImGui.InvisibleButton(id, new Vector2(size, size));
        DrawButton(ImGuiNET.ImGui.GetItemID(), pos, new Vector2(size, size), "", kind, icon,
            ImGuiNET.ImGui.IsItemHovered(), ImGuiNET.ImGui.IsItemActive(), color);
        return clicked;
    }

    private static void DrawButton(uint id, Vector2 pos, Vector2 size, string label, ButtonKind kind, Icon icon,
        bool hovered, bool active, Vector4? iconColor)
    {
        var dl  = ImGuiNET.ImGui.GetWindowDrawList();
        var hot = Animate(id, hovered, 24f);
        var (min, max) = Pressed(id, active, pos, pos + size);

        Vector4 text;
        switch (kind)
        {
            case ButtonKind.Primary:
                Gfx.GradientRect(dl, min, max, Theme.P.Accent, Theme.P.Accent2, ButtonRound);
                if (hot > 0.01f) dl.AddRectFilled(min, max, Theme.ToU32(new Vector4(1f, 1f, 1f, 0.08f * hot)), ButtonRound);
                text = Theme.Ink;
                break;
            case ButtonKind.Secondary:
                Gfx.Raised(dl, min, max, ButtonRound, Theme.Mix(Theme.RaisedFill, Theme.RaisedHot, hot), Theme.Mix(Theme.Hairline, Theme.HairlineHot, hot));
                text = Theme.P.Fg;
                break;
            case ButtonKind.Ghost:
                if (hot > 0.01f) dl.AddRectFilled(min, max, Theme.ToU32(Theme.WithAlpha(Theme.RaisedFill, Theme.RaisedFill.W * hot)), ButtonRound);
                text = Theme.Mix(Theme.P.Sub, Theme.P.Fg, hot);
                break;
            default:
                if (hot > 0.01f) dl.AddRectFilled(min, max, Theme.ToU32(Theme.WithAlpha(Theme.P.Red, 0.12f * hot)), ButtonRound);
                dl.AddRect(UiScale.Snap(min), UiScale.Snap(max), Theme.ToU32(Theme.WithAlpha(Theme.P.Red, 0.35f + 0.25f * hot)), ButtonRound, ImDrawFlags.None, 1f / UiScale.K);
                text = Theme.P.Red;
                break;
        }

        // Content is laid out on the unpressed rect, so it doesn't jitter with the fill.
        var textW = label.Length == 0 ? 0f : ImGuiNET.ImGui.CalcTextSize(label).X;
        var iconW = icon == Icon.None ? 0f : ButtonIconSize + (textW > 0f ? ButtonIconGap : 0f);
        var x = pos.X + (size.X - iconW - textW) * 0.5f;
        var cy = pos.Y + size.Y * 0.5f;
        if (icon != Icon.None)
        {
            DrawIcon(dl, new Vector2(x + ButtonIconSize * 0.5f, cy), ButtonIconSize, icon, iconColor ?? text, 2f);
            x += iconW;
        }
        if (textW > 0f)
            dl.AddText(new Vector2(x, cy - ImGuiNET.ImGui.GetTextLineHeight() * 0.5f), Theme.ToU32(text), label);
    }
}
