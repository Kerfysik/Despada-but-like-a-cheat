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

    // Per type (the event inside a MsgEntity counts as its own): the packets still in the ring,
    // the total, and a one-second rate. Over the cap, the oldest of that type leaves the ring,
    // so a chatty type can't push the others out.
    public static int KeepPerType = 1000;

    private sealed class TypeStat
    {
        public readonly LinkedList<Packet> Kept = new();
        public long Total;
        public long Second, InSecond, LastSecond;
    }

    private static readonly Dictionary<string, TypeStat> _types = new();
    private static readonly Queue<long> _evicted = new();   // seqs removed before their turn, still inside the ring's window

    // Evictions for the list to drop its rows: a running count and the latest seqs.
    public static long EvictionCount { get; private set; }
    private static readonly List<long> _evictLog = new();
    private const int EvictLogMax = 8192;

    /// <summary>The seqs evicted since <paramref name="sinceCount"/>; Complete is false when they're more than the log keeps.</summary>
    public static (List<long> Seqs, bool Complete) EvictedSince(long sinceCount)
    {
        lock (_lock)
        {
            var n = EvictionCount - sinceCount;
            if (n <= 0) return ([], true);
            if (n > _evictLog.Count) return ([], false);
            return (_evictLog.GetRange(_evictLog.Count - (int)n, (int)n), true);
        }
    }

    public static long TotalIn, TotalOut, BytesIn, BytesOut;
    /// <summary>Bytes seen on the wire, counted even while paused (the traffic graph); any thread.</summary>
    public static long WireIn, WireOut;
    public static bool Paused;

    // What the next packet the send hook sees is (set right before our own ClientSendMessage; the
    // hook runs synchronously on the same thread): a copy, a custom one, or not captured at all.
    [ThreadStatic] private static PacketMark _nextMark;
    [ThreadStatic] private static bool _skipNext;

    public static void MarkNext(PacketMark mark) => _nextMark = mark;
    public static void SkipNext() => _skipNext = true;
    /// <summary>After our own send: a flag the hook didn't consume (an early return) must not label the next packet.</summary>
    public static void ClearNext() { _nextMark = PacketMark.None; _skipNext = false; }

    /// <summary>The client's current tick, for rows made by hand.</summary>
    public static uint TickOf(object manager) => CurrentTick(manager);

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
                var old  = Snapshot();
                var kept = old.Skip(Math.Max(0, old.Count - value)).ToList();
                _ring = new Packet?[value];
                foreach (var p in kept) _ring[p.Seq % value] = p;
                // The window is what the ring holds, not its length: a bigger ring must not claim
                // packets that were never kept.
                _first = kept.Count > 0 ? Math.Max(_first, kept[0].Seq) : _nextSeq;
            }
        }
    }

    public static long FirstSeq { get { lock (_lock) return First; } }
    public static long LastSeq  { get { lock (_lock) return _nextSeq - 1; } }
    public static int  Count
    {
        get
        {
            lock (_lock)
            {
                while (_evicted.Count > 0 && _evicted.Peek() < First) _evicted.Dequeue();
                return (int)(_nextSeq - First) - _evicted.Count;
            }
        }
    }

    /// <summary>How many packets of a type the ring holds, its one-second rate and its total; the key is the type as the list names it.</summary>
    public static (int Kept, int PerSec, long Total) TypeStats(string type)
    {
        lock (_lock)
        {
            if (!_types.TryGetValue(type, out var t)) return (0, 0, 0);
            while (t.Kept.First is { } f && f.Value.Seq < First) t.Kept.RemoveFirst();
            var sec = Stopwatch.GetTimestamp() / Stopwatch.Frequency;
            var rate = sec == t.Second ? t.LastSecond : sec == t.Second + 1 ? t.InSecond : 0;
            return (t.Kept.Count, (int)rate, t.Total);
        }
    }

    /// <summary>"4 607 · 30/s" for a type, or "" when none was seen.</summary>
    public static string TypeHint(string type)
    {
        var (kept, perSec, total) = TypeStats(type);
        if (total == 0) return "";
        return perSec > 0 ? $"{kept:N0} · {perSec}/s" : $"{kept:N0}";
    }

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
            _types.Clear();
            _evicted.Clear();
            _evictLog.Clear();
            _first = _nextSeq;
            TotalIn = TotalOut = BytesIn = BytesOut = 0;
        }
    }

    public static void Install()
    {
        Hooks.Prefix(Owner, Engine.NetCoreSend, nameof(SendPrefix), typeof(NetCapture));
        Hooks.Transpiler(Owner, Engine.NetCoreSend, nameof(SendTranspiler), typeof(NetCapture));
        Hooks.Transpiler(Owner, Engine.NetDispatch, nameof(RecvTranspiler), typeof(NetCapture));
    }

    // ── hooks ────────────────────────────────────────────────────────────────────────────────

    // ... var packet = BuildMessage(message, channel);
    //     → if (!OnSend(packet, message, this)) return;   (the packet is left on the stack: popped)
    private static IEnumerable<CodeInstruction> SendTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var onSend = AccessTools.Method(typeof(NetCapture), nameof(OnSend));
        var list   = instructions.ToList();
        var at     = list.FindIndex(c => (c.opcode == OpCodes.Call || c.opcode == OpCodes.Callvirt)
                                         && c.operand is MethodInfo { Name: "BuildMessage" });
        if (at < 0) throw new InvalidOperationException("BuildMessage call not found in CoreSendMessage");

        // The instruction after BuildMessage (stloc packet) gets a label: the "go on" branch. The
        // label must come from the generator: a `new Label()` is index 0, i.e. the method's own first label.
        var goOn = generator.DefineLabel();
        list[at + 1].labels.Add(goOn);
        list.InsertRange(at + 1,
        [
            new CodeInstruction(OpCodes.Dup),        // packet
            new CodeInstruction(OpCodes.Ldarg_2),    // message
            new CodeInstruction(OpCodes.Ldarg_0),    // NetManager
            new CodeInstruction(OpCodes.Call, onSend),
            new CodeInstruction(OpCodes.Brtrue, goOn),
            new CodeInstruction(OpCodes.Pop),        // the packet: not sent
            new CodeInstruction(OpCodes.Ret),
        ]);
        return list;
    }

    // ... try { entry.Callback!.Invoke(instance); } catch … ; return true;
    //     → if (!OnRecv(instance, msg, this)) goto after the try (return true), skipping the handler
    private static IEnumerable<CodeInstruction> RecvTranspiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var onRecv = AccessTools.Method(typeof(NetCapture), nameof(OnRecv));
        var list   = instructions.ToList();
        var at     = list.FindIndex(c => c.opcode == OpCodes.Callvirt
                                         && c.operand is MethodInfo { Name: "Invoke" } m
                                         && m.DeclaringType?.Name == "ProcessMessage");
        if (at < 0) throw new InvalidOperationException("ProcessMessage.Invoke not found in DispatchNetMessage");
        // Inside a try block the only way out is `leave`: reuse the target of the try's own leave,
        // the `return true` after the handler.
        var leave = list.FindIndex(at, c => c.opcode == OpCodes.Leave || c.opcode == OpCodes.Leave_S);
        if (leave < 0 || list[leave].operand is not Label after)
            throw new InvalidOperationException("leave after ProcessMessage.Invoke not found in DispatchNetMessage");

        var goOn = generator.DefineLabel();
        list[at].labels.Add(goOn);
        list.InsertRange(at,
        [
            new CodeInstruction(OpCodes.Dup),        // message (top of the stack)
            new CodeInstruction(OpCodes.Ldarg_1),    // NetIncomingMessage
            new CodeInstruction(OpCodes.Ldarg_0),    // NetManager
            new CodeInstruction(OpCodes.Call, onRecv),
            new CodeInstruction(OpCodes.Brtrue, goOn),
            new CodeInstruction(OpCodes.Pop),        // the message
            new CodeInstruction(OpCodes.Pop),        // the callback
            new CodeInstruction(OpCodes.Leave, after),
        ]);
        return list;
    }

    // Both run inside the engine's send/receive path (on the client: the main thread, OnSend in the
    // caller of ClientSendMessage, OnRecv in the input phase's ProcessPackets): never throw (a throw here would lose the
    // message), keep it cheap (no ToString; the only reflection walk is the bounded entity scan
    // of MsgEntity events in Add). They return false when a rule stops the packet (NetActive.Apply).
    // Before the engine serialises: a modify rule changes the object here, so the bytes built next
    // carry the change and the packet keeps its place in the order (re-sending it later would let
    // messages sent in between overtake it). Nothing else happens before the packet exists.
    private static void SendPrefix(object[] __args)
    {
        try
        {
            // Our own sends (a draft, a resend, a copy) are never run through the rules.
            if (_skipNext || _nextMark != PacketMark.None || __args.Length < 2 || __args[1] is not { } message) return;
            NetActive.ModifyBeforeSend(message);
        }
        catch (Exception ex)
        {
            Hooks.Fail(Owner, "send-prefix", ex);
        }
    }

    private static bool OnSend(object packet, object message, object manager)
    {
        try
        {
            if (_skipNext) { _skipNext = false; return true; }   // a release: already in the log
            return Add(new Packet
            {
                Dir      = NetDir.Out,
                Type     = message.GetType(),
                Size     = (int)(Engine.LgLengthBytes.Invoke(packet) ?? 0),
                Bytes    = CopyBytes(packet),
                Delivery = Convert.ToInt32(Engine.MsgDelivery.Invoke(message)),
                Channel  = Engine.MsgSeqChannel.Ok ? (int)(Engine.MsgSeqChannel.Invoke(message) ?? 0) : 0,
                Tick     = CurrentTick(manager),
                Message  = message,
            });
        }
        catch (Exception ex)
        {
            Hooks.Fail(Owner, "send", ex);
            return true;
        }
    }

    private static bool OnRecv(object message, object raw, object manager)
    {
        try
        {
            var p = new Packet
            {
                Dir      = NetDir.In,
                Type     = message.GetType(),
                Size     = (int)(Engine.LgLengthBytes.Invoke(raw) ?? 0),
                Bytes    = CopyBytes(raw),
                Delivery = Convert.ToInt32(Engine.LgInDelivery.Invoke(raw)),
                Channel  = Engine.LgInSeqChannel.Ok ? (int)(Engine.LgInSeqChannel.Invoke(raw) ?? 0) : 0,
                Tick     = CurrentTick(manager),
                Message  = message,
            };
            return Add(p);
        }
        catch (Exception ex)
        {
            Hooks.Fail(Owner, "receive", ex);
            return true;
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

    // A packet that left the ring: out of its type's list (the oldest of its type, normally the head).
    private static void Forget(Packet p)
    {
        if (!_types.TryGetValue(p.Inner ?? p.Type.Name, out var t)) return;
        if (t.Kept.First is { } head && ReferenceEquals(head.Value, p)) t.Kept.RemoveFirst();
        else t.Kept.Remove(p);
    }

    private static void CountType(Packet p)
    {
        var key = p.Inner ?? p.Type.Name;
        if (!_types.TryGetValue(key, out var t)) _types[key] = t = new TypeStat();
        t.Total++;
        var sec = p.Stamp / Stopwatch.Frequency;
        if (sec != t.Second)
        {
            t.LastSecond = sec == t.Second + 1 ? t.InSecond : 0;
            t.Second = sec;
            t.InSecond = 0;
        }
        t.InSecond++;
        t.Kept.AddLast(p);
        // Over the cap: the oldest of this type go (not one open in the inspector).
        for (var node = t.Kept.First; node is not null && t.Kept.Count > KeepPerType; )
        {
            var next = node.Next;
            var old  = node.Value;
            if (old.Seq < First) t.Kept.Remove(node);
            else if (!old.Pinned)
            {
                t.Kept.Remove(node);
                if (_ring[old.Seq % _ring.Length] == old)
                {
                    _ring[old.Seq % _ring.Length] = null;
                    _evicted.Enqueue(old.Seq);
                    EvictionCount++;
                    _evictLog.Add(old.Seq);
                    if (_evictLog.Count > EvictLogMax) _evictLog.RemoveRange(0, EvictLogMax / 2);
                }
                Drop(old);
            }
            node = next;
        }
    }

    private static void Drop(Packet p, bool force = false)
    {
        if (p.Message is null) { p.Index = null; return; }   // an index written after the message went must not outlive it
        if (p.Pinned && !force)
        {
            p.Untracked = true;   // left the queues while pinned: Unpin drops it (the index stays with the message)
            return;
        }
        NetDecode.Summary(p);
        p.Message = null;
        p.Bytes   = null;
        p.Index   = null;
    }

    /// <summary>Keeps the packet's message and bytes while it is open in the inspector.</summary>
    public static void Pin(long seq)
    {
        lock (_lock)
            if (_ring[seq % _ring.Length] is { } p && p.Seq == seq) p.Pins++;
    }

    /// <summary>Pins a packet that may not be in the log (frozen while paused).</summary>
    public static void Pin(Packet p)
    {
        lock (_lock) p.Pins++;
    }

    /// <summary>Lets the packet go again; dropped now if the budget already passed it by and no one else holds it.</summary>
    public static void Unpin(long seq)
    {
        lock (_lock)
            if (_ring[seq % _ring.Length] is { } p && p.Seq == seq) UnpinLocked(p);
    }

    public static void Unpin(Packet p)
    {
        lock (_lock) UnpinLocked(p);
    }

    private static void UnpinLocked(Packet p)
    {
        if (p.Pins > 0) p.Pins--;
        if (p.Pins == 0 && p.Untracked) Drop(p);
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

    /// <summary>
    /// Records a packet (any thread) and runs the active rules on it; false when a rule stops it
    /// (the caller then doesn't send / deliver it). Exposed for tests.
    /// </summary>
    internal static bool Add(Packet p)
    {
        p.Stamp = Stopwatch.GetTimestamp();
        if (_nextMark != PacketMark.None)
        {
            // Sent by us: marked so, and left alone by the rules.
            p.Mark = _nextMark;
            p.Ours = true;
            _nextMark = PacketMark.None;
            if (p.Mark == PacketMark.Custom) Interlocked.Increment(ref NetActive.CustomCount);
        }
        if (p.Message is not null && p.Type.Name == "MsgEntity")
        {
            p.Inner    = NetDecode.InnerType(p.Message);
            p.Entities = NetDecode.EntityIds(p.Message);
        }
        if (!p.Ours) NetActive.Learn(p);
        Interlocked.Add(ref p.Dir == NetDir.In ? ref WireIn : ref WireOut, p.Size);
        // Paused, or muted by a rule: nothing is logged, but the rules still act (pause means
        // "stop writing", not "stop"; mute keeps noise from pushing the rest out of the ring).
        if (Paused || NetActive.IsMuted(p)) return NetActive.Apply(p);
        lock (_lock)
        {
            p.Seq = _nextSeq++;
            var slot = p.Seq % _ring.Length;
            if (_ring[slot] is { } gone) Forget(gone);   // pushed out of the ring: its type's list lets it go too
            _ring[slot] = p;

            if (p.Dir == NetDir.In) { TotalIn++;  BytesIn  += p.Size; }
            else                    { TotalOut++; BytesOut += p.Size; }
            CountType(p);

            // Memory: forget old message objects, and all but the newest few game states.
            // Their one-line summary is made first: it can't be made without the object.
            if (p.Message is null) goto done;
            if (p.Type.Name == "MsgState")
            {
                _states.Enqueue(p);
                if (_states.Count > KeepStates) Drop(_states.Dequeue());
                goto done;
            }
            _kept.Enqueue(p);
            _keptBytes += p.Size;
            while (_kept.Count > 0 && (_keptBytes > KeepBytes || _kept.Count > Math.Min(_ring.Length, KeepCount)
                                       || _kept.Peek().Seq <= p.Seq - _ring.Length))
            {
                var old = _kept.Dequeue();
                _keptBytes -= old.Size;
                Drop(old);
            }
        }
        done:
        return NetActive.Apply(p);
    }
}
