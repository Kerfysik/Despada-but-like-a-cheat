// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Despada.Core.Bind;

namespace Despada.Core;

/// <summary>
/// What the player ESP reads from the client: who is connected, which entity each player sits in,
/// where that body is on the map, what it carries, and how a world point lands on the screen.
/// Content is reached by component *name* through <see cref="EspHasReg"/> (the factory turns
/// "Heretic" into its Type), so no Content type is named here except the one property the combat
/// label reads directly.
/// </summary>
internal static partial class Engine
{
    // --- Who is on the server ---------------------------------------------------------------
    public static readonly TypeRef    EspPlayers      = Type("Robust.Shared.Player.ISharedPlayerManager");
    public static readonly ManagerRef EspPlayerMgr    = Manager(EspPlayers);
    public static readonly MethodRef  EspSessions     = Method(EspPlayers, "get_Sessions");
    public static readonly MethodRef  EspLocalSession = Method(EspPlayers, "get_LocalSession");
    public static readonly MethodRef  EspLocalEntity  = Method(EspPlayers, "get_LocalEntity");

    public static readonly TypeRef   EspSession     = Type("Robust.Shared.Player.ICommonSession");
    public static readonly MethodRef EspSessionName = Method(EspSession, "get_Name");
    public static readonly MethodRef EspAttached    = Method(EspSession, "get_AttachedEntity");
    public static readonly MethodRef EspStatus      = Method(EspSession, "get_Status");

    // --- World to screen (the projection a label is anchored by) -----------------------------
    public static readonly TypeRef    EspEye       = Type("Robust.Client.Graphics.IEyeManager");
    public static readonly ManagerRef EspEyeMgr    = Manager(EspEye);
    public static readonly MethodRef  EspToScreen  = Method(EspEye, "WorldToScreen", "Vector2");
    public static readonly MethodRef  EspCurMap    = Method(EspEye, "get_CurrentMap");
    public static readonly MethodRef  EspCurEye    = Method(EspEye, "get_CurrentEye");
    public static readonly MethodRef  EspViewport  = Method(EspEye, "GetWorldViewport");
    public static readonly TypeRef    EspEyeState  = Type("Robust.Shared.Graphics.IEye");
    public static readonly MethodRef  EspRotation  = Method(EspEyeState, "get_Rotation");
    public static readonly TypeRef    EspAngle     = Type("Robust.Shared.Maths.Angle");
    public static readonly FieldRef   EspAngleTh   = Field(EspAngle, "Theta");

    // A world box (Robust.Shared.Maths.Box2): its four edges, read off the boxed value.
    public static readonly TypeRef  EspBox      = Type("Robust.Shared.Maths.Box2");
    public static readonly FieldRef EspBoxLeft  = Field(EspBox, "Left");
    public static readonly FieldRef EspBoxBot   = Field(EspBox, "Bottom");
    public static readonly FieldRef EspBoxRight = Field(EspBox, "Right");
    public static readonly FieldRef EspBoxTop   = Field(EspBox, "Top");

    // --- Entities ------------------------------------------------------------------------------
    public static readonly TypeRef    EspEnt      = Type("Robust.Shared.GameObjects.IEntityManager");
    public static readonly ManagerRef EspEntities = Manager(EspEnt);
    public static readonly MethodRef  EspExists   = Method(EspEnt, "EntityExists", "EntityUid");
    public static readonly MethodRef  EspHasComp  = Method(EspEnt, "HasComponent", "EntityUid", "Type");
    public static readonly MethodRef  EspGetComp  = Method(EspEnt, "GetComponent", "EntityUid", "Type");

    // A component's name ("Heretic", "NoSlip") → its Type, once, through the factory.
    public static readonly TypeRef    EspFactory = Type("Robust.Shared.GameObjects.IComponentFactory");
    public static readonly ManagerRef EspCompMgr   = Manager(EspFactory);
    public static readonly MethodRef  EspHasReg    = Method(EspFactory, "HasRegistration", "String");
    public static readonly MethodRef  EspGetReg    = Method(EspFactory, "GetRegistration", "String", "Boolean");
    public static readonly TypeRef    EspReg       = Type("Robust.Shared.GameObjects.ComponentRegistration");
    public static readonly MethodRef  EspRegType   = Method(EspReg, "get_Type");

    // Entity systems: the AABB lookup lives in one, and systems aren't in the IoC container.
    public static readonly TypeRef    EspSystems   = Type("Robust.Shared.GameObjects.IEntitySystemManager");
    public static readonly ManagerRef EspSysMgr    = Manager(EspSystems);
    public static readonly MethodRef  EspGetSys    = Method(EspSystems, "GetEntitySystem", "Type");
    public static readonly TypeRef    EspLookup    = Type("Robust.Shared.GameObjects.EntityLookupSystem");
    public static readonly MethodRef  EspWorldAabb = Method(EspLookup, "GetWorldAABB", "EntityUid", "TransformComponent");

    // --- What a label reads off an entity -------------------------------------------------------
    public static readonly TypeRef   EspXform    = Type("Robust.Shared.GameObjects.TransformComponent");
    public static readonly MethodRef EspMapId    = Method(EspXform, "get_MapID");
    public static readonly TypeRef   EspMeta     = Type("Robust.Shared.GameObjects.MetaDataComponent");
    public static readonly MethodRef EspEntName  = Method(EspMeta, "get_EntityName");
    public static readonly MethodRef EspEntProto = Method(EspMeta, "get_EntityPrototype");
    public static readonly TypeRef   EspProto    = Type("Robust.Shared.Prototypes.EntityPrototype");
    public static readonly MethodRef EspProtoId  = Method(EspProto, "get_ID");

    // --- What a body carries: containers, and what is inside one ---------------------------------
    public static readonly TypeRef   EspCmgr      = Type("Robust.Shared.Containers.ContainerManagerComponent");
    public static readonly FieldRef  EspCmgrBoxes = Field(EspCmgr, "Containers", "_containers");
    public static readonly TypeRef   EspContainer = Type("Robust.Shared.Containers.BaseContainer");
    public static readonly MethodRef EspContained = Method(EspContainer, "get_ContainedEntities");

    // --- Content: the combat-mode field (was a property before SS14 switched to [DataField]) --------
    public static readonly TypeRef  EspCombat   = Type("Content.Shared.CombatMode.CombatModeComponent");
    public static readonly FieldRef EspCombatOn = Field(EspCombat, "IsInCombatMode");
}
