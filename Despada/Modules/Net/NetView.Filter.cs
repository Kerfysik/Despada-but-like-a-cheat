// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using System.Text;
using Despada.ImGui;
using Despada.ImGui.Hook;
using Despada.ImGui.UserInterface;
using ImGuiNET;

namespace Despada.Modules.Net;

// The filter bar: the word being typed (completions below it) and, flush with the bar's right end,
// a "N filters ▾" button whose panel lists the committed tokens by kind (click one to negate it,
// × to drop it) plus the presets. Space or Enter commits the typed word, Tab takes the highlighted
// completion, ↑/↓ pick one, Backspace on an empty field takes the last token back, Esc clears.
internal static unsafe partial class NetView
{
    private static readonly List<NetQuery.Token> _chips = new();

    // A token field's state: the bar's field, and the search field in the filters panel. One
    // callback serves both (_cb is the one being drawn).
    private sealed class TokenBox
    {
        public string Text = "";
        public List<(string Insert, string Hint)> Sug = new();
        public int     SugIndex;
        public string? ReplaceWith;
        public bool    WasEmpty = true;
    }

    private static readonly TokenBox _bar = new(), _panel = new();
    private static TokenBox _cb = _bar;

    private static readonly ImGuiInputTextCallback _filterCallback = FilterCallback;
    private static bool    _filterActive;
    private static bool    _focusFilter;
    private static bool    _focusPanel;
    private static Vector2 _sugAt;

    // Where the completion list was drawn last frame. It's drawn over everything, so clicks are
    // hit-tested here (before the field loses focus to the click) and the rows under it ignore the mouse.
    private static Vector2 _sugMin, _sugMax;
    private static float   _sugRowH;
    private static bool    _sugShown;

    private static bool MouseOverSuggestions
    {
        get
        {
            if (!_sugShown) return false;
            var m = ImGuiNET.ImGui.GetIO().MousePos;
            return m.X >= _sugMin.X && m.X < _sugMax.X && m.Y >= _sugMin.Y && m.Y < _sugMax.Y;
        }
    }

    private static NetQuery CurrentQuery(out string key)
    {
        var q = new NetQuery();
        q.Tokens.AddRange(_chips);
        // The word being typed already filters, so the list follows the typing; not while it's
        // still a bare "-", "@" or the start of a key ("si" on the way to "size:").
        var word = _bar.Text.Trim();
        var partialKey = word.TrimStart('-') is var w && (w.Length == 0 || w == "@" ||
                         NetQuery.Keys.Any(k => k.StartsWith(w, StringComparison.OrdinalIgnoreCase) && !w.Contains(':')));
        if (!partialKey && !_bar.Text.EndsWith(':') && NetQuery.ParseToken(word) is { } live) q.Tokens.Add(live);
        key = string.Join(' ', q.Tokens);
        return q;
    }

    private static void DrawFilterBar(float width)
    {
        // A click on last frame's completion list picks that completion.
        if (_sugShown && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left) && MouseOverSuggestions)
        {
            var i = (int)((ImGuiNET.ImGui.GetIO().MousePos.Y - _sugMin.Y) / _sugRowH);
            if (i >= 0 && i < _bar.Sug.Count)
            {
                _bar.ReplaceWith = _bar.Sug[i].Insert;
                _focusFilter = true;
            }
        }

        var dl  = ImGuiNET.ImGui.GetWindowDrawList();
        var pos = ImGuiNET.ImGui.GetCursorScreenPos();
        var h   = ImGuiNET.ImGui.GetFrameHeight();
        var r   = 10f * D;
        // Box on the framebuffer grid: the 1 px outline's straight runs then sit on one pixel row.
        var boxMin = UiScale.Snap(pos);
        var boxMax = UiScale.Snap(pos + new Vector2(width, h));
        dl.AddRectFilled(boxMin, boxMax, Theme.ToU32(Theme.P.Deep), r);

        var pad = ImGuiNET.ImGui.GetStyle().FramePadding;
        var x   = pos.X + 12f * D;
        // A prompt, as in the console.
        dl.AddText(new Vector2(x, pos.Y + pad.Y), Theme.ToU32(Theme.P.Accent), "›");
        x += ImGuiNET.ImGui.CalcTextSize("›").X + 10f * D;

        // The filters button, part of the bar: its right end, full height, the bar's corners.
        var buttonX = DrawFiltersButton(dl, boxMin, boxMax, r);
        // The outline last, over the button's fill, so it stays one unbroken line around both.
        dl.AddRect(boxMin, boxMax, Theme.ToU32(Theme.P.Sep), r, ImDrawFlags.None, 1f / UiScale.K);

        // The text field fills the rest of the bar, frameless.
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, pos.Y));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGuiNET.ImGui.SetNextItemWidth(MathF.Max(buttonX - x - 8f * D, 40f));
        if (_focusFilter)
        {
            ImGuiNET.ImGui.SetKeyboardFocusHere();
            _focusFilter = false;
        }

        var io = ImGuiNET.ImGui.GetIO();
        var keep = io.ConfigInputTextEnterKeepActive;
        io.ConfigInputTextEnterKeepActive = true; // Enter makes a chip, the field stays focused
        _cb = _bar;
        ImGuiNET.ImGui.InputText("##netfilter", ref _bar.Text, 256,
            ImGuiInputTextFlags.CallbackAlways | ImGuiInputTextFlags.CallbackCompletion | ImGuiInputTextFlags.EscapeClearsAll,
            _filterCallback);
        io.ConfigInputTextEnterKeepActive = keep;
        _filterActive = ImGuiNET.ImGui.IsItemActive();
        ImGuiNET.ImGui.PopStyleColor(3);

        // Completions for the word under the caret, shown under it.
        var word = CurrentWord(_bar.Text);
        if (_bar.ReplaceWith is null)
            _bar.Sug = _filterActive && (word.Length > 0 || _chips.Count == 0)
                ? NetQuery.Suggest(word, _knownTypes.OrderBy(t => t), KnownEntities(word)).Take(10).ToList()
                : [];
        if (_bar.Sug.Count == 1 && _bar.Sug[0].Insert == word) _bar.Sug.Clear();
        _bar.SugIndex = Math.Clamp(_bar.SugIndex, 0, Math.Max(0, _bar.Sug.Count - 1));
        _sugAt = new Vector2(x + ImGuiNET.ImGui.CalcTextSize(_bar.Text[..^word.Length]).X + pad.X, pos.Y + h + 4f * D);

        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        ImGuiNET.ImGui.Dummy(new Vector2(width, h));
    }

    /// <summary>Draws the bar's "N filters ▾" end and its panel; returns the button's left x.</summary>
    private static float DrawFiltersButton(ImDrawListPtr dl, Vector2 boxMin, Vector2 boxMax, float r)
    {
        var n      = _chips.Count;
        var label  = n == 0 ? "filters" : n == 1 ? "1 filter" : $"{n} filters";
        var padX   = 12f * D;
        var labelW = ImGuiNET.ImGui.CalcTextSize(label).X;
        var w      = padX + labelW + 10f * D + ImGuiNET.ImGui.CalcTextSize("▾").X + padX;
        var min    = new Vector2(UiScale.Snap(boxMax.X - w), boxMin.Y);

        ImGuiNET.ImGui.SetCursorScreenPos(min);
        if (ImGuiNET.ImGui.InvisibleButton("##filters", boxMax - min)) ImGuiNET.ImGui.OpenPopup("##filters-panel");
        var hovered = ImGuiNET.ImGui.IsItemHovered();
        var open    = ImGuiNET.ImGui.IsPopupOpen("##filters-panel");

        // To the bar's edge with its corners; the bar's outline is drawn over it afterwards.
        var fill = n > 0 ? Theme.Sel : hovered || open ? Theme.P.Raised : Vector4.Zero;
        if (fill.W > 0f)
            dl.AddRectFilled(min, boxMax, Theme.ToU32(fill), r, ImDrawFlags.RoundCornersRight);
        Gfx.VLine(dl, min.X, boxMin.Y, boxMax.Y, Theme.ToU32(Theme.P.Sep));
        var ty = boxMin.Y + (boxMax.Y - boxMin.Y - ImGuiNET.ImGui.GetTextLineHeight()) * 0.5f;
        dl.AddText(new Vector2(min.X + padX, ty), Theme.ToU32(n > 0 || hovered ? Theme.P.Fg : Theme.P.Muted), label);
        dl.AddText(new Vector2(min.X + padX + labelW + 10f * D, ty), Theme.ToU32(Theme.P.Muted), "▾");

        DrawFiltersPanel(boxMax);
        return min.X;
    }

    // Token kinds in the panel's order, with their captions.
    private static readonly (string Key, string Caption)[] Groups =
    [
        ("type", "type"), ("dir", "direction"), ("entity", "entity"), ("size", "size"),
        ("tick", "tick"), ("since", "since"), ("", "text"), ("@", "preset"),
    ];

    private static void DrawFiltersPanel(Vector2 barMax)
    {
        ImGuiNET.ImGui.SetNextWindowPos(barMax + new Vector2(0f, 6f * D), ImGuiCond.Always, new Vector2(1f, 0f));
        ImGuiNET.ImGui.SetNextWindowSizeConstraints(new Vector2(350f * D, 0f), new Vector2(700f * D, 900f * D));
        if (!ImGuiNET.ImGui.BeginPopup("##filters-panel"))
        {
            _panel.Text = "";
            _panel.Sug.Clear();
            _panel.ReplaceWith = null;
            _panel.SugIndex = 0;
            _panel.WasEmpty = true;
            return;
        }

        // Search: narrows the filters and presets shown, with the bar's completions (Tab or a click
        // takes one, ↑/↓ pick); Enter adds what was typed as filters, or toggles the one preset it names.
        if (ImGuiNET.ImGui.IsWindowAppearing()) _focusPanel = true;
        ImGuiNET.ImGui.SetNextItemWidth(-1f);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, Theme.P.Deep);
        if (_focusPanel)
        {
            ImGuiNET.ImGui.SetKeyboardFocusHere();
            _focusPanel = false;
        }
        var io   = ImGuiNET.ImGui.GetIO();
        var keep = io.ConfigInputTextEnterKeepActive;
        io.ConfigInputTextEnterKeepActive = true;   // Enter commits, the field stays active
        _cb = _panel;
        ImGuiNET.ImGui.InputText("##panel-search", ref _panel.Text, 128,
            ImGuiInputTextFlags.CallbackAlways | ImGuiInputTextFlags.CallbackCompletion, _filterCallback);
        io.ConfigInputTextEnterKeepActive = keep;
        _cb = _bar;
        ImGuiNET.ImGui.PopStyleColor();

        var pword = CurrentWord(_panel.Text);
        // From the text alone (not "while active"): a click on a suggestion deactivates the field first.
        if (_panel.ReplaceWith is null)
            _panel.Sug = pword.Length > 0
                ? NetQuery.Suggest(pword, _knownTypes.OrderBy(t => t), KnownEntities(pword)).Take(8).ToList()
                : [];
        if (_panel.Sug.Count == 1 && _panel.Sug[0].Insert == pword) _panel.Sug.Clear();
        _panel.SugIndex = Math.Clamp(_panel.SugIndex, 0, Math.Max(0, _panel.Sug.Count - 1));
        for (var i = 0; i < _panel.Sug.Count; i++)
        {
            var (insert, hint) = _panel.Sug[i];
            ImGuiNET.ImGui.PushID(i);
            if (ImGuiNET.ImGui.Selectable($"{insert}   {hint}##sug", i == _panel.SugIndex, ImGuiSelectableFlags.NoAutoClosePopups))
            {
                _panel.ReplaceWith = insert;
                _focusPanel = true;
                ImGuiInputHook.ClaimKeyboard();   // the field is inactive until refocused
            }
            ImGuiNET.ImGui.PopID();
        }
        if (_panel.Sug.Count > 0) ImGuiNET.ImGui.Separator();

        var q = _panel.Text.Trim();
        bool Shown(string s) => q.Length == 0 || s.Contains(q, StringComparison.OrdinalIgnoreCase);

        // Values are drawn straight to the draw list, which ImGui doesn't measure: make room for
        // the widest so the popup grows to fit it.
        var widest = _chips.Select(c => ImGuiNET.ImGui.CalcTextSize("− @" + c.Value).X).DefaultIfEmpty(0f).Max();
        ImGuiNET.ImGui.Dummy(new Vector2(widest + ImGuiNET.ImGui.CalcTextSize("×").X + 32f * D, 0f));

        var remove = -1;
        var negate = -1;
        var anyShown = false;
        foreach (var (key, caption) in Groups)
        {
            var any = false;
            for (var i = 0; i < _chips.Count; i++)
            {
                if (_chips[i].Key != key || !Shown(_chips[i].ToString())) continue;
                anyShown = true;
                if (!any)
                {
                    PanelCaption(caption);
                    any = true;
                }
                ImGuiNET.ImGui.PushID(i);
                switch (FilterRow(_chips[i]))
                {
                    case 1: negate = i; break;
                    case 2: remove = i; break;
                }
                ImGuiNET.ImGui.PopID();
            }
        }
        if (_chips.Count == 0)
            PanelCaption("none");
        else if (!anyShown)
            PanelCaption("no match");

        // Presets: on/off with a click.
        ImGuiNET.ImGui.Separator();
        foreach (var (name, _, _) in NetQuery.Presets)
        {
            if (!Shown("@" + name)) continue;
            // Any token of the preset counts (also a negated one): a click takes it off.
            var on = _chips.Any(c => c.Key == "@" && c.Value == name);
            var negated = on && _chips.Any(c => c.Key == "@" && c.Value == name && c.Negated);
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, negated ? Theme.P.Red : on ? Theme.P.Accent2 : Theme.P.Muted);
            if (ImGuiNET.ImGui.SmallButton("@" + name)) TogglePreset(name);
            ImGuiNET.ImGui.PopStyleColor();
            ImGuiNET.ImGui.SameLine();
        }
        ImGuiNET.ImGui.NewLine();
        if (_chips.Count > 0 && q.Length == 0)   // while searching it would also take the hidden ones
        {
            ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Theme.P.Muted);
            if (ImGuiNET.ImGui.SmallButton("clear all")) _chips.Clear();
            ImGuiNET.ImGui.PopStyleColor();
        }
        ImGuiNET.ImGui.EndPopup();

        if (negate >= 0) _chips[negate] = _chips[negate] with { Negated = !_chips[negate].Negated };
        if (remove >= 0) _chips.RemoveAt(remove);
    }

    private static void TogglePreset(string name, bool negated = false)
    {
        if (_chips.Any(c => c.Key == "@" && c.Value == name)) _chips.RemoveAll(c => c.Key == "@" && c.Value == name);
        else                                                  _chips.Add(new NetQuery.Token("@", name, negated));
    }

    // The preset a typed word names: its exact name (with or without @), or a prefix after "@" that
    // fits only one. A leading "-" asks for the negated preset.
    private static (string Name, bool Negated)? PresetNamed(string word)
    {
        if (word.Length == 0 || word.Contains(' ') || word.Contains(':')) return null;
        var negated = word.StartsWith('-');
        var body    = negated ? word[1..] : word;
        var at      = body.StartsWith('@');
        body = body.TrimStart('@');
        if (body.Length == 0) return null;
        var exact = NetQuery.Presets.Where(p => p.Name.Equals(body, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return (exact[0].Name, negated);
        if (!at) return null;
        var prefix = NetQuery.Presets.Where(p => p.Name.StartsWith(body, StringComparison.OrdinalIgnoreCase)).ToList();
        return prefix.Count == 1 ? (prefix[0].Name, negated) : null;
    }

    private static void PanelCaption(string text)
    {
        Text(text, Theme.P.Muted);
        ImGuiNET.ImGui.NewLine();
    }

    /// <summary>One token in the panel: 1 when its value was clicked (negate), 2 when its × was.</summary>
    private static int FilterRow(NetQuery.Token t)
    {
        var dl    = ImGuiNET.ImGui.GetWindowDrawList();
        var lineH = ImGuiNET.ImGui.GetTextLineHeightWithSpacing();
        var xW    = ImGuiNET.ImGui.CalcTextSize("×").X + 16f * D;
        var rowW  = ImGuiNET.ImGui.GetContentRegionAvail().X;
        var pos   = ImGuiNET.ImGui.GetCursorScreenPos();
        var result = 0;

        if (ImGuiNET.ImGui.InvisibleButton("##v", new Vector2(MathF.Max(rowW - xW, 1f), lineH))) result = 1;
        var hovered = ImGuiNET.ImGui.IsItemHovered();
        ImGuiNET.ImGui.SameLine(0f, 0f);
        if (ImGuiNET.ImGui.InvisibleButton("##x", new Vector2(xW, lineH))) result = 2;
        var xHovered = ImGuiNET.ImGui.IsItemHovered();

        if (hovered || xHovered)
            dl.AddRectFilled(pos, pos + new Vector2(rowW, lineH), Theme.ToU32(Theme.P.Raised), 6f * D);
        var ty = pos.Y + (lineH - ImGuiNET.ImGui.GetTextLineHeight()) * 0.5f;
        var x  = pos.X + 8f * D;
        if (t.Negated)
        {
            dl.AddText(new Vector2(x, ty), Theme.ToU32(Theme.P.Red), "−");
            x += ImGuiNET.ImGui.CalcTextSize("− ").X;
        }
        var valueColor = !NetQuery.IsValid(t) ? Theme.P.Red : t.Key switch
        {
            "type" => Theme.P.Accent,
            "dir"  => t.Value is "in" or "↓" ? Theme.P.Accent : Theme.P.Accent2,
            "@"    => Theme.P.Accent2,
            _      => Theme.P.Fg,
        };
        var value = t.Key == "@" ? "@" + t.Value : t.Value;
        dl.AddText(new Vector2(x, ty), Theme.ToU32(t.Negated ? Theme.P.Sub : valueColor), value);
        if (t.Negated)
            Gfx.HLine(dl, x, x + ImGuiNET.ImGui.CalcTextSize(value).X, ty + ImGuiNET.ImGui.GetTextLineHeight() * 0.55f,
                Theme.ToU32(Theme.P.Muted));
        dl.AddText(new Vector2(pos.X + rowW - xW + 8f * D, ty), Theme.ToU32(xHovered ? Theme.P.Fg : Theme.P.Muted), "×");
        return result;
    }

    private static void DrawSuggestions()
    {
        _sugShown = _bar.Sug.Count > 0;
        if (!_sugShown) return;

        var dl    = ImGuiNET.ImGui.GetForegroundDrawList();
        var rowH  = ImGuiNET.ImGui.GetTextLineHeight() + 8f * D;
        var padX  = 12f * D;
        var nameW = _bar.Sug.Max(s => ImGuiNET.ImGui.CalcTextSize(s.Insert).X) + 24f * D;
        var hintW = _bar.Sug.Max(s => ImGuiNET.ImGui.CalcTextSize(s.Hint).X);
        // On the framebuffer grid, so the 1 px outline is one pixel row (see DrawFilterBar).
        var min   = UiScale.Snap(_sugAt);
        var size  = UiScale.Snap(min + new Vector2(nameW + hintW + padX * 2f, rowH * _bar.Sug.Count)) - min;
        var r     = 10f * D;
        _sugMin = min;
        _sugMax = min + size;
        _sugRowH = rowH;

        dl.AddRectFilled(min, min + size, Theme.ToU32(Theme.P.Pop), r);
        var flags = _bar.Sug.Count == 1 ? ImDrawFlags.RoundCornersAll
                  : _bar.SugIndex == 0 ? ImDrawFlags.RoundCornersTop
                  : _bar.SugIndex == _bar.Sug.Count - 1 ? ImDrawFlags.RoundCornersBottom
                  : ImDrawFlags.RoundCornersNone;
        var hl = new Vector2(min.X, min.Y + rowH * _bar.SugIndex);
        dl.AddRectFilled(hl, hl + new Vector2(size.X, rowH), Theme.ToU32(Theme.Sel), r, flags);

        for (var i = 0; i < _bar.Sug.Count; i++)
        {
            var y = min.Y + rowH * i + 4f * D;
            var (insert, hint) = _bar.Sug[i];
            dl.AddText(new Vector2(min.X + padX, y), Theme.ToU32(insert.Contains('@') ? Theme.P.Accent2 : Theme.P.Fg), insert);
            dl.AddText(new Vector2(min.X + padX + nameW, y), Theme.ToU32(i == _bar.SugIndex ? Theme.P.Sub : Theme.P.Muted), hint);
        }
        dl.AddRect(min, min + size, Theme.ToU32(Theme.P.Sep), r, ImDrawFlags.None, 1f / UiScale.K);
    }

    // ── text field callback ──────────────────────────────────────────────────────────────────

    private static string CurrentWord(string text)
    {
        var i = text.LastIndexOf(' ');
        return i < 0 ? text : text[(i + 1)..];
    }

    private static int FilterCallback(ImGuiInputTextCallbackData* raw)
    {
        var data = new ImGuiInputTextCallbackDataPtr(raw);
        var text = Encoding.UTF8.GetString((byte*)data.Buf, data.BufTextLen);

        // A completion clicked with the mouse.
        if (_cb.ReplaceWith is { } chosen)
        {
            _cb.ReplaceWith = null;
            Accept(data, text, chosen);
            return 0;
        }

        if (ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.UpArrow) && _cb.Sug.Count > 0)
            _cb.SugIndex = (_cb.SugIndex - 1 + _cb.Sug.Count) % _cb.Sug.Count;
        else if (ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.DownArrow) && _cb.Sug.Count > 0)
            _cb.SugIndex = (_cb.SugIndex + 1) % _cb.Sug.Count;

        if (data.EventFlag == ImGuiInputTextFlags.CallbackCompletion)
        {
            if (_cb.Sug.Count > 0) Accept(data, text, _cb.Sug[_cb.SugIndex].Insert);
        }
        else if (ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.KeypadEnter))
        {
            Commit(data, text, enter: true);
        }
        else if (text.EndsWith(' '))
        {
            Commit(data, text);
        }
        // A fresh press only: held down, it would walk through filters that aren't on screen.
        else if (_cb == _bar && ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.Backspace, false) && text.Length == 0 && _cb.WasEmpty && _chips.Count > 0)
        {
            // Backspace on an empty field takes the last chip back into the field for editing.
            var last = _chips[^1];
            _chips.RemoveAt(_chips.Count - 1);
            data.InsertChars(0, last.ToString());
        }

        _cb.WasEmpty = data.BufTextLen == 0;
        return 0;
    }

    // Replaces the word being typed; a finished token becomes a chip right away.
    private static void Accept(ImGuiInputTextCallbackDataPtr data, string text, string insert)
    {
        var word = CurrentWord(text);
        var head = text[..^word.Length];
        var done = !insert.EndsWith(':') && insert != "@" && insert != "-@";
        Replace(data, head + insert + (done ? " " : ""));
        if (done) Commit(data, head + insert + " ");
        _cb.SugIndex = 0;
    }

    // Every complete word in the field becomes a chip; the field is emptied.
    private static void Commit(ImGuiInputTextCallbackDataPtr data, string text, bool enter = false)
    {
        // In the panel, Enter on a preset's name toggles it ("quiet", "-ui"); "@q…" also takes a
        // prefix that names exactly one. Space and accepted completions just add tokens.
        if (_cb == _panel && enter && PresetNamed(text.Trim()) is { } preset)
            TogglePreset(preset.Name, preset.Negated);
        else
            foreach (var w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (NetQuery.ParseToken(w) is { } t) AddChip(t);
        Replace(data, "");
        _cb.SugIndex = 0;
    }

    private static void Replace(ImGuiInputTextCallbackDataPtr data, string text)
    {
        data.DeleteChars(0, data.BufTextLen);
        if (text.Length > 0) data.InsertChars(0, text);
    }
}
