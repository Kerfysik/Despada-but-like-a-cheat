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

    private readonly record struct TableCols(float X0, float ValueX, float Right);

    // A field the palette jumped to: the table scrolls to its row once.
    private static FieldNode? _scrollToField;

    /// <summary>Opens the message's objects to depth 2 (not game states, strings or long lists).</summary>
    private static void ExpandDefault(FieldNode node, int depth)
    {
        if (!node.CanExpand) return;
        foreach (var c in NetDecode.Children(node))
        {
            if (!c.CanExpand || c.Value is string || c.Depth > 2) continue;
            if (c.Value?.GetType().Name is "GameState") continue;
            if (c.Value is { } cv && NetDecode.IsSequenceValue(cv) && NetDecode.CountOf(cv) > 32) continue;
            c.Expanded = true;
            ExpandDefault(c, depth + 1);
        }
    }

    private static void DrawFieldTable(long seq, FieldNode root)
    {
        var cw    = ImGuiNET.ImGui.CalcTextSize("M").X;
        var pos   = ImGuiNET.ImGui.GetCursorScreenPos();
        var avail = ImGuiNET.ImGui.GetContentRegionAvail().X;
        var x0    = pos.X - 2f * cw;   // depth 0; depth 1 starts at the cursor, as the tree does
        DrawTableLevel(seq, root, NetDecode.Children(root), 1, 0, new TableCols(x0, x0, pos.X + avail));
    }

    // "KeyValuePair<string, FrozenDictionary<…>>" → "KeyValuePair<…>": folded from the inside until it fits.
    private static readonly Dictionary<(string, int), string> _elided = new();

    internal static string ElideType(string label, int chars)
    {
        if (label.Length <= chars) return label;
        if (_elided.TryGetValue((label, chars), out var hit)) return hit;
        var s = label;
        while (s.Length > chars)
        {
            // Innermost <…> first: drop its last argument, then all of it, then the level around.
            var close = s.IndexOf('>');
            if (close < 0) break;
            var open = s.LastIndexOf('<', close);
            if (open < 0) break;
            while (s.AsSpan(open + 1, close - open - 1).SequenceEqual("…"))
            {
                var depth = 0;
                var parentOpen = -1;
                for (var k = open - 1; k >= 0; k--)
                {
                    if (s[k] == '>') depth++;
                    else if (s[k] == '<' && depth-- == 0) { parentOpen = k; break; }
                }
                if (parentOpen < 0) { open = -1; break; }
                depth = 0;
                var parentClose = -1;
                for (var k = close + 1; k < s.Length; k++)
                {
                    if (s[k] == '<') depth++;
                    else if (s[k] == '>' && depth-- == 0) { parentClose = k; break; }
                }
                if (parentClose < 0) { open = -1; break; }
                open = parentOpen; close = parentClose;
            }
            if (open < 0) break;
            var args = TopLevelArgs(s.AsSpan(open + 1, close - open - 1));
            var folded = args.Count > 1 && args[^1] != "…"
                ? string.Join(", ", args.Take(args.Count - 1)) + ", …"
                : "…";
            s = s[..(open + 1)] + folded + s[close..];
        }
        if (_elided.Count > 512) _elided.Clear();
        return _elided[(label, chars)] = s;
    }

    private static List<string> TopLevelArgs(ReadOnlySpan<char> inner)
    {
        var args = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '<') depth++;
            else if (inner[i] == '>') depth--;
            else if (inner[i] == ',' && depth == 0)
            {
                args.Add(inner[start..i].Trim().ToString());
                start = i + 1;
            }
        }
        args.Add(inner[start..].Trim().ToString());
        return args;
    }

    // Each group of siblings aligns its values after its own longest name, so opening a node
    // lays out only the rows under it. The width counts everything the group can ever show, not
    // what is shown: a paged list's last item "[N-1]", so paging it out moves nothing.
    private static void DrawTableLevel(long seq, FieldNode parent, List<FieldNode> nodes, int depth, uint guides, TableCols cols)
    {
        var cw    = ImGuiNET.ImGui.CalcTextSize("M").X;
        var x     = cols.X0 + depth * cw * 2f;
        var nameW = 0f;
        foreach (var n in nodes)
            nameW = MathF.Max(nameW, (n.CanExpand ? cw * 2f : 0f) + ImGuiNET.ImGui.CalcTextSize(n.Name).X);
        if (parent.Value is { } pv && NetDecode.IsSequenceValue(pv) && NetDecode.CountOf(pv) is var count and > 0)
            nameW = MathF.Max(nameW, cw * 2f + ImGuiNET.ImGui.CalcTextSize($"[{count - 1}]").X);
        cols = cols with { ValueX = x + nameW + cw * 2f };
        for (var i = 0; i < nodes.Count; i++)
        {
            var n    = nodes[i];
            var last = i == nodes.Count - 1;
            DrawTableRow(seq, n, depth, guides, last, cols);
            if (n.Expanded && n.CanExpand)
                DrawTableLevel(seq, n, NetDecode.Children(n), depth + 1, depth <= 32 && !last ? guides | (1u << (depth - 1)) : guides, cols);
        }
    }

    // A long string wraps in the value column by words; a word longer than a line is cut.
    private static int WrapLines(FieldNode n, int chars)
    {
        if (n.Value is not string || n.ValueText.Length != 1) return 1;
        var text = n.ValueText[0].Text;
        if (text.Length <= chars) return 1;
        var lines = 0;
        for (var at = 0; at < text.Length && lines < MaxWrapLines; lines++) at = NextLine(text, at, chars).End;
        return lines;
    }

    private static (List<string> Lines, bool Cut) WrappedValue(FieldNode n, int chars)
    {
        var text  = n.ValueText[0].Text;
        var lines = new List<string>();
        var at    = 0;
        while (at < text.Length && lines.Count < MaxWrapLines)
        {
            var (start, end) = NextLine(text, at, chars);
            lines.Add(text[start..end]);
            at = end;
        }
        var cut = at < text.Length;
        if (cut) lines[^1] = (lines[^1].Length > chars - 2 ? lines[^1][..(chars - 2)] : lines[^1]) + " …";
        return (lines, cut);
    }

    /// <summary>The next wrapped line of <paramref name="text"/> from <paramref name="at"/>: [Start, End), spaces at the break skipped.</summary>
    internal static (int Start, int End) NextLine(string text, int at, int chars)
    {
        while (at < text.Length && text[at] == ' ') at++;
        if (text.Length - at <= chars) return (at, text.Length);
        var end = at + chars;
        // Back up to the last space on the line, unless the line would be left nearly empty.
        var space = text.LastIndexOf(' ', end, end - at);
        if (space > at + chars / 4) end = space;
        return (at, end);
    }

    // A row shows its name, then its value, then as much of its type as fits (folded, then hidden
    // behind the hover tip). Nothing leaves the pane: a value wider than it is cut with an ellipsis
    // and shown whole in the hover tip.
    private static void DrawTableRow(long seq, FieldNode n, int depth, uint guides, bool last, TableCols cols)
    {
        var dl     = ImGuiNET.ImGui.GetWindowDrawList();
        var cw     = ImGuiNET.ImGui.CalcTextSize("M").X;
        var step   = cw * 2f;
        var lineH  = ImGuiNET.ImGui.GetTextLineHeight();
        var space  = ImGuiNET.ImGui.GetStyle().ItemSpacing.Y;
        var gap    = 14f * D;
        var open   = n.Expanded && n.CanExpand;

        // Strings wrap to the room left in the pane (24 to 120 chars a line).
        var wrapChars = Math.Clamp((int)MathF.Floor((cols.Right - cols.ValueX) / cw) - 1, 24, 120);
        var lines  = open ? 1 : WrapLines(n, wrapChars);
        var rowH   = lineH * lines + space * (lines - 1);
        if (ReferenceEquals(n, _scrollToField)) { ImGuiNET.ImGui.SetScrollHereY(0.3f); _scrollToField = null; }
        if (!ImGuiNET.ImGui.IsRectVisible(new Vector2(1f, rowH + space)))
        {
            ImGuiNET.ImGui.Dummy(new Vector2(1f, rowH));
            return;
        }
        var (wrapped, valueCut) = lines > 1 ? WrappedValue(n, wrapChars) : (null, false);

        // The value's width, then the type's share of what is left.
        var vw = 0f;
        if (open) { }
        else if (wrapped is not null) foreach (var l in wrapped) vw = MathF.Max(vw, ImGuiNET.ImGui.CalcTextSize(l).X);
        else foreach (var span in n.ValueText) vw += ImGuiNET.ImGui.CalcTextSize(span.Text).X;
        // Cut at the pane's edge (room for the ellipsis kept); the tip then has the whole value.
        var ellW   = ImGuiNET.ImGui.CalcTextSize("…").X;
        var cut    = !open && wrapped is null && cols.ValueX + vw > cols.Right;
        if (cut) { vw = MathF.Max(cols.Right - cols.ValueX, ellW); valueCut = true; }
        var valueEnd = cols.ValueX + vw;

        string? typeShown = null;
        var typeX = cols.Right;
        if (n.TypeName is { } tn)
        {
            var room = cols.Right - valueEnd - gap;
            var full = ImGuiNET.ImGui.CalcTextSize(tn).X;
            if (room >= full) typeShown = tn;
            else
            {
                var folded = ElideType(tn, (int)MathF.Floor(room / cw));
                if (ImGuiNET.ImGui.CalcTextSize(folded).X <= room) typeShown = folded;
            }
            if (typeShown is not null) typeX = cols.Right - ImGuiNET.ImGui.CalcTextSize(typeShown).X;
        }
        var rowEnd = cols.Right;

        var pos      = ImGuiNET.ImGui.GetCursorScreenPos();
        var selected = ReferenceEquals(_selField, n);
        var hoveredF = ReferenceEquals(_hoverField, n);
        var mouse    = ImGuiNET.ImGui.GetIO().MousePos;
        var overRow  = !MouseOverSuggestions && ImGuiNET.ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)
                       && mouse.X >= cols.X0 && mouse.X < rowEnd && mouse.Y >= pos.Y - space * 0.5f && mouse.Y < pos.Y + rowH + space * 0.5f;

        // Hover lights the row; selection (and the bytes' field) lights the value, or an open node's name.
        if (overRow && !selected && !hoveredF)
            dl.AddRectFilled(new Vector2(cols.X0 + step, pos.Y - space * 0.5f), new Vector2(rowEnd, pos.Y + rowH + space * 0.5f), Theme.ToU32(Theme.P.Raised), 6f * D);
        var x     = cols.X0 + depth * step;
        var nameX = n.CanExpand ? x + step : x;
        if (selected || hoveredF)
        {
            var fill = Theme.ToU32(selected ? Theme.WithAlpha(Theme.P.Accent2, 0.18f) : Theme.Sel);
            var (hx0, hx1) = open ? (nameX, nameX + ImGuiNET.ImGui.CalcTextSize(n.Name).X) : (cols.ValueX, valueEnd);
            dl.AddRectFilled(new Vector2(hx0 - 6f * D, pos.Y - 2f * D), new Vector2(hx1 + 6f * D, pos.Y + rowH + 2f * D), fill, 6f * D);
        }

        TreeRows.Guides(dl, cols.X0, depth, guides, last, pos.Y, rowH + space, lineH);

        // Caret and name; the caret (or the name, for a node) folds.
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
        dl.AddText(new Vector2(nameX, pos.Y), Theme.ToU32(n.CanExpand ? Theme.P.Sub : Theme.P.Muted), n.Name);

        // The value, whole; an open node shows none.
        if (open) { }
        else if (wrapped is not null)
        {
            var tone = Theme.ToU32(TreeRows.Color(n.ValueText[0].Tone));
            for (var i = 0; i < wrapped.Count; i++)
                dl.AddText(new Vector2(cols.ValueX, pos.Y + i * (lineH + space)), tone, wrapped[i]);
        }
        else
        {
            var vx = cols.ValueX;
            if (cut) dl.PushClipRect(new Vector2(cols.ValueX, pos.Y), new Vector2(MathF.Max(cols.ValueX, cols.Right - ellW), pos.Y + rowH), true);
            foreach (var span in n.ValueText)
            {
                dl.AddText(new Vector2(vx, pos.Y), Theme.ToU32(TreeRows.Color(span.Tone)), span.Text);
                vx += ImGuiNET.ImGui.CalcTextSize(span.Text).X;
            }
            if (cut)
            {
                dl.PopClipRect();
                dl.AddText(new Vector2(MathF.Max(cols.ValueX, cols.Right - ellW), pos.Y), Theme.ToU32(TreeRows.Color(n.ValueText[^1].Tone)), "…");
            }
        }

        if (typeShown is not null)
            dl.AddText(new Vector2(typeX, pos.Y), Theme.ToU32(Theme.WithAlpha(Theme.P.Muted, 0.8f)), typeShown);

        if (overRow) _hoverRowNext = n;
        if (n.PageOf is { } paged)
        {
            // "… N more": click = next page, Ctrl+click = all.
            if (overRow && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left)) paged.ShowMore(ImGuiNET.ImGui.GetIO().KeyCtrl);
            ImGuiNET.ImGui.PopID();
            ImGuiNET.ImGui.SetCursorScreenPos(pos);
            ImGuiNET.ImGui.Dummy(new Vector2(1f, rowH));
            return;
        }
        // Tips: a shortened or cut value; the type past the value's end (it may be folded or hidden).
        var shortened = !open && n.ValueText.Length == 1 && n.ValueText[0].Text.EndsWith("…}", StringComparison.Ordinal);
        if (overRow && (valueCut || shortened) && mouse.X >= cols.ValueX && mouse.X < valueEnd + gap * 0.5f) DrawValueTip(n, shortened, withType: cut);
        if (overRow && n.TypeName is not null && mouse.X >= valueEnd + gap * 0.5f) DrawTypeTip(n);
        if (overRow && !onToggle && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            _selField = selected ? null : n;
        if (overRow && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            RequestMenu(seq, n);
        if (toggled) n.Expanded = !n.Expanded;
        ImGuiNET.ImGui.PopID();

        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        ImGuiNET.ImGui.Dummy(new Vector2(1f, rowH));
    }

    // ── hover tips for clipped cells ─────────────────────────────────────────────────────────

    private static void BeginTip()
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, Theme.P.Pop);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Theme.P.Surface1);
        // A tooltip is a window: WindowRounding applies, not PopupRounding.
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 8f * D);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f * D, 8f * D));
        ImGuiNET.ImGui.BeginTooltip();
    }

    private static void EndTip()
    {
        ImGuiNET.ImGui.EndTooltip();
        ImGuiNET.ImGui.PopStyleVar(2);
        ImGuiNET.ImGui.PopStyleColor(2);
    }

    // The whole value; for a shortened summary, every field as name · value · type.
    private static void DrawValueTip(FieldNode n, bool allFields, bool withType = false)
    {
        List<(string Name, Span[] Value, string? Type)>? rows = null;
        if (allFields && n.Value is not null)
        {
            try { rows = NetDecode.SummaryRows(n.Value); }
            catch { return; }
            if (rows.Count == 0) return;
        }
        BeginTip();
        if (rows is not null)
        {
            const int nameMax = 28, valMax = 40;
            var cw    = ImGuiNET.ImGui.CalcTextSize("M").X;
            var nameW = Math.Clamp(rows.Max(r => r.Name.Length) + 2, 8, nameMax + 2) * cw;
            var valW  = Math.Clamp(rows.Max(r => r.Value.Sum(s => s.Text.Length)) + 3, 6, valMax + 3) * cw;
            foreach (var (name, value, type) in rows)
            {
                var x0 = ImGuiNET.ImGui.GetCursorPosX();
                Text(OneLineCut(name, nameMax), Theme.P.Muted);
                ImGuiNET.ImGui.SetCursorPosX(x0 + nameW);
                var vlen = 0;
                foreach (var span in value) { Text(OneLineCut(span.Text, valMax - vlen), TreeRows.Color(span.Tone)); vlen += span.Text.Length; }
                if (type is not null) { ImGuiNET.ImGui.SetCursorPosX(x0 + nameW + valW); Text(type, Theme.WithAlpha(Theme.P.Muted, 0.8f)); }
                ImGuiNET.ImGui.NewLine();
            }
            var t = n.Value!.GetType();
            ImGuiNET.ImGui.Dummy(new Vector2(0f, 4f * D));
            Text(NetDecode.FriendlyName(t) + (t.Namespace is { } ns ? " · " + ns : ""), Theme.P.Muted);
            ImGuiNET.ImGui.NewLine();
        }
        else
        {
            ImGuiNET.ImGui.PushTextWrapPos(900f * D);
            foreach (var span in n.ValueText) Text(span.Text, TreeRows.Color(span.Tone));
            ImGuiNET.ImGui.PopTextWrapPos();
            ImGuiNET.ImGui.NewLine();
            // A cut value hides its type: it goes here.
            if (withType && n.TypeName is { } tn)
            {
                ImGuiNET.ImGui.Dummy(new Vector2(0f, 4f * D));
                Text(tn, Theme.WithAlpha(Theme.P.Muted, 0.8f));
                ImGuiNET.ImGui.NewLine();
            }
        }
        EndTip();
    }

    private static string OneLineCut(string s, int max)
        => max <= 0 ? "" : s.Length <= max ? s : s[..Math.Max(0, max - 1)] + "…";

    // The full type in tones, then the namespace and the count.
    private static void DrawTypeTip(FieldNode n)
    {
        if (n.TypeName is not { } label) return;
        BeginTip();
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= label.Length; i++)
        {
            var c = i < label.Length ? label[i] : '\0';
            var punct = c is '<' or '>' or ',' or '?' or '[' or ']' or ' ' or '\0';
            if (!punct) continue;
            if (i > start) Text(label[start..i], depth == 0 ? Theme.P.Sub : Theme.P.Accent2);
            if (c != '\0') Text(c.ToString(), Theme.P.Muted);
            if (c == '<') depth++;
            if (c == '>') depth--;
            start = i + 1;
        }
        ImGuiNET.ImGui.NewLine();
        var rt = n.Value?.GetType();
        if (rt is not null && NetDecode.TypeLabel(rt) is { } actual && actual != label)
        {
            Text("is ", Theme.P.Muted); Text(actual, Theme.P.Sub);
            ImGuiNET.ImGui.NewLine();
        }
        var caption = rt?.Namespace ?? "";
        if (n.Value is not null && NetDecode.IsSequenceValue(n.Value))
            caption += (caption.Length > 0 ? " · " : "") + string.Concat(n.ValueText.Select(s => s.Text));
        if (caption.Length > 0) { Text(caption, Theme.P.Muted); ImGuiNET.ImGui.NewLine(); }
        EndTip();
    }
}
