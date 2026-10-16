// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Despada.ImGui;
using Despada.ImGui.UserInterface;
using ImGuiNET;

namespace Despada.Modules.Net;

// The tab's second row: the traffic graph and the rules button, then one powerline of tabs:
//   packets · frozen N · saved N · #seq Type … · new Type … · +
// packets is the list; frozen and saved show up once they hold something (frozen also while armed);
// open packets and drafts are closed with Ctrl+click or a middle click; + picks a type for a new packet.
internal static partial class NetView
{
    private enum TabKind { List, Frozen, Saved, Packet, Draft, Pick }

    private readonly record struct NetTab(TabKind Kind, long Seq = -1, PacketDraft? Draft = null)
    {
        public bool Closable => Kind is TabKind.Packet or TabKind.Draft or TabKind.Pick;
    }

    private static TabKind _tabKind = TabKind.List;
    private static readonly List<PacketDraft> _drafts = new();   // new-packet tabs
    private static PacketDraft? _activeDraft;

    private static bool ListShown => _tabKind == TabKind.List;

    private static void ShowList() => _tabKind = TabKind.List;

    private static void ShowDraft(PacketDraft d)
    {
        if (!_drafts.Contains(d)) _drafts.Add(d);
        _activeDraft = d;
        _tabKind = TabKind.Draft;
    }

    private static void CloseDraft(PacketDraft d)
    {
        var i = _drafts.IndexOf(d);
        if (i < 0) return;
        _drafts.RemoveAt(i);
        if (_activeDraft == d)
        {
            _activeDraft = _drafts.Count == 0 ? null : _drafts[Math.Min(i, _drafts.Count - 1)];
            if (_activeDraft is null) ShowList();
        }
    }

    /// <summary>The tabs in their order, with the active one's index.</summary>
    private static List<NetTab> Tabs(out int active)
    {
        var tabs = new List<NetTab> { new(TabKind.List) };
        if (NetActive.FrozenCount > 0 || NetActive.Armed) tabs.Add(new NetTab(TabKind.Frozen));
        if (NetActive.Saved.Count > 0) tabs.Add(new NetTab(TabKind.Saved));
        foreach (var seq in _docs) tabs.Add(new NetTab(TabKind.Packet, seq));
        foreach (var d in _drafts) tabs.Add(new NetTab(TabKind.Draft, Draft: d));
        if (_picking) tabs.Add(new NetTab(TabKind.Pick));

        active = 0;
        for (var i = 0; i < tabs.Count; i++)
        {
            var t = tabs[i];
            if (t.Kind != _tabKind) continue;
            if (t.Kind == TabKind.Packet && t.Seq != _activeDoc) continue;
            if (t.Kind == TabKind.Draft && t.Draft != _activeDraft) continue;
            active = i;
            break;
        }
        // The active tab is gone (closed, the frozen queue emptied): back to the list.
        if (active == 0 && _tabKind != TabKind.List) ShowList();
        return tabs;
    }

    private static string Label(NetTab t) => t.Kind switch
    {
        TabKind.List   => "packets",
        TabKind.Frozen => $"frozen {NetActive.FrozenCount}",
        TabKind.Saved  => $"saved {NetActive.Saved.Count}",
        TabKind.Packet => NetCapture.Get(t.Seq) is { } p ? $"#{t.Seq} {Short(p.Inner ?? p.Type.Name)}" : $"#{t.Seq}",
        TabKind.Draft  => $"new {Short(t.Draft!.TypeName)}",
        TabKind.Pick   => "new packet",
        _ => "",
    };

    private static void Activate(NetTab t)
    {
        _tabKind = t.Kind;
        if (t.Kind == TabKind.Packet) _activeDoc = t.Seq;
        if (t.Kind == TabKind.Draft)  _activeDraft = t.Draft;
    }

    private static void CloseTab(NetTab t)
    {
        if (t.Kind == TabKind.Packet) CloseDoc(t.Seq);
        if (t.Kind == TabKind.Draft)  CloseDraft(t.Draft!);
        if (t.Kind == TabKind.Pick)   ClosePicker();
    }

    /// <summary>After the traffic graph: the rules button and the tabs.</summary>
    private static void DrawTabRow()
    {
        RowSeparator();   // SameLine: back on the counters' line
        var y = ImGuiNET.ImGui.GetCursorPosY();
        DrawRulesButton();
        RowSeparator();

        // The tabs; names shorten to fit, then the rest go under "…" (the + always fits).
        var tabs   = Tabs(out var active);
        var plusW  = ImGuiNET.ImGui.CalcTextSize("+").X + 12f * D + 10f * D;
        var names  = tabs.Select(Label).ToList();
        var labels = Powerline.Fit(names, active, ImGuiNET.ImGui.GetContentRegionAvail().X - plusW - 12f * D, out var from, out var to);

        NetTab? close = null;
        for (var i = from; i <= to; i++)
        {
            if (i > from) Powerline.Next();
            ImGuiNET.ImGui.SetCursorPosY(y);
            var t = tabs[i];
            ImGuiNET.ImGui.PushID(i);
            var tone = t.Kind switch { TabKind.Frozen => Theme.P.Accent, TabKind.Draft or TabKind.Pick => Theme.P.Yellow, _ => (Vector4?)null };
            if (Powerline.Segment("##tab", labels[i], i == active, i == from, false, out var closeRequested, tone))
                Activate(t);
            if (closeRequested)
            {
                if (t.Closable) close = t;
                else            Activate(t);   // Ctrl+click on a fixed tab just selects it
            }
            ImGuiNET.ImGui.PopID();
        }
        if (from > 0 || to < tabs.Count - 1)
        {
            var picked = Powerline.Overflow("##tabs-more", names, active);
            if (picked >= 0) Activate(tabs[picked]);
        }

        Powerline.After(10f * D);
        ImGuiNET.ImGui.SetCursorPosY(y);
        if (Powerline.Plus("##new-packet")) OpenPicker();

        ImGuiNET.ImGui.NewLine();
        if (close is { } c) CloseTab(c);
    }

    // A hairline between the row's groups.
    private static void RowSeparator()
    {
        ImGuiNET.ImGui.SameLine(0f, 14f * D);
        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        var p0 = ImGuiNET.ImGui.GetCursorScreenPos();
        Gfx.VLine(dl, p0.X, p0.Y + 8f * D, p0.Y + ImGuiNET.ImGui.GetFrameHeight() - 8f * D, Theme.ToU32(Theme.P.Sep));
        // An item for the line, so the next SameLine counts from it.
        ImGuiNET.ImGui.Dummy(new Vector2(1f, ImGuiNET.ImGui.GetFrameHeight()));
        ImGuiNET.ImGui.SameLine(0f, 14f * D);
    }

}
