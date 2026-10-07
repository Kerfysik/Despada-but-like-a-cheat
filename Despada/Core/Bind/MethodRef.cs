// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq.Expressions;
using System.Reflection;

namespace Despada.Core.Bind;

/// <summary>
/// A method on <paramref name="owner"/>. <paramref name="parameters"/> (type names, short or full)
/// pick an overload; null means "the only method with this name". Several names = fallbacks.
/// </summary>
internal sealed class MethodRef(TypeRef owner, string[] names, string[]? parameters)
    : Binding($"{owner.Name}.{names[0]}")
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private Func<object?, object?[], object?>? _invoke;

    public MethodInfo? Method { get; private set; }

    protected override string? DoResolve()
    {
        owner.Resolve();
        if (owner.Type is null) return owner.Error;

        foreach (var name in names)
        {
            // The most derived declaration wins (Harmony patches the declared method).
            for (var t = owner.Type; t is not null; t = t.BaseType)
            {
                var candidates = t.GetMethods(All).Where(m => m.Name == name).ToList();
                if (parameters is not null)
                    candidates = candidates.Where(Matches).ToList();

                if (candidates.Count == 1)
                {
                    Method = candidates[0];
                    return null;
                }
                if (candidates.Count > 1)
                    return $"{owner.Name}.{name} is ambiguous ({candidates.Count} overloads)";
            }
        }

        var sig = parameters is null ? "" : $"({string.Join(", ", parameters)})";
        return $"method {owner.Name}.{string.Join(" / ", names)}{sig} not found";
    }

    private bool Matches(MethodInfo m)
    {
        var ps = m.GetParameters();
        if (ps.Length != parameters!.Length) return false;
        for (var i = 0; i < ps.Length; i++)
        {
            var t = ps[i].ParameterType;
            if (t.IsByRef) t = t.GetElementType()!;
            if (t.Name != parameters[i] && t.FullName != parameters[i]) return false;
        }
        return true;
    }

    /// <summary>Calls the method (target null for static). Compiled on first use; boxes arguments.</summary>
    public object? Invoke(object? target, params object?[] args)
    {
        if (Method is null) throw new InvalidOperationException($"{Name} is not available");
        if (Method.ReturnType.IsByRef || Method.GetParameters().Any(p => p.ParameterType.IsByRef))
            throw new NotSupportedException($"{Name}: by-ref parameters/returns can't be invoked (hooking is fine)");
        _invoke ??= Compile(Method);
        return _invoke(target, args);
    }

    private static Func<object?, object?[], object?> Compile(MethodInfo m)
    {
        var target = Expression.Parameter(typeof(object), "target");
        var args   = Expression.Parameter(typeof(object?[]), "args");

        var ps   = m.GetParameters();
        var call = Expression.Call(
            m.IsStatic ? null : Expression.Convert(target, m.DeclaringType!),
            m,
            ps.Select((p, i) => (Expression)Expression.Convert(
                Expression.ArrayIndex(args, Expression.Constant(i)), p.ParameterType)));

        Expression body = m.ReturnType == typeof(void)
            ? Expression.Block(call, Expression.Constant(null, typeof(object)))
            : Expression.Convert(call, typeof(object));

        return Expression.Lambda<Func<object?, object?[], object?>>(body, target, args).Compile();
    }
}
