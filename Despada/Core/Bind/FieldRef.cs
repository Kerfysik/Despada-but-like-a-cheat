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

    protected override string? DoResolve()
    {
        owner.Resolve();
        if (owner.Type is null) return owner.Error;

        foreach (var n in names)
        {
            Field = owner.Type.GetField(n, All);
            if (Field is not null) return null;
        }
        return $"field {owner.Name}.{string.Join(" / ", names)} not found";
    }

    /// <summary>Reads the field (target null for static). Compiled on first use.</summary>
    public object? Get(object? target)
    {
        if (Field is null) throw new InvalidOperationException($"{Name} is not available");
        _get ??= Compile(Field);
        return _get(target);
    }

    /// <summary>Writes the field. Rare, so plain reflection (instance readonly fields too; not static readonly).</summary>
    public void Set(object? target, object? value)
    {
        if (Field is null) throw new InvalidOperationException($"{Name} is not available");
        Field.SetValue(target, value);
    }

    private static Func<object?, object?> Compile(FieldInfo f)
    {
        var target = Expression.Parameter(typeof(object), "target");
        var access = Expression.Field(f.IsStatic ? null : Expression.Convert(target, f.DeclaringType!), f);
        return Expression.Lambda<Func<object?, object?>>(Expression.Convert(access, typeof(object)), target).Compile();
    }
}
