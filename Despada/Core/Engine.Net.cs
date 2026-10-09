// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Despada.Core.Bind;

namespace Despada.Core;

internal static partial class Engine
{
    // Sending: CoreSendMessage builds the Lidgren packet (BuildMessage) and sends it; every client
    // message goes through it. Receiving: DispatchNetMessage deserialises and calls the handler.
    public static readonly TypeRef   NetManager       = Type("Robust.Shared.Network.NetManager");
    public static readonly MethodRef NetCoreSend      = Method(NetManager, "CoreSendMessage", "NetChannel", "NetMessage");
    public static readonly MethodRef NetDispatch      = Method(NetManager, "DispatchNetMessage", "NetIncomingMessage");
    public static readonly FieldRef  NetTiming        = Field(NetManager, "_timing");

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

    public static readonly TypeRef   MsgEntity        = Type("Robust.Shared.Network.Messages.MsgEntity");
    public static readonly MethodRef MsgEntitySystem  = Method(MsgEntity, "get_SystemMessage");
    public static readonly TypeRef   MsgState         = Type("Robust.Shared.Network.Messages.MsgState");
    public static readonly FieldRef  MsgStateState    = Field(MsgState, "State");
    public static readonly TypeRef   GameStateType    = Type("Robust.Shared.GameStates.GameState");
    public static readonly FieldRef  GameStateTo      = Field(GameStateType, "ToSequence");
    public static readonly FieldRef  GameStateEnts    = Field(GameStateType, "EntityStates");
}
