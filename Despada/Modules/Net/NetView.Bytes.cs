// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Despada.ImGui;
using Despada.ImGui.UserInterface;
using ImGuiNET;

namespace Despada.Modules.Net;

// The bytes pane of the inspector: the packet as a text stream (strings readable, the rest as
// short hex runs, zero runs folded, length prefixes as badges, field bytes underlined) or as a
// plain hex dump (offsets and bytes in groups of 8, no ASCII column); "hex | text" switches. Hovering a run shows its offset, field and readings;
// a run that holds a field lights that field's row, and the selected field lights its bytes.
internal static partial class NetView
{
    private const int MaxStreamBytes = 64 * 1024;

    private static ByteStream?     _stream;
    private static long            _streamSeq = -1;
    private static byte[]?         _streamSrc;
    private static FieldNode?      _streamTree;
    private static List<ByteLine>? _lines;
    private static int             _linesCols;
    private static bool            _bytesText = true;

    // Field ↔ bytes: the clicked field; the field whose bytes are under the mouse (lights its
    // row); the row under the mouse (lights its bytes). Both hovers are last frame's.
    private static FieldNode? _selField;
    private static FieldNode? _hoverField, _hoverFieldNext;
    private static FieldNode? _hoverRow, _hoverRowNext;

    // A byte selection [SelA, SelB): dragged over bytes (hex) or runs (text), a double click takes
    // a run, Ctrl+C copies it as hex, the right button offers the other forms.
    private static int  _selA = -1, _selB = -1;
    private static (int Start, int End) _hoverBytes = (-1, -1);   // under the mouse this frame
    private static (int Start, int End) _pressBytes = (-1, -1);   // where the button went down
    private static bool _dragged;
    private static bool _bytesMenu;
    private static int  _scrollToByte = -1;   // the finder jumped to a byte: the pane scrolls to its line once
    private static long _scrollToByteSeq = -1;

    private static readonly string[] HexOf   = Enumerable.Range(0, 256).Select(i => i.ToString("x2")).ToArray();

    private static void ForgetStream()
    {
        _selA = _selB = -1;
        _pressBytes = (-1, -1);
        _stream = null;
        _streamSeq = -1;
        _streamSrc = null;
        _streamTree = null;
        _lines = null;
        _selField = null;
        _hoverField = _hoverFieldNext = null;
        _hoverRow = _hoverRowNext = null;
    }

    private static void EnsureStream(Packet p, byte[] bytes)
    {
        if (_streamSeq == p.Seq && _stream is not null && ReferenceEquals(_streamSrc, bytes) && ReferenceEquals(_streamTree, _docTree)) return;
        // Another packet: the old selection means nothing, unless a finder jump brought one for it.
        if (_streamSeq != p.Seq && _scrollToByteSeq != p.Seq) { _selA = _selB = -1; _scrollToByte = -1; }
        _scrollToByteSeq = -1;
        _streamSeq  = p.Seq;
        _streamSrc  = bytes;
        _streamTree = _docTree;
        var shown = bytes.Length > MaxStreamBytes ? bytes.AsSpan(0, MaxStreamBytes).ToArray() : bytes;
        try { _stream = ByteStream.Build(shown, _docTree is { CanExpand: true } ? _docTree : null); }
        catch (Exception ex)
        {
            ImGuiRenderer.ShowToast("network", $"bytes: {ex.Message}", 3f);
            _stream = ByteStream.Build(shown, null);
        }
        _lines = null;
        if (_selB > shown.Length) { _selA = Math.Min(_selA, shown.Length - 1); _selB = shown.Length; }
        if (_scrollToByte >= shown.Length) _scrollToByte = -1;
    }

    /// <summary>"bytes  326 B · 2 strings · 5 fields" with the hex | text switch at the right.</summary>
    private static void BytesHead(byte[]? bytes, bool withSwitch)
    {
        ImGuiNET.ImGui.Dummy(new Vector2(0f, 6f * D));
        var pos   = ImGuiNET.ImGui.GetCursorScreenPos();
        var lineH = ImGuiNET.ImGui.GetTextLineHeight();
        var right = pos.X + ImGuiNET.ImGui.GetContentRegionAvail().X;
        var dl    = ImGuiNET.ImGui.GetWindowDrawList();

        string? info = null;
        if (bytes is not null)
        {
            info = NetFeature.Bytes(bytes.Length);
            if (_stream is not null)
            {
                if (_stream.Strings > 0) info += $" · {_stream.Strings} string{(_stream.Strings == 1 ? "" : "s")}";
                if (_stream.Matched > 0) info += $" · {_stream.Matched} field{(_stream.Matched == 1 ? "" : "s")}";
            }
        }

        var chips   = withSwitch && bytes is not null;
        var chipsX  = right - Widgets.SegmentedWidth(["hex", "text"]);
        const string caption = "bytes";
        dl.AddText(pos, Theme.ToU32(Theme.P.Sub), caption);
        if (info is not null)
        {
            // The info is clipped before the switch, never under it.
            dl.PushClipRect(pos, new Vector2(chips ? chipsX - 4f * D : right, pos.Y + lineH), true);
            dl.AddText(pos + new Vector2(ImGuiNET.ImGui.CalcTextSize(caption + "  ").X, 0f), Theme.ToU32(Theme.P.Muted), info);
            dl.PopClipRect();
        }
        ImGuiNET.ImGui.Dummy(new Vector2(1f, lineH));

        if (chips)
        {
            var keep = ImGuiNET.ImGui.GetCursorScreenPos();
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(chipsX, pos.Y - (Widgets.SegmentedH - lineH) * 0.5f));
            var mode = _bytesText ? 1 : 0;
            if (Widgets.Segmented("##bytes-mode", ["hex", "text"], ref mode)) _bytesText = mode == 1;
            ImGuiNET.ImGui.SetCursorScreenPos(keep);
        }
    }


    /// <summary>The bytes themselves, in the current mode; call inside the pane's child window.</summary>
    private static void DrawBytesBody(Packet p, byte[]? bytes)
    {
        if (bytes is null)
        {
            Text("no longer kept", Theme.P.Muted);
            ImGuiNET.ImGui.NewLine();
            return;
        }
        EnsureStream(p, bytes);
        var stream = _stream!;

        ByteToken? hover = null;
        _hoverBytes = (-1, -1);
        if (_bytesText) DrawStream(stream, ref hover);
        else            DrawHexDump(stream, ref hover);

        if (bytes.Length > stream.Bytes.Length)
        {
            Text($"… {bytes.Length - stream.Bytes.Length:N0} more bytes", Theme.P.Muted);
            ImGuiNET.ImGui.NewLine();
        }

        SelectBytes(stream, hover);
        if (hover is not null)
        {
            _hoverFieldNext = hover.Field;
            if (!_dragged) DrawByteTip(stream, hover);
        }
        DrawBytesMenu(stream);
    }

    // Press and drag selects; a plain click (no drag) still picks the run's field; a double
    // click takes the whole run; the right button selects the run under it (unless inside the
    // selection) and opens the menu.
    private static void SelectBytes(ByteStream stream, ByteToken? hover)
    {
        var over = _hoverBytes.Start >= 0;
        if (over && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _pressBytes = _hoverBytes;
            _dragged = false;
            if (ImGuiNET.ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left) && hover is not null)
            {
                (_selA, _selB) = (hover.Offset, hover.End);
                _pressBytes = (-1, -1);
            }
        }
        if (_pressBytes.Start >= 0 && ImGuiNET.ImGui.IsMouseDown(ImGuiMouseButton.Left) && over && _hoverBytes != _pressBytes)
        {
            _dragged = true;
            _selA = Math.Min(_pressBytes.Start, _hoverBytes.Start);
            _selB = Math.Max(_pressBytes.End, _hoverBytes.End);
        }
        if (_pressBytes.Start >= 0 && ImGuiNET.ImGui.IsMouseReleased(ImGuiMouseButton.Left))
        {
            if (!_dragged)
            {
                _selA = _selB = -1;
                if (hover?.Field is { } f) _selField = ReferenceEquals(_selField, f) ? null : f;
            }
            _pressBytes = (-1, -1);
            _dragged = false;
        }
        if (over && ImGuiNET.ImGui.IsMouseClicked(ImGuiMouseButton.Right))
        {
            var inside = _selA >= 0 && _hoverBytes.Start >= _selA && _hoverBytes.End <= _selB;
            if (!inside && hover is not null) (_selA, _selB) = (hover.Offset, hover.End);
            if (_selA >= 0) _bytesMenu = true;
        }
    }

    private static bool HasByteSelection => _selA >= 0 && _selB > _selA;

    private static void DrawBytesMenu(ByteStream stream)
    {
        if (_bytesMenu) { _bytesMenu = false; ImGuiNET.ImGui.OpenPopup("##bytes-menu"); }
        if (!Widgets.BeginMenu("##bytes-menu", 260f)) return;
        if (!HasByteSelection) ImGuiNET.ImGui.CloseCurrentPopup();
        else
        {
            var n = _selB - _selA;
            Widgets.MenuCaption(n == 1 ? $"0x{_selA:x2} · 1 B" : $"0x{_selA:x2}–0x{_selB - 1:x2} · {n} B");
            if (Widgets.MenuItem("copy hex", Icon.Copy)) Clipboard.Set(SelectionHex(stream));
            if (Widgets.MenuItem("copy text", Icon.Copy)) Clipboard.Set(SelectionText(stream));
            if (Widgets.MenuItem("copy as C# array", Icon.Copy)) Clipboard.Set(SelectionCSharp(stream));
            if (Widgets.MenuItem("copy base64", Icon.Copy)) Clipboard.Set(Convert.ToBase64String(stream.Bytes, _selA, n));
            Widgets.MenuSeparator();
            if (Widgets.MenuItem("clear selection", Icon.Cross)) _selA = _selB = -1;
        }
        Widgets.EndMenu();
    }

    private static string SelectionHex(ByteStream stream)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = _selA; i < _selB; i++)
        {
            if (i > _selA) sb.Append((i - _selA) % 16 == 0 ? '\n' : ' ');
            sb.Append(HexOf[stream.Bytes[i]]);
        }
        return sb.ToString();
    }

    // As the text mode shows it: strings as text, the rest as hex, run by run.
    private static string SelectionText(ByteStream stream)
    {
        var parts = new List<string>();
        foreach (var t in stream.Tokens)
        {
            if (t.End <= _selA) continue;
            if (t.Offset >= _selB) break;
            var from = Math.Max(t.Offset, _selA);
            var to   = Math.Min(t.End, _selB);
            if (t.Kind == ByteKind.Str) { var (c0, c1) = t.CharRange(from, to); parts.Add(t.PlainText[c0..c1]); }
            else parts.Add(string.Join(' ', Enumerable.Range(from, to - from).Select(i => HexOf[stream.Bytes[i]])));
        }
        return string.Join(' ', parts);
    }

    private static string SelectionCSharp(ByteStream stream)
    {
        var sb = new System.Text.StringBuilder("new byte[] { ");
        for (var i = _selA; i < _selB; i++)
        {
            if (i > _selA) sb.Append(", ");
            sb.Append("0x").Append(HexOf[stream.Bytes[i]]);
        }
        return sb.Append(" }").ToString();
    }

    private static uint SelectionFill => Theme.ToU32(Theme.WithAlpha(Theme.P.Accent, 0.28f));

    private static void DrawByteTip(ByteStream stream, ByteToken t)
    {
        BeginTip();
        var range = t.Length == 1 ? $"0x{t.Offset:x2}" : $"0x{t.Offset:x2}–0x{t.End - 1:x2}";
        Text($"{range} · {t.Length} B", Theme.P.Muted);
        ImGuiNET.ImGui.NewLine();
        if (t.Field is not null)
        {
            Text(t.Field.Name, Theme.P.Sub);
            Text("  " + t.FieldValue, Theme.P.Fg);
            if (t.Field.TypeName is { } tn) Text("  " + tn, Theme.P.Muted);
            ImGuiNET.ImGui.NewLine();
        }
        switch (t.Kind)
        {
            case ByteKind.Str:
                Text(t.Chars == t.Length ? $"string · {t.Length} chars" : $"string · {t.Chars} chars · {t.Length} B", Theme.P.Muted);
                ImGuiNET.ImGui.NewLine();
                break;
            case ByteKind.Len:
                Text("length prefix of the string after it", Theme.P.Muted);
                ImGuiNET.ImGui.NewLine();
                break;
            case ByteKind.Zero:
                Text($"{t.Length} zero bytes", Theme.P.Muted);
                ImGuiNET.ImGui.NewLine();
                break;
            default:
                Text(ByteStream.Decode(stream.Bytes, t.Offset, Math.Max(t.Length, Math.Min(4, stream.Bytes.Length - t.Offset))), Theme.P.Muted);
                ImGuiNET.ImGui.NewLine();
                break;
        }
        EndTip();
    }

    // The background of a run: the selected field's bytes, the hovered field's, a string's tint.
    private static Vector4? RunFill(ByteToken t)
    {
        if (t.Field is not null && ReferenceEquals(t.Field, _selField)) return Theme.WithAlpha(Theme.P.Accent2, 0.22f);
        if (t.Field is not null && ReferenceEquals(t.Field, _hoverRow))  return Theme.Sel;
        if (t.Kind == ByteKind.Str) return Theme.WithAlpha(Theme.P.Green, 0.10f);
        return null;
    }

    // ── text stream ──────────────────────────────────────────────────────────────────────────

    private static void DrawStream(ByteStream stream, ref ByteToken? hover)
    {
        var cw     = ImGuiNET.ImGui.CalcTextSize("M").X;
        var lineH  = ImGuiNET.ImGui.GetTextLineHeight();
        var rowH   = ImGuiNET.ImGui.GetTextLineHeightWithSpacing();
        var avail  = ImGuiNET.ImGui.GetContentRegionAvail().X;
        var gutter = stream.Bytes.Length > 0xFFFF ? 7 : 5;
        var cols   = (int)MathF.Floor(avail / cw) - gutter;
        if (_lines is null || _linesCols != cols)
        {
            _lines = stream.Wrap(cols);
            _linesCols = cols;
        }

        var dl      = ImGuiNET.ImGui.GetWindowDrawList();
        var mouse   = ImGuiNET.ImGui.GetIO().MousePos;
        var canHover = !MouseOverSuggestions && ImGuiNET.ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        var offCol  = Theme.ToU32(Theme.WithAlpha(Theme.P.Muted, 0.6f));
        var ulCol   = Theme.ToU32(Theme.P.Accent);
        var fmt     = gutter == 7 ? "x6" : "x4";

        for (var li = 0; li < _lines.Count; li++)
        {
            var line = _lines[li];
            if (_scrollToByte >= 0 && line.Offset <= _scrollToByte && (li + 1 == _lines.Count || _lines[li + 1].Offset > _scrollToByte))
            {
                ImGuiNET.ImGui.SetScrollHereY(0.3f);
                _scrollToByte = -1;
            }
            if (!ImGuiNET.ImGui.IsRectVisible(new Vector2(1f, rowH)))
            {
                ImGuiNET.ImGui.Dummy(new Vector2(1f, lineH));
                continue;
            }
            var pos = ImGuiNET.ImGui.GetCursorScreenPos();
            dl.AddText(pos, offCol, line.Offset.ToString(fmt));
            var x0 = pos.X + gutter * cw;

            foreach (var (t, from, to, col) in line.Parts)
            {
                var px = x0 + col * cw;
                var w  = (to - from) * cw;
                var min = new Vector2(px, pos.Y);
                var max = new Vector2(px + w, pos.Y + lineH);
                if (RunFill(t) is { } fill) dl.AddRectFilled(min - new Vector2(0f, 1f * D), max + new Vector2(0f, 1f * D), Theme.ToU32(fill), 3f * D);
                if (HasByteSelection && t.End > _selA && t.Offset < _selB)
                {
                    // A string's characters are its bytes: only the selected ones; other runs whole.
                    var sMin = min; var sMax = max;
                    if (t.Kind == ByteKind.Str)
                    {
                        var (r0, r1) = t.CharRange(_selA, _selB);
                        var c0 = Math.Max(from, r0); var c1 = Math.Min(to, r1);
                        if (c1 <= c0) goto text;
                        sMin.X = px + (c0 - from) * cw; sMax.X = px + (c1 - from) * cw;
                    }
                    dl.AddRectFilled(sMin - new Vector2(0f, 1f * D), sMax + new Vector2(0f, 1f * D), SelectionFill, 3f * D);
                }
                text:
                if (t.Kind == ByteKind.Str)
                {
                    // The slice [from, to) of the string, span by span.
                    var at = 0;
                    var x  = px;
                    foreach (var span in t.Text)
                    {
                        var s0 = Math.Max(from, at);
                        var s1 = Math.Min(to, at + span.Text.Length);
                        if (s1 > s0)
                        {
                            var piece = span.Text.Substring(s0 - at, s1 - s0);
                            dl.AddText(new Vector2(x, pos.Y), Theme.ToU32(TreeRows.Color(span.Tone)), piece);
                            x += piece.Length * cw;
                        }
                        at += span.Text.Length;
                    }
                }
                else
                {
                    var span  = t.Text[0];
                    var color = t.Kind == ByteKind.Zero ? Theme.WithAlpha(Theme.P.Muted, 0.6f) : TreeRows.Color(span.Tone);
                    dl.AddText(min, Theme.ToU32(color), span.Text);
                    if (t.Kind == ByteKind.Num) Gfx.HLine(dl, px, px + w, pos.Y + lineH - 1f * D, ulCol);
                }

                if (canHover && mouse.X >= min.X && mouse.X < max.X && mouse.Y >= min.Y && mouse.Y < max.Y)
                {
                    hover = t;
                    if (t.Kind == ByteKind.Str)
                    {
                        var c = from + (int)((mouse.X - px) / cw);
                        _hoverBytes = (t.ByteAt(c), t.ByteAt(c + 1));
                    }
                    else _hoverBytes = (t.Offset, t.End);
                }
            }
            ImGuiNET.ImGui.Dummy(new Vector2(MathF.Max(1f, avail), lineH));
        }
    }

    // ── hex dump ─────────────────────────────────────────────────────────────────────────────

    private static void DrawHexDump(ByteStream stream, ref ByteToken? hover)
    {
        var cw    = ImGuiNET.ImGui.CalcTextSize("M").X;
        var lineH = ImGuiNET.ImGui.GetTextLineHeight();
        var rowH  = ImGuiNET.ImGui.GetTextLineHeightWithSpacing();
        var avail = ImGuiNET.ImGui.GetContentRegionAvail().X;
        var cols  = (int)MathF.Floor(avail / cw);
        var gutter = stream.Bytes.Length > 0xFFFF ? 7 : 5;
        // offset, then hex (3 columns per byte, a gap after each 8). No ASCII column: the text
        // mode reads strings, and the hover names the byte. Groups of 8 keep the offsets round.
        var per = 8;
        while (per < 64 && cols >= gutter + (per + 8) * 3 + (per + 8) / 8 - 1) per += 8;
        var fmt = gutter == 7 ? "x6" : "x4";
        // The row is justified: what is left after the last group (always under one group, since
        // the count is a multiple of 8) is spread between the bytes, at most a character each, so
        // the dump spans the pane and the offsets stay round.
        var usedW   = (gutter + per * 3 - 1 + (per - 1) / 8) * cw;
        var stretch = per > 1 ? Math.Clamp((avail - usedW) / (per - 1), 0f, cw) : 0f;

        var dl       = ImGuiNET.ImGui.GetWindowDrawList();
        var mouse    = ImGuiNET.ImGui.GetIO().MousePos;
        var canHover = !MouseOverSuggestions && ImGuiNET.ImGui.IsWindowHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem);
        var offCol   = Theme.ToU32(Theme.WithAlpha(Theme.P.Muted, 0.6f));
        var ulCol    = Theme.ToU32(Theme.P.Accent);
        var bytes    = stream.Bytes;

        for (var off = 0; off < bytes.Length; off += per)
        {
            if (_scrollToByte >= off && _scrollToByte < off + per) { ImGuiNET.ImGui.SetScrollHereY(0.3f); _scrollToByte = -1; }
            if (!ImGuiNET.ImGui.IsRectVisible(new Vector2(1f, rowH)))
            {
                ImGuiNET.ImGui.Dummy(new Vector2(1f, lineH));
                continue;
            }
            var pos = ImGuiNET.ImGui.GetCursorScreenPos();
            dl.AddText(pos, offCol, off.ToString(fmt));
            var hx = pos.X + gutter * cw;
            var n  = Math.Min(per, bytes.Length - off);
            for (var k = 0; k < n; k++)
            {
                var i = off + k;
                var t = stream.At(i);
                var x = hx + (k * 3 + k / 8) * cw + k * stretch;
                var hexMin = new Vector2(x, pos.Y);
                var hexMax = new Vector2(x + 2f * cw, pos.Y + lineH);

                var fill = t is null ? null : RunFill(t);
                if (fill is { } f)
                {
                    var u = Theme.ToU32(f);
                    // Fill runs together across the byte gaps inside one token.
                    var ext = t!.End > i + 1 && k < n - 1 ? cw + stretch + (k % 8 == 7 ? cw : 0f) : 0f;
                    dl.AddRectFilled(hexMin - new Vector2(0f, 1f * D), hexMax + new Vector2(ext, 1f * D), u);
                }
                if (i >= _selA && i < _selB)
                {
                    var ext = i + 1 < _selB && k < n - 1 ? cw + stretch + (k % 8 == 7 ? cw : 0f) : 0f;
                    dl.AddRectFilled(hexMin - new Vector2(0f, 1f * D), hexMax + new Vector2(ext, 1f * D), SelectionFill);
                }

                var tone = t?.Kind switch
                {
                    ByteKind.Str  => Theme.P.Green,
                    ByteKind.Len  => Theme.P.Muted,
                    ByteKind.Zero => Theme.WithAlpha(Theme.P.Muted, 0.6f),
                    ByteKind.Num  => Theme.P.Sub,
                    _             => Theme.P.Sub,
                };
                dl.AddText(hexMin, Theme.ToU32(tone), HexOf[bytes[i]]);
                if (t?.Kind == ByteKind.Num) Gfx.HLine(dl, x, x + 2f * cw + (t.End > i + 1 && k < n - 1 ? cw + stretch + (k % 8 == 7 ? cw : 0f) : 0f), pos.Y + lineH - 1f * D, ulCol);

                if (canHover && mouse.Y >= pos.Y && mouse.Y < pos.Y + lineH && mouse.X >= hexMin.X && mouse.X < hexMax.X + cw + stretch)
                {
                    hover = t;
                    _hoverBytes = (i, i + 1);
                }
            }
            ImGuiNET.ImGui.Dummy(new Vector2(MathF.Max(1f, avail), lineH));
        }
    }
}
