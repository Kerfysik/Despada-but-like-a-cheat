// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using ImGuiNET;

namespace Despada.ImGui.UserInterface;

public static partial class Widgets
{
    private const float ToggleW     = 46f;
    private const float ToggleH     = 24f;
    private const float KnobR       = 9f;
    private const float KnobPad     = 3f;
    private const float ToggleSpeed = 20f;

    private static void DrawToggleAt(ImDrawListPtr dl, Vector2 pos, bool value, bool hovered, uint id)
    {
        var t = Animate(id, value);

        var min    = pos;
        var max    = new Vector2(pos.X + ToggleW, pos.Y + ToggleH);
        var radius = ToggleH * 0.5f;

        var offColor = hovered ? Theme.Mix(Theme.P.Raised, Theme.P.Sep, 0.5f) : Theme.P.Raised;
        var onColor  = hovered ? Theme.P.Accent : Theme.WithAlpha(Theme.P.Accent, 0.75f);

        dl.AddRectFilled(min, max, Theme.ToU32(Theme.Mix(offColor, onColor, t)), radius);

        if (t > 0.01f)
        {
            var gradAlpha = t * 0.25f;
            // Over the whole rounded track: a rect inset by the radius left a seam where the caps begin.
            Gfx.GradientRect(dl, min, max, Theme.WithAlpha(Theme.P.Accent, gradAlpha),
                Theme.WithAlpha(Theme.P.Accent2, gradAlpha), radius);
        }

        var knobOff = min.X + KnobR + KnobPad;
        var knobOn  = max.X - KnobR - KnobPad;
        var knobX   = knobOff + (knobOn - knobOff) * t;

        var knobAlpha = 0.4f + 0.6f * t;
        var knobColor = Theme.WithAlpha(Theme.P.Fg, knobAlpha);
        dl.AddCircleFilled(new Vector2(knobX, min.Y + ToggleH * 0.5f), KnobR, Theme.ToU32(knobColor));
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
