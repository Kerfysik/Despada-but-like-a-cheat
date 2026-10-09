// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Despada.ImGui;
using Despada.ImGui.Hook;
using ImGuiNET;

namespace Despada.Modules.Net;

// Keyboard for the Network tab. A click in the list, the inspector or the tab row focuses the tab;
// a click anywhere else, or Esc in the list, gives the keyboard back to the game. While focused the
// tab claims the keyboard (the game gets none of these keys).
//
//   tabs       Alt+1 list, Alt+2..9 open packets, Alt+[ / Alt+] previous/next, Alt+C close, Esc back to the list
//   list       ↑/↓ (k/j) select, Shift+↑/↓ extend, PgUp/PgDn, Home/End (End follows the newest again),
//              Enter unfold, Ctrl+Enter open (the selection) in the inspector, Ctrl+A select all,
//              Ctrl+C copy summaries, Esc drop the selection, / or Ctrl+F filter, Space pause
//   mouse      click select, Ctrl+click add/remove, Shift+click range, double or middle click open
//   inspector  Alt+↑/Alt+↓ previous/next packet of the list, 1/2/3 fold fields/related/bytes,
//              ↑/↓ PgUp/PgDn Home/End scroll, Ctrl+C copy fields, Ctrl+Shift+C copy hex, Space pause
internal static partial class NetView
{
    private static bool _focused;

    // The selected list row: a packet (Node null) or one of its unfolded fields.
    private static long       _selSeq = -1;
    private static FieldNode? _selNode;
    private static bool       _scrollToSel;
    private static bool       _selHold;   // selected with the keys away from the newest row: the list holds still
    private static bool       _selFollows; // selected on the newest row: moves with it while the list follows
    private static bool       _followNow;  // End: follow the newest rows from this frame
    private static int        _lastDrawFrame = -10;
    private static int        _lastOpened = -1;
    private static bool       _popupAtBegin;
    private static int        _pageRows = 10;

    // Several packets selected (Ctrl/Shift+click, Shift+arrows); the range starts at the anchor.
    private static readonly HashSet<long> _multi = new();
    private static long _anchor = -1;

    // Rects that keep the focus when clicked: the list or inspector, and the tab row's pills.
    private static Vector2 _bodyMin, _bodyMax, _pillsMin, _pillsMax;

    // Scroll requested for the inspector, in lines / pages; ±2 = top / bottom.
    private static float _inspLines, _inspPages;
    private static int   _inspJump;

    private static bool IsSelected(long seq, FieldNode? node) => _focused && _selSeq == seq && _selNode == node;

    private static void Select(long seq, FieldNode? node, bool follows = false)
    {
        _selSeq = seq;
        _selNode = node;
        _selFollows = follows;
    }

    private static void ClearMulti() => _multi.Clear();

    private static void ToggleMulti(long seq)
    {
        if (_multi.Count == 0)
        {
            // Ctrl+click on the one selected packet deselects it.
            if (_selSeq == seq && _selNode is null)
            {
                _selSeq = -1;
                _anchor = -1;
                return;
            }
            // The first Ctrl+click keeps the packet selected before it, if it is a listed packet row.
            if (_focused && _selSeq >= 0 && _selNode is null && _matches.BinarySearch(_selSeq) >= 0) _multi.Add(_selSeq);
        }
        if (!_multi.Remove(seq)) _multi.Add(seq);
        Select(seq, null);   // the keyboard row moves along
        _anchor = seq;
    }

    /// <summary>Selects the packets from the anchor to <paramref name="seq"/> (both included).</summary>
    private static void SelectRange(long seq)
    {
        if (_anchor < 0) _anchor = _selSeq >= 0 ? _selSeq : seq;
        long lo = Math.Min(_anchor, seq), hi = Math.Max(_anchor, seq);
        _multi.Clear();
        foreach (var row in _rows)
            if (row.Node is null && row.Seq >= lo && row.Seq <= hi) _multi.Add(row.Seq);
    }

    /// <summary>The selected packets, oldest first: the multi-selection, else the keyboard row's packet.</summary>
    private static List<Packet> SelectedPackets()
    {
        var seqs = _multi.Count > 0 ? _multi.OrderBy(x => x) : _selSeq >= 0 ? [_selSeq] : Enumerable.Empty<long>();
        return seqs.Select(NetCapture.Get).OfType<Packet>().ToList();
    }

    private static void Unfocus()
    {
        _focused = false;
        _selHold = false;
    }

    /// <summary>Start of Draw: forget focus after the tab was hidden, claim the keyboard, run the keys.</summary>
    private static void BeginKeys()
    {
        // Not drawn last frame (another tab) or the overlay was closed since: the focus is gone.
        var frame = ImGuiNET.ImGui.GetFrameCount();
        if (frame != _lastDrawFrame + 1 || ImGuiRenderer.Opened != _lastOpened) Unfocus();
        _lastDrawFrame = frame;
        _lastOpened = ImGuiRenderer.Opened;

        var io = ImGuiNET.ImGui.GetIO();
        if (_focused && !io.WantTextInput) ImGuiInputHook.ClaimKeyboard();

        // Keys work while focused, and the tab keys also from the filter field.
        _popupAtBegin = ImGuiNET.ImGui.IsPopupOpen("", ImGuiPopupFlags.AnyPopupId);
        if (_popupAtBegin) return;
        if (_filterActive) { TabKeys(io); return; }
        if (!_focused || io.WantTextInput) return;

        if (TabKeys(io)) return;
        if (ActiveDoc() is not null) InspectorKeys(io);
        else                         ListKeys(io);
    }

    /// <summary>End of Draw: a click inside the tab focuses it, anywhere else unfocuses it.</summary>
    private static void EndKeys()
    {
        if (!ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right)
            && !ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Middle))
            return;
        // A click that picked a menu item (anywhere on screen) keeps the focus as it was.
        if (_popupAtBegin) return;
        var m = ImGuiNET.ImGui.GetIO().MousePos;
        bool In(Vector2 a, Vector2 b) => m.X >= a.X && m.X < b.X && m.Y >= a.Y && m.Y < b.Y;
        if ((In(_bodyMin, _bodyMax) || In(_pillsMin, _pillsMax)) && !MouseOverSuggestions)
        {
            _focused = true;
            ImGuiInputHook.ClaimKeyboard();   // before the next input batch
        }
        else
            Unfocus();
    }

    private static bool Key(ImGuiKey k, bool repeat = true) => ImGuiNET.ImGui.IsKeyPressed(k, repeat);

    private static bool TabKeys(ImGuiIOPtr io)
    {
        if (!io.KeyAlt || io.KeyCtrl || io.KeyShift)
        {
            if (Key(ImGuiKey.Escape, false) && !_filterActive)
            {
                if (_activeDoc >= 0)     _activeDoc = -1;
                else if (_multi.Count > 0) ClearMulti();
                else                     Unfocus();
                return true;
            }
            return false;
        }

        // The tabs in order: the list (-1), then the open packets.
        var order = new List<long>(_docs.Count + 1) { -1 };
        order.AddRange(_docs);
        var cur = Math.Max(0, order.IndexOf(_activeDoc));

        if (Key(ImGuiKey.LeftBracket))       _activeDoc = order[(cur - 1 + order.Count) % order.Count];
        else if (Key(ImGuiKey.RightBracket)) _activeDoc = order[(cur + 1) % order.Count];
        else if (Key(ImGuiKey.C, false))
        {
            if (_activeDoc >= 0) CloseDoc(_activeDoc);
        }
        else if (ActiveDoc() is { } p && (Key(ImGuiKey.UpArrow) || Key(ImGuiKey.DownArrow)))
            StepDoc(p.Seq, Key(ImGuiKey.UpArrow) ? -1 : +1);
        else
        {
            for (var i = 0; i < 9; i++)
                if (Key(ImGuiKey._1 + i, false))
                {
                    if (i < order.Count) _activeDoc = order[i];
                    break;
                }
        }
        return true;   // with Alt held, nothing else reacts
    }

    // Alt+↑/↓: the open packet becomes its neighbour in the filtered list (same tab).
    private static void StepDoc(long seq, int dir)
    {
        var i = _matches.BinarySearch(seq);
        if (i < 0) i = ~i - (dir > 0 ? 1 : 0);   // not in the list (filter changed): from where it would be
        var j = i + dir;
        if (j < 0 || j >= _matches.Count) return;
        ReplaceDoc(seq, _matches[j]);
    }

    private static void ListKeys(ImGuiIOPtr io)
    {
        bool ctrl = io.KeyCtrl, shift = io.KeyShift;
        if (shift && !ctrl)
        {
            // Extend the selection from the anchor.
            var d = Key(ImGuiKey.UpArrow) || Key(ImGuiKey.K) ? -1
                  : Key(ImGuiKey.DownArrow) || Key(ImGuiKey.J) ? +1
                  : Key(ImGuiKey.PageUp) ? -_pageRows : Key(ImGuiKey.PageDown) ? +_pageRows
                  : Key(ImGuiKey.Home, false) ? int.MinValue : Key(ImGuiKey.End, false) ? int.MaxValue : 0;
            if (d == 0) return;
            if (_anchor < 0 || (_multi.Count == 0 && _selSeq >= 0)) _anchor = _selSeq;
            MoveSelection(d, keepMulti: true);
            if (_selSeq >= 0) SelectRange(_selSeq);
            return;
        }
        if (shift) return;

        if (ctrl)
        {
            if (Key(ImGuiKey.F, false)) _focusFilter = true;
            else if (Key(ImGuiKey.A, false))
            {
                _multi.Clear();
                foreach (var seq in _matches) _multi.Add(seq);
            }
            else if (Key(ImGuiKey.C, false))
            {
                var ps = SelectedPackets();
                if (ps.Count > 0) Clipboard.Set(string.Join("\n", ps.Select(SummaryLine)));
            }
            else if (Key(ImGuiKey.Enter, false) || Key(ImGuiKey.KeypadEnter, false))
                OpenAll(SelectedPackets());
            return;
        }

        if (Key(ImGuiKey.UpArrow) || Key(ImGuiKey.K)) MoveSelection(-1);
        else if (Key(ImGuiKey.DownArrow) || Key(ImGuiKey.J)) MoveSelection(+1);
        else if (Key(ImGuiKey.PageUp)) MoveSelection(-_pageRows);
        else if (Key(ImGuiKey.PageDown)) MoveSelection(+_pageRows);
        else if (Key(ImGuiKey.Home, false)) MoveSelection(int.MinValue);
        else if (Key(ImGuiKey.End, false)) MoveSelection(int.MaxValue);
        else if (Key(ImGuiKey.Enter, false) || Key(ImGuiKey.KeypadEnter, false)) ToggleSelected();
        else if (Key(ImGuiKey.Slash, false)) _focusFilter = true;
        else if (Key(ImGuiKey.Space, false)) NetCapture.Paused = !NetCapture.Paused;
    }

    private static void InspectorKeys(ImGuiIOPtr io)
    {
        bool ctrl = io.KeyCtrl, shift = io.KeyShift;
        if (ctrl)
        {
            if (Key(ImGuiKey.C, false) && ActiveDoc() is { } p)
            {
                if (shift) { if (p.Bytes is { } b) Clipboard.Set(HexText(b)); }
                else if (p.Message is not null) Clipboard.Set(NetDecode.ToText(NetDecode.Root(p)));
            }
            return;
        }
        if (shift) return;

        if (Key(ImGuiKey._1, false) || Key(ImGuiKey.Keypad1, false)) _showFields = !_showFields;
        else if (Key(ImGuiKey._2, false) || Key(ImGuiKey.Keypad2, false)) _showRelated = !_showRelated;
        else if (Key(ImGuiKey._3, false) || Key(ImGuiKey.Keypad3, false)) _showBytes = !_showBytes;
        else if (Key(ImGuiKey.UpArrow) || Key(ImGuiKey.K)) _inspLines -= 1f;
        else if (Key(ImGuiKey.DownArrow) || Key(ImGuiKey.J)) _inspLines += 1f;
        else if (Key(ImGuiKey.PageUp)) _inspPages -= 1f;
        else if (Key(ImGuiKey.PageDown)) _inspPages += 1f;
        else if (Key(ImGuiKey.Home, false)) _inspJump = -2;
        else if (Key(ImGuiKey.End, false)) _inspJump = 2;
        else if (Key(ImGuiKey.Space, false)) NetCapture.Paused = !NetCapture.Paused;
    }

    /// <summary>Inside the inspector child: applies the scroll the keys asked for.</summary>
    private static void ApplyInspectorScroll()
    {
        if (_inspLines == 0f && _inspPages == 0f && _inspJump == 0) return;
        var y = ImGuiNET.ImGui.GetScrollY()
              + _inspLines * ImGuiNET.ImGui.GetTextLineHeightWithSpacing()
              + _inspPages * ImGuiNET.ImGui.GetWindowHeight() * 0.9f;
        if (_inspJump < 0) y = 0f;
        if (_inspJump > 0) y = ImGuiNET.ImGui.GetScrollMaxY();
        ImGuiNET.ImGui.SetScrollY(Math.Clamp(y, 0f, ImGuiNET.ImGui.GetScrollMaxY()));
        _inspLines = _inspPages = 0f;
        _inspJump = 0;
    }

    private static int SelectedRow()
    {
        if (_selSeq < 0) return -1;
        for (var i = 0; i < _rows.Count; i++)
            if (_rows[i].Seq == _selSeq && _rows[i].Node == _selNode) return i;
        // A field of a folded packet: the packet's row becomes the selection.
        for (var i = 0; i < _rows.Count; i++)
            if (_rows[i].Seq == _selSeq && _rows[i].Node is null)
            {
                _selNode = null;
                return i;
            }
        return -1;
    }

    // The selected packet left the list (evicted, filtered out): where it would be.
    private static int RowAfterSelection()
    {
        for (var i = 0; i < _rows.Count; i++)
            if (_rows[i].Seq > _selSeq) return i;
        return _rows.Count - 1;
    }

    private static void MoveSelection(int delta, bool keepMulti = false)
    {
        if (!keepMulti) ClearMulti();
        if (_rows.Count == 0) return;
        var cur = SelectedRow();
        var next = delta switch
        {
            int.MinValue => 0,
            int.MaxValue => _rows.Count - 1,
            _ when cur < 0 && _selSeq < 0 => delta < 0 ? _rows.Count - 1 : 0,
            _ when cur < 0 => Math.Clamp(RowAfterSelection() + (delta < 0 ? delta : delta - 1), 0, _rows.Count - 1),
            _ => Math.Clamp(cur + delta, 0, _rows.Count - 1),
        };
        // On the newest row the list follows the newest packets again, and the selection with it.
        var last = next == _rows.Count - 1;
        Select(_rows[next].Seq, _rows[next].Node, follows: last);
        if (!keepMulti) _anchor = _rows[next].Seq;
        _selHold = !last;
        if (last) _followNow = true;
        else      _scrollToSel = true;
    }

    private static void ToggleSelected()
    {
        var i = SelectedRow();
        if (i < 0) return;
        var row = _rows[i];
        if (row.Node is { } node)
        {
            if (!node.CanExpand) return;
            node.Expanded = !node.Expanded;
            _rowsDirty = true;
        }
        else if (NetCapture.Get(row.Seq) is { } p)
            ToggleOpen(p);
    }

    /// <summary>Opens packets in the inspector (at most as many as it holds); the first becomes active.</summary>
    private static void OpenAll(List<Packet> packets)
    {
        if (packets.Count == 0) return;
        var open = packets.TakeLast(MaxDocs).Select(p => p.Seq).ToList();
        // Make room first: opening one must never close another of the same batch.
        foreach (var seq in _docs.Where(d => !open.Contains(d)).Take(Math.Max(0, _docs.Count + open.Count(s => !_docs.Contains(s)) - MaxDocs)).ToList())
            CloseDoc(seq);
        foreach (var seq in open) OpenDoc(seq);
        _activeDoc = open.FirstOrDefault(_docs.Contains, -1);
    }

    private static string SummaryLine(Packet p)
        => $"#{p.Seq} {NetFeature.Time(p)} {(p.Dir == NetDir.In ? "in" : "out")} {p.Type.Name} {p.Size} B {NetDecode.Summary(p)}";

    private static string HexText(byte[] bytes)
        => string.Join("\n", NetDecode.HexLines(bytes).Select(l => $"{l.Offset}  {l.Hex} {l.Ascii}"));
}
