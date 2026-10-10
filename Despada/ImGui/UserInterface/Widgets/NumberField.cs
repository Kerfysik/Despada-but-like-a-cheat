// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Globalization;
using System.Numerics;
using Despada.ImGui.Hook;
using ImGuiNET;

namespace Despada.ImGui.UserInterface;

public static partial class Widgets
{
    public const float NumberH = 36f;
    private const float NumberPadX = 4f;
    private const float NumberArrowW = 16f;
    private const float DragStart = 3f;   // px before a press becomes a scrub

    private struct NumberState
    {
        public bool   Editing;
        public string? Buf;
        public float  DragX, DragV;
        public bool   Dragged;
        public int    EditStartFrame;
        public int    LastFrame;
    }

    private static readonly StateStore<NumberState> _numbers = new();

    /// <summary>
    /// A number in a Deep hairline box: an optional muted label inside at the left, the value at
    /// the right, an optional unit after it. Hover shows ‹ › at the edges; dragging left/right
    /// scrubs (<paramref name="step"/> per 12 px), the wheel steps, a click types (ImGui's text
    /// field inside our frame, Enter or blur commits). Returns true when the value changed.
    /// </summary>
    public static bool NumberField(string id, ref float value, float min, float max, float step, string? label = null,
        string? unit = null, float width = 150f, int decimals = 0, float layoutW = 0f)
    {
        var pos   = ImGuiNET.ImGui.GetCursorScreenPos();
        var size  = new Vector2(width, NumberH);
        // The item (hit area and layout) can be narrower than the drawing while it slides open.
        var item  = new Vector2(layoutW > 0f ? MathF.Min(layoutW, width) : width, NumberH);
        var dl    = ImGuiNET.ImGui.GetWindowDrawList();
        var lineH = ImGuiNET.ImGui.GetTextLineHeight();
        var io    = ImGuiNET.ImGui.GetIO();
        var changed = false;

        ImGuiNET.ImGui.PushID(id);
        var itemId = ImGuiNET.ImGui.GetID("##num");
        ref var st = ref _numbers.Get(itemId, default);
        var frame = ImGuiNET.ImGui.GetFrameCount();
        // Not drawn last frame while editing (a tab switched away): the edit is dropped, not committed.
        if (st.Editing && st.LastFrame < frame - 1) { st.Editing = false; st.Buf = null; }
        st.LastFrame = frame;
        var editing = st.Editing;

        bool hovered = false, active = false;
        if (!editing)
        {
            ImGuiNET.ImGui.InvisibleButton("##num", item);
            ImGuiNET.ImGui.SetItemKeyOwner(ImGuiKey.MouseWheelY);   // the wheel steps the value, not the page
            hovered = ImGuiNET.ImGui.IsItemHovered();
            active  = ImGuiNET.ImGui.IsItemActive();
            if (ImGuiNET.ImGui.IsItemActivated()) { st.DragX = io.MousePos.X; st.DragV = value; st.Dragged = false; }
            if (active)
            {
                var dx = io.MousePos.X - st.DragX;
                if (!st.Dragged && MathF.Abs(dx) >= DragStart) st.Dragged = true;
                if (st.Dragged)
                {
                    var v = Math.Clamp(st.DragV + MathF.Round(dx / 12f) * step, min, max);
                    if (v != value) { value = v; changed = true; }
                }
            }
            if (ImGuiNET.ImGui.IsItemDeactivated() && !st.Dragged && hovered)
            {
                // A click: type the value.
                st.Editing = true;
                st.Buf = value.ToString("F" + decimals, CultureInfo.InvariantCulture);
                st.EditStartFrame = ImGuiNET.ImGui.GetFrameCount();
                editing = true;
            }
            if (hovered && io.MouseWheel != 0f)
            {
                var v = Math.Clamp(value + MathF.Sign(io.MouseWheel) * step, min, max);
                if (v != value) { value = v; changed = true; }
            }
        }

        var hot   = Animate(itemId ^ 0x33u, hovered || active, 24f);
        var focus = Animate(itemId ^ 0x55u, editing, 24f);
        var min0  = UiScale.Snap(pos);
        var max0  = UiScale.Snap(pos + size);
        dl.AddRectFilled(min0, max0, Theme.ToU32(Theme.P.Deep), ButtonRound);
        dl.AddRect(min0, max0, Theme.ToU32(Theme.Mix(Theme.Hairline, Theme.HairlineHot, hot)), ButtonRound, ImDrawFlags.None, 1f / UiScale.K);
        Gfx.FocusRing(dl, min0, max0, ButtonRound, focus);

        var cy = pos.Y + size.Y * 0.5f;
        var textY = cy - lineH * 0.5f;
        // ‹ › appear on hover, never while typing.
        var arrowA = hot * (1f - focus);
        if (arrowA > 0.01f)
        {
            var ac = Theme.ToU32(Theme.WithAlpha(Theme.P.Sub, arrowA));
            dl.AddText(new Vector2(pos.X + NumberPadX + 2f, textY), ac, "‹");
            dl.AddText(new Vector2(pos.X + size.X - NumberPadX - NumberArrowW + 4f, textY), ac, "›");
        }
        var left = pos.X + NumberPadX + NumberArrowW;
        if (label is not null)
        {
            dl.AddText(new Vector2(left, textY), Theme.ToU32(Theme.P.Muted), label);
            left += ImGuiNET.ImGui.CalcTextSize(label).X + 8f;
        }
        var right = pos.X + size.X - NumberPadX - NumberArrowW;
        if (unit is not null)
        {
            var uw = ImGuiNET.ImGui.CalcTextSize(unit).X;
            dl.AddText(new Vector2(right - uw, textY), Theme.ToU32(Theme.P.Muted), unit);
            right -= uw + 6f;
        }

        if (editing)
        {
            // The field takes the keyboard a frame before ImGui's text field becomes active, so a
            // digit typed right after the click doesn't reach the game.
            ImGuiInputHook.ClaimKeyboard();
            // ImGui's own text field, frameless, over the value slot; Enter, Escape or losing
            // focus ends it. Decimal characters only.
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(left, pos.Y + (size.Y - ImGuiNET.ImGui.GetFrameHeight()) * 0.5f));
            ImGuiNET.ImGui.SetNextItemWidth(MathF.Max(40f, right - left));
            if (frame == st.EditStartFrame) ImGuiNET.ImGui.SetKeyboardFocusHere();
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
            var buf = st.Buf ?? "";
            var enter = ImGuiNET.ImGui.InputText("##edit", ref buf, 32,
                ImGuiInputTextFlags.CharsDecimal | ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
            ImGuiNET.ImGui.PopStyleColor(3);
            st.Buf = buf;
            var gone = ImGuiNET.ImGui.GetFrameCount() > st.EditStartFrame + 2 && !ImGuiNET.ImGui.IsItemActive();
            if (enter || gone || ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.Escape, false))
            {
                if (!ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.Escape, false)
                    && float.TryParse(buf, NumberStyles.Float, CultureInfo.InvariantCulture, out var typed))
                {
                    var v = Math.Clamp(typed, min, max);
                    if (v != value) { value = v; changed = true; }
                }
                st.Editing = false;
                st.Buf = null;
            }
        }
        else
        {
            var text = value.ToString("F" + decimals, CultureInfo.InvariantCulture);
            var tw = ImGuiNET.ImGui.CalcTextSize(text).X;
            dl.AddText(new Vector2(right - tw, textY), Theme.ToU32(Theme.P.Fg), text);
        }
        ImGuiNET.ImGui.PopID();

        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        ImGuiNET.ImGui.Dummy(item);
        return changed;
    }

    /// <summary>Integer form of <see cref="NumberField(string, ref float, float, float, float, string?, string?, float, int)"/>.</summary>
    public static bool NumberField(string id, ref int value, int min, int max, int step = 1, string? label = null, string? unit = null, float width = 150f, float layoutW = 0f)
    {
        float f = value;
        if (!NumberField(id, ref f, min, max, step, label, unit, width, 0, layoutW)) return false;
        value = (int)MathF.Round(f);
        return true;
    }

    /// <summary>
    /// The repeat control (form C): one hairline box with two halves, "× N" and "every ms", the
    /// second half sliding open only once N is 2 or more. Each half scrubs, wheels and types.
    /// </summary>
    public static bool Repeat(string id, ref int times, ref int everyMs)
    {
        var pos = ImGuiNET.ImGui.GetCursorScreenPos();
        ImGuiNET.ImGui.PushID(id);
        var baseId = ImGuiNET.ImGui.GetID("##rep");
        var changed = NumberField("##times", ref times, 1, 99, 1, "×", null, 104f);
        var open = Animate(baseId, times >= 2, 18f);
        var msW = 130f * open;
        if (open > 0.01f)
        {
            ImGuiNET.ImGui.SameLine(0f, 0f);
            var at = ImGuiNET.ImGui.GetCursorScreenPos();
            // While sliding: the window clip rect, so drawing AND hit-testing stop at the open
            // width (a draw-list clip would leave the hidden part clickable). A margin for the
            // focus glow.
            var sliding = open < 0.999f;
            if (sliding) ImGuiNET.ImGui.PushClipRect(at - new Vector2(4f, 4f), new Vector2(at.X + msW, at.Y + NumberH + 4f), true);
            changed |= NumberField("##every", ref everyMs, 1, 5000, 10, null, "ms", 130f, sliding ? MathF.Max(1f, msW) : 0f);
            if (sliding) ImGuiNET.ImGui.PopClipRect();
        }
        ImGuiNET.ImGui.PopID();
        return changed;
    }
}
