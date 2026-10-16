// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;

namespace Despada.Modules.Net;

/// <summary>
/// The Network filter: space-separated tokens, all of which must match.
/// <c>type:MsgEntity</c> (type or inner event, substring), <c>dir:in|out</c>, <c>size:&gt;100</c>,
/// <c>tick:88231</c> / <c>tick:&gt;88000</c>, <c>since:-30s</c>, <c>-key:value</c> negates,
/// <c>@quiet</c>-style presets expand to tokens, anything else matches the type or summary text.
/// </summary>
internal sealed class NetQuery
{
    public sealed record Token(string Key, string Value, bool Negated)
    {
        public override string ToString() => (Negated ? "-" : "") + (Key.Length == 0 ? Value : Key == "@" ? "@" + Value : $"{Key}:{Value}");
    }

    public static readonly string[] Keys = ["type", "dir", "entity", "size", "tick", "since"];

    public static readonly (string Name, string Description, string Expansion)[] Presets =
    [
        ("quiet", "hide MsgState, MsgStateAck",   "-type:MsgState -type:MsgStateAck"),
        ("input", "FullInputCmdMessage only",      "type:FullInputCmdMessage"),
        ("ui",    "bound UI messages",             "type:BUI."),
        ("chat",  "chat messages",                 "type:MsgChat"),
        ("out",   "only what the client sends",    "dir:out"),
    ];

    public List<Token> Tokens { get; } = new();

    public bool IsEmpty => Tokens.Count == 0;

    // Each top-level token as a group of ANDed tokens (a preset is one group, negated as a whole).
    private List<(List<Token> All, bool Negated)>? _groups;

    public static NetQuery Parse(string text)
    {
        var q = new NetQuery();
        foreach (var raw in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (ParseToken(raw) is { } t) q.Tokens.Add(t);
        return q;
    }

    public static Token? ParseToken(string raw)
    {
        var neg = raw.StartsWith('-') && raw.Length > 1;
        if (neg) raw = raw[1..];
        if (raw.StartsWith('@')) return raw.Length > 1 ? new Token("@", raw[1..], neg) : null;

        var colon = raw.IndexOf(':');
        if (colon > 0 && Keys.Contains(raw[..colon]))
            return colon < raw.Length - 1 ? new Token(raw[..colon], raw[(colon + 1)..], neg) : null;
        return new Token("", raw, neg);
    }

    private List<(List<Token> All, bool Negated)> Groups()
    {
        if (_groups is not null) return _groups;
        _groups = new();
        foreach (var t in Tokens)
        {
            if (!IsValid(t)) continue; // shown red in the bar, ignored here
            if (t.Key != "@")
            {
                _groups.Add(([t with { Negated = false }], t.Negated));
                continue;
            }
            var preset = Presets.FirstOrDefault(p => p.Name == t.Value);
            // "type:A type:B" in a preset means either type; everything else must all match.
            var inner = Parse(preset.Expansion).Tokens;
            _groups.Add((inner, t.Negated));
        }
        return _groups;
    }

    public bool Matches(Packet p, Func<Packet, string> summary)
    {
        foreach (var (all, negated) in Groups())
        {
            bool hit;
            if (all.Count > 1 && all.All(t => t.Key == "type" && !t.Negated))
                hit = all.Any(t => Test(t, p, summary));          // preset listing types: any of them
            else
                hit = all.All(t => Test(t, p, summary) != t.Negated);
            if (hit == negated) return false;
        }
        return true;
    }

    public static bool IsValid(Token t) => t.Key switch
    {
        "@"     => Presets.Any(p => p.Name == t.Value),
        "dir"    => t.Value is "in" or "out" or "↓" or "↑",
        "entity" => int.TryParse(t.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        "size"  => ValidSpec(t.Value, ParseSize),
        "tick"  => ValidSpec(t.Value, ParseLong),
        "since" => ParseAge(t.Value) is not null,
        _       => true,
    };

    private static bool ValidSpec(string spec, Func<string, long?> parse)
    {
        var range = spec.Split("..");
        if (range.Length == 2) return parse(range[0]) is not null && parse(range[1]) is not null;
        return parse(spec.TrimStart('>', '<', '=')) is not null;
    }

    private static long? ParseLong(string s)
        => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static bool Test(Token t, Packet p, Func<Packet, string> summary)
    {
        switch (t.Key)
        {
            case "type":
                // The message type, or the event inside a MsgEntity; "type:=X" is the exact name.
                if (t.Value.StartsWith('='))
                {
                    var exact = t.Value.AsSpan(1);
                    return exact.Equals(p.Type.Name, StringComparison.OrdinalIgnoreCase)
                        || (p.Inner is { } inner && exact.Equals(inner, StringComparison.OrdinalIgnoreCase));
                }
                return p.Type.Name.Contains(t.Value, StringComparison.OrdinalIgnoreCase)
                    || p.Inner?.Contains(t.Value, StringComparison.OrdinalIgnoreCase) == true;
            case "entity":
                return p.Entities is { } ids && int.TryParse(t.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
                       && ids.Contains(id);
            case "dir":
                return t.Value is "in" or "↓" ? p.Dir == NetDir.In
                     : t.Value is "out" or "↑" ? p.Dir == NetDir.Out
                     : true;
            case "size":
                return Compare(p.Size, t.Value, ParseSize);
            case "tick":
                return Compare(p.Tick, t.Value, ParseLong);
            case "since":
                return ParseAge(t.Value) is not { } age
                    || Stopwatch.GetElapsedTime(p.Stamp) <= age;
            default:
                return p.Type.Name.Contains(t.Value, StringComparison.OrdinalIgnoreCase)
                    || summary(p).Contains(t.Value, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ">100", "<1KB", "64", "88000..88100"
    private static bool Compare(long actual, string spec, Func<string, long?> parse)
    {
        var range = spec.Split("..");
        if (range.Length == 2)
            return parse(range[0]) is { } lo && parse(range[1]) is { } hi && actual >= lo && actual <= hi;
        if (spec.StartsWith(">=") && parse(spec[2..]) is { } a) return actual >= a;
        if (spec.StartsWith("<=") && parse(spec[2..]) is { } b) return actual <= b;
        if (spec.StartsWith('>') && parse(spec[1..]) is { } c) return actual > c;
        if (spec.StartsWith('<') && parse(spec[1..]) is { } d) return actual < d;
        return parse(spec) is not { } e || actual == e;
    }

    internal static long? ParseSize(string s)
    {
        s = s.Trim().ToUpperInvariant();
        var mul = 1L;
        if (s.EndsWith("KB")) { mul = 1024; s = s[..^2]; }
        else if (s.EndsWith("MB")) { mul = 1024 * 1024; s = s[..^2]; }
        else if (s.EndsWith('B')) s = s[..^1];
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (long)(v * mul) : null;
    }

    // "-30s", "30s", "5m", "1h"; a finite, non-negative span that TimeSpan can hold, else invalid.
    internal static TimeSpan? ParseAge(string s)
    {
        s = s.TrimStart('-');
        if (s.Length < 2) return null;
        if (!double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return null;
        var seconds = s[^1] switch { 's' => v, 'm' => v * 60, 'h' => v * 3600, _ => double.NaN };
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > TimeSpan.MaxValue.TotalSeconds) return null;
        return TimeSpan.FromSeconds(seconds);
    }

    // Types starting with the text first, then those containing it.
    private static IEnumerable<string> MatchTypes(IEnumerable<string> types, string text)
    {
        var all = types.ToList();
        return all.Where(t => t.StartsWith(text, StringComparison.OrdinalIgnoreCase))
            .Concat(all.Where(t => !t.StartsWith(text, StringComparison.OrdinalIgnoreCase) && t.Contains(text, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Completions for the word being typed: keys and presets for a bare word, values after a key
    /// (known types from <paramref name="types"/>, in/out, …). Each is (text to insert, description).
    /// </summary>
    /// <param name="entities">Entity ids seen in the capture, newest first, with a hint each.</param>
    public static List<(string Insert, string Hint)> Suggest(string word, IEnumerable<string> types,
        IEnumerable<(int Id, string Hint)>? entities = null, Func<string, string>? typeHint = null)
    {
        entities ??= [];
        typeHint ??= _ => "";
        var neg  = word.StartsWith('-') ? "-" : "";
        var body = neg.Length > 0 ? word[1..] : word;
        var list = new List<(string, string)>();

        if (body.StartsWith('@'))
        {
            foreach (var p in Presets.Where(p => p.Name.StartsWith(body[1..], StringComparison.OrdinalIgnoreCase)))
                list.Add(($"{neg}@{p.Name}", p.Description));
            return list;
        }

        var colon = body.IndexOf(':');
        if (colon < 0)
        {
            foreach (var k in Keys.Where(k => k.StartsWith(body, StringComparison.OrdinalIgnoreCase)))
                list.Add(($"{neg}{k}:", KeyHint(k)));
            if (body.Length == 0 || "@".StartsWith(body))
                list.Add((neg + "@", "presets"));
            if (body.Length >= 2)
            {
                // A bare word: the types and entities it could mean ("MsgStateAc" → type:MsgStateAck).
                foreach (var t in MatchTypes(types, body).Take(6))
                    list.Add(($"{neg}type:{t}", typeHint(t) is { Length: > 0 } h ? h : "type"));
                if (body.All(char.IsAsciiDigit))
                    foreach (var (id, hint) in entities.Where(e => e.Id.ToString(CultureInfo.InvariantCulture).StartsWith(body, StringComparison.Ordinal)).Take(6))
                        list.Add(($"{neg}entity:{id}", hint));
            }
            return list;
        }

        var key = body[..colon];
        var val = body[(colon + 1)..];
        IEnumerable<(string, string)> values = key switch
        {
            "type"   => MatchTypes(types, val).Take(8).Select(t => (t, typeHint(t))),
            "entity" => entities.Where(e => e.Id.ToString(CultureInfo.InvariantCulture).StartsWith(val, StringComparison.Ordinal)).Take(8).Select(e => (e.Id.ToString(), e.Hint)),
            "dir"   => new[] { ("in", "↓ received"), ("out", "↑ sent") }.Where(v => v.Item1.StartsWith(val)),
            "size"  => new[] { (">1KB", "bigger than 1 KB"), ("<100", "smaller than 100 B") }.Where(v => v.Item1.StartsWith(val)),
            "since" => new[] { ("-10s", "last 10 seconds"), ("-1m", "last minute"), ("-5m", "last 5 minutes") }.Where(v => v.Item1.StartsWith(val)),
            _       => [],
        };
        foreach (var (v, hint) in values)
            list.Add(($"{neg}{key}:{v}", hint));
        return list;
    }

    private static string KeyHint(string key) => key switch
    {
        "type"  => "message or event type",
        "dir"   => "in / out",
        "entity"=> "NetEntity id",
        "size"  => "> < bytes, KB",
        "tick"  => "tick, >tick, a..b",
        "since" => "-30s, -5m",
        _       => "",
    };
}
