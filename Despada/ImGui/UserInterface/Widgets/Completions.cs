// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using ImGuiNET;

namespace Despada.ImGui.UserInterface;

public static partial class Widgets
{
    public const float CompletionRowH = 32f;
    public const float CompletionPad = 4f;
    private const float CompletionPadX = 12f;

    /// <summary>
    /// The completion list (form A2): a small Pop box under the caret with the names only, the
    /// picked row on the gradient bar, and that row's description floating beside the box (to the
    /// right, or to the left when the screen ends). Drawn on the given list (usually the
    /// foreground, over the field). The typed text is tinted where it occurs in each name.
    /// <paramref name="selected"/> below 0 means "the row under the mouse, else the first".
    /// Returns the box so the caller can hit-test clicks next frame (rows start at
    /// <c>min.Y + CompletionPad</c>); the row under the mouse is <paramref name="hover"/>.
    /// </summary>
    public static void Completions(ImDrawListPtr dl, Vector2 at, ReadOnlySpan<(string Name, string Desc)> items, int selected,
        ReadOnlySpan<char> typed, out Vector2 min, out Vector2 max, out int hover, bool shadow = true)
    {
        var n = items.Length;
        var nameW = 0f;
        for (var i = 0; i < n; i++) nameW = MathF.Max(nameW, ImGuiNET.ImGui.CalcTextSize(items[i].Name).X);
        var w = MathF.Max(200f, nameW + CompletionPadX * 2f + CompletionPad * 2f);
        var display = ImGuiNET.ImGui.GetIO().DisplaySize;
        at.X = MathF.Max(0f, MathF.Min(at.X, display.X - w - 8f));   // the box stays on screen
        min = UiScale.Snap(at);
        max = UiScale.Snap(min + new Vector2(w, CompletionRowH * n + CompletionPad * 2f));
        var r = 10f;

        // The row under the mouse, before drawing: a lagging hover would show two rows lit.
        var mouse = ImGuiNET.ImGui.GetIO().MousePos;
        hover = -1;
        if (mouse.X >= min.X && mouse.X < max.X)
        {
            var row = (int)MathF.Floor((mouse.Y - min.Y - CompletionPad) / CompletionRowH);
            if (row >= 0 && row < n) hover = row;
        }
        if (selected < 0) selected = hover >= 0 ? hover : 0;

        if (shadow) Gfx.Shadow(dl, min, max, r, 10f, 0.3f);
        dl.AddRectFilled(min, max, Theme.ToU32(Theme.P.Pop), r);
        dl.AddRect(min, max, Theme.ToU32(Theme.P.Surface1), r, ImDrawFlags.None, 1f / UiScale.K);

        var lineH = ImGuiNET.ImGui.GetTextLineHeight();
        for (var i = 0; i < n; i++)
        {
            var y = min.Y + CompletionPad + CompletionRowH * i;
            var rowMin = new Vector2(min.X + CompletionPad, y);
            var rowMax = new Vector2(max.X - CompletionPad, y + CompletionRowH);
            if (i == selected)
                Gfx.GradientRect(dl, rowMin, rowMax, Theme.Sel, Theme.SelR, 7f);
            else if (hover == i)
                dl.AddRectFilled(rowMin, rowMax, Theme.ToU32(Theme.WithAlpha(Theme.RaisedHot, 0.4f)), 7f);

            var (name, desc) = items[i];
            var tx = rowMin.X + CompletionPadX;
            var ty = y + (CompletionRowH - lineH) * 0.5f;
            var fg = Theme.ToU32(i == selected ? Theme.P.Fg : Theme.P.Sub);
            // The typed text where it really occurs ("MsgSt" inside "type:MsgStateAck"), tinted.
            var hit = typed.Length > 0 ? name.AsSpan().IndexOf(typed, StringComparison.OrdinalIgnoreCase) : -1;
            if (hit < 0)
                dl.AddText(new Vector2(tx, ty), fg, name);
            else
            {
                var span = name.AsSpan();
                if (hit > 0) { dl.AddText(new Vector2(tx, ty), fg, span[..hit]); tx += ImGuiNET.ImGui.CalcTextSize(span[..hit]).X; }
                dl.AddText(new Vector2(tx, ty), Theme.ToU32(Theme.P.Accent2), span.Slice(hit, typed.Length));
                tx += ImGuiNET.ImGui.CalcTextSize(span.Slice(hit, typed.Length)).X;
                dl.AddText(new Vector2(tx, ty), fg, span[(hit + typed.Length)..]);
            }

            // The picked row's description beside the box, on whichever side has room.
            if (i == selected && desc.Length > 0)
            {
                var dw = ImGuiNET.ImGui.CalcTextSize(desc).X;
                var dx = max.X + 14f + dw <= display.X ? max.X + 14f : min.X - 14f - dw;
                dl.AddText(new Vector2(dx, ty), Theme.ToU32(Theme.P.Muted), desc);
            }
        }
    }
}
