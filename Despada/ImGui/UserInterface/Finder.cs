// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using System.Numerics;
using Despada.Core;
using Despada.ImGui.Hook;
using ImGuiNET;

namespace Despada.ImGui.UserInterface;

/// <summary>One hit: a kind icon, the name, where it is (path, muted), its value, the location at the right; what Enter does, what the preview shows.</summary>
internal sealed class FinderItem
{
    public Icon    Icon;
    public Tone    IconTone = Tone.Muted;
    public Span[]  Left  = [];
    public string  Path  = "";
    public Span[]  Value = [];
    public string  Right = "";
    /// <summary>What Enter does, in words, for the footer ("open in Network").</summary>
    public string  Action = "";
    public Action? Open;
    /// <summary>The preview's lines, built once when first shown.</summary>
    public Func<List<(Span[] Spans, bool Lit)>>? Preview;
    /// <summary>What Shift+Enter puts into the console.</summary>
    public string? Link;

    internal List<(Span[] Spans, bool Lit)>? PreviewLines;
}

/// <summary>
/// What to search: the text, how many hits at most, the source's data at versions in (After, Until]
/// (the whole of it for a new query, only what came since for a live update), and whether the
/// hits are for the All tab (brief: the best one per thing, not every one).
/// </summary>
internal readonly record struct FinderQuery(string Text, int Limit, long After, long Until, bool Brief);

/// <summary>
/// A tab of the finder: a kind of thing it can find. Lower ranks come first. A search yields its
/// hits lazily, newest first, and a null every so many things looked at, so the finder can stop
/// it mid-way when the frame's slice is spent and go on next frame.
/// </summary>
internal interface IFinderSource
{
    string Tab { get; }
    int Rank { get; }
    /// <summary>Grows as what the source searches grows (the newest packet's seq); 0 when it never changes.</summary>
    long Version => 0;
    IEnumerable<FinderItem?> Search(FinderQuery query);
}

/// <summary>
/// The search-everything palette (Ctrl+D): scope tabs across the top, the field, a flat list of
/// hits with sections on the All tab, and a preview of the picked hit at the bottom. A modal over
/// the main window: nothing behind it gets the mouse. The sources search in turns, a slice of time
/// per frame, so a quick one shows its hits while a slow one is still walking and typing never stalls. Tab cycles the tabs, ↑↓ pick, Enter opens, Shift+Enter puts the
/// hit's link into the console, Esc or a click outside closes.
/// </summary>
internal static class Finder
{
    private const float D = UiScale.DesignHeight / 1080f;
    private const int   PerGroup = 8;
    private const int   PerTab   = 60;
    private const int   PreviewLines = 7;
    private const double SliceMs = 2.5;

    public static readonly List<IFinderSource> Sources = new();
    /// <summary>What the scoped tabs search right now ("#4872 Lobby…"); set every frame by the shown section.</summary>
    public static string? Scope;
    /// <summary>Runs when the finder closes (the shell gives the keyboard back).</summary>
    public static Action? Closed;

    public static bool IsOpen { get; private set; }

    private static string _query = "";
    private static int    _tab;
    private static int    _sel;
    private static bool   _scrollToSel;
    private static bool   _focus;
    private static bool   _justOpened;
    private static bool   _popupOpen;    // ImGui still has the modal in its stack
    private static string _searchedFor = "\0";

    // One per source searched: its hits in rank order, and the ones a live update found, which
    // join above them once it is done. _hits is the flat list the rows are drawn from.
    private sealed class Group
    {
        public required IFinderSource Source;
        public readonly List<FinderItem> Items = new();
        public readonly List<FinderItem> Fresh = new();
        public long Version;   // the source's data searched up to here
    }
    private static readonly List<Group> _groups = new();
    private static readonly List<(string Group, FinderItem Item)> _hits = new();
    private static readonly List<(Group Group, IEnumerator<FinderItem?> Items)> _pending = new();
    private static int  _turn;
    private static bool _capped;
    private static bool _live;          // a search over what came since: the list shown stays as it is
    private static long _finishedAt;
    private static long _overListAt;    // the pointer was last over the rows (no change under it)
    private static float _lastScroll;
    private static float _scrollTo = -1f;

    public static void Toggle()
    {
        if (IsOpen) Close();
        else
        {
            IsOpen = true;
            _justOpened = true;
            _query = "";
            _tab = 0;
            _sel = 0;
            _focus = true;
            _searchedFor = "\0";
            _scrollTo = -1f;
            _live = false;
            _groups.Clear();
            _hits.Clear();
            DropPending();
        }
    }

    public static void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        _live = false;
        _groups.Clear();
        _hits.Clear();           // the items' closures hold field trees: let them go
        _searchedFor = "\0";
        DropPending();
        Closed?.Invoke();
    }

    private static void DropPending()
    {
        foreach (var (_, e) in _pending) e.Dispose();
        _pending.Clear();
    }

    private static IEnumerable<IFinderSource> Ordered => Sources.OrderBy(s => s.Rank);
    private static int Limit => _tab == 0 ? PerGroup : PerTab;

    // A new query or tab starts the search over, the list filling as hits come. Once it is done,
    // a source whose data grew is searched again a second later, live, over what came since
    // only: the hits join above the shown ones in one go, when the pointer is off the rows, the
    // pick staying on its item and its row where it was. Every frame gives the sources a slice of
    // time, one step each in turns, so no source waits for a slower one.
    private static void Refresh()
    {
        var key = $"{_tab}\n{_query}";
        var q   = _query.Trim();
        if (key != _searchedFor)
        {
            _searchedFor = key;
            _live = false;
            _groups.Clear();
            _hits.Clear();
            _sel = 0;
            _scrollToSel = true;
            _capped = false;
            DropPending();
            if (q.Length > 0)
                foreach (var src in _tab == 0 ? Ordered.ToList() : Ordered.Skip(_tab - 1).Take(1).ToList())
                {
                    var g = new Group { Source = src, Version = src.Version };
                    _groups.Add(g);
                    _pending.Add((g, src.Search(new FinderQuery(q, Limit, 0, g.Version, _tab == 0)).GetEnumerator()));
                }
        }
        else if (_pending.Count == 0 && !_live && q.Length > 0 && Stopwatch.GetElapsedTime(_finishedAt).TotalSeconds >= 1.0)
        {
            foreach (var g in _groups)
            {
                var v = g.Source.Version;
                if (v == g.Version) continue;
                g.Fresh.Clear();
                _pending.Add((g, g.Source.Search(new FinderQuery(q, Limit, g.Version, v, _tab == 0)).GetEnumerator()));
                g.Version = v;
            }
            _live = _pending.Count > 0;
        }
        if (_pending.Count == 0)
        {
            if (_live && Stopwatch.GetElapsedTime(_overListAt).TotalSeconds >= 0.5) Merge();
            return;
        }
        var started = Stopwatch.GetTimestamp();
        var added = false;
        while (_pending.Count > 0 && Stopwatch.GetElapsedTime(started).TotalMilliseconds < SliceMs)
        {
            if (_turn >= _pending.Count) _turn = 0;
            var (g, items) = _pending[_turn];
            bool more;
            try { more = items.MoveNext(); }
            catch (Exception ex)
            {
                Log.Warn("finder", $"{g.Source.Tab}: {ex.GetType().Name}: {ex.Message}");
                more = false;
            }
            if (!more) { items.Dispose(); _pending.RemoveAt(_turn); continue; }
            _turn++;
            if (items.Current is not { } hit) continue;
            (_live ? g.Fresh : g.Items).Add(hit);
            added = true;
            if (!_live && _tab != 0 && g.Items.Count >= PerTab) { _capped = true; DropPending(); }
        }
        if (_pending.Count == 0) _finishedAt = Stopwatch.GetTimestamp();
        if (added && !_live) Flatten();
    }

    // The rows in rank order; a pick the user made stays on its item as hits arrive above it.
    private static void Flatten()
    {
        var picked = _sel > 0 && _sel < _hits.Count ? _hits[_sel].Item : null;
        _hits.Clear();
        foreach (var g in _groups)
            foreach (var it in g.Items) _hits.Add((g.Source.Tab, it));
        if (picked is not null) _sel = Math.Max(0, _hits.FindIndex(h => ReferenceEquals(h.Item, picked)));
    }

    // The live hits join above the shown ones. A pick the user made (or a list they scrolled)
    // keeps its item and its place on the screen; untouched, the newest hit is the pick again.
    private static void Merge()
    {
        _live = false;
        var moved  = _sel > 0 || _lastScroll > 0f;
        var picked = moved && _sel < _hits.Count ? _hits[_sel].Item : null;
        var oldTop = RowTop(_sel);
        foreach (var g in _groups)
        {
            g.Items.InsertRange(0, g.Fresh);
            g.Fresh.Clear();
        }
        _hits.Clear();
        foreach (var g in _groups)
            foreach (var it in g.Items) _hits.Add((g.Source.Tab, it));
        var at = picked is null ? -1 : _hits.FindIndex(h => ReferenceEquals(h.Item, picked));
        if (at >= 0) { _sel = at; _scrollTo = MathF.Max(0f, _lastScroll + RowTop(at) - oldTop); }
        else _sel = 0;
    }

    // The row's top in the list's content: the rows before it, and the captions between on All.
    private static float RowTop(int index)
    {
        float y = 0f, rowH = Px(RowH), secH = Px(SecH);
        string? group = null;
        for (var i = 0; i < index && i < _hits.Count; i++)
        {
            if (_tab == 0 && _hits[i].Group != group) { group = _hits[i].Group; y += secH; }
            y += rowH;
        }
        if (_tab == 0 && index < _hits.Count && _hits[index].Group != group) y += secH;
        return y;
    }

    // The layout, in design units: Rider's Search Everywhere at the console font. Heights are
    // whole pixels: ImGui rounds the scroll, and rows of a fractional height would wobble under it.
    private const float TabsH  = 43f, ChipH = 28f, FieldH = 38f, RowH = 32f, SecH = 22f, LineH = 24f, FootH = 31f, Pad = 4f, Margin = 11f;
    private static float Px(float units) => MathF.Floor(units * D);

    /// <summary>Draws the finder over the main window (call after that window's End).</summary>
    public static void Draw(Vector2 winPos, Vector2 winSize)
    {
        // A modal left in ImGui's stack keeps blocking the mouse, so once opened the popup is
        // submitted every frame until CloseCurrentPopup has run, even after Close().
        if (!IsOpen && !_popupOpen) return;
        var io = ImGuiNET.ImGui.GetIO();
        if (IsOpen)
        {
            ImGuiInputHook.ClaimKeyboard();
            if (ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.Escape, false)) Close();
            var tabs = Sources.Count + 1;
            if (ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.Tab, false)) { _tab = (_tab + (io.KeyShift ? tabs - 1 : 1)) % tabs; _scrollToSel = true; }
            if (ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.DownArrow)) { _sel++; _scrollToSel = true; }
            if (ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.UpArrow))   { _sel--; _scrollToSel = true; }
        }

        if (IsOpen) Refresh();
        _sel = Math.Clamp(_sel, 0, Math.Max(0, _hits.Count - 1));
        var picked = _hits.Count > 0 ? _hits[_sel].Item : null;

        // Fixed tabs, field and footer; the list as tall as its rows, whole rows only, up to what
        // is left; a preview slot of a fixed number of lines when the pick has one.
        var tabsH  = Px(TabsH);
        var fieldH = Px(7f) + Px(FieldH) + Px(Pad);
        var rowH   = Px(RowH);
        var secH   = Px(SecH);
        var lineH  = Px(LineH);
        var footH  = Px(FootH);
        var pad    = Px(Pad);
        // The preview slot is there as long as there are hits, the same for every pick: the
        // window's height follows the number of hits only, never the pick.
        var prevH  = _hits.Count > 0 ? 1f + Px(6f) + Px(7f) + (PreviewLines + 1) * lineH : 0f;
        var w      = MathF.Floor(winSize.X * 0.76f);
        var maxH   = MathF.Floor(winSize.Y * 0.8f);
        var content = 0f;
        string? group = null;
        foreach (var (g, _) in _hits) { if (_tab == 0 && g != group) { content += secH; group = g; } content += rowH; }
        if (_hits.Count == 0) content = Px(2f);
        var room  = maxH - tabsH - 1f - fieldH - prevH - footH - 2f * pad;
        var listH = content <= room ? content : MathF.Max(rowH, MathF.Floor(room / rowH) * rowH);
        listH += 2f * pad;
        var h = tabsH + 1f + fieldH + listH + prevH + footH;

        var x = winPos.X + MathF.Floor((winSize.X - w) * 0.5f);
        var y = winPos.Y + MathF.Floor(winSize.Y * 0.09f);
        if (_justOpened) { ImGuiNET.ImGui.OpenPopup("##finder"); _justOpened = false; _popupOpen = true; }
        ImGuiNET.ImGui.SetNextWindowPos(new Vector2(x, y), ImGuiCond.Always);
        ImGuiNET.ImGui.SetNextWindowSize(new Vector2(w, h), ImGuiCond.Always);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
        // The modal is only a container: with NoBackground ImGui draws neither fill nor border;
        // the shadow and the fill go into its list below, in that order.
        var shown = ImGuiNET.ImGui.BeginPopupModal("##finder", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoBackground);
        if (!shown)
        {
            ImGuiNET.ImGui.PopStyleVar(2);
            _popupOpen = false;
            Close();   // the popup went away under us (another modal, a lost context)
            return;
        }
        if (!IsOpen)
        {
            ImGuiNET.ImGui.CloseCurrentPopup();
            ImGuiNET.ImGui.EndPopup();
            ImGuiNET.ImGui.PopStyleVar(2);
            _popupOpen = false;
            return;
        }
        var dl  = ImGuiNET.ImGui.GetWindowDrawList();
        var pos = ImGuiNET.ImGui.GetWindowPos();
        var max = pos + new Vector2(w, h);
        // The dim over the whole main window, then the popup's shadow and its opaque fill on top:
        // bands around the popup's rectangle would leave its rounded corners undimmed.
        dl.PushClipRect(winPos, winPos + winSize, false);
        var r = 12f * D;
        dl.AddRectFilled(winPos, winPos + winSize, Theme.ToU32(new Vector4(0f, 0f, 0f, 0.45f)), Theme.WindowRounding);
        Gfx.Shadow(dl, pos, max, r, 24f, 0.5f);
        dl.AddRectFilled(pos, max, Theme.ToU32(Theme.WithAlpha(Theme.P.Pop, 1f)), r);
        dl.PopClipRect();

        var hovered = ImGuiNET.ImGui.IsWindowHovered(ImGuiHoveredFlags.ChildWindows | ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        DrawTabs(dl, pos, w, tabsH, hovered);
        var enter = DrawField(dl, pos, w, fieldH);
        DrawHits(w, listH, content + 2f * pad, rowH, secH, hovered);
        if (picked is not null) DrawPreview(dl, w, prevH, lineH, picked);
        DrawFooter(dl, pos, w, footH, r, picked);

        if (ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !hovered) Close();
        if (enter && picked is not null)
        {
            if (io.KeyShift)
            {
                if (picked.Link is { } link) { ConsolePanel.InsertLink(link); Close(); }
            }
            else
            {
                try { picked.Open?.Invoke(); }
                catch (Exception ex) { ImGuiRenderer.ShowToast("finder", ex.Message, 3f); }
                Close();
            }
        }
        if (!IsOpen) { ImGuiNET.ImGui.CloseCurrentPopup(); _popupOpen = false; }
        ImGuiNET.ImGui.EndPopup();
        ImGuiNET.ImGui.PopStyleVar(2);
    }

    // The scope tabs as chips, the active one filled; the scoped packet's name at the right.
    private static void DrawTabs(ImDrawListPtr dl, Vector2 pos, float w, float h, bool hovered)
    {
        var x     = pos.X + 8f * D;
        var chipH = Px(ChipH);
        var top   = pos.Y + MathF.Floor((h - chipH) * 0.5f);
        var lineH = ImGuiNET.ImGui.GetTextLineHeight();
        var mouse = ImGuiNET.ImGui.GetIO().MousePos;
        var sources = Ordered.ToList();
        for (var i = 0; i <= sources.Count; i++)
        {
            var label = i == 0 ? "All" : sources[i - 1].Tab;
            var tw = ImGuiNET.ImGui.CalcTextSize(label).X;
            var bw = tw + 24f * D;
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, top));
            if (ImGuiNET.ImGui.InvisibleButton($"##tab{i}", new Vector2(bw, chipH))) { _tab = i; _scrollToSel = true; _focus = true; }
            var active = _tab == i;
            var over   = hovered && mouse.X >= x && mouse.X < x + bw && mouse.Y >= top && mouse.Y < top + chipH;
            if (active) dl.AddRectFilled(new Vector2(x, top), new Vector2(x + bw, top + chipH), Theme.ToU32(Theme.WithAlpha(Theme.P.Accent, 0.16f)), 6f * D);
            dl.AddText(new Vector2(x + 12f * D, top + (chipH - lineH) * 0.5f), Theme.ToU32(active || over ? Theme.P.Fg : Theme.P.Muted), label);
            x += bw + 3f * D;
        }
        if (Scope is { } scope)
        {
            var sw = ImGuiNET.ImGui.CalcTextSize(scope).X * 0.8f;
            dl.AddText(ImGuiNET.ImGui.GetFont(), ImGuiNET.ImGui.GetFontSize() * 0.8f, new Vector2(pos.X + w - 16f * D - sw, pos.Y + (h - lineH * 0.8f) * 0.5f),
                Theme.ToU32(Theme.P.Muted), scope);
        }
        Gfx.HLine(dl, pos.X, pos.X + w, pos.Y + h, Theme.ToU32(Theme.Hairline));
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + h + 1f));
    }

    // The field in a bordered box: a magnifier, the text, the count at the right; focused whenever
    // nothing else is being pressed. True on Enter.
    private static bool DrawField(ImDrawListPtr dl, Vector2 pos, float w, float h)
    {
        var top  = ImGuiNET.ImGui.GetCursorScreenPos().Y;
        var bMin = new Vector2(pos.X + Margin * D, top + Px(7f));
        var bMax = new Vector2(pos.X + w - Margin * D, bMin.Y + Px(FieldH));
        dl.AddRectFilled(bMin, bMax, Theme.ToU32(Theme.P.Deep), 7f * D);
        dl.AddRect(bMin, bMax, Theme.ToU32(Theme.Hairline), 7f * D);
        Widgets.DrawIcon(dl, new Vector2(bMin.X + 19f * D, (bMin.Y + bMax.Y) * 0.5f), 15f * D, Icon.Search, Theme.P.Muted, 1.6f);

        var busy  = _pending.Count > 0 && !_live;
        var count = _hits.Count == 0 ? (busy ? "searching…" : _query.Trim().Length == 0 ? "" : "nothing")
                  : _capped ? $"{_hits.Count}+" : _hits.Count == 1 && !busy ? "1 result" : $"{_sel + 1} of {_hits.Count}{(busy ? "…" : "")}";
        var cw = ImGuiNET.ImGui.CalcTextSize(count).X * 0.8f;
        dl.AddText(ImGuiNET.ImGui.GetFont(), ImGuiNET.ImGui.GetFontSize() * 0.8f, new Vector2(bMax.X - 12f * D - cw, (bMin.Y + bMax.Y - ImGuiNET.ImGui.GetTextLineHeight() * 0.8f) * 0.5f),
            Theme.ToU32(Theme.P.Muted), count);

        var x = bMin.X + 34f * D;
        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(x, (bMin.Y + bMax.Y - ImGuiNET.ImGui.GetFrameHeight()) * 0.5f));
        ImGuiNET.ImGui.SetNextItemWidth(bMax.X - 24f * D - cw - x);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        if (_focus) { ImGuiNET.ImGui.SetKeyboardFocusHere(); _focus = false; }
        var io   = ImGuiNET.ImGui.GetIO();
        var keep = io.ConfigInputTextEnterKeepActive;
        io.ConfigInputTextEnterKeepActive = true;
        var enter = ImGuiNET.ImGui.InputText("##finder-query", ref _query, 256, ImGuiInputTextFlags.EnterReturnsTrue);
        io.ConfigInputTextEnterKeepActive = keep;
        if (!ImGuiNET.ImGui.IsItemActive() && !ImGuiNET.ImGui.IsAnyItemActive() && !ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Left)) _focus = true;
        ImGuiNET.ImGui.PopStyleColor(3);

        ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(pos.X, top + h));
        return enter;
    }

    // The hits: sections as captions on the All tab, rows of icon · name · path · value · location,
    // a thin thumb at the right when they scroll.
    private static void DrawHits(float w, float h, float contentH, float rowH, float secH, bool hovered)
    {
        // The content's height is declared up front: ImGui would clamp the scroll to last frame's,
        // which is shorter than a list that live hits just joined. A scroll set here holds for this very frame.
        ImGuiNET.ImGui.SetNextWindowContentSize(new Vector2(0f, contentH));
        if (_scrollTo >= 0f) { ImGuiNET.ImGui.SetNextWindowScroll(new Vector2(-1f, _scrollTo)); _scrollTo = -1f; }
        ImGuiNET.ImGui.BeginChild("##finder-hits", new Vector2(w, h), ImGuiChildFlags.None, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
        var dl    = ImGuiNET.ImGui.GetWindowDrawList();
        var font  = ImGuiNET.ImGui.GetFont();
        var fs    = ImGuiNET.ImGui.GetFontSize();
        var lineH = ImGuiNET.ImGui.GetTextLineHeight();
        var io    = ImGuiNET.ImGui.GetIO();
        var mouse = io.MousePos;
        var cMin  = ImGuiNET.ImGui.GetWindowPos();
        var cMax  = cMin + ImGuiNET.ImGui.GetWindowSize();
        var overList = hovered && mouse.X >= cMin.X && mouse.X < cMax.X && mouse.Y >= cMin.Y && mouse.Y < cMax.Y;
        if (overList) _overListAt = Stopwatch.GetTimestamp();
        if (overList && io.MouseWheel != 0f)
            ImGuiNET.ImGui.SetScrollY(Math.Clamp(ImGuiNET.ImGui.GetScrollY() - io.MouseWheel * rowH * 3f, 0f, ImGuiNET.ImGui.GetScrollMaxY()));
        _lastScroll = ImGuiNET.ImGui.GetScrollY();
        var pad = Px(Pad);
        ImGuiNET.ImGui.Dummy(new Vector2(w, pad));

        string? group = null;
        for (var i = 0; i < _hits.Count; i++)
        {
            var (g, it) = _hits[i];
            if (_tab == 0 && g != group)
            {
                group = g;
                var sp = ImGuiNET.ImGui.GetCursorScreenPos();
                dl.AddText(font, fs * 0.8f, new Vector2(sp.X + 18f * D, sp.Y + (secH - lineH * 0.8f) * 0.5f), Theme.ToU32(Theme.P.Muted), g.ToLowerInvariant());
                ImGuiNET.ImGui.Dummy(new Vector2(w, secH));
            }
            var p = ImGuiNET.ImGui.GetCursorScreenPos();
            if (i == _sel)
            {
                Gfx.GradientRect(dl, new Vector2(p.X + 8f * D, p.Y), new Vector2(p.X + w - 8f * D, p.Y + rowH), Theme.Sel, Theme.SelR, 7f * D);
                if (_scrollToSel)
                {
                    _scrollToSel = false;
                    if (p.Y < cMin.Y || p.Y + rowH > cMax.Y) ImGuiNET.ImGui.SetScrollFromPosY(p.Y - cMin.Y + rowH * 0.5f, 0.5f);
                }
            }
            var over = overList && mouse.Y >= p.Y && mouse.Y < p.Y + rowH;
            if (over && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                _sel = i;
                if (ImGuiNET.ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                {
                    try { it.Open?.Invoke(); }
                    catch (Exception ex) { ImGuiRenderer.ShowToast("finder", ex.Message, 3f); }
                    Close();
                }
            }
            var ty = p.Y + MathF.Floor((rowH - lineH) * 0.5f);
            var x  = p.X + 18f * D;
            var rw = ImGuiNET.ImGui.CalcTextSize(it.Right).X * 0.8f;
            var limit = p.X + w - 18f * D - rw - 14f * D;
            dl.PushClipRect(new Vector2(x, p.Y), new Vector2(limit, p.Y + rowH), true);
            if (it.Icon != Icon.None)
            {
                Widgets.DrawIcon(dl, new Vector2(x + 8f * D, p.Y + rowH * 0.5f), 15f * D, it.Icon, TreeRows.Color(it.IconTone), 1.6f);
                x += 28f * D;
            }
            x = DrawSpans(dl, it.Left, x, ty);
            if (it.Path.Length > 0)
            {
                x += 10f * D;
                dl.AddText(font, fs * 0.9f, new Vector2(x, p.Y + (rowH - lineH * 0.9f) * 0.5f), Theme.ToU32(Theme.P.Muted), it.Path);
                x += ImGuiNET.ImGui.CalcTextSize(it.Path).X * 0.9f;
            }
            if (it.Value.Length > 0) DrawSpans(dl, it.Value, x + 14f * D, ty);
            dl.PopClipRect();
            dl.AddText(font, fs * 0.8f, new Vector2(p.X + w - 18f * D - rw, p.Y + (rowH - lineH * 0.8f) * 0.5f), Theme.ToU32(Theme.P.Muted), it.Right);
            ImGuiNET.ImGui.Dummy(new Vector2(w, rowH));
        }
        ImGuiNET.ImGui.Dummy(new Vector2(w, pad));

        var scrollMax = ImGuiNET.ImGui.GetScrollMaxY();
        if (scrollMax > 0f)
        {
            var track  = h - 2f * pad;
            var total  = h + scrollMax;
            var thumbH = MathF.Round(MathF.Max(20f * D, track * h / total));
            var thumbY = cMin.Y + pad + MathF.Round((track - thumbH) * (ImGuiNET.ImGui.GetScrollY() / scrollMax));
            var tx     = cMax.X - 8f * D;
            dl.AddRectFilled(new Vector2(tx, thumbY), new Vector2(tx + 4f * D, thumbY + thumbH), Theme.ToU32(Theme.Thumb), 2f * D);
        }
        ImGuiNET.ImGui.EndChild();
    }

    private static float DrawSpans(ImDrawListPtr dl, Span[] spans, float x, float y)
    {
        foreach (var span in spans)
        {
            dl.AddText(new Vector2(x, y), Theme.ToU32(TreeRows.Color(span.Tone)), span.Text);
            x += ImGuiNET.ImGui.CalcTextSize(span.Text).X;
        }
        return x;
    }

    // The pick's preview (built once per item): its first line is the place, smaller; the rest
    // the rows around the hit, the hit lit.
    private static void DrawPreview(ImDrawListPtr dl, float w, float h, float lineH, FinderItem item)
    {
        var top  = ImGuiNET.ImGui.GetCursorScreenPos();
        var font = ImGuiNET.ImGui.GetFont();
        var fs   = ImGuiNET.ImGui.GetFontSize();
        var textH = ImGuiNET.ImGui.GetTextLineHeight();
        Gfx.HLine(dl, top.X + Margin * D, top.X + w - Margin * D, top.Y, Theme.ToU32(Theme.Hairline));
        if (item.PreviewLines is null)
        {
            try { item.PreviewLines = item.Preview?.Invoke() ?? []; }
            catch (Exception ex) { item.PreviewLines = [([new Span("⚠ " + ex.Message, Tone.Red)], false)]; }
        }
        var y = top.Y + 1f + Px(6f);
        dl.PushClipRect(top, new Vector2(top.X + w, top.Y + h), true);
        var first = true;
        foreach (var (spans, lit) in item.PreviewLines.Take(PreviewLines + 1))
        {
            var x = top.X + 18f * D;
            if (first)
            {
                var text = string.Concat(spans.Select(s => s.Text));
                dl.AddText(font, fs * 0.8f, new Vector2(x, y + (lineH - textH * 0.8f) * 0.5f), Theme.ToU32(Theme.P.Muted), text);
                first = false;
            }
            else
            {
                if (lit) Gfx.GradientRect(dl, new Vector2(x - 6f * D, y), new Vector2(top.X + w - 18f * D, y + lineH), Theme.Sel, Theme.SelR, 6f * D);
                DrawSpans(dl, spans, x, y + (lineH - textH) * 0.5f);
            }
            y += lineH;
        }
        dl.PopClipRect();
        ImGuiNET.ImGui.Dummy(new Vector2(w, h));
    }

    // The footer: the pick's console link at the left, what Enter does at the right.
    private static void DrawFooter(ImDrawListPtr dl, Vector2 pos, float w, float h, float rounding, FinderItem? picked)
    {
        var top  = ImGuiNET.ImGui.GetCursorScreenPos().Y;
        var font = ImGuiNET.ImGui.GetFont();
        var fs   = ImGuiNET.ImGui.GetFontSize();
        var textH = ImGuiNET.ImGui.GetTextLineHeight() * 0.8f;
        dl.AddRectFilled(new Vector2(pos.X, top), new Vector2(pos.X + w, top + h), Theme.ToU32(Theme.P.Deep), rounding, ImDrawFlags.RoundCornersBottom);
        Gfx.HLine(dl, pos.X, pos.X + w, top, Theme.ToU32(Theme.Hairline));
        if (picked is null) { ImGuiNET.ImGui.Dummy(new Vector2(w, h)); return; }
        var ty = top + (h - textH) * 0.5f;
        if (picked.Link is { } link)
            dl.AddText(font, fs * 0.8f, new Vector2(pos.X + 18f * D, ty), Theme.ToU32(Theme.P.Muted), link);
        if (picked.Action.Length > 0)
        {
            var aw = ImGuiNET.ImGui.CalcTextSize(picked.Action).X * 0.8f;
            dl.AddText(font, fs * 0.8f, new Vector2(pos.X + w - 18f * D - aw, ty), Theme.ToU32(Theme.P.Accent), picked.Action);
        }
        ImGuiNET.ImGui.Dummy(new Vector2(w, h));
    }

    /// <summary>The text with the query's occurrences in the accent, the rest in <paramref name="tone"/>.</summary>
    public static Span[] Highlight(string text, string query, Tone tone = Tone.Fg)
    {
        if (query.Length == 0) return [new Span(text, tone)];
        var spans = new List<Span>();
        var at = 0;
        while (at < text.Length)
        {
            var hit = text.IndexOf(query, at, StringComparison.OrdinalIgnoreCase);
            if (hit < 0) { spans.Add(new Span(text[at..], tone)); break; }
            if (hit > at) spans.Add(new Span(text[at..hit], tone));
            spans.Add(new Span(text.Substring(hit, query.Length), Tone.Accent2));
            at = hit + query.Length;
        }
        return spans.ToArray();
    }
}
