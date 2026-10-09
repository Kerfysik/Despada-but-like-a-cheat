// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using System.Reflection;
using Despada.Core;

namespace Despada.Modules.Net;

/// <summary>
/// Every type that can go over the wire, read from the loaded engine and content assemblies (by
/// reflection only, nothing is registered): NetMessage subclasses, network events (EntityEventArgs
/// marked [NetSerializable]) and BUI messages (as "BUI.Name", the way MsgEntity packets show them).
/// Scanned on a worker thread, again when new assemblies have loaded (content comes in on connect).
/// </summary>
internal static class NetTypes
{
    public enum Kind { Message, Event, Bui }

    private const string EventBase = "Robust.Shared.GameObjects.EntityEventArgs";
    private const string BuiBase   = "Robust.Shared.GameObjects.BoundUserInterfaceMessage";
    private const string NetSerializable = "NetSerializableAttribute";

    private const string WrapBase = "Robust.Shared.GameObjects.BaseBoundUIWrapMessage";   // carries a BUI message, shown as BUI.X

    private static volatile (string Name, Kind Kind)[] _all = [];
    private static volatile string[] _names = [];
    private static int  _scanning;
    private static volatile int _state;   // 0 not read yet, 1 read, 2 failed

    /// <summary>The first scan finished (the lists may still grow when content loads).</summary>
    public static bool Ready => _state == 1;
    public static bool Failed => _state == 2;
    private static int  _scannedAssemblies = -1;
    private static long _lastCheck;

    /// <summary>All known wire types, sorted by name; empty until the first scan finishes.</summary>
    public static IReadOnlyList<(string Name, Kind Kind)> All
    {
        get { Refresh(); return _all; }
    }

    /// <summary>Just the names, for completions.</summary>
    public static IReadOnlyList<string> Names
    {
        get { Refresh(); return _names; }
    }

    // At most every 2 s: if engine or content assemblies were added, scan again in the background.
    private static void Refresh()
    {
        var now = Stopwatch.GetTimestamp();
        if (_scannedAssemblies >= 0 && Stopwatch.GetElapsedTime(_lastCheck, now).TotalSeconds < 2) return;
        _lastCheck = now;
        var game = AppDomain.CurrentDomain.GetAssemblies().Where(IsGameAssembly).ToArray();
        if (game.Length == _scannedAssemblies) return;
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        _scannedAssemblies = game.Length;
        Task.Run(() =>
        {
            try
            {
                var found = Scan(game, Engine.NetMessage.Type);
                _all   = found.ToArray();
                _names = found.Select(t => t.Name).ToArray();
                _state = 1;
            }
            catch (Exception ex)
            {
                _state = 2;
                _scannedAssemblies = -1;   // try again at the next check
                Log.Warn("net", $"type scan failed: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _scanning, 0);
            }
        });
    }

    private static bool IsGameAssembly(Assembly a)
    {
        var name = a.GetName().Name ?? "";
        return name.StartsWith("Robust.", StringComparison.Ordinal) || name.StartsWith("Content.", StringComparison.Ordinal);
    }

    /// <summary>The wire types defined in <paramref name="assemblies"/>, sorted by name. Exposed for tests.</summary>
    internal static List<(string Name, Kind Kind)> Scan(IEnumerable<Assembly> assemblies, Type? netMessage)
    {
        var found = new Dictionary<string, Kind>(StringComparer.Ordinal);
        foreach (var asm in assemblies)
        {
            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }   // what did load
            catch { continue; }                                            // this assembly only

            foreach (var t in types)
            {
                if (t is null || t.IsAbstract || t.IsGenericTypeDefinition) continue;
                try
                {
                    if (netMessage is not null && t.IsSubclassOf(netMessage))
                        found.TryAdd(t.Name, Kind.Message);
                    else if (DerivesFrom(t, BuiBase))
                    {
                        if (IsNetSerializable(t)) found.TryAdd("BUI." + t.Name, Kind.Bui);
                    }
                    else if (DerivesFrom(t, EventBase) && !DerivesFrom(t, WrapBase) && IsNetSerializable(t))
                        found.TryAdd(t.Name, Kind.Event);
                }
                catch
                {
                    // A type whose base or attributes can't be loaded: not one of ours anyway.
                }
            }
        }
        return found.Select(kv => (kv.Key, kv.Value)).OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
    }

    private static bool IsNetSerializable(Type t)
        => t.CustomAttributes.Any(a => a.AttributeType.Name == NetSerializable);

    private static bool DerivesFrom(Type t, string baseName)
    {
        for (var b = t.BaseType; b is not null; b = b.BaseType)
            if (b.FullName == baseName) return true;
        return false;
    }
}
