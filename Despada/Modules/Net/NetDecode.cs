// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Despada.Core;
using Despada.ImGui.UserInterface;

namespace Despada.Modules.Net;

/// <summary>A field in a packet's tree; children are read only when it is expanded.</summary>
internal sealed class FieldNode(string name, object? value, int depth)
{
    public string  Name  { get; } = name;
    public object? Value { get; } = value;
    public int     Depth { get; } = depth;
    public Span[]  Text  { get; set; } = [];
    /// <summary>The value's part of <see cref="Text"/> (the inspector's table draws name and value apart).</summary>
    public Span[]  ValueText { get; set; } = [];
    /// <summary>The declared type of the field (the item type for a sequence item), for the table's type column.</summary>
    public string? TypeName  { get; set; }
    public bool    CanExpand { get; set; }
    public bool    Expanded  { get; set; }
    public List<FieldNode>? Children { get; set; }
    /// <summary>How many pages of items are shown (int.MaxValue: all).</summary>
    public int Page { get; set; } = 1;
    /// <summary>A "… N more" row: the node whose next page it opens.</summary>
    public FieldNode? PageOf { get; set; }

    public void ShowMore(bool all)
    {
        Page = all ? int.MaxValue : Page + 1;
        NetDecode.Extend(this);
    }
}

/// <summary>
/// Turns captured messages into text: a one-line summary per packet, and a lazily expanded field
/// tree with readable forms for engine types. Only fields are read (what NetSerializer puts on the
/// wire): property getters could run engine code, block or have side effects.
/// </summary>
internal static class NetDecode
{
    private const int MaxDepth = 64;   // children are built only on demand: the limit guards a self-referencing object, nothing else
    internal const int PageSize = 100;

    // Engine plumbing that only leads away from the message itself.
    private static readonly HashSet<string> Skip =
        ["MsgChannel", "DeliveryMethod", "SequenceChannel", "MsgGroup", "MsgName", "MsgSize", "StateStream", "CompressionContext"];

    // Summaries can be made off the game thread (when an old packet lets go of its object).
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> _fields = new();
    private static readonly ConcurrentDictionary<(Type, string), FieldInfo?> _named = new();

    // ── summary ──────────────────────────────────────────────────────────────────────────────

    public static string Summary(Packet p)
    {
        if (p.Summary is not null) return p.Summary;
        var msg = p.Message; // may be dropped concurrently
        try
        {
            p.Summary = msg is null ? "" : OneLine(SummaryOf(p.Type.Name, msg));
        }
        catch (Exception ex)
        {
            p.Summary = $"⚠ {ex.GetType().Name}";
        }
        return p.Summary;
    }

    private static string SummaryOf(string type, object msg)
    {
        switch (type)
        {
            case "MsgEntity":
                var ev = Engine.MsgEntitySystem.Ok ? Engine.MsgEntitySystem.Invoke(msg) : null;
                if (ev is null) return "";
                // BUI messages arrive wrapped; show what's inside.
                if (ev.GetType().Name.Contains("BoundUIWrapMessage") && Read(ev, "Message") is { } inner)
                    return "BUI " + inner.GetType().Name;
                return ev.GetType().Name + EventHint(ev);

            case "MsgState":
                if (!Engine.MsgStateState.Ok || !Engine.GameStateTo.Ok || !Engine.GameStateEnts.Ok) return "";
                var state = Engine.MsgStateState.Get(msg);
                if (state is null) return "";
                var to   = Engine.GameStateTo.Get(state);
                var ents = Engine.GameStateEnts.Get(state);
                var n    = ents is null ? 0 : Count(Read(ents, "Value"));
                return $"tick {TickValue(to)} · {n} entities";

            default:
                // The first text field, here or one object down (chat: Message.Message).
                return FirstText(msg, 2) is { } s ? (s.Length > 60 ? s[..60] + "…" : s) : "";
        }
    }

    private static string? FirstText(object o, int depth)
    {
        foreach (var f in Fields(o.GetType()))
            if (f.GetValue(o) is string { Length: > 0 } s) return s;
        if (depth <= 1) return null;
        foreach (var f in Fields(o.GetType()))
            if (!f.FieldType.IsValueType && f.FieldType != typeof(string) && f.GetValue(o) is { } inner && !IsSequence(inner)
                && FirstText(inner, depth - 1) is { } s)
                return s;
        return null;
    }

    /// <summary>NetEntity ids in a MsgEntity's event (a few levels deep, at most 8).</summary>
    public static int[]? EntityIds(object msg)
    {
        try
        {
            var ev = Engine.MsgEntitySystem.Ok ? Engine.MsgEntitySystem.Invoke(msg) : null;
            return ev is null ? null : EntityIdsOf(ev);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>NetEntity ids in an event's fields (3 levels deep, at most 8).</summary>
    internal static int[]? EntityIdsOf(object ev)
    {
        var ids = new List<int>();
        CollectEntities(ev, 3, ids);
        return ids.Count == 0 ? null : ids.ToArray();
    }

    private static void CollectEntities(object o, int depth, List<int> ids)
    {
        foreach (var f in Fields(o.GetType()))
        {
            if (ids.Count >= 8) return;
            // The value's runtime type, so NetEntity? fields (boxed as NetEntity or null) count too.
            var t = f.FieldType;
            if (t == typeof(string) || t.IsPrimitive || t.IsEnum || f.GetValue(o) is not { } v) continue;
            if (v.GetType().Name == "NetEntity")
            {
                if (Read(v, "Id") is int id && id != 0 && !ids.Contains(id)) ids.Add(id);
            }
            else if (IsSequence(v))
            {
                // Lists of entities (one level, the first few items).
                if (v is IEnumerable items)
                    foreach (var item in items.Cast<object?>().Take(16))
                        if (item?.GetType().Name == "NetEntity" && Read(item, "Id") is int id && id != 0 && !ids.Contains(id))
                            ids.Add(id);
            }
            else if (depth > 1 && v is not Delegate)
                CollectEntities(v, depth - 1, ids);
        }
    }

    /// <summary>The tree as indented text (for copying), unfolding up to <paramref name="maxDepth"/>.</summary>
    public static string ToText(FieldNode root, int maxDepth = 4)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Concat(root.Text.Select(s => s.Text)));
        void Walk(FieldNode n, int depth)
        {
            foreach (var c in Children(n))
            {
                sb.Append(' ', depth * 2).AppendLine(string.Concat(c.Text.Select(s => s.Text)).TrimEnd());
                if (c.CanExpand && depth < maxDepth) Walk(c, depth + 1);
            }
        }
        if (root.CanExpand) Walk(root, 1);
        return sb.ToString();
    }

    /// <summary>Classic hex dump: offset, 16 bytes, ASCII.</summary>
    public static IEnumerable<(string Offset, string Hex, string Ascii)> HexLines(byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i += 16)
        {
            var row = bytes.AsSpan(i, Math.Min(16, bytes.Length - i));
            var hex = new System.Text.StringBuilder();
            var asc = new System.Text.StringBuilder();
            for (var j = 0; j < 16; j++)
            {
                if (j == 8) hex.Append(' ');
                if (j < row.Length)
                {
                    hex.Append(row[j].ToString("x2")).Append(' ');
                    asc.Append(row[j] is >= 32 and < 127 ? (char)row[j] : '.');
                }
                else hex.Append("   ");
            }
            yield return (i.ToString("x4"), hex.ToString(), asc.ToString());
        }
    }

    /// <summary>For MsgEntity: the event's type name (the inner message's for BUI wrappers).</summary>
    public static string? InnerType(object msg)
    {
        try
        {
            var ev = Engine.MsgEntitySystem.Ok ? Engine.MsgEntitySystem.Invoke(msg) : null;
            if (ev is null) return null;
            if (ev.GetType().Name.Contains("BoundUIWrapMessage") && Read(ev, "Message") is { } inner)
                return "BUI." + inner.GetType().Name;
            return ev.GetType().Name;
        }
        catch
        {
            return null;
        }
    }

    // A target entity is the most useful thing to see for interaction events.
    private static string EventHint(object ev)
    {
        foreach (var name in new[] { "Target", "Used", "Entity" })
            if (Read(ev, name) is { } v && v.GetType().Name == "NetEntity")
                return " → " + Read(v, "Id");
        return "";
    }

    // ── field tree ───────────────────────────────────────────────────────────────────────────

    public static FieldNode Root(Packet p)
    {
        var node = new FieldNode(p.Type.Name, p.Message, 0);
        Describe(node);
        if (p.Message is null)
        {
            node.Text = [new Span(p.Type.Name, Tone.Accent), new Span("  (message no longer kept)", Tone.Muted)];
            node.CanExpand = false;
        }
        return node;
    }

    /// <summary>The next page of a sequence's items appended to the children already shown (they keep their state).</summary>
    internal static void Extend(FieldNode node)
    {
        if (node.Children is not { } list || node.Value is null || !IsSequence(node.Value)) return;
        if (list.Count > 0 && list[^1].PageOf is not null) list.RemoveAt(list.Count - 1);
        var limit = node.Page == int.MaxValue ? int.MaxValue : PageSize * node.Page;
        try
        {
            var i = 0;
            foreach (var item in (IEnumerable)node.Value)
            {
                if (i == limit) { list.Add(MoreRow(node, limit)); break; }
                if (i >= list.Count) list.Add(Child($"[{i}]", item, node.Depth + 1, type: ItemType(node.Value.GetType())));
                i++;
            }
        }
        catch (Exception ex)
        {
            list.Add(ErrorRow(ex.Message, node.Depth + 1));
        }
    }

    // "… N more": a link in the table, plain in the tree (which can't page).
    private static FieldNode MoreRow(FieldNode of, int shown)
    {
        var left = $"… {Count(of.Value) - shown:N0} more";
        return new FieldNode("", null, of.Depth + 1) { Text = [new Span(left, Tone.Muted)], ValueText = [new Span(left, Tone.Accent)], PageOf = of };
    }

    private static FieldNode ErrorRow(string message, int depth)
    {
        var span = new Span("⚠ " + OneLine(message), Tone.Red);
        return new FieldNode("⚠", null, depth) { Text = [span], ValueText = [span] };
    }

    /// <summary>Every item of a sequence as throwaway nodes, unpaged, not kept on the node (for the finder's index).</summary>
    internal static IEnumerable<FieldNode> Items(FieldNode node)
    {
        if (node.Value is not { } v || !IsSequence(v)) yield break;
        if (node.Depth >= MaxDepth) yield break;
        IEnumerator? e = null;
        string? error = null;
        try { e = ((IEnumerable)v).GetEnumerator(); }
        catch (Exception ex) { error = ex.Message; }
        var type = ItemType(v.GetType());
        var i = 0;
        while (error is null)
        {
            object? item = null;
            var more = false;
            try { more = e!.MoveNext(); if (more) item = e.Current; }
            catch (Exception ex) { error = ex.Message; }
            if (error is not null || !more) break;
            yield return Child($"[{i++}]", item, node.Depth + 1, type: type);
        }
        if (error is not null) yield return ErrorRow(error, node.Depth + 1);
    }

    /// <summary>Children of an expandable node, read on first use.</summary>
    public static List<FieldNode> Children(FieldNode node)
    {
        if (node.Children is not null) return node.Children;
        var list = new List<FieldNode>();
        try
        {
            if (node.Value is string text)
            {
                const int maxLines = PageSize * 5;
                var lines = StringLines(text, maxLines + 1);
                foreach (var line in lines.Take(maxLines))
                {
                    var span = new Span(line.Length == 0 ? " " : line, Tone.Green);
                    list.Add(new FieldNode("", null, node.Depth + 1) { Text = [span], ValueText = [span] });
                }
                if (lines.Count > maxLines)
                {
                    var more = new Span($"… {LineCount(text) - maxLines} more lines", Tone.Muted);
                    list.Add(new FieldNode("", null, node.Depth + 1) { Text = [more], ValueText = [more] });
                }
            }
            else if (node.Value is not null && node.Depth >= MaxDepth)
                list.Add(ErrorRow("too deep to unfold", node.Depth + 1));
            else if (node.Value is not null)
            {
                if (IsSequence(node.Value))
                {
                    var i = 0;
                    var limit = node.Page == int.MaxValue ? int.MaxValue : PageSize * node.Page;
                    foreach (var item in (IEnumerable)node.Value)
                    {
                        if (i == limit) { list.Add(MoreRow(node, limit)); break; }
                        list.Add(Child($"[{i}]", item, node.Depth + 1, type: ItemType(node.Value.GetType())));
                        i++;
                    }
                }
                else
                {
                    foreach (var f in Fields(node.Value.GetType()))
                        list.Add(Child(DisplayName(f), SafeGet(f, node.Value, out var err), node.Depth + 1, err, f.FieldType));
                }
            }
        }
        catch (Exception ex)
        {
            list.Add(ErrorRow(ex.Message, node.Depth + 1));
        }
        node.Children = list;
        return list;
    }

    private static FieldNode Child(string name, object? value, int depth, string? error = null, Type? type = null)
    {
        var n = new FieldNode(name, value, depth) { TypeName = TypeLabel(type ?? value?.GetType()) };
        if (error is null)
        {
            try { Describe(n); return n; }
            catch (Exception ex) { error = ex.Message; }
        }
        n.ValueText = [new Span("⚠ " + OneLine(error), Tone.Red)];
        n.Text = [new Span(PadName(name), Tone.Muted), .. n.ValueText];
        n.CanExpand = false;
        return n;
    }

    /// <summary>A type as the table's type column shows it: "float", "NetEntity?", "List&lt;int&gt;".</summary>
    internal static string? TypeLabel(Type? t)
    {
        if (t is null) return null;
        if (Nullable.GetUnderlyingType(t) is { } u) return TypeLabel(u) + "?";
        if (t == typeof(int)) return "int";
        if (t == typeof(uint)) return "uint";
        if (t == typeof(float)) return "float";
        if (t == typeof(double)) return "double";
        if (t == typeof(bool)) return "bool";
        if (t == typeof(string)) return "string";
        if (t == typeof(byte)) return "byte";
        if (t == typeof(long)) return "long";
        if (t == typeof(ulong)) return "ulong";
        if (t == typeof(short)) return "short";
        if (t == typeof(ushort)) return "ushort";
        if (t == typeof(object)) return "object";
        return FriendlyName(t);
    }

    private static string PadName(string name) => name.PadRight(Math.Max(14, name.Length + 2));

    /// <summary>Sets the node's text (name + value) and whether it can be expanded.</summary>
    private static void Describe(FieldNode n)
    {
        var name = n.Depth == 0 ? [] : new[] { new Span(PadName(n.Name), Tone.Muted) };
        var (value, expand) = Format(n.Value);
        Span[]? table = null;
        if (n.Value is not null && (expand || IsSequence(n.Value)))
        {
            try { table = TableValue(n.Value); }
            catch { /* the type name is shown instead */ }
        }
        n.ValueText = table ?? value;
        n.Text = [.. name, .. value];
        n.CanExpand = expand;
    }

    /// <summary>Every field of an object as a row (name, value, type) for the hover tip of a shortened summary.</summary>
    internal static List<(string Name, Span[] Value, string? Type)> SummaryRows(object v)
    {
        var rows = new List<(string, Span[], string?)>();
        foreach (var f in Fields(v.GetType()))
        {
            var fv = SafeGet(f, v, out var err);
            Span[] value;
            if (err is not null) value = [new Span("⚠ " + OneLine(err), Tone.Red)];
            else
            {
                var (text, canExpand) = Format(fv);
                value = (canExpand && fv is not null ? TableValue(fv) : null) ?? text;
            }
            rows.Add((DisplayName(f), value, TypeLabel(f.FieldType)));
        }
        return rows;
    }

    // The table's value cell for an expandable node: what is inside rather than the type (which
    // has its own column) — a pair's key, a collection's count, an object's first fields.
    private static Span[]? TableValue(object v)
    {
        var t = v.GetType();
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            return Format(Read(v, "key")).Text;
        if (IsSequence(v))
            return [new Span($"{Count(v):N0} {(IsDictionary(t) ? "entries" : "items")}", Tone.Sub)];
        if (v is IEnumerable) return null;

        var parts = new List<string>();
        var fields = Fields(t);
        foreach (var f in fields)
        {
            if (parts.Count == 2) break;
            var fv = SafeGet(f, v, out var err);
            if (err is not null || fv is null) continue;
            var (text, canExpand) = Format(fv);
            if (canExpand) continue;
            parts.Add($"{DisplayName(f)} = {OneLine(string.Concat(text.Select(s => s.Text)))}");
        }
        if (parts.Count == 0) return fields.Length > 0 ? [new Span("{…}", Tone.Sub)] : null;
        var tail = fields.Length > parts.Count ? ", …" : "";
        return [new Span("{" + string.Join(", ", parts) + tail + "}", Tone.Sub)];
    }

    internal static bool IsDictionary(Type t)
        => typeof(IDictionary).IsAssignableFrom(t)
           || t.GetInterfaces().Any(i => i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IDictionary<,>)));

    // Rows are one line high: a string with line breaks, or too long for a row, shows its start and
    // unfolds into its lines (long lines in pieces), one per row.
    private const int StringRow = 120;

    private static (Span[] Text, bool Expand) FormatString(string s)
    {
        var breakAt = s.AsSpan().IndexOfAny('\r', '\n');
        if (breakAt < 0 && s.Length <= StringRow) return ([new Span($"\"{s}\"", Tone.Green)], false);
        var first = breakAt < 0 ? s : s[..breakAt];
        var head  = first.Length > 60 ? first[..60] : first;
        var more  = breakAt < 0 ? $"…  {s.Length:N0} chars" : $"…  {LineCount(s):N0} lines";
        return ([new Span($"\"{head}", Tone.Green), new Span(more, Tone.Muted)], true);
    }

    // Rows the string unfolds into (long lines in StringRow pieces), counted without building them.
    private static int LineCount(string s)
    {
        var count = 0;
        foreach (var raw in s.AsSpan().EnumerateLines())
            count += Math.Max(1, (raw.Length + StringRow - 1) / StringRow);
        return count;
    }

    // A summary is one row: line breaks become ⏎, and it stops after a screenful.
    private static string OneLine(string s)
    {
        s = s.Replace("\r\n", "\n").Replace('\n', '⏎').Replace('\r', '⏎');
        return s.Length <= 200 ? s : s[..200] + "…";
    }

    internal static List<string> StringLines(string s, int max = int.MaxValue)
    {
        var list = new List<string>();
        foreach (var raw in s.AsSpan().EnumerateLines())
        {
            if (raw.Length <= StringRow) list.Add(raw.ToString());
            else
                for (var i = 0; i < raw.Length && list.Count < max; i += StringRow)
                    list.Add(raw.Slice(i, Math.Min(StringRow, raw.Length - i)).ToString());
            if (list.Count >= max) break;
        }
        return list;
    }

    // Readable value + whether there is anything below it.
    private static (Span[] Text, bool Expand) Format(object? v)
    {
        if (v is null) return ([new Span("null", Tone.Muted)], false);

        var t = v.GetType();
        switch (v)
        {
            case string s:  return FormatString(s);
            case bool b:    return ([new Span(b ? "true" : "false", Tone.Accent2)], false);
            case Enum e:    return ([new Span(e.ToString(), Tone.Sub)], false);
            case byte[] bs: return ([new Span($"{bs.Length} bytes  ", Tone.Sub), new Span(Hex(bs, 16), Tone.Muted)], false);
            case IFormattable f when t.IsPrimitive || v is decimal:
                return ([new Span(f.ToString(null, CultureInfo.InvariantCulture))], false);
            case TimeSpan ts: return ([new Span(ts.ToString())], false);
        }

        switch (t.Name)
        {
            case "NetEntity": return ([new Span(Read(v, "Id")?.ToString() ?? "?", Tone.Link), new Span("  NetEntity", Tone.Muted)], false);
            case "EntityUid": return ([new Span("uid " + Read(v, "Id"), Tone.Link)], false);
            case "GameTick":  return ([new Span("tick " + TickValue(v))], false);
            case "Vector2":   return ([new Span($"{Fmt(Read(v, "X"))}, {Fmt(Read(v, "Y"))}")], false);
            case "NetCoordinates":
            case "EntityCoordinates":
                var pos = Read(v, "Position");
                var ent = Read(v, "NetEntity") ?? Read(v, "EntityId");
                return ([new Span($"{Fmt(Read(pos, "X"))}, {Fmt(Read(pos, "Y"))}"), new Span("  @ ", Tone.Muted),
                         new Span(Read(ent, "Id")?.ToString() ?? "?", Tone.Link)], false);
            case "ResPath":   return ([new Span(Read(v, "CanonPath")?.ToString() ?? v.ToString() ?? "", Tone.Green)], false);
        }
        if (t.Name.StartsWith("ProtoId") || t.Name == "EntProtoId")
            return ([new Span($"\"{Read(v, "Id")}\"", Tone.Green), new Span("  proto", Tone.Muted)], false);
        if (v is IFormattable fmt && t.IsValueType && !IsSequence(v))
            return ([new Span(fmt.ToString(null, CultureInfo.InvariantCulture) ?? "")], false);

        if (IsSequence(v))
        {
            var n = Count(v);
            return ([new Span($"{FriendlyName(t)}", Tone.Sub), new Span($"  {n} items", Tone.Muted)], n > 0);
        }
        // Lazy or unbounded sequences aren't enumerated: that would run arbitrary code.
        if (v is IEnumerable)
            return ([new Span(FriendlyName(t), Tone.Sub)], false);
        if (v is Delegate)
            return ([new Span(FriendlyName(t), Tone.Muted)], false);

        // Any other object: its type, expandable.
        return ([new Span(FriendlyName(t), Tone.Accent2)], Fields(t).Length > 0);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    // Instance fields of the type and its bases, public and not; auto-property backing fields
    // show under the property's name.
    internal static FieldInfo[] FieldsOf(Type t) => Fields(t);

    /// <summary>A field's value, or null when reading it throws (the drafts need no error text).</summary>
    internal static object? SafeRead(FieldInfo f, object o) => SafeGet(f, o, out _);

    /// <summary>A value as the field tree shows it, in one line (for read-only draft fields).</summary>
    internal static string Brief(object? v)
    {
        try { return OneLine(string.Concat(Format(v).Text.Select(s => s.Text))); }
        catch (Exception ex) { return $"⚠ {ex.GetType().Name}"; }
    }

    private static FieldInfo[] Fields(Type t) => _fields.GetOrAdd(t, static t =>
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var list = new List<FieldInfo>();
        for (var c = t; c is not null && c != typeof(object) && c != typeof(ValueType); c = c.BaseType)
            list.InsertRange(0, c.GetFields(flags).Where(f => !Skip.Contains(DisplayName(f))));
        return list.ToArray();
    });

    internal static string DisplayName(FieldInfo f)
        => f.Name.StartsWith('<') && f.Name.IndexOf('>') is > 1 and var end ? f.Name[1..end] : f.Name;

    private static object? SafeGet(FieldInfo f, object o, out string? error)
    {
        try
        {
            error = null;
            return f.GetValue(o);
        }
        catch (Exception ex)
        {
            error = (ex.InnerException ?? ex).Message;
            return null;
        }
    }

    /// <summary>A field by name (or the backing field of an auto-property) on the runtime type; null if absent.</summary>
    internal static object? Read(object? o, string name)
    {
        if (o is null) return null;
        var f = _named.GetOrAdd((o.GetType(), name), static k =>
            Fields(k.Item1).FirstOrDefault(f => f.Name == k.Item2) ?? Fields(k.Item1).FirstOrDefault(f => DisplayName(f) == k.Item2));
        return f?.GetValue(o);
    }

    // Only bounded collections are listed: arrays and anything with a Count.
    private static bool IsSequence(object v) => v is Array or ICollection || ReadOnlyCount(v) is not null;
    internal static bool IsSequenceValue(object v) => IsSequence(v);
    internal static int CountOf(object v) => Count(v);

    private static int Count(object? v) => v switch
    {
        ICollection c => c.Count,
        null          => 0,
        _             => ReadOnlyCount(v) ?? 0,
    };

    private static int? ReadOnlyCount(object v)
    {
        if (v is string) return null;
        var iface = v.GetType().GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>));
        return iface?.GetProperty("Count")?.GetValue(v) as int?;
    }

    private static string TickValue(object? tick) => tick is null ? "?" : Read(tick, "Value")?.ToString() ?? "?";

    private static string Fmt(object? v) => v is IFormattable f ? f.ToString("0.##", CultureInfo.InvariantCulture) : v?.ToString() ?? "?";

    private static Type? ItemType(Type seq)
    {
        if (seq.IsArray) return seq.GetElementType();
        var e = seq.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return e?.GetGenericArguments()[0];
    }

    internal static string FriendlyName(Type t)
    {
        if (!t.IsGenericType) return t.Name;
        var tick = t.Name.IndexOf('`');
        var name = tick < 0 ? t.Name : t.Name[..tick];
        return $"{name}<{string.Join(", ", t.GetGenericArguments().Select(a => TypeLabel(a)))}>";
    }

    private static string Hex(byte[] bytes, int max)
        => string.Join(" ", bytes.Take(max).Select(b => b.ToString("x2"))) + (bytes.Length > max ? " …" : "");
}
