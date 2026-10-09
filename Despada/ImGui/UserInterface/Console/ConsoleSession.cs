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

/// <summary>Colour role of a piece of output; mapped to the theme when drawn.</summary>
internal enum Tone { Fg, Sub, Muted, Accent, Accent2, Link, Green, Yellow, Red }

/// <summary>A coloured run of text in a console line.</summary>
internal readonly record struct Span(string Text, Tone Tone = Tone.Fg);

internal sealed class ConsoleLine(LineKind kind, string text)
{
    private static int _nextId;

    /// <summary>Stable id (ImGui ID for clickable rows; the row's index shifts when old output is trimmed).</summary>
    public int Id { get; } = Interlocked.Increment(ref _nextId);

    public LineKind Kind { get; } = kind;
    public string   Text { get; } = text;

    /// <summary>Coloured runs drawn instead of <see cref="Text"/> (tree rows, rich output).</summary>
    public Span[]? Spans;

    // Tree rows (see ConsoleTree): level, which ancestor guide lines pass through this row
    // (bit l = level l, levels ≥ 32 get no guides), whether it is the last child, and how many
    // following lines it owns.
    public int  Depth;
    public uint Guides;
    public bool Last;
    public int  Descendants;
    public bool Collapsed;
}

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

    // Indices of the lines not hidden inside a collapsed tree node; rebuilt when output changes.
    private readonly List<int> _visible = new();
    private bool _visibleDirty = true;

    public IReadOnlyList<int> Visible
    {
        get
        {
            if (!_visibleDirty) return _visible;
            _visible.Clear();
            for (var i = 0; i < Lines.Count; i++)
            {
                _visible.Add(i);
                if (Lines[i].Collapsed) i += Lines[i].Descendants;
            }
            _visibleDirty = false;
            return _visible;
        }
    }

    public void Toggle(int line)
    {
        Lines[line].Collapsed = !Lines[line].Collapsed;
        _visibleDirty = true;
    }

    public void Clear()
    {
        Lines.Clear();
        _visibleDirty = true;
    }
    public List<string> History { get; } = new();

    /// <summary>Index into <see cref="History"/> while browsing with ↑/↓; -1 = editing a new line.</summary>
    public int HistoryPos = -1;

    /// <summary>The unsent line, kept while browsing history so ↓ past the newest entry restores it.</summary>
    public string Draft = "";

    public string Input = "";
    public bool ScrollToBottom;
    /// <summary>Output arrived since the session was last shown (a dot on its tab).</summary>
    public bool Unread;

    public void Print(LineKind kind, string text)
    {
        foreach (var line in text.Split('\n'))
            Lines.Add(new ConsoleLine(kind, line.TrimEnd('\r')));
        Added();
    }

    /// <summary>One line made of coloured runs.</summary>
    public void Print(params Span[] spans)
    {
        Lines.Add(new ConsoleLine(LineKind.Output, string.Concat(spans.Select(x => x.Text))) { Spans = spans });
        Added();
    }

    /// <summary>A collapsible tree, one row per node.</summary>
    public void Print(ConsoleTree tree)
    {
        for (var i = 0; i < tree.Roots.Count; i++)
            Emit(tree.Roots[i], 0, 0, i == tree.Roots.Count - 1);
        Added();
    }

    private void Emit(ConsoleTree.Node node, int depth, uint guides, bool last)
    {
        var line = new ConsoleLine(LineKind.Output, string.Concat(node.Spans.Select(x => x.Text)))
        {
            Spans = node.Spans, Depth = depth, Guides = guides, Last = last, Collapsed = node.Collapsed,
        };
        Lines.Add(line);
        var first = Lines.Count;

        // A node that has siblings below keeps its connector line running past its children.
        var childGuides = depth is >= 1 and <= 32 && !last ? guides | (1u << (depth - 1)) : guides;
        for (var i = 0; i < node.Children.Count; i++)
            Emit(node.Children[i], depth + 1, childGuides, i == node.Children.Count - 1);

        line.Descendants = Lines.Count - first;
    }

    private void Added()
    {
        if (Lines.Count > MaxLines)
        {
            Lines.RemoveRange(0, Lines.Count - MaxLines);
            // Don't keep the tail of a tree whose root was trimmed away.
            var orphans = 0;
            while (orphans < Lines.Count && Lines[orphans].Depth > 0) orphans++;
            Lines.RemoveRange(0, orphans);
        }
        _visibleDirty = true;
        ScrollToBottom = true;
        Unread = true;
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
