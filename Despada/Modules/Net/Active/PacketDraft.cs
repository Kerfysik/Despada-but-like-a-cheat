// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Reflection;
using Despada.Core;

namespace Despada.Modules.Net;

internal enum DraftKind { Text, Number, Bool, Enum, Entity, Node, List, Readonly }

/// <summary>One editable field of a draft: a text buffer the UI edits, and what it came from.</summary>
internal sealed class DraftField
{
    public string    Name = "";
    public DraftKind Kind;
    public Type?     Type;          // the field's declared type (null for the root)
    public FieldInfo? Field;        // where it lives on the object, for the implementation
    public string    Value = "";    // what the UI edits (numbers and entity ids as text)
    public string    Original = ""; // the value when the draft was made
    public string[]  Options = [];  // Enum: its names
    public List<DraftField> Children = new();
    public bool      Expanded = true;
    public int       Depth;
    /// <summary>List: the item type; an item: the object it was made from (null for a new one).</summary>
    public Type?     ElementType;
    public object?   Source;
    /// <summary>List: items were added or removed.</summary>
    public bool      Touched;

    public bool Changed => Kind switch
    {
        DraftKind.Node or DraftKind.Readonly => false,
        DraftKind.List => Touched,
        _ => Value != Original,
    };

    /// <summary>This field or anything under it was changed.</summary>
    public bool AnyChanged() => All().Any(f => f.Changed);

    public IEnumerable<DraftField> All()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.All()) yield return d;
    }
}

/// <summary>
/// A packet to send: the type, which way it goes and its fields as the UI edits them. Made from a
/// captured packet (edit and resend) or from a type (a new packet). Turning it back into a message
/// object is the implementation's job (NetActive.Send, TODO).
/// </summary>
internal sealed class PacketDraft
{
    private const int MaxDepth = 4;

    public string TypeName = "";
    public Type?  Type;
    public NetDir Dir = NetDir.Out;
    public DraftField Root = new() { Kind = DraftKind.Node };

    /// <summary>The captured packet it was made from, if any (its live object is used while it's kept).</summary>
    public long FromSeq = -1;
    /// <summary>The object it was made from, as text: what the editor can't show is rebuilt from it when the packet is gone.</summary>
    public ObjectSnapshot.Node? Source;
    /// <summary>For a BUI draft: the wrap message around it (its entity and UI key), the same way.</summary>
    public ObjectSnapshot.Node? Wrap;

    public int ChangedCount => Root.All().Count(f => f.Changed);

    /// <summary>"Target=22" for every changed field, with its path.</summary>
    public IEnumerable<(string Path, string Value)> Changes()
    {
        foreach (var (path, f) in Walk(Root, ""))
            if (f.Changed) yield return (path, f.Value);
    }

    /// <summary>Every editable field with its path ("Coordinates.Position.X").</summary>
    public IEnumerable<(string Path, DraftField Field)> Walk(DraftField? from = null, string prefix = "")
    {
        from ??= Root;
        foreach (var c in from.Children)
        {
            var path = prefix.Length == 0 ? c.Name : c.Name.StartsWith('[') ? prefix + c.Name : prefix + "." + c.Name;
            if (c.Kind is DraftKind.Node or DraftKind.List)
                foreach (var x in Walk(c, path)) yield return x;
            else
                yield return (path, c);
        }
    }

    /// <summary>A copy to edit, so cancelling leaves this one as it is.</summary>
    public PacketDraft Clone()
    {
        var d = new PacketDraft { TypeName = TypeName, Type = Type, Dir = Dir, FromSeq = FromSeq, Source = Source, Wrap = Wrap, Root = CloneField(Root) };
        return d;
    }

    private static DraftField CloneField(DraftField f) => new()
    {
        Name = f.Name, Kind = f.Kind, Type = f.Type, Field = f.Field, Value = f.Value, Original = f.Original,
        Options = f.Options, Expanded = f.Expanded, Depth = f.Depth, Children = f.Children.Select(CloneField).ToList(),
        ElementType = f.ElementType, Source = f.Source, Touched = f.Touched,
    };

    /// <summary>Puts saved values back into the fields as their starting point (unknown paths are ignored).</summary>
    public void Apply(IReadOnlyDictionary<string, string> values)
    {
        foreach (var (path, f) in Walk())
            if (values.TryGetValue(path, out var v)) f.Value = f.Original = v;
    }

    /// <summary>Puts saved values in as edits over the fields' current values (they count as changes).</summary>
    public void ApplyEdits(IReadOnlyDictionary<string, string> values)
    {
        foreach (var (path, f) in Walk())
            if (values.TryGetValue(path, out var v)) f.Value = v;
    }

    /// <summary>The draft of a captured packet: for MsgEntity, of the event inside it.</summary>
    public static PacketDraft? From(Packet p)
    {
        var msg = p.Message;
        if (msg is null) return null;
        try
        {
            object root = msg;
            object? ev = null;
            if (p.Type.Name == "MsgEntity" && Engine.MsgEntitySystem.Ok && (ev = Engine.MsgEntitySystem.Invoke(msg)) is not null)
            {
                root = ev;
                // A BUI message travels wrapped: the draft is of the message inside, as "BUI.X" names it.
                if (p.Inner is { } inner && inner.StartsWith("BUI.", StringComparison.Ordinal) && NetDecode.Read(ev, "Message") is { } bui)
                    root = bui;
            }
            var d = Of(p.Inner ?? p.Type.Name, root, p.Dir);
            d.FromSeq = p.Seq;
            if (!ReferenceEquals(root, msg) && !ReferenceEquals(root, ev)) d.Wrap = ObjectSnapshot.Take(ev!);
            return d;
        }
        catch (Exception ex)
        {
            Log.Warn("net", $"draft of #{p.Seq}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The draft of an object (a snapshot rebuilt, a packet's message): its fields and a snapshot of it.</summary>
    public static PacketDraft Of(string typeName, object root, NetDir dir)
    {
        var d = new PacketDraft { TypeName = typeName, Type = root.GetType(), Dir = dir, Source = ObjectSnapshot.Take(root) };
        d.Root.Name = typeName;
        d.Root.Type = root.GetType();
        Fill(d.Root, root.GetType(), root, 1);
        return d;
    }

    /// <summary>An empty draft of a type (a new packet); null when the type isn't known (or can't be read).</summary>
    public static PacketDraft? ForType(string typeName)
    {
        var t = NetTypes.Resolve(typeName);
        if (t is null) return null;
        try
        {
            var d = new PacketDraft { TypeName = typeName, Type = t, Dir = NetActive.DirectionOf(typeName) ?? NetDir.Out };
            d.Root.Name = typeName;
            d.Root.Type = t;
            Fill(d.Root, t, null, 1);
            return d;
        }
        catch (Exception ex)
        {
            Log.Warn("net", $"draft of {typeName}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private const int MaxListItems = 2000;

    // The fields of `type` as draft fields; values from `instance` when there is one.
    private static void Fill(DraftField parent, Type type, object? instance, int depth)
    {
        foreach (var f in NetDecode.FieldsOf(type))
        {
            var df = new DraftField { Name = NetDecode.DisplayName(f), Type = f.FieldType, Field = f, Depth = depth };
            parent.Children.Add(df);
            Describe(df, f.FieldType, instance is null ? null : NetDecode.SafeRead(f, instance), depth);
        }
    }

    // One draft field for a value of the declared type: a leaf the editor types into, a node of
    // fields, a list of items, or read-only text.
    private static void Describe(DraftField df, Type ft, object? value, int depth)
    {
        if (ft == typeof(string))                       Set(df, DraftKind.Text, value as string ?? "");
        else if (ft == typeof(bool))                    Set(df, DraftKind.Bool, value is true ? "true" : "false");
        else if (ft.IsEnum)
        {
            df.Options = Enum.GetNames(ft);
            Set(df, DraftKind.Enum, value?.ToString() ?? (df.Options.Length > 0 ? df.Options[0] : ""));
        }
        else if (ft.IsPrimitive || ft == typeof(decimal))
            Set(df, DraftKind.Number, value is IFormattable n ? n.ToString(null, CultureInfo.InvariantCulture) : "0");
        else if (ft.Name == "NetEntity")
            Set(df, DraftKind.Entity, value is null ? "0" : NetDecode.Read(value, "Id")?.ToString() ?? "0");
        else if (Nullable.GetUnderlyingType(ft) is { } inner)
        {
            // A nullable: empty = null; a nullable struct is a node (built from T when null).
            df.Type = inner;
            if (inner == typeof(bool)) Set(df, DraftKind.Bool, value is true ? "true" : value is false ? "false" : "");
            else if (inner.IsEnum) { df.Options = Enum.GetNames(inner); Set(df, DraftKind.Enum, value?.ToString() ?? ""); }
            else if (inner.Name == "NetEntity") Set(df, DraftKind.Entity, value is null ? "" : NetDecode.Read(value, "Id")?.ToString() ?? "");
            else if (inner.IsPrimitive || inner == typeof(decimal)) Set(df, DraftKind.Number, value is IFormattable n ? n.ToString(null, CultureInfo.InvariantCulture) : "");
            else if (depth < MaxDepth && IsPlain(inner))
            {
                df.Kind = DraftKind.Node;
                df.Expanded = depth <= 1;
                df.Source = value;
                Fill(df, inner, value, depth + 1);
            }
            else Set(df, DraftKind.Readonly, value is null ? "null" : NetDecode.Brief(value));
        }
        else if (depth < MaxDepth && ListItemType(ft) is { } et && IsEditable(et) && (value is null || (value is System.Collections.ICollection c && c.Count <= MaxListItems)))
        {
            // An array or list whose items the editor can show: one row per item, + and × when editing.
            df.Kind = DraftKind.List;
            df.ElementType = et;
            df.Expanded = depth <= 1;
            if (value is System.Collections.IEnumerable items)
            {
                var i = 0;
                foreach (var item in items) df.Children.Add(Item(df, et, item, i++, depth + 1));
            }
        }
        else if (depth < MaxDepth && IsPlain(ft) && (value is not null || CanMake(ft)))
        {
            df.Kind = DraftKind.Node;
            df.Expanded = depth <= 1;
            df.Source = value;
            Fill(df, value?.GetType() ?? ft, value, depth + 1);
        }
        else
            Set(df, DraftKind.Readonly, value is null ? "null" : NetDecode.Brief(value));
    }

    private static DraftField Item(DraftField list, Type et, object? value, int index, int depth)
    {
        var item = new DraftField { Name = $"[{index}]", Type = et, Depth = depth };
        if (value is null && IsPlain(et) && !CanMake(et) && ListItemType(et) is null)
        {
            item.Kind = DraftKind.Node;
            item.Expanded = true;
            Fill(item, et, null, depth + 1);
        }
        else Describe(item, et, value, depth);
        return item;
    }

    /// <summary>A new item at the end of a list (its type's defaults).</summary>
    public static DraftField AddItem(DraftField list)
    {
        var item = Item(list, list.ElementType!, null, list.Children.Count, list.Depth + 1);
        list.Children.Add(item);
        list.Touched = true;
        return item;
    }

    public static void RemoveItem(DraftField list, DraftField item)
    {
        if (!list.Children.Remove(item)) return;
        list.Touched = true;
        for (var i = 0; i < list.Children.Count; i++) list.Children[i].Name = $"[{i}]";
    }

    /// <summary>The item type of an array or list; null for anything else (dictionaries, sets, lazy sequences).</summary>
    internal static Type? ListItemType(Type t)
    {
        if (t.IsArray) return t.GetArrayRank() == 1 ? t.GetElementType() : null;
        if (!t.IsGenericType) return null;
        var def = t.GetGenericTypeDefinition();
        return def == typeof(List<>) || def == typeof(IList<>) || def == typeof(IReadOnlyList<>) ? t.GetGenericArguments()[0] : null;
    }

    // Items the editor can type into or unfold: leaves, and plain objects with fields (a class
    // without a parameterless constructor is made uninitialised and filled field by field).
    private static bool IsEditable(Type et)
        => et == typeof(string) || et == typeof(bool) || et.IsEnum || et.IsPrimitive || et == typeof(decimal) || et.Name == "NetEntity"
           || Nullable.GetUnderlyingType(et) is { } inner && IsEditable(inner)
           || (IsPlain(et) && ListItemType(et) is null && NetDecode.FieldsOf(et).Length > 0);

    private static void Set(DraftField f, DraftKind kind, string value)
    {
        f.Kind = kind;
        f.Value = f.Original = value;
    }

    // A struct or class whose fields can be edited one by one (not a collection, not a delegate).
    private static bool IsPlain(Type t)
        => !t.IsArray && !typeof(System.Collections.IEnumerable).IsAssignableFrom(t) && !typeof(Delegate).IsAssignableFrom(t)
           && t != typeof(object) && !t.IsInterface && !t.IsAbstract;

    private static bool CanMake(Type t) => t.IsValueType || t.GetConstructor(Type.EmptyTypes) is not null;
}
