// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;

namespace Despada.Modules.Esp;

/// <summary>
/// Cerberus' eye settings, applied to the local player's own components: no FOV (the whole room
/// stays drawn), FullBright (no darkness) and zoom.
///
/// Nothing is sent to the server — <c>NetSyncEnabled</c> is switched off on every component touched,
/// exactly as Cerberus did — so this is a local view change only.
/// </summary>
internal static class EspEye
{
    /// <summary>Applies the settings; members are cached, so running it every frame is cheap.</summary>
    public static void Apply(bool noFov, bool fullBright, float zoom)
    {
        var uid = EspDetect.LocalEntity();
        if (uid is null) return;

        if (EspDetect.Comp("ContentEye", uid) is { } contentEye)
        {
            EspDetect.Set(contentEye, "NetSyncEnabled", false);
            EspDetect.Set(contentEye, "TargetZoom", new Vector2(zoom));
        }

        if (EspDetect.Comp("Eye", uid) is { } eye)
        {
            EspDetect.Set(eye, "NetSyncEnabled", false);
            if (EspDetect.Get(eye, "Eye") is { } state)
            {
                EspDetect.Set(state, "DrawFov", !noFov);
                EspDetect.Set(state, "DrawLight", !fullBright);
            }
        }

        if (EspDetect.Comp("Examiner", uid) is { } examiner)
        {
            EspDetect.Set(examiner, "NetSyncEnabled", false);
            EspDetect.Set(examiner, "CheckInRangeUnOccluded", !noFov);
            EspDetect.Set(examiner, "SkipChecks", noFov);
        }
    }
}