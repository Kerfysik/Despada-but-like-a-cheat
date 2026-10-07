// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using ImGuiNET;

namespace Despada.ImGui.UserInterface;

public static partial class Widgets
{
    public static void BeginSection(string title)
    {
        var availW = ImGuiNET.ImGui.GetContentRegionAvail().X;

        ImGuiNET.ImGui.PushID(title);
        PushSectionStyle();

        ImGuiNET.ImGui.BeginChild("##section", new Vector2(availW, 0f),
            ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.AlwaysUseWindowPadding,
            ImGuiWindowFlags.NoScrollbar);

        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Theme.P.Muted);
        ImGuiNET.ImGui.TextUnformatted(title);
        ImGuiNET.ImGui.PopStyleColor();
        ImGuiNET.ImGui.Spacing();
    }

    public static void EndSection()
    {
        ImGuiNET.ImGui.EndChild();
        PopSectionStyle();
        ImGuiNET.ImGui.PopID();
        ImGuiNET.ImGui.Dummy(new Vector2(0f, SectionGap));
    }

    /// <summary>
    /// Section with a title, description and an on/off toggle in the header.
    /// Always pair with <see cref="EndFeatureSection"/>, whatever this returns.
    /// </summary>
    public static bool BeginFeatureSection(string icon, string title, string description, ref bool enabled)
    {
        var availW = ImGuiNET.ImGui.GetContentRegionAvail().X;

        ImGuiNET.ImGui.PushID(title);
        PushSectionStyle();

        ImGuiNET.ImGui.BeginChild("##feature", new Vector2(availW, 0f),
            ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.AlwaysUseWindowPadding,
            ImGuiWindowFlags.NoScrollbar);

        var dl       = ImGuiNET.ImGui.GetWindowDrawList();
        var font     = ImGuiNET.ImGui.GetFont();
        var contentW = ImGuiNET.ImGui.GetContentRegionAvail().X;
        var origin   = ImGuiNET.ImGui.GetCursorScreenPos();

        var hL = origin.X + HeaderPad;
        var hR = origin.X + contentW - HeaderPad;

        var tMin = new Vector2(hR - ToggleW, origin.Y + (HeaderH - ToggleH) * 0.5f);
        if (ButtonAt("##ft", tMin, new Vector2(ToggleW, ToggleH), out var tHov))
            enabled = !enabled;
        var toggleId = ImGuiNET.ImGui.GetItemID();

        // Reserve the whole header; the toggle above sits inside it.
        ImGuiNET.ImGui.SetCursorScreenPos(origin);
        ImGuiNET.ImGui.Dummy(new Vector2(contentW, HeaderH));

        var animT = Animate(toggleId, enabled);

        var iconFs = ImGuiNET.ImGui.GetFontSize() * 2.0f;
        var iconSz = font.CalcTextSizeA(iconFs, float.MaxValue, 0f, icon);
        dl.AddText(font, iconFs,
            new Vector2(hL, origin.Y + (HeaderH - iconSz.Y) * 0.5f),
            Theme.ToU32(Theme.Mix(Theme.P.Muted, Theme.P.Accent, animT)), icon);

        var textX   = hL + iconSz.X + 14f;
        var titleFs = ImGuiNET.ImGui.GetFontSize() * 1.1f;
        var titleSz = font.CalcTextSizeA(titleFs, float.MaxValue, 0f, title);
        var descSz  = ImGuiNET.ImGui.CalcTextSize(description);
        var blockH  = titleSz.Y + descSz.Y + 3f;
        var blockY  = origin.Y + (HeaderH - blockH) * 0.5f;

        if (enabled)
        {
            DrawGradientText(dl, font, titleFs, new Vector2(textX, blockY), title,
                Theme.P.Accent, Theme.Mix(Theme.P.Accent, Theme.P.Accent2, 0.5f), Theme.P.Accent2);
        }
        else
        {
            dl.AddText(font, titleFs, new Vector2(textX, blockY),
                Theme.ToU32(Theme.P.Muted), title);
        }

        dl.AddText(new Vector2(textX, blockY + titleSz.Y + 3f),
            Theme.ToU32(Theme.WithAlpha(Theme.P.Muted, 0.4f)), description);

        DrawToggleAt(dl, tMin, enabled, tHov, toggleId);

        if (enabled)
        {
            var sepY = ImGuiNET.ImGui.GetCursorScreenPos().Y;
            dl.AddRectFilled(
                new Vector2(origin.X, sepY),
                new Vector2(origin.X + contentW, sepY + 1f),
                Theme.ToU32(Theme.P.Sep));
            ImGuiNET.ImGui.Dummy(new Vector2(0f, 8f));
        }

        ImGuiNET.ImGui.Indent(ContentIndent);
        return enabled;
    }

    public static void EndFeatureSection()
    {
        ImGuiNET.ImGui.Unindent(ContentIndent);
        ImGuiNET.ImGui.EndChild();
        PopSectionStyle();
        ImGuiNET.ImGui.PopID();
        ImGuiNET.ImGui.Dummy(new Vector2(0f, SectionGap));
    }

    private static void PushSectionStyle()
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.P.Deep);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, SectionRound);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(SectionPad, SectionPad));
    }

    private static void PopSectionStyle()
    {
        ImGuiNET.ImGui.PopStyleVar(2);
        ImGuiNET.ImGui.PopStyleColor();
    }
}