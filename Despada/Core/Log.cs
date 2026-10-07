// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Despada.Core;

/// <summary>
/// MarseyLogger with a category tag and repeat collapsing: the same message (per category) logged
/// again within <see cref="Window"/> is counted instead of written, and the count is appended the
/// next time it does get written. Keeps a hook that fails every frame from flooding the log.
/// </summary>
internal static class Log
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private sealed class Entry
    {
        public DateTime LastWritten;
        public int      Suppressed;
    }

    private static readonly Dictionary<string, Entry> _recent = new();
    private static readonly object _lock = new();

    public static void Info(string category, string message)  => Write(category, message, MarseyLogger.Info);
    public static void Warn(string category, string message)  => Write(category, message, MarseyLogger.Warn);
    public static void Error(string category, string message) => Write(category, message, MarseyLogger.Fatal);
    public static void Debug(string category, string message) => Write(category, message, MarseyLogger.Debug);

    private static void Write(string category, string message, Action<string> sink)
    {
        var key = category + "\n" + message;
        var now = DateTime.UtcNow;
        int repeats;

        lock (_lock)
        {
            if (_recent.TryGetValue(key, out var e) && now - e.LastWritten < Window)
            {
                e.Suppressed++;
                return;
            }

            e ??= new Entry();
            repeats = e.Suppressed;
            e.LastWritten = now;
            e.Suppressed = 0;
            _recent[key] = e;

            // Forget old entries now and then; messages with ids in them never repeat exactly.
            if (_recent.Count > 512)
                foreach (var k in _recent.Where(p => now - p.Value.LastWritten > Window).Select(p => p.Key).ToList())
                    _recent.Remove(k);
        }

        sink(repeats > 0 ? $"[{category}] {message} (+{repeats} repeats)" : $"[{category}] {message}");
    }
}
