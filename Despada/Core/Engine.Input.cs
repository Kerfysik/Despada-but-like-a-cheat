// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Despada.Core.Bind;

namespace Despada.Core;

internal static partial class Engine
{
    public static readonly TypeRef    IUiManager     = Type("Robust.Client.UserInterface.IUserInterfaceManager");
    public static readonly ManagerRef Ui             = Manager(IUiManager);

    public static readonly TypeRef    IClipboard       = Type("Robust.Client.UserInterface.IClipboardManager");
    public static readonly ManagerRef Clipboard        = Manager(IClipboard);
    public static readonly MethodRef  ClipboardGetText = Method(IClipboard, "GetText");
    public static readonly MethodRef  ClipboardSetText = Method(IClipboard, "SetText", "String");
}
