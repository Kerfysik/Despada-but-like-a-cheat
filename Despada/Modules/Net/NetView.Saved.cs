// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Despada.ImGui;
using Despada.ImGui.UserInterface;
using ImGuiNET;

namespace Despada.Modules.Net;

// The saved tab: drafts kept under a name. Each row: name (double-click renames), direction, type,
// note, when it was last sent; send as it is, edit (opens a draft tab), ×.
internal static partial class NetView
{
    private static SavedPacket? _renamingSaved;
    private static string _savedName = "";
    private static bool   _savedNameFocus;
    private static int    _savedNameFrame;

    private static void DrawSavedTab(float width, float height)
    {
        var saved = NetActive.Saved;
        var cw    = ImGuiNET.ImGui.CalcTextSize("M").X;

        ImGuiNET.ImGui.AlignTextToFramePadding();
        Text($"{saved.Count} saved", Theme.P.Fg);
        ImGuiNET.ImGui.NewLine();
        ImGuiNET.ImGui.Dummy(new Vector2(0f, 4f * D));
        var h = ImGuiNET.ImGui.GetFrameHeight();

        BeginPanel("##saved", width, height - h - 14f * D);
        ImGuiNET.ImGui.BeginChild("##saved-rows", new Vector2(-1f, -1f), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollWithMouse);
        SmoothScroll.Update();

        SavedPacket? remove = null;
        var rowH = ImGuiNET.ImGui.GetFrameHeight() + 6f * D;
        var changed = false;
        for (var i = 0; i < saved.Count; i++)
        {
            var s = saved[i];
            ImGuiNET.ImGui.PushID(i);
            var pos = ImGuiNET.ImGui.GetCursorScreenPos();
            var w   = ImGuiNET.ImGui.GetContentRegionAvail().X;
            var ty  = pos.Y + (rowH - ImGuiNET.ImGui.GetTextLineHeight()) * 0.5f;
            var dl  = ImGuiNET.ImGui.GetWindowDrawList();
            var nameW = cw * 24f;

            // The name: a double click renames it in place.
            if (_renamingSaved == s)
            {
                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(pos.X + 4f * D, pos.Y + 3f * D));
                ImGuiNET.ImGui.SetNextItemWidth(nameW - 8f * D);
                ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, Theme.P.Deep);
                var frame = ImGuiNET.ImGui.GetFrameCount();
                if (_savedNameFocus) { ImGuiNET.ImGui.SetKeyboardFocusHere(); _savedNameFocus = false; _savedNameFrame = frame; }
                // Its own id (not the row button's, which was the active item a frame ago).
                var commit = ImGuiNET.ImGui.InputText("##rename", ref _savedName, 64, ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
                // Focus lands a frame after SetKeyboardFocusHere; after that, inactive = cancelled.
                var gone = !commit && frame - _savedNameFrame > 2 && !ImGuiNET.ImGui.IsItemActive();
                ImGuiNET.ImGui.PopStyleColor();
                if (commit && _savedName.Trim().Length > 0) { s.Name = _savedName.Trim(); changed = true; }
                if (commit || gone) _renamingSaved = null;
            }
            else
            {
                ImGuiNET.ImGui.SetCursorScreenPos(pos);
                ImGuiNET.ImGui.InvisibleButton("##name", new Vector2(nameW, rowH));
                if (ImGuiNET.ImGui.IsItemHovered())
                {
                    dl.AddRectFilled(pos, pos + new Vector2(w, rowH), Theme.ToU32(Theme.P.Raised), 6f * D);
                    if (ImGuiNET.ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    {
                        _renamingSaved = s;
                        _savedName = s.Name;
                        _savedNameFocus = true;
                    }
                }
                dl.PushClipRect(pos, new Vector2(pos.X + nameW - cw, pos.Y + rowH), true);
                dl.AddText(new Vector2(pos.X + 8f * D, ty), Theme.ToU32(Theme.P.Fg), s.Name);
                dl.PopClipRect();
            }

            var tx = pos.X + nameW;
            dl.AddText(new Vector2(tx, ty), Theme.ToU32(s.Dir == NetDir.In ? Theme.P.Accent : Theme.P.Accent2), s.Dir == NetDir.In ? "↓" : "↑");
            tx += cw * 2f;
            dl.AddText(new Vector2(tx, ty), Theme.ToU32(Theme.P.Accent2), s.TypeName);
            tx += cw * MathF.Max(24f, s.TypeName.Length + 2f);
            var last = s.LastSent is { } t ? Ago(t) : "never";
            var lastW = ImGuiNET.ImGui.CalcTextSize(last).X;
            dl.PushClipRect(new Vector2(tx, pos.Y), new Vector2(pos.X + w - cw * 20f - lastW, pos.Y + rowH), true);
            dl.AddText(new Vector2(tx, ty), Theme.ToU32(Theme.P.Muted), s.Note);
            dl.PopClipRect();

            // Right end: last sent, send, edit, × (a margin from the panel's edge).
            var rx = pos.X + w - 8f * D;
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(rx - cw * 2f, pos.Y));
            if (ImGuiNET.ImGui.InvisibleButton("##x", new Vector2(cw * 2f, rowH))) remove = s;
            dl.AddText(new Vector2(rx - cw * 1.5f, ty), Theme.ToU32(ImGuiNET.ImGui.IsItemHovered() ? Theme.P.Fg : Theme.P.Muted), "×");
            rx -= cw * 2f + 8f * D;
            var bs = rowH - 10f;
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(rx - bs, pos.Y + 5f));
            if (Widgets.IconButton("##edit", Icon.Pencil, ButtonKind.Secondary, null, bs))
            {
                if (s.Draft() is { } ed) ShowDraft(ed.Clone());
                else ImGuiRenderer.ShowToast("network", $"{s.TypeName}: type not known yet", 2f);
            }
            rx -= bs + 10f;
            var armed = NetActive.Armed;
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(rx - bs, pos.Y + 5f));
            if (Widgets.IconButton("##send", s.Dir == NetDir.In ? Icon.Down : Icon.Up, armed ? ButtonKind.Primary : ButtonKind.Secondary, armed ? null : Theme.P.Muted, bs))
            {
                if (!armed) ImGuiRenderer.ShowToast("network", "arm to send packets (Settings → Network)", 2f);
                else if (s.Draft() is not { } d) ImGuiRenderer.ShowToast("network", $"{s.TypeName}: type not known yet", 2f);
                else if (NetActive.Send(d, 1, 0)) { s.LastSent = DateTime.Now; changed = true; }
            }
            rx -= bs + cw;
            dl.AddText(new Vector2(rx - lastW, ty), Theme.ToU32(Theme.P.Muted), last);

            ImGuiNET.ImGui.SetCursorScreenPos(pos);
            ImGuiNET.ImGui.Dummy(new Vector2(1f, rowH));
            Gfx.HLine(dl, pos.X, pos.X + w, pos.Y + rowH, Theme.ToU32(Theme.WithAlpha(Theme.P.Sep, 0.2f)));
            ImGuiNET.ImGui.PopID();
        }
        if (remove is not null) { saved.Remove(remove); changed = true; }
        if (changed) NetFeature.StoreSaved();
        ImGuiNET.ImGui.EndChild();
        ImGuiNET.ImGui.EndChild();
    }

    private static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        return d.TotalMinutes < 1 ? "just now" : d.TotalHours < 1 ? $"{d.TotalMinutes:0} min ago"
             : d.TotalDays < 1 ? $"{d.TotalHours:0} h ago" : t.ToString("d MMM");
    }
}
