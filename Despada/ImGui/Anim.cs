// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;

namespace Despada.ImGui;

public static class Anim
{
    public static float Lerp(float current, float target, float speed, float dt)
    {
        if (MathF.Abs(current - target) < 0.001f)
            return target;
        return current + (target - current) * Clamp01(1f - MathF.Exp(-speed * dt));
    }

    public static Vector4 Lerp(Vector4 current, Vector4 target, float speed, float dt)
    {
        float t = Clamp01(1f - MathF.Exp(-speed * dt));
        return current + (target - current) * t;
    }

    /// <summary>
    /// One step of a damped spring towards <paramref name="target"/>: <paramref name="x"/> and the
    /// velocity <paramref name="v"/> are advanced in place. <paramref name="zeta"/> below 1 overshoots
    /// a little (0.7–0.8 reads as a snappy thumb), 1 is critically damped. Stable for the frame
    /// times a game produces: the step is capped and sub-stepped.
    /// </summary>
    public static void Spring(ref float x, ref float v, float target, float dt, float stiffness = 700f, float zeta = 0.75f)
    {
        var damping = 2f * zeta * MathF.Sqrt(stiffness);
        var d = MathF.Min(MathF.Max(dt, 0f), 0.1f);   // a stall is not simulated: the spring just settles
        var steps = Math.Max(1, (int)MathF.Ceiling(d * 240f));
        var h = d / steps;
        for (var i = 0; i < steps; i++)
        {
            v += (stiffness * (target - x) - damping * v) * h;
            x += v * h;
        }
        if (MathF.Abs(x - target) < 0.0005f && MathF.Abs(v) < 0.01f) { x = target; v = 0f; }
    }

    private static float Clamp01(float v)
        => v < 0f ? 0f : v > 1f ? 1f : v;
}