// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Despada.Core.Bind;
using Despada.ImGui.UserInterface;

namespace Despada.Core;

internal enum FeatureState
{
    /// <summary>Switched off.</summary>
    Off,
    On,
    /// <summary>Something in <see cref="Feature.Requires"/> wasn't found: can't be switched on.</summary>
    Unavailable,
    /// <summary>Failed to start, or shut off after repeated errors; can be switched on again.</summary>
    Crashed,
}

/// <summary>
/// One Despada feature (network log, event log, …): its hooks, tick, console commands and settings.
/// Hooks are installed with <c>Hooks.*(Id, …)</c> and ticks with <c>Lifecycle.OnTick(Id, …)</c>;
/// both are removed automatically when the feature is switched off or crashes.
/// </summary>
internal abstract class Feature
{
    /// <summary>Short id: hook owner, log category and config key ("net").</summary>
    public abstract string Id { get; }

    public abstract string Title { get; }

    public virtual string Description => "";

    /// <summary>Bindings that must resolve for the feature to be usable.</summary>
    public virtual IReadOnlyList<Binding> Requires => [];

    public virtual bool EnabledByDefault => false;

    public FeatureState State  { get; internal set; }

    /// <summary>Why it is <see cref="FeatureState.Unavailable"/> or <see cref="FeatureState.Crashed"/>.</summary>
    public string? Reason { get; internal set; }

    /// <summary>Install hooks/ticks. Throwing (or a hook that fails to apply) marks it crashed.</summary>
    protected internal abstract void OnEnable();

    /// <summary>Extra cleanup; hooks and ticks are removed by the registry anyway.</summary>
    protected internal virtual void OnDisable() { }

    /// <summary>Console commands; they report the feature's state instead of running while it's off.</summary>
    public virtual IEnumerable<ConsoleCommand> Commands => [];

    /// <summary>Label of the feature's tab in the header (e.g. "Network"); null = no tab.</summary>
    public virtual string? Tab => null;

    /// <summary>The feature's own controls in its tab's second row, after the on/off switch (while on).</summary>
    public virtual void DrawTabRow() { }

    /// <summary>The tab's content while the feature is on (its settings live here too).</summary>
    public virtual void DrawTab() { }

    protected T Setting<T>(string key, T fallback) => Features.GetSetting(Id, key, fallback);

    protected void SetSetting<T>(string key, T value) => Features.SetSetting(Id, key, value);
}
