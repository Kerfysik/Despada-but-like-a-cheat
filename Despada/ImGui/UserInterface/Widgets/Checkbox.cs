// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace Despada.ImGui.UserInterface;

public static partial class Widgets
{
    private const float CheckboxSize  = 28f;
    private const float CheckboxRound = 6f;

    public static bool Checkbox(string id, ref bool value)
    {
        var pos = ImGuiNET.ImGui.GetCursorScreenPos();

        bool clicked = ImGuiNET.ImGui.InvisibleButton(id, new Vector2(CheckboxSize, CheckboxSize));
        bool hovered = ImGuiNET.ImGui.IsItemHovered();
        var  itemId  = ImGuiNET.ImGui.GetItemID();

        if (clicked)
        {
            value = !value;
            if (value)
            {
                var center = new Vector2(pos.X + CheckboxSize * 0.5f, pos.Y + CheckboxSize * 0.5f);
                SpawnBurstSparks(center, 12);
            }
        }

        var dl = ImGuiNET.ImGui.GetWindowDrawList();
        var t  = Animate(itemId, value);

        var min = pos;
        var max = new Vector2(pos.X + CheckboxSize, pos.Y + CheckboxSize);

        var bgOff = hovered ? Theme.Mix(Theme.P.Raised, Theme.P.Sep, 0.5f) : Theme.P.Raised;
        dl.AddRectFilled(min, max, Theme.ToU32(Theme.Mix(bgOff, Theme.P.Accent, t)), CheckboxRound);

        if (t < 0.99f)
            dl.AddRect(min, max, Theme.ToU32(Theme.P.Sep), CheckboxRound, ImDrawFlags.None, 1f);

        if (t > 0.01f)
        {
            var cx = pos.X + CheckboxSize * 0.5f;
            var cy = pos.Y + CheckboxSize * 0.5f;
            var checkCol = Theme.ToU32(Theme.WithAlpha(Theme.IsLight ? Theme.Hex(0xFFFFFF) : Theme.Hex(0x161B2B), t));

            Span<Vector2> checkPts =
            [
                new(cx - 5.5f, cy + 0.5f),
                new(cx - 1f,   cy + 5f),
                new(cx + 6.5f, cy - 4f)
            ];

            dl.AddPolyline(ref MemoryMarshal.GetReference(checkPts), checkPts.Length, checkCol, ImDrawFlags.None, 3f);
        }

        return clicked;
    }

    public static bool CheckboxRow(string label, ref bool value)
    {
        ImGuiNET.ImGui.Spacing();

        var contentW = ImGuiNET.ImGui.GetContentRegionAvail().X;

        var lineX = ImGuiNET.ImGui.GetCursorPosX(); // incl. indent; SameLine offsets count from the window edge

        ImGuiNET.ImGui.AlignTextToFramePadding();
        ImGuiNET.ImGui.TextUnformatted(DisplayLabel(label));
        ImGuiNET.ImGui.SameLine(lineX + contentW - CheckboxSize);

        ImGuiNET.ImGui.PushID(label);
        var result = Checkbox("##cb", ref value);
        ImGuiNET.ImGui.PopID();

        ImGuiNET.ImGui.Spacing();
        return result;
    }
}