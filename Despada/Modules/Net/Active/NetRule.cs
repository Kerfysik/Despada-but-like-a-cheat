// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Despada.Modules.Net;

internal enum RuleAction
{
    /// <summary>The packet is not sent / not delivered.</summary>
    Block,
    /// <summary>The packet waits in <see cref="NetActive.Frozen"/> until released or dropped.</summary>
    Freeze,
    /// <summary>The packet goes through, then <see cref="NetRule.Count"/> copies follow, <see cref="NetRule.EveryMs"/> apart.</summary>
    Duplicate,
    /// <summary>Fields are changed before the packet goes on (<see cref="NetRule.Modify"/>).</summary>
    Modify,
    /// <summary>The packet goes through but is kept out of the log (noise that would push the rest out). Needs no arm.</summary>
    Mute,
}

/// <summary>"filter → action": what to do with every packet the filter matches. Data only.</summary>
internal sealed class NetRule
{
    /// <summary>The filter, in the Network tab's query language ("↑ type:RequestInteract entity:1873").</summary>
    public string Filter = "";
    public NetQuery Query = new();
    public RuleAction Action = RuleAction.Block;

    /// <summary>
    /// Game state packets (MsgState, MsgStateAck) are left alone by block and freeze rules, or the
    /// client desyncs and the server drops it; "!unsafe" in the filter lifts that.
    /// </summary>
    public bool Unsafe;
    public const string UnsafeWord = "!unsafe";

    public static bool IsStatePacket(Packet p) => p.Type.Name.StartsWith("MsgState", StringComparison.Ordinal);

    /// <summary>The filter names a state packet type without !unsafe: it will never match one.</summary>
    public bool WouldSkipState =>
        !Unsafe && Action is RuleAction.Block or RuleAction.Freeze
        && Query.Tokens.Any(t => t.Key == "type" && !t.Negated && "MsgStateAck".Contains(t.Value, StringComparison.OrdinalIgnoreCase));
    public bool Enabled = true;

    /// <summary>Written to the config and back on the next start (the pin in the rules panel).</summary>
    public bool Pinned;

    /// <summary>Duplicate: how many copies, how far apart.</summary>
    public int Count = 1, EveryMs = 50;

    /// <summary>Modify: "Field=value" pairs, separated by ; or line breaks.</summary>
    public string Modify = "";

    /// <summary>The last modify error on a packet (shown on the rule's row).</summary>
    public string? Error;

    /// <summary>A token the filter can't use: the rule is off rather than matching everything.</summary>
    public bool Invalid => Query.Tokens.Any(t => !NetQuery.IsValid(t));

    public sealed class Dto
    {
        public string Filter { get; set; } = "";
        public string Action { get; set; } = "block";
        public bool Enabled { get; set; } = true;
        public int Count { get; set; } = 1;
        public int EveryMs { get; set; } = 50;
        public string Modify { get; set; } = "";
    }

    public Dto ToDto() => new()
    {
        Filter = Filter, Action = Action.ToString().ToLowerInvariant(), Enabled = Enabled, Count = Count, EveryMs = EveryMs, Modify = Modify,
    };

    public static NetRule FromDto(Dto d)
    {
        var r = new NetRule
        {
            Action = Enum.TryParse<RuleAction>(d.Action, ignoreCase: true, out var a) ? a : RuleAction.Block,
            Enabled = d.Enabled, Count = d.Count, EveryMs = d.EveryMs, Modify = d.Modify, Pinned = true,
        };
        r.SetFilter(d.Filter);
        return r;
    }

    public IEnumerable<(string Path, string Value)> ModifyPairs()
    {
        foreach (var part in Modify.Split(['\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq > 0) yield return (part[..eq].Trim(), part[(eq + 1)..].Trim());
        }
    }

    /// <summary>How many packets it has acted on (bumped from the network threads).</summary>
    public int Hits;

    /// <summary>The filter from tokens as they are (a plain-text chip "-1" must not re-parse as a negation).</summary>
    public void SetTokens(IEnumerable<NetQuery.Token> tokens)
    {
        Query = new NetQuery();
        Query.Tokens.AddRange(tokens);
        Filter = string.Join(' ', Query.Tokens) + (Unsafe ? " " + UnsafeWord : "");
    }

    public void SetFilter(string filter)
    {
        var words = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        Unsafe = words.RemoveAll(w => w.Equals(UnsafeWord, StringComparison.OrdinalIgnoreCase)) > 0;
        Filter = string.Join(' ', words) + (Unsafe ? " " + UnsafeWord : "");
        Query = NetQuery.Parse(string.Join(' ', words));
    }

    public bool Matches(Packet p)
    {
        if (!Enabled || Query.Tokens.Count == 0 || Invalid) return false;
        if (Action is RuleAction.Block or RuleAction.Freeze && !Unsafe && IsStatePacket(p)) return false;
        return Query.Matches(p, NetDecode.Summary);
    }

    public string ActionLabel => Action switch
    {
        RuleAction.Duplicate => $"dup ×{Count}",
        _ => Action.ToString().ToLowerInvariant(),
    };

    public string Detail => Action switch
    {
        RuleAction.Duplicate => $"every {EveryMs} ms",
        RuleAction.Modify    => Modify.Replace('\n', ' '),
        _ => "",
    };
}
