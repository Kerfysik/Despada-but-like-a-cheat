// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using Despada.ImGui.UserInterface;

namespace Despada.Modules.Net;

internal static class NetCommands
{
    public static IEnumerable<ConsoleCommand> All(NetFeature f) =>
    [
        new("net.find", "net.find <filter…> [--last N]", "find captured packets (same filter as the Network tab)", Find),
        new("net.stats", "net.stats", "message types by count and bytes", Stats),
        new("net.clear", "net.clear", "drop all captured packets", (s, _) => { NetCapture.Clear(); s.Print(LineKind.Muted, "network log cleared"); }),
        new("net.pause", "net.pause [on|off]", "stop or resume capturing", Pause),
        new("net.buffer", "net.buffer [packets]", "show or set how many packets are kept", (s, a) => Buffer(f, s, a)),
        new("net.types", "net.types [text]", "every message, event and BUI type the game can send", Types),
    ];

    private static void Types(ConsoleSession s, string[] args)
    {
        var text  = string.Join(' ', args);
        var types = NetTypes.All;
        if (types.Count == 0)
        {
            s.Print(LineKind.Muted, NetTypes.Failed ? "reading the types failed (see the log)" : "types are still being read, try again in a moment");
            return;
        }
        var all = types.Where(t => t.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (all.Count == 0)
        {
            s.Print(LineKind.Muted, $"no types match '{text}'");
            return;
        }
        // Without a filter only the counts: thousands of lines would push the history out.
        const int MaxShown = 200;
        var t = new ConsoleTree();
        foreach (var (kind, caption) in new[] { (NetTypes.Kind.Message, "messages"), (NetTypes.Kind.Event, "events"), (NetTypes.Kind.Bui, "BUI messages") })
        {
            var group = all.Where(x => x.Kind == kind).ToList();
            if (group.Count == 0) continue;
            var node = t.Add([new Span(caption, Tone.Accent), new Span($"  {group.Count}", Tone.Muted)]);
            if (text.Length == 0) continue;
            foreach (var x in group.Take(MaxShown)) node.Add([new Span(x.Name)]);
            if (group.Count > MaxShown) node.Add([new Span($"… {group.Count - MaxShown} more", Tone.Muted)]);
        }
        s.Print(t);
        s.Print(new Span($"{all.Count} of {NetTypes.All.Count}", Tone.Muted));
    }

    private static void Find(ConsoleSession s, string[] args)
    {
        var last = 20;
        var words = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--last")
            {
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out var n)) { last = Math.Clamp(n, 1, 500); i++; }
            }
            else words.Add(args[i]);
        }

        var query = NetQuery.Parse(string.Join(' ', words));
        var hits  = new List<Packet>();
        var all   = NetCapture.Snapshot();
        for (var i = all.Count - 1; i >= 0 && hits.Count < last; i--)
            if (query.Matches(all[i], NetDecode.Summary)) hits.Add(all[i]);
        hits.Reverse();

        if (hits.Count == 0)
        {
            s.Print(LineKind.Muted, "no packets match");
            return;
        }

        var t = new ConsoleTree();
        foreach (var p in hits)
        {
            var node = t.Add(Header(p));
            node.Collapsed = true;
            if (p.Message is null) continue;
            foreach (var c in NetDecode.Children(NetDecode.Root(p)))
                node.Add(c.Text);
        }
        s.Print(t);
        s.Print(new Span($"{hits.Count} shown", Tone.Muted));
    }

    public static Span[] Header(Packet p) =>
    [
        new($"#{p.Seq}  ", Tone.Accent),
        new(NetFeature.Time(p) + "  ", Tone.Muted),
        p.Dir == NetDir.In ? new("↓ ", Tone.Accent) : new("↑ ", Tone.Accent2),
        new(p.Type.Name + "  "),
        new(NetFeature.Bytes(p.Size) + "  ", Tone.Sub),
        new(NetDecode.Summary(p), Tone.Muted),
    ];

    private static void Stats(ConsoleSession s, string[] _)
    {
        var all = NetCapture.Snapshot();
        if (all.Count == 0)
        {
            s.Print(LineKind.Muted, "nothing captured yet");
            return;
        }

        var t = new ConsoleTree();
        // MsgEntity is split by the event inside; that's what tells entity traffic apart.
        var groups = all.GroupBy(p => (p.Dir, Name: p.Inner ?? p.Type.Name)).OrderByDescending(g => g.Sum(p => (long)p.Size)).ToList();
        var w = groups.Max(g => g.Key.Name.Length) + 2;
        foreach (var g in groups)
        {
            t.Add(g.Key.Dir == NetDir.In ? new Span("↓ ", Tone.Accent) : new Span("↑ ", Tone.Accent2),
                new Span(g.Key.Name.PadRight(w)),
                new Span(g.Count().ToString("N0", CultureInfo.InvariantCulture).PadLeft(8) + "  ", Tone.Sub),
                new Span(NetFeature.Bytes(g.Sum(p => (double)p.Size)), Tone.Muted));
        }
        s.Print(t);
        s.Print(new Span($"{all.Count:N0} packets · ", Tone.Muted), new Span($"↓ {NetFeature.Bytes(NetFeature.RateIn)}/s", Tone.Accent),
            new Span("  "), new Span($"↑ {NetFeature.Bytes(NetFeature.RateOut)}/s", Tone.Accent2));
    }

    private static void Pause(ConsoleSession s, string[] a)
    {
        if (a.Length > 0 && a[0] is not ("on" or "off"))
        {
            s.Print(LineKind.Error, "✕ expected on or off");
            return;
        }
        NetCapture.Paused = a.Length == 0 ? !NetCapture.Paused : a[0] == "on";
        s.Print(LineKind.Muted, NetCapture.Paused ? "capture paused" : "capture resumed");
    }

    private static void Buffer(NetFeature f, ConsoleSession s, string[] a)
    {
        if (a.Length > 0 && int.TryParse(a[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            f.SetBuffer(n);
        s.Print(new Span("buffer  ", Tone.Sub), new Span($"{NetCapture.Count:N0} / {NetCapture.Capacity:N0}"));
    }
}
