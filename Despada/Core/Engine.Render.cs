// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Despada.Core.Bind;

namespace Despada.Core;

internal static partial class Engine
{
    public static readonly TypeRef   Clyde               = Type("Robust.Client.Graphics.Clyde.Clyde");
    public static readonly MethodRef ClydeRender         = Method(Clyde, "Render");
    public static readonly MethodRef ClydeTakeScreenshot = Method(Clyde, "TakeScreenshot", "ScreenshotType");

    // The one call every full-frame capture goes through (it only queues; the frame is copied on the
    // next render): Content's ScreenshotHook (F2) and IClyde.ScreenshotAsync both end here.
    public static readonly MethodRef ClydeScreenshot      = Method(Clyde, "Screenshot");
    public static readonly MethodRef ClydeSetCursor      = Method(Clyde, "SetCursor", "ICursor");
    public static readonly MethodRef ClydeStdCursor      = Method(Clyde, "GetStandardCursor", "StandardCursorShape");
    public static readonly TypeRef   StdCursorShape      = Type("Robust.Client.Graphics.StandardCursorShape");
    public static readonly TypeRef   IClyde              = Type("Robust.Client.Graphics.IClyde");
    public static readonly ManagerRef ClydeManager       = Manager(IClyde);

}
