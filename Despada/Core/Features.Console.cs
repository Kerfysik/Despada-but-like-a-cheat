// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Despada.ImGui.UserInterface;

namespace Despada.Core;

// Console side of the core: feature switches and diagnostics (bindings, hooks).
internal static partial class Features
{
    private static void RegisterCommands()
    {
        ConsoleCommands.Register(new ConsoleCommand("features", "features", "list features and their state", ListFeatures));
        ConsoleCommands.Register(new ConsoleCommand("feature", "feature <id> [on|off]", "show or switch a feature", FeatureCmd));
        ConsoleCommands.Register(new ConsoleCommand("bind", "bind [all]", "engine bindings (missing ones, or all)", BindCmd));
        ConsoleCommands.Register(new ConsoleCommand("hooks", "hooks [owner]", "installed engine hooks", HooksCmd));
    }

    // A feature's commands only run while it is on; otherwise they say why not.
    private static void RegisterFeatureCommand(Feature f, ConsoleCommand cmd)
    {
        if (ConsoleCommands.Exists(cmd.Name))
        {
            Log.Error(f.Id, $"command '{cmd.Name}' already exists — not registered");
            return;
        }
        ConsoleCommands.Register(cmd with
        {
            Run = (s, a) =>
            {
                if (f.State == FeatureState.On) cmd.Run(s, a);
                else s.Print(LineKind.Error, $"✕ {f.Title} is {Describe(f)}");
            },
        });
    }

    public static string Describe(Feature f) => f.State switch
    {
        FeatureState.On          => "on",
        FeatureState.Off         => "off",
        FeatureState.Unavailable => $"unavailable ({f.Reason})",
        _                        => $"crashed ({f.Reason})",
    };

    private static Tone StateTone(FeatureState state) => state switch
    {
        FeatureState.On          => Tone.Green,
        FeatureState.Off         => Tone.Muted,
        FeatureState.Unavailable => Tone.Yellow,
        _                        => Tone.Red,
    };

    // "3/3" green when complete, yellow when some are missing.
    private static Span Count(int ok, int total) => new($"{ok}/{total}", ok == total ? Tone.Green : Tone.Yellow);

    private static void ListFeatures(ConsoleSession s, string[] _)
    {
        if (_all.Count == 0)
        {
            s.Print(LineKind.Muted, "no features yet");
            return;
        }

        var w = _all.Max(f => f.Id.Length) + 2;
        var t = new ConsoleTree();
        foreach (var f in _all)
        {
            var node = t.Add(new Span(f.Id.PadRight(w), Tone.Accent), new Span(f.Title + "  "),
                new Span(f.State.ToString().ToLowerInvariant(), StateTone(f.State)));
            if (f.Reason is not null) node.Add(new Span(f.Reason, StateTone(f.State)));
            foreach (var h in Hooks.Of(f.Id))
                node.Add(new Span(h.Target + "  ", Tone.Sub), HookState(h));
            node.Collapsed = node.Children.Count > 0 && f.State == FeatureState.Off;
        }
        s.Print(t);
    }

    private static void FeatureCmd(ConsoleSession s, string[] a)
    {
        var f = a.Length > 0 ? Find(a[0]) : null;
        if (f is null)
        {
            s.Print(LineKind.Error, a.Length == 0 ? "✕ expected a feature id (see features)" : $"✕ no feature '{a[0]}'");
            return;
        }

        if (a.Length > 1)
        {
            if (a[1] is not ("on" or "off"))
            {
                s.Print(LineKind.Error, "✕ expected on or off");
                return;
            }
            Request(f, a[1] == "on");
            s.Print(new Span(f.Title, Tone.Accent), new Span(" → ", Tone.Muted), new Span(a[1], a[1] == "on" ? Tone.Green : Tone.Muted));
            return;
        }
        s.Print(new Span(f.Title + "  ", Tone.Accent), new Span(Describe(f), StateTone(f.State)));
    }

    // Bindings grouped by type: "Clyde.Render" goes under "Clyde". Missing only, unless "all".
    private static void BindCmd(ConsoleSession s, string[] a)
    {
        var all  = Engine.All;
        var full = a.Length > 0 && a[0] == "all";
        var ok   = all.Count(b => b.Ok);

        s.Print(new Span("engine bindings  ", Tone.Sub), Count(ok, all.Count));
        if (!full && ok == all.Count) return;

        var t = new ConsoleTree();
        foreach (var group in all.GroupBy(b => b.Name.Contains('.') ? b.Name[..b.Name.LastIndexOf('.')] : b.Name))
        {
            var items = group.Where(b => full || !b.Ok).ToList();
            if (items.Count == 0) continue;

            var node = t.Add(new Span(group.Key + "  ", Tone.Accent), Count(group.Count(b => b.Ok), group.Count()));
            foreach (var b in items)
            {
                var member = b.Name != group.Key ? b.Name[(group.Key.Length + 1)..]
                           : b is Bind.ManagerRef ? "manager" : "type";
                node.Add(b.Ok
                    ? [new Span(member + "  "), new Span("ok", Tone.Green)]
                    : [new Span(member + "  "), new Span(b.Error ?? "missing", Tone.Red)]);
            }
        }
        s.Print(t);
    }

    private static Span HookState(Hooks.Hook h) => h.State switch
    {
        Hooks.State.Active => new Span("active", Tone.Green),
        Hooks.State.Failed => new Span($"failed — {h.Error}", Tone.Red),
        _                  => new Span("removed", Tone.Muted),
    };

    // Hooks grouped by owner.
    private static void HooksCmd(ConsoleSession s, string[] a)
    {
        var hooks = Hooks.All.Where(h => a.Length == 0 || h.Owner == a[0]).ToList();
        if (hooks.Count == 0)
        {
            s.Print(LineKind.Muted, "no hooks");
            return;
        }

        var t = new ConsoleTree();
        foreach (var group in hooks.GroupBy(h => h.Owner))
        {
            var w    = group.Max(h => h.Target.Length) + 2;
            var node = t.Add(new Span(group.Key + "  ", Tone.Accent),
                Count(group.Count(h => h.State == Hooks.State.Active), group.Count()));
            foreach (var h in group)
                node.Add(new Span(h.Target.PadRight(w)), HookState(h));
        }
        s.Print(t);
    }
}
