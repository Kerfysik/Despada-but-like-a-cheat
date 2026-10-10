// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using ImGuiNET;

namespace Despada.ImGui.UserInterface;

public static partial class Widgets
{
    private const float ToggleW     = 44f;
    private const float ToggleH     = 24f;
    private const float KnobR       = 9f;
    private const float KnobPad     = 3f;
    private const float ToggleSpeed = 20f;

    // v2: the track is a hairline Deep pill when off and the plain accent when on; the knob is a
    // Sub disc that becomes ink on the accent, and it springs across rather than easing.
    private static void DrawToggleAt(ImDrawListPtr dl, Vector2 pos, bool value, bool hovered, uint id)
    {
        var t   = Animate(id, value);
        var hot = Animate(id ^ 0x33u, hovered, 24f);

        var min    = pos;
        var max    = new Vector2(pos.X + ToggleW, pos.Y + ToggleH);
        var radius = ToggleH * 0.5f;

        var off = Theme.Mix(Theme.RaisedFill, Theme.RaisedHot, hot);
        dl.AddRectFilled(min, max, Theme.ToU32(Theme.Mix(off, Theme.P.Accent, t)), radius);
        if (t < 0.99f)
            dl.AddRect(UiScale.Snap(min), UiScale.Snap(max), Theme.ToU32(Theme.WithAlpha(Theme.Hairline, Theme.Hairline.W * (1f - t))), radius, ImDrawFlags.None, 1f / UiScale.K);
        Gfx.HLine(dl, min.X + radius * 0.6f, max.X - radius * 0.6f, min.Y + 1f, Theme.ToU32(Theme.WithAlpha(Theme.Hilite, Theme.Hilite.W * (1f - t))));

        // The spring runs on 0..1, not on screen x: the knob must not trail the track when the
        // window is dragged.
        var knobOff = min.X + KnobR + KnobPad;
        var knobOn  = max.X - KnobR - KnobPad;
        var s       = SpringTo(id ^ 0x44u, value ? 1f : 0f, 800f, 0.72f);
        var knobC   = new Vector2(knobOff + (knobOn - knobOff) * s, min.Y + ToggleH * 0.5f);
        dl.AddCircleFilled(knobC + new Vector2(0f, 1f), KnobR, Theme.ToU32(Theme.WithAlpha(Theme.P.Shadow, 0.35f)));
        dl.AddCircleFilled(knobC, KnobR, Theme.ToU32(Theme.Mix(Theme.P.Sub, Theme.Ink, t)));
    }

    public static bool Toggle(string id, ref bool value)
    {
        // A frame-high item with the switch centred in it, so it lines up with what shares its line
        // (AlignTextToFramePadding text, pills, inputs) instead of hanging from the line's top.
        var h   = MathF.Max(ToggleH, ImGuiNET.ImGui.GetFrameHeight());
        var pos = ImGuiNET.ImGui.GetCursorScreenPos();

        var clicked = ImGuiNET.ImGui.InvisibleButton(id, new Vector2(ToggleW, h));
        if (clicked)
            value = !value;

        DrawToggleAt(ImGuiNET.ImGui.GetWindowDrawList(), pos + new Vector2(0f, (h - ToggleH) * 0.5f), value,
            ImGuiNET.ImGui.IsItemHovered(), ImGuiNET.ImGui.GetItemID());
        return clicked;
    }

    public static bool ToggleRow(string label, ref bool value)
    {
        ImGuiNET.ImGui.Spacing();

        var contentW = ImGuiNET.ImGui.GetContentRegionAvail().X;

        var lineX = ImGuiNET.ImGui.GetCursorPosX(); // incl. indent; SameLine offsets count from the window edge

        ImGuiNET.ImGui.AlignTextToFramePadding();
        ImGuiNET.ImGui.TextUnformatted(DisplayLabel(label));
        ImGuiNET.ImGui.SameLine(lineX + contentW - ToggleW);

        ImGuiNET.ImGui.PushID(label);
        var result = Toggle("##tgl", ref value);
        ImGuiNET.ImGui.PopID();

        ImGuiNET.ImGui.Spacing();
        return result;
    }
}
