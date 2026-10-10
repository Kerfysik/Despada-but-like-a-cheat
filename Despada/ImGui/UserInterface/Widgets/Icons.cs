// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace Despada.ImGui.UserInterface;

/// <summary>Line icons drawn on the draw list on a 20-unit box scaled to <c>size</c>; the stroke width is given as is.</summary>
public enum Icon
{
    None,
    Up,       // arrow up: send, release
    Down,     // arrow down: deliver (an incoming packet)
    Pencil,   // edit
    Check,
    Cross,
    Trash,    // drop
    Copy,
    Open,     // open in a tab: arrow out of a box
    Funnel,   // filters
    Plus,
    Gear,     // rules
    Dots,     // more (⋯)
    Play,
    Pause,
}

public static partial class Widgets
{
    private static void LineAt(ImDrawListPtr dl, Vector2 c, float u, uint col, float th, float x0, float y0, float x1, float y1)
        => dl.AddLine(new Vector2(c.X + x0 * u, c.Y + y0 * u), new Vector2(c.X + x1 * u, c.Y + y1 * u), col, th);

    private static void PolyAt(ImDrawListPtr dl, Vector2 c, float u, uint col, float th, ReadOnlySpan<Vector2> pts, bool closed)
    {
        Span<Vector2> buf = stackalloc Vector2[pts.Length];
        for (var i = 0; i < pts.Length; i++) buf[i] = new Vector2(c.X + pts[i].X * u, c.Y + pts[i].Y * u);
        dl.AddPolyline(ref MemoryMarshal.GetReference(buf), buf.Length, col, closed ? ImDrawFlags.Closed : ImDrawFlags.None, th);
    }

    /// <summary>Draws <paramref name="icon"/> centred at <paramref name="c"/>, fitting a <paramref name="size"/> box.</summary>
    public static void DrawIcon(ImDrawListPtr dl, Vector2 c, float size, Icon icon, Vector4 color, float thickness = 2f)
    {
        var u   = size / 20f;              // the icons are designed on a 20-unit box
        var col = Theme.ToU32(color);
        // The draw list is passed, never captured: a closure field of an ImGui.NET value type would
        // need ImGui.NET at type-load time (see LoadTests).
        Vector2 P(float x, float y) => new(c.X + x * u, c.Y + y * u);

        switch (icon)
        {
            case Icon.Up:
                LineAt(dl, c, u, col, thickness, 0f, 7f, 0f, -7f);
                PolyAt(dl, c, u, col, thickness, [new(-5.5f, -1.5f), new(0f, -7f), new(5.5f, -1.5f)], false);
                break;
            case Icon.Down:
                LineAt(dl, c, u, col, thickness, 0f, -7f, 0f, 7f);
                PolyAt(dl, c, u, col, thickness, [new(-5.5f, 1.5f), new(0f, 7f), new(5.5f, 1.5f)], false);
                break;
            case Icon.Pencil:
                PolyAt(dl, c, u, col, thickness, [new(-7f, 7f), new(-6f, 3f), new(4f, -7f), new(7f, -4f), new(-3f, 6f)], true);
                LineAt(dl, c, u, col, thickness, 2f, -5f, 5f, -2f);
                break;
            case Icon.Check:
                PolyAt(dl, c, u, col, thickness, [new(-6f, 0.5f), new(-1.5f, 5f), new(6.5f, -4f)], false);
                break;
            case Icon.Cross:
                LineAt(dl, c, u, col, thickness, -5.5f, -5.5f, 5.5f, 5.5f);
                LineAt(dl, c, u, col, thickness, 5.5f, -5.5f, -5.5f, 5.5f);
                break;
            case Icon.Trash:
                LineAt(dl, c, u, col, thickness, -8f, -5f, 8f, -5f);
                PolyAt(dl, c, u, col, thickness, [new(-3f, -5f), new(-3f, -8f), new(3f, -8f), new(3f, -5f)], false);
                PolyAt(dl, c, u, col, thickness, [new(-6f, -5f), new(-5f, 8f), new(5f, 8f), new(6f, -5f)], false);
                LineAt(dl, c, u, col, thickness, -2f, -1f, -2f, 5f); LineAt(dl, c, u, col, thickness, 2f, -1f, 2f, 5f);
                break;
            case Icon.Copy:
                dl.AddRect(P(-2f, -2f), P(7f, 7f), col, 2f * u, ImDrawFlags.None, thickness);
                PolyAt(dl, c, u, col, thickness, [new(-6f, 3f), new(-6f, -6f), new(-5f, -7f), new(3f, -7f)], false);
                break;
            case Icon.Open:
                PolyAt(dl, c, u, col, thickness, [new(2f, -7f), new(7f, -7f), new(7f, -2f)], false);
                LineAt(dl, c, u, col, thickness, 7f, -7f, -1f, 1f);
                // The box is pulled in from the arrow, as Lucide draws it.
                PolyAt(dl, c, u, col, thickness, [new(-2f, -4f), new(-6f, -4f), new(-7f, -3f), new(-7f, 6f), new(-6f, 7f), new(3f, 7f), new(4f, 6f), new(4f, 2f)], false);
                break;
            case Icon.Funnel:
                PolyAt(dl, c, u, col, thickness, [new(-8f, -7f), new(8f, -7f), new(2f, 0f), new(2f, 6f), new(-2f, 8f), new(-2f, 0f)], true);
                break;
            case Icon.Plus:
                LineAt(dl, c, u, col, thickness, 0f, -7f, 0f, 7f);
                LineAt(dl, c, u, col, thickness, -7f, 0f, 7f, 0f);
                break;
            case Icon.Gear:
                dl.AddCircle(c, 3f * u, col, 12, thickness);
                for (var i = 0; i < 8; i++)
                {
                    var a = i * MathF.PI / 4f;
                    var (sn, cs) = MathF.SinCos(a);
                    dl.AddLine(c + new Vector2(cs, sn) * 5.5f * u, c + new Vector2(cs, sn) * 8f * u, col, thickness);
                }
                break;
            case Icon.Dots:
                for (var i = -1; i <= 1; i++) dl.AddCircleFilled(P(i * 6f, 0f), 1.8f * u, col);
                break;
            case Icon.Play:
                dl.AddTriangleFilled(P(-5f, -7f), P(7f, 0f), P(-5f, 7f), col);
                break;
            case Icon.Pause:
                dl.AddRectFilled(P(-6f, -7f), P(-2f, 7f), col, 1f * u);
                dl.AddRectFilled(P(2f, -7f), P(6f, 7f), col, 1f * u);
                break;
        }
    }
}
