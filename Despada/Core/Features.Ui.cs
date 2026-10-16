// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Despada.ImGui;
using Despada.ImGui.UserInterface;
using ImGuiNET;

namespace Despada.Core;

// A feature is switched and configured in its own tab (Settings is for the client itself): the tab's
// second row starts with the on/off switch, unavailable/crashed ones say why next to it.
internal static partial class Features
{
    public static IEnumerable<Feature> WithTabs => _all.Where(f => f.Tab is not null);

    public static void DrawTabRow(Feature f)
    {
        ImGuiNET.ImGui.PushID(f.Id);

        var unavailable = f.State == FeatureState.Unavailable;
        var on = f.State == FeatureState.On;
        if (unavailable) ImGuiNET.ImGui.BeginDisabled();
        if (Widgets.Toggle("##on", ref on))
            Request(f, on);
        if (unavailable) ImGuiNET.ImGui.EndDisabled();

        ImGuiNET.ImGui.SameLine();
        if (f.State is FeatureState.Unavailable or FeatureState.Crashed)
        {
            ImGuiNET.ImGui.AlignTextToFramePadding();
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Theme.P.Yellow);
            ImGuiNET.ImGui.TextUnformatted($"⚠ {f.Reason}");
            ImGuiNET.ImGui.PopStyleColor();
        }
        else if (f.State == FeatureState.On)
        {
            // Running without an optional binding: a ⚠ at the row's right end, the names on hover.
            var rowY  = ImGuiNET.ImGui.GetCursorScreenPos().Y;
            var right = ImGuiNET.ImGui.GetCursorScreenPos().X + ImGuiNET.ImGui.GetContentRegionAvail().X;
            Guarded(f, "tab row", f.DrawTabRow);
            if (f.Warning is { } warning)
            {
                var at = new Vector2(right - 10f * (UiScale.DesignHeight / 1080f) - ImGuiNET.ImGui.CalcTextSize("⚠").X, rowY + ImGuiNET.ImGui.GetStyle().FramePadding.Y);
                ImGuiNET.ImGui.GetWindowDrawList().AddText(at, Theme.ToU32(Theme.WithAlpha(Theme.P.Yellow, 0.85f)), "⚠");
                var m = ImGuiNET.ImGui.GetIO().MousePos;
                if (m.X >= at.X - 6f && m.X <= right + 6f && m.Y >= rowY && m.Y <= rowY + ImGuiNET.ImGui.GetFrameHeight())
                    ImGuiNET.ImGui.SetTooltip(warning);
            }
        }
        else if (f.Warning is { } warning)
        {
            ImGuiNET.ImGui.AlignTextToFramePadding();
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Theme.WithAlpha(Theme.P.Yellow, 0.8f));
            ImGuiNET.ImGui.TextUnformatted($"⚠ {warning}");
            ImGuiNET.ImGui.PopStyleColor();
        }

        ImGuiNET.ImGui.PopID();
    }

    // A throwing feature takes only itself down: its ImGui state is unwound and it is switched off.
    private static void Guarded(Feature f, string where, Action draw)
    {
        if (!WindowGuard.TryDraw(draw, out var ex))
            Lifecycle.Post(() => Disable(f, $"{where}: {ex!.GetType().Name}: {ex.Message}"));
    }

    public static void DrawTabBody(Feature f)
    {
        if (f.State == FeatureState.On)
        {
            ImGuiNET.ImGui.PushID(f.Id);
            Guarded(f, "tab", f.DrawTab);
            ImGuiNET.ImGui.PopID();
            return;
        }

        // Off/unavailable: just say so, centred in the tab.
        var text  = f.State == FeatureState.Off ? $"{f.Title} is off" : $"{f.Title} is {Describe(f)}";
        var avail = ImGuiNET.ImGui.GetContentRegionAvail();
        var size  = ImGuiNET.ImGui.CalcTextSize(text);
        var pos   = ImGuiNET.ImGui.GetCursorPos();
        ImGuiNET.ImGui.SetCursorPos(pos + new Vector2(MathF.Max(0f, (avail.X - size.X) * 0.5f), MathF.Max(0f, (avail.Y - size.Y) * 0.4f)));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Theme.P.Muted);
        ImGuiNET.ImGui.TextUnformatted(text);
        ImGuiNET.ImGui.PopStyleColor();
    }

    /// <summary>Settings › About: "engine 23/23 · hooks 9/9", green when complete.</summary>
    public static void DrawEngineSummary()
    {
        var bindOk = Engine.All.Count(b => b.Ok);
        var hooks  = Hooks.All;
        var hookOk = hooks.Count(h => h.State == Hooks.State.Active);

        Part("engine ", Theme.P.Sub);
        Part($"{bindOk}/{Engine.All.Count}", bindOk == Engine.All.Count ? Theme.P.Green : Theme.P.Yellow);
        Part("  ·  ", Theme.P.Muted);
        Part("hooks ", Theme.P.Sub);
        Part($"{hookOk}/{hooks.Count}", hookOk == hooks.Count ? Theme.P.Green : Theme.P.Yellow, last: true);

        static void Part(string text, Vector4 color, bool last = false)
        {
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGuiNET.ImGui.TextUnformatted(text);
            ImGuiNET.ImGui.PopStyleColor();
            if (!last) ImGuiNET.ImGui.SameLine(0f, 0f);
        }
    }
}
