// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Reflection;
using Despada.Core;

namespace Despada.Modules.Esp;

/// <summary>
/// Cerberus' icon set: instead of drawing sprites of our own, the game's own HUD components are
/// added to the local player's body — the client then draws antag, job, mind-shield, criminal-record
/// and syndicate icons over every mob exactly as it does for its own HUD.
///
/// Everything here is reflection by component <i>name</i> (no Content reference): a fork without,
/// say, syndicate icons simply never gets that toggle. <c>NetSyncEnabled</c> is switched off on
/// every component added, so nothing is sent to the server.
/// </summary>
internal static class EspIcons
{
    private static readonly string[] Names =
    [
        "ShowAntagIcons", "ShowJobIcons", "ShowMindShieldIcons", "ShowCriminalRecordIcons",
        "ShowSyndicateIcons", "SolutionScanner", "ShowElectrocutionHUD",
    ];

    /// <summary>Bitmask of the components currently added to the local body.</summary>
    private static int _applied;

    private static long _nextTry;

    /// <summary>Which of the seven icon components this client has (shown in the tab).</summary>
    public static string Report()
    {
        var missing = Names.Where(n => EspDetect.TypeOf(n) is null).ToList();
        return missing.Count == 0
            ? $"icon components {Names.Length}/{Names.Length} resolved"
            : $"icon components {Names.Length - missing.Count}/{Names.Length} resolved · missing {string.Join(", ", missing)}";
    }

    /// <summary>Brings the local player's icon components in line with the toggles.</summary>
    public static void Sync(bool antag, bool job, bool mind, bool criminal, bool syndicate, bool chemical, bool electro)
    {
        var want = 0;
        if (antag)     want |= 1 << 0;
        if (job)       want |= 1 << 1;
        if (mind)      want |= 1 << 2;
        if (criminal)  want |= 1 << 3;
        if (syndicate) want |= 1 << 4;
        if (chemical)  want |= 1 << 5;
        if (electro)   want |= 1 << 6;

        if (_applied == want) return;
        if (Environment.TickCount64 < _nextTry) return;
        _nextTry = Environment.TickCount64 + 500;

        var uid = EspDetect.LocalEntity();
        if (uid is null)
        {
            _applied = 0;
            return;
        }

        var em = Engine.EspEntities.Instance;
        if (em is null) return;

        for (var i = 0; i < Names.Length; i++)
        {
            var on  = (want & (1 << i)) != 0;
            var had = (_applied & (1 << i)) != 0;
            if (on == had) continue;

            // A component this fork doesn't have is "done": never retried, never logged again.
            if (EspDetect.TypeOf(Names[i]) is null)
            {
                _applied = on ? _applied | (1 << i) : _applied & ~(1 << i);
                continue;
            }

            try
            {
                if (on) Add(em, uid, Names[i]);
                else    Remove(em, uid, Names[i]);

                _applied = on ? _applied | (1 << i) : _applied & ~(1 << i);
            }
            catch (Exception ex)
            {
                MarseyLogger.Warn($"[EspIcons] {Names[i]}: {ex.Message}");
            }
        }
    }

    /// <summary>The local body changed (respawn): apply everything again.</summary>
    public static void Reset() => _applied = 0;

    private static void Add(object em, object uid, string name)
    {
        var type = EspDetect.TypeOf(name) ?? throw new InvalidOperationException("not on this server");
        if (Engine.EspHasComp.Invoke(em, uid, type) is true) return;

        var factory = EspDetect.Get(em, "ComponentFactory")
                      ?? throw new InvalidOperationException("no ComponentFactory");
        var comp = Call(factory, "GetComponent", name, false)
                   ?? throw new InvalidOperationException("GetComponent returned null");

        EspDetect.Set(comp, "NetSyncEnabled", false);

        // AddComponent<T>(EntityUid, T, bool, MetaDataComponent?): closed with the component's own
        // type, so no Robust/Content type is named here.
        foreach (var m in em.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.Name != "AddComponent" || !m.IsGenericMethodDefinition) continue;
            if (m.GetParameters().Length != 4) continue;

            try
            {
                m.MakeGenericMethod(comp.GetType()).Invoke(em, [uid, comp, false, null]);
                return;
            }
            catch (ArgumentException) { /* another generic overload; keep looking */ }
        }

        throw new InvalidOperationException("no AddComponent<T>(EntityUid, T, bool, MetaDataComponent)");
    }

    private static void Remove(object em, object uid, string name)
    {
        var type = EspDetect.TypeOf(name);
        if (type is null) return;
        if (Engine.EspHasComp.Invoke(em, uid, type) is not true) return;

        Call(em, "RemoveComponent", uid, type, null);
    }

    private static object? Call(object target, string name, params object?[] args)
    {
        foreach (var m in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (m.Name == name && m.GetParameters().Length == args.Length)
                return m.Invoke(target, args);
        return null;
    }
}