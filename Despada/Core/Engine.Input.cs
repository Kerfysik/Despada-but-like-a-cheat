// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Despada.Core.Bind;

namespace Despada.Core;

internal static partial class Engine
{
    public static readonly TypeRef   InputManager   = Type("Robust.Client.Input.InputManager");
    public static readonly MethodRef InputKeyDown   = Method(InputManager, "KeyDown", "KeyEventArgs");
    public static readonly MethodRef InputKeyUp     = Method(InputManager, "KeyUp", "KeyEventArgs");
    public static readonly TypeRef   KeyboardKey    = Type("Robust.Client.Input.Keyboard+Key");

    public static readonly TypeRef    UiManager      = Type("Robust.Client.UserInterface.UserInterfaceManager");
    public static readonly MethodRef  UiMouseMove    = Method(UiManager, "MouseMove", "MouseMoveEventArgs");
    public static readonly MethodRef  UiMouseWheel   = Method(UiManager, "MouseWheel", "MouseWheelEventArgs");
    public static readonly TypeRef    IUiManager     = Type("Robust.Client.UserInterface.IUserInterfaceManager");
    public static readonly ManagerRef Ui             = Manager(IUiManager);
}
