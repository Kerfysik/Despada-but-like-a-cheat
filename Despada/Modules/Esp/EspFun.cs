// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Despada.ImGui;
using ImGuiNET;

namespace Despada.Modules.Esp;

/// <summary>
/// Cerberus' "Fun": a fading trail behind each player, drawn from the same screen anchors the
/// labels use (so it needs no world math of its own). Points are kept per ckey, trimmed to a
/// configurable length, and cleared when a body jumps — a teleport or a map switch would otherwise
/// draw one long line across the screen.
/// </summary>
internal sealed class EspFun
{
    private sealed class Trail
    {
        public readonly List<Vector2> Points = new();
        public long Seen;
    }

    private readonly Dictionary<string, Trail> _trails = new(StringComparer.Ordinal);

    public bool    Enabled;
    public bool    Rainbow = true;
    public int     Length  = 48;
    public float   Width   = 1.6f;
    public Vector4 Color   = new(0.54f, 0.68f, 0.96f, 1f);

    public void Clear() => _trails.Clear();

    /// <summary>Adds this frame's anchor for the body. Same spot as last frame is not a new point.</summary>
    public void Push(string key, Vector2 at, long now)
    {
        if (!Enabled) return;

        if (!_trails.TryGetValue(key, out var trail))
            _trails[key] = trail = new Trail();

        trail.Seen = now;

        if (trail.Points.Count > 0)
        {
            var last = trail.Points[^1];
            var dx   = at.X - last.X;
            var dy   = at.Y - last.Y;
            var d2   = dx * dx + dy * dy;

            if (d2 < 4f) return;                          // standing still: no duplicate points
            if (d2 > 200f * 200f) trail.Points.Clear();    // teleport, or the view was cut
        }

        trail.Points.Add(at);

        var max = Math.Clamp(Length, 4, 256);
        while (trail.Points.Count > max) trail.Points.RemoveAt(0);
    }

    /// <summary>The trail itself: older segments are thinner and dimmer, the newest is full width.</summary>
    public void Draw(ImDrawListPtr dl, string key, long now)
    {
        if (!Enabled) return;
        if (!_trails.TryGetValue(key, out var trail) || trail.Points.Count < 2) return;

        var pts   = trail.Points;
        var count = pts.Count;
        for (var i = 1; i < count; i++)
        {
            var t = i / (float)(count - 1);
            var c = Rainbow
                ? Hsv((now * 0.0002f + i * 0.012f) % 1f, 0.75f, 1f)
                : Color;
            c.W = 0.06f + 0.74f * t;

            dl.AddLine(pts[i - 1], pts[i], Theme.ToU32(c), Width * (0.35f + 0.65f * t));
        }
    }

    /// <summary>Drops trails of bodies that have not been drawn for a couple of seconds.</summary>
    public void Prune(long now)
    {
        if (_trails.Count == 0) return;

        List<string>? dead = null;
        foreach (var (key, trail) in _trails)
            if (now - trail.Seen > 2000)
                (dead ??= new()).Add(key);

        if (dead is null) return;
        foreach (var key in dead) _trails.Remove(key);
    }

    private static Vector4 Hsv(float h, float s, float v)
    {
        var i = (int)(h * 6f) % 6;
        var f = h * 6f - MathF.Floor(h * 6f);
        var p = v * (1f - s);
        var q = v * (1f - f * s);
        var t = v * (1f - (1f - f) * s);

        return i switch
        {
            0 => new Vector4(v, t, p, 1f),
            1 => new Vector4(q, v, p, 1f),
            2 => new Vector4(p, v, t, 1f),
            3 => new Vector4(p, q, v, 1f),
            4 => new Vector4(t, p, v, 1f),
            _ => new Vector4(v, p, q, 1f),
        };
    }
}