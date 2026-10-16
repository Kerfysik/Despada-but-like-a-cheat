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

internal static unsafe partial class NetView
{
    private static readonly List<NetQuery.Token> _chips = [new NetQuery.Token("@", "quiet", false)];

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
    private static float   _sugRowsY;   // the first row's top (the box has padding above it)
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
        // A "/command" being typed is not a filter: the list must not empty under it.
        if (!partialKey && !_bar.Text.EndsWith(':') && !HasSlash(_bar.Text) && NetQuery.ParseToken(word) is { } live) q.Tokens.Add(live);
        key = string.Join(' ', q.Tokens);
        return q;
    }

    private static void DrawFilterBar(float width)
    {
        // A click on last frame's completion list picks that completion.
        if (_sugShown && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left) && MouseOverSuggestions)
        {
            var i = (int)MathF.Floor((ImGuiNET.ImGui.GetIO().MousePos.Y - _sugRowsY) / _sugRowH);
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
        // Before it, the match count: "12409/12409", muted.
        var count  = $"{_matches.Count}/{NetCapture.Count}";
        var countW = ImGuiNET.ImGui.CalcTextSize(count).X;
        dl.AddText(new Vector2(buttonX - 14f * D - countW, pos.Y + pad.Y), Theme.ToU32(Theme.P.Muted), count);
        buttonX -= countW + 14f * D;
        // The outline last, over the button's fill, so it stays one unbroken line around both;
        // the focus ring over that.
        dl.AddRect(boxMin, boxMax, Theme.ToU32(Theme.Hairline), r, ImDrawFlags.None, 1f / UiScale.K);
        Gfx.FocusRing(dl, boxMin, boxMax, r, Widgets.Hover(ImGuiNET.ImGui.GetID("##netfilter"), _filterActive));

        // The text field fills the rest with the bar, frameless.
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
            _bar.Sug = !_filterActive ? []
                     : word.StartsWith('/') ? SlashSuggest(word)
                     : HasSlash(_bar.Text) ? []   // a command's arguments: no filter completions
                     : word.Length > 0 || _chips.Count == 0 ? NetQuery.Suggest(word, TypeSource(), KnownEntities(word), NetCapture.TypeHint).Take(10).ToList()
                     : [];
        if (_bar.Sug.Count == 1 && _bar.Sug[0].Insert == word) _bar.Sug.Clear();
        _bar.SugIndex = Math.Clamp(_bar.SugIndex, 0, Math.Max(0, _bar.Sug.Count - 1));
        _sugAt = new Vector2(x + ImGuiNET.ImGui.CalcTextSize(_bar.Text[..^word.Length]).X + pad.X, pos.Y + h + 4f * D);

        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        ImGuiNET.ImGui.Dummy(new Vector2(width, h));
    }

    /// <summary>Draws the bar's funnel end and its panel; returns the button's left x.</summary>
    // A funnel with the filter count beside it, at the bar's right end.
    private static float DrawFiltersButton(ImDrawListPtr dl, Vector2 boxMin, Vector2 boxMax, float r)
    {
        var n      = _chips.Count;
        var padX   = 12f * D;
        var countW = n > 0 ? ImGuiNET.ImGui.CalcTextSize(n.ToString()).X + 8f : 0f;
        var w      = padX + 18f + countW + padX;
        var min    = new Vector2(UiScale.Snap(boxMax.X - w), boxMin.Y);

        ImGuiNET.ImGui.SetCursorScreenPos(min);
        if (ImGuiNET.ImGui.InvisibleButton("##filters", boxMax - min)) ImGuiNET.ImGui.OpenPopup("##filters-panel");
        var hovered = ImGuiNET.ImGui.IsItemHovered();
        var open    = ImGuiNET.ImGui.IsPopupOpen("##filters-panel");
        var hot     = Widgets.Hover(ImGuiNET.ImGui.GetItemID(), hovered || open);

        // To the bar's edge with its corners; the bar's outline is drawn over it afterwards.
        if (hot > 0.01f)
            dl.AddRectFilled(min, boxMax, Theme.ToU32(Theme.WithAlpha(Theme.RaisedFill, Theme.RaisedFill.W * hot)), r, ImDrawFlags.RoundCornersRight);
        var cy = (boxMin.Y + boxMax.Y) * 0.5f;
        Widgets.DrawIcon(dl, new Vector2(min.X + padX + 9f, cy), 18f, Icon.Funnel, Theme.Mix(Theme.P.Muted, Theme.P.Sub, hot));
        if (n > 0)
            dl.AddText(new Vector2(min.X + padX + 18f + 8f, cy - ImGuiNET.ImGui.GetTextLineHeight() * 0.5f), Theme.ToU32(Theme.P.Accent), n.ToString());

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

        // The field looks like the bar: a Deep box with the › prompt, a hairline, the focus ring.
        var dl    = ImGuiNET.ImGui.GetWindowDrawList();
        var pos   = ImGuiNET.ImGui.GetCursorScreenPos();
        var w     = ImGuiNET.ImGui.GetContentRegionAvail().X;
        var h     = ImGuiNET.ImGui.GetFrameHeight();
        var pad   = ImGuiNET.ImGui.GetStyle().FramePadding;
        var bMin  = UiScale.Snap(pos);
        var bMax  = UiScale.Snap(pos + new Vector2(w, h));
        dl.AddRectFilled(bMin, bMax, Theme.ToU32(Theme.P.Deep), 10f * D);
        var x = pos.X + 12f * D;
        dl.AddText(new Vector2(x, pos.Y + pad.Y), Theme.ToU32(Theme.P.Accent), "›");
        x += ImGuiNET.ImGui.CalcTextSize("›").X + 10f * D;

        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, pos.Y));
        ImGuiNET.ImGui.SetNextItemWidth(MathF.Max(bMax.X - x - 8f * D, 40f));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
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
        var active = ImGuiNET.ImGui.IsItemActive();
        _cb = _bar;
        ImGuiNET.ImGui.PopStyleColor(3);
        dl.AddRect(bMin, bMax, Theme.ToU32(Theme.Hairline), 10f * D, ImDrawFlags.None, 1f / UiScale.K);
        Gfx.FocusRing(dl, bMin, bMax, 10f * D, Widgets.Hover(ImGuiNET.ImGui.GetID("##panel-search"), active));
        ImGuiNET.ImGui.SetCursorScreenPos(pos);
        ImGuiNET.ImGui.Dummy(new Vector2(w, h));

        var pword = CurrentWord(_panel.Text);
        // From the text alone (not "while active"): a click on a suggestion deactivates the field first.
        if (_panel.ReplaceWith is null)
            _panel.Sug = pword.Length > 0
                ? NetQuery.Suggest(pword, TypeSource(), KnownEntities(pword), NetCapture.TypeHint).Take(8).ToList()
                : [];
        if (_panel.Sug.Count == 1 && _panel.Sug[0].Insert == pword) _panel.Sug.Clear();
        _panel.SugIndex = Math.Clamp(_panel.SugIndex, 0, Math.Max(0, _panel.Sug.Count - 1));
        if (_panel.Sug.Count > 0)
        {
            // The same completion list as under the bar, inline under the field; the
            // description stays inside the popup, so it goes to the right only when it fits.
            var at = new Vector2(x + ImGuiNET.ImGui.CalcTextSize(_panel.Text[..^pword.Length]).X + pad.X, pos.Y + h + 4f * D);
            Widgets.Completions(dl, at, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_panel.Sug), _panel.SugIndex, pword,
                out var sMin, out var sMax, out var sHover, shadow: false, limitX: bMax.X, limitMinX: bMin.X);
            if (sHover >= 0 && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _panel.ReplaceWith = _panel.Sug[sHover].Insert;
                _focusPanel = true;
                ImGuiInputHook.ClaimKeyboard();   // the field is inactive until refocused
            }
            ImGuiNET.ImGui.Dummy(new Vector2(1f, sMax.Y - pos.Y - h + 6f * D));
        }

        var q = _panel.Text.Trim();
        bool Shown(string s) => q.Length == 0 || s.Contains(q, StringComparison.OrdinalIgnoreCase);

        // Values are drawn straight to the draw list, which ImGui doesn't measure: make room for
        // the widest so the popup grows to fit it.
        var widest = _chips.Select(c => ImGuiNET.ImGui.CalcTextSize("− @" + c.Value).X).DefaultIfEmpty(0f).Max();
        ImGuiNET.ImGui.Dummy(new Vector2(widest + ImGuiNET.ImGui.CalcTextSize("×").X + 32f * D, 0f));

        var remove = -1;
        var negate = -1;
        foreach (var (key, caption) in Groups)
        {
            var any = false;
            for (var i = 0; i < _chips.Count; i++)
            {
                if (_chips[i].Key != key || !Shown(_chips[i].ToString())) continue;
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
        if (_chips.Count == 0 && _panel.Sug.Count == 0)
            PanelCaption("none");

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

    // Types seen in the capture first, then every other wire type the game defines; rebuilt only
    // when either list changes.
    private static string[] _typeSource = [];
    private static int _typeSourceSeen = -1;
    private static IReadOnlyList<string>? _typeSourceAll;

    private static IEnumerable<string> TypeSource()
    {
        var all = NetTypes.Names;
        if (_knownTypes.Count != _typeSourceSeen || !ReferenceEquals(all, _typeSourceAll))
        {
            _typeSourceSeen = _knownTypes.Count;
            _typeSourceAll  = all;
            _typeSource = _knownTypes.OrderBy(t => t, StringComparer.Ordinal)
                .Concat(all.Where(n => !_knownTypes.Contains(n))).ToArray();
        }
        return _typeSource;
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

        // The list under the caret: names only, the picked one's hint beside.
        Widgets.Completions(ImGuiNET.ImGui.GetForegroundDrawList(), _sugAt, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_bar.Sug),
            _bar.SugIndex, CurrentWord(_bar.Text), out _sugMin, out _sugMax, out _);
        _sugRowH  = Widgets.CompletionRowH;
        _sugRowsY = _sugMin.Y + Widgets.CompletionPad;
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
        // A "/command" in the bar makes a rule from the filter (Enter); a space only adds tokens.
        if (_cb == _bar && HasSlash(text))
        {
            if (!enter) return;
            if (RunSlash(text)) { Replace(data, ""); _cb.SugIndex = 0; }
            return;
        }
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

    // ── "/" commands: a rule from the current filter ─────────────────────────────────────────

    private static readonly (string Name, RuleAction? Action, string Hint)[] Slash =
    [
        ("/block",   RuleAction.Block,     "drop the matching packets"),
        ("/freeze",  RuleAction.Freeze,    "hold them in the frozen tab"),
        ("/dup",     RuleAction.Duplicate, "send copies: /dup 3 50"),
        ("/modify",  RuleAction.Modify,    "change fields: /modify Field=value"),
        ("/mute",    RuleAction.Mute,      "keep them out of the log (still delivered)"),
        ("/release", null,                 "let every frozen packet go"),
        ("/drop",    null,                 "throw every frozen packet away"),
        ("/clear",   null,                 "drop every filter"),
    ];

    private static List<(string Insert, string Hint)> SlashSuggest(string word)
        => Slash.Where(s => s.Name.StartsWith(word, StringComparison.OrdinalIgnoreCase)).Select(s => (s.Name, s.Hint)).ToList();

    // A word that starts a command (a "/" inside a path or a plain-text search is not one).
    private static bool HasSlash(string text)
        => text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(w => w.StartsWith('/') && Slash.Any(s => s.Name.StartsWith(w, StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// "type:X dir:out /block", "/dup 3 50", "/modify Range=0.35 Alpha=1": the tokens before the
    /// command join the chips as the rule's filter; the words after it are its parameters.
    /// </summary>
    private static bool RunSlash(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var at = words.FindIndex(w => w.StartsWith('/'));
        if (at < 0) return false;
        var cmd = Slash.FirstOrDefault(s => s.Name.Equals(words[at], StringComparison.OrdinalIgnoreCase));
        if (cmd.Name is null)
        {
            ImGuiRenderer.ShowToast("network", $"{words[at]}: no such command", 2f);
            return false;
        }
        if (cmd.Action is null)
        {
            switch (cmd.Name)
            {
                case "/clear":   _chips.Clear(); break;
                case "/release": NetActive.ReleaseAll(); break;
                case "/drop":    NetActive.DropAll(); break;
            }
            return true;
        }
        var tokens = new List<NetQuery.Token>(_chips);
        foreach (var w in words.Take(at))
            if (NetQuery.ParseToken(w) is { } t && !tokens.Contains(t)) tokens.Add(t);
        if (tokens.Count == 0)
        {
            ImGuiRenderer.ShowToast("network", "a rule needs a filter: type:… dir:… entity:…", 2.5f);
            return false;
        }
        var args = words.Skip(at + 1).ToList();
        var r = new NetRule { Action = cmd.Action.Value, Count = 3, EveryMs = 50 };
        if (r.Action == RuleAction.Duplicate)
        {
            if (args.Count > 0 && int.TryParse(args[0], out var n)) r.Count = Math.Clamp(n, 1, 100);
            if (args.Count > 1 && int.TryParse(args[1], out var ms)) r.EveryMs = Math.Clamp(ms, 0, 60_000);
        }
        else if (r.Action == RuleAction.Modify)
        {
            r.Modify = string.Join(";", args);
            if (args.Count == 0) { ImGuiRenderer.ShowToast("network", "/modify needs Field=value", 2.5f); return false; }
        }
        r.SetTokens(tokens);
        NetActive.Rules.Add(r);
        ImGuiRenderer.ShowToast("network", $"rule: {r.Filter} → {r.ActionLabel}", 2.5f);
        return true;
    }

    private static void Replace(ImGuiInputTextCallbackDataPtr data, string text)
    {
        data.DeleteChars(0, data.BufTextLen);
        if (text.Length > 0) data.InsertChars(0, text);
    }
}
