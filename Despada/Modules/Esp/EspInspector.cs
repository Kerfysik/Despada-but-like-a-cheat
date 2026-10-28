// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections;

namespace Despada.Modules.Esp;

/// <summary>
/// Cerberus' StorageViewer and ClickDamage, as data: what a player is carrying (pockets, back,
/// belt, ID, suit storage, implants — plus nested bags) and how much damage of which kind is on
/// them. The panels themselves are drawn by <see cref="EspFeature"/>, with the same font as the
/// rest of the HUD.
/// </summary>
internal static class EspInspector
{
    /// <summary>Container ids on a humanoid, and how the panel labels them.</summary>
    public static readonly (string Id, string Label)[] Slots =
    [
        ("hand1",       "Hand 1"),
        ("hand2",       "Hand 2"),
        ("pocket1",     "Pocket 1"),
        ("pocket2",     "Pocket 2"),
        ("back",        "Back"),
        ("belt",        "Belt"),
        ("suitstorage", "Suit storage"),
        ("id",          "ID"),
        ("shoes",       "Shoes"),
        ("implant",     "Implants"),
    ];

    /// <summary>Every non-empty container of the entity, contents grouped by name.</summary>
    public static List<(string Label, List<(string Name, int Count)> Items)> Storage(object uid)
    {
        var result = new List<(string, List<(string, int)>)>();

        foreach (var (id, label) in Slots)
        {
            var box = EspDetect.Box(uid, id);
            if (box is null) continue;

            var items = Group(box);
            if (items.Count > 0) result.Add((label, items));
        }

        return result;
    }

    private static List<(string Name, int Count)> Group(object box)
    {
        var counts = new Dictionary<string, int>();
        var order  = new List<string>();

        void Add(object item)
        {
            var name = EspDetect.Name(item) ?? "?";
            if (!counts.ContainsKey(name))
            {
                counts[name] = 0;
                order.Add(name);
            }
            counts[name]++;
        }

        foreach (var item in EspDetect.Inside(box))
        {
            Add(item);

            // One level into bags worn or held: a backpack inside the back slot, a box in a pocket.
            if (EspDetect.Comp("Storage", item) is { } storage
                && EspDetect.Get(storage, "Container") is { } inner)
                foreach (var deep in EspDetect.Inside(inner))
                    Add(deep);
        }

        return order.Select(n => (n, counts[n])).ToList();
    }

    /// <summary>
    /// Ammo in everything the body carries (hands included): the ballistic provider stores the
    /// loaded count and the magazine size, which is what Cerberus' Magazine ESP showed next to a gun.
    /// </summary>
    public static List<(string Name, int Count, int Capacity)> Ammo(object uid)
    {
        var result = new List<(string, int, int)>();

        foreach (var item in EspDetect.Everything(uid))
        {
            var provider = EspDetect.Comp("BallisticAmmoProvider", item);
            if (provider is null) continue;

            var count    = (int)MathF.Round(EspDetect.ToFloat(EspDetect.Get(provider, "Count")));
            var capacity = (int)MathF.Round(EspDetect.ToFloat(EspDetect.Get(provider, "Capacity")));
            if (capacity <= 0 && count <= 0) continue;

            result.Add((EspDetect.Name(item) ?? "?", count, capacity));
        }

        return result;
    }

    /// <summary>The entity's total damage and its per-type breakdown.</summary>
    public static List<(string Type, float Amount)> Damage(object uid, out float total)
    {
        total = 0f;
        var types = new List<(string, float)>();

        var dmg = EspDetect.Comp("Damageable", uid);
        if (dmg is null) return types;

        total = EspDetect.ToFloat(EspDetect.Get(dmg, "TotalDamage"));

        if (EspDetect.Get(dmg, "Damage") is not { } spec) return types;
        if (EspDetect.Get(spec, "DamageDict") is not IDictionary dict) return types;

        foreach (DictionaryEntry e in dict)
        {
            var amount = EspDetect.ToFloat(e.Value);
            if (amount <= 0f) continue;
            types.Add((e.Key?.ToString() ?? "?", amount));
        }

        types.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        return types;
    }
}