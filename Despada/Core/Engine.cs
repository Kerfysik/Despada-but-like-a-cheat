// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.CompilerServices;
using Despada.Core.Bind;

namespace Despada.Core;

/// <summary>
/// Everything Despada uses from the engine/content, declared in one place (one file per area:
/// Engine.Game, Engine.Input, Engine.Render, …) and resolved once at startup. Missing members
/// don't throw: they are reported, and whatever needs them stays off.
/// </summary>
/// <remarks>
/// Partial across files, and C# doesn't order static initializers between files: a binding may
/// only reference bindings declared earlier in the same file. The list itself is created lazily
/// for the same reason.
/// </remarks>
internal static partial class Engine
{
    private static List<Binding>? _all;

    public static IReadOnlyList<Binding> All => _all ??= [];

    private static T Add<T>(T binding) where T : Binding
    {
        (_all ??= []).Add(binding);
        return binding;
    }

    private static TypeRef Type(params string[] names) => Add(new TypeRef(names));

    /// <param name="parameters">Parameter type names to pick an overload; omit when the name is unique.</param>
    private static MethodRef Method(TypeRef owner, string name, params string[]? parameters)
        => Add(new MethodRef(owner, [name], parameters is { Length: > 0 } ? parameters : null));

    private static MethodRef Method(TypeRef owner, string[] names, params string[]? parameters)
        => Add(new MethodRef(owner, names, parameters is { Length: > 0 } ? parameters : null));

    private static FieldRef Field(TypeRef owner, params string[] names) => Add(new FieldRef(owner, names));

    private static ManagerRef Manager(TypeRef service) => Add(new ManagerRef(service));

    /// <summary>Resolves every binding and logs one summary line (plus one line per missing one).</summary>
    public static void ResolveAll()
    {
        // Touch every partial's statics so their bindings are registered.
        RuntimeHelpers.RunClassConstructor(typeof(Engine).TypeHandle);

        foreach (var b in All)
            b.Resolve();

        var missing = All.Where(b => !b.Ok).ToList();
        Log.Info("bind", $"{All.Count - missing.Count}/{All.Count} engine bindings resolved");
        foreach (var b in missing)
            Log.Warn("bind", $"missing {b.Name}: {b.Error}");
    }
}
