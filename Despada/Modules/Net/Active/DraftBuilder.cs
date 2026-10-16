// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Despada.Core;

namespace Despada.Modules.Net;

/// <summary>
/// Turns a <see cref="PacketDraft"/> back into a message object, and sets single fields by path
/// ("Coordinates.Position.X") on an existing object (the modify rule). All by reflection; a value
/// that doesn't parse stops the build with a message naming the field.
/// </summary>
internal static class DraftBuilder
{
    private static readonly MethodInfo CloneMethod =
        typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;

    /// <summary>
    /// The object to send: a copy of the captured one with the draft's fields set when the draft
    /// came from a packet (so fields the editor can't show keep their values) — the live object
    /// while the log still holds it, else one rebuilt from the draft's snapshot — else a new
    /// instance. For a BUI draft the result is the wrap message with the copied inner one.
    /// </summary>
    public static object Build(PacketDraft d, out bool isBuiWrap)
    {
        isBuiWrap = false;
        if (d.Type is null) throw new InvalidOperationException($"{d.TypeName}: type not known");

        object? original = null;
        object? wrap = null;
        if (d.FromSeq >= 0 && NetCapture.Get(d.FromSeq)?.Message is { } msg)
        {
            original = msg;
            if (Engine.MsgEntitySystem.Ok && msg.GetType().Name == "MsgEntity" && Engine.MsgEntitySystem.Invoke(msg) is { } ev)
            {
                original = ev;
                if (d.TypeName.StartsWith("BUI.", StringComparison.Ordinal) && NetDecode.Read(ev, "Message") is { } inner)
                {
                    wrap = Clone(ev);
                    original = inner;
                }
            }
        }

        if (original is null && d.Source is not null) original = Rebuild(d.Source, d.Type, "the packet");
        if (wrap is null && d.Wrap is not null && d.TypeName.StartsWith("BUI.", StringComparison.Ordinal)) wrap = Rebuild(d.Wrap, null, "the BUI wrap");

        var obj = original is not null && original.GetType() == d.Type ? Clone(original) : Create(d.Type);
        Apply(obj, d.Root.Children, setAll: original is null);

        if (wrap is not null)
        {
            // The wrap keeps its entity and UI key; only the message inside is ours.
            var f = wrap.GetType().GetField("Message", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("BUI wrap: Message field not found");
            f.SetValue(wrap, obj);
            isBuiWrap = true;
            return wrap;
        }
        return obj;
    }

    // The packet is gone from the log and its snapshot can't be rebuilt in full: better no send
    // than one with silently emptied fields.
    private static object Rebuild(ObjectSnapshot.Node node, Type? type, string what)
    {
        try { return ObjectSnapshot.Materialize(node, type) ?? throw new InvalidOperationException("nothing came back"); }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"can't rebuild {what} from its snapshot: {ex.Message}", ex);
        }
    }

    /// <summary>Sets "path=value" on an object (the modify rule); false with a reason when it can't.</summary>
    public static bool TrySet(object root, string path, string value, out string error)
    {
        error = "";
        try
        {
            SetPath(root, path.Split('.'), 0, value);
            return true;
        }
        catch (Exception ex)
        {
            error = $"{path}: {ex.Message}";
            return false;
        }
    }

    // ── fields ───────────────────────────────────────────────────────────────────────────────

    private static void Apply(object target, List<DraftField> fields, bool setAll)
    {
        foreach (var f in fields)
        {
            if (f.Field is null || f.Kind == DraftKind.Readonly) continue;
            if (f.Kind == DraftKind.Node)
            {
                var current = f.Field.GetValue(target);
                // A captured null stays null unless something under it was edited: a nullable
                // struct or an optional object must not turn into a default instance just by being
                // sent. A fresh object (setAll) takes every node: there is nothing captured to keep.
                if (current is null && !setAll && !f.AnyChanged()) continue;
                var child = current is not null ? Copy(current) : Create(Nullable.GetUnderlyingType(f.Field.FieldType) ?? f.Field.FieldType);
                Apply(child, f.Children, setAll || current is null);
                f.Field.SetValue(target, child);   // a struct comes back boxed: written back
                continue;
            }
            if (f.Kind == DraftKind.List)
            {
                if (!setAll && !f.AnyChanged()) continue;   // untouched: the original list stays as it is
                try { f.Field.SetValue(target, BuildList(f)); }
                catch (Exception ex) when (ex is not InvalidOperationException)
                {
                    throw new InvalidOperationException($"{f.Name}: {ex.Message}", ex);
                }
                continue;
            }
            if (!setAll && !f.Changed) continue;
            try
            {
                f.Field.SetValue(target, Parse(f.Value, f.Field.FieldType, f.Kind));
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                throw new InvalidOperationException($"{f.Name}: {ex.Message}", ex);
            }
        }
    }

    // A list from its item rows: leaves parsed, objects from their source (edits on top) or new.
    private static object BuildList(DraftField list)
    {
        var et    = list.ElementType ?? throw new InvalidOperationException($"{list.Name}: no item type");
        var items = Array.CreateInstance(et, list.Children.Count);
        for (var i = 0; i < list.Children.Count; i++)
        {
            var item = list.Children[i];
            object? value;
            if (item.Kind == DraftKind.Node)
            {
                var child = item.Source is { } src ? Copy(src) : Create(Nullable.GetUnderlyingType(et) ?? et);
                Apply(child, item.Children, setAll: item.Source is null);
                value = child;
            }
            else if (item.Kind == DraftKind.Readonly) value = item.Source;
            else value = Parse(item.Value, et, item.Kind);
            items.SetValue(value, i);
        }
        return ObjectSnapshot.MakeCollection(list.Field!.FieldType, et, items);
    }

    private static void SetPath(object target, string[] path, int i, string value)
    {
        // "Name" or "Name[index]" (an array or list element).
        var seg = path[i];
        int? index = null;
        var br = seg.IndexOf('[');
        if (br > 0 && seg.EndsWith(']'))
        {
            index = int.Parse(seg.AsSpan(br + 1, seg.Length - br - 2), CultureInfo.InvariantCulture);
            seg = seg[..br];
        }
        var f = NetDecode.FieldsOf(target.GetType()).FirstOrDefault(x => NetDecode.DisplayName(x) == seg)
                ?? throw new InvalidOperationException($"no field {seg} on {target.GetType().Name}");
        var last = i == path.Length - 1;

        if (index is { } at)
        {
            if (f.GetValue(target) is not System.Collections.IList list)
                throw new InvalidOperationException($"{seg} is not a list");
            if (at < 0 || at >= list.Count) throw new InvalidOperationException($"{seg} has {list.Count} items, not {at + 1}");
            var et = f.FieldType.IsArray ? f.FieldType.GetElementType()! : list[at]?.GetType() ?? typeof(object);
            if (last) { list[at] = Parse(value, et, KindOf(et)); return; }
            var item = list[at] ?? Create(et);
            SetPath(item, path, i + 1, value);
            list[at] = item;   // a struct element comes back boxed: written back
            return;
        }
        if (last)
        {
            f.SetValue(target, Parse(value, f.FieldType, KindOf(f.FieldType)));
            return;
        }
        var child = f.GetValue(target) ?? Create(Nullable.GetUnderlyingType(f.FieldType) ?? f.FieldType);
        SetPath(child, path, i + 1, value);
        f.SetValue(target, child);
    }

    private static DraftKind KindOf(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(string)) return DraftKind.Text;
        if (t == typeof(bool)) return DraftKind.Bool;
        if (t.IsEnum) return DraftKind.Enum;
        if (t.Name == "NetEntity") return DraftKind.Entity;
        if (t.IsPrimitive || t == typeof(decimal)) return DraftKind.Number;
        throw new InvalidOperationException($"{t.Name} can't be set from text");
    }

    /// <summary>A field value from the editor's text; an empty value is null for a nullable.</summary>
    public static object? Parse(string value, Type fieldType, DraftKind kind)
    {
        var t = Nullable.GetUnderlyingType(fieldType);
        if (t is not null && value.Length == 0) return null;
        t ??= fieldType;
        switch (kind)
        {
            case DraftKind.Text:   return value;
            case DraftKind.Bool:   return value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1";
            case DraftKind.Enum:   return Enum.Parse(t, value, ignoreCase: true);
            case DraftKind.Number: return Convert.ChangeType(value, t, CultureInfo.InvariantCulture);
            case DraftKind.Entity:
            {
                var id = int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
                return Activator.CreateInstance(t, id) ?? throw new InvalidOperationException("NetEntity(int) not found");
            }
            default: throw new InvalidOperationException($"{kind} can't be set from text");
        }
    }

    // ── instances ────────────────────────────────────────────────────────────────────────────

    /// <summary>A new instance: the parameterless constructor when there is one, else uninitialised.</summary>
    public static object Create(Type t)
    {
        if (t.IsValueType) return Activator.CreateInstance(t)!;
        if (t.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, Type.EmptyTypes) is { } ctor)
            return ctor.Invoke(null);
        return RuntimeHelpers.GetUninitializedObject(t);
    }

    private static object Clone(object o) => CloneMethod.Invoke(o, null)!;

    // A source is never written to: a class is cloned, a boxed struct re-boxed (writing through a
    // FieldInfo into the box the draft keeps would make the edit stick past a revert or a cancel).
    private static object Copy(object o) => o.GetType().IsValueType ? System.Runtime.CompilerServices.RuntimeHelpers.GetObjectValue(o) : Clone(o);
}
