// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Despada.Core;
using ImGuiNET;

namespace Despada.ImGui.UserInterface;

// The floating main window: logo + section tabs, a per-section second row, and the section body.
// Sections are added here as their features land; for now Console and Settings.
public static class Shell
{
    private const float D = UiScale.DesignHeight / 1080f; // 1080p px → logical units

    // Sections: Console, one per feature that has a tab (its id), Settings on the right.
    private const string ConsoleId  = "#console";
    private const string SettingsId = "#settings";

    private static string _section = ConsoleId;

    // Header drag: started by the header button last frame and applied before Begin, so the window's
    // clip/work rects, the blurred backdrop and the content all use the new position.
    private static bool     _dragging;
    private static Vector2? _winPos;

    /// <summary>Called when the overlay becomes visible.</summary>
    public static void OnOpened()
    {
        if (_section == ConsoleId)
            ConsolePanel.RequestFocus();
    }

    /// <summary>Called when the overlay is hidden.</summary>
    public static void OnClosed() => SettingsPanel.CancelCapture();

    public static void Draw()
    {
        Widgets.ResetFrame();
        ConsolePanel.InputActive = false;

        var display = ImGuiNET.ImGui.GetIO().DisplaySize;
        var size    = Vector2.Min(new Vector2(1000f * D, 632f * D), display);
        ImGuiNET.ImGui.SetNextWindowSize(size, ImGuiCond.Always);
        if (_winPos is { } last)
        {
            var target = last;
            if (_dragging && ImGuiNET.ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0f))
                target += ImGuiNET.ImGui.GetIO().MouseDelta;
            // Stays on screen (drags, resolution changes).
            target = Vector2.Clamp(target, Vector2.Zero, Vector2.Max(Vector2.Zero, display - size));
            ImGuiNET.ImGui.SetNextWindowPos(target, ImGuiCond.Always);
        }
        else
            ImGuiNET.ImGui.SetNextWindowPos((display - size) * 0.5f, ImGuiCond.Always);

        // With blur the window background is drawn by DrawBackdrop instead.
        var blur = GlassBlur.Available;
        if (blur) ImGuiNET.ImGui.PushStyleColor(ImGuiCol.WindowBg, Vector4.Zero);

        // NoMove: ImGui would otherwise move the window from any empty spot (children included);
        // it is dragged by its header only (DrawHeader). Fixed size, no resizing.
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var open = ImGuiNET.ImGui.Begin("despada##main",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoResize);
        ImGuiNET.ImGui.PopStyleVar();
        if (blur) ImGuiNET.ImGui.PopStyleColor();

        _winPos = ImGuiNET.ImGui.GetWindowPos();
        if (blur) DrawBackdrop();

        if (open)
        {
            DrawHeader();

            if (_section == ConsoleId)
            {
                BeginRow(36f * D);
                ConsolePanel.DrawSessionTabs();
                EndRow();
                ConsolePanel.Draw();
            }
            else if (_section == SettingsId)
            {
                ImGuiNET.ImGui.Dummy(new Vector2(0f, 8f * D));
                SettingsPanel.Draw();
            }
            else if (Features.Find(_section) is { Tab: not null } feature)
            {
                // A feature's tab: its on/off switch (and why it can't be on) lead the second row.
                BeginRow(36f * D);
                Features.DrawTabRow(feature);
                EndRow();
                Features.DrawTabBody(feature);
            }
            else
                _section = ConsoleId;
        }

        ImGuiNET.ImGui.End();
        Widgets.EndFrame();
    }

    // Blurred game image under the window plus the glass tint on top. The tint is lighter than the
    // plain glass: the blur already does most of the work.
    private static void DrawBackdrop()
    {
        var dl  = ImGuiNET.ImGui.GetWindowDrawList();
        var min = ImGuiNET.ImGui.GetWindowPos();
        var max = min + ImGuiNET.ImGui.GetWindowSize();

        // Still requested while the blur retries after a failed frame (Texture is 0 then): tint only.
        GlassBlur.Request(min, max);
        var tex = GlassBlur.Texture;
        if (tex != 0)
        {
            GlassBlur.GetUv(min, max, out var uv0, out var uv1);
            dl.AddImageRounded(tex, min, max, uv0, uv1, 0xFFFFFFFF, Theme.WindowRounding);
        }

        var tint = 0.45f + (Theme.GlassAlpha - 0.6f) / 0.4f * 0.5f;
        dl.AddRectFilled(min, max, Theme.ToU32(new Vector4(Theme.P.Glass, tint)), Theme.WindowRounding);
    }

    private static void DrawHeader()
    {
        var dl     = ImGuiNET.ImGui.GetWindowDrawList();
        var winPos = ImGuiNET.ImGui.GetWindowPos();
        var width  = ImGuiNET.ImGui.GetWindowWidth();
        var h      = 38f * D;
        var padX   = 18f * D;

        // Drag handle under the whole header; tabs drawn later take hover priority.
        ImGuiNET.ImGui.SetCursorPos(Vector2.Zero);
        ImGuiNET.ImGui.SetNextItemAllowOverlap();
        ImGuiNET.ImGui.InvisibleButton("##drag", new Vector2(width, h));
        _dragging = ImGuiNET.ImGui.IsItemActive();

        var x    = winPos.X + padX;
        var logo = 18f * D;
        x += Logo.DrawWithWordmark(dl, new Vector2(x, winPos.Y + (h - logo) * 0.5f), logo, 10f * D) + 28f * D;

        x += SectionTab(ConsoleId, "Console", x, h) + 22f * D;
        foreach (var f in Features.WithTabs)
            x += SectionTab(f.Id, f.Tab!, x, h) + 22f * D;

        SectionTab(SettingsId, "Settings", winPos.X + width - padX - ImGuiNET.ImGui.CalcTextSize("Settings").X, h);

        dl.AddLine(new Vector2(winPos.X, winPos.Y + h), new Vector2(winPos.X + width, winPos.Y + h),
            Theme.ToU32(Theme.P.Sep));

        ImGuiNET.ImGui.SetCursorPos(new Vector2(0f, h + 1f));
    }

    /// <summary>A text tab in the header; returns its width.</summary>
    private static float SectionTab(string id, string label, float x, float headerH)
    {
        var winPos = ImGuiNET.ImGui.GetWindowPos();
        var size   = ImGuiNET.ImGui.CalcTextSize(label);
        var active = _section == id;

        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, winPos.Y));
        if (ImGuiNET.ImGui.InvisibleButton(id, new Vector2(size.X, headerH)) && !active)
        {
            _section = id;
            SettingsPanel.CancelCapture();
            if (id == ConsoleId) ConsolePanel.RequestFocus();
        }

        var hovered = ImGuiNET.ImGui.IsItemHovered();
        var dl      = ImGuiNET.ImGui.GetWindowDrawList();
        var color   = active || hovered ? Theme.P.Fg : Theme.P.Muted;
        dl.AddText(new Vector2(x, winPos.Y + (headerH - size.Y) * 0.5f), Theme.ToU32(color), label);

        if (active)
        {
            var y = winPos.Y + headerH - 2f;
            dl.AddRectFilledMultiColor(new Vector2(x, y), new Vector2(x + size.X, y + 2f),
                Theme.ToU32(Theme.P.Accent), Theme.ToU32(Theme.P.Accent2),
                Theme.ToU32(Theme.P.Accent2), Theme.ToU32(Theme.P.Accent));
        }

        return size.X;
    }

    private static void BeginRow(float height)
    {
        var pos = ImGuiNET.ImGui.GetCursorPos();
        ImGuiNET.ImGui.SetCursorPos(new Vector2(12f * D, pos.Y + (height - ImGuiNET.ImGui.GetFrameHeight()) * 0.5f));
        ImGuiNET.ImGui.BeginGroup();
        _rowBottom = pos.Y + height;
    }

    private static float _rowBottom;

    private static void EndRow()
    {
        ImGuiNET.ImGui.EndGroup();
        ImGuiNET.ImGui.SetCursorPos(new Vector2(0f, _rowBottom));
    }

    /// <summary>
    /// Rounded text pill used for session / document tabs. Returns true when the pill was clicked;
    /// <paramref name="closeClicked"/> reports a click on its × instead.
    /// </summary>
    public static bool Pill(string id, string label, bool active, bool closable, out bool closeClicked)
    {
        closeClicked = false;

        var padX   = 10f * D;
        var h      = ImGuiNET.ImGui.GetFrameHeight();
        var textW  = ImGuiNET.ImGui.CalcTextSize(label).X;
        var closeW = closable ? ImGuiNET.ImGui.CalcTextSize("×").X + 8f * D : 0f;
        var size   = new Vector2(textW + closeW + padX * 2f, h);
        var pos    = ImGuiNET.ImGui.GetCursorScreenPos();

        var clicked = ImGuiNET.ImGui.InvisibleButton(id, size);
        var hovered = ImGuiNET.ImGui.IsItemHovered();

        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        if (active)
            Gfx.GradientRect(dl, pos, pos + size, Theme.Sel, Theme.SelR, Theme.SmallRounding);
        else if (hovered)
            dl.AddRectFilled(pos, pos + size, Theme.ToU32(Theme.P.Raised), Theme.SmallRounding);

        var ty = pos.Y + (h - ImGuiNET.ImGui.GetFontSize()) * 0.5f;
        dl.AddText(new Vector2(pos.X + padX, ty), Theme.ToU32(active || hovered ? Theme.P.Fg : Theme.P.Muted), label);

        if (closable)
        {
            var cx = pos.X + padX + textW + 8f * D;
            var mouse = ImGuiNET.ImGui.GetIO().MousePos;
            var overClose = hovered && mouse.X >= cx - 4f * D;
            dl.AddText(new Vector2(cx, ty), Theme.ToU32(overClose ? Theme.P.Fg : Theme.P.Muted), "×");
            if (clicked && overClose)
            {
                closeClicked = true;
                clicked = false;
            }
        }

        return clicked;
    }
}
