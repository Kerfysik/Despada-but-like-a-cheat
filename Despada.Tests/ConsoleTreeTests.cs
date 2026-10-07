// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Despada.ImGui.UserInterface;
using Xunit;

namespace Despada.Tests;

public class ConsoleTreeTests
{
    // a
    // ├─ a1
    // │  └─ a1x
    // └─ a2
    // b
    private static ConsoleSession Printed(out int start)
    {
        var s = new ConsoleSession(1);
        start = s.Lines.Count;
        var t = new ConsoleTree();
        var a = t.Add(new Span("a"));
        a.Add(new Span("a1")).Add(new Span("a1x"));
        a.Add(new Span("a2"));
        t.Add(new Span("b"));
        s.Print(t);
        return s;
    }

    [Fact]
    public void RowsCarryDepthGuidesAndOwnership()
    {
        var s = Printed(out var i);
        var rows = s.Lines.Skip(i).ToList();

        Assert.Equal(["a", "a1", "a1x", "a2", "b"], rows.Select(r => r.Text));
        Assert.Equal([0, 1, 2, 1, 0], rows.Select(r => r.Depth));
        Assert.Equal([3, 1, 0, 0, 0], rows.Select(r => r.Descendants));
        Assert.Equal([false, false, true, true, true], rows.Select(r => r.Last));

        // a1 isn't the last child of a, so its guide (level 0) runs through a1x.
        Assert.Equal(1u, rows[2].Guides & 1u);
        Assert.Equal(0u, rows[3].Guides);
    }

    [Fact]
    public void CollapsingHidesTheSubtree()
    {
        var s = Printed(out var i);
        var before = s.Visible.Count;

        s.Toggle(i); // collapse "a"
        Assert.Equal(before - 3, s.Visible.Count);
        Assert.DoesNotContain(i + 1, s.Visible);

        s.Toggle(i);
        Assert.Equal(before, s.Visible.Count);
    }

    [Fact]
    public void RichLineKeepsPlainText()
    {
        var s = new ConsoleSession(2);
        s.Print(new Span("hooks ", Tone.Accent), new Span("9/9", Tone.Green));
        Assert.Equal("hooks 9/9", s.Lines[^1].Text);
        Assert.NotNull(s.Lines[^1].Spans);
    }
}
