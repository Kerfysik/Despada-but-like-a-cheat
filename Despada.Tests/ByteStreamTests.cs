// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text;
using Despada.ImGui.UserInterface;
using Despada.Modules.Net;
using Xunit;

namespace Despada.Tests;

public sealed class StreamProbeEvent
{
    public NetEntity Target = new(1873);   // on the wire as NetSerializer's zigzag varint: a2 1d
    public uint Tick = 4531;               // as Lidgren's little-endian word: b3 11 00 00
    public float Range = 0.2f;
    public string Hand = "right";
    public string Reason = "ab";           // inside a longer printable run: the run is split around it
    public int Dup = 1;                    // 01 00 00 00 occurs twice in the probe bytes: ambiguous, not matched
    public bool Alt = true;                // one byte: never matched
}

public class ByteStreamTests
{
    private static byte[] Cat(params object[] parts)
    {
        var list = new List<byte>();
        foreach (var p in parts)
            switch (p)
            {
                case byte[] b: list.AddRange(b); break;
                case string s: list.AddRange(Encoding.ASCII.GetBytes(s)); break;
                case int i: list.AddRange(BitConverter.GetBytes(i)); break;
                case float f: list.AddRange(BitConverter.GetBytes(f)); break;
                default: throw new ArgumentException(p.GetType().Name);
            }
        return list.ToArray();
    }

    private static IEnumerable<ByteToken> Of(ByteStream s, ByteKind k) => s.Tokens.Where(t => t.Kind == k);

    [Fact]
    public void Type_name_with_its_prefix_inside_the_printable_run()
    {
        const string name = "Content.Shared.Foo.BarEvent, Content.Shared";   // 43 chars: prefix 0x2c 0x2b, both printable
        var bytes = Cat(new byte[] { 0x01 }, new[] { (byte)(name.Length + 1), (byte)name.Length }, name, new byte[] { 0, 0, 0, 0, 0 }, new byte[] { 7, 1, 2, 3 });

        var s = ByteStream.Build(bytes, null);

        // The 01 marker before the two varints joins the badge.
        var len = Assert.Single(Of(s, ByteKind.Len));
        Assert.Equal(0, len.Offset);
        Assert.Equal(3, len.Length);
        Assert.Equal("43", len.PlainText);

        var str = Assert.Single(Of(s, ByteKind.Str));
        Assert.Equal(3, str.Offset);
        Assert.Equal(name, str.PlainText);
        Assert.Equal(["Content.Shared.Foo.", "BarEvent", ", Content.Shared"], str.Text.Select(x => x.Text));
        Assert.Equal([Tone.Muted, Tone.Accent2, Tone.Muted], str.Text.Select(x => x.Tone));

        var zero = Assert.Single(Of(s, ByteKind.Zero));
        Assert.Equal(5, zero.Length);
        Assert.Equal("00 ×5", zero.PlainText);
        Assert.Equal(1, s.Strings);
        // Every byte belongs to exactly one token, in order.
        Assert.Equal(bytes.Length, s.Tokens.Sum(t => t.Length));
        Assert.Equal(s.Tokens.OrderBy(t => t.Offset), s.Tokens);
    }

    [Fact]
    public void Length_prefix_before_a_short_string_and_plain_text_tones()
    {
        var bytes = Cat(new byte[] { 0x0b, 0x0a }, "abcdefghij", new byte[] { 0x05 }, "/Textures/x.png", new byte[] { 0, 0 });
        var s = ByteStream.Build(bytes, null);

        var len = Of(s, ByteKind.Len).First();
        Assert.Equal((0, 2), (len.Offset, len.Length));
        Assert.Equal(Tone.Green, Of(s, ByteKind.Str).First().Text[0].Tone);
        Assert.Equal(Tone.Link, Of(s, ByteKind.Str).Last().Text[0].Tone);
        // Two zeros stay two plain bytes.
        Assert.Empty(Of(s, ByteKind.Zero));
        Assert.Equal(2, Of(s, ByteKind.Hex).Count(t => t.PlainText == "00"));
    }

    [Fact]
    public void Fields_match_by_value_only_when_unique()
    {
        var ev = new StreamProbeEvent();
        var bytes = Cat(new byte[] { 0x2a, 0x01 }, 1, new byte[] { 0, 0 }, (int)4531, new byte[] { 0xa2, 0x1d }, 0.2f,
                        new byte[] { 0x01, 0x06, 0x05 }, "right", 1, new byte[] { 1 }, "xxabyy", new byte[] { 0x00 });
        var root = NetDecode.Root(new Packet { Type = typeof(StreamProbeEvent), Message = ev });

        var s = ByteStream.Build(bytes, root);

        Assert.Equal(5, s.Matched);
        var nums = Of(s, ByteKind.Num).OrderBy(t => t.Offset).ToList();
        Assert.Equal(["Tick", "Target", "Range"], nums.Select(t => t.Field!.Name));
        Assert.Equal("b3 11 00 00", nums[0].PlainText);
        Assert.Equal("a2 1d", nums[1].PlainText);
        Assert.Equal("1873", nums[1].FieldValue![..4]);
        // "right" with its 01 06 05 prefix: the marker and both varints make one badge.
        var len = Assert.Single(Of(s, ByteKind.Len));
        Assert.Equal((3, "5"), (len.Length, len.PlainText));
        var strs = Of(s, ByteKind.Str).OrderBy(t => t.Offset).ToList();
        Assert.Equal(["right", "xx", "ab", "yy"], strs.Select(t => t.PlainText));
        Assert.Equal("Hand", strs[0].Field!.Name);
        Assert.Equal("Reason", strs[2].Field!.Name);
        Assert.DoesNotContain(s.Tokens, t => t.Field?.Name is "Dup" or "Alt");
        Assert.Equal(nums[1], s.At(nums[1].Offset + 1));
        Assert.Equal(bytes.Length, s.Tokens.Sum(t => t.Length));
    }

    [Fact]
    public void Long_string_two_byte_varints_and_no_prefix_over_a_neighbour()
    {
        var text = new string('q', 200);
        // 01, varint(201) = c9 01, varint(200) = c8 01, then the bytes.
        var bytes = Cat(new byte[] { 0x01, 0xc9, 0x01, 0xc8, 0x01 }, text, new byte[] { 0x00 });
        var s = ByteStream.Build(bytes, null);
        var len = Assert.Single(Of(s, ByteKind.Len));
        Assert.Equal((0, 5, "200"), (len.Offset, len.Length, len.PlainText));
        Assert.Equal(5, Of(s, ByteKind.Str).Single().Offset);

        // "abcd" then a 31-char string whose prefix 0x20 0x1f touches it: the space stays text.
        var second = new string('z', 31);
        bytes = Cat("abcd", new byte[] { 0x20, 0x1f }, second);
        s = ByteStream.Build(bytes, null);
        Assert.Equal(bytes.Length, s.Tokens.Sum(t => t.Length));
        Assert.All(s.Tokens.Where(t => t.Kind == ByteKind.Len), t => Assert.True(t.Offset >= 5));
    }

    [Fact]
    public void Wrap_breaks_strings_and_keeps_offsets()
    {
        const string text = "abcdefghijklmnopqrstuvwxyz0123";   // 30 chars
        var bytes = Cat(new byte[] { 0x01, (byte)(text.Length + 1), (byte)text.Length }, text, new byte[] { 0xff, 0xfe });
        var s = ByteStream.Build(bytes, null);

        var lines = s.Wrap(20);

        // The badge "30" glued to its string, which fills the line and breaks.
        var first = lines[0];
        Assert.Equal(0, first.Offset);
        Assert.Equal([0, 2], first.Parts.Select(p => p.Col));
        Assert.Equal(ByteKind.Len, first.Parts[0].Token.Kind);
        Assert.Equal((0, 18), (first.Parts[1].From, first.Parts[1].To));
        // The rest of the string starts the next line at its own offset; then plain bytes, one space apart.
        Assert.Equal(3 + 18, lines[1].Offset);
        Assert.Equal((18, 30), (lines[1].Parts[0].From, lines[1].Parts[0].To));
        Assert.Equal([0, 14, 17], lines[1].Parts.Select(p => p.Col));
        Assert.All(lines, l => Assert.True(l.Parts.Max(p => p.Col + p.To - p.From) <= 20));
    }

    [Fact]
    public void Wrap_moves_a_fitting_string_whole_to_the_next_line()
    {
        // Two short strings: "9 ghostrole  7 suicide" is 20 columns, one more than the line, so
        // the second goes to the next line whole, badge and all — never "s" / "uicide".
        const string a = "ghostrole", b = "suicide";
        var bytes = Cat(new byte[] { 0x01, (byte)(a.Length + 1), (byte)a.Length }, a,
                        new byte[] { 0x01, (byte)(b.Length + 1), (byte)b.Length }, b);
        var s = ByteStream.Build(bytes, null);

        var lines = s.Wrap(19);

        Assert.Equal(2, lines.Count);
        var second = lines[1];
        Assert.Equal(ByteKind.Len, second.Parts[0].Token.Kind);
        Assert.Equal(0, second.Parts[0].Col);
        var str = second.Parts[1];
        Assert.Equal(ByteKind.Str, str.Token.Kind);
        Assert.Equal((0, b.Length), (str.From, str.To));
        Assert.Equal(second.Parts[0].Token.Offset, second.Offset);
        Assert.DoesNotContain(lines[0].Parts, p => p.Token.Kind == ByteKind.Str && p.Token.PlainText == b);
    }

    [Fact]
    public void Decode_and_type_labels()
    {
        Assert.StartsWith("u32 1873 · f32 ", ByteStream.Decode(BitConverter.GetBytes(1873), 0, 4));
        Assert.Equal("varint 3746 · zigzag 1873 · u16 7586", ByteStream.Decode([0xa2, 0x1d], 0, 2));
        Assert.Equal("u16 7", ByteStream.Decode([7, 0], 0, 2));
        Assert.Equal([0xa2, 0x1d], ByteStream.Varint(ByteStream.ZigZag(1873)));
        Assert.Equal(-5, ByteStream.UnZigZag(ByteStream.ZigZag(-5)));
        Assert.Equal("int?", NetDecode.TypeLabel(typeof(int?)));
        Assert.Equal("List<int>", NetDecode.TypeLabel(typeof(List<int>)));
        Assert.Equal("NetEntity", NetDecode.TypeLabel(typeof(NetEntity)));
        Assert.Empty(ByteStream.Patterns(true));
        Assert.Empty(ByteStream.Patterns((byte)3));
        Assert.Equal([[1, 0, 0, 0]], ByteStream.Patterns(1));   // zigzag 02 is one byte and dropped; the word stays
        Assert.Equal([[0xa2, 0x1d], [0x51, 0x07, 0, 0]], ByteStream.Patterns(new NetEntity(1873)));
    }
}
