// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;

namespace Despada.Core.Bind;

/// <summary>
/// An engine/content manager resolved from the IoC container (<c>IoCManager.Resolve&lt;T&gt;()</c>,
/// built by reflection). Robust's container is thread-local, so <see cref="Instance"/> only works
/// on the main thread; the instance is cached once found.
/// </summary>
internal sealed class ManagerRef(TypeRef service) : Binding(service.Name)
{
    private static MethodInfo? _resolveDef;

    private Func<object>? _resolve;
    private object?       _instance;

    public Type? Type => service.Type;

    protected override string? DoResolve()
    {
        service.Resolve();
        if (service.Type is null) return service.Error;

        _resolveDef ??= AccessTools.TypeByName("Robust.Shared.IoC.IoCManager")?
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "Resolve" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        if (_resolveDef is null) return "IoCManager.Resolve<T>() not found";

        var call = Expression.Call(_resolveDef.MakeGenericMethod(service.Type));
        _resolve = Expression.Lambda<Func<object>>(Expression.Convert(call, typeof(object))).Compile();
        return null;
    }

    /// <summary>The manager, or null if unavailable / not on the main thread / IoC not ready yet.</summary>
    public object? Instance
    {
        get
        {
            // Engine managers aren't thread-safe and the IoC container is per thread.
            if (Lifecycle.MainThreadId != -1 && !Lifecycle.OnMainThread) return null;
            if (_instance is not null || _resolve is null) return _instance;
            try
            {
                _instance = _resolve();
            }
            catch
            {
                // Not registered (yet), or a thread without a container.
            }
            return _instance;
        }
    }
}
