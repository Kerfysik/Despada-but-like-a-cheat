// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace Despada.ImGui.UserInterface;

public static partial class Widgets
{
    private const float SectionPad    = 12f;
    private const float SectionGap    = 14f;
    private const float SectionRound  = 10f;
    private const float ContentIndent = 16f;
    private const float HeaderPad     = 16f;
    private const float HeaderH       = 64f;

    private const int StateSweepEvery = 120;
    private const int StateTtlFrames  = 600;

    /// <summary>Call once per frame before drawing widgets: drops state of widgets no longer drawn.</summary>
    public static void ResetFrame()
    {
        var frame = ImGuiNET.ImGui.GetFrameCount();
        if (frame % StateSweepEvery != 0) return;

        _anims.Sweep(frame, StateTtlFrames);
        _springs.Sweep(frame, StateTtlFrames);
        _numbers.Sweep(frame, StateTtlFrames);
        _comboPage.Sweep(frame, StateTtlFrames);
        _sliderKnobX.Sweep(frame, StateTtlFrames);
        _colorPickerState.Sweep(frame, StateTtlFrames);
    }

    /// <summary>Call once per frame after all widgets — advances and draws shared effects.</summary>
    public static void EndFrame()
    {
        UpdateSparks(ImGuiNET.ImGui.GetForegroundDrawList(), ImGuiNET.ImGui.GetIO().DeltaTime);
    }

    /// <summary>
    /// Per-widget state keyed by ImGui ID. Entries not touched for a while are dropped by
    /// <see cref="Sweep"/>, so widgets that stop being drawn don't leak.
    /// </summary>
    private sealed class StateStore<T>
    {
        private struct Entry
        {
            public T   Value;
            public int Frame;
        }

        private readonly Dictionary<uint, Entry> _map = new();

        /// <summary>The returned ref is only valid until the next Get on this store.</summary>
        public ref T Get(uint id, T initial)
        {
            ref var e = ref CollectionsMarshal.GetValueRefOrAddDefault(_map, id, out var exists);
            if (!exists) e.Value = initial;
            e.Frame = ImGuiNET.ImGui.GetFrameCount();
            return ref e.Value;
        }

        public void Sweep(int frame, int ttl)
        {
            foreach (var (id, e) in _map)
                if (frame - e.Frame > ttl)
                    _map.Remove(id);
        }
    }

    private struct AnimState
    {
        public float T;
        public int   SteppedFrame;
    }

    private static readonly StateStore<AnimState> _anims = new();

    /// <summary>0→1 eased towards <paramref name="value"/>; stepped at most once per frame per id.</summary>
    private static float Animate(uint id, bool value, float speed = ToggleSpeed)
    {
        var target = value ? 1f : 0f;
        ref var a = ref _anims.Get(id, new AnimState { T = target, SteppedFrame = -1 });

        var frame = ImGuiNET.ImGui.GetFrameCount();
        if (a.SteppedFrame != frame)
        {
            a.SteppedFrame = frame;
            a.T = Anim.Lerp(a.T, target, speed, ImGuiNET.ImGui.GetIO().DeltaTime);
        }

        return a.T;
    }

    private struct SpringState
    {
        public float X, V;
        public int   SteppedFrame;
    }

    private static readonly StateStore<SpringState> _springs = new();

    /// <summary>
    /// A value that springs towards <paramref name="target"/> (thumbs, sliding segments): a little
    /// overshoot, settles in ~200 ms. Stepped once per frame per id; the first call lands at once.
    /// </summary>
    private static float SpringTo(uint id, float target, float stiffness = 700f, float zeta = 0.75f)
    {
        ref var s = ref _springs.Get(id, new SpringState { X = target, SteppedFrame = -1 });
        var frame = ImGuiNET.ImGui.GetFrameCount();
        if (s.SteppedFrame != frame)
        {
            s.SteppedFrame = frame;
            Anim.Spring(ref s.X, ref s.V, target, ImGuiNET.ImGui.GetIO().DeltaTime, stiffness, zeta);
        }
        return s.X;
    }

    /// <summary>0→1 hover ease for a control drawn by hand (keyed by its item id).</summary>
    public static float Hover(uint id, bool hovered) => Animate(id ^ 0x33u, hovered, 24f);

    private const float PressSpeed = 30f;   // ≈95 % in 100 ms
    private const float PressScale = 0.96f;

    /// <summary>
    /// The rect of a control's fill while it is pressed: scaled about its centre to
    /// <see cref="PressScale"/>, eased both ways. Only the fill moves, never the glyph.
    /// </summary>
    private static (Vector2 Min, Vector2 Max) Pressed(uint id, bool active, Vector2 min, Vector2 max)
    {
        var t = Animate(id ^ 0x5A5Au, active, PressSpeed);
        if (t <= 0.001f) return (min, max);
        var k = 1f - (1f - PressScale) * t;
        var c = (min + max) * 0.5f;
        return (UiScale.Snap(c + (min - c) * k), UiScale.Snap(c + (max - c) * k));
    }

    /// <summary>Invisible button at an absolute screen position. Cursor is left after the item.</summary>
    private static bool ButtonAt(string id, Vector2 min, Vector2 size, out bool hovered)
    {
        ImGuiNET.ImGui.SetCursorScreenPos(min);
        var pressed = ImGuiNET.ImGui.InvisibleButton(id, size);
        hovered = ImGuiNET.ImGui.IsItemHovered();
        return pressed;
    }

    /// <summary>Muted text that is never treated as a printf format.</summary>
    public static void MutedText(string text)
    {
        ImGuiNET.ImGui.PushStyleColor(ImGuiCol.Text, Theme.P.Muted);
        ImGuiNET.ImGui.TextUnformatted(text);
        ImGuiNET.ImGui.PopStyleColor();
    }

    private static string DisplayLabel(string label)
    {
        var idx = label.IndexOf("##", StringComparison.Ordinal);
        return idx >= 0 ? label[..idx] : label;
    }
}
