// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using System.Runtime.CompilerServices;
using Despada.ImGui;
using Despada.ImGui.UserInterface;
using ImGuiNET;

namespace Despada.Modules.Net;

// The inspector's fields as a table: tree guides and the name at the left, values aligned in one
// column, the declared type muted at the right. A long string wraps inside the value column
// (a few lines, then it unfolds as before). A click selects the field (its bytes light up in the
// bytes pane), the right button opens the field menu; nodes fold on their caret or name.
internal static partial class NetView
{
    private const int MaxWrapLines = 6;

    private readonly record struct TableCols(float X0, float ValueX, float TypeX, float Right, int ValueChars);

    /// <summary>Opens the message's objects to depth 2 (not game states, strings or long lists).</summary>
    private static void ExpandDefault(FieldNode node, int depth)
    {
        if (!node.CanExpand) return;
        foreach (var c in NetDecode.Children(node))
        {
            if (!c.CanExpand || c.Value is string || c.Depth > 2) continue;
            if (c.Value?.GetType().Name is "GameState") continue;
            if (c.Value is System.Collections.ICollection { Count: > 32 }) continue;
            c.Expanded = true;
            ExpandDefault(c, depth + 1);
        }
    }

    private static void DrawFieldTable(long seq, FieldNode root)
    {
        var cw    = ImGuiNET.ImGui.CalcTextSize("M").X;
        var pos   = ImGuiNET.ImGui.GetCursorScreenPos();
        var avail = ImGuiNET.ImGui.GetContentRegionAvail().X;
        var total = (int)MathF.Floor(avail / cw);

        // Column widths from the rows that are open: names (with their indent) and types.
        int nameChars = 0, typeChars = 0;
        Measure(NetDecode.Children(root), 1, ref nameChars, ref typeChars);
        nameChars = Math.Clamp(nameChars + 2, 12, Math.Max(12, total * 2 / 5));
        typeChars = Math.Clamp(typeChars, 4, Math.Max(4, Math.Min(22, total / 4)));

        var x0    = pos.X - 2f * cw;   // depth 0; depth 1 starts at the cursor, as the tree does
        var right = pos.X + avail;
        var cols  = new TableCols(x0, x0 + nameChars * cw, right - typeChars * cw, right,
            Math.Max(4, (int)MathF.Floor((right - typeChars * cw - (x0 + nameChars * cw)) / cw) - 2));
        DrawTableLevel(seq, NetDecode.Children(root), 1, 0, cols);
    }

    private static void Measure(List<FieldNode> nodes, int depth, ref int nameChars, ref int typeChars)
    {
        foreach (var n in nodes)
        {
            nameChars = Math.Max(nameChars, depth * 2 + (n.CanExpand ? 2 : 0) + n.Name.Length);
            if (n.TypeName is { } t) typeChars = Math.Max(typeChars, t.Length);
            if (n.Expanded && n.CanExpand) Measure(NetDecode.Children(n), depth + 1, ref nameChars, ref typeChars);
        }
    }

    private static void DrawTableLevel(long seq, List<FieldNode> nodes, int depth, uint guides, TableCols cols)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            var n    = nodes[i];
            var last = i == nodes.Count - 1;
            DrawTableRow(seq, n, depth, guides, last, cols);
            if (n.Expanded && n.CanExpand)
                DrawTableLevel(seq, NetDecode.Children(n), depth + 1, depth <= 32 && !last ? guides | (1u << (depth - 1)) : guides, cols);
        }
    }

    // A string value longer than the column wraps: how many lines it takes, and those lines
    // (built only for rows that are drawn).
    private static int WrapLines(FieldNode n, int chars)
    {
        if (n.Value is not string || n.ValueText.Length != 1) return 1;
        var len = n.ValueText[0].Text.Length;
        return len <= chars ? 1 : Math.Min(MaxWrapLines, (len + chars - 1) / chars);
    }

    private static List<string>? WrappedValue(FieldNode n, int chars)
    {
        if (WrapLines(n, chars) == 1) return null;
        var text = n.ValueText[0].Text;
        var lines = new List<string>();
        for (var i = 0; i < text.Length && lines.Count < MaxWrapLines; i += chars)
            lines.Add(text.Substring(i, Math.Min(chars, text.Length - i)));
        if (lines.Count * chars < text.Length) lines[^1] = lines[^1][..Math.Max(0, chars - 2)] + " …";
        return lines;
    }

    private static void DrawTableRow(long seq, FieldNode n, int depth, uint guides, bool last, TableCols cols)
    {
        var dl     = ImGuiNET.ImGui.GetWindowDrawList();
        var cw     = ImGuiNET.ImGui.CalcTextSize("M").X;
        var step   = cw * 2f;
        var lineH  = ImGuiNET.ImGui.GetTextLineHeight();
        var space  = ImGuiNET.ImGui.GetStyle().ItemSpacing.Y;
        var lines  = WrapLines(n, cols.ValueChars);
        var rowH   = lineH * lines + space * (lines - 1);

        if (!ImGuiNET.ImGui.IsRectVisible(new Vector2(1f, rowH + space)))
        {
            ImGuiNET.ImGui.Dummy(new Vector2(1f, rowH));
            return;
        }
        var wrapped = lines > 1 ? WrappedValue(n, cols.ValueChars) : null;

        var pos      = ImGuiNET.ImGui.GetCursorScreenPos();
        var selected = ReferenceEquals(_selField, n);
        var hoveredF = ReferenceEquals(_hoverField, n);
        var mouse    = ImGuiNET.ImGui.GetIO().MousePos;
        var overRow  = !MouseOverSuggestions && ImGuiNET.ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)
                       && mouse.X >= cols.X0 && mouse.X < cols.Right && mouse.Y >= pos.Y - space * 0.5f && mouse.Y < pos.Y + rowH + space * 0.5f;

        // The hovered row lights as a row; the selected field (and the one whose bytes are under
        // the mouse) lights on its value, the content the bytes belong to.
        if (overRow && !selected && !hoveredF)
            dl.AddRectFilled(new Vector2(cols.X0 + step, pos.Y - space * 0.5f), new Vector2(cols.Right, pos.Y + rowH + space * 0.5f), Theme.ToU32(Theme.P.Raised), 6f * D);
        if (selected || hoveredF)
        {
            var vw = 0f;
            if (wrapped is not null) foreach (var l in wrapped) vw = MathF.Max(vw, ImGuiNET.ImGui.CalcTextSize(l).X);
            else foreach (var span in n.ValueText) vw += ImGuiNET.ImGui.CalcTextSize(span.Text).X;
            var vMin = new Vector2(cols.ValueX - 6f * D, pos.Y - 2f * D);
            var vMax = new Vector2(MathF.Min(cols.ValueX + vw + 6f * D, cols.TypeX - cw), pos.Y + rowH + 2f * D);
            dl.AddRectFilled(vMin, vMax, Theme.ToU32(selected ? Theme.WithAlpha(Theme.P.Accent2, 0.18f) : Theme.Sel), 6f * D);
        }

        TreeRows.Guides(dl, cols.X0, depth, guides, last, pos.Y, rowH + space, lineH);

        // Caret and name; the caret (or the name, for a node) folds.
        var x     = cols.X0 + depth * step;
        var nameX = n.CanExpand ? x + step : x;
        ImGuiNET.ImGui.PushID(RuntimeHelpers.GetHashCode(n));
        var toggled  = false;
        var onToggle = false;
        if (n.CanExpand)
        {
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, pos.Y));
            toggled  = ImGuiNET.ImGui.InvisibleButton("##node", new Vector2(MathF.Max(1f, cols.ValueX - x), lineH)) && !MouseOverSuggestions;
            onToggle = ImGuiNET.ImGui.IsItemHovered();
            dl.AddText(new Vector2(x, pos.Y), Theme.ToU32(onToggle ? Theme.P.Fg : Theme.P.Muted), n.Expanded ? "▾" : "▸");
        }
        dl.PushClipRect(new Vector2(nameX, pos.Y), new Vector2(cols.ValueX - cw, pos.Y + lineH), true);
        dl.AddText(new Vector2(nameX, pos.Y), Theme.ToU32(n.CanExpand ? Theme.P.Sub : Theme.P.Muted), n.Name);
        dl.PopClipRect();

        // The value, in its column.
        dl.PushClipRect(new Vector2(cols.ValueX, pos.Y), new Vector2(cols.TypeX - cw, pos.Y + rowH), true);
        if (wrapped is not null)
        {
            var tone = Theme.ToU32(TreeRows.Color(n.ValueText[0].Tone));
            for (var i = 0; i < wrapped.Count; i++)
                dl.AddText(new Vector2(cols.ValueX, pos.Y + i * (lineH + space)), tone, wrapped[i]);
        }
        else
        {
            var vx = cols.ValueX;
            foreach (var span in n.ValueText)
            {
                dl.AddText(new Vector2(vx, pos.Y), Theme.ToU32(TreeRows.Color(span.Tone)), span.Text);
                vx += ImGuiNET.ImGui.CalcTextSize(span.Text).X;
            }
        }
        dl.PopClipRect();

        // The type, right-aligned and clipped to its column.
        if (n.TypeName is { } tn)
        {
            var tw = ImGuiNET.ImGui.CalcTextSize(tn).X;
            dl.PushClipRect(new Vector2(cols.TypeX, pos.Y), new Vector2(cols.Right, pos.Y + lineH), true);
            dl.AddText(new Vector2(MathF.Max(cols.TypeX, cols.Right - tw), pos.Y), Theme.ToU32(Theme.WithAlpha(Theme.P.Muted, 0.8f)), tn);
            dl.PopClipRect();
        }

        if (overRow) _hoverRowNext = n;
        if (overRow && !onToggle && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            _selField = selected ? null : n;
        if (overRow && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            RequestMenu(seq, n);
        if (toggled) n.Expanded = !n.Expanded;
        ImGuiNET.ImGui.PopID();

        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        ImGuiNET.ImGui.Dummy(new Vector2(1f, rowH));
    }
}
