// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using ImGuiNET;

namespace Despada.ImGui.UserInterface;

/// <summary>
/// One row of a tree drawn at the cursor (console output, network packets): hairline guides,
/// a ▾/▸ toggle for nodes with children, then coloured runs. Rows have a fixed height, so lists of
/// them work with ImGuiListClipper.
/// </summary>
internal static class TreeRows
{
    public static Vector4 Color(Tone tone) => tone switch
    {
        Tone.Sub     => Theme.P.Sub,
        Tone.Muted   => Theme.P.Muted,
        Tone.Accent  => Theme.P.Accent,
        Tone.Accent2 => Theme.P.Accent2,
        Tone.Link    => Theme.P.Link,
        Tone.Green   => Theme.P.Green,
        Tone.Yellow  => Theme.P.Yellow,
        Tone.Red     => Theme.P.Red,
        _            => Theme.P.Fg,
    };

    /// <param name="id">Stable ImGui id for the row's toggle.</param>
    /// <param name="x0">Left edge of depth 0.</param>
    /// <param name="guides">Bit l set: an ancestor's guide line passes through this row at level l.</param>
    /// <returns>True when the node's toggle (the whole row) was clicked.</returns>
    public static bool Draw(int id, float x0, int depth, uint guides, bool last, bool isNode, bool collapsed,
        Span[] spans)
    {
        var dl    = ImGuiNET.ImGui.GetWindowDrawList();
        var pos   = ImGuiNET.ImGui.GetCursorScreenPos();
        var lineH = ImGuiNET.ImGui.GetTextLineHeight();
        var rowH  = ImGuiNET.ImGui.GetTextLineHeightWithSpacing();
        var cw    = ImGuiNET.ImGui.CalcTextSize("M").X;
        var step  = cw * 2f;

        if (depth > 0)
        {
            // One-pixel hairlines on the framebuffer grid; segments end where ImGui puts the next row
            // (it truncates the cursor), so joins neither gap nor overlap (a doubled pixel of the
            // translucent colour would look dotted). The elbow's vertical ends where its horizontal starts.
            var col = Theme.ToU32(Theme.P.Sep);
            var end = MathF.Floor(pos.Y + rowH);
            float Gx(int level) => x0 + level * step + cw * 0.5f;

            for (var l = 0; l < Math.Min(depth - 1, 32); l++)
                if ((guides & (1u << l)) != 0)
                    Gfx.VLine(dl, Gx(l), pos.Y, end, col);

            var gx  = Gx(depth - 1);
            var mid = pos.Y + lineH * 0.5f;
            Gfx.VLine(dl, gx, pos.Y, last ? mid : end, col);
            Gfx.HLine(dl, gx, gx + step - cw * 0.7f, mid, col);
        }

        var x     = x0 + depth * step;
        var textX = isNode ? x + step : x;
        var textW = 0f;
        foreach (var span in spans)
            textW += ImGuiNET.ImGui.CalcTextSize(span.Text).X;

        var hovered = false;
        var clicked = false;
        if (isNode)
        {
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, pos.Y));
            ImGuiNET.ImGui.PushID(id);
            clicked = ImGuiNET.ImGui.InvisibleButton("##node", new Vector2(textX - x + textW, lineH));
            hovered = ImGuiNET.ImGui.IsItemHovered();
            ImGuiNET.ImGui.PopID();

            dl.AddText(new Vector2(x, pos.Y), Theme.ToU32(hovered ? Theme.P.Fg : Theme.P.Muted), collapsed ? "▸" : "▾");
        }

        foreach (var span in spans)
        {
            var c = Color(span.Tone);
            if (hovered && span.Tone == Tone.Fg) c = Theme.P.Accent;
            dl.AddText(new Vector2(textX, pos.Y), Theme.ToU32(c), span.Text);
            textX += ImGuiNET.ImGui.CalcTextSize(span.Text).X;
        }

        // Reserve the row as one item so the clipper keeps a fixed row height.
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        ImGuiNET.ImGui.Dummy(new Vector2(MathF.Max(1f, textX - pos.X), lineH));
        return clicked;
    }
}
