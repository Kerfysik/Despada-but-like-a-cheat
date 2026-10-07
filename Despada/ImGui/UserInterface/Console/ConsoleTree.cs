// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Despada.ImGui.UserInterface;

/// <summary>
/// Output shaped as a tree: printed into a session as one row per node, with guide lines, and
/// nodes that have children collapse/expand on click.
/// <code>
/// var t = new ConsoleTree();
/// var input = t.Add(new("input", Tone.Accent), new("  6/6", Tone.Green));
/// input.Add(new("InputManager.KeyDown"), new("  active", Tone.Green));
/// session.Print(t);
/// </code>
/// </summary>
internal sealed class ConsoleTree
{
    public sealed class Node(Span[] spans)
    {
        public Span[]     Spans    { get; } = spans;
        public List<Node> Children { get; } = new();
        public bool       Collapsed { get; set; }

        public Node Add(params Span[] spans)
        {
            var n = new Node(spans);
            Children.Add(n);
            return n;
        }
    }

    public List<Node> Roots { get; } = new();

    public Node Add(params Span[] spans)
    {
        var n = new Node(spans);
        Roots.Add(n);
        return n;
    }
}
