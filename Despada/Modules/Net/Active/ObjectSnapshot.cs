// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Despada.Core;

namespace Despada.Modules.Net;

/// <summary>
/// A message object as a tree of text, taken by reflection, so a draft can rebuild the fields the
/// editor doesn't show after the captured packet is gone (a restart, a cleared log, a saved
/// packet). Rebuilding is all or nothing: a cut or unreadable snapshot throws rather than send a
/// packet with silently emptied fields.
/// </summary>
internal static class ObjectSnapshot
{
    private const int MaxDepth = 24;
    private const int MaxNodes = 50_000;

    public sealed class Node
    {
        [JsonPropertyName("t"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? T { get; set; }
        [JsonPropertyName("v"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? V { get; set; }
        [JsonPropertyName("n"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool N { get; set; }
        /// <summary>Cut by the size or depth limit: not a real value.</summary>
        [JsonPropertyName("c"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public bool C { get; set; }
        [JsonPropertyName("f"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public Dictionary<string, Node>? F { get; set; }
        [JsonPropertyName("i"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<Node>? I { get; set; }

        public static Node Null => new() { N = true };
        public static Node Cut  => new() { N = true, C = true };

        /// <summary>Something inside was cut: the tree is incomplete.</summary>
        public bool Complete => !C && (F is null || F.Values.All(c => c.Complete)) && (I is null || I.All(c => c.Complete));
    }

    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 256 };

    public static string ToJson(Node n) => JsonSerializer.Serialize(n, Json);

    public static Node? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<Node>(json, Json); }
        catch (Exception ex) { Log.Warn("net", $"snapshot: {ex.Message}"); return null; }
    }

    // ── taking ───────────────────────────────────────────────────────────────────────────────

    public static Node Take(object root)
    {
        var budget = MaxNodes;
        var n = Take(root, root.GetType(), 0, new HashSet<object>(ReferenceEqualityComparer.Instance), ref budget);
        n.T ??= root.GetType().FullName;
        return n;
    }

    private static Node Take(object? value, Type declared, int depth, HashSet<object> seen, ref int budget)
    {
        if (value is null) return Node.Null;
        if (depth > MaxDepth || --budget < 0) return Node.Cut;
        var rt = value.GetType();
        if (rt == typeof(IntPtr) || rt == typeof(UIntPtr) || rt.IsPointer || typeof(Delegate).IsAssignableFrom(rt) || value is Type) return Node.Null;

        var node = new Node();
        var underlying = Nullable.GetUnderlyingType(declared) ?? declared;
        if (rt != underlying) node.T = rt.FullName;

        if (value is byte[] bytes) { node.V = Convert.ToBase64String(bytes); return node; }
        if (IsLeaf(rt)) { node.V = LeafText(value, rt); return node; }

        if (!rt.IsValueType && !seen.Add(value)) return Node.Cut;   // a cycle
        try
        {
            if (value is IDictionary dict)
            {
                var (kt, vt) = PairTypes(rt);
                node.I = new List<Node>();
                foreach (DictionaryEntry e in dict)
                {
                    if (budget <= 0) { node.I.Add(Node.Cut); break; }
                    node.I.Add(Pair(Take(e.Key, kt, depth + 1, seen, ref budget), Take(e.Value, vt, depth + 1, seen, ref budget)));
                }
                return node;
            }
            if (PairTypes(rt) is var (gk, gv) && gk != typeof(object) && value is IEnumerable pairs && IsBounded(value))
            {
                // A generic dictionary that isn't an IDictionary: its pairs by reflection.
                var pt = typeof(KeyValuePair<,>).MakeGenericType(gk, gv);
                var pk = pt.GetProperty("Key")!; var pv = pt.GetProperty("Value")!;
                node.I = new List<Node>();
                foreach (var p in pairs)
                {
                    if (budget <= 0) { node.I.Add(Node.Cut); break; }
                    node.I.Add(Pair(Take(pk.GetValue(p), gk, depth + 1, seen, ref budget), Take(pv.GetValue(p), gv, depth + 1, seen, ref budget)));
                }
                return node;
            }
            if (value is IEnumerable items && IsBounded(value))
            {
                // Only bounded collections: a lazy sequence could be endless.
                var et = ItemType(rt) ?? typeof(object);
                node.I = new List<Node>();
                foreach (var item in items)
                {
                    if (budget <= 0) { node.I.Add(Node.Cut); break; }
                    node.I.Add(Take(item, et, depth + 1, seen, ref budget));
                }
                return node;
            }
            node.F = new Dictionary<string, Node>();
            foreach (var f in NetDecode.FieldsOf(rt))
            {
                if (budget <= 0) { node.C = true; break; }
                node.F[NetDecode.DisplayName(f)] = Take(NetDecode.SafeRead(f, value), f.FieldType, depth + 1, seen, ref budget);
            }
            return node;
        }
        finally
        {
            if (!rt.IsValueType) seen.Remove(value);
        }
    }

    private static Node Pair(Node key, Node value) => new() { F = new Dictionary<string, Node> { ["Key"] = key, ["Value"] = value } };

    private static bool IsBounded(object v) => v is Array or ICollection || v.GetType().GetInterfaces()
        .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>));

    private static bool IsLeaf(Type t)
        => t == typeof(string) || t.IsPrimitive || t.IsEnum || t == typeof(decimal) || t.Name == "NetEntity"
           || t == typeof(Guid) || t == typeof(TimeSpan) || t == typeof(DateTime);

    private static string LeafText(object v, Type t)
    {
        if (t.Name == "NetEntity") return NetDecode.Read(v, "Id")?.ToString() ?? "0";
        return v switch
        {
            string s       => s,
            bool b         => b ? "true" : "false",
            Enum e         => e.ToString(),
            DateTime d     => d.ToString("o", CultureInfo.InvariantCulture),
            TimeSpan ts    => ts.ToString("c", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _              => v.ToString() ?? "",
        };
    }

    // ── materialising ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A new object from the tree; <paramref name="declared"/> is the type to make when the root
    /// names none. Throws when the snapshot was cut or any part of it couldn't be rebuilt.
    /// </summary>
    public static object? Materialize(Node n, Type? declared = null)
    {
        var t = (n.T is { } name ? ResolveType(name) : null) ?? declared
                ?? throw new InvalidOperationException("the type is not known");
        var errors = new List<string>();
        var obj = Build(n, t, "", errors);
        if (errors.Count > 0)
            throw new InvalidOperationException(errors.Count == 1 ? errors[0] : $"{errors[0]} (+{errors.Count - 1} more)");
        return obj;
    }

    private static object? Build(Node n, Type declared, string path, List<string> errors)
    {
        var t = Nullable.GetUnderlyingType(declared) ?? declared;
        if (n.T is { } name && ResolveType(name) is { } rt && t.IsAssignableFrom(rt)) t = rt;
        if (n.C) { errors.Add($"{Where(path)}: cut from the snapshot"); return Default(t, declared); }
        if (n.N) return Default(t, declared);
        if (n.V is { } v)
        {
            try { return t == typeof(byte[]) ? Convert.FromBase64String(v) : ParseLeaf(v, t); }
            catch (Exception ex) { errors.Add($"{Where(path)}: {ex.Message}"); return Default(t, declared); }
        }

        if (n.I is { } items)
        {
            try { return BuildCollection(items, t, path, errors); }
            catch (Exception ex) { errors.Add($"{Where(path)}: {(ex.InnerException ?? ex).Message}"); return Default(t, declared); }
        }

        object obj;
        try { obj = DraftBuilder.Create(t); }
        catch (Exception ex) { errors.Add($"{Where(path)}: {ex.Message}"); return Default(t, declared); }
        if (n.F is { } fields)
        {
            foreach (var f in NetDecode.FieldsOf(t))
            {
                if (!fields.TryGetValue(NetDecode.DisplayName(f), out var child)) continue;
                var fp = path.Length == 0 ? NetDecode.DisplayName(f) : path + "." + NetDecode.DisplayName(f);
                try { f.SetValue(obj, Build(child, f.FieldType, fp, errors)); }
                catch (Exception ex) { errors.Add($"{fp}: {(ex.InnerException ?? ex).Message}"); }
            }
        }
        return obj;
    }

    private static object? BuildCollection(List<Node> items, Type t, string path, List<string> errors)
    {
        if (typeof(IDictionary).IsAssignableFrom(t) || IsGenericDictionary(t))
        {
            var (kt, vt) = PairTypes(t);
            var dict = (IDictionary)DraftBuilder.Create(t.IsInterface || t.IsAbstract ? typeof(Dictionary<,>).MakeGenericType(kt, vt) : t);
            foreach (var pair in items)
            {
                if (pair.C) { errors.Add($"{Where(path)}: cut from the snapshot"); continue; }
                if (pair.F is null || !pair.F.TryGetValue("Key", out var kn) || !pair.F.TryGetValue("Value", out var vn)) continue;
                var key = Build(kn, kt, path + ".Key", errors);
                if (key is not null) dict[key] = Build(vn, vt, path + ".Value", errors);
            }
            return dict;
        }

        var et  = ItemType(t) ?? typeof(object);
        var arr = Array.CreateInstance(et, items.Count);
        for (var i = 0; i < items.Count; i++) arr.SetValue(Build(items[i], et, $"{path}[{i}]", errors), i);
        return MakeCollection(t, et, arr);
    }

    /// <summary>A collection of type <paramref name="t"/> holding <paramref name="arr"/>: the array itself, a list for an interface, a constructor taking the items (immutable and engine collections), else an empty one filled through Add.</summary>
    internal static object MakeCollection(Type t, Type et, Array arr)
    {
        if (t.IsArray || t == typeof(object)) return arr;
        if (t.IsInterface || t.IsAbstract)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(et))!;
            foreach (var item in arr) list.Add(item);
            return list;
        }
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        if (t.GetConstructor(flags, [arr.GetType()]) is { } fromArray) return fromArray.Invoke([arr]);
        if (t.GetConstructor(flags, [typeof(IEnumerable<>).MakeGenericType(et)]) is { } fromItems) return fromItems.Invoke([arr]);
        var coll = DraftBuilder.Create(t);
        if (coll is IList l && !l.IsFixedSize)
        {
            foreach (var item in arr) l.Add(item);
            return coll;
        }
        if ((t.GetMethod("Add", [et]) ?? t.GetMethod("Enqueue", [et])) is { } add)
        {
            foreach (var item in arr) add.Invoke(coll, [item]);
            return coll;
        }
        if (t.GetMethod("Push", [et]) is { } push)
        {
            for (var i = arr.Length - 1; i >= 0; i--) push.Invoke(coll, [arr.GetValue(i)]);   // a stack enumerates top first
            return coll;
        }
        throw new InvalidOperationException($"can't fill a {t.Name}");
    }

    private static object? Default(Type t, Type declared)
        => t.IsValueType && Nullable.GetUnderlyingType(declared) is null ? Activator.CreateInstance(t) : null;

    private static string Where(string path) => path.Length == 0 ? "root" : path;

    private static object? ParseLeaf(string v, Type t)
    {
        if (t == typeof(string)) return v;
        if (t == typeof(bool))   return v == "true" || v == "1";
        if (t.IsEnum)            return Enum.Parse(t, v, ignoreCase: true);
        if (t.Name == "NetEntity") return Activator.CreateInstance(t, int.Parse(v, CultureInfo.InvariantCulture));
        if (t == typeof(Guid))     return Guid.Parse(v);
        if (t == typeof(TimeSpan)) return TimeSpan.Parse(v, CultureInfo.InvariantCulture);
        if (t == typeof(DateTime)) return DateTime.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        if (t == typeof(object) || t == typeof(Enum) || t == typeof(ValueType)) throw new InvalidOperationException($"the type of \"{v}\" is not known");
        return Convert.ChangeType(v, t, CultureInfo.InvariantCulture);
    }

    // ── types ────────────────────────────────────────────────────────────────────────────────

    private static readonly ConcurrentDictionary<string, Type> _types = new();

    /// <summary>A type by its full name from any loaded assembly; misses aren't cached (content loads later).</summary>
    internal static Type? ResolveType(string fullName)
    {
        if (_types.TryGetValue(fullName, out var known)) return known;
        var t = Type.GetType(fullName);
        if (t is null)
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { if (a.GetType(fullName) is { } found) { t = found; break; } }
                catch { /* a dynamic assembly without types */ }
            }
        if (t is not null) _types[fullName] = t;
        return t;
    }

    private static Type? ItemType(Type t)
    {
        if (t.IsArray) return t.GetElementType();
        var ie = t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>) ? t
               : t.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return ie?.GetGenericArguments()[0];
    }

    private static bool IsGenericDictionary(Type t)
        => t.IsGenericType && t.GetGenericTypeDefinition() is var d && (d == typeof(IDictionary<,>) || d == typeof(IReadOnlyDictionary<,>));

    private static (Type Key, Type Value) PairTypes(Type t)
    {
        var id = IsGenericDictionary(t) ? t
               : t.GetInterfaces().FirstOrDefault(i => i.IsGenericType && (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) || i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
        if (id is null) return (typeof(object), typeof(object));
        var args = id.GetGenericArguments();
        return (args[0], args[1]);
    }
}
