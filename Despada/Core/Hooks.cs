// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using Despada.Core.Bind;
using HarmonyLib;

namespace Despada.Core;

/// <summary>
/// Every Harmony patch Despada applies, grouped by owner (a feature id, or "core" / "render" /
/// "input" for the infrastructure). All go through the one Harmony instance Despada already has
/// (no extra owner ids); an owner's patches can be removed individually at runtime.
/// </summary>
internal static partial class Hooks
{
    public enum State { Active, Failed, Removed }

    public sealed class Hook
    {
        public required string     Owner;
        public required string     Target;
        public MethodBase? Original;
        public MethodInfo? Patch;
        public State   State;
        public string?  Error;
    }

    private static readonly List<Hook> _hooks = new();

    public static IReadOnlyList<Hook> All => _hooks;

    public static IEnumerable<Hook> Of(string owner) => _hooks.Where(h => h.Owner == owner);

    public static bool Prefix(string owner, MethodRef target, string patchMethod, Type patchType)
        => Apply(owner, target, patchType, patchMethod, HarmonyPatchType.Prefix);

    public static bool Postfix(string owner, MethodRef target, string patchMethod, Type patchType)
        => Apply(owner, target, patchType, patchMethod, HarmonyPatchType.Postfix);

    public static bool Transpiler(string owner, MethodRef target, string patchMethod, Type patchType)
        => Apply(owner, target, patchType, patchMethod, HarmonyPatchType.Transpiler);

    public static bool Finalizer(string owner, MethodRef target, string patchMethod, Type patchType)
        => Apply(owner, target, patchType, patchMethod, HarmonyPatchType.Finalizer);

    /// <summary>
    /// Patches <paramref name="target"/> with <paramref name="patchType"/>.<paramref name="patchMethod"/>
    /// (any visibility, static). Never throws: a missing target or a failed patch is recorded and
    /// logged, and false is returned.
    /// </summary>
    private static bool Apply(string owner, MethodRef target, Type patchType, string patchMethod, HarmonyPatchType kind)
    {
        target.Resolve();
        MethodInfo? patch;
        try
        {
            patch = AccessTools.Method(patchType, patchMethod);
        }
        catch (Exception ex) // e.g. overloaded patch method name
        {
            return Record(owner, target.Name, null, null, $"patch method {patchType.Name}.{patchMethod}: {ex.Message}");
        }
        if (patch is null)
            return Record(owner, target.Name, null, null, $"patch method {patchType.Name}.{patchMethod} not found");
        if (target.Method is null)
            return Record(owner, target.Name, null, patch, target.Error ?? "target not found");

        // Harmony doesn't dedupe: applying twice would run the patch twice.
        if (_hooks.Any(h => h.Owner == owner && h.Original == target.Method && h.Patch == patch && h.State == State.Active))
            return true;

        try
        {
            var hm = new HarmonyMethod(patch);
            SubverterPatch.Harm.Patch(target.Method,
                prefix:     kind == HarmonyPatchType.Prefix ? hm : null,
                postfix:    kind == HarmonyPatchType.Postfix ? hm : null,
                transpiler: kind == HarmonyPatchType.Transpiler ? hm : null,
                finalizer:  kind == HarmonyPatchType.Finalizer ? hm : null);
            return Record(owner, target.Name, target.Method, patch, null);
        }
        catch (Exception ex)
        {
            return Record(owner, target.Name, target.Method, patch, ex.Message);
        }
    }

    private static bool Record(string owner, string target, MethodBase? original, MethodInfo? patch, string? error)
    {
        // Failures are recorded too (also when the target or patch method wasn't found), so a
        // feature's OnEnable sees them and the hook counts show them.
        var hook = _hooks.FirstOrDefault(h => h.Owner == owner && h.Target == target && h.Patch == patch);
        if (hook is null)
            _hooks.Add(hook = new Hook { Owner = owner, Target = target, Original = original, Patch = patch });

        hook.Original = original;
        hook.State = error is null ? State.Active : State.Failed;
        hook.Error = error;

        if (error is null)
            Log.Info(owner, $"hooked {target}");
        else
            Log.Error(owner, $"hook {target} failed: {error}");
        return error is null;
    }

    /// <summary>Removes every active patch of <paramref name="owner"/>.</summary>
    public static void RemoveAll(string owner)
    {
        foreach (var h in _hooks.Where(h => h.Owner == owner && h.State == State.Active && h.Original is not null && h.Patch is not null))
        {
            try
            {
                SubverterPatch.Harm.Unpatch(h.Original!, h.Patch!);
                h.State = State.Removed;
                Log.Info(owner, $"unhooked {h.Target}");
            }
            catch (Exception ex)
            {
                h.Error = ex.Message;
                Log.Error(owner, $"unhook {h.Target} failed: {ex.Message}");
            }
        }
    }
}
