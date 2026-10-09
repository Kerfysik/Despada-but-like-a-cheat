// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;

namespace Despada.ImGui;

internal static class UiScale
{
    public const float DesignHeight = 1600f;
    
    public static float K = 1f;

    /// <summary>Logical coordinate moved to the nearest framebuffer pixel boundary.</summary>
    public static float Snap(float v) => MathF.Round(v * K) / K;

    /// <inheritdoc cref="Snap(float)"/>
    public static Vector2 Snap(Vector2 v) => new(Snap(v.X), Snap(v.Y));
}
