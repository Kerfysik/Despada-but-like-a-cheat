// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Despada.ImGui;
using Despada.ImGui.UserInterface;
using ImGuiNET;

namespace Despada.Modules.Net;

// Drafts: the editor rows (a name, then a control by field kind, in the tree's order), the footer
// (↑ · ×N every M ms · pencil / ✓ ✗ / save), the type picker behind + and the new-packet tab.
internal static partial class NetView
{
    // Repeat settings of the footer, shared by every tab (they're rarely changed).
    private static int _sendTimes = 1, _sendEveryMs = 100;

    private static string _typeSearch = "";
    private static bool   _typeSearchFocus;

    // The entity hint list drawn last frame: a click on it is taken here, before the field it belongs
    // to loses focus to that click (and before any row under the list sees it).
    private static DraftField? _hintField;
    private static List<(int Id, string Hint)> _hints = [];
    private static Vector2 _hintMin, _hintMax;
    private static float   _hintRowH;
    private static bool    _hintShown, _hintClicked;
    private static int     _hintHover = -1;   // the row under the mouse (the picked one)
    private static float   _hintRowsY;        // the first row's top (the box has padding above it)
    private static (string, string)[] _hintBuf = new (string, string)[8];

    private static bool MouseOverHints
    {
        get
        {
            if (!_hintShown) return false;
            var m = ImGuiNET.ImGui.GetIO().MousePos;
            return m.X >= _hintMin.X && m.X < _hintMax.X && m.Y >= _hintMin.Y && m.Y < _hintMax.Y;
        }
    }

    // ── the new-packet tab ───────────────────────────────────────────────────────────────────

    private static void DrawDraftTab(PacketDraft d, float width, float height)
    {
        BeginPanel("##draft", width, height);
        // The arrow is a switch: a guessed direction (↑? types, bidirectional events) can be corrected.
        var dir = d.Dir;
        var arrowPos = ImGuiNET.ImGui.GetCursorScreenPos();
        if (ImGuiNET.ImGui.InvisibleButton("##dir", new Vector2(ImGuiNET.ImGui.CalcTextSize("↓ ").X, ImGuiNET.ImGui.GetTextLineHeight())))
            d.Dir = dir = dir == NetDir.In ? NetDir.Out : NetDir.In;
        var arrowHot = ImGuiNET.ImGui.IsItemHovered();
        ImGuiNET.ImGui.GetWindowDrawList().AddText(arrowPos, Theme.ToU32(arrowHot ? Theme.P.Fg : dir == NetDir.In ? Theme.P.Accent : Theme.P.Accent2), dir == NetDir.In ? "↓" : "↑");
        ImGuiNET.ImGui.SameLine(0f, 0f);
        Text(d.TypeName, Theme.P.Accent2);
        if (d.ChangedCount > 0) Text($"   {d.ChangedCount} set", Theme.P.Muted);
        ImGuiNET.ImGui.NewLine();
        ImGuiNET.ImGui.Dummy(new Vector2(0f, 6f * D));

        ImGuiNET.ImGui.BeginChild("##draft-fields", new Vector2(-1f, -FooterHeight()), ImGuiChildFlags.None,
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.HorizontalScrollbar);
        SmoothScroll.Update();
        DrawDraftRows(d, editable: true);
        ImGuiNET.ImGui.EndChild();

        var action = DrawFooter(FooterMode.Draft, d.Dir);
        if (action == FooterAction.Send) NetActive.Send(d, _sendTimes, _sendEveryMs);
        if (action == FooterAction.Save) SaveDraft(d);
        ImGuiNET.ImGui.EndChild();
    }

    // The rounded dark panel every tab body sits in (the inspector's look).
    private static void BeginPanel(string id, float width, float height)
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.P.Deep);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Theme.SmallRounding);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(18f * D, 14f * D));
        ImGuiNET.ImGui.BeginChild(id, new Vector2(width, height), ImGuiChildFlags.AlwaysUseWindowPadding,
            ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoScrollbar);
        ImGuiNET.ImGui.PopStyleVar(2);
        ImGuiNET.ImGui.PopStyleColor();
    }

    // ── the editor rows ──────────────────────────────────────────────────────────────────────

    /// <summary>The draft's fields as rows: controls when <paramref name="editable"/>, text otherwise.</summary>
    private static void DrawDraftRows(PacketDraft d, bool editable)
    {
        // A click on last frame's hint list picks that id.
        _hintClicked = false;
        if (_hintShown && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left) && MouseOverHints && _hintField is not null)
        {
            var i = (int)MathF.Floor((ImGuiNET.ImGui.GetIO().MousePos.Y - _hintRowsY) / _hintRowH);
            if (i >= 0 && i < _hints.Count) _hintField.Value = _hints[i].Id.ToString();
            _hintClicked = true;
        }
        var shownBefore = _hintShown;
        _hintShown = false;
        var x0 = ImGuiNET.ImGui.GetCursorScreenPos().X - 2f * ImGuiNET.ImGui.CalcTextSize("M").X;
        DrawDraftLevel(d.Root.Children, x0, 1, 0, editable);
        if (shownBefore && !_hintShown) _hintField = null;
        if (!_hintShown) _hintHover = -1;
    }

    private static void DrawDraftLevel(List<DraftField> fields, float x0, int depth, uint guides, bool editable)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            var f    = fields[i];
            var last = i == fields.Count - 1;
            DrawDraftRow(f, x0, depth, guides, last, editable);
            if (f.Kind == DraftKind.Node && f.Expanded)
                DrawDraftLevel(f.Children, x0, depth + 1, depth <= 32 && !last ? guides | (1u << (depth - 1)) : guides, editable);
        }
    }

    private static void DrawDraftRow(DraftField f, float x0, int depth, uint guides, bool last, bool editable)
    {
        var dl    = ImGuiNET.ImGui.GetWindowDrawList();
        var cw    = ImGuiNET.ImGui.CalcTextSize("M").X;
        var step  = cw * 2f;
        var lineH = ImGuiNET.ImGui.GetTextLineHeight();
        var rowH  = editable && f.Kind is not (DraftKind.Node or DraftKind.Readonly)
            ? ImGuiNET.ImGui.GetFrameHeight() + 4f * D
            : ImGuiNET.ImGui.GetTextLineHeightWithSpacing();
        var pos   = ImGuiNET.ImGui.GetCursorScreenPos();
        var textY = pos.Y + (rowH - lineH) * 0.5f;
        TreeRows.Guides(dl, x0, depth, guides, last, pos.Y, rowH, rowH);

        ImGuiNET.ImGui.PushID(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(f));
        var x = x0 + depth * step;

        // A node folds like a tree row; its name is the whole row's button.
        if (f.Kind == DraftKind.Node)
        {
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, pos.Y));
            var w = step + ImGuiNET.ImGui.CalcTextSize(f.Name).X + cw * 2f + ImGuiNET.ImGui.CalcTextSize(TypeLabel(f)).X;
            if (ImGuiNET.ImGui.InvisibleButton("##node", new Vector2(w, lineH))) f.Expanded = !f.Expanded;
            var hov = ImGuiNET.ImGui.IsItemHovered();
            dl.AddText(new Vector2(x, textY), Theme.ToU32(hov ? Theme.P.Fg : Theme.P.Muted), f.Expanded ? "▾" : "▸");
            dl.AddText(new Vector2(x + step, textY), Theme.ToU32(Theme.P.Muted), f.Name);
            dl.AddText(new Vector2(x + step + ImGuiNET.ImGui.CalcTextSize(f.Name).X + cw * 2f, textY), Theme.ToU32(Theme.P.Accent2), TypeLabel(f));
            ImGuiNET.ImGui.SetCursorScreenPos(pos);
            ImGuiNET.ImGui.Dummy(new Vector2(1f, rowH - ImGuiNET.ImGui.GetStyle().ItemSpacing.Y));
            ImGuiNET.ImGui.PopID();
            return;
        }

        // Name, padded like the field tree, then the value.
        var nameW = MathF.Max(14, f.Name.Length + 2) * cw;
        dl.AddText(new Vector2(x, textY), Theme.ToU32(Theme.P.Muted), f.Name);
        var vx = x + nameW;

        if (!editable || f.Kind == DraftKind.Readonly)
        {
            var color = f.Kind switch
            {
                DraftKind.Text   => Theme.P.Green,
                DraftKind.Bool   => Theme.P.Accent2,
                DraftKind.Enum   => Theme.P.Sub,
                DraftKind.Entity => Theme.P.Link,
                DraftKind.Readonly => Theme.P.Sub,
                _ => Theme.P.Fg,
            };
            var shown = f.Kind == DraftKind.Text ? $"\"{f.Value}\"" : f.Value.Length == 0 ? "null" : f.Value;
            dl.AddText(new Vector2(vx, textY), Theme.ToU32(f.Value.Length == 0 && f.Kind != DraftKind.Text ? Theme.P.Muted : color), shown);
            vx += ImGuiNET.ImGui.CalcTextSize(shown).X;
            if (f.Kind == DraftKind.Entity) { dl.AddText(new Vector2(vx, textY), Theme.ToU32(Theme.P.Muted), "  NetEntity"); vx += ImGuiNET.ImGui.CalcTextSize("  NetEntity").X; }
            if (f.Changed)
                dl.AddText(new Vector2(vx + cw, textY), Theme.ToU32(Theme.P.Accent2), $"was {f.Original}");
            ImGuiNET.ImGui.SetCursorScreenPos(pos);
            ImGuiNET.ImGui.Dummy(new Vector2(1f, rowH - ImGuiNET.ImGui.GetStyle().ItemSpacing.Y));
            ImGuiNET.ImGui.PopID();
            return;
        }

        // The control.
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(vx, pos.Y + 2f * D));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, Theme.P.Deep);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, f.Changed ? Theme.P.Accent2 : Theme.P.Sep);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameBorderSize, 1f / UiScale.K);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 7f * D);
        switch (f.Kind)
        {
            case DraftKind.Bool:
            {
                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(vx, pos.Y + 2f * D));   // the item is frame-high; the switch is centred in it
                var on = f.Value == "true";
                if (Widgets.Toggle("##v", ref on)) f.Value = on ? "true" : "false";
                ImGuiNET.ImGui.SameLine(0f, 8f * D);
                ImGuiNET.ImGui.SetCursorPosY(ImGuiNET.ImGui.GetCursorPosY() - 2f * D);
                Text(f.Value, Theme.P.Muted);
                break;
            }
            case DraftKind.Enum:
            {
                ImGuiNET.ImGui.SetNextItemWidth(MathF.Max(cw * 8f, ImGuiNET.ImGui.CalcTextSize(f.Value).X + cw * 4f));
                if (ImGuiNET.ImGui.BeginCombo("##v", f.Value.Length == 0 ? "null" : f.Value))
                {
                    if (f.Original.Length == 0 && ImGuiNET.ImGui.Selectable("null", f.Value.Length == 0)) f.Value = "";
                    foreach (var o in f.Options)
                        if (ImGuiNET.ImGui.Selectable(o, o == f.Value)) f.Value = o;
                    ImGuiNET.ImGui.EndCombo();
                }
                break;
            }
            case DraftKind.Number:
                ImGuiNET.ImGui.SetNextItemWidth(cw * 12f);
                ImGuiNET.ImGui.InputText("##v", ref f.Value, 64, ImGuiInputTextFlags.CharsScientific);
                break;
            case DraftKind.Entity:
            {
                ImGuiNET.ImGui.SetNextItemWidth(cw * 10f);
                // The click that picked a hint must not land in a field under the list.
                if (_hintClicked) ImGuiNET.ImGui.BeginDisabled();
                ImGuiNET.ImGui.InputText("##v", ref f.Value, 16, ImGuiInputTextFlags.CharsDecimal);
                if (_hintClicked) ImGuiNET.ImGui.EndDisabled();
                var active = ImGuiNET.ImGui.IsItemActive();
                ImGuiNET.ImGui.SameLine(0f, 8f * D);
                Text("NetEntity", Theme.P.Muted);
                // Ids seen in the capture, under the field while it's typed in (kept one frame past
                // deactivation, so a click on the list still finds it).
                if ((active || (f == _hintField && ImGuiNET.ImGui.IsItemDeactivated())) && f.Value.Length > 0 && !_hintClicked)
                    DrawEntityHints(f, new Vector2(vx, pos.Y + rowH));
                break;
            }
            default:
                ImGuiNET.ImGui.SetNextItemWidth(MathF.Max(cw * 16f, MathF.Min(cw * 60f, ImGuiNET.ImGui.CalcTextSize(f.Value).X + cw * 3f)));
                ImGuiNET.ImGui.InputText("##v", ref f.Value, 4096);
                break;
        }
        ImGuiNET.ImGui.PopStyleVar(2);
        ImGuiNET.ImGui.PopStyleColor(2);
        if (f.Changed)
        {
            ImGuiNET.ImGui.SameLine(0f, 10f * D);
            ImGuiNET.ImGui.SetCursorPosY(ImGuiNET.ImGui.GetCursorPosY() + 2f * D);
            Text($"was {f.Original}", Theme.P.Muted);
        }
        ImGuiNET.ImGui.NewLine();   // Text() leaves the line open (SameLine): close it, else the row's Dummy inherits the control's line
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        ImGuiNET.ImGui.Dummy(new Vector2(1f, rowH - ImGuiNET.ImGui.GetStyle().ItemSpacing.Y));
        ImGuiNET.ImGui.PopID();
    }

    private static string TypeLabel(DraftField f) => f.Type?.Name ?? "";

    // A few known entity ids starting with what's typed; the click is read next frame (DrawDraftRows).
    private static void DrawEntityHints(DraftField f, Vector2 at)
    {
        var hints = KnownEntities(f.Value);
        if (hints.Count == 0) return;
        _hintField = f;
        _hints = hints;
        _hintShown = true;
        if (_hintBuf.Length < hints.Count) _hintBuf = new (string, string)[hints.Count];
        for (var i = 0; i < hints.Count; i++) _hintBuf[i] = (hints[i].Id.ToString(), hints[i].Hint);
        // The picked row is the hovered one (there is no keyboard pick for hints).
        Widgets.Completions(ImGuiNET.ImGui.GetForegroundDrawList(), at + new Vector2(0f, 2f * D), _hintBuf.AsSpan(0, hints.Count), -1,
            f.Value.AsSpan().TrimStart('-'), out _hintMin, out _hintMax, out _hintHover);
        _hintRowH  = Widgets.CompletionRowH;
        _hintRowsY = _hintMin.Y + Widgets.CompletionPad;
    }

    // ── the footer ───────────────────────────────────────────────────────────────────────────

    private enum FooterMode { View, Edit, Draft }
    private enum FooterAction { None, Send, Edit, Accept, Cancel, Save }

    private static float FooterHeight() => ImGuiNET.ImGui.GetFrameHeight() + 18f * D;

    /// <summary>
    /// "↑ · × N every M ms" and, on the right, the pencil (view), ✓ ✗ (edit) or save (draft).
    /// The send button is dimmed while not armed; a click then only says why.
    /// </summary>
    private static FooterAction DrawFooter(FooterMode mode, NetDir dir)
    {
        var action = FooterAction.None;
        var h   = ImGuiNET.ImGui.GetFrameHeight();
        var cw  = ImGuiNET.ImGui.CalcTextSize("M").X;
        var dl  = ImGuiNET.ImGui.GetWindowDrawList();
        ImGuiNET.ImGui.Dummy(new Vector2(0f, 6f * D));
        var pos = ImGuiNET.ImGui.GetCursorScreenPos();

        // ↑ / ↓: the primary gradient when armed, a muted secondary otherwise (a click then says why).
        var armed = NetActive.Armed;
        if (Widgets.IconButton("##send", dir == NetDir.In ? Icon.Down : Icon.Up, armed ? ButtonKind.Primary : ButtonKind.Secondary,
                armed ? null : Theme.P.Muted))
        {
            if (armed) action = FooterAction.Send;
            else ImGuiRenderer.ShowToast("network", "arm to send packets (Settings → Network)", 2f);
        }

        // × N | every ms (the second half opens from ×2).
        ImGuiNET.ImGui.SameLine(0f, 10f);
        Widgets.Repeat("##rep", ref _sendTimes, ref _sendEveryMs);
        _sendTimes = Math.Clamp(_sendTimes, 1, 99);

        // Right end.
        var right = pos.X + ImGuiNET.ImGui.GetContentRegionAvail().X;   // the panel's content edge (its padding is wider than the style's)
        var bw    = Widgets.ButtonH;
        switch (mode)
        {
            case FooterMode.View:
                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(right - bw, pos.Y));
                if (Widgets.IconButton("##edit", Icon.Pencil)) action = FooterAction.Edit;
                break;
            case FooterMode.Edit:
                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(right - bw, pos.Y));
                if (Widgets.IconButton("##cancel", Icon.Cross, ButtonKind.Secondary, Theme.P.Red)) action = FooterAction.Cancel;
                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(right - bw * 2f - 8f, pos.Y));
                if (Widgets.IconButton("##accept", Icon.Check, ButtonKind.Secondary, Theme.P.Green)) action = FooterAction.Accept;
                break;
            case FooterMode.Draft:
            {
                var sw = ImGuiNET.ImGui.CalcTextSize("save").X + 28f;
                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(right - sw, pos.Y));
                if (Widgets.Button("##save", "save")) action = FooterAction.Save;
                break;
            }
        }
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        ImGuiNET.ImGui.Dummy(new Vector2(1f, h));
        return action;
    }



    // ── the type picker ──────────────────────────────────────────────────────────────────────

    private static void DrawTypePicker()
    {
        ImGuiNET.ImGui.SetNextWindowSizeConstraints(new Vector2(560f * D, 0f), new Vector2(760f * D, 700f * D));
        if (!ImGuiNET.ImGui.BeginPopup("##type-picker"))
        {
            _typeSearch = "";
            return;
        }
        // The search gets the caret the frame the popup appears (and the next, in case focus is late).
        if (ImGuiNET.ImGui.IsWindowAppearing()) _typeSearchFocus = true;
        if (_typeSearchFocus)
        {
            ImGuiNET.ImGui.SetKeyboardFocusHere();
            _typeSearchFocus = false;
        }
        ImGuiNET.ImGui.AlignTextToFramePadding();
        Text("› ", Theme.P.Accent);
        ImGuiNET.ImGui.SameLine(0f, 0f);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, Theme.P.Deep);
        ImGuiNET.ImGui.SetNextItemWidth(-1f);
        var enter = ImGuiNET.ImGui.InputText("##type-search", ref _typeSearch, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGuiNET.ImGui.PopStyleColor();

        var all = NetTypes.All;
        if (all.Count == 0)
        {
            PanelCaption(NetTypes.Failed ? "reading the types failed" : "reading the types…");
            ImGuiNET.ImGui.EndPopup();
            return;
        }
        var q = _typeSearch.Trim();
        var hits = all.Where(t => t.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
        string? pick = enter && hits.Count > 0 ? hits[0].Name : null;

        // Every type, in a scrolling list (over a thousand without a search).
        ImGuiNET.ImGui.BeginChild("##type-list", new Vector2(-1f, MathF.Min(hits.Count + 3, 16) * ImGuiNET.ImGui.GetTextLineHeightWithSpacing()),
            ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollWithMouse);
        SmoothScroll.Update();
        foreach (var (kind, caption) in new[] { (NetTypes.Kind.Event, "events"), (NetTypes.Kind.Message, "messages"), (NetTypes.Kind.Bui, "BUI messages") })
        {
            var group = hits.Where(t => t.Kind == kind).ToList();
            if (group.Count == 0) continue;
            PanelCaption($"{caption}  {group.Count}");
            foreach (var t in group)
            {
                var dir = NetActive.DirectionOf(t.Name);
                ImGuiNET.ImGui.PushID(t.Name);
                if (ImGuiNET.ImGui.Selectable("##t", false, ImGuiSelectableFlags.None, new Vector2(0f, ImGuiNET.ImGui.GetTextLineHeightWithSpacing())))
                    pick = t.Name;
                var min = ImGuiNET.ImGui.GetItemRectMin();
                var max = ImGuiNET.ImGui.GetItemRectMax();
                var dl  = ImGuiNET.ImGui.GetWindowDrawList();
                dl.AddText(min + new Vector2(16f * D, 2f * D), Theme.ToU32(Theme.P.Fg), t.Name);
                var arrow = dir is null ? "↑?" : dir == NetDir.In ? "↓" : "↑";
                dl.AddText(new Vector2(max.X - 16f * D - ImGuiNET.ImGui.CalcTextSize(arrow).X, min.Y + 2f * D),
                    Theme.ToU32(dir == NetDir.In ? Theme.P.Accent : dir is null ? Theme.P.Muted : Theme.P.Accent2), arrow);
                ImGuiNET.ImGui.PopID();
            }
        }
        if (hits.Count == 0) PanelCaption("no type matches");
        ImGuiNET.ImGui.EndChild();
        if (pick is not null)
        {
            if (PacketDraft.ForType(pick) is { } d) ShowDraft(d);
            else ImGuiRenderer.ShowToast("network", $"{pick}: type not found", 2f);
            ImGuiNET.ImGui.CloseCurrentPopup();
        }
        ImGuiNET.ImGui.EndPopup();
    }

    // ── saving ───────────────────────────────────────────────────────────────────────────────

    private static void SaveDraft(PacketDraft d)
    {
        var name = d.TypeName;
        var n = 2;
        while (NetActive.Saved.Any(s => s.Name == name)) name = $"{d.TypeName} {n++}";
        NetActive.Saved.Add(SavedPacket.FromDraft(d, name));
        NetFeature.StoreSaved();
        ImGuiRenderer.ShowToast("network", $"saved as “{name}”", 2f);
    }
}
