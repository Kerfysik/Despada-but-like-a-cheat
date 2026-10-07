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
