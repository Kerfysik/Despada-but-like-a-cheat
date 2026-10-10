// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace Despada.ImGui.UserInterface;

internal static unsafe class Gfx
{
    private static delegate* unmanaged<nint, int, int, Vector2, Vector2, uint, uint, void> _shade;
    private static bool _resolved;

    /// <summary>
    /// Recolours vertices [<paramref name="start"/>, <paramref name="end"/>) of the draw list with a linear
    /// gradient between p0 and p1, keeping each vertex's alpha (ImGui::ShadeVertsLinearColorGradientKeepAlpha).
    /// Lets rounded shapes and strokes get gradients, which AddRectFilledMultiColor can't.
    /// </summary>
    public static void Shade(ImDrawListPtr dl, int start, int end, Vector2 p0, Vector2 p1, uint c0, uint c1)
    {
        if (!_resolved)
        {
            _resolved = true;
            if (NativeLoader.Handle != 0 &&
                NativeLibrary.TryGetExport(NativeLoader.Handle, "igShadeVertsLinearColorGradientKeepAlpha", out var fn))
                _shade = (delegate* unmanaged<nint, int, int, Vector2, Vector2, uint, uint, void>)fn;
        }

        // Without the export shapes keep their flat colour.
        if (_shade != null && end > start)
            _shade((nint)dl.NativePtr, start, end, p0, p1, c0, c1);
    }

    /// <summary>
    /// Horizontal hairline: exactly one framebuffer pixel row (the one starting at <paramref name="y"/>,
    /// snapped), drawn as a plain rect. An anti-aliased 1/K-thick AddLine lands between pixel rows
    /// whenever K ≠ 1 and smears into two half-bright rows; a 1 px non-AA rect always covers one row
    /// at full alpha.
    /// </summary>
    public static void HLine(ImDrawListPtr dl, float x0, float x1, float y, uint col)
    {
        var top = UiScale.Snap(y);
        dl.AddRectFilled(new Vector2(UiScale.Snap(x0), top), new Vector2(UiScale.Snap(x1), top + 1f / UiScale.K), col);
    }

    /// <summary>Vertical hairline, see <see cref="HLine"/>. Ends meet an HLine at the same y exactly.</summary>
    public static void VLine(ImDrawListPtr dl, float x, float y0, float y1, uint col)
    {
        var left = UiScale.Snap(x);
        dl.AddRectFilled(new Vector2(left, UiScale.Snap(y0)), new Vector2(left + 1f / UiScale.K, UiScale.Snap(y1)), col);
    }

    /// <summary>
    /// A raised control surface (v2 widgets): the fill, a hairline outline and a one-pixel highlight
    /// along the top edge, inset so it follows the rounding.
    /// </summary>
    public static void Raised(ImDrawListPtr dl, Vector2 min, Vector2 max, float rounding, Vector4 fill, Vector4 border, bool hilite = true)
    {
        // On the pixel grid, or a one-pixel border smears over two rows.
        min = UiScale.Snap(min);
        max = UiScale.Snap(max);
        dl.AddRectFilled(min, max, Theme.ToU32(fill), rounding);
        if (hilite)
        {
            var inset = MathF.Max(1f, rounding * 0.6f);
            HLine(dl, min.X + inset, max.X - inset, min.Y + 1f, Theme.ToU32(Theme.Hilite));
        }
        if (border.W > 0f)
            dl.AddRect(min, max, Theme.ToU32(border), rounding, ImDrawFlags.None, 1f / UiScale.K);
    }

    /// <summary>Focus: the accent ring at <paramref name="t"/> plus a 3 px glow fading outwards.</summary>
    public static void FocusRing(ImDrawListPtr dl, Vector2 min, Vector2 max, float rounding, float t)
    {
        if (t <= 0.01f) return;
        var glow = Theme.FocusGlow;
        for (var i = 1; i <= 3; i++)
        {
            var a = glow.W * t * (1f - (i - 1) / 3f);
            dl.AddRect(min - new Vector2(i, i), max + new Vector2(i, i), Theme.ToU32(Theme.WithAlpha(glow, a)), rounding + i, ImDrawFlags.None, 1f);
        }
        dl.AddRect(min, max, Theme.ToU32(Theme.WithAlpha(Theme.FocusRing, Theme.FocusRing.W * t)), rounding, ImDrawFlags.None, 1f / UiScale.K);
    }

    /// <summary>A soft drop shadow under a popup: a few expanding rects fading out.</summary>
    public static void Shadow(ImDrawListPtr dl, Vector2 min, Vector2 max, float rounding, float spread = 14f, float alpha = 0.35f)
    {
        var steps = 6;
        for (var i = steps; i >= 1; i--)
        {
            var d = spread * i / steps;
            var a = alpha * (1f - (float)i / (steps + 1)) / steps * 2.2f;
            // Offset downwards: the shadow starts a little below the top edge and reaches further below.
            dl.AddRectFilled(min + new Vector2(-d, d * 0.2f), max + new Vector2(d, d * 1.2f), Theme.ToU32(Theme.WithAlpha(Theme.P.Shadow, a)), rounding + d);
        }
    }

    /// <summary>Rounded rect filled with a horizontal gradient.</summary>
    public static void GradientRect(ImDrawListPtr dl, Vector2 min, Vector2 max, Vector4 left, Vector4 right, float rounding)
    {
        var start = dl.VtxBuffer.Size;
        dl.AddRectFilled(min, max, Theme.ToU32(left), rounding);
        Shade(dl, start, dl.VtxBuffer.Size, min, new Vector2(max.X, min.Y), Theme.ToU32(left), Theme.ToU32(right));
    }

    /// <summary>
    /// Recolours vertices [start, end) with a horizontal 3-stop gradient (c0 → c1 → c2 → c0) that
    /// repeats every <paramref name="width"/> and is shifted by <paramref name="phase"/> (0..1 = one
    /// cycle). Vertex alpha is kept, so anti-aliased fringes stay soft.
    /// </summary>
    public static void RunningGradient(ImDrawListPtr dl, int start, int end, float x0, float width, float phase,
        Vector4 c0, Vector4 c1, Vector4 c2)
    {
        if (end <= start || width <= 0f) return;
        var vtx = (ImDrawVert*)dl.VtxBuffer.Data;
        for (var i = start; i < end; i++)
        {
            var t = (vtx[i].pos.X - x0) / width - phase;
            t -= MathF.Floor(t);
            var seg = t * 3f;
            Vector4 c = seg < 1f ? Vector4.Lerp(c0, c1, seg)
                      : seg < 2f ? Vector4.Lerp(c1, c2, seg - 1f)
                      :            Vector4.Lerp(c2, c0, seg - 2f);
            var a = (vtx[i].col >> 24) & 0xFF;
            vtx[i].col = (a << 24)
                       | ((uint)(Math.Clamp(c.Z, 0f, 1f) * 255f + 0.5f) << 16)
                       | ((uint)(Math.Clamp(c.Y, 0f, 1f) * 255f + 0.5f) << 8)
                       |  (uint)(Math.Clamp(c.X, 0f, 1f) * 255f + 0.5f);
        }
    }
}
