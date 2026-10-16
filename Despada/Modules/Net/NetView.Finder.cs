// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using System.Text;
using Despada.Core;
using Despada.ImGui.UserInterface;

namespace Despada.Modules.Net;

// What the finder finds in the network: the fields of every kept packet (or of the open one alone),
// bytes, packets, types. Enter shows the Network section and jumps there.
//
// Fields are searched through an index, as an IDE does: a packet's fields to a depth and a count,
// shallow ones first, lists whole, strings whole, one line each (name, path, type, kind, tone,
// value), made once and kept with the message. A query is a scan of these strings; a hit is a line.
// The tree is built only to show: the inspector's, resolved by the hit's path (paging lists as
// needed). The index of a packet is made the tick after it arrives (IndexPump), a slice at a time,
// or by the search itself when it gets there first. Game states are indexed only when open: their
// thousands of entities times the states kept would say the same thing 32 times.
internal static partial class NetView
{
    private const int SearchNodes = 20_000;
    private const int SearchDepth = 8;
    private const int Beat = 32;   // fields looked at between the nulls that let the finder pause a source
    private const char Sep  = '\u0001';

    private static readonly IFinderSource[] _finderSources = [new FieldsSource(), new BytesSource(), new PacketsSource(), new TypesSource()];

    internal static void RegisterFinder()
    {
        foreach (var s in _finderSources) if (!Finder.Sources.Contains(s)) Finder.Sources.Add(s);
    }

    internal static void UnregisterFinder()
    {
        foreach (var s in _finderSources) Finder.Sources.Remove(s);
        DropIndexes();
    }

    // The packet the scoped tabs search: the open one, only while the Network section is shown.
    private static Packet? ScopePacket() => Finder.Scope is not null && _tabKind == TabKind.Packet ? ActiveDoc() : null;

    // The newest packet's seq: a source's version. Fixed (0) while a packet is open: its content never changes.
    private static long CaptureVersion => ScopePacket() is null ? NetCapture.LastSeq : 0;

    // The kept packets with seqs in (after, until], newest first.
    private static IEnumerable<Packet> PacketsBetween(long after, long until)
    {
        var top = Math.Min(until, NetCapture.LastSeq);
        var low = Math.Max(after, NetCapture.FirstSeq - 1);
        for (var seq = top; seq > low; seq--)
            if (NetCapture.Get(seq) is { } p) yield return p;
    }

    private static string JoinPath(string prefix, string name)
        => prefix.Length == 0 ? name : name.StartsWith('[') ? prefix + name : prefix + "." + name;

    private static string ParentPath(string path)
    {
        var dot = path.LastIndexOf('.');
        var br  = path.LastIndexOf('[');
        var cut = Math.Max(dot, br);
        return cut < 0 ? "" : path[..cut];
    }

    private static bool IsState(Packet p) => p.Type.Name == "MsgState";

    // ── the index ────────────────────────────────────────────────────────────────────────────

    // The indexes are a cache with a budget of their own (in chars, the same figure as the
    // messages' bytes): over it, the oldest packets' go, which only makes a later search slower,
    // never wrong. Main thread only (the search in the render, the pump in the tick).
    private const long IndexChars = NetCapture.KeepBytes;
    private static readonly PriorityQueue<(Packet Packet, int Chars), long> _indexed = new();
    private static long _indexChars;

    // Every field under the root, level by level (shallow fields before any list's depths), with
    // its path; lists whole, through throwaway items, so the tree is never paged or inflated.
    private static IEnumerable<(string Path, FieldNode Node)> WalkAll(FieldNode root, int maxDepth)
    {
        var queue = new Queue<(FieldNode Node, string Path, int Depth)>();
        queue.Enqueue((root, "", 1));
        while (queue.TryDequeue(out var at))
        {
            var kids = at.Node.Value is { } v && NetDecode.IsSequenceValue(v) ? NetDecode.Items(at.Node) : NetDecode.Children(at.Node);
            foreach (var c in kids)
            {
                if (c.PageOf is not null) continue;
                var path = JoinPath(at.Path, c.Name);
                yield return (path, c);
                if (at.Depth < maxDepth && c.CanExpand && c.Value is not string)
                    queue.Enqueue((c, path, at.Depth + 1));
            }
        }
    }

    // One line: name, path, type, kind (s: a string, whole), tone, value.
    private static void AppendLine(StringBuilder sb, string path, FieldNode node)
    {
        var name = node.Name;
        sb.Append(name).Append(Sep).Append(path).Append(Sep).Append(node.TypeName ?? "").Append(Sep);
        if (node.Value is string s)
            sb.Append('s').Append(Sep).Append((char)('0' + (int)Tone.Green)).Append(Sep).Append(s.Replace("\r\n", "\n").Replace('\n', '⏎').Replace('\r', '⏎'));
        else
        {
            var tone = node.ValueText.Length > 0 ? node.ValueText[0].Tone : Tone.Fg;
            sb.Append('v').Append(Sep).Append((char)('0' + (int)tone)).Append(Sep);
            foreach (var span in node.ValueText) sb.Append(span.Text.Replace('\n', '⏎'));
        }
        sb.Append('\n');
    }

    // Makes the packet's index over several steps (a null per Beat fields), the text last.
    private static IEnumerable<string?> IndexSteps(Packet p)
    {
        var sb = new StringBuilder();
        FieldNode? root = null;
        try { root = NetDecode.Root(p); }
        catch (Exception ex) { sb.Append("⚠ ").Append(ex.Message).Append('\n'); }
        if (root is { CanExpand: true })
        {
            var n = 0;
            foreach (var (path, node) in WalkAll(root, SearchDepth))
            {
                if (++n > SearchNodes) break;
                AppendLine(sb, path, node);
                if (n % Beat == 0) yield return null;
            }
        }
        yield return sb.ToString();
    }

    private static void SetIndex(Packet p, string text)
    {
        p.Index = text;
        if (p.Message is null) { p.Index = null; return; }   // dropped meanwhile: nothing outlives the message
        _indexed.Enqueue((p, text.Length), p.Seq);
        _indexChars += text.Length;
        while (_indexChars > IndexChars && _indexed.TryDequeue(out var old, out _))
        {
            _indexChars -= old.Chars;
            old.Packet.Index = null;
        }
    }

    private static void DropIndexes()
    {
        while (_indexed.TryDequeue(out var old, out _)) old.Packet.Index = null;
        _indexChars = 0;
        _pump?.Dispose();
        _pump = null;
    }

    // Indexing on arrival: a millisecond a tick over the packets not yet indexed, oldest first,
    // so a search over a long capture is a scan and not a build.
    private static long _pumpSeq;
    private static Packet? _pumpPacket;
    private static IEnumerator<string?>? _pump;

    internal static void IndexPump()
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started).TotalMilliseconds < 1.0)
        {
            if (_pump is null)
            {
                if (_pumpSeq < NetCapture.FirstSeq) _pumpSeq = NetCapture.FirstSeq;
                if (_pumpSeq > NetCapture.LastSeq) return;
                var p = NetCapture.Get(_pumpSeq++);
                if (p is null || p.Message is null || p.Index is not null || IsState(p)) continue;
                _pumpPacket = p;
                _pump = IndexSteps(p).GetEnumerator();
            }
            bool more;
            try { more = _pump.MoveNext(); }
            catch (Exception ex) { Log.Warn("finder", $"index #{_pumpPacket?.Seq}: {ex.GetType().Name}: {ex.Message}"); more = false; }
            if (!more) { _pump.Dispose(); _pump = null; continue; }
            if (_pump.Current is { } text) { SetIndex(_pumpPacket!, text); _pump.Dispose(); _pump = null; }
        }
    }

    // A line of the index.
    private readonly record struct IndexLine(string Name, string Path, string Type, bool IsString, Tone Tone, string Value)
    {
        public static IndexLine? Parse(ReadOnlySpan<char> line)
        {
            Span<Range> parts = stackalloc Range[6];
            if (line.Split(parts, Sep) < 6) return null;
            var kind = line[parts[3]];
            var tone = line[parts[4]];
            return new IndexLine(line[parts[0]].ToString(), line[parts[1]].ToString(), line[parts[2]].ToString(),
                kind.Length > 0 && kind[0] == 's', tone.Length > 0 ? (Tone)(tone[0] - '0') : Tone.Fg, line[parts[5]].ToString());
        }
    }

    private static IEnumerable<IndexLine> Lines(string index)
    {
        var at = 0;
        while (at < index.Length)
        {
            var end = index.IndexOf('\n', at);
            if (end < 0) end = index.Length;
            if (IndexLine.Parse(index.AsSpan(at, end - at)) is { } line) yield return line;
            at = end + 1;
        }
    }

    // The value as a row shows it: whole when short, else a window around the match; strings quoted.
    private static string Shown(IndexLine l, string query)
    {
        var v = l.Value;
        const int max = 120;
        if (v.Length > max)
        {
            var at = Math.Max(0, v.IndexOf(query, StringComparison.OrdinalIgnoreCase));
            var from = Math.Max(0, at - 30);
            var to = Math.Min(v.Length, from + max);
            v = (from > 0 ? "…" : "") + v[from..to] + (to < v.Length ? "…" : "");
        }
        return l.IsString ? "\"" + v + "\"" : v;
    }

    // ── fields ───────────────────────────────────────────────────────────────────────────────

    private sealed class FieldsSource : IFinderSource
    {
        public string Tab => "Fields";
        public int Rank => 10;
        public long Version => CaptureVersion;

        public IEnumerable<FinderItem?> Search(FinderQuery q)
        {
            var limit = q.Limit;
            if (ScopePacket() is { } open)
            {
                if (q.After > 0) yield break;
                foreach (var it in InPacket(open, q, scoped: true)) { yield return it; if (it is not null && --limit <= 0) yield break; }
                yield break;
            }
            var seen = 0;
            foreach (var p in PacketsBetween(q.After, q.Until))
            {
                if (p.Message is null || IsState(p)) continue;
                if (++seen % Beat == 0) yield return null;
                foreach (var it in InPacket(p, q, scoped: false)) { yield return it; if (it is not null && --limit <= 0) yield break; }
            }
        }

        // The packet's matching lines as hits; brief (the All tab), its best one with a count:
        // a name match before a value match, then the shallower path.
        private static IEnumerable<FinderItem?> InPacket(Packet p, FinderQuery q, bool scoped)
        {
            if (p.Index is null)
                foreach (var step in IndexSteps(p)) { if (step is { } text) SetIndex(p, text); else yield return null; }
            if (p.Index is not { } index || !index.Contains(q.Text, StringComparison.OrdinalIgnoreCase)) yield break;
            IndexLine? best = null;
            var bestScore = int.MaxValue;
            var count = 0;
            foreach (var line in Lines(index))
            {
                var byName  = line.Name.Contains(q.Text, StringComparison.OrdinalIgnoreCase);
                var byValue = line.Value.Contains(q.Text, StringComparison.OrdinalIgnoreCase);
                if (!byName && !byValue) continue;
                count++;
                if (!q.Brief) { yield return Hit(p, line, q.Text, scoped, 0); continue; }
                var score = (byName ? 0 : 1_000_000) + line.Path.Count(c => c is '.' or '[');
                if (score < bestScore) { bestScore = score; best = line; }
            }
            if (best is { } b) yield return Hit(p, b, q.Text, scoped, count - 1);
        }

        private static FinderItem Hit(Packet p, IndexLine line, string query, bool scoped, int more)
        {
            var seq   = p.Seq;
            var path  = line.Path;
            var where = path == line.Name ? "" : path;
            var right = scoped ? line.Type : NetFeature.Time(p);
            if (more > 0) right = $"+{more} · {right}";
            return new FinderItem
            {
                Icon    = Icon.Field,
                Left    = Finder.Highlight(line.Name, query),
                Path    = scoped ? where : where.Length == 0 ? $"#{seq}" : $"#{seq} · {where}",
                Value   = Finder.Highlight(Shown(line, query), query, line.Tone),
                Right   = right,
                Open    = () => RevealField(seq, path),
                Preview = () => FieldPreview(p, path, query),
                Link    = $"#{seq} {path}",
                Action  = "open in Network",
            };
        }
    }

    // The inspector on the packet, its tree opened along the path, the field picked and scrolled to.
    private static void RevealField(long seq, string path)
    {
        Shell.Show(NetCapture.Owner);
        OpenDoc(seq);
        if (NetCapture.Get(seq) is not { } p) return;
        if (_docTreeSeq != seq || _docTree is null)
        {
            _docTree = NetDecode.Root(p);
            _docTreeSeq = seq;
            _related = null;
            ExpandDefault(_docTree, 1);
        }
        var node = Resolve(_docTree, path);
        _showFields = true;
        _selField = node;
        _scrollToField = node;
    }

    // Walks the tree along "A.B[3].C": each step opens its node, a list paged out to the item.
    internal static FieldNode? Resolve(FieldNode root, string path)
    {
        var cur = root;
        var at = 0;
        while (at < path.Length)
        {
            string name;
            if (path[at] == '[')
            {
                var close = path.IndexOf(']', at);
                if (close < 0) return null;
                name = path[at..(close + 1)];
                at = close + 1;
            }
            else
            {
                if (path[at] == '.') at++;
                var end = at;
                while (end < path.Length && path[end] != '.' && path[end] != '[') end++;
                name = path[at..end];
                at = end;
            }
            cur.Expanded = true;
            var kids = NetDecode.Children(cur);
            if (name.StartsWith('[') && int.TryParse(name.AsSpan(1, name.Length - 2), out var i)
                && kids.Count > 0 && kids[^1].PageOf is not null && i >= kids.Count - 1)
            {
                cur.Page = Math.Max(cur.Page, i / NetDecode.PageSize + 1);
                NetDecode.Extend(cur);
                kids = NetDecode.Children(cur);
            }
            if (kids.FirstOrDefault(k => k.Name == name) is not { } next) return cur == root ? null : cur;
            cur = next;
        }
        return cur;
    }

    // The hit among its siblings (the lines under the same parent), lit; the path above.
    private static List<(Span[], bool)> FieldPreview(Packet p, string path, string query)
    {
        var lines  = new List<(Span[], bool)>();
        var parent = ParentPath(path);
        lines.Add(([new Span(parent.Length == 0 ? "fields" : parent.Replace(".", " › "), Tone.Muted)], false));
        if (p.Index is not { } index) { lines.Add(([new Span("fields no longer kept", Tone.Muted)], false)); return lines; }
        var rows = Lines(index).Where(l => ParentPath(l.Path) == parent).ToList();
        var at   = rows.FindIndex(l => l.Path == path);
        var from = Math.Max(0, at - 2);
        for (var i = from; i < Math.Min(rows.Count, from + 6); i++)
        {
            var r = rows[i];
            var spans = new List<Span> { new("  " + r.Name.PadRight(Math.Max(14, r.Name.Length + 2)), Tone.Muted) };
            spans.AddRange(Finder.Highlight(Shown(r, query), i == at ? query : "", r.Tone));
            lines.Add((spans.ToArray(), i == at));
        }
        return lines;
    }

    // ── bytes ────────────────────────────────────────────────────────────────────────────────

    private sealed class BytesSource : IFinderSource
    {
        public string Tab => "Bytes";
        public int Rank => 20;
        public long Version => CaptureVersion;

        public IEnumerable<FinderItem?> Search(FinderQuery fq)
        {
            var (query, limit, after, until, _) = fq;
            var hexq  = query.Replace(" ", "");
            if (hexq.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hexq = hexq[2..];
            var isHex = hexq.Length >= 4 && hexq.Length % 2 == 0 && hexq.All(Uri.IsHexDigit);
            var scope = ScopePacket();
            if (scope is not null && after > 0) yield break;   // the open packet never changes
            if (scope is { } p && _streamSeq == p.Seq && _stream is { } stream)
            {
                var seq = p.Seq;
                byte[]? pattern = isHex ? Convert.FromHexString(hexq) : null;
                var tokens = 0;
                foreach (var t in stream.Tokens)
                {
                    if (++tokens % Beat == 0) yield return null;
                    var text   = t.Kind == ByteKind.Str ? t.PlainText : null;
                    var byText = text is not null && text.Contains(query, StringComparison.OrdinalIgnoreCase);
                    var byHex  = pattern is not null && stream.Bytes.AsSpan(t.Offset, t.Length).IndexOf(pattern) >= 0;
                    if (!byText && !byHex) continue;
                    var token = t;
                    var shown = text ?? HexOfRun(stream.Bytes, t.Offset, Math.Min(t.Length, 12));
                    yield return new FinderItem
                    {
                        Icon    = Icon.Bytes,
                        Left    = [new Span($"0x{t.Offset:x2}", Tone.Sub)],
                        Value   = Finder.Highlight(shown, byText ? query : "", text is not null ? Tone.Green : Tone.Sub),
                        Right   = text is not null ? $"{t.Length} B · string" : $"{t.Length} B · {t.Kind.ToString().ToLowerInvariant()}",
                        Open    = () => RevealBytes(seq, token.Offset, token.End),
                        Preview = () => BytesPreview(stream.Bytes, token.Offset, token.Length, t.Field is { } f ? $"field {f.Name}" : t.Kind.ToString().ToLowerInvariant()),
                        Link    = $"#{seq} 0x{t.Offset:x2}",
                        Action  = "open in Network",
                    };
                    if (--limit <= 0) yield break;
                }
                yield break;
            }
            // Raw bytes (the open packet before its pane has built a stream, else the newest kept
            // packets): as UTF-8 text first, then as hex when the query reads as one; only what
            // the bytes pane can show.
            var utf8  = System.Text.Encoding.UTF8.GetBytes(query);
            var hex   = isHex ? Convert.FromHexString(hexq) : null;
            var seen = 0;
            foreach (var q in scope is { } one ? [one] : PacketsBetween(after, until))
            {
                if (q.Bytes is not { } raw) continue;
                if (++seen % Beat == 0) yield return null;
                var span   = raw.AsSpan(0, Math.Min(raw.Length, MaxStreamBytes));
                var needle = utf8;
                var at     = span.IndexOf(needle);
                if (at < 0 && hex is not null) { needle = hex; at = span.IndexOf(needle); }
                if (at < 0) continue;
                var asText = ReferenceEquals(needle, utf8);
                var seq0 = q.Seq;
                var off  = at;
                var end  = Math.Min(at + needle.Length, MaxStreamBytes);
                var shown = Readable(raw, at, 48);
                yield return new FinderItem
                {
                    Icon    = Icon.Bytes,
                    Left    = [new Span($"0x{at:x2}", Tone.Sub)],
                    Path    = scope is null ? $"#{q.Seq}" : "",
                    Value   = Finder.Highlight(shown, asText ? query : "", Tone.Green),
                    Right   = scope is null ? NetFeature.Time(q) + " · " + (q.Inner ?? q.Type.Name) : $"{needle.Length} B · raw",
                    Open    = () => RevealBytes(seq0, off, end),
                    Preview = () => BytesPreview(raw, off, needle.Length, q.Inner ?? q.Type.Name),
                    Link    = $"#{seq0} 0x{at:x2}",
                    Action  = "open in Network",
                };
                if (--limit <= 0) yield break;
            }
        }
    }

    private static void RevealBytes(long seq, int from, int to)
    {
        Shell.Show(NetCapture.Owner);
        OpenDoc(seq);
        _bytesText = true;
        _selA = from;
        _selB = to;
        _scrollToByte = from;
        _scrollToByteSeq = seq;
    }

    private static string HexOfRun(byte[] bytes, int at, int count)
        => string.Join(' ', bytes.Skip(at).Take(count).Select(b => b.ToString("x2")));

    // The bytes from `at` as text: printable ASCII and UTF-8 characters as they are, anything else a dot.
    private static string Readable(byte[] raw, int at, int max)
    {
        var sb = new System.Text.StringBuilder();
        var end = Math.Min(raw.Length, at + max);
        for (var i = at; i < end; )
        {
            var len = ByteStream.CharLength(raw, i);
            if (len == 0) { sb.Append('·'); i++; continue; }
            sb.Append(System.Text.Encoding.UTF8.GetString(raw, i, Math.Min(len, raw.Length - i)));
            i += len;
        }
        return sb.ToString();
    }

    // A hex dump of the rows around the hit (offset · 16 bytes · their text), the hit's bytes in the accent.
    private static List<(Span[], bool)> BytesPreview(byte[] bytes, int at, int length, string what)
    {
        var end   = Math.Min(bytes.Length, at + length);
        var range = length == 1 ? $"0x{at:x2}" : $"0x{at:x2}–0x{end - 1:x2}";
        var lines = new List<(Span[], bool)> { ([new Span($"{range} · {length} B · {what}", Tone.Muted)], false) };
        const int perRow = 16, rows = 6;
        var first = Math.Max(0, at / perRow - 2) * perRow;
        for (var row = first; row < Math.Min(bytes.Length, first + rows * perRow); row += perRow)
        {
            var spans = new List<Span> { new($"{row:x4}  ", Tone.Muted) };
            var text  = new System.Text.StringBuilder();
            var hex   = new System.Text.StringBuilder();
            var inHit = false;
            for (var i = row; i < row + perRow; i++)
            {
                var hit = i >= at && i < end;
                if (i < bytes.Length && hit != inHit)
                {
                    if (hex.Length > 0) { spans.Add(new Span(hex.ToString(), inHit ? Tone.Accent2 : Tone.Sub)); hex.Clear(); }
                    inHit = hit;
                }
                hex.Append(i < bytes.Length ? bytes[i].ToString("x2") : "  ").Append(i == row + 7 ? "  " : " ");
                text.Append(i < bytes.Length ? bytes[i] is >= 0x20 and < 0x7f ? (char)bytes[i] : '·' : ' ');
            }
            spans.Add(new Span(hex.ToString(), inHit ? Tone.Accent2 : Tone.Sub));
            spans.Add(new Span("  " + text, Tone.Green));
            lines.Add((spans.ToArray(), at >= row && at < row + perRow));
        }
        return lines;
    }

    // ── packets ──────────────────────────────────────────────────────────────────────────────

    private sealed class PacketsSource : IFinderSource
    {
        public string Tab => "Packets";
        public int Rank => 30;
        public long Version => NetCapture.LastSeq;

        public IEnumerable<FinderItem?> Search(FinderQuery fq)
        {
            var (query, limit, after, until, _) = fq;
            var seen = 0;
            foreach (var p in PacketsBetween(after, until))
            {
                if (++seen % Beat == 0) yield return null;
                var type = p.Inner ?? p.Type.Name;
                var summary = NetDecode.Summary(p);
                if (!type.Contains(query, StringComparison.OrdinalIgnoreCase) && !p.Type.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    && !summary.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                var seq = p.Seq;
                yield return new FinderItem
                {
                    Icon     = p.Dir == NetDir.In ? Icon.Down : Icon.Up,
                    IconTone = p.Dir == NetDir.In ? Tone.Accent : Tone.Accent2,
                    Left     = Finder.Highlight(type, query),
                    Path     = $"#{p.Seq}",
                    Value    = Finder.Highlight(summary, query, Tone.Muted),
                    Right    = NetFeature.Time(p) + " · " + NetFeature.Bytes(p.Size),
                    Open     = () => { Shell.Show(NetCapture.Owner); OpenDoc(seq); },
                    Preview  = () => PacketPreview(p, query),
                    Link     = $"#{seq}",
                    Action   = "open in Network",
                };
                if (--limit <= 0) yield break;
            }
        }
    }

    // The packet's line, then up to six of its indexed fields that hold the query.
    private static List<(Span[], bool)> PacketPreview(Packet p, string query)
    {
        var lines = new List<(Span[], bool)>
        {
            ([new Span($"{NetFeature.Time(p)} · {NetFeature.Bytes(p.Size)} · tick {p.Tick} · {(p.Dir == NetDir.In ? "in" : "out")}", Tone.Muted)], false),
        };
        if (p.Message is null) { lines.Add(([new Span("fields no longer kept", Tone.Muted)], false)); return lines; }
        if (p.Index is not { } index) { lines.Add(([new Span(IsState(p) ? "a game state: open it to search its fields" : "not indexed yet", Tone.Muted)], false)); return lines; }
        var shown = 0;
        foreach (var l in Lines(index))
        {
            if (shown >= 6) break;
            if (!l.Name.Contains(query, StringComparison.OrdinalIgnoreCase) && !l.Value.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            lines.Add(([new Span("  " + l.Path.PadRight(Math.Max(20, l.Path.Length + 2)), Tone.Sub), .. Finder.Highlight(Shown(l, query), query, l.Tone)], false));
            shown++;
        }
        if (shown == 0) lines.Add(([new Span("  (in the summary only)", Tone.Muted)], false));
        return lines;
    }

    // ── types ────────────────────────────────────────────────────────────────────────────────

    // The type's counts, then its latest packets (seq · time · size · summary), a few thousand packets back at most.
    private static List<(Span[], bool)> TypePreview(string type, NetTypes.Kind kind)
    {
        var (kept, perSec, total) = NetCapture.TypeStats(type);
        var lines = new List<(Span[], bool)>
        {
            ([new Span($"{kind.ToString().ToLowerInvariant()} · {total:N0} captured · {kept:N0} kept · {perSec}/s", Tone.Muted)], false),
        };
        var scanned = 0;
        foreach (var p in PacketsBetween(0, long.MaxValue))
        {
            if (++scanned > 5000 || lines.Count > 6) break;
            if ((p.Inner ?? p.Type.Name) != type) continue;
            lines.Add(([new Span($"  #{p.Seq}".PadRight(10), Tone.Sub), new Span($"{NetFeature.Time(p)} · {NetFeature.Bytes(p.Size),-8} ", Tone.Muted), new Span(NetDecode.Summary(p), Tone.Fg)], false));
        }
        if (lines.Count == 1) lines.Add(([new Span("  none in the capture", Tone.Muted)], false));
        return lines;
    }

    private sealed class TypesSource : IFinderSource
    {
        public string Tab => "Types";
        public int Rank => 40;

        public IEnumerable<FinderItem?> Search(FinderQuery fq)
        {
            var (query, limit, after, until, _) = fq;
            foreach (var (name, kind) in NetTypes.All)
            {
                if (!name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                var hint = NetCapture.TypeHint(name);
                var type = name;
                yield return new FinderItem
                {
                    Icon   = Icon.Diamond,
                    Left   = Finder.Highlight(name, query),
                    Right  = hint.Length > 0 ? $"{kind.ToString().ToLowerInvariant()} · {hint}" : kind.ToString().ToLowerInvariant(),
                    Open   = () => { Shell.Show(NetCapture.Owner); AddFilter($"type:={type}"); },
                    Preview = () => TypePreview(type, kind),
                    Link   = $"type:={name}",
                    Action = "filter Network by type",
                };
                if (--limit <= 0) yield break;
            }
        }
    }
}
