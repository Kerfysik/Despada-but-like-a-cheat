// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Despada.Core.Bind;

namespace Despada.Core;

internal static partial class Engine
{
    public static readonly TypeRef   GameController   = Type("Robust.Client.GameController");
    public static readonly MethodRef GameUpdate       = Method(GameController, "Update", "FrameEventArgs");
    public static readonly MethodRef GameCleanup      = Method(GameController, "CleanupGameThread");
    public static readonly MethodRef GameTextEntered  = Method(GameController, "TextEntered", "TextEnteredEventArgs");
}
