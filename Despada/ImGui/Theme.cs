// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using ImGuiNET;

namespace Despada.ImGui;

// Semantic colour tokens (AGS palette: Catppuccin Macchiato on the bar's navy glass,
// Latte-based light variant). Widgets and windows read colours only from here.
public sealed class Palette
{
    public required Vector3 Glass;      // window background, alpha from config
    public required Vector4 Deep;       // tinted strips: input line, tab rows
    public required Vector4 Raised;     // frames, hovered rows
    public required Vector4 Pop;        // popups
    public required Vector4 Line;       // outlines, separators
    public required Vector4 Sep;
    public required Vector4 Fg;
    public required Vector4 Sub;
    public required Vector4 Muted;
    public required Vector4 Accent;     // blue: prompt, caret, cursor, active state
    public required Vector4 Accent2;    // mauve: secondary highlights
    public required Vector4 Link;
    public required Vector4 Green;
    public required Vector4 Yellow;
    public required Vector4 Red;
    public required Vector4 Shadow;
    // Catppuccin surface steps: raised controls, their hover and outlines (v2 widgets).
    public required Vector4 Surface0;
    public required Vector4 Surface1;
    public required Vector4 Surface2;
}

public static class Theme
{
    public static readonly Palette Dark = new()
    {
        Glass   = Rgb(0x161B2B),
        Deep    = HexA(0x000000, 0.16f),
        Raised  = HexA(0x2A3447, 0.70f),
        Pop     = HexA(0x1A1F2E, 0.99f),
        Line    = Hex(0x3A4665),
        Sep     = HexA(0x6E738D, 0.45f),
        Fg      = Hex(0xCAD3F5),
        Sub     = Hex(0xA5ADCB),
        Muted   = Hex(0x8087A2),
        Accent  = Hex(0x8AADF4),
        Accent2 = Hex(0xC6A0F6),
        Link    = Hex(0x91D7E3),
        Green   = Hex(0xA6DA95),
        Yellow  = Hex(0xEED49F),
        Red     = Hex(0xED8796),
        Shadow  = HexA(0x000000, 0.45f),
        Surface0 = Hex(0x363A4F),
        Surface1 = Hex(0x494D64),
        Surface2 = Hex(0x5B6078),
    };

    public static readonly Palette Light = new()
    {
        Glass   = Rgb(0xEFF1F8),
        Deep    = HexA(0x2B3150, 0.05f),
        Raised  = HexA(0xFFFFFF, 0.85f),
        Pop     = HexA(0xFAFBFD, 0.99f),
        Line    = Hex(0xC3CADF),
        Sep     = HexA(0x7882AA, 0.40f),
        Fg      = Hex(0x2B3150),
        Sub     = Hex(0x4C5373),
        Muted   = Hex(0x5C6383),
        Accent  = Hex(0x1E66F5),
        Accent2 = Hex(0x8839EF),
        Link    = Hex(0x0B7FAD),
        Green   = Hex(0x3D8F2A),
        Yellow  = Hex(0x9A6309),
        Red     = Hex(0xD20F39),
        Shadow  = HexA(0x1E233C, 0.25f),
        Surface0 = Hex(0xDCE0EF),
        Surface1 = Hex(0xC3CADF),
        Surface2 = Hex(0xA6AFCB),
    };

    // Logo gradient stays the AGS signature red → mauve → blue in both themes.
    public static readonly Vector4 LogoRed  = Hex(0xED8796);
    public static readonly Vector4 LogoBlue = Hex(0x8AADF4);

    public static Palette P { get; private set; } = Dark;
    public static float GlassAlpha { get; private set; } = 0.92f;

    // Metrics are written as 1080p pixels and converted to logical units (see UiScale).
    private const float D = UiScale.DesignHeight / 1080f;

    public const float WindowRounding = 18f * D;
    public const float SmallRounding  = 10f * D;

    private static bool _sizesScaled;

    public static Vector4 WindowBg => new(P.Glass, GlassAlpha);

    // Selection / active tint: accent at low alpha; SelR is the right end of the gradient variant.
    // Same alpha on both: Gfx.Shade keeps the vertex alpha of the left colour anyway.
    public static Vector4 Sel  => WithAlpha(P.Accent, IsLight ? 0.13f : 0.20f);
    public static Vector4 SelR => WithAlpha(P.Accent2, IsLight ? 0.13f : 0.20f);

    public static bool IsLight => ReferenceEquals(P, Light);

    // v2 widget surfaces: quiet hairlines, raised fills a step above the glass, a one-pixel
    // highlight on the top edge, focus as the accent at half alpha plus a soft outer glow.
    public static Vector4 Hairline    => WithAlpha(P.Surface1, IsLight ? 0.95f : 0.55f);
    public static Vector4 HairlineHot => WithAlpha(P.Surface2, IsLight ? 1f : 0.85f);
    public static Vector4 RaisedFill  => WithAlpha(P.Surface0, 0.75f);
    public static Vector4 RaisedHot   => WithAlpha(P.Surface1, 0.90f);
    public static Vector4 Pressed     => WithAlpha(P.Surface0, 0.50f);
    public static Vector4 Hilite      => IsLight ? WithAlpha(Hex(0xFFFFFF), 0.7f) : WithAlpha(Hex(0xB7BDF8), 0.09f);
    /// <summary>The sliding thumb of a segmented switch: a step up in dark, white in light.</summary>
    public static Vector4 Thumb       => IsLight ? P.Raised : RaisedHot;
    public static Vector4 FocusRing   => WithAlpha(P.Accent, 0.55f);
    public static Vector4 FocusGlow   => WithAlpha(P.Accent, 0.16f);
    /// <summary>Text on an accent or gradient fill.</summary>
    public static Vector4 Ink         => IsLight ? Hex(0xFFFFFF) : new Vector4(Dark.Glass, 1f);

    public static void Apply(bool light, float glassAlpha)
    {
        P = light ? Light : Dark;
        GlassAlpha = Math.Clamp(glassAlpha, 0.5f, 1f);

        var style  = ImGuiNET.ImGui.GetStyle();
        var colors = style.Colors;

        // Once per context for everything not set below; the values set below are absolute.
        if (!_sizesScaled)
        {
            style.ScaleAllSizes(D);
            _sizesScaled = true;
        }

        style.AntiAliasedLines       = true;
        style.AntiAliasedLinesUseTex = false;
        style.AntiAliasedFill        = true;

        style.WindowRounding    = WindowRounding;
        style.ChildRounding     = SmallRounding;
        style.FrameRounding     = 8f;
        style.PopupRounding     = 6f;
        style.ScrollbarRounding = 4f;
        style.GrabRounding      = 4f;
        style.TabRounding       = 8f;

        style.WindowBorderSize  = 0f;
        style.ChildBorderSize   = 0f;
        style.FrameBorderSize   = 0f;
        style.PopupBorderSize   = 1f;
        style.TabBorderSize     = 0f;

        style.WindowPadding    = new Vector2(12f, 12f);
        style.FramePadding     = new Vector2(10f, 6f);
        style.ItemSpacing      = new Vector2(8f, 7f);
        style.ItemInnerSpacing = new Vector2(6f, 4f);
        style.IndentSpacing    = 20f;
        style.ScrollbarSize    = 10f;
        style.GrabMinSize      = 10f;
        // Small on purpose (toasts are 280×64); the main window sets its own constraints.
        style.WindowMinSize    = new Vector2(32f, 32f) * D;

        colors[(int)ImGuiCol.WindowBg]       = WindowBg;
        colors[(int)ImGuiCol.ChildBg]        = Vector4.Zero;
        colors[(int)ImGuiCol.PopupBg]        = P.Pop;
        colors[(int)ImGuiCol.Border]         = P.Line;
        colors[(int)ImGuiCol.BorderShadow]   = Vector4.Zero;

        colors[(int)ImGuiCol.Text]           = P.Fg;
        colors[(int)ImGuiCol.TextDisabled]   = P.Muted;
        colors[(int)ImGuiCol.TextSelectedBg] = WithAlpha(P.Accent, 0.35f);
        colors[(int)ImGuiCol.TextLink]       = P.Link;

        colors[(int)ImGuiCol.FrameBg]        = P.Deep;
        colors[(int)ImGuiCol.FrameBgHovered] = P.Raised;
        colors[(int)ImGuiCol.FrameBgActive]  = Sel;

        colors[(int)ImGuiCol.Button]         = Vector4.Zero;
        colors[(int)ImGuiCol.ButtonHovered]  = P.Raised;
        colors[(int)ImGuiCol.ButtonActive]   = Sel;

        colors[(int)ImGuiCol.Header]         = Sel;
        colors[(int)ImGuiCol.HeaderHovered]  = P.Raised;
        colors[(int)ImGuiCol.HeaderActive]   = Sel;

        colors[(int)ImGuiCol.CheckMark]        = P.Accent;
        colors[(int)ImGuiCol.SliderGrab]       = P.Accent;
        colors[(int)ImGuiCol.SliderGrabActive] = P.Accent2;

        colors[(int)ImGuiCol.ScrollbarBg]          = Vector4.Zero;
        colors[(int)ImGuiCol.ScrollbarGrab]        = WithAlpha(P.Muted, 0.35f);
        colors[(int)ImGuiCol.ScrollbarGrabHovered] = WithAlpha(P.Muted, 0.55f);
        colors[(int)ImGuiCol.ScrollbarGrabActive]  = WithAlpha(P.Muted, 0.75f);

        colors[(int)ImGuiCol.Separator]        = P.Sep;
        colors[(int)ImGuiCol.SeparatorHovered] = P.Accent;
        colors[(int)ImGuiCol.SeparatorActive]  = P.Accent2;

        colors[(int)ImGuiCol.ResizeGrip]        = Vector4.Zero;
        colors[(int)ImGuiCol.ResizeGripHovered] = WithAlpha(P.Accent, 0.5f);
        colors[(int)ImGuiCol.ResizeGripActive]  = P.Accent;

        colors[(int)ImGuiCol.Tab]         = Vector4.Zero;
        colors[(int)ImGuiCol.TabHovered]  = P.Raised;
        colors[(int)ImGuiCol.TabSelected] = Sel;

        colors[(int)ImGuiCol.TableHeaderBg]     = P.Deep;
        colors[(int)ImGuiCol.TableBorderStrong] = P.Sep;
        colors[(int)ImGuiCol.TableBorderLight]  = Fade(P.Sep, 0.5f);
        colors[(int)ImGuiCol.TableRowBg]        = Vector4.Zero;
        colors[(int)ImGuiCol.TableRowBgAlt]     = P.Deep;

        colors[(int)ImGuiCol.NavCursor]             = P.Accent;
        // ImGui's veil behind a modal covers the whole screen, the game included, and is read at
        // EndFrame (a push around the popup can't change it): off, the finder dims the window itself.
        colors[(int)ImGuiCol.ModalWindowDimBg]      = Vector4.Zero;
        colors[(int)ImGuiCol.NavWindowingHighlight] = P.Accent;
        colors[(int)ImGuiCol.DragDropTarget]        = P.Accent2;
    }

    public static Vector4 Hex(uint rgb) => HexA(rgb, 1f);

    public static Vector4 HexA(uint rgb, float a)
    {
        var c = Rgb(rgb);
        return new Vector4(c, a);
    }

    private static Vector3 Rgb(uint rgb) => new(
        ((rgb >> 16) & 0xFF) / 255f,
        ((rgb >> 8) & 0xFF) / 255f,
        (rgb & 0xFF) / 255f);

    // Goes through ImGui so style.Alpha (and with it BeginDisabled) dims custom-drawn widgets too.
    public static uint ToU32(Vector4 c) => ImGuiNET.ImGui.GetColorU32(c);

    /// <summary>The colour with this alpha (for raw constants; a token's own alpha is replaced).</summary>
    public static Vector4 WithAlpha(Vector4 c, float a) => new(c.X, c.Y, c.Z, a);
    /// <summary>The colour at a fraction of its own alpha: for anything derived from a palette token.</summary>
    public static Vector4 Fade(Vector4 c, float k) => new(c.X, c.Y, c.Z, c.W * k);

    public static Vector4 Mix(Vector4 a, Vector4 b, float t) => a + (b - a) * t;
}
