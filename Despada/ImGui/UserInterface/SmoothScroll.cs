// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.InteropServices;
using ImGuiNET;

namespace Despada.ImGui.UserInterface;

internal static class SmoothScroll
{
    private const float Speed = 20f; // 1/s, see Anim.Lerp

    private struct State
    {
        public float Target, Applied;
        public bool  Active;
    }

    private static readonly Dictionary<uint, State> _states = new();
    
    public static float Update(float shift = 0f)
    {
        var io  = ImGuiNET.ImGui.GetIO();
        var id  = ImGuiNET.ImGui.GetID("##smooth-scroll");
        ref var s = ref CollectionsMarshal.GetValueRefOrAddDefault(_states, id, out _);

        var cur = ImGuiNET.ImGui.GetScrollY();
        var max = ImGuiNET.ImGui.GetScrollMaxY();

        // Moved by someone else since our last SetScrollY (ImGui rounds scroll to whole units).
        if (s.Active && MathF.Abs(cur - s.Applied) > 1f)
            s.Active = false;

        // Animate from our own unrounded position: re-reading ImGui's rounded one would stall once
        // a frame's step drops under half a unit (easily at 240 Hz).
        var pos = s.Active ? s.Applied : cur;
        if (shift != 0f)
        {
            pos       = Math.Clamp(pos + shift, 0f, max);
            s.Target += shift;
        }

        // The console input stays active all the time, which plain IsWindowHovered() refuses.
        if (ImGuiNET.ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem) && !io.KeyCtrl)
        {
            // As ImGui's own wheel handling: Shift turns the vertical wheel horizontal.
            float wheelY = io.MouseWheel, wheelX = io.MouseWheelH;
            if (io.KeyShift && wheelX == 0f)
            {
                wheelX = wheelY;
                wheelY = 0f;
            }

            var fontSize = ImGuiNET.ImGui.GetFontSize();
            var winSize  = ImGuiNET.ImGui.GetWindowSize();
            if (wheelY != 0f && max > 0f)
            {
                if (!s.Active) s.Target = pos;
                s.Target = Math.Clamp(s.Target - wheelY * MathF.Floor(MathF.Min(5f * fontSize, winSize.Y * 0.67f)), 0f, max);
                s.Active = true;
            }
            if (wheelX != 0f)
            {
                var step = MathF.Floor(MathF.Min(2f * fontSize, winSize.X * 0.67f));
                ImGuiNET.ImGui.SetScrollX(Math.Clamp(ImGuiNET.ImGui.GetScrollX() - wheelX * step, 0f, ImGuiNET.ImGui.GetScrollMaxX()));
            }
        }

        if (!s.Active)
        {
            if (shift != 0f) ImGuiNET.ImGui.SetScrollY(pos);
            return pos;
        }

        s.Target = Math.Clamp(s.Target, 0f, max);
        var next = Anim.Lerp(pos, s.Target, Speed, io.DeltaTime);
        if (MathF.Abs(next - s.Target) < 0.5f)
        {
            next     = s.Target;
            s.Active = false;
        }
        ImGuiNET.ImGui.SetScrollY(next);
        s.Applied = next;
        return s.Active ? s.Target : next;
    }
}
