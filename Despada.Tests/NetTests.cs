// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Despada.ImGui.UserInterface;
using Despada.Modules.Net;
using Xunit;

namespace Despada.Tests;

// Stand-ins named like the engine types the decoder recognises.
public readonly struct NetEntity(int id) { public readonly int Id = id; }
public sealed class RequestInteractEvent { public NetEntity Target = new(1873); public bool AltInteract; public string Hand = "right"; }
public sealed class MsgChatMessage { public string Text = "who took the jaws?"; public int[] Recipients = [1, 2, 3]; }
public sealed class RequestPerformActionEvent { public NetEntity? EntityTarget = new NetEntity(77); public NetEntity? None; }
public sealed class MsgLobbyInfo { public string TextBlob = "Welcome to [color=white]Space Station 14![/color]\nRound: #65\nPlayers: 1"; }
public sealed class MsgState { }
public sealed class MsgStateAck { }
public sealed class MsgEntity { }

public class NetQueryTests
{
    private static Packet P(string type, NetDir dir, int size = 64, uint tick = 100, string summary = "")
        => new() { Type = Type.GetType("Despada.Tests." + type) ?? typeof(object), Dir = dir, Size = size, Tick = tick, Summary = summary };

    private static bool M(string q, Packet p) => NetQuery.Parse(q).Matches(p, x => x.Summary ?? "");

    [Fact]
    public void Type_Dir_Size_Tick()
    {
        var chat = P("MsgChatMessage", NetDir.In, size: 212, tick: 88231);
        Assert.True(M("type:chat", chat));
        Assert.True(M("dir:in", chat));
        Assert.False(M("dir:out", chat));
        Assert.True(M("size:>200 size:<1KB", chat));
        Assert.False(M("size:>1KB", chat));
        Assert.True(M("tick:88000..88300", chat));
        Assert.False(M("tick:>88300", chat));
    }

    [Fact]
    public void NegationPresetsAndFreeText()
    {
        var state = P("MsgState", NetDir.In, summary: "tick 88231 · 41 entities");
        var ev    = P("RequestInteractEvent", NetDir.Out, summary: "RequestInteractEvent → 1873");
        Assert.False(M("-type:MsgState", state));
        Assert.False(M("@quiet", state));
        Assert.True(M("@quiet", ev));
        Assert.True(M("type:RequestInteract", ev));   // the inner event counts as the type
        Assert.True(M("1873", ev));                    // free text searches the summary
        Assert.True(M("@out", ev));
        Assert.False(M("-@out", ev));
    }

    [Fact]
    public void NegatedPresetIsOneGroup()
    {
        var state = P("MsgState", NetDir.In);
        var ack   = new Packet { Type = typeof(object), Dir = NetDir.Out, Summary = "" };
        ack.Type = typeof(MsgStateAck);
        var ev    = P("RequestInteractEvent", NetDir.Out);
        Assert.True(M("-@quiet", state));   // only the noise
        Assert.True(M("-@quiet", ack));
        Assert.False(M("-@quiet", ev));
    }

    [Fact]
    public void TypeMatchesTheInnerEvent_NotTheSummaryText()
    {
        var entity = new Packet { Type = typeof(MsgEntity), Dir = NetDir.Out, Inner = "RequestInteractEvent", Summary = "RequestInteractEvent → 1873" };
        var state  = P("MsgState", NetDir.In, summary: "tick 88231 · 41 entities");
        Assert.True(M("type:RequestInteract", entity));
        Assert.False(M("type:tick", state));
        Assert.True(M("tick", state));        // free text still searches the summary
        var bui = new Packet { Type = typeof(MsgEntity), Inner = "BUI.ApcToggleMainBreakerMessage", Summary = "" };
        Assert.True(M("@ui", bui));
        Assert.False(M("@ui", entity));
    }

    [Fact]
    public void EntityFilter()
    {
        var ev = P("RequestInteractEvent", NetDir.Out);
        ev.Entities = [1873, 22];
        Assert.True(M("entity:1873", ev));
        Assert.True(M("entity:22", ev));
        Assert.False(M("entity:5", ev));
        Assert.False(M("entity:1873", P("MsgState", NetDir.In)));   // no entities at all
        Assert.True(M("-entity:5", ev));
        Assert.False(NetQuery.IsValid(new NetQuery.Token("entity", "abc", false)));
    }

    [Fact]
    public void InvalidValuesAreIgnored()
    {
        var chat = P("MsgChatMessage", NetDir.In, size: 212);
        Assert.False(NetQuery.IsValid(NetQuery.ParseToken("size:>1k")!));
        Assert.True(M("size:>1k", chat));     // ignored, not "matches nothing"
        Assert.False(NetQuery.IsValid(NetQuery.ParseToken("@nope")!));
        Assert.False(NetQuery.IsValid(NetQuery.ParseToken("dir:sideways")!));
    }

    [Fact]
    public void Parsing()
    {
        Assert.Equal("type:MsgEntity", NetQuery.ParseToken("type:MsgEntity")!.ToString());
        Assert.Equal("-type:MsgState", NetQuery.ParseToken("-type:MsgState")!.ToString());
        Assert.Equal("@quiet", NetQuery.ParseToken("@quiet")!.ToString());
        Assert.Null(NetQuery.ParseToken("type:"));          // unfinished key
        Assert.Equal("", NetQuery.ParseToken("foo:bar")!.Key); // unknown key = free text
        Assert.Equal(1536, NetQuery.ParseSize("1.5KB"));
        Assert.Equal(TimeSpan.FromSeconds(30), NetQuery.ParseAge("-30s"));
    }

    [Fact]
    public void Suggestions()
    {
        var types = new[] { "MsgEntity", "MsgState", "RequestInteractEvent" };
        Assert.Contains(NetQuery.Suggest("ty", types), s => s.Insert == "type:");
        Assert.Contains(NetQuery.Suggest("-ty", types), s => s.Insert == "-type:");
        Assert.Equal(["type:MsgEntity", "type:MsgState"], NetQuery.Suggest("type:Msg", types).Select(s => s.Insert));
        Assert.Contains(NetQuery.Suggest("@q", types), s => s.Insert == "@quiet");
        Assert.Contains(NetQuery.Suggest("dir:o", types), s => s.Insert == "dir:out");
    }

    [Fact]
    public void BareWordsAndEntitiesAreSuggested()
    {
        var types = new[] { "MsgStateAck", "MsgState", "RequestInteractEvent" };
        (int, string)[] ents = [(1873, "RequestInteractEvent · 14"), (182, "PopupEntityEvent · 3"), (22, "MsgState · 61")];
        Assert.Contains(NetQuery.Suggest("MsgStateAc", types), s => s.Insert == "type:MsgStateAck");
        Assert.Contains(NetQuery.Suggest("Interact", types), s => s.Insert == "type:RequestInteractEvent");   // contains
        Assert.Equal(["entity:1873", "entity:182"], NetQuery.Suggest("entity:18", types, ents).Select(s => s.Insert));
        Assert.Contains(NetQuery.Suggest("18", types, ents), s => s.Insert == "entity:1873" && s.Hint.StartsWith("RequestInteract"));
        Assert.Contains(NetQuery.Suggest("-Interact", types), s => s.Insert == "-type:RequestInteractEvent");
    }
}

public class NetCaptureTests
{
    [Fact]
    public void RingKeepsTheNewest_AndDropsOldMessageObjects()
    {
        NetCapture.Clear();
        NetCapture.Capacity = 1000;
        var first = NetCapture.LastSeq + 1;
        for (var i = 0; i < 2500; i++)
            NetCapture.Add(new Packet { Type = typeof(MsgChatMessage), Dir = NetDir.In, Size = 10, Message = new object() });

        Assert.Equal(1000, NetCapture.Count);
        Assert.Null(NetCapture.Get(first));                         // overwritten
        var newest = NetCapture.Get(NetCapture.LastSeq)!;
        Assert.NotNull(newest.Message);
        Assert.Equal(first + 2499, newest.Seq);
        Assert.Equal(1000, NetCapture.Since(0).Count);
        Assert.Equal(25000, NetCapture.BytesIn);
    }

    [Fact]
    public void RangeIsBounded()
    {
        NetCapture.Clear();
        NetCapture.Capacity = 1000;
        for (var i = 0; i < 10; i++) NetCapture.Add(new Packet { Type = typeof(MsgChatMessage), Size = 1 });
        var first = NetCapture.FirstSeq;
        var range = NetCapture.Range(first + 2, first + 4);
        Assert.Equal([first + 2, first + 3, first + 4], range.Select(p => p.Seq));
        Assert.Equal(10, NetCapture.Range(first - 50, first + 50).Count);
    }

    [Fact]
    public void ClearResetsCountsAndKeepsNumbering()
    {
        NetCapture.Clear();
        NetCapture.Capacity = 1000;
        for (var i = 0; i < 10; i++) NetCapture.Add(new Packet { Type = typeof(MsgChatMessage), Size = 1 });
        var last = NetCapture.LastSeq;
        NetCapture.Clear();
        Assert.Equal(0, NetCapture.Count);
        Assert.Empty(NetCapture.Since(0));
        Assert.Equal(last + 1, NetCapture.FirstSeq);
        NetCapture.Add(new Packet { Type = typeof(MsgChatMessage), Size = 1 });
        Assert.Equal(1, NetCapture.Count);
        Assert.Equal(last + 1, NetCapture.LastSeq);
    }

    [Fact]
    public void DroppedPacketsKeepTheirSummary()
    {
        NetCapture.Clear();
        NetCapture.Capacity = 5000;
        NetCapture.Add(new Packet { Type = typeof(MsgChatMessage), Message = new MsgChatMessage() });
        var first = NetCapture.LastSeq;
        // Small packets all keep their object; going over the byte budget drops the oldest.
        for (var i = 0; i < 2100; i++) NetCapture.Add(new Packet { Type = typeof(MsgChatMessage), Size = 40, Message = new object() });
        Assert.NotNull(NetCapture.Get(first)!.Message);
        NetCapture.Add(new Packet { Type = typeof(MsgChatMessage), Size = (int)NetCapture.KeepBytes, Message = new object() });
        var p = NetCapture.Get(first)!;
        Assert.Null(p.Message);
        Assert.Equal("who took the jaws?", p.Summary);
    }

    [Fact]
    public void PinnedPacketsKeepTheirObject()
    {
        NetCapture.Clear();
        NetCapture.Capacity = 5000;
        NetCapture.Add(new Packet { Type = typeof(MsgChatMessage), Message = new MsgChatMessage(), Bytes = [1, 2] });
        var pinned = NetCapture.Get(NetCapture.LastSeq)!;
        pinned.Pinned = true;
        NetCapture.Add(new Packet { Type = typeof(MsgChatMessage), Size = (int)NetCapture.KeepBytes + 1, Message = new object() });
        Assert.NotNull(pinned.Message);
        Assert.NotNull(pinned.Bytes);
        NetCapture.ReleaseMessages();   // feature off lets go of everything
        Assert.Null(pinned.Message);
    }

    [Fact]
    public void UnpinDropsWhatTheBudgetPassedBy()
    {
        NetCapture.Clear();
        NetCapture.Capacity = 5000;
        NetCapture.Add(new Packet { Type = typeof(MsgChatMessage), Message = new MsgChatMessage(), Bytes = [1] });
        var seq = NetCapture.LastSeq;
        NetCapture.Pin(seq);
        NetCapture.Add(new Packet { Type = typeof(MsgChatMessage), Size = (int)NetCapture.KeepBytes + 1, Message = new object() });
        Assert.NotNull(NetCapture.Get(seq)!.Message);   // kept while open
        NetCapture.Unpin(seq);
        Assert.Null(NetCapture.Get(seq)!.Message);      // released on close
        Assert.Equal("who took the jaws?", NetCapture.Get(seq)!.Summary);
    }

    [Fact]
    public void OnlyTheNewestGameStatesKeepTheirObject()
    {
        NetCapture.Clear();
        NetCapture.Capacity = 1000;
        for (var i = 0; i < 40; i++)
            NetCapture.Add(new Packet { Type = typeof(MsgState), Dir = NetDir.In, Message = new object() });
        var states = NetCapture.Since(0).Where(p => p.Type == typeof(MsgState)).ToList();
        Assert.Equal(32, states.Count(p => p.Message is not null));
        Assert.Null(states[0].Message);
    }
}

public class NetDecodeTests
{
    [Fact]
    public void FieldTreeReadsEngineTypesNicely()
    {
        var p = new Packet { Type = typeof(RequestInteractEvent), Message = new RequestInteractEvent() };
        var root = NetDecode.Root(p);
        Assert.True(root.CanExpand);

        var kids = NetDecode.Children(root).ToDictionary(c => c.Name);
        Assert.Contains(kids["Target"].Text, s => s.Text == "1873" && s.Tone == Tone.Link);
        Assert.Contains(kids["AltInteract"].Text, s => s.Text == "false" && s.Tone == Tone.Accent2);
        Assert.Contains(kids["Hand"].Text, s => s.Text == "\"right\"" && s.Tone == Tone.Green);
    }

    [Fact]
    public void CollectionsAndSummary()
    {
        var p = new Packet { Type = typeof(MsgChatMessage), Message = new MsgChatMessage() };
        Assert.Equal("who took the jaws?", NetDecode.Summary(p));

        var recipients = NetDecode.Children(NetDecode.Root(p)).Single(c => c.Name == "Recipients");
        Assert.True(recipients.CanExpand);
        Assert.Equal(["[0]", "[1]", "[2]"], NetDecode.Children(recipients).Select(c => c.Name));
    }

    [Fact]
    public void DroppedMessageCannotExpand()
    {
        var root = NetDecode.Root(new Packet { Type = typeof(MsgState), Message = null });
        Assert.False(root.CanExpand);
    }

    [Fact]
    public void HexDump()
    {
        var bytes = Enumerable.Range(0, 20).Select(i => (byte)(i + 60)).ToArray();
        var lines = NetDecode.HexLines(bytes).ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal("0000", lines[0].Offset);
        Assert.Equal("0010", lines[1].Offset);
        Assert.StartsWith("3c 3d 3e 3f 40 41 42 43  44 ", lines[0].Hex);
        Assert.Equal("<=>?@ABCDEFGHIJK", lines[0].Ascii);
        Assert.Equal("LMNO", lines[1].Ascii);
        Assert.Equal(lines[0].Hex.Length, lines[1].Hex.Length);   // the short row is padded
        Assert.Equal(".", NetDecode.HexLines([0x07]).Single().Ascii);
    }

    [Fact]
    public void FieldsAsText()
    {
        var p = new Packet { Type = typeof(MsgChatMessage), Dir = NetDir.In, Message = new MsgChatMessage() };
        var text = NetDecode.ToText(NetDecode.Root(p));
        Assert.Contains("who took the jaws?", text);
        Assert.Contains("Recipients", text);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length >= 3);
        Assert.StartsWith("  ", lines[1]);   // fields are indented under the header
    }

    [Fact]
    public void NullableEntityFieldsAreCollected()
    {
        var ids = NetDecode.EntityIdsOf(new RequestPerformActionEvent());
        Assert.Equal([77], ids);
    }

    [Fact]
    public void MultiLineStringsFoldIntoTheirLines()
    {
        var p = new Packet { Type = typeof(MsgLobbyInfo), Dir = NetDir.In, Message = new MsgLobbyInfo() };
        var blob = NetDecode.Children(NetDecode.Root(p)).Single(n => n.Name == "TextBlob");
        Assert.True(blob.CanExpand);                                     // one row shows only its start
        Assert.Contains("3 lines", string.Concat(blob.Text.Select(t => t.Text)));
        var lines = NetDecode.Children(blob);
        Assert.Equal(3, lines.Count);
        Assert.Equal("Round: #65", lines[1].Text[0].Text);
        Assert.Equal(["a", "b"], NetDecode.StringLines("a\r\nb"));
        Assert.Equal(2, NetDecode.StringLines(new string('x', 200)).Count);   // a long line in pieces
    }

    [Fact]
    public void WireTypesAreFoundByReflection()
    {
        var found = NetTypes.Scan([typeof(NetDecodeTests).Assembly], typeof(FakeNetMessage)).ToDictionary(x => x.Name, x => x.Kind);
        Assert.Equal(NetTypes.Kind.Message, found["MsgFakePing"]);
        Assert.Equal(NetTypes.Kind.Event, found["FakeInteractEvent"]);
        Assert.Equal(NetTypes.Kind.Bui, found["BUI.FakeStorageMessage"]);
        Assert.False(found.ContainsKey("LocalOnlyEvent"));
        Assert.False(found.ContainsKey("FakeStorageMessage"));   // a BUI message is not listed as an event
        Assert.False(found.ContainsKey("FakeWrapMessage"));      // only carries one
        Assert.False(found.ContainsKey("FakeNetMessage"));   // abstract
    }
}
