// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using ImGuiNET;

namespace Despada.ImGui.UserInterface;

public static partial class Widgets
{
    private static readonly StateStore<int> _comboPage = new();
    private static readonly Dictionary<string, string[]> _comboItemsCache = new();

    public static bool Combo(string label, ref int current, string items)
    {
        if (!_comboItemsCache.TryGetValue(items, out var parsed))
        {
            parsed = items.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            _comboItemsCache[items] = parsed;
        }
        return Combo(label, ref current, parsed);
    }

    public static bool Combo(string label, ref int current, string[] items)
    {
        const float boxR     = 6f;
        const float pad      = 14f;
        const float stripR   = 6f;
        const float closeW   = 38f;
        const float arrowW   = 28f;
        const float itemMin  = 72f;
        const float sepInset = 8f;
        const int   maxVis   = 4;

        ImGuiNET.ImGui.Spacing();

        var dl   = ImGuiNET.ImGui.GetWindowDrawList();
        var font = ImGuiNET.ImGui.GetFont();
        // Current font size (includes io.FontGlobalScale), not the atlas size: they differ whenever
        // the UI factor K isn't exactly 1, and the text then didn't match the box.
        var fs   = ImGuiNET.ImGui.GetFontSize();

        // Box and strip never smaller than a text line; the box grows to fit the longest item.
        float longest = 0f;
        foreach (var it in items)
            longest = MathF.Max(longest, font.CalcTextSizeA(fs, float.MaxValue, 0f, it).X);
        var chevronW = font.CalcTextSizeA(fs, float.MaxValue, 0f, ">").X;
        var boxH   = MathF.Max(34f, ImGuiNET.ImGui.GetFrameHeight());
        var boxW   = MathF.Max(160f, longest + chevronW + pad * 3f);
        var stripH = MathF.Max(36f, boxH + 2f);
        var cw   = ImGuiNET.ImGui.GetContentRegionAvail().X;
        var lineX = ImGuiNET.ImGui.GetCursorPosX(); // incl. indent; SameLine offsets count from the window edge
        var pos  = ImGuiNET.ImGui.GetCursorScreenPos();
        var changed = false;

        var dLabel = DisplayLabel(label);

        ImGuiNET.ImGui.PushID(label);
        var pageId  = ImGuiNET.ImGui.GetID("##page");
        bool isOpen = ImGuiNET.ImGui.IsPopupOpen("##combo_popup");
        var openT   = Animate(ImGuiNET.ImGui.GetID("##open"), isOpen);

        if (!string.IsNullOrEmpty(dLabel))
        {
            ImGuiNET.ImGui.AlignTextToFramePadding();
            ImGuiNET.ImGui.TextUnformatted(dLabel);
        }

        ImGuiNET.ImGui.SameLine(lineX + cw - boxW);
        pos = ImGuiNET.ImGui.GetCursorScreenPos();

        var bMin = pos;
        var bMax = new Vector2(pos.X + boxW, pos.Y + boxH);

        bool bPressed = ImGuiNET.ImGui.InvisibleButton("##box", new Vector2(boxW, boxH));
        bool bH = ImGuiNET.ImGui.IsItemHovered();
        if (bPressed && !isOpen)
        {
            ImGuiNET.ImGui.OpenPopup("##combo_popup");
            _comboPage.Get(pageId, 0) = current >= maxVis ? current - maxVis + 1 : 0;
        }

        dl.AddRectFilled(bMin, bMax, Theme.ToU32(Theme.P.Deep), boxR);

        var baseBdr = bH ? Theme.P.Line : Theme.P.Sep;
        var bdrCol  = Theme.Mix(baseBdr, Theme.WithAlpha(Theme.P.Accent, 0.6f), openT);
        dl.AddRect(bMin, bMax, Theme.ToU32(bdrCol), boxR, ImDrawFlags.None, 1f);

        var vt = current >= 0 && current < items.Length ? items[current] : "";
        var vs = font.CalcTextSizeA(fs, float.MaxValue, 0f, vt);
        dl.AddText(font, fs,
            new Vector2(bMin.X + pad, bMin.Y + (boxH - vs.Y) * 0.5f),
            Theme.ToU32(Theme.P.Fg), vt);

        var chev = isOpen ? "<" : ">";
        var chSz = font.CalcTextSizeA(fs, float.MaxValue, 0f, chev);
        dl.AddText(font, fs,
            new Vector2(bMax.X - pad - chSz.X, bMin.Y + (boxH - chSz.Y) * 0.5f),
            Theme.ToU32(isOpen ? Theme.P.Fg : Theme.P.Muted), chev);

        bool needPaging = items.Length > maxVis;
        int pageOff = _comboPage.Get(pageId, 0);
        int maxOff = Math.Max(0, items.Length - maxVis);
        pageOff = Math.Clamp(pageOff, 0, maxOff);
        int visN = Math.Min(items.Length, maxVis);

        float maxTW = 0f;
        for (int i = 0; i < items.Length; i++)
        {
            var tw = font.CalcTextSizeA(fs, float.MaxValue, 0f, items[i]).X;
            if (tw > maxTW) maxTW = tw;
        }

        var iW     = MathF.Max(maxTW + 28f, itemMin);
        var totalW = closeW + iW * visN + (needPaging ? arrowW * 2f : 0f);
        var sepCol = Theme.ToU32(Theme.Fade(Theme.P.Sep, 0.5f));
        var hovCol = Theme.Mix(Theme.P.Raised, Theme.P.Sep, 0.5f);

        ImGuiNET.ImGui.SetNextWindowPos(new Vector2(bMin.X, bMax.Y + 1f));
        ImGuiNET.ImGui.SetNextWindowSize(new Vector2(totalW, stripH));

        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, stripR);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, Theme.P.Pop);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Theme.P.Sep);

        if (ImGuiNET.ImGui.BeginPopup("##combo_popup",
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize))
        {
            var p  = ImGuiNET.ImGui.GetWindowDrawList();
            var wP = ImGuiNET.ImGui.GetWindowPos();

            float curX = wP.X;

            var xMin = new Vector2(curX, wP.Y);
            var xMax = new Vector2(curX + closeW, wP.Y + stripH);
            bool xPressed = ButtonAt("##close", xMin, xMax - xMin, out var xHov);
            if (xHov)
                p.AddRectFilled(xMin, xMax, Theme.ToU32(hovCol), stripR, ImDrawFlags.RoundCornersLeft);

            var xTs = font.CalcTextSizeA(fs, float.MaxValue, 0f, "X");
            p.AddText(font, fs,
                new Vector2(xMin.X + (closeW - xTs.X) * 0.5f, xMin.Y + (stripH - xTs.Y) * 0.5f),
                Theme.ToU32(xHov ? Theme.P.Fg : Theme.P.Muted), "X");

            if (xPressed)
                ImGuiNET.ImGui.CloseCurrentPopup();

            p.AddLine(new Vector2(xMax.X, wP.Y + sepInset), new Vector2(xMax.X, wP.Y + stripH - sepInset), sepCol);
            curX += closeW;

            if (needPaging)
            {
                var aMin = new Vector2(curX, wP.Y);
                var aMax = new Vector2(curX + arrowW, wP.Y + stripH);
                bool aPressed = ButtonAt("##prev", aMin, aMax - aMin, out var aH);
                bool canL = pageOff > 0;

                if (aH && canL)
                    p.AddRectFilled(aMin, aMax, Theme.ToU32(hovCol));

                var aTs = font.CalcTextSizeA(fs, float.MaxValue, 0f, "<");
                p.AddText(font, fs,
                    new Vector2(aMin.X + (arrowW - aTs.X) * 0.5f, aMin.Y + (stripH - aTs.Y) * 0.5f),
                    Theme.ToU32(canL ? Theme.P.Muted : Theme.WithAlpha(Theme.P.Muted, 0.4f)), "<");

                if (aPressed && canL)
                    _comboPage.Get(pageId, 0) = --pageOff;

                p.AddLine(new Vector2(aMax.X, wP.Y + sepInset), new Vector2(aMax.X, wP.Y + stripH - sepInset), sepCol);
                curX += arrowW;
            }

            for (int vi = 0; vi < visN; vi++)
            {
                int i = vi + pageOff;
                if (i >= items.Length) break;

                var oMin = new Vector2(curX, wP.Y);
                var oMax = new Vector2(curX + iW, wP.Y + stripH);
                ImGuiNET.ImGui.PushID(i);
                bool oPressed = ButtonAt("##opt", oMin, oMax - oMin, out var oHov);
                ImGuiNET.ImGui.PopID();
                bool sel  = i == current;

                // Only the last cell touches the strip's rounded right edge.
                var lastCell = vi == visN - 1 && !needPaging;
                var cr = lastCell ? stripR : 0f;
                var cf = lastCell ? ImDrawFlags.RoundCornersRight : ImDrawFlags.None;

                if (sel)
                {
                    var start = p.VtxBuffer.Size;
                    p.AddRectFilled(oMin, oMax, Theme.ToU32(Theme.Sel), cr, cf);
                    Gfx.Shade(p, start, p.VtxBuffer.Size, oMin, new Vector2(oMax.X, oMin.Y),
                        Theme.ToU32(Theme.Sel), Theme.ToU32(Theme.SelR));
                }
                else if (oHov)
                {
                    p.AddRectFilled(oMin, oMax, Theme.ToU32(hovCol), cr, cf);
                }

                var ts = font.CalcTextSizeA(fs, float.MaxValue, 0f, items[i]);
                p.AddText(font, fs,
                    new Vector2(oMin.X + (iW - ts.X) * 0.5f, oMin.Y + (stripH - ts.Y) * 0.5f),
                    Theme.ToU32(sel ? Theme.P.Accent : Theme.P.Fg), items[i]);

                if (vi < visN - 1)
                    p.AddLine(new Vector2(oMax.X, wP.Y + sepInset), new Vector2(oMax.X, wP.Y + stripH - sepInset), sepCol);

                if (oPressed)
                {
                    current = i;
                    changed = true;
                    ImGuiNET.ImGui.CloseCurrentPopup();
                }

                curX += iW;
            }

            if (needPaging)
            {
                p.AddLine(new Vector2(curX, wP.Y + sepInset), new Vector2(curX, wP.Y + stripH - sepInset), sepCol);

                var aMin = new Vector2(curX, wP.Y);
                var aMax = new Vector2(curX + arrowW, wP.Y + stripH);
                bool aPressed = ButtonAt("##next", aMin, aMax - aMin, out var aH);
                bool canR = pageOff < maxOff;

                if (aH && canR)
                    p.AddRectFilled(aMin, aMax, Theme.ToU32(hovCol), stripR, ImDrawFlags.RoundCornersRight);

                var aTs = font.CalcTextSizeA(fs, float.MaxValue, 0f, ">");
                p.AddText(font, fs,
                    new Vector2(aMin.X + (arrowW - aTs.X) * 0.5f, aMin.Y + (stripH - aTs.Y) * 0.5f),
                    Theme.ToU32(canR ? Theme.P.Muted : Theme.WithAlpha(Theme.P.Muted, 0.4f)), ">");

                if (aPressed && canR)
                    _comboPage.Get(pageId, 0) = ++pageOff;
            }

            // Items above were placed absolutely; reserve the strip as one block.
            ImGuiNET.ImGui.SetCursorScreenPos(wP);
            ImGuiNET.ImGui.Dummy(new Vector2(totalW, stripH));

            // The popup border is drawn in Begin; the full-height cell fills above cover it.
            p.AddRect(wP, new Vector2(wP.X + totalW, wP.Y + stripH), Theme.ToU32(Theme.P.Sep), stripR);

            ImGuiNET.ImGui.EndPopup();
        }

        ImGuiNET.ImGui.PopStyleColor(2);
        ImGuiNET.ImGui.PopStyleVar(3);
        ImGuiNET.ImGui.PopID();

        ImGuiNET.ImGui.Spacing();
        return changed;
    }
}