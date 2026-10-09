// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using Despada.ImGui;
using Despada.ImGui.UserInterface;
using ImGuiNET;

namespace Despada.Modules.Net;

// The inspector: packets opened with a double or middle click, Ctrl+Enter (or the menu) are pills in the tab's second row,
// after a "packets" pill for the list. The active one takes the whole tab: header, fields, related
// packets, bytes. Ctrl+click or a middle click on a pill closes it. Open packets are pinned, so
// their message and bytes stay however old they get.
internal static partial class NetView
{
    private const int MaxDocs = 8;

    private static readonly List<long> _docs = new();
    private static long _activeDoc = -1;

    // The active packet's own tree (folding here doesn't touch the list's) and its related packets.
    private static long       _docTreeSeq = -1;
    private static FieldNode? _docTree;
    private static List<(Packet Packet, string Why)>? _related;
    private static long _relatedAt;
    private static List<(string Offset, string Hex, string Ascii)>? _docHex;

    private static bool IsDoc(long seq) => _docs.Contains(seq);

    public static void OpenDoc(long seq)
    {
        if (!_docs.Contains(seq))
        {
            if (_docs.Count == MaxDocs) CloseDoc(_docs[0]);
            _docs.Add(seq);
            NetCapture.Pin(seq);
        }
        _activeDoc = seq;
    }

    private static void CloseDoc(long seq)
    {
        var i = _docs.IndexOf(seq);
        if (i < 0) return;
        _docs.RemoveAt(i);
        NetCapture.Unpin(seq);
        if (seq == _docTreeSeq)
        {
            // Its tree holds the message object.
            _docTreeSeq = -1;
            _docTree = null;
            _related = null;
            _docHex = null;
        }
        if (_activeDoc == seq) _activeDoc = _docs.Count == 0 ? -1 : _docs[Math.Min(i, _docs.Count - 1)];
    }

    /// <summary>The active packet, if it's still in the buffer (closes the ones that aren't).</summary>
    private static Packet? ActiveDoc()
    {
        for (var i = _docs.Count - 1; i >= 0; i--)
            if (NetCapture.Get(_docs[i]) is null) CloseDoc(_docs[i]);
        if (_activeDoc >= 0 && !_docs.Contains(_activeDoc)) _activeDoc = -1;
        return _activeDoc < 0 ? null : NetCapture.Get(_activeDoc);
    }

    /// <summary>Drops the inspector state (its tree holds the message object).</summary>
    private static void ForgetDocs()
    {
        foreach (var seq in _docs)
            NetCapture.Unpin(seq);
        _docs.Clear();
        _activeDoc = -1;
        _docTreeSeq = -1;
        _docTree = null;
        _related = null;
        _docHex = null;
    }

    private static void DrawDocPills()
    {
        _pillsMin = _pillsMax = Vector2.Zero;
        if (_docs.Count == 0) return;
        ImGuiNET.ImGui.SameLine(0f, 14f * D);
        var y  = ImGuiNET.ImGui.GetCursorPosY();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        var p0 = ImGuiNET.ImGui.GetCursorScreenPos();
        Gfx.VLine(dl, p0.X, p0.Y + 8f * D, p0.Y + ImGuiNET.ImGui.GetFrameHeight() - 8f * D, Theme.ToU32(Theme.P.Sep));
        // An item for the line, so the next SameLine counts from it (not again from the counters).
        ImGuiNET.ImGui.Dummy(new Vector2(1f, ImGuiNET.ImGui.GetFrameHeight()));
        ImGuiNET.ImGui.SameLine(0f, 14f * D);

        // A powerline: "packets" (the list), then the open packets (Alt+1, Alt+2 …). Too many for the
        // row: names shorten, then the rest go under "…".
        var names = new List<string>(_docs.Count + 1) { "packets" };
        foreach (var seq in _docs)
            names.Add(NetCapture.Get(seq) is { } p ? $"#{seq} {Short(p.Inner ?? p.Type.Name)}" : $"#{seq}");
        var active = _activeDoc < 0 ? 0 : _docs.IndexOf(_activeDoc) + 1;
        var labels = Powerline.Fit(names, active, ImGuiNET.ImGui.GetContentRegionAvail().X - 12f * D,
            out var from, out var to);

        _pillsMin = ImGuiNET.ImGui.GetCursorScreenPos();
        long close = -1;
        for (var i = from; i <= to; i++)
        {
            if (i > from) Powerline.Next();
            ImGuiNET.ImGui.SetCursorPosY(y);
            var seq = i == 0 ? -1 : _docs[i - 1];
            if (Powerline.Segment(i == 0 ? "##docs-list" : $"##doc{seq}", labels[i], seq == _activeDoc, i == from,
                    false, out var closeRequested))
                _activeDoc = seq;
            // Ctrl+click on the list's segment just selects it: the list can't be closed.
            if (closeRequested)
            {
                if (seq < 0) _activeDoc = -1;
                else         close = seq;
            }
        }
        if (from > 0 || to < names.Count - 1)
        {
            var picked = Powerline.Overflow("##docs-more", names, active);
            if (picked >= 0) _activeDoc = picked == 0 ? -1 : _docs[picked - 1];
        }
        _pillsMax = ImGuiNET.ImGui.GetItemRectMax();
        ImGuiNET.ImGui.NewLine();
        if (close >= 0) CloseDoc(close);
    }

    /// <summary>The open packet <paramref name="seq"/> becomes <paramref name="next"/>, in the same tab.</summary>
    private static void ReplaceDoc(long seq, long next)
    {
        if (_docs.Contains(next))
        {
            _activeDoc = next;
            return;
        }
        var i = _docs.IndexOf(seq);
        if (i < 0) return;
        NetCapture.Unpin(seq);
        _docs[i] = next;
        NetCapture.Pin(next);
        _activeDoc = next;
        _inspJump = -2;   // a new packet starts at its top
    }

    private static string Short(string name) => name.Length <= 18 ? name : name[..17] + "…";

    private static void DrawInspector(float width, float height)
    {
        var p = ActiveDoc();
        if (p is null) return;
        // Rebuilt for another packet, and once the packet's message is released.
        if (_docTreeSeq != p.Seq || (p.Message is null && _docTree?.Value is not null))
        {
            _docTreeSeq = p.Seq;
            _docTree = NetDecode.Root(p);
            if (_docTree.CanExpand)
                foreach (var c in NetDecode.Children(_docTree))
                    c.Expanded = c.CanExpand && c.Depth <= 1 && c.Value?.GetType().Name is not "GameState";
            _related = null;
            _docHex = p.Bytes is { } b ? NetDecode.HexLines(b).Take(64).ToList() : null;
        }
        var bytes = p.Bytes;
        if (bytes is null) _docHex = null;

        // Styles are read by BeginChild; popped right away so the menu inside gets the usual ones.
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.P.Deep);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Theme.SmallRounding);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(18f * D, 14f * D));
        ImGuiNET.ImGui.BeginChild("##inspector", new Vector2(width, height), ImGuiChildFlags.AlwaysUseWindowPadding,
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.HorizontalScrollbar);
        ImGuiNET.ImGui.PopStyleVar(2);
        ImGuiNET.ImGui.PopStyleColor();
        SmoothScroll.Update();
        ApplyInspectorScroll();

        // Header: #4512 ↑ MsgEntity · RequestInteractEvent, then time · size · tick · delivery.
        Text($"#{p.Seq}  ", Theme.P.Fg);
        Text(p.Dir == NetDir.In ? "↓ " : "↑ ", p.Dir == NetDir.In ? Theme.P.Accent : Theme.P.Accent2);
        Text(p.Type.Name, Theme.P.Fg);
        if (p.Inner is not null)
        {
            Text("  ·  ", Theme.P.Muted);
            Text(p.Inner, Theme.P.Accent2);
        }
        ImGuiNET.ImGui.NewLine();
        Text($"{NetFeature.Time(p)} · {NetFeature.Bytes(p.Size)} · tick {p.Tick} · {Delivery(p.Delivery)}" +
             (p.Channel != 0 ? $" · channel {p.Channel}" : ""), Theme.P.Muted);
        ImGuiNET.ImGui.NewLine();

        if (Section("fields", null, ref _showFields))
        {
            if (_docTree is { CanExpand: true })
                DrawTree(p.Seq, NetDecode.Children(_docTree), ImGuiNET.ImGui.GetCursorScreenPos().X - 2f * ImGuiNET.ImGui.CalcTextSize("M").X, 1, 0);
            else
            {
                Text(p.Message is null ? "no longer kept" : "—", Theme.P.Muted);
                ImGuiNET.ImGui.NewLine();
            }
            EndSection();
        }

        // Refreshed while packets after it keep arriving.
        if (_related is null || Stopwatch.GetElapsedTime(_relatedAt).TotalSeconds > 1)
        {
            _related = FindRelated(p);
            _relatedAt = Stopwatch.GetTimestamp();
        }
        if (Section("related", _related.Count > 0 ? _related.Count.ToString() : null, ref _showRelated))
        {
            if (_related.Count == 0)
            {
                Text("—", Theme.P.Muted);
                ImGuiNET.ImGui.NewLine();
            }
            DrawRelated();
            EndSection();
        }

        // Collapsed, the bytes line still shows the size and how the packet starts.
        var bytesInfo = bytes is null ? null : NetFeature.Bytes(bytes.Length);
        var bytesHead = bytes is null ? null
            : $"{bytesInfo}  ·  {string.Join(' ', bytes.Take(8).Select(b => b.ToString("x2")))}{(bytes.Length > 8 ? " …" : "")}";
        if (Section("bytes", bytesInfo, ref _showBytes, bytesHead))
        {
            if (_docHex is null)
            {
                Text("no longer kept", Theme.P.Muted);
                ImGuiNET.ImGui.NewLine();
            }
            else
                foreach (var (offset, hex, ascii) in _docHex)
                {
                    Text(offset + "  ", Theme.P.Muted);
                    Text(hex + " ", Theme.P.Sub);
                    Text(ascii, Theme.P.Muted);
                    ImGuiNET.ImGui.NewLine();
                }
            if (_docHex is not null && bytes is { Length: > 1024 })
            {
                Text($"… {bytes.Length - 1024:N0} more bytes", Theme.P.Muted);
                ImGuiNET.ImGui.NewLine();
            }
            EndSection();
        }

        DrawMenu();
        ImGuiNET.ImGui.EndChild();
        _bodyMin = ImGuiNET.ImGui.GetItemRectMin();
        _bodyMax = ImGuiNET.ImGui.GetItemRectMax();
    }

    private static void DrawRelated()
    {
        foreach (var (r, why) in _related!)
        {
            ImGuiNET.ImGui.PushID(unchecked((int)r.Seq));
            var pos = ImGuiNET.ImGui.GetCursorScreenPos();
            var label = $"#{r.Seq}";
            if (ImGuiNET.ImGui.InvisibleButton("##rel", ImGuiNET.ImGui.CalcTextSize(label))) OpenDoc(r.Seq);
            var hovered = ImGuiNET.ImGui.IsItemHovered();
            ImGuiNET.ImGui.GetWindowDrawList().AddText(pos, Theme.ToU32(hovered ? Theme.P.Accent : Theme.P.Link), label);
            ImGuiNET.ImGui.PopID();
            ImGuiNET.ImGui.SameLine(0f, 0f);
            Text("  " + (r.Dir == NetDir.In ? "↓ " : "↑ "), r.Dir == NetDir.In ? Theme.P.Accent : Theme.P.Accent2);
            Text(r.Inner ?? r.Type.Name, Theme.P.Fg);
            Text("  " + why, Theme.P.Muted);
            ImGuiNET.ImGui.NewLine();
        }
    }

    private static void DrawTree(long seq, List<FieldNode> nodes, float x0, int depth, uint guides)
    {
        var rowH = ImGuiNET.ImGui.GetTextLineHeightWithSpacing();
        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            var last = i == nodes.Count - 1;
            // Rows outside the view only take their space (an unfolded GameState is thousands of rows).
            if (ImGuiNET.ImGui.IsRectVisible(new Vector2(1f, rowH)))
                DrawField(seq, n, x0, depth, guides, last);
            else
                ImGuiNET.ImGui.Dummy(new Vector2(1f, ImGuiNET.ImGui.GetTextLineHeight()));
            if (n.Expanded && n.CanExpand)
                DrawTree(seq, NetDecode.Children(n), x0, depth + 1, depth <= 32 && !last ? guides | (1u << (depth - 1)) : guides);
        }
    }

    /// <summary>
    /// A field row (list or inspector) that opens the field menu; true when it was folded/unfolded.
    /// In the list a click also selects it for the keyboard.
    /// </summary>
    private static bool DrawField(long seq, FieldNode node, float x0, int depth, uint guides, bool last, bool list = false)
    {
        var pos      = ImGuiNET.ImGui.GetCursorScreenPos();
        var lineH    = ImGuiNET.ImGui.GetTextLineHeight();
        var selected = list && IsSelected(seq, node);
        if (selected)
        {
            var dl  = ImGuiNET.ImGui.GetWindowDrawList();
            var pad = 3f * D;
            dl.AddRectFilled(pos - new Vector2(pad, pad * 0.5f),
                pos + new Vector2(ImGuiNET.ImGui.GetContentRegionAvail().X, lineH + pad * 0.5f), Theme.ToU32(Theme.Sel), 6f * D);
            SelectionBar(dl, pos, lineH);
        }
        var toggled = TreeRows.Draw(RuntimeHelpers.GetHashCode(node), x0, depth, guides, last, node.CanExpand,
                          !node.Expanded, node.Text) && !MouseOverSuggestions;
        // The row's toggle button owns the hover, so test the row rectangle itself.
        // AllowWhenBlockedByActiveItem: on the press frame the node's own toggle is already active.
        var overRow = !MouseOverSuggestions && ImGuiNET.ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem)
                      && ImGuiNET.ImGui.IsMouseHoveringRect(ImGuiNET.ImGui.GetItemRectMin(), ImGuiNET.ImGui.GetItemRectMax());
        if (list && overRow && (ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right)))
        {
            ClearMulti();
            Select(seq, node);
        }
        if (overRow && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            RequestMenu(seq, node);
        if (toggled) node.Expanded = !node.Expanded;
        return toggled;
    }

    // The packets nearest in time (±3 s, ±400 packets) that mention any of the same entities.
    private static List<(Packet, string)> FindRelated(Packet p)
    {
        if (p.Entities is not { Length: > 0 } ids) return [];
        return NetCapture.Range(p.Seq - 400, p.Seq + 400)
            .Where(r => r.Seq != p.Seq && r.Entities is not null)
            .Select(r => (r, Dt: Stopwatch.GetElapsedTime(p.Stamp, r.Stamp).TotalMilliseconds,
                          Shared: r.Entities!.Intersect(ids).FirstOrDefault()))
            .Where(x => x.Shared != 0 && Math.Abs(x.Dt) <= 3000)
            .OrderBy(x => Math.Abs(x.Dt))
            .Take(8)
            .OrderBy(x => x.r.Seq)
            .Select(x => (x.r, $"{(x.Dt >= 0 ? "+" : "−")}{Math.Abs(x.Dt):0} ms · {x.Shared}"))
            .ToList();
    }

    // Sections fold with a click on their caption; folding is kept across packets.
    private static bool _showFields = true, _showRelated = true, _showBytes;

    /// <summary>"▾ caption  info" line; true (and indented) when the section is open.</summary>
    /// <param name="closedInfo">Shown instead of <paramref name="info"/> while the section is folded.</param>
    private static bool Section(string caption, string? info, ref bool open, string? closedInfo = null)
    {
        ImGuiNET.ImGui.Dummy(new Vector2(0f, 6f * D));
        var pos  = ImGuiNET.ImGui.GetCursorScreenPos();
        var widest = (closedInfo?.Length ?? 0) > (info?.Length ?? 0) ? closedInfo : info;
        var w    = ImGuiNET.ImGui.CalcTextSize("▾ " + caption + (widest is null ? "" : "  " + widest)).X;
        if (ImGuiNET.ImGui.InvisibleButton("##sec-" + caption, new Vector2(w, ImGuiNET.ImGui.GetTextLineHeight())))
            open = !open;
        var text = (open ? "▾ " : "▸ ") + caption;
        if (!open && closedInfo is not null) info = closedInfo;
        var hovered = ImGuiNET.ImGui.IsItemHovered();
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        dl.AddText(pos, Theme.ToU32(hovered ? Theme.P.Fg : Theme.P.Sub), text);
        if (info is not null)
            dl.AddText(pos + new Vector2(ImGuiNET.ImGui.CalcTextSize(text + "  ").X, 0f), Theme.ToU32(Theme.P.Muted), info);
        if (open) ImGuiNET.ImGui.Indent(2f * ImGuiNET.ImGui.CalcTextSize("M").X);
        return open;
    }

    private static void EndSection() => ImGuiNET.ImGui.Unindent(2f * ImGuiNET.ImGui.CalcTextSize("M").X);

    // Lidgren NetDeliveryMethod values.
    private static string Delivery(int d) => d switch
    {
        1  => "Unreliable",
        2  => "UnreliableSequenced",
        34 => "ReliableUnordered",
        35 => "ReliableSequenced",
        67 => "ReliableOrdered",
        _  => $"delivery {d}",
    };
}
