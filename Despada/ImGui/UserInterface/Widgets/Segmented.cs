// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using ImGuiNET;

namespace Despada.ImGui.UserInterface;

public static partial class Widgets
{
    public const float SegmentedH = 36f;
    private const float SegmentedPad = 3f;
    private const float SegmentedCellPadX = 14f;
    private const float SegmentedDot = 8f;

    /// <summary>The width <see cref="Segmented"/> takes for these items (to place it from the right).</summary>
    public static float SegmentedWidth(ReadOnlySpan<string> items, bool dots = false)
    {
        var total = SegmentedPad * 2f;
        for (var i = 0; i < items.Length; i++)
            total += ImGuiNET.ImGui.CalcTextSize(items[i]).X + SegmentedCellPadX * 2f + (dots ? SegmentedDot + 8f : 0f) + (i > 0 ? 2f : 0f);
        return total;
    }

    /// <summary>
    /// A segmented switch: a Deep track with a hairline, the chosen segment a raised thumb that
    /// slides (spring) between positions. <paramref name="dots"/>, when given, puts a coloured dot
    /// before each label (rule actions). Returns true when the choice changed.
    /// </summary>
    public static bool Segmented(string id, ReadOnlySpan<string> items, ref int index, ReadOnlySpan<Vector4> dots = default)
    {
        var dl  = ImGuiNET.ImGui.GetWindowDrawList();
        var pos = ImGuiNET.ImGui.GetCursorScreenPos();
        var cellH = SegmentedH - SegmentedPad * 2f;

        // Cells: label width + padding (+ dot), laid out left to right.
        Span<float> widths = stackalloc float[items.Length];
        var total = SegmentedPad * 2f;
        for (var i = 0; i < items.Length; i++)
        {
            widths[i] = ImGuiNET.ImGui.CalcTextSize(items[i]).X + SegmentedCellPadX * 2f + (dots.Length > i ? SegmentedDot + 8f : 0f);
            total += widths[i] + (i > 0 ? 2f : 0f);
        }

        ImGuiNET.ImGui.PushID(id);
        var changed = false;
        var x = pos.X + SegmentedPad;
        Span<float> lefts = stackalloc float[items.Length];
        var hoveredCell = -1;
        for (var i = 0; i < items.Length; i++)
        {
            lefts[i] = x;
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, pos.Y + SegmentedPad));
            ImGuiNET.ImGui.PushID(i);
            if (ImGuiNET.ImGui.InvisibleButton("##seg", new Vector2(widths[i], cellH)) && index != i) { index = i; changed = true; }
            if (ImGuiNET.ImGui.IsItemHovered()) hoveredCell = i;
            ImGuiNET.ImGui.PopID();
            x += widths[i] + 2f;
        }
        var baseId = ImGuiNET.ImGui.GetID("##thumb");
        ImGuiNET.ImGui.PopID();

        // Track.
        var min = UiScale.Snap(pos);
        var max = UiScale.Snap(pos + new Vector2(total, SegmentedH));
        dl.AddRectFilled(min, max, Theme.ToU32(Theme.P.Deep), ButtonRound + 1f);
        dl.AddRect(min, max, Theme.ToU32(Theme.Hairline), ButtonRound + 1f, ImDrawFlags.None, 1f / UiScale.K);

        // The thumb slides to the chosen cell (sprung relative to the track, so a moving window
        // carries it along); its width follows too. Clamped to the track: the spring overshoots.
        var cur = Math.Clamp(index, 0, Math.Max(0, items.Length - 1));
        if (items.Length > 0)
        {
            var tx = pos.X + SpringTo(baseId, lefts[cur] - pos.X, 700f, 0.85f);
            var tw = SpringTo(baseId ^ 0x77u, widths[cur]);
            var left  = MathF.Max(pos.X + SegmentedPad, tx);
            var right = MathF.Min(pos.X + total - SegmentedPad, tx + tw);
            var tmin = new Vector2(left, pos.Y + SegmentedPad);
            var tmax = new Vector2(right, pos.Y + SegmentedPad + cellH);
            dl.AddRectFilled(tmin + new Vector2(0f, 1f), tmax + new Vector2(0f, 1f), Theme.ToU32(Theme.WithAlpha(Theme.P.Shadow, 0.35f)), ButtonRound - 2f);
            Gfx.Raised(dl, tmin, tmax, ButtonRound - 2f, Theme.Thumb, Theme.Hairline);
        }

        // Labels and dots.
        var lineH = ImGuiNET.ImGui.GetTextLineHeight();
        for (var i = 0; i < items.Length; i++)
        {
            var on  = i == cur;
            var hot = Animate(baseId ^ (uint)(0x100 + i), hoveredCell == i, 24f);
            var col = on ? Theme.P.Fg : Theme.Mix(Theme.P.Muted, Theme.P.Sub, hot);
            var cx  = lefts[i] + SegmentedCellPadX;
            var cy  = pos.Y + SegmentedH * 0.5f;
            if (dots.Length > i)
            {
                dl.AddCircleFilled(new Vector2(cx + SegmentedDot * 0.5f, cy), SegmentedDot * 0.5f, Theme.ToU32(dots[i]));
                cx += SegmentedDot + 8f;
            }
            dl.AddText(new Vector2(cx, cy - lineH * 0.5f), Theme.ToU32(col), items[i]);
        }

        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        ImGuiNET.ImGui.Dummy(new Vector2(total, SegmentedH));
        return changed;
    }
}
