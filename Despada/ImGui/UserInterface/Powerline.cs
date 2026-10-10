// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace Despada.ImGui.UserInterface;

/// <summary>
/// Tabs drawn as a powerline: segments with an arrow on the right, each one tucked into the notch
/// of the next, the first with rounded left corners. The active segment is filled with the
/// accent → mauve gradient and dark text; the others are raised with muted text. Used for console
/// sessions and open network packets.
/// </summary>
internal static class Powerline
{
    private const float D = UiScale.DesignHeight / 1080f;

    private const float Arrow     = 10f * D;  // depth of the arrow / notch
    private const float Overlap   = 7f * D;   // how far a segment slides under the previous arrow
    private const float PadFirst  = 12f * D;
    private const float PadLeft   = 19f * D;  // after a notch
    private const float PadRight  = 20f * D;  // before the arrow

    /// <summary>
    /// One segment at the cursor. <paramref name="first"/> starts a chain; the next segment is placed
    /// with <see cref="Next"/>. Returns true on a plain click; a middle click or Ctrl+click reports
    /// <paramref name="closeRequested"/> instead. <paramref name="mark"/> adds a small dot after the
    /// label (new output in an inactive session); it sits in the right padding, so a tab keeps its
    /// width when the dot comes or goes.
    /// </summary>
    public static bool Segment(string id, string label, bool active, bool first, bool mark, out bool closeRequested,
        Vector4? tone = null)
    {
        closeRequested = false;

        var h      = ImGuiNET.ImGui.GetFrameHeight();
        var textW  = ImGuiNET.ImGui.CalcTextSize(label).X;
        var padL   = first ? PadFirst : PadLeft;
        var w      = padL + textW + PadRight;
        var cursor = ImGuiNET.ImGui.GetCursorScreenPos();

        // A segment after the first is drawn Overlap further left, tucked under the previous arrow,
        // but its button starts at the cursor (the previous arrow's tip): buttons never overlap, so
        // hover, clicks and middle clicks always go to the segment that's visibly under the mouse.
        var pos = first ? cursor : cursor - new Vector2(Overlap, 0f);
        var clicked = ImGuiNET.ImGui.InvisibleButton(id, new Vector2(first ? w : w - Overlap, h));
        var hovered = ImGuiNET.ImGui.IsItemHovered();
        var middle  = ImGuiNET.ImGui.IsItemClicked(ImGuiMouseButton.Middle);
        if (middle || (clicked && ImGuiNET.ImGui.GetIO().KeyCtrl))
        {
            closeRequested = true;
            clicked = false;
        }

        var dl    = ImGuiNET.ImGui.GetWindowDrawList();
        var start = dl.VtxBuffer.Size;
        var fill  = active ? Theme.P.Accent
                  : hovered ? Theme.Mix(Theme.P.Raised, Theme.P.Sep, 0.5f)
                  : Theme.P.Raised;
        Shape(dl, pos, w, h, first, Theme.ToU32(fill));
        if (active)
            Gfx.Shade(dl, start, dl.VtxBuffer.Size, pos, pos + new Vector2(w, 0f),
                Theme.ToU32(Theme.P.Accent), Theme.ToU32(Theme.P.Accent2));

        var text = active ? new Vector4(Theme.P.Glass, 1f) : hovered ? Theme.P.Fg : tone ?? Theme.P.Sub;
        var ty   = pos.Y + (h - ImGuiNET.ImGui.GetFontSize()) * 0.5f;
        dl.AddText(new Vector2(pos.X + padL, ty), Theme.ToU32(text), label);
        if (mark)
            dl.AddCircleFilled(new Vector2(pos.X + padL + textW + 8f * D, pos.Y + h * 0.5f), 3f * D,
                Theme.ToU32(Theme.P.Accent2));

        return clicked;
    }

    /// <summary>Places the cursor for the next segment of the chain (right at the previous arrow's tip).</summary>
    // Not SameLine(0, -Overlap): ImGui reads any negative spacing as "the default item spacing".
    public static void Next() => ImGuiNET.ImGui.SameLine(0f, 0f);

    /// <summary>Places the cursor after the chain's last arrow, for what follows it.</summary>
    public static void After(float spacing) => ImGuiNET.ImGui.SameLine(0f, spacing);

    /// <summary>How far a segment moves the cursor (its button width).</summary>
    public static float Advance(string label, bool first)
    {
        var w = (first ? PadFirst : PadLeft) + ImGuiNET.ImGui.CalcTextSize(label).X + PadRight;
        return first ? w : w - Overlap;
    }

    /// <summary>
    /// Fits a chain into <paramref name="avail"/>: first every label but the active one is shortened
    /// (down to a couple of characters and "…"); if that is still too wide, only a run of segments
    /// around the active one is kept (<paramref name="from"/>..<paramref name="to"/>) and room is
    /// left for an <see cref="Overflow"/> segment after it.
    /// </summary>
    public static string[] Fit(IReadOnlyList<string> labels, int active, float avail, out int from, out int to)
    {
        var n = labels.Count;
        from = 0;
        to = n - 1;
        var result = labels.ToArray();
        if (n == 0 || Width(result, 0, n - 1) <= avail) return result;

        var longest = labels.Max(l => l.Length);
        for (var len = longest - 1; len >= 3; len--)
        {
            for (var i = 0; i < n; i++)
                result[i] = i == active || labels[i].Length <= len ? labels[i] : labels[i][..(len - 1)] + "…";
            if (Width(result, 0, n - 1) <= avail) return result;
        }

        // Still too wide: the active segment and as many neighbours as fit, then "…"; the active
        // label itself is shortened if it alone is wider than the room.
        var room = avail - Advance("…", false);
        if (active >= 0 && active < n)
            for (var len = labels[active].Length - 1; len >= 3 && Advance(result[active], true) > room; len--)
                result[active] = labels[active][..(len - 1)] + "…";
        from = to = Math.Clamp(active, 0, n - 1);
        for (var grew = true; grew;)
        {
            grew = false;
            if (to + 1 < n && Width(result, from, to + 1) <= room) { to++; grew = true; }
            if (from > 0 && Width(result, from - 1, to) <= room) { from--; grew = true; }
        }
        return result;
    }

    private static float Width(string[] labels, int from, int to)
    {
        var w = 0f;
        for (var i = from; i <= to; i++) w += Advance(labels[i], i == from);
        return w;
    }

    /// <summary>
    /// The "…" segment ending a chain that didn't fit: a click lists every item. Returns the index
    /// picked from <paramref name="items"/>, or -1.
    /// </summary>
    public static int Overflow(string id, IReadOnlyList<string> items, int active)
    {
        Next();
        if (Segment(id, "…", false, false, false, out _))
            ImGuiNET.ImGui.OpenPopup(id + "-list");
        var picked = -1;
        if (ImGuiNET.ImGui.BeginPopup(id + "-list"))
        {
            for (var i = 0; i < items.Count; i++)
            {
                ImGuiNET.ImGui.PushID(i);   // names may repeat or hold "##"
                if (ImGuiNET.ImGui.MenuItem(items[i].Replace("##", "#\u200B#"), null, i == active)) picked = i;
                ImGuiNET.ImGui.PopID();
            }
            ImGuiNET.ImGui.EndPopup();
        }
        return picked;
    }

    /// <summary>
    /// The shape of an editable segment (a rename field) at the cursor, <paramref name="textW"/> wide
    /// inside. Returns where the text field goes; after it, <see cref="EndField"/> moves the cursor to
    /// the segment's arrow so the chain continues with <see cref="Next"/>.
    /// </summary>
    public static Vector2 BeginField(float textW, bool first, out float endX)
    {
        var h      = ImGuiNET.ImGui.GetFrameHeight();
        var padL   = first ? PadFirst : PadLeft;
        var w      = padL + textW + PadRight;
        var cursor = ImGuiNET.ImGui.GetCursorScreenPos();
        var pos    = first ? cursor : cursor - new Vector2(Overlap, 0f);
        Shape(ImGuiNET.ImGui.GetWindowDrawList(), pos, w, h, first, Theme.ToU32(Theme.Mix(Theme.P.Raised, Theme.P.Sep, 0.5f)));
        endX = pos.X + w;
        return new Vector2(pos.X + padL, cursor.Y);
    }

    public static void EndField(float endX, float y)
    {
        ImGuiNET.ImGui.SameLine(0f, 0f);
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(endX, y));
        ImGuiNET.ImGui.Dummy(new Vector2(0f, ImGuiNET.ImGui.GetFrameHeight()));
    }

    /// <summary>A bare "+" after the chain (new session).</summary>
    public static bool Plus(string id)
    {
        var h   = ImGuiNET.ImGui.GetFrameHeight();
        var w   = ImGuiNET.ImGui.CalcTextSize("+").X + 12f * D;
        var pos = ImGuiNET.ImGui.GetCursorScreenPos();
        var clicked = ImGuiNET.ImGui.InvisibleButton(id, new Vector2(w, h));
        var color   = ImGuiNET.ImGui.IsItemHovered() ? Theme.P.Fg : Theme.P.Muted;
        ImGuiNET.ImGui.GetWindowDrawList().AddText(
            new Vector2(pos.X + 6f * D, pos.Y + (h - ImGuiNET.ImGui.GetFontSize()) * 0.5f), Theme.ToU32(color), "+");
        return clicked;
    }

    // Rect with an arrow on the right; a notch on the left unless it's the first (rounded corners).
    private static unsafe void Shape(ImDrawListPtr dl, Vector2 p, float w, float h, bool first, uint col)
    {
        var x0 = p.X;
        var x1 = p.X + w;
        var y0 = p.Y;
        var y1 = p.Y + h;
        var ym = p.Y + h * 0.5f;

        if (first)
        {
            var r = MathF.Min(Theme.SmallRounding, h * 0.5f);
            dl.PathClear();
            dl.PathArcToFast(new Vector2(x0 + r, y0 + r), r, 6, 9);   // top-left corner
            dl.PathLineTo(new Vector2(x1 - Arrow, y0));
            dl.PathLineTo(new Vector2(x1, ym));
            dl.PathLineTo(new Vector2(x1 - Arrow, y1));
            dl.PathArcToFast(new Vector2(x0 + r, y1 - r), r, 3, 6);   // bottom-left corner
            dl.PathFillConvex(col);
            return;
        }

        Span<Vector2> pts =
        [
            new(x0, y0), new(x1 - Arrow, y0), new(x1, ym),
            new(x1 - Arrow, y1), new(x0, y1), new(x0 + Arrow, ym),
        ];
        dl.AddConcavePolyFilled(ref MemoryMarshal.GetReference(pts), pts.Length, col);
    }
}
