// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Text.Json;
using Despada.Config;

namespace Despada.Core;

// Feature state in config.json: "features": { "<id>": { "enabled": true, "values": { … } } }.
internal static partial class Features
{
    private static bool IsEnabled(Feature f)
        => DespadaConfig.Current.Features.TryGetValue(f.Id, out var s) && s.Enabled is { } on ? on : f.EnabledByDefault;

    private static void SetConfigEnabled(Feature f, bool on)
        => DespadaConfig.Update(c => Entry(c, f.Id).Enabled = on);

    public static T GetSetting<T>(string id, string key, T fallback)
    {
        if (!DespadaConfig.Current.Features.TryGetValue(id, out var s) || !s.Values.TryGetValue(key, out var el))
            return fallback;
        try
        {
            return el.Deserialize<T>() ?? fallback;
        }
        catch (Exception)
        {
            return fallback; // malformed or wrong-typed value in a hand-edited config
        }
    }

    public static void SetSetting<T>(string id, string key, T value)
        => DespadaConfig.Update(c => Entry(c, id).Values[key] = JsonSerializer.SerializeToElement(value, ValueOptions));

    /// <summary>
    /// A list setting read item by item: one bad item (a hand edit, a newer build's shape) is
    /// skipped and returned raw, not the whole list; <see cref="SetSettingItems{T}"/> writes the
    /// raw ones back unchanged, so a save never erases what couldn't be read.
    /// </summary>
    public static (List<T> Items, List<JsonElement> Unread) GetSettingItems<T>(string id, string key)
    {
        var items  = new List<T>();
        var unread = new List<JsonElement>();
        if (!DespadaConfig.Current.Features.TryGetValue(id, out var s) || !s.Values.TryGetValue(key, out var el) || el.ValueKind != JsonValueKind.Array)
            return (items, unread);
        foreach (var e in el.EnumerateArray())
        {
            try
            {
                if (e.Deserialize<T>(ValueOptions) is { } item) items.Add(item);
                else unread.Add(e.Clone());
            }
            catch (Exception ex)
            {
                Log.Warn("config", $"{id}.{key}: an item kept as is, not read: {ex.Message}");
                unread.Add(e.Clone());
            }
        }
        return (items, unread);
    }

    public static void SetSettingItems<T>(string id, string key, IEnumerable<T> items, IEnumerable<JsonElement> unread)
    {
        var all = items.Select(i => JsonSerializer.SerializeToElement(i, ValueOptions)).Concat(unread).ToList();
        DespadaConfig.Update(c => Entry(c, id).Values[key] = JsonSerializer.SerializeToElement(all, ValueOptions));
    }

    private static readonly JsonSerializerOptions ValueOptions = new()
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private static FeatureSettings Entry(DespadaSettings c, string id)
    {
        if (!c.Features.TryGetValue(id, out var s))
            c.Features[id] = s = new FeatureSettings();
        return s;
    }
}
