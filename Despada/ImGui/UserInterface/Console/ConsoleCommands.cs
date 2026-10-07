// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Text;
using Despada.Config;

namespace Despada.ImGui.UserInterface;

internal sealed record ConsoleCommand(string Name, string Usage, string Description, Action<ConsoleSession, string[]> Run);

// Despada's own command registry — deliberately separate from Robust's console (nothing is
// registered with the engine).
internal static class ConsoleCommands
{
    private static readonly SortedDictionary<string, ConsoleCommand> _commands = new(StringComparer.Ordinal);

    static ConsoleCommands()
    {
        Register(new ConsoleCommand("help", "help [command]", "list commands or show one", Help));
        Register(new ConsoleCommand("clear", "clear", "clear this session", (s, _) => s.Clear()));
        Register(new ConsoleCommand("echo", "echo <text>", "print text", (s, a) => s.Print(LineKind.Output, string.Join(' ', a))));
        Register(new ConsoleCommand("theme", "theme [dark|light]", "show or switch the theme", Theme_));
        Register(new ConsoleCommand("glass", "glass [0.6..1]", "show or set window opacity", Glass));
        Register(new ConsoleCommand("blur", "blur [0..1]", "show or set background blur", Blur));
        Register(new ConsoleCommand("session", "session [new|close|rename <name>]", "list, open, close or rename sessions", Session));
        Register(new ConsoleCommand("config", "config", "show where settings are stored", (s, _) => s.Print(LineKind.Output, DespadaConfig.FilePath)));
    }

    public static IEnumerable<ConsoleCommand> All => _commands.Values;

    public static void Register(ConsoleCommand cmd) => _commands[cmd.Name] = cmd;

    public static bool Exists(string name) => _commands.ContainsKey(name);

    public static IEnumerable<ConsoleCommand> Matching(string prefix)
        => _commands.Values.Where(c => c.Name.StartsWith(prefix, StringComparison.Ordinal));

    public static void Execute(ConsoleSession session, string line)
    {
        var parts = Tokenize(line);
        if (parts.Count == 0) return;

        if (!_commands.TryGetValue(parts[0], out var cmd))
        {
            session.Print(LineKind.Error, $"✕ unknown command: {parts[0]}");
            return;
        }

        try
        {
            cmd.Run(session, parts.Skip(1).ToArray());
        }
        catch (Exception ex)
        {
            session.Print(LineKind.Error, $"✕ {cmd.Name}: {ex.Message}");
            MarseyLogger.Warn($"[Console] {cmd.Name} failed: {ex}");
        }
    }

    /// <summary>Splits on whitespace; double quotes group words.</summary>
    public static List<string> Tokenize(string line)
    {
        var result  = new List<string>();
        var current = new StringBuilder();
        var quoted  = false;

        foreach (var ch in line)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }

        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    private static void Help(ConsoleSession s, string[] args)
    {
        if (args.Length > 0)
        {
            if (_commands.TryGetValue(args[0], out var one))
                s.Print(LineKind.Output, $"{one.Usage}\n  {one.Description}");
            else
                s.Print(LineKind.Error, $"✕ unknown command: {args[0]}");
            return;
        }

        var width = _commands.Values.Max(c => c.Usage.Length) + 3;
        foreach (var c in _commands.Values)
            s.Print(new Span(c.Name, Tone.Accent), new Span(c.Usage[c.Name.Length..].PadRight(width - c.Name.Length), Tone.Sub),
                new Span(c.Description, Tone.Muted));
    }

    private static void Theme_(ConsoleSession s, string[] args)
    {
        if (args.Length == 0)
        {
            s.Print(LineKind.Output, DespadaConfig.Current.Theme);
            return;
        }

        if (args[0] is not ("dark" or "light"))
        {
            s.Print(LineKind.Error, "✕ expected dark or light");
            return;
        }

        DespadaConfig.Update(c => c.Theme = args[0]);
        s.Print(LineKind.Muted, $"theme → {args[0]}");
    }

    private static void Glass(ConsoleSession s, string[] args)
        => SetFloat(s, args, "glass", 0.6f, 1f, () => DespadaConfig.Current.GlassOpacity, v => DespadaConfig.Update(c => c.GlassOpacity = v));

    private static void Blur(ConsoleSession s, string[] args)
        => SetFloat(s, args, "blur", 0f, 1f, () => DespadaConfig.Current.GlassBlur, v => DespadaConfig.Update(c => c.GlassBlur = v));

    private static void Session(ConsoleSession s, string[] args)
    {
        if (args.Length == 0)
        {
            var list    = ConsolePanel.Sessions;
            var current = ConsolePanel.Current;
            for (var i = 0; i < list.Count; i++)
                s.Print(LineKind.Output, $"{(list[i] == current ? '›' : ' ')} {i + 1}  {list[i].Name}");
            return;
        }

        switch (args[0])
        {
            case "new":
                ConsolePanel.NewSession();
                break;

            case "close":
                if (!ConsolePanel.CloseSession(s))
                    s.Print(LineKind.Error, "✕ session: the last session can't be closed");
                break;

            case "rename":
                // No name goes back to the automatic one.
                ConsolePanel.RenameSession(s, string.Join(' ', args.Skip(1)));
                s.Print(LineKind.Muted, $"session → {s.Name}");
                break;

            default:
                s.Print(LineKind.Error, "✕ session: expected new, close or rename <name>");
                break;
        }
    }

    private static void SetFloat(ConsoleSession s, string[] args, string name, float min, float max,
        Func<float> get, Action<float> set)
    {
        if (args.Length == 0)
        {
            s.Print(LineKind.Output, get().ToString("0.##", CultureInfo.InvariantCulture));
            return;
        }

        if (!float.TryParse(args[0].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            || !float.IsFinite(v) || v < min || v > max)
        {
            s.Print(LineKind.Error, $"✕ {name}: expected a number from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}");
            return;
        }

        set(v);
        s.Print(LineKind.Muted, $"{name} → {v.ToString("0.##", CultureInfo.InvariantCulture)}");
    }
}
