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
    /// came from a packet (so fields the editor can't show keep their values), else a new instance.
    /// For a BUI draft of a captured packet the result is the wrap message with the copied inner one.
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
                var child   = current is not null ? (f.Field.FieldType.IsValueType ? current : Clone(current)) : Create(Nullable.GetUnderlyingType(f.Field.FieldType) ?? f.Field.FieldType);
                Apply(child, f.Children, setAll || current is null);
                f.Field.SetValue(target, child);   // a struct comes back boxed: written back
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

    private static void SetPath(object target, string[] path, int i, string value)
    {
        var f = NetDecode.FieldsOf(target.GetType()).FirstOrDefault(x => NetDecode.DisplayName(x) == path[i])
                ?? throw new InvalidOperationException($"no field {path[i]} on {target.GetType().Name}");
        if (i == path.Length - 1)
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
}
