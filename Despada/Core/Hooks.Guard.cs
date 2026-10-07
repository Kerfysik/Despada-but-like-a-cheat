// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Despada.Core;

internal static partial class Hooks
{
    // An owner whose patches throw this often is shut off: its hooks are removed and, for a
    // feature, the feature is disabled with the error as the reason.
    private const int      MaxFailures   = 3;
    private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(1);

    private static readonly Dictionary<string, Queue<DateTime>> _failures = new();
    private static readonly object _failLock = new();

    /// <summary>Raised on the game thread (via Lifecycle.Post) after an owner was shut off: owner, reason.</summary>
    public static event Action<string, string>? Tripped;

    /// <summary>
    /// Report an exception caught inside a patch body. Patch bodies catch everything themselves
    /// (nothing may escape into the engine) and call this:
    /// <code>catch (Exception ex) { Hooks.Fail("net", "ClientSendMessage", ex); }</code>
    /// </summary>
    public static void Fail(string owner, string where, Exception ex)
    {
        Log.Error(owner, $"{where}: {ex.GetType().Name}: {ex.Message}");

        bool trip;
        lock (_failLock)
        {
            if (!_failures.TryGetValue(owner, out var q))
                _failures[owner] = q = new Queue<DateTime>();

            var now = DateTime.UtcNow;
            q.Enqueue(now);
            while (q.Count > 0 && now - q.Peek() > FailureWindow) q.Dequeue();
            trip = q.Count >= MaxFailures;
            if (trip) q.Clear();
        }

        if (!trip) return;

        var reason = $"{where} failed {MaxFailures}× within a minute: {ex.Message}";
        Log.Error(owner, $"shut off — {reason}");
        Lifecycle.Post(() =>
        {
            RemoveAll(owner);
            Tripped?.Invoke(owner, reason);
        });
    }
}
