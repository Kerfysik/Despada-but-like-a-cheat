// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using ImGuiNET;

namespace Despada.ImGui.UserInterface;

internal static unsafe class ConsolePanel
{
    private const float D           = UiScale.DesignHeight / 1080f; // 1080p px → logical units
    private const uint  InputMaxLen = 1024;
    private const int   MaxSuggest  = 6;

    private static readonly List<ConsoleSession> _sessions = new();
    private static int  _active;
    private static bool _welcomed;
    private static bool _focusInput = true;

    // Inline rename of a session pill (double-click); -1 = none.
    private static int    _renaming = -1;
    private static int    _renameStartFrame;
    private static bool   _renameFocus;
    private static string _renameBuf = "";

    // Input state captured by the callback (byte offsets into the UTF-8 buffer) and the fish-style
    // suggestion for the text as it is now.
    private static int    _cursor, _selStart, _selEnd;
    private static string _ghost = "";

    // Caret/length from the previous callback: → and End move the caret before the callback runs,
    // so "was at the end" has to come from the frame before.
    private static int _lastCursor = -1, _lastLen = -1;

    // Session the input callback last edited; all sessions share one InputText (see InputCallback).
    private static ConsoleSession? _cbSession;

    // ImGui keeps the horizontal scroll after the text shrinks back, so once the text overflowed the
    // overdraw positions are unknown until the field is emptied or re-activated.
    private static bool _overflowed;

    // Kept in a field: ImGui holds the native callback pointer for the duration of the call.
    private static readonly ImGuiInputTextCallback _inputCallback = InputCallback;

    // Line seen by the callback on Enter (see InputCallback), and whether the field still has to be
    // emptied after a submit.
    private static string? _submitted;
    private static bool    _clearPending;
    private static bool    _enterAccepted;

    // Suggestion list (fish pager): selected row, whether the user moved it, hidden by Esc until the
    // next edit.
    private static int  _sugIndex;
    private static bool _sugNavigated;
    private static bool _sugHidden;

    // Esc never reaches ImGui while the console input is active (it would drop the focus and hand
    // the keyboard to the game); the input hook forwards it here instead.
    private static bool _escape;

    // Output scroll requested by hotkeys, applied inside the output child.
    private static float _scrollLines, _scrollPages;
    private static int   _scrollJump; // -1 top, +1 bottom

    /// <summary>True while the command input owns the keyboard (set every frame the console is drawn).</summary>
    public static bool InputActive { get; set; }

    public static void RequestEscape() => _escape = true;

    private static ConsoleSession Active
    {
        get
        {
            if (_sessions.Count == 0) NewSession();
            _active = Math.Clamp(_active, 0, _sessions.Count - 1);
            return _sessions[_active];
        }
    }

    public static void RequestFocus() => _focusInput = true;

    public static IReadOnlyList<ConsoleSession> Sessions => _sessions;
    public static ConsoleSession Current => Active;

    /// <summary>Opens a session with the lowest number not used by an open one.</summary>
    public static void NewSession()
    {
        var number = 1;
        while (_sessions.Any(x => x.Number == number)) number++;

        var s = new ConsoleSession(number);
        if (!_welcomed)
        {
            s.Print(LineKind.Muted, "despada console · help lists commands");
            _welcomed = true;
        }
        _sessions.Add(s);
        _active = _sessions.Count - 1;
        _focusInput = true;
    }

    /// <summary>Closes a session; the last one stays open.</summary>
    public static bool CloseSession(ConsoleSession s)
    {
        var i = _sessions.IndexOf(s);
        if (i < 0 || _sessions.Count <= 1) return false;

        _sessions.RemoveAt(i);
        if (_active >= i) _active = Math.Max(0, _active - 1);
        if (_renaming == i) _renaming = -1;
        else if (_renaming > i) _renaming--;
        _focusInput = true;
        return true;
    }

    /// <summary>Empty or whitespace goes back to the automatic name.</summary>
    public static void RenameSession(ConsoleSession s, string name)
    {
        name = name.Trim();
        s.CustomName = name.Length == 0 ? null : name;
    }

    /// <summary>Second header row: one tab per session, × closes, + opens a new one.</summary>
    public static void DrawSessionTabs()
    {
        _ = Active;
        var close = -1;

        for (var i = 0; i < _sessions.Count; i++)
        {
            ImGuiNET.ImGui.PushID(i);
            if (_renaming == i)
                DrawRenameField(_sessions[i]);
            else
            {
                if (Shell.Pill("##s", _sessions[i].Name, i == _active, _sessions.Count > 1, out var closeClicked))
                {
                    _active = i;
                    _focusInput = true;
                }
                if (closeClicked) close = i;

                if (ImGuiNET.ImGui.IsItemHovered() && ImGuiNET.ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    StartRename(i);
            }
            ImGuiNET.ImGui.PopID();
            ImGuiNET.ImGui.SameLine(0f, 4f * D);
        }

        if (Shell.Pill("##new", "+", false, false, out _))
            NewSession();

        if (close >= 0)
            CloseSession(_sessions[close]);
    }

    // Enter commits (empty = automatic name); Esc or clicking elsewhere cancels.
    private static void DrawRenameField(ConsoleSession s)
    {
        var frame = ImGuiNET.ImGui.GetFrameCount();
        var w = MathF.Max(ImGuiNET.ImGui.CalcTextSize(s.Name).X, ImGuiNET.ImGui.CalcTextSize("12345678").X) + 20f * D;

        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, Theme.P.Deep);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Theme.SmallRounding);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10f * D, ImGuiNET.ImGui.GetStyle().FramePadding.Y));
        ImGuiNET.ImGui.SetNextItemWidth(w);
        if (_renameFocus)
        {
            _renameFocus = false;
            _renameStartFrame = frame;
            ImGuiNET.ImGui.SetKeyboardFocusHere();
        }

        var commit = ImGuiNET.ImGui.InputText("##rename", ref _renameBuf, 64,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.AutoSelectAll);
        // Focus lands a frame after SetKeyboardFocusHere; after that, inactive = cancelled.
        var gone = !commit && frame - _renameStartFrame > 2 && !ImGuiNET.ImGui.IsItemActive();

        ImGuiNET.ImGui.PopStyleVar(2);
        ImGuiNET.ImGui.PopStyleColor();

        if (commit) RenameSession(s, _renameBuf);
        if (commit || gone)
        {
            _renaming = -1;
            _focusInput = true;
        }
    }

    public static void Draw()
    {
        HandleHotkeys();

        var s     = Active;
        var avail = ImGuiNET.ImGui.GetContentRegionAvail();
        var inputH = 40f * D;
        var gap    = 8f * D;

        // Output.
        ImGuiNET.ImGui.SetCursorPosX(ImGuiNET.ImGui.GetCursorPosX() + 20f * D);
        ImGuiNET.ImGui.BeginChild("##out", new Vector2(avail.X - 30f * D, avail.Y - inputH - gap - 10f * D),
            ImGuiChildFlags.None, ImGuiWindowFlags.HorizontalScrollbar);

        DrawLines(s);

        if (s.ScrollToBottom)
        {
            ImGuiNET.ImGui.SetScrollHereY(1f);
            s.ScrollToBottom = false;
        }
        ApplyScroll();

        DrawSuggestions(s);
        ImGuiNET.ImGui.EndChild();

        // Input line.
        ImGuiNET.ImGui.Dummy(new Vector2(0f, gap - ImGuiNET.ImGui.GetStyle().ItemSpacing.Y));
        ImGuiNET.ImGui.SetCursorPosX(ImGuiNET.ImGui.GetCursorPosX() + 10f * D);
        // The outline is ImGui's own child border, drawn under the parent's clip (a rect drawn inside
        // the child got half clipped). 1/K logical units = exactly one physical pixel.
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.ChildBg, Theme.P.Deep);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Theme.P.Sep);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, Theme.SmallRounding);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.ChildBorderSize, 1f / UiScale.K);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(12f * D, 0f));
        ImGuiNET.ImGui.BeginChild("##in", new Vector2(avail.X - 20f * D, inputH),
            ImGuiChildFlags.Borders | ImGuiChildFlags.AlwaysUseWindowPadding, ImGuiWindowFlags.NoScrollbar);

        var lineH = ImGuiNET.ImGui.GetFrameHeight();
        ImGuiNET.ImGui.SetCursorPosY((inputH - lineH) * 0.5f);
        ImGuiNET.ImGui.AlignTextToFramePadding();
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Theme.P.Accent);
        ImGuiNET.ImGui.TextUnformatted("›");
        ImGuiNET.ImGui.PopStyleColor();
        ImGuiNET.ImGui.SameLine();

        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBg, Vector4.Zero);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Vector4.Zero);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Vector4.Zero);
        ImGuiNET.ImGui.SetNextItemWidth(-1f);
        if (_focusInput && _renaming < 0)
        {
            ImGuiNET.ImGui.SetKeyboardFocusHere();
            _focusInput = false;
        }

        // Enter keeps the field active: no frame without text focus, where keys would reach the game.
        // Only for this field: other inputs (slider editor) rely on Enter deactivating them.
        var io        = ImGuiNET.ImGui.GetIO();
        var keepAfter = io.ConfigInputTextEnterKeepActive;
        io.ConfigInputTextEnterKeepActive = true;
        _submitted = null;
        _enterAccepted = false;
        _ghost = "";
        // Ctrl+C without a selection drops the line; ImGui copies it first, so keep the clipboard.
        string? clipKeep = null;
        if (io.KeyCtrl && !io.KeyShift && !io.KeyAlt && ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.C, false)
            && _selStart == _selEnd)
            clipKeep = ImGuiNET.ImGui.GetClipboardText() ?? "";
        _cursor = _selStart = _selEnd = -1;
        var submitted = ImGuiNET.ImGui.InputText("##cmd", ref s.Input, InputMaxLen,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CallbackHistory |
            ImGuiInputTextFlags.CallbackCompletion | ImGuiInputTextFlags.CallbackEdit |
            ImGuiInputTextFlags.CallbackAlways,
            _inputCallback);
        io.ConfigInputTextEnterKeepActive = keepAfter;
        InputActive = ImGuiNET.ImGui.IsItemActive();
        if (clipKeep is not null) ImGuiNET.ImGui.SetClipboardText(clipKeep);

        if (submitted && !_enterAccepted)
        {
            var line = _submitted ?? s.Input;
            s.Input = "";
            _clearPending = true;
            s.Submit(line);
        }
        else
            DrawInputDecorations(s.Input, ImGuiNET.ImGui.IsItemActive());

        ImGuiNET.ImGui.PopStyleColor(3);
        ImGuiNET.ImGui.EndChild();
        ImGuiNET.ImGui.PopStyleVar(3);
        ImGuiNET.ImGui.PopStyleColor(2);
    }

    // Session keys follow yazi's tab keys behind Alt (the input always has focus, so bare keys would
    // type): Alt+T new, Alt+1..9, Alt+[ / ] previous/next, Alt+{ / } move, Alt+C close, Alt+R rename.
    // Output keys follow kitty (Ctrl+Shift+↑/↓/PgUp/PgDn/Home/End, Shift+PgUp/PgDn) plus fish's
    // Ctrl+L. Line editing lives in InputCallback. Only while the console input owns the keyboard,
    // so the game never sees these keys at the same time.
    private static void HandleHotkeys()
    {
        if (_escape)
        {
            _escape = false;
            _sugHidden = true;
        }

        var io = ImGuiNET.ImGui.GetIO();
        if (!io.WantTextInput || _renaming >= 0) return;

        bool ctrl = io.KeyCtrl, shift = io.KeyShift, alt = io.KeyAlt;
        bool Key(ImGuiKey k, bool repeat = false) => ImGuiNET.ImGui.IsKeyPressed(k, repeat);

        if (alt && !ctrl)
        {
            var n = _sessions.Count;
            if (shift)
            {
                if (Key(ImGuiKey.LeftBracket, true)) Move(-1);
                else if (Key(ImGuiKey.RightBracket, true)) Move(+1);
                return;
            }
            if (Key(ImGuiKey.T)) NewSession();
            else if (Key(ImGuiKey.C)) CloseSession(Active);
            else if (Key(ImGuiKey.R)) StartRename(_active);
            else if (Key(ImGuiKey.LeftBracket, true)) Select((_active - 1 + n) % n);
            else if (Key(ImGuiKey.RightBracket, true)) Select((_active + 1) % n);
            else
            {
                for (var i = 0; i < 9; i++)
                    if (Key(ImGuiKey._1 + i)) { Select(i); break; }
            }
            return;
        }

        if (ctrl && shift && !alt)
        {
            if (Key(ImGuiKey.UpArrow, true)) _scrollLines -= 1f;
            else if (Key(ImGuiKey.DownArrow, true)) _scrollLines += 1f;
            else if (Key(ImGuiKey.PageUp, true)) _scrollPages -= 1f;
            else if (Key(ImGuiKey.PageDown, true)) _scrollPages += 1f;
            else if (Key(ImGuiKey.Home)) _scrollJump = -1;
            else if (Key(ImGuiKey.End)) _scrollJump = 1;
            return;
        }

        if (shift && !ctrl && !alt)
        {
            if (Key(ImGuiKey.PageUp, true)) _scrollPages -= 1f;
            else if (Key(ImGuiKey.PageDown, true)) _scrollPages += 1f;
            return;
        }

        if (ctrl && !shift && !alt && Key(ImGuiKey.L))
            Active.Lines.Clear();
    }

    private static void StartRename(int i)
    {
        if (i < 0 || i >= _sessions.Count) return;
        _renaming = i;
        _renameBuf = _sessions[i].Name;
        _renameFocus = true;
        _focusInput = false;
    }

    private static void Move(int dir)
    {
        var to = _active + dir;
        if (to < 0 || to >= _sessions.Count) return;
        (_sessions[_active], _sessions[to]) = (_sessions[to], _sessions[_active]);
        _active = to;
    }

    private static void ApplyScroll()
    {
        if (_scrollJump < 0) ImGuiNET.ImGui.SetScrollY(0f);
        else if (_scrollJump > 0) ImGuiNET.ImGui.SetScrollY(ImGuiNET.ImGui.GetScrollMaxY());

        var delta = _scrollLines * ImGuiNET.ImGui.GetTextLineHeightWithSpacing()
                  + _scrollPages * ImGuiNET.ImGui.GetWindowHeight() * 0.9f;
        if (delta != 0f)
            ImGuiNET.ImGui.SetScrollY(Math.Clamp(ImGuiNET.ImGui.GetScrollY() + delta, 0f, ImGuiNET.ImGui.GetScrollMaxY()));

        _scrollJump = 0;
        _scrollLines = _scrollPages = 0f;
    }

    private static void Select(int i)
    {
        if (i < 0 || i >= _sessions.Count) return;
        _active = i;
        _focusInput = true;
    }

    /// <summary>
    /// Fish-style extras drawn over the input: the command word coloured (known = accent, anything
    /// else = red), quoted strings green, and the suggested rest of the line in muted text after the
    /// caret. Same font and positions as ImGui's own text, so it only works while nothing scrolls.
    /// </summary>
    private static void DrawInputDecorations(string text, bool active)
    {
        if (text.Length == 0 || !active) _overflowed = false;
        if (text.Length == 0) return;

        var pad   = ImGuiNET.ImGui.GetStyle().FramePadding;
        var min   = ImGuiNET.ImGui.GetItemRectMin();
        var room  = ImGuiNET.ImGui.GetItemRectSize().X - pad.X * 2f;
        if (ImGuiNET.ImGui.CalcTextSize(text).X > room) _overflowed = true;
        var ghost = active && _cursor == System.Text.Encoding.UTF8.GetByteCount(text) ? _ghost : "";
        if (_overflowed || ImGuiNET.ImGui.CalcTextSize(text + ghost).X > room) return;

        var dl     = ImGuiNET.ImGui.GetWindowDrawList();
        var origin = min + pad;
        Vector2 At(int chars) => origin + new Vector2(ImGuiNET.ImGui.CalcTextSize(text[..chars]).X, 0f);

        // Recolouring selected text would fight the selection highlight.
        if (_selStart == _selEnd)
        {
            var start = 0;
            while (start < text.Length && text[start] == ' ') start++;
            var end = text.IndexOf(' ', start);
            if (end < 0) end = text.Length;
            if (end > start)
            {
                var word = text[start..end];
                var col  = ConsoleCommands.Exists(word) ? Theme.P.Accent : Theme.P.Red;
                dl.AddText(At(start), Theme.ToU32(col), word);
            }

            for (var i = end; i < text.Length; i++)
            {
                if (text[i] != '"') continue;
                var close = text.IndexOf('"', i + 1);
                var stop  = close < 0 ? text.Length : close + 1;
                dl.AddText(At(i), Theme.ToU32(Theme.P.Green), text[i..stop]);
                i = stop - 1;
            }
        }

        if (ghost.Length > 0)
            dl.AddText(At(text.Length), Theme.ToU32(Theme.P.Muted), ghost);
    }

    /// <summary>Rest of the line to suggest: newest matching history entry, else a command name.</summary>
    private static string Ghost(ConsoleSession s, string text)
    {
        if (text.Length == 0) return "";

        for (var i = s.History.Count - 1; i >= 0; i--)
        {
            var h = s.History[i];
            if (h.Length > text.Length && h.StartsWith(text, StringComparison.Ordinal))
                return h[text.Length..];
        }

        if (text.Contains(' ')) return "";
        var cmd = ConsoleCommands.Matching(text).FirstOrDefault(c => c.Name.Length > text.Length);
        return cmd is null ? "" : cmd.Name[text.Length..];
    }

    private static void DrawLines(ConsoleSession s)
    {
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        clipper.Begin(s.Lines.Count);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var line = s.Lines[i];
                if (line.Kind == LineKind.Command)
                {
                    ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Theme.P.Accent);
                    ImGuiNET.ImGui.TextUnformatted("›");
                    ImGuiNET.ImGui.PopStyleColor();
                    ImGuiNET.ImGui.SameLine();
                    ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Theme.P.Sub);
                }
                else
                {
                    ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, line.Kind switch
                    {
                        LineKind.Muted => Theme.P.Muted,
                        LineKind.Warn  => Theme.P.Yellow,
                        LineKind.Error => Theme.P.Red,
                        _              => Theme.P.Fg,
                    });
                    // Align with command text, which sits after the "›" prompt.
                    ImGuiNET.ImGui.SetCursorPosX(ImGuiNET.ImGui.GetCursorPosX() + PromptIndent());
                }

                ImGuiNET.ImGui.TextUnformatted(line.Text);
                ImGuiNET.ImGui.PopStyleColor();
            }
        }
        clipper.End();
        clipper.Destroy();
    }

    private static float PromptIndent()
        => ImGuiNET.ImGui.CalcTextSize("›").X + ImGuiNET.ImGui.GetStyle().ItemSpacing.X;

    /// <summary>While the command name is being typed, matching commands are listed above the input.</summary>
    private static List<ConsoleCommand> SuggestionsFor(string text)
    {
        if (_sugHidden || text.Length == 0 || text.Contains(' ')) return [];
        var matches = ConsoleCommands.Matching(text).Take(MaxSuggest).ToList();
        if (matches.Count == 1 && matches[0].Name == text) return [];
        return matches;
    }

    private static void DrawSuggestions(ConsoleSession s)
    {
        var text    = s.Input;
        var matches = SuggestionsFor(text);
        if (matches.Count == 0) return;
        var sel = Math.Clamp(_sugIndex, 0, matches.Count - 1);

        var dl     = ImGuiNET.ImGui.GetWindowDrawList();
        var winPos = ImGuiNET.ImGui.GetWindowPos();
        var winSz  = ImGuiNET.ImGui.GetWindowSize();
        var padY   = 4f * D;
        var padX   = 12f * D;
        var rowH   = ImGuiNET.ImGui.GetTextLineHeight() + padY * 2f;
        var nameW  = matches.Max(m => ImGuiNET.ImGui.CalcTextSize(m.Name).X) + 24f * D;
        var descW  = matches.Max(m => ImGuiNET.ImGui.CalcTextSize(m.Description).X);
        var size   = new Vector2(MathF.Min(nameW + descW + padX * 2f, winSz.X - 20f * D), rowH * matches.Count);
        var min    = new Vector2(winPos.X + 4f * D, winPos.Y + winSz.Y - size.Y - 4f * D);
        var r      = Theme.SmallRounding;

        dl.AddRectFilled(min, min + size, Theme.ToU32(Theme.P.Pop), r);

        // The highlighted row fills its whole row; at the box's top/bottom it follows the box corners.
        var hlFlags = matches.Count == 1 ? ImDrawFlags.RoundCornersAll
                    : sel == 0 ? ImDrawFlags.RoundCornersTop
                    : sel == matches.Count - 1 ? ImDrawFlags.RoundCornersBottom
                    : ImDrawFlags.RoundCornersNone;
        var hlMin = new Vector2(min.X, min.Y + rowH * sel);
        var vtx = dl.VtxBuffer.Size;
        dl.AddRectFilled(hlMin, new Vector2(min.X + size.X, hlMin.Y + rowH), Theme.ToU32(Theme.Sel), r, hlFlags);
        Gfx.Shade(dl, vtx, dl.VtxBuffer.Size, hlMin, new Vector2(min.X + size.X, hlMin.Y),
            Theme.ToU32(Theme.Sel), Theme.ToU32(Theme.SelR));

        for (var i = 0; i < matches.Count; i++)
        {
            var y    = min.Y + rowH * i + padY;
            var x    = min.X + padX;
            var name = matches[i].Name;
            dl.AddText(new Vector2(x, y), Theme.ToU32(Theme.P.Accent2), name[..text.Length]);
            dl.AddText(new Vector2(x + ImGuiNET.ImGui.CalcTextSize(name[..text.Length]).X, y), Theme.ToU32(Theme.P.Fg), name[text.Length..]);
            dl.AddText(new Vector2(x + nameW, y), Theme.ToU32(i == sel ? Theme.P.Sub : Theme.P.Muted), matches[i].Description);
        }

        dl.AddRect(min, min + size, Theme.ToU32(Theme.P.Sep), r, ImDrawFlags.None, 1f / UiScale.K);
    }

    private static int InputCallback(ImGuiInputTextCallbackData* raw)
    {
        var data = new ImGuiInputTextCallbackDataPtr(raw);
        var s    = Active;

        // Submitted last frame: empty the field now (before a possible session swap below).
        if (_clearPending)
        {
            _clearPending = false;
            data.DeleteChars(0, data.BufTextLen);
            _lastCursor = _lastLen = 0;
        }

        // The field stays active across a session switch (hotkeys, commands), and an active
        // InputText doesn't reload the user buffer: swap the text in, or the old session's line
        // would be written into the new one.
        if (!ReferenceEquals(s, _cbSession))
        {
            _cbSession = s;
            Replace(data, s.Input);
            _lastCursor = _lastLen = -1;
        }

        var io = ImGuiNET.ImGui.GetIO();
        bool ctrl = io.KeyCtrl, shift = io.KeyShift, alt = io.KeyAlt;
        var list = SuggestionsFor(BufferText(data));

        // Enter was handled just before the callback (the field stays active, text all selected).
        // Only remember the line: ImGui may not have validated (Enter shares a frame with another
        // key), so it's cleared next frame and only if InputText returned true. After picking a
        // suggestion with the arrows, Enter inserts it instead of running the line (fish).
        if (ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.KeypadEnter))
        {
            if (_sugNavigated && list.Count > 0)
            {
                Replace(data, list[Math.Clamp(_sugIndex, 0, list.Count - 1)].Name + " ");
                _enterAccepted = true;
                ResetSuggestions();
                Capture(data, s);
                return 0;
            }
            _submitted = BufferText(data);
            return 0;
        }

        if (shift && !ctrl && !alt && ImGuiNET.ImGui.IsKeyPressed(ImGuiKey.Tab) && list.Count > 0)
            StepSuggestion(list.Count, -1);
        else if (EditLine(data, s, ctrl, shift, alt))
        {
            // Our own edits don't raise CallbackEdit; treat them like typing.
            if (data.BufDirty)
            {
                s.HistoryPos = -1;
                ResetSuggestions();
            }
        }
        else
            HandleEvent(data, s, list, ctrl, shift);

        Capture(data, s);
        return 0;
    }

    private static void HandleEvent(ImGuiInputTextCallbackDataPtr data, ConsoleSession s, List<ConsoleCommand> list,
        bool ctrl, bool shift)
    {
        switch (data.EventFlag)
        {
            case ImGuiInputTextFlags.CallbackHistory:
                // Ctrl+Shift+↑/↓ scroll the output instead.
                if (ctrl || shift) break;
                if (list.Count > 0)
                    StepSuggestion(list.Count, data.EventKey == ImGuiKey.UpArrow ? -1 : +1);
                else
                    StepHistory(data, s, data.EventKey == ImGuiKey.UpArrow);
                break;

            case ImGuiInputTextFlags.CallbackCompletion:
                // Tab: the highlighted suggestion, else the grey suggestion, else the common prefix.
                if (list.Count > 0)
                {
                    Replace(data, list[Math.Clamp(_sugIndex, 0, list.Count - 1)].Name + " ");
                    ResetSuggestions();
                    break;
                }
                if (data.CursorPos == data.BufTextLen && AcceptGhost(data, s, wholeWord: false)) break;

                var text = BufferText(data);
                if (text.Contains(' ')) break;
                var names = ConsoleCommands.Matching(text).Select(c => c.Name).ToList();
                if (names.Count == 1)
                    Replace(data, names[0] + " ");
                else if (names.Count > 1)
                    Replace(data, CommonPrefix(names));
                break;

            case ImGuiInputTextFlags.CallbackEdit:
                // Typing leaves history browsing and resets the list.
                s.HistoryPos = -1;
                ResetSuggestions();
                break;
        }
    }

    /// <summary>
    /// fish/readline line editing. ImGui has already handled the key when this runs (e.g. Ctrl+A
    /// selected all, → moved the caret), so the result is set over it. Returns true if handled.
    /// </summary>
    private static bool EditLine(ImGuiInputTextCallbackDataPtr data, ConsoleSession s, bool ctrl, bool shift, bool alt)
    {
        bool Key(ImGuiKey k) => ImGuiNET.ImGui.IsKeyPressed(k);
        var buf = (byte*)data.Buf;
        var cur = data.CursorPos;
        var len = data.BufTextLen;
        var atEnd = cur == len && _lastCursor == _lastLen;
        var before = Math.Clamp(_lastCursor, 0, len); // caret before ImGui handled this frame's key

        // Accept the grey suggestion: → / End / Ctrl+F at the end (whole), Alt+→ / Alt+F (one word).
        if (!shift && !alt && ((!ctrl && (Key(ImGuiKey.RightArrow) || Key(ImGuiKey.End))) || (ctrl && Key(ImGuiKey.F)))
            && atEnd && AcceptGhost(data, s, wholeWord: false))
            return true;
        if (alt && !ctrl && !shift && (Key(ImGuiKey.RightArrow) || Key(ImGuiKey.F)) && atEnd
            && AcceptGhost(data, s, wholeWord: true))
            return true;

        // Alt+←/→ by word (ImGui on Linux moved one character).
        if (alt && !ctrl && !shift && Key(ImGuiKey.RightArrow)) { SetCursor(data, NextWord(buf, before, len)); return true; }
        if (alt && !ctrl && !shift && Key(ImGuiKey.LeftArrow)) { SetCursor(data, PrevWord(buf, before)); return true; }

        if (ctrl && shift && !alt)
        {
            // Ctrl+Shift+Home/End scroll the output; undo the selection ImGui made with them.
            if (Key(ImGuiKey.Home) || Key(ImGuiKey.End)) { SetCursor(data, before); return true; }

            if (Key(ImGuiKey.C))
            {
                var text = BufferText(data);
                ImGuiNET.ImGui.SetClipboardText(HasSelection(data) ? Selected(data) : text);
                return true;
            }
            if (Key(ImGuiKey.V))
            {
                var clip = (ImGuiNET.ImGui.GetClipboardText() ?? "").Replace("\r", "").Replace('\n', ' ');
                if (HasSelection(data)) DeleteSelection(data);
                data.InsertChars(data.CursorPos, clip);
                return true;
            }
            return false;
        }

        if (ctrl && !shift && !alt)
        {
            if (Key(ImGuiKey.A)) { SetCursor(data, 0); return true; }
            if (Key(ImGuiKey.E)) { SetCursor(data, len); return true; }
            if (Key(ImGuiKey.B)) { SetCursor(data, PrevChar(buf, cur)); return true; }
            if (Key(ImGuiKey.F)) { SetCursor(data, NextChar(buf, cur, len)); return true; }
            if (Key(ImGuiKey.W)) { var p = PrevWord(buf, cur); data.DeleteChars(p, cur - p); return true; }
            if (Key(ImGuiKey.U)) { data.DeleteChars(0, cur); return true; }
            if (Key(ImGuiKey.K)) { data.DeleteChars(cur, len - cur); return true; }
            // Ctrl+C without a selection drops the line (ImGui has copied it already).
            if (Key(ImGuiKey.C) && !HasSelection(data)) { data.DeleteChars(0, len); return true; }
            return false;
        }

        if (alt && !ctrl && !shift)
        {
            if (Key(ImGuiKey.B)) { SetCursor(data, PrevWord(buf, cur)); return true; }
            if (Key(ImGuiKey.F)) { SetCursor(data, NextWord(buf, cur, len)); return true; }
            if (Key(ImGuiKey.D)) { var n = NextWord(buf, cur, len); data.DeleteChars(cur, n - cur); return true; }
        }
        return false;
    }

    private static void StepSuggestion(int count, int dir)
    {
        _sugIndex = ((_sugIndex + dir) % count + count) % count;
        _sugNavigated = true;
    }

    private static void ResetSuggestions()
    {
        _sugIndex = 0;
        _sugNavigated = false;
        _sugHidden = false;
    }

    /// <summary>fish history search: ↑/↓ walk the entries that contain what was typed.</summary>
    private static void StepHistory(ImGuiInputTextCallbackDataPtr data, ConsoleSession s, bool up)
    {
        if (s.History.Count == 0) return;
        if (s.HistoryPos == -1)
            s.Draft = BufferText(data);

        var shown = BufferText(data);
        bool Hit(string h) => h != shown && (s.Draft.Length == 0 || h.Contains(s.Draft, StringComparison.OrdinalIgnoreCase));

        if (up)
        {
            for (var i = (s.HistoryPos == -1 ? s.History.Count : s.HistoryPos) - 1; i >= 0; i--)
                if (Hit(s.History[i])) { s.HistoryPos = i; Replace(data, s.History[i]); return; }
            return;
        }

        if (s.HistoryPos == -1) return;
        for (var i = s.HistoryPos + 1; i < s.History.Count; i++)
            if (Hit(s.History[i])) { s.HistoryPos = i; Replace(data, s.History[i]); return; }
        s.HistoryPos = -1;
        Replace(data, s.Draft);
    }

    private static bool AcceptGhost(ImGuiInputTextCallbackDataPtr data, ConsoleSession s, bool wholeWord)
    {
        var ghost = CurrentGhost(s, BufferText(data));
        if (ghost.Length == 0) return false;
        if (wholeWord)
        {
            var i = 0;
            while (i < ghost.Length && ghost[i] == ' ') i++;
            while (i < ghost.Length && ghost[i] != ' ') i++;
            ghost = ghost[..i];
        }
        data.InsertChars(data.BufTextLen, ghost);
        ResetSuggestions();
        return true;
    }

    // While a suggestion is picked with the arrows the grey text previews it; otherwise history/command.
    private static string CurrentGhost(ConsoleSession s, string text)
    {
        if (_sugNavigated)
        {
            var list = SuggestionsFor(text);
            if (list.Count > 0)
            {
                var name = list[Math.Clamp(_sugIndex, 0, list.Count - 1)].Name;
                return name.Length > text.Length ? name[text.Length..] : "";
            }
        }
        return Ghost(s, text);
    }

    private static void Capture(ImGuiInputTextCallbackDataPtr data, ConsoleSession s)
    {
        _cursor   = _lastCursor = data.CursorPos;
        _lastLen  = data.BufTextLen;
        _selStart = data.SelectionStart;
        _selEnd   = data.SelectionEnd;
        _ghost    = CurrentGhost(s, BufferText(data));
    }

    private static void SetCursor(ImGuiInputTextCallbackDataPtr data, int pos)
    {
        data.CursorPos = pos;
        data.SelectionStart = data.SelectionEnd = pos;
    }

    private static bool HasSelection(ImGuiInputTextCallbackDataPtr data) => data.SelectionStart != data.SelectionEnd;

    private static string Selected(ImGuiInputTextCallbackDataPtr data)
    {
        var a = Math.Min(data.SelectionStart, data.SelectionEnd);
        var b = Math.Max(data.SelectionStart, data.SelectionEnd);
        return System.Text.Encoding.UTF8.GetString((byte*)data.Buf + a, b - a);
    }

    private static void DeleteSelection(ImGuiInputTextCallbackDataPtr data)
    {
        var a = Math.Min(data.SelectionStart, data.SelectionEnd);
        var b = Math.Max(data.SelectionStart, data.SelectionEnd);
        data.DeleteChars(a, b - a);
    }

    // Byte offsets in the UTF-8 buffer; words are separated by spaces.
    private static int PrevChar(byte* buf, int i)
    {
        if (i <= 0) return 0;
        i--;
        while (i > 0 && (buf[i] & 0xC0) == 0x80) i--;
        return i;
    }

    private static int NextChar(byte* buf, int i, int len)
    {
        if (i >= len) return len;
        i++;
        while (i < len && (buf[i] & 0xC0) == 0x80) i++;
        return i;
    }

    private static int PrevWord(byte* buf, int i)
    {
        while (i > 0 && buf[i - 1] == (byte)' ') i--;
        while (i > 0 && buf[i - 1] != (byte)' ') i--;
        return i;
    }

    private static int NextWord(byte* buf, int i, int len)
    {
        while (i < len && buf[i] == (byte)' ') i++;
        while (i < len && buf[i] != (byte)' ') i++;
        return i;
    }

    private static string BufferText(ImGuiInputTextCallbackDataPtr data)
        => System.Text.Encoding.UTF8.GetString((byte*)data.Buf, data.BufTextLen);

    private static void Replace(ImGuiInputTextCallbackDataPtr data, string text)
    {
        data.DeleteChars(0, data.BufTextLen);
        data.InsertChars(0, text);
    }

    private static string CommonPrefix(List<string> names)
    {
        var prefix = names[0];
        foreach (var n in names)
        {
            var len = 0;
            while (len < prefix.Length && len < n.Length && prefix[len] == n[len]) len++;
            prefix = prefix[..len];
        }
        return prefix;
    }
}
