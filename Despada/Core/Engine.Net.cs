// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Despada.Core.Bind;

namespace Despada.Core;

internal static partial class Engine
{
    public static readonly TypeRef   NetManager       = Type("Robust.Shared.Network.NetManager");
    public static readonly MethodRef NetCoreSend      = Method(NetManager, "CoreSendMessage", "NetChannel", "NetMessage");
    public static readonly MethodRef NetDispatch      = Method(NetManager, "DispatchNetMessage", "NetIncomingMessage");
    public static readonly FieldRef  NetTiming        = Field(NetManager, "_timing");
    public static readonly TypeRef    IClientNet      = Type("Robust.Shared.Network.IClientNetManager");
    public static readonly ManagerRef ClientNet       = Manager(IClientNet);
    public static readonly MethodRef  NetClientSend   = Method(NetManager, "ClientSendMessage", "NetMessage");
    public static readonly MethodRef  NetDispatchLocal = Method(NetManager, "DispatchLocalNetMessage", "NetMessage");
    public static readonly MethodRef  NetIsConnected  = Method(NetManager, "get_IsConnected");
    // Message types this side has a receive handler for (so: ones that come in).
    public static readonly MethodRef  NetCallbackAudit = Method(NetManager, "get_CallbackAudit");

    public static readonly TypeRef   NetMessage       = Type("Robust.Shared.Network.NetMessage");
    public static readonly MethodRef MsgDelivery      = Method(NetMessage, "get_DeliveryMethod");
    public static readonly MethodRef MsgSeqChannel    = Method(NetMessage, "get_SequenceChannel");

    public static readonly TypeRef   LgBuffer         = Type("Lidgren.Network.NetBuffer");
    public static readonly MethodRef LgLengthBytes    = Method(LgBuffer, "get_LengthBytes");
    public static readonly MethodRef LgData           = Method(LgBuffer, "get_Data");
    public static readonly TypeRef   LgIncoming       = Type("Lidgren.Network.NetIncomingMessage");
    public static readonly MethodRef LgInDelivery     = Method(LgIncoming, "get_DeliveryMethod");
    public static readonly MethodRef LgInSeqChannel   = Method(LgIncoming, "get_SequenceChannel");

    public static readonly TypeRef   GameTiming       = Type("Robust.Shared.Timing.IGameTiming");
    public static readonly MethodRef TimingCurTick    = Method(GameTiming, "get_CurTick");
    public static readonly TypeRef   GameTick         = Type("Robust.Shared.Timing.GameTick");
    public static readonly FieldRef  GameTickValue    = Field(GameTick, "Value");

    // Fresh events to the server go through the entity manager, which wraps them in a MsgEntity.
    public static readonly TypeRef    IEntityNet          = Type("Robust.Shared.GameObjects.IEntityNetworkManager");
    public static readonly ManagerRef EntityNet           = Manager(IEntityNet);
    public static readonly MethodRef  SendSystemNetMessage = Method(IEntityNet, "SendSystemNetworkMessage", "EntityEventArgs", "Boolean");

    // The event bus: which events this client subscribed to from the network (so: ones that come in).
    public static readonly TypeRef    IEntityManager  = Type("Robust.Shared.GameObjects.IEntityManager");
    public static readonly ManagerRef Entities        = Manager(IEntityManager);
    public static readonly MethodRef  EntityEventBus  = Method(IEntityManager, "get_EventBus");

    public static readonly TypeRef   MsgEntity        = Type("Robust.Shared.Network.Messages.MsgEntity");
    public static readonly MethodRef MsgEntitySystem  = Method(MsgEntity, "get_SystemMessage");
    public static readonly TypeRef   MsgState         = Type("Robust.Shared.Network.Messages.MsgState");
    public static readonly FieldRef  MsgStateState    = Field(MsgState, "State");
    public static readonly TypeRef   GameStateType    = Type("Robust.Shared.GameStates.GameState");
    public static readonly FieldRef  GameStateTo      = Field(GameStateType, "ToSequence");
    public static readonly FieldRef  GameStateEnts    = Field(GameStateType, "EntityStates");
}
