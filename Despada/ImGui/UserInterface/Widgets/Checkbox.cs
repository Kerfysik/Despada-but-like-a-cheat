// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace Despada.ImGui.UserInterface;

public static partial class Widgets
{
    private const float CheckboxSize  = 22f;
    private const float CheckboxRound = 6f;

    /// <summary>
    /// v2 flag: a Deep hairline box that fills with <paramref name="color"/> (the accent, or a rule
    /// action's colour) when on; the check is drawn as a stroke that grows in.
    /// </summary>
    public static bool Checkbox(string id, ref bool value, Vector4? color = null)
    {
        // A frame-high item with the box centred, so it sits on the line like the toggle does.
        var h    = MathF.Max(CheckboxSize, ImGuiNET.ImGui.GetFrameHeight());
        var item = ImGuiNET.ImGui.GetCursorScreenPos();
        var pos  = item + new Vector2(0f, (h - CheckboxSize) * 0.5f);

        bool clicked = ImGuiNET.ImGui.InvisibleButton(id, new Vector2(CheckboxSize, h));
        bool hovered = ImGuiNET.ImGui.IsItemHovered();
        var  itemId  = ImGuiNET.ImGui.GetItemID();

        if (clicked) value = !value;

        var dl  = ImGuiNET.ImGui.GetWindowDrawList();
        var t   = Animate(itemId, value, 22f);
        var hot = Animate(itemId ^ 0x33u, hovered, 24f);
        var on  = color ?? Theme.P.Accent;

        var (min, max) = Pressed(itemId, ImGuiNET.ImGui.IsItemActive(), pos, pos + new Vector2(CheckboxSize, CheckboxSize));
        var off = Theme.Mix(Theme.IsLight ? Theme.RaisedFill : Theme.P.Deep, Theme.RaisedHot, hot * 0.6f);
        dl.AddRectFilled(min, max, Theme.ToU32(Theme.Mix(off, on, t)), CheckboxRound);
        if (t < 0.99f)
        {
            var line = Theme.Mix(Theme.Hairline, Theme.HairlineHot, hot);
            dl.AddRect(min, max, Theme.ToU32(Theme.WithAlpha(line, line.W * (1f - t))), CheckboxRound, ImDrawFlags.None, 1f / UiScale.K);
        }

        if (t > 0.01f)
        {
            // The stroke grows along its path: first the short leg, then the long one.
            var c  = (min + max) * 0.5f;
            var u  = CheckboxSize / 22f;
            Span<Vector2> pts = [c + new Vector2(-5.5f, 0.5f) * u, c + new Vector2(-1.5f, 4.5f) * u, c + new Vector2(6f, -4.5f) * u];
            var ink = Theme.ToU32(Theme.WithAlpha(Theme.Ink, MathF.Min(1f, t * 1.5f)));
            var l1 = Vector2.Distance(pts[0], pts[1]);
            var l2 = Vector2.Distance(pts[1], pts[2]);
            var len = t * (l1 + l2);
            if (len <= l1)
                dl.AddLine(pts[0], Vector2.Lerp(pts[0], pts[1], len / l1), ink, 2.5f);
            else
            {
                Span<Vector2> path = [pts[0], pts[1], Vector2.Lerp(pts[1], pts[2], MathF.Min(1f, (len - l1) / l2))];
                dl.AddPolyline(ref MemoryMarshal.GetReference(path), path.Length, ink, ImDrawFlags.None, 2.5f);
            }
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
