// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace Despada.ImGui.UserInterface;

// "Prompt blade" mark (Assets/logo.svg) + "DESPADA" wordmark, drawn with primitives and recoloured
// afterwards with one running gradient across both (like the AGS bar).
internal static class Logo
{
    private const float Tracking    = 0.14f; // em
    private const float GradPeriod  = 4f;    // seconds per full cycle

    /// <summary>Draws the mark at <paramref name="pos"/> (size × size) and the wordmark after it; returns the total width.</summary>
    public static float DrawWithWordmark(ImDrawListPtr dl, Vector2 pos, float size, float gap)
    {
        var vtxStart = dl.VtxBuffer.Size;
        var col      = Theme.ToU32(Theme.P.Fg);

        DrawMark(dl, pos, size, col);
        var width = size + gap + DrawWordmark(dl, new Vector2(pos.X + size + gap, pos.Y), size, col);

        var phase = (float)(ImGuiNET.ImGui.GetTime() / GradPeriod);
        Gfx.RunningGradient(dl, vtxStart, dl.VtxBuffer.Size, pos.X, width, phase,
            Theme.LogoRed, Theme.P.Accent2, Theme.LogoBlue);
        return width;
    }

    private static void DrawMark(ImDrawListPtr dl, Vector2 pos, float size, uint col)
    {
        var k = size / 64f;
        Vector2 P(float x, float y) => new(pos.X + x * k, pos.Y + y * k);

        // Chevron with round ends/joint (polylines have flat caps).
        var stroke = MathF.Max(7f * k, 1.5f);
        Span<Vector2> chevron = [P(9, 16), P(25, 32), P(9, 48)];
        dl.AddPolyline(ref MemoryMarshal.GetReference(chevron), chevron.Length, col, ImDrawFlags.None, stroke);
        foreach (var p in chevron)
            dl.AddCircleFilled(p, stroke * 0.5f, col);

        // Blade and guard.
        dl.AddTriangleFilled(P(31, 28), P(59, 32), P(31, 36), col);
        var guard = MathF.Max(5f * k, 1f);
        dl.AddLine(P(30, 21), P(30, 43), col, guard);
        dl.AddCircleFilled(P(30, 21), guard * 0.5f, col);
        dl.AddCircleFilled(P(30, 43), guard * 0.5f, col);
    }

    // ExtraBold caps with tracking, vertically centred on the mark. Falls back to the body font.
    private static unsafe float DrawWordmark(ImDrawListPtr dl, Vector2 pos, float markSize, uint col)
    {
        const string text = "DESPADA";
        var font = ImGuiRenderer.WordmarkFont != 0
            ? new ImFontPtr((ImFont*)ImGuiRenderer.WordmarkFont)
            : ImGuiNET.ImGui.GetFont();
        var size = ImGuiRenderer.WordmarkFont != 0
            ? font.FontSize * ImGuiNET.ImGui.GetIO().FontGlobalScale
            : ImGuiNET.ImGui.GetFontSize() * 0.7f;

        var y = pos.Y + (markSize - size) * 0.5f;
        var x = pos.X;
        for (var i = 0; i < text.Length; i++)
        {
            var glyph = text.Substring(i, 1);
            dl.AddText(font, size, new Vector2(x, y), col, glyph);
            x += font.CalcTextSizeA(size, float.MaxValue, 0f, glyph).X;
            if (i < text.Length - 1) x += size * Tracking;
        }
        return x - pos.X;
    }
}
