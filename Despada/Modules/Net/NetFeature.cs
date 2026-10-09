// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using Despada.Core;
using Despada.Core.Bind;
using Despada.ImGui.UserInterface;

namespace Despada.Modules.Net;

/// <summary>Passive network log: every message the client sends or receives, in the Network tab.</summary>
internal sealed class NetFeature : Feature
{
    public override string Id => NetCapture.Owner;
    public override string Title => "Network log";
    public override string? Tab => "Network";
    public override string Description => "every message the client sends and receives";

    public override IReadOnlyList<Binding> Requires =>
    [
        Engine.NetCoreSend, Engine.NetDispatch, Engine.NetTiming, Engine.LgLengthBytes, Engine.LgInDelivery,
        Engine.LgInSeqChannel, Engine.MsgDelivery, Engine.MsgSeqChannel, Engine.TimingCurTick, Engine.GameTickValue,
        Engine.MsgEntitySystem, Engine.MsgStateState, Engine.GameStateTo, Engine.GameStateEnts,
    ];

    // Wall-clock anchor for packet times (Stopwatch → local time).
    public static DateTime AnchorTime { get; private set; } = DateTime.Now;
    public static long     AnchorStamp { get; private set; } = Stopwatch.GetTimestamp();

    // Bytes per second, sampled once a second on the tick.
    public static double RateIn, RateOut;
    private static long _lastIn, _lastOut, _lastSample;

    protected internal override void OnEnable()
    {
        AnchorTime  = DateTime.Now;
        AnchorStamp = Stopwatch.GetTimestamp();
        NetCapture.Capacity = Setting("buffer", 20000);
        NetCapture.Install();
        Lifecycle.OnTick(Id, Tick);
    }

    protected internal override void OnDisable()
    {
        NetCapture.Paused = false;
        NetCapture.ReleaseMessages();
        NetView.Forget();
    }

    private static void Tick()
    {
        var now = Stopwatch.GetTimestamp();
        var dt  = (now - _lastSample) / (double)Stopwatch.Frequency;
        if (dt < 1) return;
        RateIn  = Math.Max(0, (NetCapture.BytesIn - _lastIn) / dt);   // cleared in between: negative
        RateOut = Math.Max(0, (NetCapture.BytesOut - _lastOut) / dt);
        _lastIn = NetCapture.BytesIn;
        _lastOut = NetCapture.BytesOut;
        _lastSample = now;
    }

    public static string Time(Packet p)
        => (AnchorTime + Stopwatch.GetElapsedTime(AnchorStamp, p.Stamp)).ToString("HH:mm:ss.fff");

    public static string Bytes(double b) => b switch
    {
        >= 1024 * 1024 => $"{b / 1024 / 1024:0.#} MB",
        >= 1024        => $"{b / 1024:0.#} KB",
        _              => $"{b:0} B",
    };

    public override void DrawTabRow() => NetView.DrawCounters();

    public override void DrawTab() => NetView.Draw();

    public override IEnumerable<ConsoleCommand> Commands => NetCommands.All(this);

    internal void SetBuffer(int size)
    {
        NetCapture.Capacity = size;
        SetSetting("buffer", NetCapture.Capacity);
    }
}
