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

// The inspector: packets opened with Ctrl+Shift+click, Ctrl+Enter or the menu are tabs in
// the second row. The active one takes the whole body: header, fields, related packets, bytes, and
// a footer (↑ send again · × N every M ms · pencil). The pencil turns the fields into controls
// (✓ keeps the changes on this tab, ✗ drops them); an edited packet shows its draft and sends that.
// Open packets are pinned, so their message and bytes stay however old they get.
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

    // Editing: the draft being edited and whose; accepted edits, per packet.
    private static PacketDraft? _editing;
    private static long _editingSeq = -1;
    private static readonly Dictionary<long, PacketDraft> _edited = new();

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
        _tabKind = TabKind.Packet;
    }

    /// <summary>Opens the packet and starts editing it.</summary>
    public static void EditDoc(long seq)
    {
        OpenDoc(seq);
        BeginEdit();
    }

    private static void BeginEdit()
    {
        var p = ActiveDoc();
        if (p is null) return;
        var d = _edited.TryGetValue(p.Seq, out var e) ? e.Clone() : PacketDraft.From(p);
        if (d is null)
        {
            ImGuiRenderer.ShowToast("network", "no longer kept: nothing to edit", 2f);
            return;
        }
        _editing = d;
        _editingSeq = p.Seq;
    }

    private static void CloseDoc(long seq)
    {
        var i = _docs.IndexOf(seq);
        if (i < 0) return;
        _docs.RemoveAt(i);
        NetCapture.Unpin(seq);
        _edited.Remove(seq);
        if (_editingSeq == seq) { _editing = null; _editingSeq = -1; }
        if (seq == _docTreeSeq)
        {
            // Its tree holds the message object.
            _docTreeSeq = -1;
            _docTree = null;
            _related = null;
            ForgetStream();
        }
        if (_activeDoc == seq) _activeDoc = _docs.Count == 0 ? -1 : _docs[Math.Min(i, _docs.Count - 1)];
        if (_activeDoc < 0 && _tabKind == TabKind.Packet) ShowList();
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
        _edited.Clear();
        _editing = null;
        _editingSeq = -1;
        _drafts.Clear();
        _activeDraft = null;
        ShowList();
        _docTreeSeq = -1;
        _docTree = null;
        _related = null;
        ForgetStream();
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
        if (_editingSeq == seq) CancelEdit();
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
            ExpandDefault(_docTree, 1);
            _related = null;
            _selField = null;
        }
        var bytes = p.Bytes;
        if (bytes is not null) EnsureStream(p, bytes);   // before the head reads its counts
        _hoverField = _hoverFieldNext;
        _hoverRow   = _hoverRowNext;
        _hoverFieldNext = _hoverRowNext = null;

        var editing = _editing is not null && _editingSeq == p.Seq ? _editing : null;
        var edited  = editing is null && _edited.TryGetValue(p.Seq, out var ed) ? ed : null;

        BeginPanel("##inspector", width, height);

        // Header: #4512 ↑ MsgEntity · RequestInteractEvent, then time · size · tick · delivery.
        // Its right end: "editing · N changed", or "edited · N changed  revert"; a long title is
        // clipped before it (the tag and the button stay reachable however narrow the window).
        var rv  = "revert";
        var tag = editing is not null ? (editing.ChangedCount > 0 ? $"editing · {editing.ChangedCount} changed" : "editing")
                : edited is not null  ? $"edited · {edited.ChangedCount} changed   " : null;
        var tagW    = tag is null ? 0f : ImGuiNET.ImGui.CalcTextSize(tag).X
                    + (edited is not null ? ImGuiNET.ImGui.CalcTextSize(rv).X + 2f * ImGuiNET.ImGui.GetStyle().FramePadding.X : 0f);
        var headTop = ImGuiNET.ImGui.GetCursorPos();
        var right   = headTop.X + ImGuiNET.ImGui.GetContentRegionAvail().X;
        var dl      = ImGuiNET.ImGui.GetWindowDrawList();
        var clipped = tag is not null;
        if (clipped)
        {
            var sp = ImGuiNET.ImGui.GetCursorScreenPos();
            dl.PushClipRect(sp, new Vector2(sp.X + right - tagW - 2f * ImGuiNET.ImGui.CalcTextSize("M").X - headTop.X, sp.Y + ImGuiNET.ImGui.GetTextLineHeight()), true);
        }
        Text($"#{p.Seq}  ", Theme.P.Fg);
        Text(p.Dir == NetDir.In ? "↓ " : "↑ ", p.Dir == NetDir.In ? Theme.P.Accent : Theme.P.Accent2);
        Text(p.Type.Name, Theme.P.Fg);
        if (p.Inner is not null)
        {
            Text("  ·  ", Theme.P.Muted);
            Text(p.Inner, Theme.P.Accent2);
        }
        if (clipped) dl.PopClipRect();
        ImGuiNET.ImGui.NewLine();
        Text($"{NetFeature.Time(p)} · {NetFeature.Bytes(p.Size)} · tick {p.Tick} · {Delivery(p.Delivery)}" +
             (p.Channel != 0 ? $" · channel {p.Channel}" : ""), Theme.P.Muted);
        ImGuiNET.ImGui.NewLine();
        if (tag is not null)
        {
            ImGuiNET.ImGui.SetCursorPos(new Vector2(right - tagW, headTop.Y));
            Text(tag, editing is not null ? Theme.P.Yellow : Theme.P.Accent2);
            if (edited is not null)
            {
                if (ImGuiNET.ImGui.SmallButton(rv)) _edited.Remove(p.Seq);
            }
            ImGuiNET.ImGui.NewLine();
        }
        ImGuiNET.ImGui.SetCursorPosY(headTop.Y + ImGuiNET.ImGui.GetTextLineHeightWithSpacing() * 2f);

        // Two columns when the bytes pane is open and there is room: fields and related at the
        // left, the bytes at the right. Folded (or narrow), one column with the bytes line last.
        var twoCols = bytes is not null && ImGuiNET.ImGui.GetContentRegionAvail().X >= 700f * D;
        var bodyH   = -FooterHeight();
        var leftW   = twoCols ? MathF.Floor(ImGuiNET.ImGui.GetContentRegionAvail().X * 0.53f) : -1f;

        ImGuiNET.ImGui.BeginChild("##insp-body", new Vector2(leftW, bodyH), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollWithMouse);
        SmoothScroll.Update();
        ApplyInspectorScroll();

        if (Section("fields", editing is not null ? "editing" : edited is not null ? "edited" : null, ref _showFields))
        {
            if (editing is not null)
                DrawDraftRows(editing, editable: true);
            else if (edited is not null)
                DrawDraftRows(edited, editable: false);
            else if (_docTree is { CanExpand: true })
                DrawFieldTable(p.Seq, _docTree);
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

        if (!twoCols)
        {
            BytesHead(bytes, withSwitch: true);
            ImGuiNET.ImGui.Indent(2f * ImGuiNET.ImGui.CalcTextSize("M").X);
            DrawBytesBody(p, bytes);
            ImGuiNET.ImGui.Unindent(2f * ImGuiNET.ImGui.CalcTextSize("M").X);
        }

        DrawMenu();
        ImGuiNET.ImGui.EndChild();

        if (twoCols)
        {
            ImGuiNET.ImGui.SameLine(0f, 0f);
            var sepX = ImGuiNET.ImGui.GetCursorScreenPos().X + 10f * D;
            var sepY = ImGuiNET.ImGui.GetCursorScreenPos().Y;
            Gfx.VLine(ImGuiNET.ImGui.GetWindowDrawList(), sepX, sepY + 8f * D, sepY + ImGuiNET.ImGui.GetContentRegionAvail().Y + bodyH - 8f * D, Theme.ToU32(Theme.P.Sep));
            ImGuiNET.ImGui.SameLine(0f, 22f * D);
            ImGuiNET.ImGui.BeginChild("##insp-bytes", new Vector2(-1f, bodyH), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollWithMouse);
            SmoothScroll.Update();
            BytesHead(bytes, withSwitch: true);
            DrawBytesBody(p, bytes);
            ImGuiNET.ImGui.EndChild();
        }

        // The footer: send again (the draft when edited), repeat, and the mode's buttons.
        var action = DrawFooter(editing is not null ? FooterMode.Edit : FooterMode.View, p.Dir);
        switch (action)
        {
            case FooterAction.Send:
                if (editing is not null) NetActive.Send(editing, _sendTimes, _sendEveryMs);
                else if (edited is not null) NetActive.Send(edited, _sendTimes, _sendEveryMs);
                else NetActive.Resend(p, _sendTimes, _sendEveryMs);
                break;
            case FooterAction.Edit:   BeginEdit(); break;
            case FooterAction.Accept: AcceptEdit(); break;
            case FooterAction.Cancel: CancelEdit(); break;
        }
        ImGuiNET.ImGui.EndChild();
    }

    private static void AcceptEdit()
    {
        if (_editing is null) return;
        if (_editing.ChangedCount > 0) _edited[_editingSeq] = _editing;
        else _edited.Remove(_editingSeq);
        _editing = null;
        _editingSeq = -1;
    }

    private static void CancelEdit()
    {
        _editing = null;
        _editingSeq = -1;
    }

    /// <summary>Saves the open packet (its edited draft when it has one) under a name.</summary>
    private static void SaveDoc(Packet p)
    {
        var d = _edited.TryGetValue(p.Seq, out var e) ? e : PacketDraft.From(p);
        if (d is null) { ImGuiRenderer.ShowToast("network", "no longer kept: nothing to save", 2f); return; }
        SaveDraft(d);
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
    private static bool _showFields = true, _showRelated = true;

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
