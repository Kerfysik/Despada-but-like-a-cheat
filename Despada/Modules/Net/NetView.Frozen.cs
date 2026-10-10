// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using System.Numerics;
using Despada.ImGui;
using Despada.ImGui.UserInterface;
using ImGuiNET;

namespace Despada.Modules.Net;

// The frozen tab: packets a freeze rule is holding, oldest first, each with how long it has waited
// and release / drop; "/release" and "/drop" in the search bar take them all.
internal static partial class NetView
{
    private static void DrawFrozenTab(float width, float height)
    {
        var frozen = NetActive.FrozenSnapshot();
        var cw     = ImGuiNET.ImGui.CalcTextSize("M").X;

        // The line above the panel: the count, the oldest, the buttons.
        var left = ImGuiNET.ImGui.GetCursorScreenPos().X;
        ImGuiNET.ImGui.AlignTextToFramePadding();
        Text($"{frozen.Count} frozen", Theme.P.Accent);
        if (frozen.Count > 0)
            Text($"   ·   oldest {Age(frozen[0])}", Theme.P.Muted);
        var h = ImGuiNET.ImGui.GetFrameHeight();
        var y = ImGuiNET.ImGui.GetCursorScreenPos().Y;
        ImGuiNET.ImGui.NewLine();
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(left, y + h + 14f * D));

        BeginPanel("##frozen", width, height - h - 14f * D);
        ImGuiNET.ImGui.BeginChild("##frozen-rows", new Vector2(-1f, -1f), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollWithMouse);
        SmoothScroll.Update();
        if (frozen.Count == 0)
            Text("nothing frozen", Theme.P.Muted);

        Packet? release = null, drop = null;
        var rowH = ImGuiNET.ImGui.GetFrameHeight() + 6f * D;
        foreach (var p in frozen)
        {
            ImGuiNET.ImGui.PushID(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p));   // frozen while paused: no Seq
            var pos = ImGuiNET.ImGui.GetCursorScreenPos();
            var w   = ImGuiNET.ImGui.GetContentRegionAvail().X;
            var ty  = pos.Y + (rowH - ImGuiNET.ImGui.GetTextLineHeight()) * 0.5f;
            var dl  = ImGuiNET.ImGui.GetWindowDrawList();

            // Ctrl+Shift+click opens it in the inspector, as in the list.
            ImGuiNET.ImGui.InvisibleButton("##row", new Vector2(MathF.Max(1f, w - cw * 20f), rowH));
            if (ImGuiNET.ImGui.IsItemHovered())
            {
                dl.AddRectFilled(pos, pos + new Vector2(w, rowH), Theme.ToU32(Theme.P.Raised), 6f * D);
                var io = ImGuiNET.ImGui.GetIO();
                if (ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left) && io.KeyCtrl && io.KeyShift && NetCapture.Get(p.Seq) == p) OpenDoc(p.Seq);
            }
            var tx = pos.X + 8f * D;
            dl.AddText(new Vector2(tx, ty), Theme.ToU32(Theme.P.Muted), p.TimeText ??= NetFeature.Time(p));
            tx += cw * 14f;
            dl.AddText(new Vector2(tx, ty), Theme.ToU32(p.Dir == NetDir.In ? Theme.P.Accent : Theme.P.Accent2), p.Dir == NetDir.In ? "↓" : "↑");
            tx += cw * 2f;
            dl.AddText(new Vector2(tx, ty), Theme.ToU32(Theme.P.Fg), p.Type.Name);
            tx += cw * MathF.Max(12f, p.Type.Name.Length + 2f);
            dl.PushClipRect(new Vector2(tx, pos.Y), new Vector2(pos.X + w - cw * 22f, pos.Y + rowH), true);
            dl.AddText(new Vector2(tx, ty), Theme.ToU32(Theme.P.Accent2), p.Inner ?? NetDecode.Summary(p));
            dl.PopClipRect();

            // Right end: age, then release (↑ in the accent) and drop (a red bin), square icon buttons.
            var bs = rowH - 10f;
            var rx = pos.X + w - 8f * D;
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(rx - bs, pos.Y + 5f));
            if (Widgets.IconButton("##drop", Icon.Trash, ButtonKind.Secondary, Theme.P.Red, bs)) drop = p;
            rx -= bs + 8f;
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(rx - bs, pos.Y + 5f));
            if (Widgets.IconButton("##release", p.Dir == NetDir.In ? Icon.Down : Icon.Up, ButtonKind.Secondary, Theme.P.Accent, bs)) release = p;
            rx -= bs + cw;
            var age = Age(p);
            dl.AddText(new Vector2(rx - ImGuiNET.ImGui.CalcTextSize(age).X, ty), Theme.ToU32(Theme.P.Accent), age);

            ImGuiNET.ImGui.SetCursorScreenPos(pos);
            ImGuiNET.ImGui.Dummy(new Vector2(1f, rowH));
            Gfx.HLine(dl, pos.X, pos.X + w, pos.Y + rowH, Theme.ToU32(Theme.WithAlpha(Theme.P.Sep, 0.2f)));
            ImGuiNET.ImGui.PopID();
        }
        if (release is not null) NetActive.Release(release);
        if (drop is not null) NetActive.Drop(drop);
        ImGuiNET.ImGui.EndChild();
        ImGuiNET.ImGui.EndChild();
    }

    private static string Age(Packet p)
    {
        var s = Stopwatch.GetElapsedTime(p.Stamp).TotalSeconds;
        return s < 60 ? $"{s:0.0} s" : $"{s / 60:0} min";
    }

}
