// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace Despada.ImGui.UserInterface;

public static partial class Widgets
{
    /// <summary>
    /// A sparkline after the ags traffic widget: the samples as a Catmull-Rom curve (control points
    /// clamped so it never overshoots), the area under it in a vertical fade of <paramref name="color"/>.
    /// <paramref name="flip"/> grows it downwards from the top edge (the ↑ half of the mirrored
    /// pair). <paramref name="phase"/> 0..1 scrolls by a fraction of one step so the line moves
    /// continuously between samples.
    /// </summary>
    public static void Sparkline(ImDrawListPtr dl, Vector2 min, Vector2 max, ReadOnlySpan<float> samples, float scale,
        Vector4 color, bool flip = false, float phase = 0f)
    {
        var n = samples.Length;
        if (n < 2) return;
        var w = max.X - min.X;
        var h = max.Y - min.Y;
        // One step wider than the box: the newest sample slides in from beyond the right edge and
        // the oldest leaves on the left, so no gap opens at the end between samples.
        var step = w / (n - 2);
        var shift = step * phase;
        Span<Vector2> pts = stackalloc Vector2[n];
        for (var i = 0; i < n; i++)
        {
            var v = scale <= 0f ? 0f : Math.Clamp(samples[i] / scale, 0f, 1f);
            var y = 0.08f + 0.86f * v;
            pts[i] = new Vector2(min.X + i * step - shift, flip ? min.Y + y * h : max.Y - y * h);
        }

        // Catmull-Rom → cubic Bézier, 6 segments per span; the control y is clamped to the span.
        const int seg = 6;
        Span<Vector2> curve = stackalloc Vector2[(n - 1) * seg + 1];
        var k = 0;
        for (var i = 0; i < n - 1; i++)
        {
            var p0 = pts[Math.Max(i - 1, 0)]; var p1 = pts[i]; var p2 = pts[i + 1]; var p3 = pts[Math.Min(i + 2, n - 1)];
            var c1 = p1 + (p2 - p0) / 6f;
            var c2 = p2 - (p3 - p1) / 6f;
            var lo = MathF.Min(p1.Y, p2.Y); var hi = MathF.Max(p1.Y, p2.Y);
            c1.Y = Math.Clamp(c1.Y, lo, hi); c2.Y = Math.Clamp(c2.Y, lo, hi);
            for (var s = 0; s < seg; s++)
            {
                var t = s / (float)seg; var u = 1f - t;
                curve[k++] = u * u * u * p1 + 3f * u * u * t * c1 + 3f * u * t * t * c2 + t * t * t * p2;
            }
        }
        curve[k] = pts[n - 1];

        dl.PushClipRect(min, max, true);
        // Area: one triangle strip, two vertices per curve point (on the curve at 0.5 alpha, on the
        // baseline at 0.04) — a true per-vertex fade, no anti-aliasing fringes between slivers.
        var baseY = flip ? min.Y : max.Y;
        var m = curve.Length;
        var uv = ImGuiNET.ImGui.GetFontTexUvWhitePixel();
        var cTop = Theme.ToU32(Theme.WithAlpha(color, 0.5f));
        var cBase = Theme.ToU32(Theme.WithAlpha(color, 0.04f));
        dl.PrimReserve(6 * (m - 1), 2 * m);
        var first = dl._VtxCurrentIdx;
        for (var i = 0; i < m; i++)
        {
            dl.PrimWriteVtx(curve[i], uv, cTop);
            dl.PrimWriteVtx(new Vector2(curve[i].X, baseY), uv, cBase);
        }
        for (var i = 0; i < m - 1; i++)
        {
            var a = (ushort)(first + 2 * i);
            dl.PrimWriteIdx(a); dl.PrimWriteIdx((ushort)(a + 1)); dl.PrimWriteIdx((ushort)(a + 2));
            dl.PrimWriteIdx((ushort)(a + 1)); dl.PrimWriteIdx((ushort)(a + 3)); dl.PrimWriteIdx((ushort)(a + 2));
        }
        dl.AddPolyline(ref MemoryMarshal.GetReference(curve), curve.Length, Theme.ToU32(color), ImDrawFlags.None, 1.2f);
        dl.PopClipRect();
    }

    /// <summary>
    /// The traffic widget (form T3): a hairline box with ↓ growing up from the middle and ↑ growing
    /// down from it, then the two rates in fixed six-character slots. Fixed 152 px wide: the window
    /// never jumps.
    /// </summary>
    public static void Traffic(ReadOnlySpan<float> inSamples, float inScale, ReadOnlySpan<float> outSamples, float outScale,
        float phase, string rateIn, string rateOut)
    {
        const float boxW = 152f, boxH = 36f;
        var pos = ImGuiNET.ImGui.GetCursorScreenPos();
        var dl  = ImGuiNET.ImGui.GetWindowDrawList();
        var min = UiScale.Snap(pos);
        var max = UiScale.Snap(pos + new Vector2(boxW, boxH));
        dl.AddRectFilled(min, max, Theme.ToU32(Theme.P.Deep), ButtonRound);
        var mid = (min.Y + max.Y) * 0.5f;
        // Inset past the corner radius so a peak doesn't poke into the rounded corners.
        var inner = new Vector2(min.X + 3f, min.Y + 2f);
        Sparkline(dl, inner, new Vector2(max.X - 3f, mid), inSamples, inScale, Theme.P.Accent, false, phase);
        Sparkline(dl, new Vector2(min.X + 3f, mid), max - new Vector2(3f, 2f), outSamples, outScale, Theme.P.Accent2, true, phase);
        dl.AddRect(min, max, Theme.ToU32(Theme.Hairline), ButtonRound, ImDrawFlags.None, 1f / UiScale.K);

        // Rates: two lines of 20 px, each "↓ " + a right-aligned 6-char slot.
        var x = max.X + 12f;
        var small = ImGuiNET.ImGui.GetFontSize() * 0.8f;
        var font  = ImGuiNET.ImGui.GetFont();
        var slot  = ImGuiNET.ImGui.CalcTextSize("000000").X * 0.8f;
        dl.AddText(font, small, new Vector2(x, min.Y - 1f), Theme.ToU32(Theme.P.Accent), "↓");
        dl.AddText(font, small, new Vector2(x, mid - 1f), Theme.ToU32(Theme.P.Accent2), "↑");
        var wIn  = ImGuiNET.ImGui.CalcTextSize(rateIn).X * 0.8f;
        var wOut = ImGuiNET.ImGui.CalcTextSize(rateOut).X * 0.8f;
        var tx = x + small + slot;
        dl.AddText(font, small, new Vector2(tx - wIn, min.Y - 1f), Theme.ToU32(Theme.P.Sub), rateIn);
        dl.AddText(font, small, new Vector2(tx - wOut, mid - 1f), Theme.ToU32(Theme.P.Sub), rateOut);

        ImGuiNET.ImGui.Dummy(new Vector2(boxW + 12f + small + slot, boxH));
    }
}
