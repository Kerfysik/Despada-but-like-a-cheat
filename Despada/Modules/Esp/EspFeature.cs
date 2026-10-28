// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Collections;
using System.Linq;
using System.Numerics;
using Despada.Core;
using Despada.Core.Bind;
using Despada.ImGui;
using Despada.ImGui.UserInterface;
using ImGuiNET;

namespace Despada.Modules.Esp;

/// <summary>
/// Player ESP: a stack of labels over every other player's head — name, ckey, the antag marks
/// Cerberus looked for, friend/priority, combat mode and what they are carrying — anchored at the
/// top-right of their world box and drawn on the overlay frame, outside the menu, so the
/// anti-screengrab hides the whole thing in one move.
/// </summary>
internal sealed class EspFeature : Feature
{
    private static readonly Vector4 White = new(1f, 1f, 1f, 1f);

    public override string Id => "esp";

    public override string Title => "Player ESP";

    public override string Description => "names, antag marks and gear over every player";

    public override string? Tab => "ESP";

    public override bool HasHud => true;

    public override IReadOnlyList<Binding> Requires =>
    [
        Engine.EspPlayers, Engine.EspPlayerMgr, Engine.EspSessions, Engine.EspLocalSession,
        Engine.EspSession, Engine.EspSessionName, Engine.EspAttached, Engine.EspStatus,
        Engine.EspEye, Engine.EspEyeMgr, Engine.EspToScreen, Engine.EspCurMap, Engine.EspCurEye,
        Engine.EspEyeState, Engine.EspRotation, Engine.EspAngle, Engine.EspAngleTh,
        Engine.EspBox, Engine.EspBoxLeft, Engine.EspBoxBot, Engine.EspBoxRight, Engine.EspBoxTop,
        Engine.EspEnt, Engine.EspEntities, Engine.EspExists, Engine.EspHasComp, Engine.EspGetComp,
        Engine.EspFactory, Engine.EspCompMgr, Engine.EspHasReg, Engine.EspGetReg, Engine.EspReg, Engine.EspRegType,
        Engine.EspSystems, Engine.EspSysMgr, Engine.EspGetSys, Engine.EspLookup, Engine.EspWorldAabb,
        Engine.EspXform, Engine.EspMapId, Engine.EspMeta, Engine.EspEntName, Engine.EspEntProto,
        Engine.EspProto, Engine.EspProtoId, Engine.EspCmgr, Engine.EspCmgrBoxes, Engine.EspContained,
    ];

    /// <summary>Content-only: without it everything else still draws, the combat line doesn't.</summary>
    public override IReadOnlyList<Binding> Optional => [Engine.EspCombatOn, Engine.EspLocalEntity];

    // --- Settings, read at enable (DrawHud runs every frame and must not touch the config) --------

    private bool   _name       = true;
    private bool   _ckey       = true;
    private bool   _antag      = true;
    private bool   _friend     = true;
    private bool   _priority   = true;
    private bool   _combat     = true;
    private bool   _contraband = true;
    private bool   _implants   = true;
    private bool   _weapon     = true;
    private bool   _noslip     = true;
    private bool   _role       = true;
    private bool   _health     = true;
    private bool   _stamina    = true;
    private bool   _distance   = true;
    private bool   _hpText     = true;
    private bool   _arrows     = true;

    // Cerberus' "General": a local-only view change (see EspEye).
    private bool   _noFov;
    private bool   _fullBright;
    private float  _zoom       = 1f;

    // Cerberus' icon toggles: the game's own HUD components on the local body (see EspIcons).
    private bool   _antagIcons    = true;
    private bool   _jobIcons      = true;
    private bool   _mindIcons     = true;
    private bool   _criminalIcons = true;
    private bool   _syndIcons     = true;
    private bool   _chemIcons     = true;
    private bool   _electroIcons  = true;

    // Cerberus' palette, kept: teal names, yellow ckeys, dark-red antags, green friend/priority.
    private Vector4 _colName    = new(0.49803922f, 1f, 0.83137256f, 1f);
    private Vector4 _colCkey    = new(1f, 1f, 0f, 1f);
    private Vector4 _colAntag   = new(0.54509807f, 0f, 0f, 1f);
    private Vector4 _colFriend  = new(0f, 0.5019608f, 0f, 1f);
    private Vector4 _colCombat  = new(1f, 0f, 0f, 1f);
    private Vector4 _colItems   = new(1f, 23f / 85f, 0f, 1f);
    private Vector4 _colNoSlip  = new(0.6784314f, 72f / 85f, 46f / 51f, 1f);
    private Vector4 _colRole    = new(0.75f, 0.75f, 1f, 1f);
    private Vector4 _colHealth  = new(0.2f, 0.9f, 0.35f, 1f);
    private Vector4 _colStamina = new(1f, 31f / 51f, 2f / 3f, 1f);

    private int  _size     = 15;   // logical px; Cerberus drew at 10 device px with a 15 px step
    private int  _interval = 22;

    private string          _friendsBuf   = "";
    private string          _priorityBuf  = "";
    private HashSet<string> _friends      = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _prioritySet  = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The EntityLookupSystem, resolved once (systems aren't in the IoC container).</summary>
    private object? _lookup;

    // Cerberus' StorageViewer / ClickDamage inspector and Fun trails. The inspector follows the
    // cursor (nearest label anchor) or pins a body with F8; the panels read live every frame and
    // never keep an entity past its life.
    private readonly EspFun _fun = new();
    private bool     _viewer;
    private bool     _damage;
    private bool     _trails;
    private bool     _ammo = true;
    private int      _inspectorSize = 18;
    private object?  _lockUid;
    private object?  _hoverUid;
    private Vector2  _hoverAt;
    private Vector2? _lockAt;
    private bool     _lockVisible;
    private float    _hoverDist = float.MaxValue;

    // Render-time diagnostics: most absent labels are not exceptions. A player can be disconnected,
    // spectating, on another map, or outside this client's replicated world. Keep the exact split
    // visible in the tab instead of silently making all of those cases look like a broken ESP.
    private DrawStats _lastDraw;

    private struct DrawStats
    {
        public int Sessions, Attached, Existing, OnMap, OnScreen, OffScreen, Labelled, Faulted;
    }

    protected internal override void OnEnable()
    {
        _name       = Setting("name", true);
        _ckey       = Setting("ckey", true);
        _antag      = Setting("antag", true);
        _friend     = Setting("friend", true);
        _priority   = Setting("priority", true);
        _combat     = Setting("combat", true);
        _contraband = Setting("contraband", true);
        _implants   = Setting("implants", true);
        _weapon     = Setting("weapon", true);
        _noslip     = Setting("noslip", true);
        _role       = Setting("role", true);
        _health     = Setting("health", true);
        _stamina    = Setting("stamina", true);
        _distance   = Setting("distance", true);
        _hpText     = Setting("hptext", true);
        _arrows     = Setting("arrows", true);
        _noFov      = Setting("visuals.nofov", false);
        _fullBright = Setting("visuals.fullbright", false);
        _zoom       = Math.Clamp(Setting("visuals.zoom", 1f), 0.5f, 3f);

        _antagIcons    = Setting("icons.antag", true);
        _jobIcons      = Setting("icons.job", true);
        _mindIcons     = Setting("icons.mindshield", true);
        _criminalIcons = Setting("icons.criminal", true);
        _syndIcons     = Setting("icons.syndicate", true);
        _chemIcons     = Setting("icons.chemical", true);
        _electroIcons  = Setting("icons.electrocution", true);

        _colName   = Setting("col.name",   _colName);
        _colCkey   = Setting("col.ckey",   _colCkey);
        _colAntag  = Setting("col.antag",  _colAntag);
        _colFriend = Setting("col.friend", _colFriend);
        _colCombat = Setting("col.combat", _colCombat);
        _colItems  = Setting("col.items",  _colItems);
        _colNoSlip = Setting("col.noslip", _colNoSlip);
        _colRole   = Setting("col.role", _colRole);
        _colHealth = Setting("col.health", _colHealth);
        _colStamina = Setting("col.stamina", _colStamina);

        _size     = Setting("size", 15);
        _interval = Setting("interval", 22);

        // Cerberus' StorageViewer / ClickDamage / Fun.
        _viewer      = Setting("tools.viewer", false);
        _damage      = Setting("tools.damage", false);
        _trails      = Setting("fun.trails", false);
        _fun.Enabled = _trails;
        _fun.Rainbow = Setting("fun.rainbow", true);
        _fun.Length  = Math.Clamp(Setting("fun.length", 48), 8, 128);
        _fun.Width   = Math.Clamp(Setting("fun.width", 1.6f), 0.5f, 4f);
        _fun.Color   = Setting("fun.color", _fun.Color);
        _ammo        = Setting("tools.ammo", true);
        _inspectorSize = Math.Clamp(Setting("tools.size", 18), 12, 30);
        _lockUid     = null;
        _lockAt      = null;

        var (friends, _)   = Features.GetSettingItems<string>(Id, "friends");
        var (priority, _)  = Features.GetSettingItems<string>(Id, "priority");
        _friends     = new HashSet<string>(friends, StringComparer.OrdinalIgnoreCase);
        _prioritySet = new HashSet<string>(priority, StringComparer.OrdinalIgnoreCase);
        _friendsBuf  = string.Join(", ", friends);
        _priorityBuf = string.Join(", ", priority);

        _lookup = null;
        Log.Info(Id, "labels over every other player on the map");
    }

    // --- The drawing ------------------------------------------------------------------------------

    public override void DrawHud()
    {
        // Cerberus' eye settings: no FOV, FullBright, zoom — local view only, nothing sent to the server.
        EspEye.Apply(_noFov, _fullBright, _zoom);

        // Cerberus' icons: the game's own HUD components on the local body draw them over everyone.
        EspIcons.Sync(_antagIcons, _jobIcons, _mindIcons, _criminalIcons, _syndIcons, _chemIcons, _electroIcons);

        // Inspector hover: the nearest label anchor to the cursor wins this frame.
        _hoverUid    = null;
        _hoverDist   = float.MaxValue;
        _lockVisible = false;

        var stats = new DrawStats();
        var players = Engine.EspPlayerMgr.Instance;
        var eye     = Engine.EspEyeMgr.Instance;
        var em      = Engine.EspEntities.Instance;
        if (players is null || eye is null || em is null)
        {
            _lastDraw = stats;
            return;
        }

        IEnumerable sessions;
        try { sessions = Engine.EspSessions.Invoke(players) as IEnumerable ?? Array.Empty<object>(); }
        catch
        {
            _lastDraw = stats;
            return;
        }

        string? local = null;
        try
        {
            if (Engine.EspLocalSession.Invoke(players) is { } mine)
                local = Engine.EspSessionName.Invoke(mine) as string;
        }
        catch { }

        object curMap;
        try
        {
            // No eye yet (main menu): nothing to project against, so nothing to draw.
            curMap = Engine.EspCurMap.Invoke(eye)!;
        }
        catch
        {
            _lastDraw = stats;
            return;
        }

        var lookup = Lookup();
        if (lookup is null)
        {
            _lastDraw = stats;
            return;
        }

        var dl = ImGuiNET.ImGui.GetForegroundDrawList();
        var k  = UiScale.K > 0f ? UiScale.K : 1f;

        // Distances are measured between AABB centres; the local body's centre is the same value
        // per frame, so it is read once instead of once per player.
        var localWorld = _distance ? LocalWorld(lookup) : null;

        foreach (var session in sessions)
        {
            if (session is null) continue;
            stats.Sessions++;
            try { DrawOne(dl, session, em, eye, lookup, curMap, local, k, localWorld, ref stats); }
            catch { stats.Faulted++; } // entity state can disappear between the reads above
        }

        _lastDraw = stats;

        var now = Environment.TickCount64;
        _fun.Prune(now);
        if (_lockUid is not null && !Alive(_lockUid))
        {
            _lockUid = null;
            _lockAt  = null;
        }

        DrawInspector(dl, now);
        Hotkeys();   // after the loop: F8 pins the body the cursor is on this frame
    }

    private object? Lookup()
    {
        if (_lookup is not null) return _lookup;
        try
        {
            var mgr = Engine.EspSysMgr.Instance;
            if (mgr is null || !Engine.EspGetSys.Ok || Engine.EspLookup.Type is not { } type) return null;
            _lookup = Engine.EspGetSys.Invoke(mgr, type);
        }
        catch { _lookup = null; }
        return _lookup;
    }

    private void DrawOne(ImDrawListPtr dl, object session, object em, object eye, object lookup,
        object curMap, string? local, float k, Vector2? localWorld, ref DrawStats stats)
    {
        // AttachedEntity boxes as the EntityUid itself when it has a value, null otherwise.
        if (Engine.EspAttached.Invoke(session) is not { } uid) return;
        stats.Attached++;
        if (Engine.EspExists.Invoke(em, uid) is not true) return;
        stats.Existing++;

        var xform = Engine.EspGetComp.Invoke(em, uid, Engine.EspXform.Type!);
        if (xform is null || !Equals(Engine.EspMapId.Invoke(xform), curMap)) return;   // another map
        stats.OnMap++;

        if (Engine.EspWorldAabb.Invoke(lookup, uid, null) is not { } aabb) return;

        var ckey = Engine.EspSessionName.Invoke(session) as string;
        if (ckey is not null && ckey == local) return;   // ourselves

        if (Anchor(aabb, eye, k, out var world) is not { } at) return;

        // Screen space, as ImGui draws in logical units. A body outside the window becomes an edge
        // arrow (Cerberus' off-screen markers) instead of a silently dropped label; absurdly far
        // projections (behind the camera on some builds) are dropped instead of drawn.
        var display = ImGuiNET.ImGui.GetIO().DisplaySize;
        if (at.X < 0f || at.Y < 0f || at.X > display.X || at.Y > display.Y)
        {
            stats.OffScreen++;
            if (_arrows && float.IsFinite(at.X) && float.IsFinite(at.Y)
                && MathF.Abs(at.X) < 20000f && MathF.Abs(at.Y) < 20000f)
                DrawArrow(dl, at, display, ckey ?? "player");
            return;
        }
        stats.OnScreen++;

        // Fun trail and inspector hover, both on the label anchor: the trail follows the body as
        // it moves, and the cursor picks the nearest anchor on screen.
        if (_lockUid is not null && Equals(_lockUid, uid))
        {
            _lockVisible = true;
            _lockAt      = at;
        }

        var mouse = ImGuiNET.ImGui.GetIO().MousePos;
        var dx    = mouse.X - at.X;
        var dy    = mouse.Y - at.Y;
        var dist  = dx * dx + dy * dy;
        if (dist < 90f * 90f && dist < _hoverDist)
        {
            _hoverDist = dist;
            _hoverUid  = uid;
            _hoverAt   = at;
        }

        if (ckey is not null)
        {
            var now = Environment.TickCount64;
            _fun.Push(ckey, at, now);
            _fun.Draw(dl, ckey, now);
        }

        var meta = Engine.EspGetComp.Invoke(em, uid, Engine.EspMeta.Type!);
        var name = meta is null ? null : Engine.EspEntName.Invoke(meta) as string;

        var y    = at.Y;
        var step = (float)_interval;

        // The draw list goes in as an argument, never captured: a closure field of an ImGui.NET
        // value type would make the type unloadable before ImGui.NET resolves (LoadTests).
        void Line(ImDrawListPtr list, string text, Vector4 color)
        {
            var font = EspFont();
            var pos  = new Vector2(at.X, y);
            list.AddText(font, _size, pos + new Vector2(1f, 1f), 0x99000000u, text);   // shadow over bright floors
            list.AddText(font, _size, pos, Theme.ToU32(color), text);
            y += step;
        }

        // Cerberus' HUD, anchored just above the labels: stamina (pink) over health (green -> red).
        var barW = 72f;
        var barH = 3.5f;
        var barY = at.Y - 6f;

        if (_stamina && EspDetect.Stamina(uid, out var stamRatio))
        {
            barY -= barH + 2f;
            DrawBar(dl, at.X, barY, barW, barH, stamRatio, _colStamina);
        }
        if (_health && EspDetect.Health(uid, out var hp, out var crit))
        {
            barY -= barH + 2f;
            DrawBar(dl, at.X, barY, barW, barH, hp, crit ? _colCombat : Vector4.Lerp(_colCombat, _colHealth, hp));

            if (_hpText)
            {
                var hpFont  = EspFont();
                var hpSize  = MathF.Max(10f, _size - 3f);
                var hpLabel = $"{(int)MathF.Round(hp * 100f)}%";
                var hpPos   = new Vector2(at.X + barW + 5f, barY - 2f);
                dl.AddText(hpFont, hpSize, hpPos + new Vector2(1f, 1f), 0x99000000u, hpLabel);
                dl.AddText(hpFont, hpSize, hpPos, Theme.ToU32(crit ? _colCombat : _colHealth), hpLabel);
            }
        }

        // The configured colours, always: the old "in-game bodies are white" test used the wrong
        // SessionStatus value and made every live player ignore the colour pickers.
        if (_name && name is not null) Line(dl, name, _colName);
        if (_ckey && ckey is not null) Line(dl, ckey, _colCkey);
        if (_role && EspDetect.Job(uid) is { } job) Line(dl, job, _colRole);
        if (_distance && localWorld is { } from)
        {
            var meters = (world - from).Length();
            Line(dl, meters < 10f ? $"{meters:0.0} m" : $"{meters:0} m", _colRole);
        }

        if (_antag)
        {
            if (EspDetect.Agent(uid))      Line(dl, "Agent", _colAntag);
            if (EspDetect.Heretic(uid))    Line(dl, "Heretic", _colAntag);
            if (EspDetect.Vampire(uid))    Line(dl, "Vampire", _colAntag);
            if (EspDetect.FleshCult(uid))  Line(dl, "Flesh cult", _colAntag);
            if (EspDetect.Zombie(uid))     Line(dl, "Zombie", _colAntag);
            if (EspDetect.Changeling(uid)) Line(dl, "Changeling", _colAntag);
            if (EspDetect.CosmicCult(uid)) Line(dl, "Cosmic cult", _colAntag);
            if (EspDetect.Devil(uid))      Line(dl, "Devil", _colAntag);
            if (EspDetect.Blob(uid))       Line(dl, "Blob", _colAntag);
            if (EspDetect.Thief(uid))      Line(dl, "Thief", _colAntag);
        }

        if (_friend && ckey is not null && _friends.Contains(ckey))       Line(dl, "Friend", _colFriend);
        if (_priority && ckey is not null && _prioritySet.Contains(ckey)) Line(dl, "Priority", _colFriend);
        if (_combat && EspDetect.Combat(uid))         Line(dl, "Combat", _colCombat);
        if (_contraband && EspDetect.Contraband(uid)) Line(dl, "Contraband", _colItems);
        if (_implants && EspDetect.Implants(uid))     Line(dl, "Implants", _colItems);
        if (_weapon && EspDetect.Weapon(uid))         Line(dl, "Weapon", _colItems);
        if (_noslip && EspDetect.NoSlip(uid))         Line(dl, "No slip", _colNoSlip);
        if (y > at.Y) stats.Labelled++;
    }

    /// <summary>World-space centre of a Box2 (the point a distance is measured from).</summary>
    private static Vector2? Centre(object box)
    {
        try
        {
            var l = Convert.ToSingle(Engine.EspBoxLeft.Get(box)!);
            var b = Convert.ToSingle(Engine.EspBoxBot.Get(box)!);
            var r = Convert.ToSingle(Engine.EspBoxRight.Get(box)!);
            var t = Convert.ToSingle(Engine.EspBoxTop.Get(box)!);
            return new Vector2((l + r) * 0.5f, (b + t) * 0.5f);
        }
        catch { return null; }
    }

    /// <summary>The local body's world centre, for distances; null in the lobby or as a spectator.</summary>
    private static Vector2? LocalWorld(object lookup)
    {
        try
        {
            if (EspDetect.LocalEntity() is not { } local) return null;
            if (Engine.EspWorldAabb.Invoke(lookup, local, null) is not { } box) return null;
            return Centre(box);
        }
        catch { return null; }
    }

    /// <summary>Cerberus' edge marker: an arrow on the window rim pointing at an off-screen body.</summary>
    private void DrawArrow(ImDrawListPtr dl, Vector2 at, Vector2 display, string label)
    {
        var centre = display * 0.5f;
        var dir    = at - centre;
        var len    = dir.Length();
        if (!float.IsFinite(len) || len < 1f) return;
        dir /= len;

        var half = centre - new Vector2(34f, 34f);
        if (half.X <= 0f || half.Y <= 0f) return;

        var tx  = MathF.Abs(dir.X) > 1e-4f ? half.X / MathF.Abs(dir.X) : float.MaxValue;
        var ty  = MathF.Abs(dir.Y) > 1e-4f ? half.Y / MathF.Abs(dir.Y) : float.MaxValue;
        var pos = centre + dir * MathF.Min(tx, ty);

        var side = new Vector2(-dir.Y, dir.X);
        var tip  = pos + dir * 9f;
        var a    = pos - dir * 5f + side * 5f;
        var b    = pos - dir * 5f - side * 5f;

        dl.AddTriangleFilled(tip, a, b, Theme.ToU32(_colCkey));
        dl.AddTriangle(tip, a, b, Theme.ToU32(_colCombat), 1f);

        var font = EspFont();
        var size = MathF.Max(11f, _size - 3f);
        var text = font.CalcTextSizeA(size, float.MaxValue, 0f, label);
        var pos2 = pos - dir * 20f - text * 0.5f;
        dl.AddText(font, size, pos2 + new Vector2(1f, 1f), 0x99000000u, label);
        dl.AddText(font, size, pos2, Theme.ToU32(_colName), label);
    }

    /// <summary>Boxfont Round (the game's own face) when it loaded, the UI font otherwise.</summary>
    private static unsafe ImFontPtr EspFont()
    {
        var p = ImGuiRenderer.EspFont;
        return p != 0 ? new ImFontPtr((ImGuiNET.ImFont*)p) : ImGuiNET.ImGui.GetFont();
    }

    /// <summary>One Cerberus-style bar: a dark plate with the fill on top and a darker lower edge.</summary>
    private static void DrawBar(ImDrawListPtr dl, float x, float y, float w, float h, float ratio, Vector4 color)
    {
        ratio = Math.Clamp(ratio, 0f, 1f);
        dl.AddRectFilled(new Vector2(x - 1f, y - 1f), new Vector2(x + w + 1f, y + h + 1f), 0xC0000000u, 1.5f);
        if (ratio <= 0f) return;

        var right = x + w * ratio;
        dl.AddRectFilled(new Vector2(x, y), new Vector2(right, y + h), Theme.ToU32(color), 1f);
        dl.AddRectFilled(new Vector2(x, y + h - 1f), new Vector2(right, y + h), 0x80000000u, 0f);
    }

    /// <summary>The label anchor: the box's top-right corner, turned by the eye's rotation first (as
    /// Cerberus did) so it stays the visual top-right when the view is turned, in screen pixels
    /// divided to Despada's logical units.</summary>
    private static Vector2? Anchor(object aabb, object eye, float k, out Vector2 world)
    {
        world = default;
        float l, b, r, t;
        try
        {
            l = Convert.ToSingle(Engine.EspBoxLeft.Get(aabb)!);
            b = Convert.ToSingle(Engine.EspBoxBot.Get(aabb)!);
            r = Convert.ToSingle(Engine.EspBoxRight.Get(aabb)!);
            t = Convert.ToSingle(Engine.EspBoxTop.Get(aabb)!);
        }
        catch { return null; }

        var center = new Vector2((l + r) * 0.5f, (b + t) * 0.5f);
        world = center;
        var offset = new Vector2(r - center.X, t - center.Y);

        try
        {
            if (Engine.EspCurEye.Invoke(eye) is { } state
                && Engine.EspRotation.Invoke(state) is { } angle
                && Engine.EspAngleTh.Get(angle) is { } thetaValue)
            {
                var theta = Convert.ToDouble(thetaValue);
                if (theta != 0.0)
                {
                    var c = (float)Math.Cos(-theta);
                    var s = (float)Math.Sin(-theta);
                    offset = new Vector2(offset.X * c - offset.Y * s, offset.X * s + offset.Y * c);
                }
            }
        }
        catch { /* no eye yet: the plain corner is still right */ }

        try
        {
            if (Engine.EspToScreen.Invoke(eye, center + offset) is not Vector2 screen) return null;
            return new Vector2(screen.X / k + 2f, screen.Y / k + 5f);
        }
        catch
        {
            return null;
        }
    }

    // --- Inspector (Cerberus' StorageViewer + ClickDamage) and hotkeys -----------------------------

    /// <summary>Global keys, polled once a frame: F6 FOV, F7 FullBright, F8 pin, +/- zoom.</summary>
    private void Hotkeys()
    {
        if (EspHotkeys.Pressed(EspHotkeys.VkFov))
        {
            _noFov = !_noFov;
            SetSetting("visuals.nofov", _noFov);
        }

        if (EspHotkeys.Pressed(EspHotkeys.VkFullBright))
        {
            _fullBright = !_fullBright;
            SetSetting("visuals.fullbright", _fullBright);
        }

        if (EspHotkeys.Pressed(EspHotkeys.VkZoomUp))   ZoomBy(0.1f);
        if (EspHotkeys.Pressed(EspHotkeys.VkZoomDown)) ZoomBy(-0.1f);

        if (!EspHotkeys.Pressed(EspHotkeys.VkLock)) return;

        if (_lockUid is not null)
        {
            _lockUid = null;
            _lockAt  = null;
        }
        else if (_hoverUid is not null)
        {
            _lockUid = _hoverUid;
            _lockAt  = _hoverAt;
        }
    }

    private void ZoomBy(float delta)
    {
        _zoom = Math.Clamp(MathF.Round(_zoom + delta, 2), 0.5f, 3f);
        SetSetting("visuals.zoom", _zoom);
    }

    /// <summary>Still a live, replicated entity — the pin must not outlive the body.</summary>
    private static bool Alive(object? uid)
    {
        if (uid is null) return false;
        var em = Engine.EspEntities.Instance;
        if (em is null) return false;
        try { return Engine.EspExists.Invoke(em, uid) is true; }
        catch { return false; }
    }

    /// <summary>
    /// Cerberus' two panels over one body: what it carries (pockets, back, belt, suit storage, ID,
    /// shoes, implants, one level into bags) and how much damage of which kind is on it. Drawn in the
    /// HUD font next to the label anchor, at the cursor or at the F8 pin.
    /// </summary>
    private void DrawInspector(ImDrawListPtr dl, long now)
    {
        if (!_viewer && !_damage) return;

        object? target;
        Vector2 at;

        if (_lockUid is not null && _lockVisible && _lockAt is { } pinned)
        {
            target = _lockUid;
            at     = pinned;
        }
        else if (_hoverUid is not null)
        {
            target = _hoverUid;
            at     = _hoverAt;
        }
        else
        {
            return;
        }

        if (!Alive(target)) return;

        var lines = new List<(string Text, Vector4 Color)>
        {
            (InspectorTitle(target), _colName),
        };

        if (_viewer)
        {
            var storage = EspInspector.Storage(target);
            if (storage.Count == 0)
            {
                lines.Add(("nothing carried", Theme.P.Muted));
            }
            else
            {
                foreach (var (label, items) in storage)
                {
                    lines.Add((label, _colRole));
                    foreach (var (item, count) in items)
                        lines.Add((count > 1 ? $"   {item} ×{count}" : $"   {item}", White));
                }
            }
        }

        if (_ammo)
        {
            var ammo = EspInspector.Ammo(target);
            if (ammo.Count > 0)
            {
                lines.Add(("ammo", _colRole));
                foreach (var (gun, count, capacity) in ammo)
                    lines.Add(($"   {gun} {count}/{capacity}", _colItems));
            }
        }

        if (_damage)
        {
            var types = EspInspector.Damage(target, out var total);
            lines.Add(($"damage {total:0.#}", _colHealth));
            if (types.Count == 0)
            {
                lines.Add(("   none", Theme.P.Muted));
            }
            else
            {
                foreach (var (kind, amount) in types)
                    lines.Add(($"   {kind} {amount:0.#}", _colCombat));
            }
        }

        var font = EspFont();
        var size = MathF.Max(12f, _inspectorSize);
        var step = size + 6f;
        const float pad = 10f;

        var width = 0f;
        foreach (var (text, _) in lines)
            width = MathF.Max(width, font.CalcTextSizeA(size, float.MaxValue, 0f, text).X);

        var display = ImGuiNET.ImGui.GetIO().DisplaySize;
        var panelW  = MathF.Min(width + pad * 2f, MathF.Max(160f, display.X - 16f));
        var panelH  = lines.Count * step + pad * 2f;

        var x = at.X + 14f;
        if (x + panelW > display.X - 4f) x = at.X - panelW - 14f;
        x = Math.Clamp(x, 4f, MathF.Max(4f, display.X - panelW - 4f));
        var y = Math.Clamp(at.Y, 4f, MathF.Max(4f, display.Y - panelH - 4f));

        dl.AddRectFilled(new Vector2(x, y), new Vector2(x + panelW, y + panelH), 0xD00C0F16, 5f);
        dl.AddRect(new Vector2(x, y), new Vector2(x + panelW, y + panelH), Theme.ToU32(Theme.Hairline), 5f);

        var ty = y + pad;
        foreach (var (text, color) in lines)
        {
            if (ty + step > y + panelH + 0.5f) break;   // a huge bag can't cover the screen
            dl.AddText(font, size, new Vector2(x + pad, ty), Theme.ToU32(color), text);
            ty += step;
        }

        // The pin: a pulsing ring on the body the panel is following.
        if (_lockUid is not null)
        {
            var pulse = 9f + MathF.Sin(now * 0.006f) * 1.5f;
            dl.AddCircle(at, pulse, Theme.ToU32(_colCkey), 20, 1.5f);
        }
    }

    private static string InspectorTitle(object target)
    {
        var name = EspDetect.Name(target) ?? "?";
        var job  = EspDetect.Job(target);
        return job is null ? name : $"{name}  ·  {job}";
    }

    // --- The tab ------------------------------------------------------------------------------------

    public override void DrawTab()
    {
        // The main shell deliberately never scrolls. Keep these settings in their own viewport so
        // the lower sections remain reachable on smaller screens.
        ImGuiNET.ImGui.BeginChild("##esp-settings", new Vector2(-1f, -1f),
            ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollWithMouse);
        SmoothScroll.Update();

        Widgets.BeginSection("Live status");
        {
            var s = _lastDraw;
            Widgets.MutedText($"sessions {s.Sessions}  ·  attached {s.Attached}  ·  replicated {s.Existing}  ·  current map {s.OnMap}");
            Widgets.MutedText($"on screen {s.OnScreen}  ·  off screen {s.OffScreen}  ·  labels drawn {s.Labelled}" +
                              (s.Faulted == 0 ? "" : $"  ·  transient reads {s.Faulted}"));
            Widgets.MutedText(EspDetect.Report());
            Widgets.MutedText($"health: {EspDetect.HealthWhy}");
            Widgets.MutedText("Players outside the client’s replicated world cannot be positioned until their entity is received.");
        }
        Widgets.EndSection();

        Widgets.BeginSection("Labels");
        {
            Row("Name", ref _name, "name");
            Row("CKey", ref _ckey, "ckey");
            Row("Antag marks", ref _antag, "antag");
            Row("Friend", ref _friend, "friend");
            Row("Priority", ref _priority, "priority");
            Row("Combat mode", ref _combat, "combat");
            Row("Contraband", ref _contraband, "contraband");
            Row("Implants", ref _implants, "implants");
            Row("Weapon", ref _weapon, "weapon");
            Row("No slip", ref _noslip, "noslip");
            Row("Role", ref _role, "role");
            Row("Distance", ref _distance, "distance");
        }
        Widgets.EndSection();

        Widgets.BeginSection("HUD");
        {
            Row("Health bar", ref _health, "health");
            Row("Stamina bar", ref _stamina, "stamina");
            Row("HP text", ref _hpText, "hptext");
            Pick("Health", ref _colHealth, "col.health");
            Pick("Stamina", ref _colStamina, "col.stamina");
            Pick("Role", ref _colRole, "col.role");

            Row("Antag icons", ref _antagIcons, "icons.antag");
            Row("Job icons", ref _jobIcons, "icons.job");
            Row("Mind shield icons", ref _mindIcons, "icons.mindshield");
            Row("Criminal record icons", ref _criminalIcons, "icons.criminal");
            Row("Syndicate icons", ref _syndIcons, "icons.syndicate");
            Row("Chemical analysis", ref _chemIcons, "icons.chemical");
            Row("Show electrocution", ref _electroIcons, "icons.electrocution");

            Widgets.MutedText(EspIcons.Report());
            Widgets.MutedText("Text is drawn with the game's own Boxfont Round.");
        }
        Widgets.EndSection();

        Widgets.BeginSection("Visuals");
        {
            Row("No FOV", ref _noFov, "visuals.nofov");
            Row("FullBright", ref _fullBright, "visuals.fullbright");
            Row("Off-screen arrows", ref _arrows, "arrows");

            var zoom = _zoom;
            if (Widgets.SliderFloat("Zoom", ref zoom, 0.5f, 3f, 2))
            {
                _zoom = zoom;
                SetSetting("visuals.zoom", zoom);
            }

            if (Widgets.Button("visuals.reset", "Reset"))
            {
                _noFov      = false;
                _fullBright = false;
                _zoom       = 1f;
                SetSetting("visuals.nofov", false);
                SetSetting("visuals.fullbright", false);
                SetSetting("visuals.zoom", 1f);
            }
        }
        Widgets.EndSection();

        Widgets.BeginSection("Tools");
        {
            Row("Storage viewer", ref _viewer, "tools.viewer");
            Row("Click damage", ref _damage, "tools.damage");
            Row("Ammo in storage", ref _ammo, "tools.ammo");

            if (Widgets.CheckboxRow("Trails", ref _trails))
            {
                _fun.Enabled = _trails;
                SetSetting("fun.trails", _trails);
                if (!_trails) _fun.Clear();
            }

            var rainbow = _fun.Rainbow;
            if (Widgets.CheckboxRow("Rainbow trails", ref rainbow))
            {
                _fun.Rainbow = rainbow;
                SetSetting("fun.rainbow", rainbow);
            }

            var length = _fun.Length;
            if (Widgets.SliderInt("Trail length", ref length, 8, 128))
            {
                _fun.Length = length;
                SetSetting("fun.length", length);
            }

            var trailWidth = _fun.Width;
            if (Widgets.SliderFloat("Trail width", ref trailWidth, 0.5f, 4f, 2))
            {
                _fun.Width = trailWidth;
                SetSetting("fun.width", trailWidth);
            }

            var inspectorSize = _inspectorSize;
            if (Widgets.SliderInt("Panel text size", ref inspectorSize, 12, 30))
            {
                _inspectorSize = inspectorSize;
                SetSetting("tools.size", inspectorSize);
            }

            if (!_fun.Rainbow) Pick("Trail color", ref _fun.Color, "fun.color");

            Widgets.MutedText("Hover a player to inspect their storage and damage. F8 pins the panel.");
            Widgets.MutedText("Hotkeys: F6 No FOV  ·  F7 FullBright  ·  F8 lock  ·  + / - zoom");
        }
        Widgets.EndSection();

        Widgets.BeginSection("Colors");
        {
            Pick("Name", ref _colName, "col.name");
            Pick("CKey", ref _colCkey, "col.ckey");
            Pick("Antag", ref _colAntag, "col.antag");
            Pick("Friend / priority", ref _colFriend, "col.friend");
            Pick("Combat", ref _colCombat, "col.combat");
            Pick("Contraband / implants / weapon", ref _colItems, "col.items");
            Pick("No slip", ref _colNoSlip, "col.noslip");
        }
        Widgets.EndSection();

        Widgets.BeginSection("Text");
        {
            var size = _size;
            if (Widgets.SliderInt("Size", ref size, 8, 32))
            {
                _size = size;
                SetSetting("size", size);
            }

            var interval = _interval;
            if (Widgets.SliderInt("Line spacing", ref interval, 10, 48))
            {
                _interval = interval;
                SetSetting("interval", interval);
            }
        }
        Widgets.EndSection();

        Widgets.BeginSection("Marks");
        {
            Widgets.MutedText("Player names that always get the Friend / Priority mark, comma-separated.");

            ImGuiNET.ImGui.PushItemWidth(-1f);
            if (ImGuiNET.ImGui.InputText("Friends", ref _friendsBuf, 512))
                SaveNames("friends", ref _friends, _friendsBuf);
            if (ImGuiNET.ImGui.InputText("Priority", ref _priorityBuf, 512))
                SaveNames("priority", ref _prioritySet, _priorityBuf);
            ImGuiNET.ImGui.PopItemWidth();
        }
        Widgets.EndSection();

        ImGuiNET.ImGui.EndChild();
    }

    private void Row(string label, ref bool value, string key)
    {
        if (Widgets.CheckboxRow(label, ref value))
            SetSetting(key, value);
    }

    private void Pick(string label, ref Vector4 color, string key)
    {
        if (Widgets.ColorPickerRow(label, ref color))
            SetSetting(key, color);
    }

    private void SaveNames(string key, ref HashSet<string> set, string buffer)
    {
        set = new HashSet<string>(
            buffer.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
        SetSetting(key, set.ToList());
    }
}
