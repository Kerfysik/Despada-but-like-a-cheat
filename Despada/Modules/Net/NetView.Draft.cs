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
        DrawDraftLevel(d.Root.Children, null, x0, 1, 0, editable);
        if (shownBefore && !_hintShown) _hintField = null;
        if (!_hintShown) _hintHover = -1;
    }

    private static void DrawDraftLevel(List<DraftField> fields, DraftField? parent, float x0, int depth, uint guides, bool editable)
    {
        DraftField? remove = null;
        for (var i = 0; i < fields.Count; i++)
        {
            var f    = fields[i];
            var last = i == fields.Count - 1;
            if (DrawDraftRow(f, parent, x0, depth, guides, last, editable)) remove = f;
            if (f.Kind is DraftKind.Node or DraftKind.List && f.Expanded)
                DrawDraftLevel(f.Children, f, x0, depth + 1, depth <= 32 && !last ? guides | (1u << (depth - 1)) : guides, editable);
        }
        if (remove is not null && parent is not null) PacketDraft.RemoveItem(parent, remove);
    }

    // True when the row's × was clicked (a list item to remove).
    private static bool DrawDraftRow(DraftField f, DraftField? parent, float x0, int depth, uint guides, bool last, bool editable)
    {
        var dl    = ImGuiNET.ImGui.GetWindowDrawList();
        var cw    = ImGuiNET.ImGui.CalcTextSize("M").X;
        var step  = cw * 2f;
        var lineH = ImGuiNET.ImGui.GetTextLineHeight();
        var isItem = editable && parent is { Kind: DraftKind.List };
        var remove = false;
        var rowH  = editable && f.Kind is not (DraftKind.Node or DraftKind.List or DraftKind.Readonly)
            ? ImGuiNET.ImGui.GetFrameHeight() + 4f * D
            : ImGuiNET.ImGui.GetTextLineHeightWithSpacing();
        var pos   = ImGuiNET.ImGui.GetCursorScreenPos();
        var textY = pos.Y + (rowH - lineH) * 0.5f;
        TreeRows.Guides(dl, x0, depth, guides, last, pos.Y, rowH, rowH);

        ImGuiNET.ImGui.PushID(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(f));
        var x = x0 + depth * step;

        // A node or list folds like a tree row; its name is the whole row's button. A list gets
        // its count and, when editing, a + for a new item; an item node gets its ×.
        if (f.Kind is DraftKind.Node or DraftKind.List)
        {
            var label = f.Kind == DraftKind.List ? $"{TypeLabel(f)}  {f.Children.Count} items" : TypeLabel(f);
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, pos.Y));
            var w = step + ImGuiNET.ImGui.CalcTextSize(f.Name).X + cw * 2f + ImGuiNET.ImGui.CalcTextSize(label).X;
            if (ImGuiNET.ImGui.InvisibleButton("##node", new Vector2(w, lineH))) f.Expanded = !f.Expanded;
            var hov = ImGuiNET.ImGui.IsItemHovered();
            dl.AddText(new Vector2(x, textY), Theme.ToU32(hov ? Theme.P.Fg : Theme.P.Muted), f.Expanded ? "▾" : "▸");
            dl.AddText(new Vector2(x + step, textY), Theme.ToU32(Theme.P.Muted), f.Name);
            var lx = x + step + ImGuiNET.ImGui.CalcTextSize(f.Name).X + cw * 2f;
            dl.AddText(new Vector2(lx, textY), Theme.ToU32(f.Kind == DraftKind.List && f.Touched ? Theme.P.Accent2 : Theme.P.Accent2), label);
            lx += ImGuiNET.ImGui.CalcTextSize(label).X + cw;
            if (editable && f.Kind == DraftKind.List && f.ElementType is not null)
            {
                ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(lx, pos.Y));
                if (ImGuiNET.ImGui.InvisibleButton("##add", new Vector2(cw * 2f, lineH))) { PacketDraft.AddItem(f).Expanded = true; f.Expanded = true; }
                dl.AddText(new Vector2(lx + cw * 0.5f, textY), Theme.ToU32(ImGuiNET.ImGui.IsItemHovered() ? Theme.P.Fg : Theme.P.Muted), "+");
                lx += cw * 2f;
            }
            if (isItem) remove = RemoveMark(dl, lx, pos.Y, textY, lineH, cw);
            ImGuiNET.ImGui.SetCursorScreenPos(pos);
            ImGuiNET.ImGui.Dummy(new Vector2(1f, rowH - ImGuiNET.ImGui.GetStyle().ItemSpacing.Y));
            ImGuiNET.ImGui.PopID();
            return remove;
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
            return false;
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
        if (isItem)
        {
            ImGuiNET.ImGui.SameLine(0f, 10f * D);
            var rx = ImGuiNET.ImGui.GetCursorScreenPos().X;
            remove = RemoveMark(dl, rx, pos.Y, textY, lineH, cw);
        }
        ImGuiNET.ImGui.NewLine();   // Text() leaves the line open (SameLine): close it, else the row's Dummy inherits the control's line
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        ImGuiNET.ImGui.Dummy(new Vector2(1f, rowH - ImGuiNET.ImGui.GetStyle().ItemSpacing.Y));
        ImGuiNET.ImGui.PopID();
        return remove;
    }

    // The × that removes a list item; true when clicked.
    private static bool RemoveMark(ImDrawListPtr dl, float x, float y, float textY, float lineH, float cw)
    {
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, y));
        var clicked = ImGuiNET.ImGui.InvisibleButton("##remove", new Vector2(cw * 2f, lineH));
        dl.AddText(new Vector2(x + cw * 0.5f, textY), Theme.ToU32(ImGuiNET.ImGui.IsItemHovered() ? Theme.P.Red : Theme.P.Muted), "×");
        return clicked;
    }

    private static string TypeLabel(DraftField f) => NetDecode.TypeLabel(f.Type) ?? "";

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



    // ── the type picker: the "new packet" tab before a type is chosen ─────────────────────

    private static bool   _picking;
    private static string _pickText = "";
    private static bool   _pickFocus;
    private static int    _pickSel;
    private static bool   _pickScroll;
    private static readonly List<string> _recentTypes = new();
    private const int RecentMax = 6;

    private static void OpenPicker()
    {
        _picking   = true;
        _pickText  = "";
        _pickSel   = 0;
        _pickFocus = true;
        _tabKind   = TabKind.Pick;
    }

    private static void ClosePicker()
    {
        _picking = false;
        if (_tabKind == TabKind.Pick) ShowList();
    }

    private static void PickType(string name)
    {
        if (PacketDraft.ForType(name) is not { } d)
        {
            ImGuiRenderer.ShowToast("network", $"{name}: type not found", 2f);
            return;
        }
        _recentTypes.Remove(name);
        _recentTypes.Insert(0, name);
        if (_recentTypes.Count > RecentMax) _recentTypes.RemoveAt(_recentTypes.Count - 1);
        _picking = false;
        ShowDraft(d);
    }

    private static void DrawPickTab(float width, float height)
    {
        BeginPanel("##pick", width, height);
        var dl    = ImGuiNET.ImGui.GetWindowDrawList();
        var pos   = ImGuiNET.ImGui.GetCursorScreenPos();
        var w     = ImGuiNET.ImGui.GetContentRegionAvail().X;
        var h     = ImGuiNET.ImGui.GetFrameHeight();
        var pad   = ImGuiNET.ImGui.GetStyle().FramePadding;
        var lineH = ImGuiNET.ImGui.GetTextLineHeight();
        var small = ImGuiNET.ImGui.GetFontSize() * 0.8f;
        var font  = ImGuiNET.ImGui.GetFont();

        var all = NetTypes.All;
        var q   = _pickText.Trim();
        List<(string Name, NetTypes.Kind Kind)> hits;
        if (q.Length == 0)
            hits = _recentTypes.Select(n => (n, all.FirstOrDefault(t => t.Name == n).Kind)).Where(t => all.Any(a => a.Name == t.n)).ToList();
        else
            hits = all.Where(t => t.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(t => t.Name, StringComparer.Ordinal)
                .ToList();
        var recentN = q.Length == 0 ? hits.Count : 0;
        if (q.Length == 0) hits.AddRange(all.OrderBy(t => t.Name, StringComparer.Ordinal));

        // The search field, as the bar: › prompt, a hairline, the focus ring; the count at the right.
        var bMin = UiScale.Snap(pos);
        var bMax = UiScale.Snap(pos + new Vector2(w, h));
        dl.AddRectFilled(bMin, bMax, Theme.ToU32(Theme.P.Deep), 10f * D);
        var x = pos.X + 12f * D;
        dl.AddText(new Vector2(x, pos.Y + pad.Y), Theme.ToU32(Theme.P.Accent), "›");
        x += ImGuiNET.ImGui.CalcTextSize("›").X + 10f * D;
        var count  = all.Count == 0 ? (NetTypes.Failed ? "reading the types failed" : "reading the types…")
                   : q.Length == 0 ? $"{all.Count:N0}" : $"{hits.Count:N0} of {all.Count:N0}";
        var countW = ImGuiNET.ImGui.CalcTextSize(count).X * 0.8f;
        dl.AddText(font, small, new Vector2(bMax.X - 14f * D - countW, pos.Y + (h - small) * 0.5f), Theme.ToU32(Theme.P.Muted), count);

        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, pos.Y));
        ImGuiNET.ImGui.SetNextItemWidth(MathF.Max(bMax.X - countW - 28f * D - x, 40f));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        if (_pickFocus) { ImGuiNET.ImGui.SetKeyboardFocusHere(); _pickFocus = false; }
        var io   = ImGuiNET.ImGui.GetIO();
        var keep = io.ConfigInputTextEnterKeepActive;
        io.ConfigInputTextEnterKeepActive = true;
        var before = _pickText;
        var enter  = ImGuiNET.ImGui.InputText("##pick-search", ref _pickText, 128, ImGuiInputTextFlags.EnterReturnsTrue);
        io.ConfigInputTextEnterKeepActive = keep;
        var active = ImGuiNET.ImGui.IsItemActive();
        ImGuiNET.ImGui.PopStyleColor(3);
        dl.AddRect(bMin, bMax, Theme.ToU32(Theme.Hairline), 10f * D, ImDrawFlags.None, 1f / UiScale.K);
        Gfx.FocusRing(dl, bMin, bMax, 10f * D, Widgets.Hover(ImGuiNET.ImGui.GetID("##pick-search"), active));
        if (before != _pickText) { _pickSel = 0; _pickScroll = true; }

        // ↑ ↓ move the pick, Enter takes it, Escape closes the tab.
        if (active || ImGuiNET.ImGui.IsWindowFocused(ImGuiFocusedFlags.ChildWindows))
        {
            if (ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.DownArrow)) { _pickSel++; _pickScroll = true; }
            if (ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.UpArrow))   { _pickSel--; _pickScroll = true; }
            if (ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.Escape, false)) ClosePicker();
        }
        _pickSel = hits.Count == 0 ? 0 : Math.Clamp(_pickSel, 0, hits.Count - 1);
        string? pick = enter && hits.Count > 0 ? hits[_pickSel].Name : null;

        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        ImGuiNET.ImGui.Dummy(new Vector2(w, h));
        ImGuiNET.ImGui.Dummy(new Vector2(0f, 6f * D));

        // The rows: a dot for the kind, the name with the typed part tinted, the namespace in
        // grey, the usual direction at the end.
        ImGuiNET.ImGui.BeginChild("##pick-list", new Vector2(-1f, -1f), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollWithMouse);
        SmoothScroll.Update();
        var rowH   = 36f;
        var rowW   = ImGuiNET.ImGui.GetContentRegionAvail().X;
        var rdl    = ImGuiNET.ImGui.GetWindowDrawList();
        var mouse  = io.MousePos;
        var canHover = ImGuiNET.ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        if (hits.Count == 0 && all.Count > 0) Text("no type matches", Theme.P.Muted);
        for (var i = 0; i < hits.Count; i++)
        {
            if (q.Length == 0 && (i == 0 && recentN > 0 || i == recentN)) PanelCaption(i == 0 && recentN > 0 ? "recent" : "all");
            var rpos = ImGuiNET.ImGui.GetCursorScreenPos();
            if (_pickScroll && i == _pickSel) { ImGuiNET.ImGui.SetScrollHereY(0.5f); _pickScroll = false; }
            if (!ImGuiNET.ImGui.IsRectVisible(new Vector2(1f, rowH))) { ImGuiNET.ImGui.Dummy(new Vector2(1f, rowH)); continue; }
            var (name, kind) = hits[i];
            var over = canHover && mouse.X >= rpos.X && mouse.X < rpos.X + rowW && mouse.Y >= rpos.Y && mouse.Y < rpos.Y + rowH;
            if (over && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left)) pick = name;
            var rMin = rpos; var rMax = rpos + new Vector2(rowW, rowH);
            if (i == _pickSel)  Gfx.GradientRect(rdl, rMin, rMax, Theme.Sel, Theme.SelR, 7f);
            else if (over)      rdl.AddRectFilled(rMin, rMax, Theme.ToU32(Theme.P.Raised), 7f);

            var ty = rpos.Y + (rowH - lineH) * 0.5f;
            var kx = rpos.X + 12f * D;
            rdl.AddCircleFilled(new Vector2(kx + 4f, rpos.Y + rowH * 0.5f), 4f, Theme.ToU32(KindColor(kind)));
            var tx = kx + 20f * D;
            var fg = Theme.ToU32(i == _pickSel ? Theme.P.Fg : Theme.P.Sub);
            var hit = q.Length > 0 ? name.IndexOf(q, StringComparison.OrdinalIgnoreCase) : -1;
            if (hit < 0) { rdl.AddText(new Vector2(tx, ty), fg, name); tx += ImGuiNET.ImGui.CalcTextSize(name).X; }
            else
            {
                var a = name[..hit]; var b = name.Substring(hit, q.Length); var c = name[(hit + q.Length)..];
                rdl.AddText(new Vector2(tx, ty), fg, a); tx += ImGuiNET.ImGui.CalcTextSize(a).X;
                rdl.AddText(new Vector2(tx, ty), Theme.ToU32(Theme.P.Accent2), b); tx += ImGuiNET.ImGui.CalcTextSize(b).X;
                rdl.AddText(new Vector2(tx, ty), fg, c); tx += ImGuiNET.ImGui.CalcTextSize(c).X;
            }
            if (NetTypes.Resolve(name)?.Namespace is { Length: > 0 } ns)
            {
                rdl.PushClipRect(new Vector2(tx, rpos.Y), new Vector2(rMax.X - 50f * D, rMax.Y), true);
                rdl.AddText(font, small, new Vector2(tx + 14f * D, rpos.Y + (rowH - small) * 0.5f), Theme.ToU32(Theme.WithAlpha(Theme.P.Muted, 0.8f)), ns);
                rdl.PopClipRect();
            }
            var dir   = NetActive.DirectionOf(name);
            var arrow = dir is null ? "↑?" : dir == NetDir.In ? "↓" : "↑";
            rdl.AddText(new Vector2(rMax.X - 14f * D - ImGuiNET.ImGui.CalcTextSize(arrow).X, ty),
                Theme.ToU32(dir == NetDir.In ? Theme.P.Accent : dir is null ? Theme.P.Muted : Theme.P.Accent2), arrow);
            ImGuiNET.ImGui.Dummy(new Vector2(rowW, rowH));
        }
        ImGuiNET.ImGui.EndChild();
        ImGuiNET.ImGui.EndChild();

        if (pick is not null) PickType(pick);
    }

    private static Vector4 KindColor(NetTypes.Kind k) => k switch
    {
        NetTypes.Kind.Event => Theme.P.Accent2,
        NetTypes.Kind.Bui   => Theme.P.Link,
        _                   => Theme.P.Accent,
    };

    // ── saving ───────────────────────────────────────────────────────────────────────────────

    private static void SaveDraft(PacketDraft d)
    {
        var name = d.TypeName;
        var n = 2;
        while (NetActive.Saved.Any(s => s.Name == name)) name = $"{d.TypeName} {n++}";
        var s = SavedPacket.FromDraft(d, name);
        NetActive.Saved.Add(s);
        NetFeature.StoreSaved();
        ImGuiRenderer.ShowToast("network", s.NoSnapshot ? $"saved as “{name}” (fields the editor doesn't show won't be kept: too big)" : $"saved as “{name}”", s.NoSnapshot ? 3.5f : 2f);
    }
}
