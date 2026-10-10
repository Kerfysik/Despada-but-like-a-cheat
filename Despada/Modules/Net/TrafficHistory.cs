// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;

namespace Despada.Modules.Net;

/// <summary>
/// Ten seconds of traffic for the sparklines: a sample every ~167 ms per direction (bytes/s over
/// that window), a smoothed scale that follows the peak, and the latest rate for the labels.
/// Sampled from the feature's tick and drawn from the render hook: both run on the game thread,
/// so nothing here is synchronised.
/// </summary>
internal static class TrafficHistory
{
    public const int Points = 60;
    public const double IntervalSec = 10.0 / Points;
    private const double MinScale = 2048;          // 2 KB/s floor: silence isn't stretched to the top
    private const float SmoothPerSec = 6f;

    public static readonly float[] In  = new float[Points];
    public static readonly float[] Out = new float[Points];
    public static float ScaleIn { get; private set; } = (float)MinScale;
    public static float ScaleOut { get; private set; } = (float)MinScale;
    public static double RateIn { get; private set; }
    public static double RateOut { get; private set; }
    /// <summary>0..1: how far the newest sample has moved towards the next slot (for smooth scrolling).</summary>
    public static float Phase => _lastSample == 0 ? 0f : (float)Math.Clamp(Stopwatch.GetElapsedTime(_lastSample).TotalSeconds / IntervalSec, 0, 1);

    private static long _lastSample, _lastRate, _lastIn, _lastOut, _lastSmooth;

    public static void Sample(long bytesIn, long bytesOut)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastSample == 0) { _lastSample = _lastRate = now; _lastSmooth = now; _lastIn = bytesIn; _lastOut = bytesOut; return; }
        var slotDt = Stopwatch.GetElapsedTime(_lastSample, now).TotalSeconds;
        if (slotDt < IntervalSec) return;
        // Counters that went backwards (cleared): start over from here.
        if (bytesIn < _lastIn) _lastIn = bytesIn;
        if (bytesOut < _lastOut) _lastOut = bytesOut;
        // The rate is over the time the bytes were really counted (not the slot grid, which lags
        // behind now by a fraction of a frame). One sample for the whole gap: a stall writes the
        // same average into every missed slot.
        var dt   = Math.Max(1e-3, Stopwatch.GetElapsedTime(_lastRate, now).TotalSeconds);
        var due  = Math.Min(Points, (int)(slotDt / IntervalSec));
        var rIn  = Math.Max(0, (bytesIn - _lastIn) / dt);
        var rOut = Math.Max(0, (bytesOut - _lastOut) / dt);
        for (var k = 0; k < due; k++) { Push(In, (float)rIn); Push(Out, (float)rOut); }
        RateIn = Mean(In, RateSlots); RateOut = Mean(Out, RateSlots);
        _lastIn = bytesIn; _lastOut = bytesOut;
        _lastRate = now;
        _lastSample += (long)(due * IntervalSec * Stopwatch.Frequency);
        if (now - _lastSample > Stopwatch.Frequency) _lastSample = now;   // long stall: re-anchor
    }

    /// <summary>Eases the scales towards the current peaks; call every frame from the drawer.</summary>
    public static void Smooth()
    {
        var now = Stopwatch.GetTimestamp();
        var dt  = (float)Math.Clamp(Stopwatch.GetElapsedTime(_lastSmooth, now).TotalSeconds, 0, 0.1);
        _lastSmooth = now;
        var k = 1f - MathF.Exp(-SmoothPerSec * dt);
        ScaleIn  += (MathF.Max((float)MinScale, Peak(In)) - ScaleIn) * k;
        ScaleOut += (MathF.Max((float)MinScale, Peak(Out)) - ScaleOut) * k;
    }

    public static void Clear()
    {
        Array.Clear(In); Array.Clear(Out);
        RateIn = RateOut = 0;
        ScaleIn = ScaleOut = (float)MinScale;
        _lastSample = _lastRate = 0;
            }

    private const int RateSlots = 6;   // the labels average the last second: one burst isn't a rate

    private static double Mean(float[] h, int last)
    {
        double sum = 0;
        for (var i = h.Length - last; i < h.Length; i++) sum += h[i];
        return sum / last;
    }

    private static void Push(float[] h, float v)
    {
        Array.Copy(h, 1, h, 0, h.Length - 1);
        h[^1] = v;
    }

    private static float Peak(float[] h)
    {
        var m = 0f;
        foreach (var v in h) if (v > m) m = v;
        return m;
    }
}
