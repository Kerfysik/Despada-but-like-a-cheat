// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Despada.Core;

/// <summary>The feature registry: availability, on/off (persisted), crash handling.</summary>
internal static partial class Features
{
    private static readonly List<Feature> _all = new();

    public static IReadOnlyList<Feature> All => _all;

    public static Feature? Find(string id) => _all.FirstOrDefault(f => f.Id == id);

    // Built-in features, in header-tab order.
    private static IEnumerable<Feature> BuiltIn() => [new Modules.Net.NetFeature()];

    public static void Initialize()
    {
        Hooks.Tripped += OnTripped;
        RegisterCommands();

        foreach (var f in BuiltIn())
            Register(f);

        Log.Info("core", $"features: {_all.Count(f => f.State == FeatureState.On)} on, " +
                         $"{_all.Count(f => f.State == FeatureState.Unavailable)} unavailable, {_all.Count} total");
    }

    // Hook owners used by the infrastructure; a feature with one of these ids would remove them.
    private static readonly string[] Reserved = ["core", "input", "render", "bind"];

    public static void Register(Feature f)
    {
        string id;
        try
        {
            id = f.Id;
        }
        catch (Exception ex)
        {
            Log.Error("core", $"feature {f.GetType().Name}: {ex.Message}");
            return;
        }
        if (Reserved.Contains(id) || Find(id) is not null)
        {
            Log.Error("core", $"feature id '{id}' is reserved or already registered — {f.GetType().Name} skipped");
            return;
        }

        _all.Add(f);
        try
        {
            RegisterChecked(f);
        }
        catch (Exception ex)
        {
            f.State  = FeatureState.Crashed;
            f.Reason = $"failed to register: {ex.Message}";
            Log.Error(f.Id, f.Reason);
        }
    }

    private static void RegisterChecked(Feature f)
    {
        foreach (var cmd in f.Commands)
            RegisterFeatureCommand(f, cmd);

        var missing = f.Requires.Where(b => { b.Resolve(); return !b.Ok; }).Select(b => b.Name).ToList();
        if (missing.Count > 0)
        {
            f.State  = FeatureState.Unavailable;
            f.Reason = $"not found: {string.Join(", ", missing)}";
            Log.Warn(f.Id, $"{f.Title} unavailable — {f.Reason}");
            return;
        }

        if (IsEnabled(f))
            Enable(f);
    }

    /// <summary>
    /// UI/console switch: applied on the game thread at the next tick, not in the middle of a
    /// rendered frame (patching Clyde.Render while it runs).
    /// </summary>
    public static void Request(Feature f, bool on) => Lifecycle.Post(() => SetEnabled(f, on));

    /// <summary>Switch on or off now; remembered in the config.</summary>
    public static void SetEnabled(Feature f, bool on)
    {
        SetConfigEnabled(f, on);
        if (on) Enable(f);
        else    Disable(f, null);
    }

    private static void Enable(Feature f)
    {
        if (f.State is FeatureState.On or FeatureState.Unavailable) return;

        try
        {
            f.OnEnable();
            var failed = Hooks.Of(f.Id).FirstOrDefault(h => h.State == Hooks.State.Failed);
            if (failed is not null)
                throw new InvalidOperationException($"hook {failed.Target}: {failed.Error}");

            f.State  = FeatureState.On;
            f.Reason = null;
            Log.Info(f.Id, $"{f.Title} on");
        }
        catch (Exception ex)
        {
            Disable(f, $"failed to start: {ex.Message}", startedPartly: true);
        }
    }

    private static void Disable(Feature f, string? crashReason, bool startedPartly = false)
    {
        if (f.State == FeatureState.Unavailable) return;

        // OnDisable only undoes an OnEnable that ran (fully, or partly before it threw).
        if (f.State == FeatureState.On || startedPartly)
        {
            try
            {
                f.OnDisable();
            }
            catch (Exception ex)
            {
                Log.Error(f.Id, $"OnDisable: {ex.Message}");
            }
        }

        Hooks.RemoveAll(f.Id);
        Lifecycle.RemoveTick(f.Id);

        f.State  = crashReason is null ? FeatureState.Off : FeatureState.Crashed;
        f.Reason = crashReason;
        if (crashReason is null) Log.Info(f.Id, $"{f.Title} off");
        else                     Log.Error(f.Id, $"{f.Title} crashed — {crashReason}");
    }

    // Hooks.Fail shut the owner off: reflect it in the feature (its hooks are already removed).
    private static void OnTripped(string owner, string reason)
    {
        var f = Find(owner);
        if (f is { State: FeatureState.On })
            Disable(f, reason);
    }
}
