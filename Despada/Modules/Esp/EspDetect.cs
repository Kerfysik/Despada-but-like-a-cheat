// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections;
using System.Linq;
using System.Reflection;
using Despada.Core;

namespace Despada.Modules.Esp;

/// <summary>
/// The label checks, done the way Cerberus did them but by component <i>name</i>: the factory
/// turns "Heretic" into its Type once (cached, re-checked if this server doesn't have it), and
/// <c>HasComponent</c> answers per frame — so nothing here needs Content referenced, and a
/// component this fork doesn't have simply never shows its label.
/// </summary>
internal static class EspDetect
{
    private static readonly Dictionary<string, Type?> _types = new();
    private static long _retryAt;

    // --- Components by name --------------------------------------------------------------------

    /// <summary>The registered Type of a component name, null when this server has no such component.</summary>
    public static Type? TypeOf(string name)
    {
        if (_types.TryGetValue(name, out var t) && (t is not null || Environment.TickCount64 < _retryAt))
            return t;

        t = Lookup(name);
        _types[name] = t;
        if (t is null) _retryAt = Environment.TickCount64 + 3000;   // content may still be loading
        return t;
    }

    private static Type? Lookup(string name)
    {
        try
        {
            var factory = Engine.EspCompMgr.Instance;
            if (factory is null || !Engine.EspHasReg.Ok || !Engine.EspGetReg.Ok || !Engine.EspRegType.Ok)
                return null;
            if (!(bool)Engine.EspHasReg.Invoke(factory, name)!)
                return null;
            return Engine.EspGetReg.Invoke(factory, name, false) is { } reg
                ? Engine.EspRegType.Invoke(reg) as Type
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Which of the label components this client actually has, for the ESP tab: a server without,
    /// say, Heretic simply never shows that line, and the tab should say so instead of looking broken.
    /// </summary>
    public static string Report()
    {
        var missing = new List<string>();
        var ok      = 0;
        foreach (var name in Probe)
        {
            if (TypeOf(name) is null) missing.Add(name);
            else ok++;
        }

        return missing.Count == 0
            ? $"label components {ok}/{Probe.Length} resolved"
            : $"label components {ok}/{Probe.Length} resolved · missing {string.Join(", ", missing)}";
    }

    private static readonly string[] Probe =
    [
        "MetaData", "Transform", "ContainerManager", "CombatMode", "Contraband", "NoSlip",
        "Heretic", "Vampire", "FleshCultist", "PendingZombie", "Changeling", "CosmicCult",
        "CheatDeath", "BlobCarrier", "StoreDiscount",
    ];

    /// <summary>Does this entity have the named component?</summary>
    public static bool Has(string component, object uid)
    {
        var type = TypeOf(component);
        if (type is null) return false;
        var em = Engine.EspEntities.Instance;
        if (em is null || !Engine.EspHasComp.Ok) return false;
        try { return (bool)Engine.EspHasComp.Invoke(em, uid, type)!; }
        catch { return false; }   // entity went away between the exists check and here
    }

    /// <summary>The named component instance on this entity, or null.</summary>
    public static object? Comp(string component, object uid)
    {
        var type = TypeOf(component);
        if (type is null) return null;
        var em = Engine.EspEntities.Instance;
        if (em is null || !Engine.EspHasComp.Ok || !Engine.EspGetComp.Ok) return null;
        try
        {
            // HasComponent first: GetComponent throws for a missing component, and this runs for
            // every player every frame.
            if (!(bool)Engine.EspHasComp.Invoke(em, uid, type)!) return null;
            return Engine.EspGetComp.Invoke(em, uid, type);
        }
        catch { return null; }
    }

    // --- HUD values (Cerberus' health/stamina bars and job title) --------------------------------

    private static readonly Dictionary<(Type, string), MemberInfo?> _members = new();

    private static MemberInfo? Member(Type type, string name)
    {
        var key = (type, name);
        if (_members.TryGetValue(key, out var m)) return m;
        try { m = (MemberInfo?)type.GetProperty(name) ?? type.GetField(name); }
        catch { m = null; }
        _members[key] = m;
        return m;
    }

    private static object? Read(object target, string name)
    {
        try
        {
            return Member(target.GetType(), name) switch
            {
                PropertyInfo p => p.GetValue(target),
                FieldInfo f    => f.GetValue(target),
                _              => null,
            };
        }
        catch { return null; }
    }

    /// <summary>A member's value by name (the eye/visuals code reads the local player this way).</summary>
    public static object? Get(object target, string name) => Read(target, name);

    /// <summary>Writes a property or field by name; false when it isn't writable here.</summary>
    public static bool Set(object target, string name, object? value)
    {
        try
        {
            switch (Member(target.GetType(), name))
            {
                case PropertyInfo { CanWrite: true } p: p.SetValue(target, value); return true;
                case FieldInfo f:                        f.SetValue(target, value); return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>The local player's body, or null (main menu, spectator).</summary>
    public static object? LocalEntity()
    {
        var mgr = Engine.EspPlayerMgr.Instance;
        if (mgr is null || !Engine.EspLocalEntity.Ok) return null;
        try { return Engine.EspLocalEntity.Invoke(mgr); } catch { return null; }
    }

    /// <summary>The entity's display name ("Sierfint Lampseeker"), or null.</summary>
    public static string? Name(object uid)
    {
        var meta = Comp("MetaData", uid);
        if (meta is null || !Engine.EspEntName.Ok) return null;
        try { return Engine.EspEntName.Invoke(meta) as string; } catch { return null; }
    }

    /// <summary>FixedPoint2 (or a plain number) as float, for the inspector panels.</summary>
    public static float ToFloat(object? value) => Fp(value);

    private static float Number(object? value)
    {
        try { return value is null ? 0f : Convert.ToSingle(value); }
        catch { return 0f; }
    }

    /// <summary>FixedPoint2 → float, through its own Float() (it is not IConvertible).</summary>
    private static float Fp(object? value)
    {
        if (value is null) return 0f;
        try
        {
            // Float() is a method — Member() only looks at properties/fields.
            var m = value.GetType().GetMethod("Float", BindingFlags.Public | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            if (m is not null) return Convert.ToSingle(m.Invoke(value, null));
        }
        catch { }
        return Number(value);
    }

    /// <summary>
    /// The MobState → threshold map, whatever it is called on this build: Cerberus read
    /// <c>_thresholds</c>, but forks rename or expose it, so any dictionary on the component counts.
    /// </summary>
    private static float Threshold(object? thresholds, int state)
    {
        if (thresholds is null) return 0f;
        try
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var m in thresholds.GetType().GetMembers(all))
            {
                object? value = m switch
                {
                    PropertyInfo p => p.GetValue(thresholds),
                    FieldInfo f    => f.GetValue(thresholds),
                    _              => null,
                };
                if (value is not IDictionary dict) continue;

                foreach (DictionaryEntry e in dict)
                    if (Convert.ToInt32(e.Key) == state) return Fp(e.Value);
            }

            // Some builds keep the map private behind a helper instead of a readable member.
            foreach (var m in thresholds.GetType().GetMethods(all))
            {
                if (m.Name != "TryGetThresholdForState") continue;
                var ps = m.GetParameters();
                if (ps.Length != 2) continue;

                var args = new object?[] { Enum.ToObject(ps[0].ParameterType, state), null };
                if (m.Invoke(thresholds, args) is true && args[1] is { } found) return Fp(found);
            }
        }
        catch { }
        return 0f;
    }

    /// <summary>Why the last <see cref="Health"/> call failed or how it got its numbers (shown in the tab).</summary>
    public static string HealthWhy { get; private set; } = "";

    /// <summary>Health as 0..1, plus "in crit or dead" (Cerberus' health bar).</summary>
    public static bool Health(object uid, out float ratio, out bool bad)
    {
        ratio = 0f;
        bad   = false;

        var dmg = Comp("Damageable", uid);
        if (dmg is null)
        {
            HealthWhy = "no Damageable";
            return false;
        }

        var thr  = Comp("MobThresholds", uid);
        var crit = Threshold(thr, 2);   // MobState.Critical
        var fallback = crit <= 0f;
        if (fallback) crit = 100f;      // SS14's default crit threshold, Cerberus' own fallback

        var total = Fp(Read(dmg, "TotalDamage"));

        var mob = 0;
        if (Comp("MobState", uid) is { } state)
        {
            try { mob = Convert.ToInt32(Read(state, "CurrentState") ?? 0); }
            catch { }
        }

        if (mob == 3)
        {
            bad = true;
            HealthWhy = "dead";
            return true;
        }
        if (mob == 2) bad = true;   // critical: red

        ratio = Math.Clamp(1f - total / crit, 0f, 1f);
        HealthWhy = fallback ? $"no thresholds · damage {total:0.#}/100" : $"ok {total:0.#}/{crit:0.#}";
        return true;
    }

    /// <summary>Stamina as 0..1 (1 = full), Cerberus' stamina bar.</summary>
    public static bool Stamina(object uid, out float ratio)
    {
        ratio = 0f;
        var stam = Comp("Stamina", uid);
        if (stam is null) return false;

        var crit   = Number(Read(stam, "CritThreshold"));
        var damage = Read(stam, "StaminaDamage");
        if (crit <= 0f || damage is null) return false;

        ratio = Math.Clamp(1f - Number(damage) / crit, 0f, 1f);
        return true;
    }

    /// <summary>The job title from the ID card in the "id" container (Cerberus' role line).</summary>
    public static string? Job(object uid)
    {
        foreach (var card in Inside(uid, "id"))
        {
            var id = Comp("IdCard", card);
            if (id is null) continue;

            if (Read(id, "LocalizedJobTitle") is string title && !string.IsNullOrWhiteSpace(title))
                return title;

            // No localised title: the card's own name ends in "(Job)".
            var meta = Comp("MetaData", card);
            var name = meta is null ? null : Engine.EspEntName.Invoke(meta) as string;
            if (name is not null)
            {
                var m = System.Text.RegularExpressions.Regex.Match(name, @"\(([^)]*)\)");
                if (m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value))
                    return m.Groups[1].Value;
            }

            return "Unknown";
        }
        return null;
    }

    // --- Carrying things ------------------------------------------------------------------------

    /// <summary>
    /// The entity's container map. Builds differ: some expose a <c>Containers</c> property, others
    /// keep <c>_containers</c>, and the exposed one is an <c>IReadOnlyDictionary</c> — which is
    /// <i>not</i> an <see cref="IDictionary"/>, so casting to it silently returned null and the
    /// StorageViewer said "nothing carried". The map is found by shape (a string-keyed
    /// TryGetValue) and cached per component type.
    /// </summary>
    private static readonly Dictionary<Type, MemberInfo?> _containerMember = new();
    private static readonly Dictionary<Type, MethodInfo?>   _containerGet    = new();

    private static MemberInfo? ContainerMember(Type type)
    {
        if (_containerMember.TryGetValue(type, out var cached)) return cached;

        MemberInfo? found = null;
        try
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (var m in type.GetMembers(all))
            {
                var mt = m switch
                {
                    FieldInfo f    => f.FieldType,
                    PropertyInfo p => p.PropertyType,
                    _              => null,
                };
                if (mt is null) continue;

                foreach (var method in mt.GetMethods())
                {
                    if (method.Name != "TryGetValue") continue;
                    var ps = method.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(string))
                    {
                        found = m;
                        break;
                    }
                }
                if (found is not null) break;
            }
        }
        catch { found = null; }

        _containerMember[type] = found;
        return found;
    }

    private static object? ContainerMap(object uid)
    {
        var comp = Comp("ContainerManager", uid);
        if (comp is null) return null;

        var member = ContainerMember(comp.GetType());
        if (member is null) return null;

        try
        {
            return member switch
            {
                FieldInfo f    => f.GetValue(comp),
                PropertyInfo p => p.GetValue(comp),
                _              => null,
            };
        }
        catch { return null; }
    }

    /// <summary>One container by id ("pocket1", "back", "id", "hand1"), or null.</summary>
    public static object? Box(object uid, string id)
    {
        var map = ContainerMap(uid);
        if (map is null) return null;

        var type = map.GetType();
        if (!_containerGet.TryGetValue(type, out var method))
        {
            method = null;
            try
            {
                const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                foreach (var m in type.GetMethods(all))
                {
                    if (m.Name != "TryGetValue") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(string)) { method = m; break; }
                }
            }
            catch { method = null; }
            _containerGet[type] = method;
        }

        if (method is null) return null;

        try
        {
            var args = new object?[] { id, null };
            return method.Invoke(map, args) is true ? args[1] : null;
        }
        catch { return null; }
    }

    /// <summary>Every container on the entity (the map's values), or empty.</summary>
    public static IEnumerable<object> Boxes(object uid)
    {
        var map = ContainerMap(uid);
        if (map is null || map is not IEnumerable entries) yield break;

        foreach (var entry in entries)
        {
            if (entry is null) continue;

            object? value;
            try { value = entry.GetType().GetProperty("Value")?.GetValue(entry); }
            catch { continue; }
            if (value is not null) yield return value;
        }
    }

    /// <summary>Contents of one container; empty when there is no such container.</summary>
    public static IEnumerable<object> Inside(object uid, string id) => Inside(Box(uid, id));

    /// <summary>Contents of one container, read one entity at a time (an entity can vanish mid-loop).</summary>
    public static IEnumerable<object> Inside(object? box)
    {
        if (box is null || !Engine.EspContained.Ok) yield break;

        IEnumerator? it = null;
        try { it = (Engine.EspContained.Invoke(box) as IEnumerable)?.GetEnumerator(); }
        catch { yield break; }
        if (it is null) yield break;

        while (true)
        {
            bool moved;
            try { moved = it.MoveNext(); }
            catch { yield break; }
            if (!moved) yield break;
            if (it.Current is { } current) yield return current;
        }
    }

    /// <summary>Everything the entity holds, in every container.</summary>
    public static IEnumerable<object> Everything(object uid)
    {
        foreach (var box in Boxes(uid))
            foreach (var item in Inside(box))
                yield return item;
    }

    /// <summary>The prototype id of an entity ("WeaponPistolMk58"), or null.</summary>
    public static string? ProtoId(object uid)
    {
        var em = Engine.EspEntities.Instance;
        if (em is null || Engine.EspMeta.Type is null || !Engine.EspGetComp.Ok
            || !Engine.EspEntProto.Ok || !Engine.EspProtoId.Ok)
            return null;
        try
        {
            var meta   = Engine.EspGetComp.Invoke(em, uid, Engine.EspMeta.Type);
            var proto  = meta is null ? null : Engine.EspEntProto.Invoke(meta);
            return proto as string ?? (proto is null ? null : Engine.EspProtoId.Invoke(proto) as string);
        }
        catch
        {
            return null;
        }
    }

    // --- Antags (Cerberus' AntagDetector) -------------------------------------------------------

    public static bool Agent(object uid)      => Inside(uid, "id").Any(e => Has("StoreDiscount", e));
    public static bool Heretic(object uid)    => Has("Heretic", uid);
    public static bool Vampire(object uid)    => Has("Vampire", uid);
    public static bool FleshCult(object uid)  => Has("FleshCultist", uid);
    public static bool Zombie(object uid)     => Has("PendingZombie", uid);
    public static bool Changeling(object uid) => Has("Changeling", uid);
    public static bool CosmicCult(object uid) => Has("CosmicCult", uid);
    public static bool Devil(object uid)      => Has("CheatDeath", uid);
    public static bool Blob(object uid)       => Has("BlobCarrier", uid);

    /// <summary>Anything he stole sits in a container somewhere on him, prototype id says "Thief".</summary>
    public static bool Thief(object uid) => ThiefInside(uid, 4);

    private static bool ThiefInside(object uid, int depth)
    {
        foreach (var box in Boxes(uid))
        {
            foreach (var item in Inside(box))
            {
                if (ProtoId(item)?.Contains("Thief", StringComparison.Ordinal) == true) return true;
                if (depth > 0 && ThiefInside(item, depth - 1)) return true;
            }
        }
        return false;
    }

    // --- What they are carrying (Cerberus' ContrabandDetector) ------------------------------------

    /// <summary>Contraband component on anything held, or (on forks without it) a known contraband id.</summary>
    public static bool Contraband(object uid)
    {
        if (TypeOf("Contraband") is not null)
            return Everything(uid).Any(e => Has("Contraband", e));

        foreach (var item in Everything(uid))
            if (ProtoId(item) is { } id && ContrabandItems.Contains(id))
                return true;
        return false;
    }

    public static bool Weapon(object uid)
    {
        foreach (var item in Everything(uid))
            if (ProtoId(item) is { } id && WeaponItems.Contains(id))
                return true;
        return false;
    }

    public static bool Implants(object uid) => Inside(uid, "implant").Any();

    /// <summary>True when the label should say they can't be slipped: NoSlip on them or on their shoes.</summary>
    public static bool NoSlip(object uid)
    {
        if (Has("NoSlip", uid)) return true;
        foreach (var shoe in Inside(uid, "shoes"))
            return Has("NoSlip", shoe);   // the first shoe decides, as it did in Cerberus
        return false;                     // barefoot: they can slip
    }

    // --- Combat mode ------------------------------------------------------------------------------

    public static bool Combat(object uid)
    {
        var comp = Comp("CombatMode", uid);
        if (comp is null || !Engine.EspCombatOn.Ok) return false;
        try { return (bool)Engine.EspCombatOn.Get(comp)!; }
        catch { return false; }
    }

    // --- Cerberus' hardcoded lists (their fallback where the Contraband component isn't present) ----

    private static readonly HashSet<string> ContrabandItems = new()
    {
        "Emag", "ClothingHandsGlovesNorthStar", "Telecrystal1", "BibleNecronomicon", "HotPotato", "SupermatterGrenade", "WhiteholeGrenade", "EmpGrenade", "C4", "SyndieMiniBomb",
        "SyndiHypo", "EnergyDagger", "PenExploding", "Hypopen", "HolyHandGrenade", "TrashBananaPeelExplosive", "RubberStampSyndicate", "SoapSyndie", "SlipocalypseClusterSoap", "NocturineChemistryBottle",
        "Stimpack", "StimpackMini", "ExGrenade", "ChameleonProjector", "ClothingHandsGlovesBoxingRigged",
    };

    private static readonly HashSet<string> WeaponItems = new()
    {
        "WeaponRifleAk", "WeaponLaserSvalinn", "WeaponLaserGun", "WeaponMakeshiftLaser", "WeaponTeslaGun", "WeaponLaserCarbine", "WeaponLaserCarbinePractice", "WeaponPulsePistol", "WeaponPulseCarbine", "WeaponPulseRifle",
        "WeaponLaserCannon", "WeaponParticleDecelerator", "WeaponXrayCannon", "WeaponDisabler", "WeaponDisablerSMG", "WeaponDisablerPractice", "WeaponTaser", "WeaponAntiqueLaser", "WeaponAdvancedLaser", "WeaponPistolCHIMP",
        "WeaponPistolCHIMPUpgraded", "WeaponBehonkerLaser", "BaseWeaponHeavyMachineGun", "WeaponMinigun", "BaseWeaponLauncher", "WeaponLauncherChinaLake", "WeaponLauncherRocket", "WeaponLauncherMultipleRocket", "WeaponLauncherPirateCannon", "WeaponTetherGun",
        "WeaponForceGun", "WeaponGrapplingGun", "WeaponTetherGunAdmin", "WeaponForceGunAdmin", "WeaponLauncherAdmemeMeteorLarge", "WeaponLauncherAdmemeImmovableRodSlow", "BaseWeaponLightMachineGun", "WeaponLightMachineGunL6", "WeaponLightMachineGunL6C", "BaseWeaponPistol",
        "WeaponPistolViper", "WeaponPistolCobra", "WeaponPistolMk58", "WeaponPistolN1984", "WeaponRifleM90GrenadeLauncher", "WeaponRifleLecter", "WeaponShotgunBulldog", "WeaponShotgunDoubleBarreled", "WeaponShotgunDoubleBarreledRubber", "WeaponShotgunEnforcer",
        "WeaponShotgunEnforcerRubber", "WeaponShotgunKammerer", "WeaponShotgunSawn", "WeaponShotgunSawnEmpty", "WeaponShotgunHandmade", "WeaponShotgunBlunderbuss", "WeaponShotgunImprovised", "WeaponShotgunImprovisedLoaded", "BaseWeaponRevolver", "WeaponRevolverDeckard",
        "WeaponRevolverInspector", "WeaponRevolverMateba", "WeaponRevolverPython", "WeaponRevolverPythonAP", "WeaponRevolverPirate", "WeaponSubMachineGunAtreides", "WeaponSubMachineGunC20r", "WeaponSubMachineGunDrozd", "WeaponSubMachineGunVector", "WeaponSubMachineGunWt550",
        "WeaponSniperMosin", "WeaponSniperHristov", "Musket", "WeaponPistolFlintlock", "EnergySword", "EnergySwordDouble",
    };
}
