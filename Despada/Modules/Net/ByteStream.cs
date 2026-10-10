// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Text;
using Despada.ImGui.UserInterface;

namespace Despada.Modules.Net;

internal enum ByteKind
{
    Hex,    // one byte, nothing known about it
    Zero,   // a run of zero bytes, shown folded: 00 ×N
    Len,    // the length prefix NetSerializer writes before a string (chars+1, then bytes)
    Str,    // a printable run, shown as text
    Num,    // bytes that hold a field's value (matched by value, so approximate)
}

/// <summary>A run of a packet's bytes as the stream shows it.</summary>
internal sealed class ByteToken(ByteKind kind, int offset, int length)
{
    public ByteKind Kind   { get; } = kind;
    public int      Offset { get; } = offset;
    public int      Length { get; } = length;
    public int      End => Offset + Length;

    /// <summary>What is drawn: the text parts of a string, "00 ×7", the hex of a number…</summary>
    public Span[] Text { get; set; } = [];
    /// <summary>The field this run holds (Num, and a Str that is a field's value).</summary>
    public FieldNode? Field { get; set; }
    public string? FieldValue { get; set; }

    public string PlainText => string.Concat(Text.Select(s => s.Text));
}

/// <summary>One visual line of the wrapped stream: the offset of its first byte and its pieces.</summary>
internal sealed class ByteLine(int offset)
{
    public int Offset { get; } = offset;
    /// <summary>A token, or a slice of a string token's characters [From, To).</summary>
    public List<(ByteToken Token, int From, int To, int Col)> Parts { get; } = new();
}

/// <summary>
/// A packet's bytes read for people: printable runs as text (type names split into namespace,
/// name and assembly), zero runs folded, a string's length prefix as a badge, and the bytes that
/// hold a field's value found by searching for that value (unique matches only). Pure: built once
/// per packet, wrapped per width.
/// </summary>
internal sealed class ByteStream
{
    public const int MinString = 4;
    public const int MinZeros  = 3;
    private const int MatchDepth = 3;

    public byte[]          Bytes   { get; }
    public List<ByteToken> Tokens  { get; } = new();
    public int Strings { get; private set; }
    public int Matched { get; private set; }

    // Token covering each byte (null: a plain Hex token's byte, or none yet while building).
    private readonly ByteToken?[] _at;

    private ByteStream(byte[] bytes)
    {
        Bytes = bytes;
        _at = new ByteToken?[bytes.Length];
    }

    /// <summary>The token holding byte <paramref name="offset"/>.</summary>
    public ByteToken? At(int offset) => offset >= 0 && offset < _at.Length ? _at[offset] : null;

    public static ByteStream Build(byte[] bytes, FieldNode? fields)
    {
        var s = new ByteStream(bytes);
        s.FindStrings();
        if (fields is not null) s.MatchFields(fields);
        s.FoldZeros();
        s.FillHex();
        s.Tokens.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return s;
    }

    // ── strings ──────────────────────────────────────────────────────────────────────────────

    public static bool Printable(byte b) => b is >= 32 and < 127;

    private void FindStrings()
    {
        var n = Bytes.Length;
        var i = 0;
        while (i < n)
        {
            if (!Printable(Bytes[i])) { i++; continue; }
            var j = i;
            while (j < n && Printable(Bytes[j])) j++;
            if (j - i >= MinString) AddString(i, j);
            i = j;
        }
    }

    // A printable run [a, b) with its length prefix peeled off. A string NetSerializer writes
    // outside the mapped table is 01, varint(utf8 bytes + 1), varint(chars), the bytes
    // (RobustMappedStringSerializer.WriteMappedString → Primitives.WritePrimitive(string)); the
    // varint bytes are printable from 32 on, so they may sit inside the run. Only that exact
    // shape counts: a lone byte equal to the length would often be a coincidence.
    private void AddString(int a, int b)
    {
        for (var p = Math.Max(0, a - 6); p <= a + 4 && p < b; p++)
        {
            if (!ReadVarint(p, out var v1, out var n1) || !ReadVarint(p + n1, out var v2, out var n2)) continue;
            var start = p + n1 + n2;
            if (start < a || start >= b) continue;
            var len = b - start;
            if (v1 != (uint)len + 1 || v2 != (uint)len) continue;
            var from = p > 0 && Bytes[p - 1] == 1 && _at[p - 1] is null ? p - 1 : p;   // the "unmapped" marker
            if (Overlaps(from, start - from)) continue;   // a prefix byte that ends the run before
            Add(new ByteToken(ByteKind.Len, from, start - from) { Text = [new Span(len.ToString(), Tone.Muted)] });
            a = start;
            break;
        }

        var text = Encoding.ASCII.GetString(Bytes, a, b - a);
        Add(new ByteToken(ByteKind.Str, a, b - a) { Text = Classify(text) });
        Strings++;
    }

    /// <summary>A 7-bit varint at <paramref name="at"/> (NetSerializer and Lidgren write the same form).</summary>
    public bool ReadVarint(int at, out uint value, out int length)
    {
        value = 0;
        length = 0;
        for (var shift = 0; shift <= 28 && at + length < Bytes.Length; shift += 7)
        {
            var b = Bytes[at + length++];
            value |= (uint)(b & 0x7f) << shift;
            if ((b & 0x80) == 0) return true;
        }
        return false;
    }

    public static byte[] Varint(uint v)
    {
        var list = new List<byte>(5);
        for (; v >= 0x80u; v >>= 7) list.Add((byte)(v | 0x80u));
        list.Add((byte)v);
        return list.ToArray();
    }

    public static byte[] Varint(ulong v)
    {
        var list = new List<byte>(10);
        for (; v >= 0x80u; v >>= 7) list.Add((byte)(v | 0x80u));
        list.Add((byte)v);
        return list.ToArray();
    }

    public static uint  ZigZag(int n)  => (uint)((n << 1) ^ (n >> 31));
    public static ulong ZigZag(long n) => (ulong)((n << 1) ^ (n >> 63));
    public static int   UnZigZag(uint v) => (int)(v >> 1) ^ -(int)(v & 1);

    /// <summary>
    /// A type name "Ns.Sub.Name, Assembly" in three tones (the name stands out), a resource path
    /// in the link tone, any other text green.
    /// </summary>
    public static Span[] Classify(string s)
    {
        if (s.Length > 0 && s[0] == '/') return [new Span(s, Tone.Link)];
        var comma = s.IndexOf(", ", StringComparison.Ordinal);
        var head  = comma < 0 ? s : s[..comma];
        var dot   = head.LastIndexOf('.');
        if (dot > 0 && dot < head.Length - 1 && IsIdentifierPath(head))
        {
            var spans = new List<Span> { new(head[..(dot + 1)], Tone.Muted), new(head[(dot + 1)..], Tone.Accent2) };
            if (comma >= 0) spans.Add(new Span(s[comma..], Tone.Muted));
            return spans.ToArray();
        }
        return [new Span(s, Tone.Green)];
    }

    private static bool IsIdentifierPath(string s)
    {
        if (s.Length == 0 || !(char.IsLetter(s[0]) || s[0] == '_')) return false;
        var lastDot = true;
        foreach (var c in s)
        {
            if (c == '.') { if (lastDot) return false; lastDot = true; continue; }
            if (!(char.IsLetterOrDigit(c) || c is '_' or '`' or '+')) return false;
            lastDot = false;
        }
        return !lastDot;
    }

    // ── fields ───────────────────────────────────────────────────────────────────────────────

    private void MatchFields(FieldNode root)
    {
        var candidates = new List<(FieldNode Node, byte[] Pattern, string Value)>();
        Collect(root, 1, candidates);
        var done = new HashSet<FieldNode>();
        // Longer patterns first: they are the surer matches and may contain shorter ones.
        foreach (var (node, pattern, value) in candidates.OrderByDescending(c => c.Pattern.Length))
        {
            if (done.Contains(node)) continue;
            var at = UniqueIndexOf(pattern);
            if (at < 0) continue;
            done.Add(node);
            // A string found as text keeps its token and gains the field; a longer run that holds
            // it is split around it, so the field's bytes light up exactly.
            if (_at[at] is { Kind: ByteKind.Str } str && str.Offset <= at && str.End >= at + pattern.Length)
            {
                if (str.Field is not null) continue;
                if (str.Offset != at || str.Length != pattern.Length) str = Split(str, at, pattern.Length);
                str.Field = node;
                str.FieldValue = value;
                Matched++;
                continue;
            }
            if (Overlaps(at, pattern.Length)) continue;
            var hex = string.Join(' ', pattern.Select(b => b.ToString("x2")));
            Add(new ByteToken(ByteKind.Num, at, pattern.Length) { Text = [new Span(hex, Tone.Sub)], Field = node, FieldValue = value });
            Matched++;
        }
    }

    // The string token cut into [before][middle][after] (empty pieces left out); the middle is returned.
    private ByteToken Split(ByteToken str, int at, int length)
    {
        Tokens.Remove(str);
        ByteToken Piece(int from, int to)
        {
            var t = new ByteToken(ByteKind.Str, from, to - from) { Text = Classify(Encoding.ASCII.GetString(Bytes, from, to - from)) };
            Add(t);
            return t;
        }
        if (at > str.Offset) Piece(str.Offset, at);
        var mid = Piece(at, at + length);
        if (at + length < str.End) Piece(at + length, str.End);
        return mid;
    }

    private void Collect(FieldNode node, int depth, List<(FieldNode, byte[], string)> list)
    {
        if (depth > MatchDepth || !node.CanExpand) return;
        foreach (var c in NetDecode.Children(node))
        {
            var patterns = Patterns(c.Value);
            if (patterns.Count > 0)
                foreach (var p in patterns) list.Add((c, p, string.Concat(c.ValueText.Select(s => s.Text)).Trim()));
            else if (c.CanExpand && c.Value is not string) Collect(c, depth + 1, list);
        }
    }

    /// <summary>
    /// The byte forms a value may take on the wire, when they are worth searching for: NetSerializer
    /// writes integers as 7-bit varints (signed ones zigzagged) and floats raw, Lidgren's own header
    /// fields (tick, sequence) are plain little-endian. One byte would match anywhere, so bools,
    /// bytes and small varints are left out.
    /// </summary>
    public static List<byte[]> Patterns(object? v)
    {
        var list = new List<byte[]>();
        void Int(int x)   { list.Add(Varint(ZigZag(x))); list.Add(BitConverter.GetBytes(x)); }
        void UInt(uint x) { list.Add(Varint(x)); list.Add(BitConverter.GetBytes(x)); }
        switch (v)
        {
            case null or bool or byte or sbyte: break;
            case string s: if (s.Length >= 2) list.Add(Encoding.UTF8.GetBytes(s)); break;
            case short x:  Int(x); break;
            case ushort x: UInt(x); break;
            case int x:    Int(x); break;
            case uint x:   UInt(x); break;
            case long x:   list.Add(Varint(ZigZag(x))); list.Add(BitConverter.GetBytes(x)); break;
            case ulong x:  list.Add(Varint(x)); list.Add(BitConverter.GetBytes(x)); break;
            case float x:  list.Add(BitConverter.GetBytes(x)); break;
            case double x: list.Add(BitConverter.GetBytes(x)); break;
            default:
                switch (v.GetType().Name)
                {
                    case "NetEntity": if (NetDecode.Read(v, "Id") is int id && id != 0) Int(id); break;
                    case "GameTick":  if (NetDecode.Read(v, "Value") is uint t && t != 0) UInt(t); break;
                    case "Vector2":
                        if (NetDecode.Read(v, "X") is float x2 && NetDecode.Read(v, "Y") is float y2)
                            list.Add([.. BitConverter.GetBytes(x2), .. BitConverter.GetBytes(y2)]);
                        break;
                }
                break;
        }
        list.RemoveAll(p => p.Length < 2);
        return list;
    }

    // The one place the pattern occurs, or -1 when it is absent or occurs more than once (a small
    // number would match anywhere).
    private int UniqueIndexOf(byte[] pattern)
    {
        var span = Bytes.AsSpan();
        var first = span.IndexOf(pattern);
        if (first < 0) return -1;
        var again = span[(first + 1)..].IndexOf(pattern);
        return again < 0 ? first : -1;
    }

    // ── the rest ─────────────────────────────────────────────────────────────────────────────

    private void FoldZeros()
    {
        var n = Bytes.Length;
        var i = 0;
        while (i < n)
        {
            if (Bytes[i] != 0 || _at[i] is not null) { i++; continue; }
            var j = i;
            while (j < n && Bytes[j] == 0 && _at[j] is null) j++;
            if (j - i >= MinZeros) Add(new ByteToken(ByteKind.Zero, i, j - i) { Text = [new Span($"00 ×{j - i}", Tone.Muted)] });
            i = j;
        }
    }

    private void FillHex()
    {
        for (var i = 0; i < Bytes.Length; i++)
            if (_at[i] is null)
                Add(new ByteToken(ByteKind.Hex, i, 1) { Text = [new Span(Bytes[i].ToString("x2"), Tone.Muted)] });
    }

    private bool Overlaps(int at, int length)
    {
        for (var i = at; i < at + length; i++)
            if (_at[i] is not null) return true;
        return false;
    }

    private void Add(ByteToken t)
    {
        Tokens.Add(t);
        for (var i = t.Offset; i < t.End; i++) _at[i] = t;
    }

    // ── layout ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The stream wrapped to <paramref name="cols"/> characters: tokens are separated by two
    /// spaces (one between plain bytes, none between a length badge and its string), a string
    /// breaks wherever the line ends, anything else moves to the next line whole.
    /// </summary>
    public List<ByteLine> Wrap(int cols)
    {
        cols = Math.Max(cols, 8);
        var lines = new List<ByteLine>();
        ByteLine? line = null;
        var col = 0;
        ByteToken? prev = null;

        void NewLine(int offset)
        {
            line = new ByteLine(offset);
            lines.Add(line);
            col = 0;
        }

        foreach (var t in Tokens)
        {
            var gap = prev is null ? 0
                    : prev.Kind == ByteKind.Len && t.Kind == ByteKind.Str ? 0
                    : prev.Kind == ByteKind.Hex && t.Kind == ByteKind.Hex ? 1
                    : 2;
            if (t.Kind == ByteKind.Str)
            {
                var text = t.PlainText;
                var from = 0;
                // A string that fits on one line goes whole to the next when it doesn't fit here
                // ("suicide", not "s" / "uicide"); its length badge moves along with it. Only a
                // string longer than a line is broken.
                var fits  = text.Length <= cols;
                var badge = prev is { Kind: ByteKind.Len } && line is not null && line.Parts.Count > 0 && ReferenceEquals(line.Parts[^1].Token, prev) ? prev : null;
                if (line is null || col + gap >= cols || (fits && col + gap + text.Length > cols))
                {
                    if (badge is not null) line!.Parts.RemoveAt(line.Parts.Count - 1);
                    NewLine(badge?.Offset ?? t.Offset);
                    if (badge is not null)
                    {
                        line!.Parts.Add((badge, 0, badge.PlainText.Length, 0));
                        col = badge.PlainText.Length;
                    }
                }
                else col += gap;
                while (from < text.Length)
                {
                    if (col >= cols) NewLine(t.Offset + from);
                    var take = Math.Min(cols - col, text.Length - from);
                    line!.Parts.Add((t, from, from + take, col));
                    col  += take;
                    from += take;
                }
            }
            else
            {
                var w = t.PlainText.Length;
                var need = t.Kind == ByteKind.Len ? w + 1 : w;   // a badge keeps at least a character of its string
                if (line is null || col + gap + need > cols) NewLine(t.Offset);
                else col += gap;
                line!.Parts.Add((t, 0, w, col));
                col += w;
            }
            prev = t;
        }
        return lines;
    }

    // ── decoding a selection ─────────────────────────────────────────────────────────────────

    /// <summary>The readings of the bytes at <paramref name="offset"/> that fit: varint (as NetSerializer writes integers), then little-endian words.</summary>
    public static string Decode(byte[] bytes, int offset, int length)
    {
        var span = bytes.AsSpan(offset, Math.Min(length, bytes.Length - offset));
        var parts = new List<string>();
        var vi = 0u;
        var ok = false;
        for (int i = 0, shift = 0; i < span.Length && shift <= 28; i++, shift += 7)
        {
            vi |= (uint)(span[i] & 0x7f) << shift;
            if ((span[i] & 0x80) == 0) { ok = i + 1 == length || length > 4; break; }
        }
        if (ok) parts.Add($"varint {vi} · zigzag {UnZigZag(vi)}");
        if (span.Length >= 4)
        {
            parts.Add("u32 " + BitConverter.ToUInt32(span));
            var i = BitConverter.ToInt32(span);
            if (i < 0) parts.Add("i32 " + i);
            parts.Add("f32 " + BitConverter.ToSingle(span).ToString("g4", CultureInfo.InvariantCulture));
        }
        else if (span.Length >= 2) parts.Add("u16 " + BitConverter.ToUInt16(span));
        else if (span.Length == 1) parts.Add("u8 " + span[0]);
        if (span.Length >= 8) parts.Add("f64 " + BitConverter.ToDouble(span).ToString("g6", CultureInfo.InvariantCulture));
        return string.Join(" · ", parts);
    }
}
