// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using Despada.Core;
using HarmonyLib;

namespace Despada.Modules.Net;

/// <summary>
/// Passive capture of every client NetMessage into a ring buffer. Two transpilers:
/// CoreSendMessage gets a call right after BuildMessage (the serialised packet is on the stack),
/// DispatchNetMessage one right before the handler is invoked (the message is deserialised, the
/// raw Lidgren message still alive). The hooks only record; nothing is changed.
/// </summary>
internal static class NetCapture
{
    public const string Owner = "net";

    // Message objects (and bytes) are kept for as many of the newest packets as fit a budget of
    // serialised bytes; most packets are tens of bytes, so normally that is the whole buffer. Game
    // states are big graphs: only the newest few. Packets open in the inspector are pinned.
    internal const long KeepBytes  = 16L * 1024 * 1024;
    private const int   KeepCount  = 20_000;   // and at most this many, whatever the buffer size
    private const int   KeepStates = 32;

    private static readonly object _lock = new();
    private static Packet?[] _ring = new Packet?[20000];
    private static long _nextSeq = 1;
    private static long _first = 1;   // first seq still kept (moves on Clear)
    private static readonly Queue<Packet> _states = new();
    private static readonly Queue<Packet> _kept = new();   // non-state packets still holding a message
    private static long _keptBytes;

    public static long TotalIn, TotalOut, BytesIn, BytesOut;
    public static bool Paused;

    private static object? _timing;

    public static int Capacity
    {
        get => _ring.Length;
        set
        {
            value = Math.Clamp(value, 1000, 200_000);
            lock (_lock)
            {
                if (value == _ring.Length) return;
                var old = Snapshot();
                _ring = new Packet?[value];
                foreach (var p in old.Skip(Math.Max(0, old.Count - value)))
                    _ring[p.Seq % value] = p;
            }
        }
    }

    public static long FirstSeq { get { lock (_lock) return First; } }
    public static long LastSeq  { get { lock (_lock) return _nextSeq - 1; } }
    public static int  Count    { get { lock (_lock) return (int)(_nextSeq - First); } }

    private static long First => Math.Max(_first, _nextSeq - _ring.Length);

    public static Packet? Get(long seq)
    {
        lock (_lock)
        {
            var p = _ring[seq % _ring.Length];
            return p is not null && p.Seq == seq ? p : null;
        }
    }

    /// <summary>Packets with Seq ≥ <paramref name="fromSeq"/>, oldest first.</summary>
    public static List<Packet> Since(long fromSeq)
    {
        lock (_lock)
        {
            var list = new List<Packet>();
            for (var s = Math.Max(fromSeq, First); s < _nextSeq; s++)
                if (_ring[s % _ring.Length] is { } p && p.Seq == s) list.Add(p);
            return list;
        }
    }

    /// <summary>Packets with <paramref name="fromSeq"/> ≤ Seq ≤ <paramref name="toSeq"/>, oldest first.</summary>
    public static List<Packet> Range(long fromSeq, long toSeq)
    {
        lock (_lock)
        {
            var list = new List<Packet>();
            for (var s = Math.Max(fromSeq, First); s < _nextSeq && s <= toSeq; s++)
                if (_ring[s % _ring.Length] is { } p && p.Seq == s) list.Add(p);
            return list;
        }
    }

    public static List<Packet> Snapshot() => Since(0);

    public static void Clear()
    {
        lock (_lock)
        {
            Array.Clear(_ring);
            _states.Clear();
            _kept.Clear();
            _keptBytes = 0;
            _first = _nextSeq;
            TotalIn = TotalOut = BytesIn = BytesOut = 0;
        }
    }

    public static void Install()
    {
        Hooks.Transpiler(Owner, Engine.NetCoreSend, nameof(SendTranspiler), typeof(NetCapture));
        Hooks.Transpiler(Owner, Engine.NetDispatch, nameof(RecvTranspiler), typeof(NetCapture));
    }

    // ── hooks ────────────────────────────────────────────────────────────────────────────────

    // ... var packet = BuildMessage(message, channel);  → + OnSend(packet, message, this)
    private static IEnumerable<CodeInstruction> SendTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var onSend = AccessTools.Method(typeof(NetCapture), nameof(OnSend));
        var list   = instructions.ToList();
        var at     = list.FindIndex(c => (c.opcode == OpCodes.Call || c.opcode == OpCodes.Callvirt)
                                         && c.operand is MethodInfo { Name: "BuildMessage" });
        if (at < 0) throw new InvalidOperationException("BuildMessage call not found in CoreSendMessage");

        list.InsertRange(at + 1,
        [
            new CodeInstruction(OpCodes.Dup),        // packet
            new CodeInstruction(OpCodes.Ldarg_2),    // message
            new CodeInstruction(OpCodes.Ldarg_0),    // NetManager
            new CodeInstruction(OpCodes.Call, onSend),
        ]);
        return list;
    }

    // ... entry.Callback!.Invoke(instance);  → OnRecv(instance, msg) just before
    private static IEnumerable<CodeInstruction> RecvTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var onRecv = AccessTools.Method(typeof(NetCapture), nameof(OnRecv));
        var list   = instructions.ToList();
        var at     = list.FindIndex(c => c.opcode == OpCodes.Callvirt
                                         && c.operand is MethodInfo { Name: "Invoke" } m
                                         && m.DeclaringType?.Name == "ProcessMessage");
        if (at < 0) throw new InvalidOperationException("ProcessMessage.Invoke not found in DispatchNetMessage");

        list.InsertRange(at,
        [
            new CodeInstruction(OpCodes.Dup),        // message (top of the stack)
            new CodeInstruction(OpCodes.Ldarg_1),    // NetIncomingMessage
            new CodeInstruction(OpCodes.Ldarg_0),    // NetManager
            new CodeInstruction(OpCodes.Call, onRecv),
        ]);
        return list;
    }

    // Both run inside the engine's send/receive path: never throw (a throw here would lose the
    // message), keep it cheap (no ToString; the only reflection walk is the bounded entity scan
    // of MsgEntity events in Add).
    private static void OnSend(object packet, object message, object manager)
    {
        try
        {
            if (Paused) return;
            Add(new Packet
            {
                Dir      = NetDir.Out,
                Type     = message.GetType(),
                Size     = (int)(Engine.LgLengthBytes.Invoke(packet) ?? 0),
                Bytes    = CopyBytes(packet),
                Delivery = Convert.ToInt32(Engine.MsgDelivery.Invoke(message)),
                Channel  = (int)(Engine.MsgSeqChannel.Invoke(message) ?? 0),
                Tick     = CurrentTick(manager),
                Message  = message,
            });
        }
        catch (Exception ex)
        {
            Hooks.Fail(Owner, "send", ex);
        }
    }

    private static void OnRecv(object message, object raw, object manager)
    {
        try
        {
            if (Paused) return;
            var p = new Packet
            {
                Dir      = NetDir.In,
                Type     = message.GetType(),
                Size     = (int)(Engine.LgLengthBytes.Invoke(raw) ?? 0),
                Bytes    = CopyBytes(raw),
                Delivery = Convert.ToInt32(Engine.LgInDelivery.Invoke(raw)),
                Channel  = (int)(Engine.LgInSeqChannel.Invoke(raw) ?? 0),
                Tick     = CurrentTick(manager),
                Message  = message,
            };
            Add(p);
        }
        catch (Exception ex)
        {
            Hooks.Fail(Owner, "receive", ex);
        }
    }

    // The Lidgren buffer is pooled (and encrypted in place right after the send hook): copy now.
    private static byte[]? CopyBytes(object buffer)
    {
        try
        {
            // Data throws when the buffer has already gone back to the pool.
            if (!Engine.LgData.Ok || Engine.LgData.Invoke(buffer) is not byte[] data) return null;
            var length = (int)(Engine.LgLengthBytes.Invoke(buffer) ?? 0);
            return data.AsSpan(0, Math.Clamp(length, 0, data.Length)).ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static uint CurrentTick(object manager)
    {
        _timing ??= Engine.NetTiming.Get(manager);
        if (_timing is null) return 0;
        var tick = Engine.TimingCurTick.Invoke(_timing);
        return tick is null ? 0 : (uint)(Engine.GameTickValue.Get(tick) ?? 0u);
    }

    private static void Drop(Packet p, bool force = false)
    {
        if (p.Message is null) return;
        if (p.Pinned && !force)
        {
            p.Untracked = true;   // left the queues while pinned: Unpin drops it
            return;
        }
        NetDecode.Summary(p);
        p.Message = null;
        p.Bytes   = null;
    }

    /// <summary>Keeps the packet's message and bytes while it is open in the inspector.</summary>
    public static void Pin(long seq)
    {
        lock (_lock)
            if (_ring[seq % _ring.Length] is { } p && p.Seq == seq) p.Pinned = true;
    }

    /// <summary>Lets the packet go again; dropped now if the budget already passed it by.</summary>
    public static void Unpin(long seq)
    {
        lock (_lock)
        {
            if (_ring[seq % _ring.Length] is not { } p || p.Seq != seq) return;
            p.Pinned = false;
            if (p.Untracked) Drop(p);
        }
    }

    /// <summary>Lets go of every message object (feature switched off); headers and summaries stay.</summary>
    public static void ReleaseMessages()
    {
        lock (_lock)
        {
            foreach (var p in _ring)
                if (p is not null) Drop(p, force: true);
            _states.Clear();
            _kept.Clear();
            _keptBytes = 0;
        }
    }

    /// <summary>Records a packet (any thread). Exposed for tests.</summary>
    internal static void Add(Packet p)
    {
        p.Stamp = Stopwatch.GetTimestamp();
        if (p.Message is not null && p.Type.Name == "MsgEntity")
        {
            p.Inner    = NetDecode.InnerType(p.Message);
            p.Entities = NetDecode.EntityIds(p.Message);
        }
        lock (_lock)
        {
            p.Seq = _nextSeq++;
            _ring[p.Seq % _ring.Length] = p;

            if (p.Dir == NetDir.In) { TotalIn++;  BytesIn  += p.Size; }
            else                    { TotalOut++; BytesOut += p.Size; }

            // Memory: forget old message objects, and all but the newest few game states.
            // Their one-line summary is made first: it can't be made without the object.
            if (p.Message is null) return;
            if (p.Type.Name == "MsgState")
            {
                _states.Enqueue(p);
                if (_states.Count > KeepStates) Drop(_states.Dequeue());
                return;
            }
            _kept.Enqueue(p);
            _keptBytes += p.Size;
            // Packets overwritten in the ring are the oldest, so they leave from the front too.
            while (_kept.Count > 0 && (_keptBytes > KeepBytes || _kept.Count > Math.Min(_ring.Length, KeepCount)
                                       || _kept.Peek().Seq <= p.Seq - _ring.Length))
            {
                var old = _kept.Dequeue();
                _keptBytes -= old.Size;
                Drop(old);
            }
        }
    }
}
