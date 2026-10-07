// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections.Concurrent;
using Despada.Config;

namespace Despada.Core;

/// <summary>
/// Game-thread lifecycle: <see cref="OnTick"/> handlers run after every GameController.Update
/// (also while minimized), <see cref="OnShutdown"/> handlers when the game thread is cleaned up.
/// <see cref="Post"/> moves work from other threads (network, hooks) onto the game thread.
/// </summary>
internal static class Lifecycle
{
    private static readonly ConcurrentQueue<Action> _posted = new();
    private static readonly List<(string Owner, Action Run)> _tick = new();
    private static readonly List<(string Owner, Action Run)> _shutdown = new();
    private static (string Owner, Action Run)[] _tickNow = [];
    private static bool _shutDown;

    /// <summary>False if the Update hook couldn't be installed (ticks then run per rendered frame).</summary>
    public static bool TickHooked { get; private set; }

    /// <summary>The game thread (IoC and engine state live there).</summary>
    public static int MainThreadId { get; private set; } = -1;

    public static bool OnMainThread => Environment.CurrentManagedThreadId == MainThreadId;

    public static void Install()
    {
        MainThreadId = Environment.CurrentManagedThreadId;
        TickHooked = Hooks.Postfix("core", Engine.GameUpdate, nameof(UpdatePostfix), typeof(Lifecycle));
        Hooks.Prefix("core", Engine.GameCleanup, nameof(CleanupPrefix), typeof(Lifecycle));
    }

    public static void OnTick(string owner, Action run)
    {
        _tick.Add((owner, run));
        _tickNow = _tick.ToArray();
    }

    public static void RemoveTick(string owner)
    {
        _tick.RemoveAll(t => t.Owner == owner);
        _tickNow = _tick.ToArray();
    }

    public static void OnShutdown(string owner, Action run) => _shutdown.Add((owner, run));

    /// <summary>Runs <paramref name="work"/> on the game thread at the next tick (or frame).</summary>
    public static void Post(Action work) => _posted.Enqueue(work);

    /// <summary>Runs posted work. Called every tick, and every rendered frame as a fallback.</summary>
    public static void Pump()
    {
        while (_posted.TryDequeue(out var work))
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                Log.Error("core", $"posted work failed: {ex}");
            }
        }
    }

    private static void UpdatePostfix() => RunTick();

    /// <summary>One tick: posted work, then every OnTick handler. Also the per-frame fallback.</summary>
    public static void RunTick()
    {
        try
        {
            MainThreadId = Environment.CurrentManagedThreadId;
            Pump();
            foreach (var (owner, run) in _tickNow)
            {
                try
                {
                    run();
                }
                catch (Exception ex)
                {
                    Hooks.Fail(owner, "tick", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("core", $"tick: {ex}");
        }
    }

    private static void CleanupPrefix()
    {
        if (_shutDown) return;
        _shutDown = true;

        foreach (var (owner, run) in _shutdown)
        {
            try
            {
                run();
            }
            catch (Exception ex)
            {
                Log.Error(owner, $"shutdown: {ex.Message}");
            }
        }
        DespadaConfig.Flush();
        Log.Info("core", "shutdown");
    }
}
