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
public struct FakeVector2 { public float X; public float Y; }
public struct FakeCoordinates { public NetEntity NetEntity; public FakeVector2 Position; }
public enum FakeHand { Left, Right }
public sealed class FakeInteractDraftEvent
{
    public NetEntity Target = new(1873);
    public bool AltInteract;
    public FakeHand Hand = FakeHand.Right;
    public FakeCoordinates Coordinates = new() { NetEntity = new NetEntity(2), Position = new FakeVector2 { X = 12.5f, Y = -3.25f } };
    public int[] Tags = [1, 2];
    public NetEntity? Maybe;
}
public sealed class SnapInner { public string Name = "a"; public List<int> Ids = [1, 2]; }
public sealed class SnapProbe
{
    public NetEntity Target = new(1873);
    public FakeHand Hand = FakeHand.Right;
    public object Key = FakeHand.Left;                       // an enum behind object: the type travels with it
    public FakeCoordinates Where = new() { NetEntity = new NetEntity(2), Position = new FakeVector2 { X = 1.5f, Y = -2f } };
    public int[] Tags = [3, 4];
    public Dictionary<string, int> Counts = new() { ["a"] = 1, ["b"] = 2 };
    public SnapInner? Inner = new();
    public SnapInner? Missing;
    public float? Alpha = 0.25f;
    public TimeSpan Delay = TimeSpan.FromMilliseconds(1500);
    public string? Note;
    public byte[] Blob = [1, 2, 250];
    public Stack<int> Order = new([1, 2, 3]);
}
public sealed class SnapHolder { public Dictionary<string, int[]> Map = new(); public SnapInner? Inner; }
public sealed class SnapHuge { public byte[] Big = new byte[10]; public int[] Many = new int[60_000]; public int After = 42; }
public sealed class MsgState { }
public sealed class MsgStateAck { }
public sealed class MsgStateLeavePvs { }
public sealed class MsgEntity { }

public class NetQueryTests
{
    private static Packet P(string type, NetDir dir, int size = 64, uint tick = 100, string summary = "")
        => new() { Type = Type.GetType("Despada.Tests." + type) ?? typeof(object), Dir = dir, Size = size, Tick = tick, Summary = summary };

    private static bool M(string q, Packet p) => NetQuery.Parse(q).Matches(p, x => x.Summary ?? "");

    [Fact]
    public void ExactTypeTokenDoesNotMatchLongerNames()
    {
        Assert.True(M("type:MsgState", P("MsgStateAck", NetDir.Out)));    // contains
        Assert.False(M("type:=MsgState", P("MsgStateAck", NetDir.Out)));  // exact
        Assert.True(M("type:=MsgState", P("MsgState", NetDir.In)));
        Assert.True(M("type:=msgstate", P("MsgState", NetDir.In)));
    }

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
        var keep = NetCapture.KeepPerType;
        NetCapture.KeepPerType = 10_000;   // one type here: the byte budget is what's under test
        try
        {
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
        finally { NetCapture.KeepPerType = keep; }
    }

    [Fact]
    public void PinnedPacketsKeepTheirObject()
    {
        NetCapture.Clear();
        NetCapture.Capacity = 5000;
        NetCapture.Add(new Packet { Type = typeof(MsgChatMessage), Message = new MsgChatMessage(), Bytes = [1, 2] });
        var pinned = NetCapture.Get(NetCapture.LastSeq)!;
        pinned.Pins = 1;
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

    [Fact]
    public void DraftFromAPacketMirrorsItsFields()
    {
        var p = new Packet { Type = typeof(FakeInteractDraftEvent), Dir = NetDir.Out, Message = new FakeInteractDraftEvent(), Seq = 7 };
        var d = PacketDraft.From(p)!;
        Assert.Equal("FakeInteractDraftEvent", d.TypeName);
        Assert.Equal(NetDir.Out, d.Dir);
        var byPath = d.Walk().ToDictionary(x => x.Path, x => x.Field);
        Assert.Equal(DraftKind.Entity, byPath["Target"].Kind);
        Assert.Equal("1873", byPath["Target"].Value);
        Assert.Equal(DraftKind.Bool, byPath["AltInteract"].Kind);
        Assert.Equal(DraftKind.Enum, byPath["Hand"].Kind);
        Assert.Equal(["Left", "Right"], byPath["Hand"].Options);
        Assert.Equal("Right", byPath["Hand"].Value);
        Assert.Equal(DraftKind.Number, byPath["Coordinates.Position.X"].Kind);   // nested structs unfold
        Assert.Equal("12.5", byPath["Coordinates.Position.X"].Value);
        Assert.Equal(DraftKind.Number, byPath["Tags[0]"].Kind);                   // list items are edited one by one
        Assert.Equal("2", byPath["Tags[1]"].Value);
        Assert.Equal(DraftKind.Entity, byPath["Maybe"].Kind);
        Assert.Equal("", byPath["Maybe"].Value);                                  // a nullable: empty = null
        Assert.Equal(0, d.ChangedCount);

        byPath["Target"].Value = "22";
        Assert.Equal([("Target", "22")], d.Changes());
        var copy = d.Clone();
        copy.Walk().First(x => x.Path == "Target").Field.Value = "5";
        Assert.Equal("22", byPath["Target"].Value);                              // the clone is its own
    }

    [Fact]
    public void SavedPacketsRoundTrip()
    {
        var p = new Packet { Type = typeof(FakeInteractDraftEvent), Dir = NetDir.Out, Message = new FakeInteractDraftEvent() };
        var d = PacketDraft.From(p)!;
        d.Walk().First(x => x.Path == "Hand").Field.Value = "Left";
        var saved = SavedPacket.FromDraft(d, "test");
        Assert.Equal("Left", saved.Values["Hand"]);
        var dto  = saved.ToDto();
        var back = SavedPacket.FromDto(dto);
        Assert.Equal("test", back.Name);
        Assert.Equal(NetDir.Out, back.Dir);
        Assert.Equal("Left", back.Values["Hand"]);
    }

    [Fact]
    public void SnapshotRoundTripsThroughJson()
    {
        var src = new SnapProbe();
        var json = ObjectSnapshot.ToJson(ObjectSnapshot.Take(src));
        var back = (SnapProbe)ObjectSnapshot.Materialize(ObjectSnapshot.FromJson(json)!)!;

        Assert.Equal(1873, back.Target.Id);
        Assert.Equal(FakeHand.Right, back.Hand);
        Assert.Equal(FakeHand.Left, back.Key);
        Assert.Equal(2, back.Where.NetEntity.Id);
        Assert.Equal(-2f, back.Where.Position.Y);
        Assert.Equal([3, 4], back.Tags);
        Assert.Equal(2, back.Counts["b"]);
        Assert.Equal("a", back.Inner!.Name);
        Assert.Equal([1, 2], back.Inner.Ids);
        Assert.Null(back.Missing);
        Assert.Equal(0.25f, back.Alpha);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), back.Delay);
        Assert.Null(back.Note);
        Assert.Equal([1, 2, 250], back.Blob);                 // bytes travel as one base64 leaf
        Assert.Equal([1, 2, 3], back.Order.ToArray());        // a stack keeps its order
        Assert.DoesNotContain("\"n\":false", json);            // defaults aren't written
    }

    [Fact]
    public void ACutSnapshotRefusesToRebuild()
    {
        var node = ObjectSnapshot.Take(new SnapHuge());       // 60k items: over the node budget
        Assert.False(node.Complete);
        var ex = Assert.Throws<InvalidOperationException>(() => ObjectSnapshot.Materialize(node));
        Assert.Contains("cut", ex.Message);
        // A draft of it can't be sent from the snapshot: Build throws instead of sending zeros.
        var d = PacketDraft.From(new Packet { Type = typeof(SnapHuge), Dir = NetDir.Out, Message = new SnapHuge(), Seq = 999_997 })!;
        Assert.Throws<InvalidOperationException>(() => DraftBuilder.Build(d, out _));
        // And a saved copy keeps no snapshot rather than a broken one.
        Assert.True(SavedPacket.FromDraft(d, "huge").NoSnapshot);
    }

    [Fact]
    public void DraftBuildsFromItsSnapshotWhenThePacketIsGone()
    {
        // Seq 999_999 is not in the log: the live object can't be used, the snapshot is.
        var p = new Packet { Type = typeof(SnapProbe), Dir = NetDir.Out, Message = new SnapProbe(), Seq = 999_999 };
        var d = PacketDraft.From(p)!;
        Assert.NotNull(d.Source);
        d.Walk().First(x => x.Path == "Hand").Field.Value = "Left";
        var built = (SnapProbe)DraftBuilder.Build(d, out _);
        Assert.Equal(FakeHand.Left, built.Hand);         // the edit
        Assert.Equal([3, 4], built.Tags);                // a read-only field, back from the snapshot
        Assert.Equal(FakeHand.Left, built.Key);
        Assert.Equal("a", built.Inner!.Name);
    }

    [Fact]
    public void SavedPacketKeepsTheSnapshotAndItsEditsApply()
    {
        var p = new Packet { Type = typeof(SnapProbe), Dir = NetDir.Out, Message = new SnapProbe(), Seq = 999_998 };
        var d = PacketDraft.From(p)!;
        d.Walk().First(x => x.Path == "Where.Position.X").Field.Value = "7";
        var back = SavedPacket.FromDto(SavedPacket.FromDraft(d, "probe").ToDto());
        Assert.NotNull(back.Source);
        var again = back.Draft()!;
        Assert.Equal(0, again.ChangedCount);             // what was saved is the edited packet itself
        var built = (SnapProbe)DraftBuilder.Build(again, out _);
        Assert.Equal(7f, built.Where.Position.X);
        Assert.Equal([3, 4], built.Tags);
    }

    [Fact]
    public void MuteKeepsMatchingPacketsOutOfTheLog_Unarmed()
    {
        NetActive.Reset();
        NetActive.Rules.Clear();
        NetCapture.Clear();
        NetActive.MutedCount = 0;
        var mute = new NetRule { Action = RuleAction.Mute }; mute.SetFilter("type:MsgState");
        NetActive.Rules.Add(mute);
        try
        {
            Assert.True(NetCapture.Add(new Packet { Type = typeof(MsgState), Dir = NetDir.In, Size = 1 }));      // delivered
            Assert.True(NetCapture.Add(new Packet { Type = typeof(MsgChatMessage), Dir = NetDir.In, Size = 1 }));
            Assert.Equal(1, NetCapture.Count);                    // only the chat message was logged
            Assert.Equal(typeof(MsgChatMessage), NetCapture.Snapshot()[0].Type);
            Assert.Equal(1, mute.Hits);
            Assert.Equal(1, NetActive.MutedCount);
            Assert.Equal(1, NetCapture.TotalIn);                  // the muted one isn't counted as logged traffic
        }
        finally { NetActive.Rules.Clear(); NetCapture.Clear(); }
    }

    [Fact]
    public void PinnedRulesRoundTripThroughTheirDto()
    {
        var r = new NetRule { Action = RuleAction.Duplicate, Count = 4, EveryMs = 20, Enabled = false };
        r.SetFilter("dir:out type:=MsgStateAck !unsafe");
        var back = NetRule.FromDto(r.ToDto());
        Assert.Equal(RuleAction.Duplicate, back.Action);
        Assert.Equal((4, 20, false, true), (back.Count, back.EveryMs, back.Enabled, back.Pinned));
        Assert.Equal(r.Filter, back.Filter);
        Assert.True(back.Unsafe);
        Assert.Equal(2, back.Query.Tokens.Count);
    }

    [Fact]
    public void LongStringsWrapByWords()
    {
        const string text = "\"Removes 30,0 кДж of heat from the body it's in when у него есть по крайней мере 12ед. this reagent.\"";
        var lines = new List<string>();
        for (var at = 0; at < text.Length;)
        {
            var (start, end) = NetView.NextLine(text, at, 46);
            lines.Add(text[start..end]);
            at = end;
        }
        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => { Assert.True(l.Length <= 46); Assert.False(l.StartsWith(' ')); Assert.False(l.EndsWith(' ')); });
        Assert.Equal(text, string.Join(' ', lines));                       // whole words, nothing lost
        Assert.EndsWith("reagent.\"", lines[^1]);                           // the closing quote stays with its word
        var word = new string('x', 100);
        Assert.Equal((0, 40), NetView.NextLine(word, 0, 40));              // a word longer than a line is cut
    }

    [Fact]
    public void TypeLabelsFoldFromTheInsideToFit()
    {
        const string t = "KeyValuePair<string, FrozenDictionary<string, FrozenDictionary<string, object>>>";
        Assert.Equal(t, NetView.ElideType(t, 100));
        Assert.Equal("KeyValuePair<string, FrozenDictionary<string, …>>", NetView.ElideType(t, 50));
        Assert.Equal("KeyValuePair<string, FrozenDictionary<…>>", NetView.ElideType(t, 41));
        Assert.Equal("KeyValuePair<…>", NetView.ElideType(t, 20));
        Assert.Equal("KeyValuePair<…>", NetView.ElideType(t, 10));   // the floor: never folded further
        Assert.Equal("List<int>", NetView.ElideType("List<int>", 9));
    }

    [Fact]
    public void TableValuesShowContentAndPagesOpenOnDemand()
    {
        var big = new Dictionary<string, int[]>();
        for (var i = 0; i < 250; i++) big[$"k{i}"] = [i];
        var root = NetDecode.Root(new Packet { Type = typeof(object), Message = new SnapHolder { Map = big, Inner = new SnapInner() } });
        var kids = NetDecode.Children(root).ToDictionary(c => c.Name);
        Assert.Equal("250 entries", kids["Map"].ValueText[0].Text);                       // a dictionary: its count
        Assert.StartsWith("{Name = \"a\"", kids["Inner"].ValueText[0].Text);              // an object: its first fields
        var items = NetDecode.Children(kids["Map"]);
        Assert.Equal(101, items.Count);                                                   // 100 + the "… more" row
        Assert.Equal("\"k0\"", items[0].ValueText[0].Text);                               // a pair: its key
        Assert.Same(kids["Map"], items[^1].PageOf);
        Assert.Equal("… 150 more", items[^1].ValueText[0].Text);
        kids["Map"].ShowMore(all: false);
        Assert.Equal(201, NetDecode.Children(kids["Map"]).Count);
        kids["Map"].ShowMore(all: true);
        Assert.Equal(250, NetDecode.Children(kids["Map"]).Count);
    }

    [Fact]
    public void ACapPerTypeEvictsTheOldestOfThatTypeOnly()
    {
        NetActive.Reset();
        NetActive.Rules.Clear();
        NetCapture.Clear();
        var keep = NetCapture.KeepPerType;
        NetCapture.KeepPerType = 50;
        var before = NetCapture.EvictionCount;
        try
        {
            NetCapture.Add(new Packet { Type = typeof(MsgChatMessage), Dir = NetDir.In, Size = 1 });   // the one that must survive
            for (var i = 0; i < 120; i++) NetCapture.Add(new Packet { Type = typeof(MsgState), Dir = NetDir.In, Size = 1 });
            var all = NetCapture.Snapshot();
            Assert.Equal(51, all.Count);
            Assert.Equal(51, NetCapture.Count);
            Assert.Equal(typeof(MsgChatMessage), all[0].Type);                       // still there, before the states
            Assert.Equal(50, all.Count(p => p.Type == typeof(MsgState)));
            Assert.Equal((50, 120, 120L), (NetCapture.TypeStats("MsgState").Kept, (int)NetCapture.TypeStats("MsgState").Total, NetCapture.TypeStats("MsgState").Total));
            Assert.Equal(1, NetCapture.TypeStats("MsgChatMessage").Kept);
            Assert.Equal("", NetCapture.TypeHint("Nope"));
            var (gone, complete) = NetCapture.EvictedSince(before);
            Assert.True(complete);
            Assert.Equal(70, gone.Count);                                             // the list gets every evicted seq
            Assert.All(gone, seq => Assert.Null(NetCapture.Get(seq)));
        }
        finally { NetCapture.KeepPerType = keep; NetCapture.Clear(); }
    }

    [Fact]
    public void ListItemsAreEditedAddedAndRemoved()
    {
        var p = new Packet { Type = typeof(SnapProbe), Dir = NetDir.Out, Message = new SnapProbe(), Seq = 999_996 };
        var d = PacketDraft.From(p)!;
        var tags  = d.Root.Children.Single(f => f.Name == "Tags");
        var inner = d.Root.Children.Single(f => f.Name == "Inner");
        var ids   = inner.Children.Single(f => f.Name == "Ids");
        Assert.Equal(DraftKind.List, tags.Kind);
        Assert.Equal(["[0]", "[1]"], tags.Children.Select(c => c.Name));
        Assert.Equal(0, d.ChangedCount);

        tags.Children[0].Value = "9";                         // edit
        PacketDraft.AddItem(tags).Value = "5";                 // add
        PacketDraft.RemoveItem(ids, ids.Children[0]);          // remove from a nested list
        Assert.Equal("[0]", ids.Children[0].Name);             // renumbered
        Assert.True(tags.Touched);
        Assert.Equal(4, d.ChangedCount);                       // the edit, the new item's value, the two touched lists

        var built = (SnapProbe)DraftBuilder.Build(d, out _);
        Assert.Equal([9, 4, 5], built.Tags);
        Assert.Equal([2], built.Inner!.Ids);
        Assert.Equal(FakeHand.Left, built.Key);                // untouched fields stay (the live object is used)

        var saved = SavedPacket.FromDraft(d, "lists").Draft()!;   // saved as the edited packet
        Assert.Equal([9, 4, 5], ((SnapProbe)DraftBuilder.Build(saved, out _)).Tags);
    }

    [Fact]
    public void FinderHighlightsEveryOccurrence()
    {
        var spans = Despada.ImGui.UserInterface.Finder.Highlight("ss14s://corvaxforge.ru/corvax", "corvax", Tone.Green);
        Assert.Equal(["ss14s://", "corvax", "forge.ru/", "corvax"], spans.Select(s => s.Text));
        Assert.Equal([Tone.Green, Tone.Accent2, Tone.Green, Tone.Accent2], spans.Select(s => s.Tone));
        Assert.Equal("Corvax", Despada.ImGui.UserInterface.Finder.Highlight("Corvax", "CORVAX").Single().Text);   // case-insensitive, one span
        Assert.Single(Despada.ImGui.UserInterface.Finder.Highlight("nothing", "x"));
    }

    [Fact]
    public void RulesMatchWithTheListsQueryLanguage()
    {
        var r = new NetRule { Action = RuleAction.Freeze };
        r.SetFilter("dir:in type:MsgChat");
        var chat = new Packet { Type = typeof(MsgChatMessage), Dir = NetDir.In, Summary = "" };
        var ack  = new Packet { Type = typeof(MsgStateAck), Dir = NetDir.Out, Summary = "" };
        Assert.True(r.Matches(chat));
        Assert.False(r.Matches(ack));
        r.Enabled = false;
        Assert.False(r.Matches(chat));
        Assert.Equal("freeze", r.ActionLabel);
        var empty = new NetRule();
        empty.SetFilter("   ");
        Assert.False(empty.Matches(chat));   // no filter never means "everything"
    }

    [Fact]
    public void RulesActOnlyWhenArmed_AndFirstMatchWins()
    {
        NetActive.Reset();
        NetActive.Rules.Clear();
        NetActive.BlockedCount = 0;
        var block  = new NetRule { Action = RuleAction.Block };  block.SetFilter("type:MsgChat");
        var freeze = new NetRule { Action = RuleAction.Freeze }; freeze.SetFilter("dir:in");
        NetActive.Rules.Add(block);
        NetActive.Rules.Add(freeze);
        Packet Chat() => new() { Type = typeof(MsgChatMessage), Dir = NetDir.In, Summary = "" };
        Packet Ack()  => new() { Type = typeof(MsgEntity), Dir = NetDir.In, Summary = "" };   // (state packets are guarded)

        Assert.True(NetActive.Apply(Chat()));                 // not armed: everything passes
        NetActive.Armed = true;
        var c = Chat();
        Assert.False(NetActive.Apply(c));                     // the block rule comes first
        Assert.Equal(PacketMark.Blocked, c.Mark);
        Assert.Equal(1, block.Hits);
        Assert.Equal(1, NetActive.BlockedCount);
        var a = Ack();
        Assert.False(NetActive.Apply(a));                     // only the freeze rule matches
        Assert.Equal(PacketMark.Frozen, a.Mark);
        Assert.Equal([a], NetActive.FrozenSnapshot());
        var ours = Chat(); ours.Ours = true;
        Assert.True(NetActive.Apply(ours));                   // our own packets never trigger rules
        Assert.Equal(PacketMark.None, ours.Mark);
        NetActive.Drop(a);
        Assert.Empty(NetActive.FrozenSnapshot());
        Assert.Equal(PacketMark.Blocked, a.Mark);
        NetActive.Reset();
        Assert.False(NetActive.Armed);
    }

    [Fact]
    public void ModifyRuleChangesTheObjectBeforeItIsBuilt()
    {
        NetActive.Reset();
        NetActive.Rules.Clear();
        var r = new NetRule { Action = RuleAction.Modify, Modify = "Hand=left; AltInteract=true" }; r.SetFilter("type:RequestInteract");
        NetActive.Rules.Add(r);
        NetActive.Armed = true;
        try
        {
            var ev = new RequestInteractEvent();
            // The prefix, before the engine serialises: the object changes in place.
            NetActive.ModifyBeforeSend(ev);
            Assert.Equal("left", ev.Hand);
            Assert.True(ev.AltInteract);
            // The capture after BuildMessage: the packet goes on (its bytes already carry the change), marked.
            var p = new Packet { Type = typeof(RequestInteractEvent), Dir = NetDir.Out, Message = ev, Summary = "" };
            Assert.True(NetActive.Apply(p));
            Assert.Equal(PacketMark.Edited, p.Mark);
            Assert.Null(r.Error);
            // A bad path is remembered on the rule, the packet still passes.
            var bad = new NetRule { Action = RuleAction.Modify, Modify = "Nope=1" }; bad.SetFilter("type:RequestInteract");
            NetActive.Rules.Insert(0, bad);
            NetActive.ModifyBeforeSend(new RequestInteractEvent());
            Assert.Contains("Nope", bad.Error);
        }
        finally
        {
            NetActive.Reset();
            NetActive.Rules.Clear();
        }
    }

    [Fact]
    public void RulesWithUnknownTokensAreOff_AndStateGuardCoversAllStateMessages()
    {
        NetActive.Reset();
        NetActive.Rules.Clear();
        var typo = new NetRule { Action = RuleAction.Block }; typo.SetFilter("dir:up");
        Assert.True(typo.Invalid);
        NetActive.Rules.Add(typo);
        NetActive.Armed = true;
        try
        {
            Assert.True(NetActive.Apply(new Packet { Type = typeof(MsgChatMessage), Dir = NetDir.Out, Summary = "" }));
            var all = new NetRule { Action = RuleAction.Block }; all.SetFilter("dir:in");
            NetActive.Rules.Add(all);
            Assert.True(NetActive.Apply(new Packet { Type = typeof(MsgStateLeavePvs), Dir = NetDir.In, Summary = "" }));   // MsgState* guarded
            Assert.False(NetActive.Apply(new Packet { Type = typeof(MsgChatMessage), Dir = NetDir.In, Summary = "" }));
        }
        finally
        {
            NetActive.Reset();
            NetActive.Rules.Clear();
        }
    }

    [Fact]
    public void PinsAreCounted_AnInspectorTabClosingKeepsAFrozenPacket()
    {
        NetCapture.Clear();
        NetCapture.Capacity = 5000;
        NetCapture.Add(new Packet { Type = typeof(MsgChatMessage), Message = new MsgChatMessage(), Bytes = [1, 2] });
        var p = NetCapture.Get(NetCapture.LastSeq)!;
        NetCapture.Pin(p);          // frozen
        NetCapture.Pin(p.Seq);      // opened in the inspector
        NetCapture.Add(new Packet { Type = typeof(MsgChatMessage), Size = (int)NetCapture.KeepBytes + 1, Message = new object() });
        NetCapture.Unpin(p.Seq);    // the tab closes
        Assert.NotNull(p.Message);  // still frozen: kept
        NetCapture.Unpin(p);        // released
        Assert.Null(p.Message);
    }

    [Fact]
    public void AddReturnsWhetherThePacketGoesOn()
    {
        NetCapture.Clear();
        NetCapture.Capacity = 1000;
        NetActive.Rules.Clear();
        var r = new NetRule { Action = RuleAction.Block }; r.SetFilter("type:MsgChat");
        NetActive.Rules.Add(r);
        NetActive.Armed = true;
        Assert.False(NetCapture.Add(new Packet { Type = typeof(MsgChatMessage), Dir = NetDir.Out, Size = 1, Message = new MsgChatMessage() }));
        Assert.True(NetCapture.Add(new Packet { Type = typeof(MsgState), Dir = NetDir.In, Size = 1, Message = new MsgState() }));
        Assert.Equal(2, NetCapture.Count);                    // a blocked packet is still in the log
        NetActive.Reset();
        NetActive.Rules.Clear();
    }

    [Fact]
    public void StatePacketsPassBlockAndFreeze_UnlessUnsafe()
    {
        var r = new NetRule { Action = RuleAction.Block };
        r.SetFilter("dir:in");
        var state = new Packet { Type = typeof(MsgState), Dir = NetDir.In, Summary = "" };
        var chat  = new Packet { Type = typeof(MsgChatMessage), Dir = NetDir.In, Summary = "" };
        Assert.False(r.Matches(state));
        Assert.True(r.Matches(chat));
        r.SetFilter("dir:in !unsafe");
        Assert.True(r.Unsafe);
        Assert.True(r.Matches(state));
        Assert.Equal("dir:in !unsafe", r.Filter);
        var dup = new NetRule { Action = RuleAction.Duplicate };
        dup.SetFilter("type:MsgState");
        Assert.True(dup.Matches(state));   // only block and freeze are guarded
        var hint = new NetRule { Action = RuleAction.Freeze };
        hint.SetFilter("type:MsgState");
        Assert.True(hint.WouldSkipState);
    }

    [Fact]
    public void ScheduledSendsRespectTheRateLimit()
    {
        NetActive.Reset();
        NetActive.Armed = true;
        NetActive.RateLimit = 3;
        var runs = 0;
        NetActive.Schedule(5, 0, () => runs++);
        NetActive.Pump();
        Assert.Equal(3, runs);                 // the limit within one second
        NetActive.Pump();
        Assert.Equal(3, runs);                 // the rest wait for the next window
        NetActive.Armed = false;
        NetActive.Pump();                      // disarmed: pending sends are dropped
        NetActive.Armed = true;
        NetActive.RateLimit = 20;
        NetActive.Pump();
        Assert.Equal(3, runs);
        NetActive.Reset();
    }

    [Fact]
    public void OurPacketsAreMarkedAndSkipTheRules()
    {
        NetCapture.Clear();
        NetCapture.Capacity = 1000;
        NetActive.Rules.Clear();
        var r = new NetRule { Action = RuleAction.Block }; r.SetFilter("type:MsgChat");
        NetActive.Rules.Add(r);
        NetActive.Armed = true;
        NetActive.CustomCount = 0;
        NetCapture.MarkNext(PacketMark.Custom);
        Assert.True(NetCapture.Add(new Packet { Type = typeof(MsgChatMessage), Dir = NetDir.Out, Size = 1, Message = new MsgChatMessage() }));
        var p = NetCapture.Get(NetCapture.LastSeq)!;
        Assert.True(p.Ours);
        Assert.Equal(PacketMark.Custom, p.Mark);
        Assert.Equal(1, NetActive.CustomCount);
        Assert.False(NetCapture.Add(new Packet { Type = typeof(MsgChatMessage), Dir = NetDir.Out, Size = 1, Message = new MsgChatMessage() }));   // the mark was one-shot
        NetActive.Reset();
        NetActive.Rules.Clear();
    }

    [Fact]
    public void DuplicateRuleMarksTheOriginal_AndQueuesTheCopies()
    {
        NetActive.Reset();
        NetActive.Rules.Clear();
        var r = new NetRule { Action = RuleAction.Duplicate, Count = 3, EveryMs = 1000 };
        r.SetFilter("type:MsgChat");
        NetActive.Rules.Add(r);
        NetActive.Armed = true;
        var p = new Packet { Type = typeof(MsgChatMessage), Dir = NetDir.Out, Size = 9, Message = new MsgChatMessage(), Summary = "" };
        Assert.True(NetActive.Apply(p));                      // the original goes on
        Assert.Equal(PacketMark.Duplicated, p.Mark);
        Assert.Equal(3, p.MarkCount);
        Assert.Equal(3, NetActive.PendingSends);               // the copies wait for their time
        NetActive.Pump();
        Assert.Equal(3, NetActive.PendingSends);               // not due yet (a second away)
        NetActive.Reset();
        Assert.Equal(0, NetActive.PendingSends);
        NetActive.Rules.Clear();
    }

    [Fact]
    public void DraftBuildsBackIntoAnObject()
    {
        var src = new FakeInteractDraftEvent();
        var p = new Packet { Type = typeof(FakeInteractDraftEvent), Dir = NetDir.Out, Message = src, Seq = 1 };
        var d = PacketDraft.From(p)!;
        d.FromSeq = -1;   // not in the capture: a new instance from the draft's values
        var f = d.Walk().ToDictionary(x => x.Path, x => x.Field);
        f["Target"].Value = "22";
        f["AltInteract"].Value = "true";
        f["Hand"].Value = "left";
        f["Coordinates.Position.X"].Value = "7.5";
        f["Maybe"].Value = "9";
        var built = (FakeInteractDraftEvent)DraftBuilder.Build(d, out var wrap);
        Assert.False(wrap);
        Assert.NotSame(src, built);
        Assert.Equal(22, built.Target.Id);
        Assert.True(built.AltInteract);
        Assert.Equal(FakeHand.Left, built.Hand);
        Assert.Equal(7.5f, built.Coordinates.Position.X);
        Assert.Equal(-3.25f, built.Coordinates.Position.Y);   // the struct's other field kept
        Assert.Equal(2, built.Coordinates.NetEntity.Id);
        Assert.Equal(9, built.Maybe!.Value.Id);
        Assert.Equal(src.Tags, built.Tags);                  // read-only fields come from the draft's source? no: a new instance keeps its own default
    }

    [Fact]
    public void DraftFromACapturedPacketCopiesIt()
    {
        NetCapture.Clear();
        NetCapture.Capacity = 1000;
        var src = new FakeInteractDraftEvent { Tags = [7, 8, 9] };
        NetCapture.Add(new Packet { Type = typeof(FakeInteractDraftEvent), Dir = NetDir.Out, Message = src, Size = 1 });
        var p = NetCapture.Get(NetCapture.LastSeq)!;
        var d = PacketDraft.From(p)!;
        d.Walk().First(x => x.Path == "Target").Field.Value = "22";
        var built = (FakeInteractDraftEvent)DraftBuilder.Build(d, out _);
        Assert.NotSame(src, built);
        Assert.Equal(22, built.Target.Id);
        Assert.Equal(1873, src.Target.Id);                   // the captured object is untouched
        Assert.Equal([7, 8, 9], built.Tags);                 // fields the editor can't show are copied
    }

    [Fact]
    public void FieldsAreSetByPath_AndBadValuesAreReported()
    {
        var o = new FakeInteractDraftEvent();
        Assert.True(DraftBuilder.TrySet(o, "Coordinates.Position.Y", "1.25", out _));
        Assert.Equal(1.25f, o.Coordinates.Position.Y);
        Assert.True(DraftBuilder.TrySet(o, "Hand", "Left", out _));
        Assert.Equal(FakeHand.Left, o.Hand);
        Assert.True(DraftBuilder.TrySet(o, "Target", "5", out _));
        Assert.True(DraftBuilder.TrySet(o, "Tags[1]", "9", out _));            // an array element
        Assert.Equal(9, o.Tags[1]);
        Assert.False(DraftBuilder.TrySet(o, "Tags[5]", "9", out var range));  // out of range: a reason, not a throw
        Assert.Contains("2 items", range);
        Assert.Equal(5, o.Target.Id);
        Assert.False(DraftBuilder.TrySet(o, "Target", "abc", out var err));
        Assert.Contains("Target", err);
        Assert.False(DraftBuilder.TrySet(o, "Nope", "1", out err));
        Assert.Contains("no field", err);
    }

    [Fact]
    public void FrozenPacketsKeepTheirMessage_AndRulesActWhilePaused()
    {
        NetCapture.Clear();
        NetCapture.Capacity = 1000;
        NetActive.Reset();
        NetActive.Rules.Clear();
        var r = new NetRule { Action = RuleAction.Freeze }; r.SetFilter("type:MsgChat");
        NetActive.Rules.Add(r);
        NetActive.Armed = true;
        Assert.False(NetCapture.Add(new Packet { Type = typeof(MsgChatMessage), Dir = NetDir.In, Size = 1, Message = new MsgChatMessage() }));
        var frozen = NetActive.FrozenSnapshot().Single();
        Assert.True(frozen.Pinned);
        // The budget passes it by: the message stays while it's frozen.
        NetCapture.Add(new Packet { Type = typeof(MsgEntity), Size = (int)NetCapture.KeepBytes + 1, Message = new object() });
        Assert.NotNull(frozen.Message);
        NetActive.Drop(frozen);
        Assert.Null(frozen.Message);                              // released from the budget on drop

        try
        {
            NetCapture.Paused = true;
            var count = NetCapture.Count;
            Assert.False(NetCapture.Add(new Packet { Type = typeof(MsgChatMessage), Dir = NetDir.In, Size = 1, Message = new MsgChatMessage() }));
            Assert.Equal(count, NetCapture.Count);                // not logged, but still frozen
            Assert.Single(NetActive.FrozenSnapshot());
        }
        finally
        {
            NetCapture.Paused = false;
            NetActive.Reset();
            NetActive.Rules.Clear();
        }
    }

    [Fact]
    public void DirectionIsLearnedFromTraffic_NotFromOurOwnPackets()
    {
        NetCapture.Clear();
        NetCapture.Capacity = 1000;
        NetCapture.Add(new Packet { Type = typeof(MsgChatMessage), Dir = NetDir.Out, Size = 1, Message = new MsgChatMessage() });
        Assert.Equal(NetDir.Out, NetActive.DirectionOf("MsgChatMessage"));
        NetCapture.MarkNext(PacketMark.Copy);
        NetCapture.Add(new Packet { Type = typeof(MsgChatMessage), Dir = NetDir.In, Size = 1, Message = new MsgChatMessage() });
        Assert.Equal(NetDir.Out, NetActive.DirectionOf("MsgChatMessage"));   // a delivery to ourselves doesn't count
    }
}

public class FinderWalkTests
{
    public sealed class WalkComp { public string Name = "comp"; public int A = 1; public int[] Ids = [1, 2, 3]; }
    public sealed class WalkEnt  { public int Uid; public List<WalkComp> Comps = [new(), new(), new(), new(), new()]; }
    public sealed class WalkState { public int Tick = 1; public List<WalkEnt> Ents = []; public string Server = "ss14.example.net:1212"; public string Note = ""; }

    private static IFinderSource Fields => Finder.Sources.Single(s => s.Tab == "Fields");
    private static FinderQuery Q(string text, bool brief = false, long after = 0, long until = long.MaxValue) => new(text, 100, after, until, brief);

    private static void Fresh()
    {
        NetActive.Reset(); NetActive.Rules.Clear(); NetCapture.Clear();
        NetView.UnregisterFinder();
        NetView.RegisterFinder();
    }

    [Fact]
    public void AFieldAfterABigListIsStillFound()
    {
        Fresh();
        var st = new WalkState();
        for (var i = 0; i < 400; i++) st.Ents.Add(new WalkEnt { Uid = i });
        NetCapture.Add(new Packet { Type = typeof(WalkState), Dir = NetDir.In, Size = 1, Message = st });
        try
        {
            var hits = Fields.Search(Q("example")).OfType<FinderItem>().ToList();
            Assert.Single(hits);
            Assert.Equal("Server", hits[0].Left[0].Text);
        }
        finally { NetView.UnregisterFinder(); NetCapture.Clear(); }
    }

    [Fact]
    public void AnOldPacketIsFoundBehindAThousandNewerOnes()
    {
        Fresh();
        NetCapture.Add(new Packet { Type = typeof(WalkState), Dir = NetDir.In, Size = 1, Message = new WalkState { Server = "hub.corvax.example" } });
        for (var i = 0; i < 1000; i++) NetCapture.Add(new Packet { Type = typeof(WalkComp), Dir = NetDir.In, Size = 1, Message = new WalkComp() });
        try
        {
            // Cold: a pause before each packet's index is made, so the finder's slice can stop the search.
            var steps = Fields.Search(Q("corvax")).ToList();
            Assert.True(steps.Count(s => s is null) >= 30, $"{steps.Count} steps");
            var hit = Assert.Single(steps.OfType<FinderItem>());
            Assert.Equal("Server", hit.Left[0].Text);
            // The index: one line per field (name, path, type, kind, tone, value), gone with the message.
            var p = NetCapture.Snapshot()[0];
            Assert.Contains("Server\u0001Server\u0001string\u0001s\u0001", p.Index);
            Assert.Contains("hub.corvax.example", p.Index);
            // Warm: only a scan of the indexes.
            Assert.True(Fields.Search(Q("corvax")).Count() < 100);
            // A live update: only what came after the version searched, up to the version now.
            var version = Fields.Version;
            NetCapture.Add(new Packet { Type = typeof(WalkState), Dir = NetDir.In, Size = 1, Message = new WalkState { Server = "corvax-two" } });
            Assert.NotEqual(version, Fields.Version);
            var fresh = Fields.Search(Q("corvax", after: version, until: Fields.Version)).OfType<FinderItem>().ToList();
            Assert.Single(fresh);
            Assert.Contains("corvax-two", string.Concat(fresh[0].Value.Select(v => v.Text)));
        }
        finally { NetView.UnregisterFinder(); NetCapture.Clear(); }
    }

    [Fact]
    public void ListsAreSearchedWholeAndStringsWhole()
    {
        Fresh();
        var st = new WalkState { Note = new string('x', 500) + " needle-in-note " + new string('y', 500) };
        for (var i = 0; i < 350; i++) st.Ents.Add(new WalkEnt { Uid = 7000 + i });
        NetCapture.Add(new Packet { Type = typeof(WalkState), Dir = NetDir.In, Size = 1, Message = st });
        try
        {
            // Item 340 lies past the first three pages of 100: the item (its summary) and its field.
            var deep = Fields.Search(Q("7340")).OfType<FinderItem>().Select(h => h.Link[h.Link.IndexOf(' ')..].Trim()).ToList();
            Assert.Equal(["Ents[340]", "Ents[340].Uid"], deep);
            // The whole string is matched; the row shows a window around the match.
            var note = Assert.Single(Fields.Search(Q("needle-in-note")).OfType<FinderItem>());
            var shown = string.Concat(note.Value.Select(v => v.Text));
            Assert.Contains("needle-in-note", shown);
            Assert.True(shown.Length < 200, shown);
            Assert.StartsWith("\"…", shown);
        }
        finally { NetView.UnregisterFinder(); NetCapture.Clear(); }
    }

    [Fact]
    public void BriefGivesOneRowPerPacketWithACount()
    {
        Fresh();
        var st = new WalkState();
        for (var i = 0; i < 3; i++) st.Ents.Add(new WalkEnt { Uid = i });   // "Comps" in 3 list names, "comp" in 15 item summaries and 15 Name values
        NetCapture.Add(new Packet { Type = typeof(WalkState), Dir = NetDir.In, Size = 1, Message = st });
        try
        {
            Assert.Equal(33, Fields.Search(Q("comp")).OfType<FinderItem>().Count());
            var brief = Assert.Single(Fields.Search(Q("comp", brief: true)).OfType<FinderItem>());
            Assert.Equal("Comps", string.Concat(brief.Left.Select(l => l.Text)));   // a name match, the shallowest
            Assert.StartsWith("+32 · ", brief.Right);
        }
        finally { NetView.UnregisterFinder(); NetCapture.Clear(); }
    }

    [Fact]
    public void APathResolvesThroughPagedLists()
    {
        var st = new WalkState();
        for (var i = 0; i < 350; i++) st.Ents.Add(new WalkEnt { Uid = i });
        var root = NetDecode.Root(new Packet { Type = typeof(WalkState), Message = st });
        var node = NetView.Resolve(root, "Ents[340].Comps[2].Ids");
        Assert.NotNull(node);
        Assert.Equal("Ids", node!.Name);
        var ents = NetDecode.Children(root).Single(c => c.Name == "Ents");
        Assert.True(ents.Expanded);
        Assert.Equal(4, ents.Page);                                   // paged out to the item
        Assert.Equal(350, NetDecode.Children(ents).Count);            // the fourth page shows the rest
    }
}
