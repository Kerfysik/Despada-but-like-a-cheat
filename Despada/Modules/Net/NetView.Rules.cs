// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using Despada.ImGui;
using Despada.ImGui.UserInterface;
using ImGuiNET;

namespace Despada.Modules.Net;

// The rules button (a gear with the count) and its panel: every rule as a row (flag, filter,
// action, hits, ×). Rules are made with "/" in the search bar or from a packet's menu.
internal static partial class NetView
{
    // A gear with the rule count beside it; the panel opens under it.
    private static void DrawRulesButton()
    {
        var n   = NetActive.Rules.Count;
        var pos = ImGuiNET.ImGui.GetCursorScreenPos();
        var h   = Widgets.ButtonH;
        var countW = n > 0 ? ImGuiNET.ImGui.CalcTextSize(n.ToString()).X + 6f : 0f;
        var w   = h + countW;
        ImGuiNET.ImGui.InvisibleButton("##rules-hit", new Vector2(w, h));
        var clicked = ImGuiNET.ImGui.IsItemClicked();
        var hovered = ImGuiNET.ImGui.IsItemHovered() || ImGuiNET.ImGui.IsPopupOpen("##rules-panel");
        var dl  = ImGuiNET.ImGui.GetWindowDrawList();
        var hot = Widgets.Hover(ImGuiNET.ImGui.GetItemID(), hovered);
        if (hot > 0.01f)
            dl.AddRectFilled(pos, pos + new Vector2(w, h), Theme.ToU32(Theme.WithAlpha(Theme.RaisedFill, Theme.RaisedFill.W * hot)), Widgets.ButtonRound);
        Widgets.DrawIcon(dl, pos + new Vector2(h * 0.5f, h * 0.5f), 18f, Icon.Gear, Theme.Mix(Theme.P.Muted, Theme.P.Sub, hot));
        if (n > 0)
            dl.AddText(new Vector2(pos.X + h - 2f, pos.Y + (h - ImGuiNET.ImGui.GetTextLineHeight()) * 0.5f), Theme.ToU32(Theme.P.Accent), n.ToString());
        if (clicked) ImGuiNET.ImGui.OpenPopup("##rules-panel");

        ImGuiNET.ImGui.SetNextWindowPos(pos + new Vector2(0f, h + 6f), ImGuiCond.Always);
        ImGuiNET.ImGui.SetNextWindowSizeConstraints(new Vector2(0f, 0f), new Vector2(ImGuiNET.ImGui.GetWindowSize().X - 40f, 700f * D));
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupRounding, Widgets.MenuRound);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.PopupBorderSize, 1f);
        ImGuiNET.ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(16f, 10f));
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.PopupBg, Theme.P.Pop);
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Border, Theme.P.Surface1);
        var open = ImGuiNET.ImGui.BeginPopup("##rules-panel");
        if (open)
        {
            DrawRulesPanel();
            ImGuiNET.ImGui.EndPopup();
        }
        ImGuiNET.ImGui.PopStyleColor(2);
        ImGuiNET.ImGui.PopStyleVar(3);
    }

    private const float RuleRowH = 40f;

    // One line per rule, fixed columns: the flag, the filter, the action (its dot and word, then
    // the detail), the hits, ×. The popup is sized here, not by ImGui's auto-size.
    private static void DrawRulesPanel()
    {
        var rules = NetActive.Rules;
        var cw    = ImGuiNET.ImGui.CalcTextSize("M").X;
        var lineH = ImGuiNET.ImGui.GetTextLineHeight();
        var dl    = ImGuiNET.ImGui.GetWindowDrawList();

        // Column widths from the content, within bounds, so the panel doesn't grow past the window.
        float filterW = cw * 28f, actionW = cw * 12f;
        foreach (var r in rules)
        {
            filterW = MathF.Max(filterW, ImGuiNET.ImGui.CalcTextSize(FilterText(r) + Notes(r)).X);   // the column holds everything drawn in it
            actionW = MathF.Max(actionW, 18f + ImGuiNET.ImGui.CalcTextSize(ActionText(r)).X);
        }
        filterW = MathF.Min(filterW, cw * 60f);
        actionW = MathF.Min(actionW, cw * 30f);
        var hitsW = cw * 4f;
        const float gap = 14f;
        var xFilter = Widgets.CheckboxSize + gap;
        var xAction = xFilter + filterW + gap;
        var xHits   = xAction + actionW + gap;
        var xPin    = xHits + hitsW + 20f;
        var xClose  = xPin + 22f + 10f;
        var rowW    = xClose + cw * 2f;

        if (rules.Count == 0)
        {
            ImGuiNET.ImGui.Dummy(new Vector2(rowW, 0f));
            Text("no rules", Theme.P.Muted);
            return;
        }

        NetRule? remove = null;
        var store = false;
        for (var i = 0; i < rules.Count; i++)
        {
            var r   = rules[i];
            var pos = ImGuiNET.ImGui.GetCursorScreenPos();
            var ty  = pos.Y + (RuleRowH - lineH) * 0.5f;
            ImGuiNET.ImGui.PushID(i);

            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + (RuleRowH - ImGuiNET.ImGui.GetFrameHeight()) * 0.5f));
            var on = r.Enabled;
            if (Widgets.Checkbox("##on", ref on, ActionColor(r.Action))) { r.Enabled = on; store |= r.Pinned; }
            var alpha = on ? 1f : 0.55f;

            // The filter, coloured like the bar's tokens, clipped to its column.
            dl.PushClipRect(new Vector2(pos.X + xFilter, pos.Y), new Vector2(pos.X + xFilter + filterW, pos.Y + RuleRowH), true);
            var x = pos.X + xFilter;
            foreach (var t in r.Query.Tokens)
            {
                x = Put(dl, x, ty, TokenHead(t), Theme.WithAlpha(t.Negated ? Theme.P.Red : Theme.P.Muted, alpha));
                x = Put(dl, x, ty, t.Value + " ", Theme.WithAlpha(TokenColor(t), alpha));
            }
            if (r.Query.Tokens.Count == 0) x = Put(dl, x, ty, "(empty: matches nothing) ", Theme.P.Red);
            else if (r.Invalid)            x = Put(dl, x, ty, "(unknown token) ", Theme.P.Red);
            if (r.Error is { } err)        x = Put(dl, x, ty, $"⚠ {err} ", Theme.P.Red);
            if (r.Unsafe)                  Put(dl, x, ty, NetRule.UnsafeWord, Theme.P.Red);
            else if (r.WouldSkipState)     Put(dl, x, ty, "(state passes: !unsafe)", Theme.P.Red);
            dl.PopClipRect();

            // The action: a dot in its colour, the word, the detail; then the hits.
            x = pos.X + xAction;
            dl.AddCircleFilled(new Vector2(x + 5f, ty + lineH * 0.5f), 4.5f, Theme.ToU32(Theme.WithAlpha(ActionColor(r.Action), alpha)));
            dl.PushClipRect(new Vector2(x, pos.Y), new Vector2(x + actionW, pos.Y + RuleRowH), true);
            Put(dl, x + 18f, ty, ActionText(r), Theme.WithAlpha(Theme.P.Sub, alpha));
            dl.PopClipRect();
            var hits = r.Hits.ToString();
            dl.AddText(new Vector2(pos.X + xHits + hitsW - ImGuiNET.ImGui.CalcTextSize(hits).X, ty), Theme.ToU32(Theme.WithAlpha(Theme.P.Muted, alpha)), hits);

            // The pin: the rule comes back after a restart.
            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(pos.X + xPin, pos.Y + (RuleRowH - 22f) * 0.5f));
            ImGuiNET.ImGui.InvisibleButton("##pin", new Vector2(22f, 22f));
            if (ImGuiNET.ImGui.IsItemClicked()) { r.Pinned = !r.Pinned; store = true; }
            var pinHot = Widgets.Hover(ImGuiNET.ImGui.GetItemID(), ImGuiNET.ImGui.IsItemHovered());
            Widgets.DrawIcon(dl, pos + new Vector2(xPin + 11f, RuleRowH * 0.5f), 16f, Icon.Pin,
                r.Pinned ? Theme.Mix(Theme.P.Accent, Theme.P.Fg, pinHot * 0.4f) : Theme.WithAlpha(Theme.P.Muted, 0.45f + 0.55f * pinHot), 1.6f);

            ImGuiNET.ImGui.SetCursorScreenPos(new Vector2(pos.X + xClose, pos.Y));
            if (ImGuiNET.ImGui.InvisibleButton("##x", new Vector2(cw * 2f, RuleRowH))) remove = r;
            dl.AddText(new Vector2(pos.X + xClose + cw * 0.5f, ty), Theme.ToU32(ImGuiNET.ImGui.IsItemHovered() ? Theme.P.Fg : Theme.P.Muted), "×");

            ImGuiNET.ImGui.SetCursorScreenPos(pos);
            ImGuiNET.ImGui.Dummy(new Vector2(rowW, RuleRowH));
            ImGuiNET.ImGui.PopID();
        }
        if (remove is not null) { rules.Remove(remove); store |= remove.Pinned; }
        if (store) NetFeature.StoreRules();
    }

    private static float Put(ImDrawListPtr dl, float x, float y, string text, Vector4 color)
    {
        dl.AddText(new Vector2(x, y), Theme.ToU32(color), text);
        return x + ImGuiNET.ImGui.CalcTextSize(text).X;
    }

    private static string FilterText(NetRule r)
        => r.Query.Tokens.Count == 0 ? "(empty: matches nothing) " : string.Concat(r.Query.Tokens.Select(t => TokenHead(t) + t.Value + " "));

    // What the row draws after the tokens, in the same column.
    private static string Notes(NetRule r)
        => (r.Query.Tokens.Count > 0 && r.Invalid ? "(unknown token) " : "")
         + (r.Error is { } err ? $"⚠ {err} " : "")
         + (r.Unsafe ? NetRule.UnsafeWord : r.WouldSkipState ? "(state passes: !unsafe)" : "");

    private static string ActionText(NetRule r)
        => r.Detail.Length > 0 ? $"{r.ActionLabel}  {r.Detail}" : r.ActionLabel;

    /// <summary>
    /// Keeps a type out of the log (a mute rule, once per type). Its chips leave the view filter:
    /// hidden or shown, a muted type has nothing left to show.
    /// </summary>
    private static void MuteType(string type)
    {
        _chips.RemoveAll(c => c.Key == "type" && c.Value == type);
        var filter = $"type:={type}";   // exact: "MsgState" must not take MsgStateAck with it
        if (NetActive.Rules.FirstOrDefault(r => r.Action == RuleAction.Mute && r.Filter == filter) is { } had)
        {
            had.Enabled = true;
            return;
        }
        var r = new NetRule { Action = RuleAction.Mute };
        r.SetFilter(filter);
        NetActive.Rules.Add(r);
    }

    /// <summary>Adds a rule for a packet's type from a menu ("Block this type").</summary>
    private static void AddRule(Packet p, RuleAction action)
    {
        var r = new NetRule { Action = action, Count = 3, EveryMs = 50 };
        r.SetFilter($"dir:{(p.Dir == NetDir.In ? "in" : "out")} type:{p.Inner ?? p.Type.Name}");
        NetActive.Rules.Add(r);
    }

    private static string TokenHead(NetQuery.Token t)
        => (t.Negated ? "−" : "") + t.Key switch { "" => "", "@" => "@", _ => t.Key + ":" };

    private static Vector4 TokenColor(NetQuery.Token t) => !NetQuery.IsValid(t) ? Theme.P.Red : t.Key switch
    {
        "type" => Theme.P.Accent,
        "dir"  => t.Value is "in" or "↓" ? Theme.P.Accent : Theme.P.Accent2,
        "@"    => Theme.P.Accent2,
        _      => Theme.P.Fg,
    };

    internal static Vector4 ActionColor(RuleAction a) => a switch
    {
        RuleAction.Block  => Theme.P.Red,
        RuleAction.Freeze => Theme.P.Accent,
        RuleAction.Modify => Theme.P.Yellow,
        RuleAction.Mute   => Theme.P.Muted,
        _                 => Theme.P.Accent2,
    };
}
