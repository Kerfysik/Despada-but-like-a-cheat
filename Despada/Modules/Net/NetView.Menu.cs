// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Despada.ImGui;
using Despada.ImGui.UserInterface;
using ImGuiNET;

namespace Despada.Modules.Net;

// Right-click menus on packets and on fields. Rows only open the menu; it is drawn once per window
// after the rows, so it stays put when its row scrolls out of the clipper's range.
internal static partial class NetView
{
    private static long       _menuSeq = -1;
    private static FieldNode? _menuNode;
    private static bool       _menuRequested;

    private static void RequestMenu(long seq, FieldNode? node)
    {
        _menuSeq = seq;
        _menuNode = node;
        _menuRequested = true;
    }

    /// <summary>Draws the menu of the current window; true while it is open.</summary>
    private static bool DrawMenu()
    {
        if (_menuRequested)
        {
            _menuRequested = false;
            ImGuiNET.ImGui.OpenPopup("##netmenu");
            _menuSel = null;
            if (_menuNode is null && _multi.Count > 1 && _multi.Contains(_menuSeq)) SnapshotSelection();
        }
        if (!ImGuiNET.ImGui.BeginPopup("##netmenu")) return false;
        if (NetCapture.Get(_menuSeq) is not { } p)
            ImGuiNET.ImGui.CloseCurrentPopup();
        else if (_menuNode is not null)
            FieldMenu(p, _menuNode);
        else if (_menuSel is not null)
            SelectionMenu();
        else
            PacketMenu(p);
        ImGuiNET.ImGui.EndPopup();
        return true;
    }

    // The selection the menu was opened for, worked out once (a Ctrl+A can be 20 000 packets).
    private static List<Packet>? _menuSel;
    private static List<string>  _menuTypes = [];
    private static List<int>     _menuShared = [];

    private static void SnapshotSelection()
    {
        _menuSel   = SelectedPackets();
        _menuTypes = _menuSel.Select(x => x.Inner ?? x.Type.Name).Distinct().ToList();
        var shared = new HashSet<int>(_menuSel.Count > 0 ? _menuSel[0].Entities ?? [] : []);
        foreach (var p in _menuSel)
        {
            if (shared.Count == 0) break;
            shared.IntersectWith(p.Entities ?? []);
        }
        _menuShared = shared.Take(3).ToList();
    }

    // The menu of a multi-selection.
    private static void SelectionMenu()
    {
        var ps = _menuSel!;
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Theme.P.Muted);
        ImGuiNET.ImGui.TextUnformatted($"{ps.Count} packets");
        ImGuiNET.ImGui.PopStyleColor();

        if (ImGuiNET.ImGui.MenuItem(ps.Count > MaxDocs ? $"Open last {MaxDocs} in inspector" : "Open in inspector"))
            OpenAll(ps);
        ImGuiNET.ImGui.Separator();
        if (ImGuiNET.ImGui.MenuItem("Copy summaries"))
            Clipboard.Set(string.Join("\n", ps.Select(SummaryLine)));
        if (ImGuiNET.ImGui.MenuItem("Copy fields", null, false, ps.Any(x => x.Message is not null)))
            Clipboard.Set(string.Join("\n", ps.Where(x => x.Message is not null)
                .Select(x => $"#{x.Seq}\n" + NetDecode.ToText(NetDecode.Root(x)))));
        if (ImGuiNET.ImGui.MenuItem("Copy hex", null, false, ps.Any(x => x.Bytes is not null)))
            Clipboard.Set(string.Join("\n\n", ps.Where(x => x.Bytes is not null).Select(x => $"#{x.Seq}\n" + HexText(x.Bytes!))));
        ImGuiNET.ImGui.Separator();

        // "Only" works for one type (type: tokens are all required); hiding works for any number.
        var types = _menuTypes;
        if (types.Count == 1 && ImGuiNET.ImGui.MenuItem($"Only type:{types[0]}")) AddFilter($"type:{types[0]}");
        if (ImGuiNET.ImGui.MenuItem(types.Count == 1 ? $"Hide type:{types[0]}" : $"Hide these {types.Count} types"))
            foreach (var t in types) AddFilter($"-type:{t}");
        foreach (var id in _menuShared)
            if (ImGuiNET.ImGui.MenuItem($"With entity {id}")) AddFilter($"entity:{id}");
        ImGuiNET.ImGui.Separator();
        if (ImGuiNET.ImGui.MenuItem("Clear selection")) ClearMulti();
    }

    private static void PacketMenu(Packet p)
    {
        var type = p.Inner ?? p.Type.Name;

        if (ImGuiNET.ImGui.MenuItem("Open in inspector")) OpenDoc(p.Seq);
        ImGuiNET.ImGui.Separator();

        if (ImGuiNET.ImGui.MenuItem("Copy summary"))
            Clipboard.Set($"#{p.Seq} {NetFeature.Time(p)} {(p.Dir == NetDir.In ? "in" : "out")} {p.Type.Name} {p.Size} B {NetDecode.Summary(p)}");
        if (ImGuiNET.ImGui.MenuItem("Copy fields", null, false, p.Message is not null))
            Clipboard.Set(NetDecode.ToText(NetDecode.Root(p)));
        var bytes = p.Bytes;
        if (ImGuiNET.ImGui.MenuItem("Copy hex", null, false, bytes is not null))
            Clipboard.Set(string.Join("\n", NetDecode.HexLines(bytes!).Select(l => $"{l.Offset}  {l.Hex} {l.Ascii}")));
        ImGuiNET.ImGui.Separator();

        if (ImGuiNET.ImGui.MenuItem($"Only type:{type}")) AddFilter($"type:{type}");
        if (ImGuiNET.ImGui.MenuItem($"Hide type:{type}")) AddFilter($"-type:{type}");
        if (ImGuiNET.ImGui.MenuItem($"Only tick {p.Tick}")) AddFilter($"tick:{p.Tick}");
        foreach (var id in p.Entities ?? [])
            if (ImGuiNET.ImGui.MenuItem($"With entity {id}")) AddFilter($"entity:{id}");
        ImGuiNET.ImGui.Separator();

        if (ImGuiNET.ImGui.MenuItem("Find in console"))
        {
            Shell.ShowConsole();
            ConsolePanel.Run($"net.find type:{type} --last 20");
        }
    }

    private static void FieldMenu(Packet p, FieldNode node)
    {
        // The value part of the row: everything after the name.
        // Rows without a name (lines of a text, "… more") are all value.
        var value = string.Concat(node.Text.Skip(node.Depth == 0 || node.Name.Length == 0 || node.Name == "…" ? 0 : 1)
            .Select(s => s.Text)).Trim();
        var entity = node.Value?.GetType().Name == "NetEntity" ? NetDecode.Read(node.Value, "Id") as int? : null;

        // A long or multi-line string: the whole of it, not the shortened row.
        if (ImGuiNET.ImGui.MenuItem("Copy value")) Clipboard.Set(node.Value as string ?? value);
        if (node.CanExpand && ImGuiNET.ImGui.MenuItem("Copy as text")) Clipboard.Set(NetDecode.ToText(node));
        // Only offered when the filter keeps this packet: entity: knows the entities of MsgEntity
        // events, free text searches the type and summary.
        if (entity is { } id && p.Entities?.Contains(id) == true)
        {
            if (ImGuiNET.ImGui.MenuItem($"With entity {id}")) AddFilter($"entity:{id}");
        }
        else if (value.Length is > 0 and < 60 && NetDecode.Summary(p).Contains(value.Trim('"'), StringComparison.OrdinalIgnoreCase)
                 && ImGuiNET.ImGui.MenuItem($"With \"{value.Trim('"')}\""))
        {
            // Added as plain text: a value like "-1" or "@x" must not turn into a negation or preset.
            AddChip(new NetQuery.Token("", value.Trim('"'), false));
        }
    }

    private static void AddFilter(string token)
    {
        if (NetQuery.ParseToken(token) is { } t) AddChip(t);
    }

    private static void AddChip(NetQuery.Token t)
    {
        if (!_chips.Contains(t)) _chips.Add(t);
        _activeDoc = -1;   // a filter from the inspector: show the list it now filters
    }
}
