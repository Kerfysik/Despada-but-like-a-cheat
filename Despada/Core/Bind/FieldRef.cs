// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq.Expressions;
using System.Reflection;

namespace Despada.Core.Bind;

/// <summary>A field on <paramref name="owner"/> (any visibility, instance or static); names = fallbacks.</summary>
internal sealed class FieldRef(TypeRef owner, params string[] names) : Binding($"{owner.Name}.{names[0]}")
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static;

    private Func<object?, object?>? _get;

    public FieldInfo? Field { get; private set; }

    /// <summary>
    /// The property that stands in for the field on builds where the member is one: newer Robust
    /// turned Box2.Left/Bottom/Right/Top (and other struct fields) into read-only properties, and
    /// the same source has to bind on both. Reading it through the getter gives the same value.
    /// </summary>
    public PropertyInfo? Property { get; private set; }

    protected override string? DoResolve()
    {
        owner.Resolve();
        if (owner.Type is null) return owner.Error;

        foreach (var n in names)
        {
            Field = owner.Type.GetField(n, All);
            if (Field is not null) return null;

            Property = owner.Type.GetProperty(n, All);
            if (Property?.GetMethod is not null) return null;
            Property = null;
        }
        return $"field {owner.Name}.{string.Join(" / ", names)} not found";
    }

    /// <summary>Reads the field or property (target null for static). Compiled on first use.</summary>
    public object? Get(object? target)
    {
        if (Field is null && Property is null)
            throw new InvalidOperationException($"{Name} is not available");

        _get ??= Field is not null ? Compile(Field) : Compile(Property!);
        return _get(target);
    }

    /// <summary>Writes the field. Rare, so plain reflection (instance readonly fields too; not static readonly).</summary>
    public void Set(object? target, object? value)
    {
        if (Field is not null) { Field.SetValue(target, value); return; }
        if (Property is { CanWrite: true }) { Property.SetValue(target, value); return; }
        throw new InvalidOperationException($"{Name} is not writable");
    }

    private static Func<object?, object?> Compile(FieldInfo f)
    {
        var target = Expression.Parameter(typeof(object), "target");
        var access = Expression.Field(f.IsStatic ? null : Expression.Convert(target, f.DeclaringType!), f);
        return Expression.Lambda<Func<object?, object?>>(Expression.Convert(access, typeof(object)), target).Compile();
    }

    private static Func<object?, object?> Compile(PropertyInfo p)
    {
        var getter = p.GetMethod ?? throw new InvalidOperationException($"{p.Name} has no getter");
        var target = Expression.Parameter(typeof(object), "target");
        var access = Expression.Property(getter.IsStatic ? null : Expression.Convert(target, p.DeclaringType!), p);
        return Expression.Lambda<Func<object?, object?>>(Expression.Convert(access, typeof(object)), target).Compile();
    }
}
