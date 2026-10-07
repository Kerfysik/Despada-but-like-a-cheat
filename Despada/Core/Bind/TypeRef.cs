// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using HarmonyLib;

namespace Despada.Core.Bind;

/// <summary>A type by full name; later names are fallbacks for renames between engine versions.</summary>
internal sealed class TypeRef(params string[] names) : Binding(names[0][(names[0].LastIndexOf('.') + 1)..])
{
    public Type? Type { get; private set; }

    protected override string? DoResolve()
    {
        foreach (var n in names)
        {
            Type = AccessTools.TypeByName(n);
            if (Type is not null) return null;
        }
        return $"type {string.Join(" / ", names)} not found";
    }
}
