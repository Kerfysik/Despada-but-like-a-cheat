// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Despada.Config;
using Despada.Core;
using Despada.ImGui.Hook;
using ImGuiNET;

namespace Despada.ImGui.UserInterface;

internal static class SettingsPanel
{

    private const float D       = UiScale.DesignHeight / 1080f;
    private const float MaxWidth = 640f * D;

    private static readonly string[] Themes = ["dark", "light"];

    // Label of the hotkey row waiting for a key press, if any.
    private static string? _capturingRow;

    /// <summary>Stops waiting for a hotkey (tab switched, overlay closed, clicked elsewhere).</summary>
    public static void CancelCapture()
    {
        if (_capturingRow is null) return;
        _capturingRow = null;
        ImGuiInputHook.CancelKeyCapture();
    }

    public static void Draw()
    {
        var avail = ImGuiNET.ImGui.GetContentRegionAvail();
        ImGuiNET.ImGui.SetCursorPosX(ImGuiNET.ImGui.GetCursorPosX() + 20f * D);
        ImGuiNET.ImGui.BeginChild("##settings", new Vector2(avail.X - 30f * D, avail.Y - 12f * D));

        var width = MathF.Min(ImGuiNET.ImGui.GetContentRegionAvail().X, MaxWidth);
        ImGuiNET.ImGui.PushItemWidth(width);
        ImGuiNET.ImGui.BeginGroup();
        ImGuiNET.ImGui.Dummy(new Vector2(width, 0f));

        var cfg = DespadaConfig.Current;

        Widgets.BeginSection("Appearance");
        {
            var themeIdx = cfg.Theme == "light" ? 1 : 0;
            if (Widgets.Combo("Theme", ref themeIdx, Themes))
                DespadaConfig.Update(c => c.Theme = Themes[themeIdx]);

            var glass = cfg.GlassOpacity;
            if (Widgets.SliderFloat("Glass opacity", ref glass, 0.6f, 1f, decimals: 2))
                DespadaConfig.Update(c => c.GlassOpacity = glass);

            var blur = cfg.GlassBlur;
            if (Widgets.SliderFloat("Glass blur", ref blur, 0f, 1f, decimals: 2, step: 0.05f))
                DespadaConfig.Update(c => c.GlassBlur = blur);
        }
        Widgets.EndSection();

        Widgets.BeginSection("Network");
        {
            // The safety for the active side: not saved, off again after a restart.
            var armed = Despada.Modules.Net.NetActive.Armed;
            if (Widgets.ToggleRow("Active network (arm)", ref armed)) Despada.Modules.Net.NetActive.Armed = armed;
            ImGuiNET.ImGui.AlignTextToFramePadding();
            ImGuiNET.ImGui.TextUnformatted("Rate cap");
            ImGuiNET.ImGui.SameLine(ImGuiNET.ImGui.GetCursorPosX() + ImGuiNET.ImGui.GetContentRegionAvail().X - 150f);
            var cap = Despada.Modules.Net.NetActive.RateLimit;
            if (Widgets.NumberField("##cap", ref cap, 1, 1000, 1, null, "/s", 150f)) Despada.Modules.Net.NetActive.RateLimit = cap;
        }
        Widgets.EndSection();

        Widgets.BeginSection("Hotkeys");
        KeyRow("Toggle overlay", cfg.ToggleKey, key => DespadaConfig.Update(c => c.ToggleKey = key));
        Widgets.EndSection();

        Widgets.BeginSection("About");
        AboutRow("config", DespadaConfig.FilePath);
        AboutLabel("core");
        Features.DrawEngineSummary();
        Widgets.EndSection();

        ImGuiNET.ImGui.EndGroup();
        ImGuiNET.ImGui.PopItemWidth();
        ImGuiNET.ImGui.EndChild();
    }

    private static void AboutRow(string label, string value)
    {
        AboutLabel(label);
        ImGuiNET.ImGui.TextUnformatted(value);
    }

    private static void AboutLabel(string label)
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Theme.P.Muted);
        ImGuiNET.ImGui.TextUnformatted(label);
        ImGuiNET.ImGui.PopStyleColor();
        ImGuiNET.ImGui.SameLine();
    }

    /// <summary>Label + key button; clicking it waits for the next key press (Esc cancels).</summary>
    private static void KeyRow(string label, string current, Action<string> set)
    {
        var capturing = _capturingRow == label;
        ImGuiNET.ImGui.AlignTextToFramePadding();
        ImGuiNET.ImGui.TextUnformatted(label);

        var text = capturing ? "press a key…" : current;
        var w    = ImGuiNET.ImGui.CalcTextSize(text).X + 24f * D;
        ImGuiNET.ImGui.SameLine();
        var avail = ImGuiNET.ImGui.GetContentRegionAvail().X;
        if (avail > w) ImGuiNET.ImGui.SetCursorPosX(ImGuiNET.ImGui.GetCursorPosX() + avail - w);

        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Button, Theme.P.Raised);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, capturing ? Theme.P.Accent : Theme.P.Fg);
        ImGuiNET.ImGui.PushID(label);
        if (ImGuiNET.ImGui.Button(text + "###key", new Vector2(w, 0f)) && !capturing)
        {
            _capturingRow = label;
            ImGuiInputHook.BeginKeyCapture(key =>
            {
                _capturingRow = null;
                if (key is not null) set(key);
            });
        }
        else if (capturing && !ImGuiNET.ImGui.IsItemHovered() &&
                 (ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left) || ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right)))
        {
            CancelCapture();
        }
        ImGuiNET.ImGui.PopID();
        ImGuiNET.ImGui.PopStyleColor(2);
    }
}
