// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using System.Runtime.CompilerServices;
using Despada.ImGui;
using Despada.ImGui.UserInterface;
using ImGuiNET;

namespace Despada.Modules.Net;

/// <summary>
/// The Network tab: filter bar, the packet list (click a packet to unfold its fields in place) and
/// a status line. The list is virtualised: one fixed-height row per packet or unfolded field.
/// </summary>
internal static unsafe partial class NetView
{
    private const float D = UiScale.DesignHeight / 1080f;

    // A visible row: a packet, or one of its unfolded fields (Node != null).
    private struct Row
    {
        public long       Seq;
        public FieldNode? Node;
        public int        Depth;
        public uint       Guides;
        public bool       Last;
    }

    private static readonly List<long> _matches = new();       // seqs passing the filter, oldest first
    private static readonly List<Row>  _rows = new();
    private static readonly Dictionary<long, FieldNode> _open = new(); // unfolded packets
    private static readonly HashSet<string> _knownTypes = new(StringComparer.Ordinal);
    // Entities seen in the capture, for the filter's suggestions: what last mentioned it, how often.
    private static readonly Dictionary<int, (string Type, int Count, long Seq)> _knownEntities = new();
    private static long   _scanned;           // newest seq already tested against the filter
    private static string _appliedQuery = "\0";
    private static NetQuery _query = new();
    private static bool   _rowsDirty = true;
    private static bool   _stickToBottom = true;
    private static int    _rowsRemovedAbove;  // rows dropped from the top this frame (keeps the view still)
    private static int    _topRow;            // the first row on screen last frame
    private static long   _evictionsSeen;
    private static int    _ghostRows;         // of those, the ones last frame still laid out as blank rows
    private static bool   _menuHeld;          // the list held still for an open menu last frame
    private static int    _typeChars = 14;    // width of the type column, in characters

    /// <summary>Drops unfolded trees (they hold message objects) when the feature is switched off.</summary>
    public static void Forget()
    {
        _open.Clear();
        _rowsDirty = true;
        _knownEntities.Clear();
        ForgetDocs();
    }

    public static void DrawCounters()
    {
        // The traffic graph (fixed width: the row never jumps) and the pause mark.
        TrafficHistory.Smooth();
        Widgets.Traffic(TrafficHistory.In, TrafficHistory.ScaleIn, TrafficHistory.Out, TrafficHistory.ScaleOut, TrafficHistory.Phase,
            NetFeature.Short(TrafficHistory.RateIn), NetFeature.Short(TrafficHistory.RateOut));
        if (NetCapture.Paused)
        {
            ImGuiNET.ImGui.SameLine(0f, 12f * D);
            ImGuiNET.ImGui.AlignTextToFramePadding();
            Text("\uf04c paused", Theme.P.Yellow);   // Nerd Font pause (⏸ isn't in the font)
        }
        // No NewLine: the tab row's RowSeparator comes back to this line with SameLine.
        DrawTabRow();
    }

    public static void Draw()
    {
        var avail = ImGuiNET.ImGui.GetContentRegionAvail();
        var left  = 10f * D;         // the same margin both sides
        var width = avail.X - 20f * D;

        BeginKeys();
        ImGuiNET.ImGui.Dummy(new Vector2(0f, 6f * D));
        Finder.Scope = _tabKind == TabKind.Packet && ActiveDoc() is { } doc ? $"#{doc.Seq} {doc.Inner ?? doc.Type.Name}" : null;

        // Any other tab takes the whole body; "packets" goes back to the list.
        if (_tabKind == TabKind.Packet && ActiveDoc() is null) ShowList();
        if (_tabKind == TabKind.Draft && _activeDraft is null) ShowList();
        if (!ListShown)
        {
            Update();   // the list keeps following the capture while it's hidden
            _sugShown = false;      // suggestions aren't drawn here: their stale rect would eat clicks
            _filterActive = false;  // nor the filter: it can't be the one with the keyboard
            ImGuiNET.ImGui.Indent(left);   // kept across the tab's own lines (SetCursorPosX would be lost at the first NewLine)
            var bodyH = MathF.Max(ImGuiNET.ImGui.GetContentRegionAvail().Y - 12f * D, 40f);
            switch (_tabKind)
            {
                case TabKind.Packet: DrawInspector(width, bodyH); break;
                case TabKind.Frozen: DrawFrozenTab(width, bodyH); break;
                case TabKind.Saved:  DrawSavedTab(width, bodyH); break;
                case TabKind.Draft:  DrawDraftTab(_activeDraft!, width, bodyH); break;
                case TabKind.Pick:   DrawPickTab(width, bodyH); break;
            }
            ImGuiNET.ImGui.Unindent(left);
            return;
        }

        ImGuiNET.ImGui.SetCursorPosX(ImGuiNET.ImGui.GetCursorPosX() + left);
        DrawFilterBar(width);
        Update();

        var cols = Columns();

        // Column header.
        ImGuiNET.ImGui.Dummy(new Vector2(0f, 4f * D));
        var hp = ImGuiNET.ImGui.GetCursorScreenPos() + new Vector2(left, 0f);
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        var muted = Theme.ToU32(Theme.P.Muted);
        dl.AddText(hp + new Vector2(cols.Time, 0f), muted, "time");
        dl.AddText(hp + new Vector2(cols.Type, 0f), muted, "type");
        dl.AddText(hp + new Vector2(cols.SizeRight - ImGuiNET.ImGui.CalcTextSize("size").X, 0f), muted, "size");
        dl.AddText(hp + new Vector2(cols.Summary, 0f), muted, "summary");
        ImGuiNET.ImGui.Dummy(new Vector2(width, ImGuiNET.ImGui.GetTextLineHeight()));
        var sepY = ImGuiNET.ImGui.GetCursorScreenPos().Y - 2f * D;
        Gfx.HLine(dl, hp.X, hp.X + width, sepY, Theme.ToU32(Theme.P.Sep));

        // The list, down to a fixed bottom margin: nothing below it that could move.
        ImGuiNET.ImGui.SetCursorPosX(ImGuiNET.ImGui.GetCursorPosX() + left);
        var listH = MathF.Max(ImGuiNET.ImGui.GetContentRegionAvail().Y - 12f * D, 40f);
        ImGuiNET.ImGui.BeginChild("##netlist", new Vector2(width, listH), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollWithMouse);
        DrawRows(cols);
        ImGuiNET.ImGui.EndChild();

        DrawSuggestions();
    }

    // ── model ────────────────────────────────────────────────────────────────────────────────

    private static void Update()
    {
        var query = CurrentQuery(out var key);
        key += "\n" + NetActive.MuteKey();   // muted types leave the list too
        var changed = key != _appliedQuery;
        if (changed)
        {
            _appliedQuery = key;
            _query = query;
            _matches.Clear();
            _scanned = 0;
            _rowsDirty = true;
            _rowsRemovedAbove = 0;   // counted against the old rows (e.g. while the inspector was open)
            ClearMulti();            // a selection is of what was listed
            _anchor = -1;
        }

        // Packets evicted by the per-type cap leave the list where they were.
        var pruned = false;
        if (NetCapture.EvictionCount != _evictionsSeen)
        {
            var (gone, complete) = NetCapture.EvictedSince(_evictionsSeen);
            _evictionsSeen = NetCapture.EvictionCount;
            if (complete) foreach (var seq in gone) RemoveMatch(seq);
            else
            {
                _matches.RemoveAll(seq => NetCapture.Get(seq) is null);
                _rowsDirty = true;
            }
            pruned = true;
        }

        // Packets that fell out of the ring (or were cleared), and with since: those now too old.
        var first = NetCapture.FirstSeq;
        var maxAge = _query.Tokens.Where(t => t.Key == "since" && !t.Negated).Select(t => NetQuery.ParseAge(t.Value)).Min();
        var drop = 0;
        while (drop < _matches.Count && (_matches[drop] < first ||
               maxAge is { } age && NetCapture.Get(_matches[drop]) is { } old && System.Diagnostics.Stopwatch.GetElapsedTime(old.Stamp) > age))
            drop++;
        if (drop > 0)
        {
            var cut = _matches[drop - 1];
            _matches.RemoveRange(0, drop);
            if (_multi.Count > 0) _multi.RemoveWhere(seq => seq <= cut);   // selected packets that left the list
            if (!_rowsDirty)
            {
                var rows = 0;
                while (rows < _rows.Count && _rows[rows].Seq <= cut) rows++;
                _rows.RemoveRange(0, rows);
                _rowsRemovedAbove += rows;
            }
        }

        foreach (var p in NetCapture.Since(_scanned + 1))
        {
            _scanned = p.Seq;
            Learn(p);
            if (!_query.Matches(p, NetDecode.Summary) || NetActive.IsMutedForView(p)) continue;
            _matches.Add(p.Seq);
            if (!_rowsDirty) _rows.Add(new Row { Seq = p.Seq });
        }

        // Unfolded packets that left the list would otherwise keep their message objects alive.
        if (_open.Count > 0 && (changed || drop > 0 || pruned))
            foreach (var k in _open.Keys.Where(k => k < first || _matches.BinarySearch(k) < 0).ToList())   // _matches is sorted
                _open.Remove(k);

        if (_rowsDirty) Rebuild();
    }

    // Type names for the filter's suggestions: message types and, for MsgEntity, the event type.
    private static void Learn(Packet p)
    {
        if (_knownTypes.Add(p.Type.Name)) _typeChars = Math.Clamp(Math.Max(_typeChars, p.Type.Name.Length), 14, 26);
        if (p.Inner is not null) _knownTypes.Add(p.Inner);
        if (p.Entities is { } ids)
            foreach (var id in ids)
            {
                // Learn() runs again over the whole buffer after every filter change: only packets
                // newer than the last one counted add to the count.
                if (!_knownEntities.TryGetValue(id, out var e)) _knownEntities[id] = (p.Inner ?? p.Type.Name, 1, p.Seq);
                else if (p.Seq > e.Seq)                          _knownEntities[id] = (p.Inner ?? p.Type.Name, e.Count + 1, p.Seq);
            }
    }

    /// <summary>
    /// Entity ids for the suggestions of <paramref name="word"/> (after "entity:", or a bare
    /// number): the 8 most recently seen starting with its digits, with a hint each.
    /// </summary>
    private static List<(int Id, string Hint)> KnownEntities(string word)
    {
        var w = word.TrimStart('-');
        if (w.StartsWith("entity:", StringComparison.Ordinal)) w = w["entity:".Length..];
        else if (w.Length < 2 || !w.All(char.IsAsciiDigit)) return [];
        var found = new List<(int Id, string Type, int Count, long Seq)>();
        foreach (var (id, (type, count, seq)) in _knownEntities)
            if (id.ToString(System.Globalization.CultureInfo.InvariantCulture).StartsWith(w, StringComparison.Ordinal))
                found.Add((id, type, count, seq));
        return found.OrderByDescending(e => e.Seq).Take(8).Select(e => (e.Id, $"{e.Type} · {e.Count}")).ToList();
    }

    // One packet out of the list and its rows; rows above the screen count as removed from the top.
    private static void RemoveMatch(long seq)
    {
        var i = _matches.BinarySearch(seq);
        if (i < 0) return;
        _matches.RemoveAt(i);
        _multi.Remove(seq);
        _open.Remove(seq);
        if (_rowsDirty) return;
        int lo = 0, hi = _rows.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_rows[mid].Seq < seq) lo = mid + 1; else hi = mid;
        }
        var n = 0;
        while (lo + n < _rows.Count && _rows[lo + n].Seq == seq) n++;
        if (n == 0) return;
        _rows.RemoveRange(lo, n);
        if (lo < _topRow) _rowsRemovedAbove += Math.Min(n, _topRow - lo);
    }

    private static void Rebuild()
    {
        _rows.Clear();
        _rowsRemovedAbove = 0;   // a new list: nothing on screen to keep in place
        foreach (var seq in _matches)
        {
            _rows.Add(new Row { Seq = seq });
            if (_open.TryGetValue(seq, out var root))
                AddChildren(seq, NetDecode.Children(root), 1, 0);
        }
        _rowsDirty = false;
    }

    private static void AddChildren(long seq, List<FieldNode> children, int depth, uint guides)
    {
        for (var i = 0; i < children.Count; i++)
        {
            var c    = children[i];
            var last = i == children.Count - 1;
            _rows.Add(new Row { Seq = seq, Node = c, Depth = depth, Guides = guides, Last = last });
            if (c.Expanded && c.CanExpand)
            {
                var next = depth <= 32 && !last ? guides | (1u << (depth - 1)) : guides;
                AddChildren(seq, NetDecode.Children(c), depth + 1, next);
            }
        }
    }

    // ── drawing ──────────────────────────────────────────────────────────────────────────────

    private readonly record struct Cols(float Caret, float Time, float Dir, float Type, float SizeRight, float Summary);

    private static Cols Columns()
    {
        float W(string s) => ImGuiNET.ImGui.CalcTextSize(s).X;
        var cw    = W("M");
        var time  = cw * 2f;
        var dir   = time + W("00:00:00.000") + cw * 1.2f;
        var type  = dir + cw * 2f;
        var sizeR = type + cw * _typeChars + cw * 2f + W("999.9 KB");
        return new Cols(0f, time, dir, type, sizeR, sizeR + cw * 2f);
    }

    private static void DrawRows(Cols cols)
    {
        // The step ImGui lays a row out with: it truncates the cursor, so with a fractional font size
        // (K != 1) that is floor(line height + spacing), not GetTextLineHeightWithSpacing(). Given to
        // the clipper too: measuring the first row, which sits above the window (negative y,
        // truncated towards zero), would give one unit more than the rows on screen use.
        var step = MathF.Floor(ImGuiNET.ImGui.GetTextLineHeightWithSpacing());

        // Rows that left the top since last frame. This frame's scroll was fixed at BeginChild, so
        // they stay laid out as blank rows for one more frame while the scroll (and a running wheel
        // animation) moves up by as much; next frame both changes land together and nothing moves.
        // Also while following the bottom: with a filter a row can leave without another arriving.
        // Only as many as the scroll can absorb: at the top, the rest shift right away as before
        // (otherwise a band of blank rows would stay above the first one).
        var removed = _rowsRemovedAbove;
        _rowsRemovedAbove = 0;
        _topRow = (int)(ImGuiNET.ImGui.GetScrollY() / step);
        var ghost = Math.Min(removed, (int)(ImGuiNET.ImGui.GetScrollY() / step));
        var heading = SmoothScroll.Update(-ghost * step);
        // GetScrollMaxY() still counts last frame's blank rows; compare with where the bottom will be.
        // All removed rows count here, also those the scroll couldn't absorb (else a big turnover
        // would read as "not at the bottom" and the list would stop following).
        var bottom   = ImGuiNET.ImGui.GetScrollMaxY() - (_ghostRows + removed) * step;
        var atBottom = heading >= MathF.Max(0f, bottom) - 2f;
        _ghostRows = ghost;
        // Just after a menu closed the list hasn't followed new rows: it still counts as following.
        if (_menuHeld) atBottom |= _stickToBottom;
        _menuHeld = false;
        // End pressed: follow the newest rows again from here.
        if (_followNow) atBottom = true;
        _followNow = false;
        // The wheel takes over from a keyboard selection that held the view.
        if (ImGuiNET.ImGui.IsWindowHovered() && ImGuiNET.ImGui.GetIO().MouseWheel != 0f) _selHold = false;

        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(ghost + _rows.Count, step);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                if (i < ghost)
                {
                    ImGuiNET.ImGui.Dummy(new Vector2(1f, ImGuiNET.ImGui.GetTextLineHeight()));
                    continue;
                }
                var row = _rows[i - ghost];
                if (row.Node is null) DrawPacketRow(row.Seq, cols);
                else                  DrawFieldRow(row);
            }
        }
        clipper.End();
        clipper.Destroy();

        // Keyboard selection: keep the selected row in view.
        _pageRows = Math.Max(1, (int)(ImGuiNET.ImGui.GetWindowHeight() / step) - 1);
        if (_scrollToSel)
        {
            _scrollToSel = false;
            var sel = SelectedRow();
            if (sel >= 0)
            {
                // Compared in this frame's layout (ghost rows above), set in the next one's (none).
                var y   = (sel + ghost) * step;
                var top = ImGuiNET.ImGui.GetScrollY();
                var h   = ImGuiNET.ImGui.GetWindowHeight();
                if (y < top)                 ImGuiNET.ImGui.SetScrollY(sel * step);
                else if (y + step > top + h) ImGuiNET.ImGui.SetScrollY((sel + 1) * step - h);
            }
        }

        // While a menu is open the list holds still (its row would otherwise scroll away).
        if (DrawMenu())
        {
            _menuHeld = true;
            return;
        }
        // SetScrollHereY(1f) minus the blank rows: next frame's last row at the bottom edge.
        if (_stickToBottom && atBottom)
            ImGuiNET.ImGui.SetScrollY(MathF.Max(0f, ImGuiNET.ImGui.GetCursorPosY() - ghost * step
                - ImGuiNET.ImGui.GetStyle().ItemSpacing.Y - ImGuiNET.ImGui.GetWindowHeight()));
        // A row selected with the keys holds the view (unless it is the newest one).
        _stickToBottom = (atBottom && !_selHold) || _rows.Count == 0;
        // Selected on the newest row while following: the selection follows too.
        if (_selFollows && _stickToBottom && _rows.Count > 0) Select(_rows[^1].Seq, _rows[^1].Node, follows: true);
    }

    private static void DrawPacketRow(long seq, Cols cols)
    {
        var p = NetCapture.Get(seq);
        var pos   = ImGuiNET.ImGui.GetCursorScreenPos();
        var lineH = ImGuiNET.ImGui.GetTextLineHeight();
        var width = ImGuiNET.ImGui.GetContentRegionAvail().X;
        if (p is null)
        {
            ImGuiNET.ImGui.Dummy(new Vector2(width, lineH));
            return;
        }

        var open = _open.ContainsKey(seq);
        ImGuiNET.ImGui.PushID(unchecked((int)seq));
        var clicked = ImGuiNET.ImGui.InvisibleButton("##p", new Vector2(width, lineH)) && !MouseOverSuggestions;
        var hovered = ImGuiNET.ImGui.IsItemHovered() && !MouseOverSuggestions;
        if (!MouseOverSuggestions && ImGuiNET.ImGui.IsItemClicked(ImGuiMouseButton.Right))
        {
            // On a selected packet the menu is for the whole selection; elsewhere it selects that one.
            if (!_multi.Contains(seq)) ClearMulti();
            Select(seq, null);
            RequestMenu(seq, null);
        }
        ImGuiNET.ImGui.PopID();

        // Ctrl+click adds/removes, Shift+click selects the range; neither unfolds the packet.
        // Ctrl+Shift+click opens it in the inspector.
        if (clicked)
        {
            var io = ImGuiNET.ImGui.GetIO();
            if (io.KeyCtrl && io.KeyShift) { OpenDoc(seq); clicked = false; }
            else if (io.KeyCtrl)  { ToggleMulti(seq); clicked = false; }
            else if (io.KeyShift) { SelectRange(seq); clicked = false; Select(seq, null); }
            else                  { ClearMulti(); _anchor = seq; Select(seq, null); }
        }

        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        var pad = 3f * D;
        var doc = IsDoc(seq);
        var mark = p.Mark;
        // The fill: the multi-selection when there is one, else the keyboard row; the bar marks the
        // keyboard row only.
        var cursor   = IsSelected(seq, null);
        var selected = _multi.Count > 0 ? _multi.Contains(seq) : cursor;
        // What the active side did with it tints the row; the badge at the right end says what.
        var tint = MarkTint(mark);
        if (open || hovered || doc || selected || tint is not null)
            dl.AddRectFilled(pos - new Vector2(pad, pad * 0.5f), pos + new Vector2(width, lineH + pad * 0.5f),
                Theme.ToU32(open || selected ? Theme.Sel : hovered || doc ? Theme.P.Raised : tint!.Value), 6f * D);
        var badgeW = 0f;
        if (mark != PacketMark.None)
        {
            var (label, color) = MarkBadge(mark, p.MarkCount);
            var bw = ImGuiNET.ImGui.CalcTextSize(label).X + 12f * D;
            var bx = pos.X + width - bw - 6f * D;
            dl.AddRect(new Vector2(bx, pos.Y - 1f * D), new Vector2(bx + bw, pos.Y + lineH + 1f * D), Theme.ToU32(color), 5f * D, ImDrawFlags.None, 1f / UiScale.K);
            dl.AddText(new Vector2(bx + 6f * D, pos.Y), Theme.ToU32(color), label);
            badgeW = bw + 12f * D;
            if (mark == PacketMark.Copy) dl.PushClipRect(pos, pos + new Vector2(width - badgeW, lineH), true);
        }
        if (cursor) SelectionBar(dl, pos, lineH);

        var canOpen = open || p.Message is not null; // an unfolded packet can always be folded
        if (canOpen)
            dl.AddText(pos + new Vector2(cols.Caret, 0f), Theme.ToU32(hovered ? Theme.P.Fg : Theme.P.Muted), open ? "▾" : "▸");
        dl.AddText(pos + new Vector2(cols.Time, 0f), Theme.ToU32(Theme.P.Muted), p.TimeText ??= NetFeature.Time(p));
        dl.AddText(pos + new Vector2(cols.Dir, 0f), Theme.ToU32(p.Dir == NetDir.In ? Theme.P.Accent : Theme.P.Accent2),
            p.Dir == NetDir.In ? "↓" : "↑");
        dl.AddText(pos + new Vector2(cols.Type, 0f), Theme.ToU32(Theme.P.Fg), p.Type.Name);
        var size = p.SizeText ??= NetFeature.Bytes(p.Size);
        dl.AddText(pos + new Vector2(cols.SizeRight - ImGuiNET.ImGui.CalcTextSize(size).X, 0f), Theme.ToU32(Theme.P.Sub), size);
        if (badgeW > 0f && mark != PacketMark.Copy) dl.PushClipRect(pos, pos + new Vector2(width - badgeW, lineH), true);
        dl.AddText(pos + new Vector2(cols.Summary, 0f), Theme.ToU32(mark == PacketMark.Copy ? Theme.P.Sub : Theme.P.Muted), NetDecode.Summary(p));
        if (badgeW > 0f) dl.PopClipRect();
        if (mark == PacketMark.Blocked)
            Gfx.HLine(dl, pos.X + cols.Type, pos.X + width - badgeW, pos.Y + lineH * 0.55f, Theme.ToU32(Theme.WithAlpha(Theme.P.Red, 0.7f)));

        if (clicked && canOpen) ToggleOpen(p);
    }

    private static Vector4? MarkTint(PacketMark m) => m switch
    {
        PacketMark.Blocked    => Theme.WithAlpha(Theme.P.Red, 0.14f),
        PacketMark.Frozen or PacketMark.Releasing => Theme.WithAlpha(Theme.P.Accent, 0.14f),
        PacketMark.Duplicated => Theme.WithAlpha(Theme.P.Accent2, 0.14f),
        PacketMark.Edited     => Theme.WithAlpha(Theme.P.Accent2, 0.14f),
        PacketMark.Custom     => Theme.WithAlpha(Theme.P.Yellow, 0.12f),
        _ => null,
    };

    private static (string Label, Vector4 Color) MarkBadge(PacketMark m, int count) => m switch
    {
        PacketMark.Blocked    => ("⊘ blocked", Theme.P.Red),
        PacketMark.Frozen     => ("frozen", Theme.P.Accent),
        PacketMark.Releasing  => ("releasing", Theme.P.Accent),
        PacketMark.Duplicated => ($"×{Math.Max(count, 2)}", Theme.P.Accent2),
        PacketMark.Copy       => ("copy", Theme.P.Muted),
        PacketMark.Edited     => ("edited", Theme.P.Accent2),
        PacketMark.Custom     => ("custom", Theme.P.Yellow),
        _ => ("", Theme.P.Muted),
    };

    /// <summary>Unfolds a packet in place, or folds it back.</summary>
    private static void ToggleOpen(Packet p)
    {
        if (!_open.Remove(p.Seq))
        {
            if (p.Message is null) return;
            _open[p.Seq] = NetDecode.Root(p);
        }
        _rowsDirty = true;
    }

    // The keyboard cursor: a short accent bar at the row's left edge.
    // The list child clips at the rows' left edge, so the bar gets a clip rect of its own just left
    // of it (still clipped to the child's height).
    private static void SelectionBar(ImDrawListPtr dl, Vector2 pos, float lineH)
    {
        var win = ImGuiNET.ImGui.GetWindowPos();
        dl.PushClipRect(new Vector2(pos.X - 6f * D, win.Y), new Vector2(pos.X, win.Y + ImGuiNET.ImGui.GetWindowHeight()), false);
        dl.AddRectFilled(pos + new Vector2(-5f * D, -1.5f * D), pos + new Vector2(-3f * D, lineH + 1.5f * D),
            Theme.ToU32(Theme.P.Accent), 1f * D);
        dl.PopClipRect();
    }

    private static void DrawFieldRow(Row row)
    {
        var node = row.Node!;
        var x0 = ImGuiNET.ImGui.GetCursorScreenPos().X;
        if (DrawField(row.Seq, node, x0, row.Depth, row.Guides, row.Last, list: true))
            _rowsDirty = true;
    }

    private static void Text(string text, Vector4 color)
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGuiNET.ImGui.TextUnformatted(text);
        ImGuiNET.ImGui.PopStyleColor();
        ImGuiNET.ImGui.SameLine(0f, 0f);
    }
}
