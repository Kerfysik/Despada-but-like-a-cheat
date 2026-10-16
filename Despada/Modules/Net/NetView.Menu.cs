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
        if (!Widgets.BeginMenu("##netmenu", 320f)) return false;
        if (NetCapture.Get(_menuSeq) is not { } p)
            ImGuiNET.ImGui.CloseCurrentPopup();
        else if (_menuNode is not null)
            FieldMenu(p, _menuNode);
        else if (_menuSel is not null)
            SelectionMenu();
        else
            PacketMenu(p);
        Widgets.EndMenu();
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
        Widgets.MenuCaption($"{ps.Count} packets");
        if (Widgets.MenuItem(ps.Count > MaxDocs ? $"open last {MaxDocs} in tabs" : "open in tabs", Icon.Open)) OpenAll(ps);
        Widgets.MenuSeparator();
        if (Widgets.MenuItem("resend all, as they are", Icon.Up, enabled: NetActive.Armed && ps.Any(x => x.Message is not null)))
            foreach (var x in ps.Where(x => x.Message is not null)) NetActive.Resend(x, 1, 0);
        Widgets.MenuSeparator();
        if (Widgets.MenuItem("copy summaries", Icon.Copy))
            Clipboard.Set(string.Join("\n", ps.Select(SummaryLine)));
        if (Widgets.MenuItem("copy fields", Icon.Copy, enabled: ps.Any(x => x.Message is not null)))
            Clipboard.Set(string.Join("\n", ps.Where(x => x.Message is not null)
                .Select(x => $"#{x.Seq}\n" + NetDecode.ToText(NetDecode.Root(x)))));
        if (Widgets.MenuItem("copy hex", Icon.Copy, enabled: ps.Any(x => x.Bytes is not null)))
            Clipboard.Set(string.Join("\n\n", ps.Where(x => x.Bytes is not null).Select(x => $"#{x.Seq}\n" + HexText(x.Bytes!))));
        Widgets.MenuSeparator();

        // "Only" works for one type (type: tokens are all required); hiding works for any number.
        var types = _menuTypes;
        if (types.Count == 1 && Widgets.MenuItem($"only type:{types[0]}", Icon.Funnel)) AddFilter($"type:{types[0]}");
        if (Widgets.MenuItem(types.Count == 1 ? $"hide type:{types[0]}" : $"hide these {types.Count} types", Icon.Funnel))
            foreach (var t in types) AddFilter($"-type:{t}");
        if (Widgets.MenuItem(types.Count == 1 ? $"mute type:{types[0]}" : $"mute these {types.Count} types", dot: Theme.P.Muted))
            foreach (var t in types) MuteType(t);
        foreach (var id in _menuShared)
            if (Widgets.MenuItem($"with entity {id}##e{id}", Icon.Funnel)) AddFilter($"entity:{id}");
        Widgets.MenuSeparator();
        if (Widgets.MenuItem("clear selection", Icon.Cross)) ClearMulti();
    }

    private static void PacketMenu(Packet p)
    {
        var type  = p.Inner ?? p.Type.Name;
        var armed = NetActive.Armed;
        var kept  = p.Message is not null;

        if (Widgets.MenuItem("open in a tab", Icon.Open)) OpenDoc(p.Seq);
        if (Widgets.MenuItem(p.Dir == NetDir.In ? "deliver again" : "resend", p.Dir == NetDir.In ? Icon.Down : Icon.Up, enabled: armed && kept))
            NetActive.Resend(p, 1, 0);
        if (Widgets.MenuItem("edit and send", Icon.Pencil, enabled: armed && kept)) EditDoc(p.Seq);
        if (Widgets.MenuItem("save…", enabled: kept)) SaveDoc(p);
        Widgets.MenuSeparator();

        Widgets.MenuCaption(NetCapture.TypeHint(type) is { Length: > 0 } hint ? $"{type}   {hint}" : type);
        if (Widgets.MenuItem("block", dot: Theme.P.Red)) AddRule(p, RuleAction.Block);
        if (Widgets.MenuItem("freeze", dot: Theme.P.Accent)) AddRule(p, RuleAction.Freeze);
        if (Widgets.MenuItem("dup ×3", dot: Theme.P.Accent2)) AddRule(p, RuleAction.Duplicate);
        Widgets.MenuSeparator();

        if (Widgets.MenuItem("copy summary", Icon.Copy))
            Clipboard.Set($"#{p.Seq} {NetFeature.Time(p)} {(p.Dir == NetDir.In ? "in" : "out")} {p.Type.Name} {p.Size} B {NetDecode.Summary(p)}");
        if (Widgets.MenuItem("copy fields", Icon.Copy, enabled: kept))
            Clipboard.Set(NetDecode.ToText(NetDecode.Root(p)));
        var bytes = p.Bytes;
        if (Widgets.MenuItem("copy hex", Icon.Copy, enabled: bytes is not null))
            Clipboard.Set(string.Join("\n", NetDecode.HexLines(bytes!).Select(l => $"{l.Offset}  {l.Hex} {l.Ascii}")));
        Widgets.MenuSeparator();

        if (Widgets.MenuItem($"hide type:{type}", Icon.Funnel)) AddFilter($"-type:{type}");
        if (Widgets.MenuItem($"mute type:{type}", dot: Theme.P.Muted)) MuteType(type);
        if (Widgets.MenuItem($"only type:{type}", Icon.Funnel)) AddFilter($"type:{type}");
        if (Widgets.MenuItem($"only tick {p.Tick}", Icon.Funnel)) AddFilter($"tick:{p.Tick}");
        foreach (var id in p.Entities ?? [])
            if (Widgets.MenuItem($"with entity {id}##e{id}", Icon.Funnel)) AddFilter($"entity:{id}");
        Widgets.MenuSeparator();

        if (Widgets.MenuItem("find in console"))
        {
            Shell.ShowConsole();
            ConsolePanel.Run($"net.find type:{type} --last 20");
        }
    }

    private static void FieldMenu(Packet p, FieldNode node)
    {
        var value = string.Concat(node.Text.Skip(node.Depth == 0 || node.Name.Length == 0 || node.Name == "…" ? 0 : 1)
            .Select(s => s.Text)).Trim();
        var entity = node.Value?.GetType().Name == "NetEntity" ? NetDecode.Read(node.Value, "Id") as int? : null;

        if (Widgets.MenuItem("copy value", Icon.Copy)) Clipboard.Set(node.Value as string ?? value);
        if (node.CanExpand && Widgets.MenuItem("copy as text", Icon.Copy)) Clipboard.Set(NetDecode.ToText(node));
        if (entity is { } id && p.Entities?.Contains(id) == true)
        {
            if (Widgets.MenuItem($"with entity {id}", Icon.Funnel)) AddFilter($"entity:{id}");
        }
        else if (value.Length is > 0 and < 60 && NetDecode.Summary(p).Contains(value.Trim('"'), StringComparison.OrdinalIgnoreCase)
                 && Widgets.MenuItem($"with \"{value.Trim('"')}\"", Icon.Funnel))
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
        ShowList();   // a filter from the inspector: show the list it now filters
    }
}
