// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Despada.ImGui.UserInterface;

internal enum LineKind
{
    Command,
    Output,
    Muted,
    Warn,
    Error,
}

internal readonly record struct ConsoleLine(LineKind Kind, string Text);

internal sealed class ConsoleSession(int number)
{
    private const int MaxLines   = 5000;
    private const int MaxHistory = 200;

    /// <summary>Lowest number free when the session was opened; gives the automatic name.</summary>
    public int Number { get; } = number;

    /// <summary>Name set by the user (pill double-click or <c>session rename</c>); null = automatic.</summary>
    public string? CustomName;

    public string Name => CustomName ?? (Number == 1 ? "main" : $"session {Number}");
    public List<ConsoleLine> Lines { get; } = new();
    public List<string> History { get; } = new();

    /// <summary>Index into <see cref="History"/> while browsing with ↑/↓; -1 = editing a new line.</summary>
    public int HistoryPos = -1;

    /// <summary>The unsent line, kept while browsing history so ↓ past the newest entry restores it.</summary>
    public string Draft = "";

    public string Input = "";
    public bool ScrollToBottom;

    public void Print(LineKind kind, string text)
    {
        foreach (var line in text.Split('\n'))
            Lines.Add(new ConsoleLine(kind, line.TrimEnd('\r')));

        if (Lines.Count > MaxLines)
            Lines.RemoveRange(0, Lines.Count - MaxLines);

        ScrollToBottom = true;
    }

    public void Submit(string line)
    {
        line = line.Trim();
        HistoryPos = -1;
        if (line.Length == 0) return;

        if (History.Count == 0 || History[^1] != line)
        {
            History.Add(line);
            if (History.Count > MaxHistory)
                History.RemoveAt(0);
        }

        Print(LineKind.Command, line);
        ConsoleCommands.Execute(this, line);
    }
}
