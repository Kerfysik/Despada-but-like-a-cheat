// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using Despada.Core;
using Despada.ImGui;

namespace Despada.Modules.Net;

/// <summary>
/// The active side of the network log: the seam between the UI (NetView.*) and the code that really
/// sends, blocks, freezes and duplicates packets. The UI only reads the state here and calls the
/// methods below; every method marked TODO is a stub that just reports itself, to be written.
///
/// Rules of the house for the real implementation: nothing goes out while <see cref="Armed"/> is
/// off; <see cref="RateLimit"/> per rule and per manual send; packets we send ourselves are marked
/// (<see cref="PacketMark.Custom"/> / <see cref="PacketMark.Copy"/>) and never trigger rules again.
/// </summary>
internal static class NetActive
{
    /// <summary>The safety: while off, nothing is sent, blocked, frozen or changed. Never persisted.</summary>
    public static bool Armed;

    /// <summary>Sends per second the scheduler lets through, all rules and repeats together.</summary>
    public static int RateLimit = 20;

    /// <summary>Rules in their order; evaluated by the capture hooks (TODO).</summary>
    public static readonly List<NetRule> Rules = new();

    /// <summary>
    /// Packets held back by freeze rules, oldest first. Written by the capture hooks, read by the
    /// UI; on the client both are the main thread, but the lock keeps it true elsewhere too.
    /// </summary>
    public static readonly List<Packet> Frozen = new();
    public static readonly object Sync = new();

    /// <summary>A copy of the frozen queue for drawing.</summary>
    public static List<Packet> FrozenSnapshot()
    {
        lock (Sync) return Frozen.ToList();
    }

    public static int FrozenCount
    {
        get { lock (Sync) return Frozen.Count; }
    }

    /// <summary>Packets kept for later: drafts with a name.</summary>
    public static readonly List<SavedPacket> Saved = new();

    // Counts for the status line.
    public static int BlockedCount, CustomCount, MutedCount;

    /// <summary>
    /// A mute rule matches: the packet is not logged (it still goes through, and the other rules
    /// still see it). Passive, so it works unarmed and on our own packets too. On the hot path
    /// for every packet: no copy of the list (the hooks and the UI share the main thread).
    /// </summary>
    public static bool IsMuted(Packet p)
    {
        try
        {
            for (var i = 0; i < Rules.Count; i++)
            {
                var r = Rules[i];
                if (r.Action != RuleAction.Mute || !Hit(r, p)) continue;
                Interlocked.Increment(ref r.Hits);
                Interlocked.Increment(ref MutedCount);
                return true;
            }
        }
        catch (ArgumentOutOfRangeException) { }   // the list shrank under a send from another thread: not muted
        return false;
    }

    // A rule's filter on a packet; a rule that throws goes off with its error, the packet passes.
    // Rules never reach the hooks' failure count: one bad rule must not shut the whole log off.
    private static bool Hit(NetRule r, Packet p)
    {
        try { return r.Matches(p); }
        catch (Exception ex) { Fault(r, ex); return false; }
    }

    private static void Fault(NetRule r, Exception ex)
    {
        r.Enabled = false;
        r.Error = ex.Message;
        Log.Warn("net", $"rule '{r.Filter}' off: {ex.GetType().Name}: {ex.Message}");
        ImGuiRenderer.ShowToast("network", $"rule off: {r.Filter}", 3f);
    }

    /// <summary>
    /// The rules on a packet just captured. The first enabled rule whose filter matches decides:
    /// Block and Freeze stop it (false), Duplicate lets it through and schedules copies, Modify
    /// changes it. Our own packets and everything while not armed pass. On the client the hooks,
    /// the UI and the scheduler all run on the main thread; the snapshot of the rules keeps this
    /// safe should a send ever come from another thread.
    /// </summary>
    /// <summary>A logged packet an enabled mute rule would mute now: the list leaves it out.</summary>
    public static bool IsMutedForView(Packet p)
    {
        for (var i = 0; i < Rules.Count; i++)
        {
            var r = Rules[i];
            if (r.Action == RuleAction.Mute && Hit(r, p)) return true;
        }
        return false;
    }

    /// <summary>Changes with the mute rules: part of the list's filter key.</summary>
    public static string MuteKey()
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < Rules.Count; i++)
            if (Rules[i].Action == RuleAction.Mute && Rules[i].Enabled) sb.Append(Rules[i].Filter).Append('|');
        return sb.ToString();
    }

    public static bool Apply(Packet p)
    {
        if (!Armed || p.Ours) return true;
        NetRule? hit = null;
        foreach (var r in Rules.ToArray())
            if (r.Action != RuleAction.Mute && Hit(r, p)) { hit = r; break; }
        if (hit is null) return true;

        Interlocked.Increment(ref hit.Hits);
        switch (hit.Action)
        {
            case RuleAction.Block:
                p.Mark = PacketMark.Blocked;
                Interlocked.Increment(ref BlockedCount);
                return false;
            case RuleAction.Freeze:
                p.Mark = PacketMark.Frozen;
                NetCapture.Pin(p);   // the retention budget must not drop its message while it waits
                lock (Sync) Frozen.Add(p);
                return false;
            case RuleAction.Duplicate:
            {
                // The original goes on now; the copies follow from the scheduler (rate-limited,
                // marked Copy, never run through the rules again).
                p.Mark = PacketMark.Duplicated;
                p.MarkCount = hit.Count;
                var msg = p.Message;
                if (msg is not null)
                {
                    var dir  = p.Dir;
                    var size = p.Size;
                    var every = hit.EveryMs;
                    // The first copy a step after the original, not alongside it.
                    Schedule(hit.Count, every, () => Put(msg, dir, PacketMark.Copy, size: size), delayMs: Math.Max(every, 1), owner: hit);
                }
                return true;
            }
            case RuleAction.Modify:
            {
                // Outgoing: already changed before serialisation (ModifyBeforeSend); only marked.
                if (p.Dir == NetDir.Out)
                {
                    if (_preModified) { _preModified = false; p.Mark = PacketMark.Edited; p.Summary = null; }
                    return true;
                }
                // Incoming: the handler gets the changed object as is.
                var target = p.Message;
                if (target is not null && ApplyModify(hit, target))
                {
                    p.Mark = PacketMark.Edited;
                    p.Summary = null;   // built from the fields: again, from the new ones
                }
                return true;
            }
        }
        return true;
    }

    // Set by ModifyBeforeSend for the packet being built; consumed by Apply on the same thread.
    [ThreadStatic] private static bool _preModified;

    /// <summary>
    /// Before the engine serialises an outgoing message: when the first matching rule is a modify
    /// rule, its changes go into the object now, so they are on the wire. The packet is then
    /// captured, matched again and marked.
    /// </summary>
    public static void ModifyBeforeSend(object message)
    {
        _preModified = false;
        if (!Armed || Rules.Count == 0) return;
        var p = new Packet { Dir = NetDir.Out, Type = message.GetType(), Message = message };
        if (p.Type.Name == "MsgEntity")
        {
            p.Inner    = NetDecode.InnerType(message);
            p.Entities = NetDecode.EntityIds(message);
        }
        NetRule? hit = null;
        foreach (var r in Rules.ToArray())
            if (r.Action != RuleAction.Mute && Hit(r, p)) { hit = r; break; }
        if (hit is null || hit.Action != RuleAction.Modify) return;
        _preModified = ApplyModify(hit, message);
    }

    // "Field=value" pairs set on the message (for MsgEntity: on the event inside); true when any took.
    private static bool ApplyModify(NetRule rule, object target)
    {
        try
        {
            if (target.GetType().Name == "MsgEntity" && Engine.MsgEntitySystem.Ok && Engine.MsgEntitySystem.Invoke(target) is { } ev)
                target = ev;
            var changed = false;
            foreach (var (path, value) in rule.ModifyPairs())
            {
                if (DraftBuilder.TrySet(target, path, value, out var error)) changed = true;
                else if (rule.Error != error)
                {
                    rule.Error = error;
                    Log.Warn("net", $"modify rule: {error}");
                }
            }
            return changed;
        }
        catch (Exception ex) { Fault(rule, ex); return false; }
    }

    // Types as they were last seen going by (events only know their direction from traffic).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, NetDir> _seenDir = new();

    /// <summary>Called for every captured packet: remembers which way its type went.</summary>
    public static void Learn(Packet p) => _seenDir[p.Inner ?? p.Type.Name] = p.Dir;

    /// <summary>
    /// Which way a type goes, when known: ↑ to the server, ↓ to us. A NetMessage with a receive
    /// handler on this client comes in, one without goes out (MsgEntity does both: by its event);
    /// an event the way it was last captured. Null = unknown (the UI assumes ↑).
    /// </summary>
    public static NetDir? DirectionOf(string typeName)
    {
        if (_seenDir.TryGetValue(typeName, out var seen)) return seen;
        var t = NetTypes.Resolve(typeName);
        if (t is null) return null;
        if (Engine.NetMessage.Type is { } netMessage && t.IsSubclassOf(netMessage))
        {
            if (t.Name == "MsgEntity") return null;
            var audit = HandledMessages();
            return audit is null ? null : audit.Contains(t) ? NetDir.In : NetDir.Out;
        }
        // An event, by how this client subscribed to it (read from the event bus):
        //   only from the network (SubscribeNetworkEvent in client code) → it comes in;
        //   from the network and locally (SubscribeAllEvent in a shared system), or with a session
        //   (EntitySessionMessage<T>) → a request the server handles: we send it;
        //   no network subscription at all → we send it.
        var subs = NetworkSubscriptions();
        if (subs is null) return null;
        return subs.TryGetValue(t, out var kind) && kind == SubKind.NetworkOnly ? NetDir.In : NetDir.Out;
    }

    private enum SubKind { NetworkOnly, Shared }

    // Event type → how it is subscribed, from EntityEventBus._eventDataUnfrozen (the live table; the
    // frozen copy shares its entries) → EventData.BroadcastRegistrations → BroadcastRegistration.Mask.
    // Session-handler subscriptions are keyed by EntitySessionMessage<T>: unwrapped to T. Re-read
    // every few seconds: systems subscribe on connect.
    private static Dictionary<Type, SubKind>? _netSubs;
    private static long _netSubsAt;

    // Message types with a client handler (NetManager.CallbackAudit builds a fresh dictionary per
    // call, so it is asked every 5 s, not per type per frame).
    private static HashSet<Type>? _handled;
    private static long _handledAt;

    private static HashSet<Type>? HandledMessages()
    {
        var now = Stopwatch.GetTimestamp();
        if (_handled is not null && Stopwatch.GetElapsedTime(_handledAt, now).TotalSeconds < 5) return _handled;
        _handledAt = now;
        var net = Engine.ClientNet.Instance;
        if (net is null || !Engine.NetCallbackAudit.Ok) return _handled;
        try
        {
            if (Engine.NetCallbackAudit.Invoke(net) is not System.Collections.IDictionary audit) return _handled;
            var set = new HashSet<Type>();
            foreach (var k in audit.Keys) if (k is Type t) set.Add(t);
            return _handled = set;
        }
        catch
        {
            return _handled;
        }
    }

    private static Dictionary<Type, SubKind>? NetworkSubscriptions()
    {
        var now = Stopwatch.GetTimestamp();
        if (_netSubs is not null && Stopwatch.GetElapsedTime(_netSubsAt, now).TotalSeconds < 5) return _netSubs;
        _netSubsAt = now;
        try
        {
            var em  = Engine.Entities.Instance;
            var bus = em is null || !Engine.EntityEventBus.Ok ? null : Engine.EntityEventBus.Invoke(em);
            if (bus is null) return _netSubs;
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var map = new Dictionary<Type, SubKind>();
            if (bus.GetType().GetField("_eventDataUnfrozen", any)?.GetValue(bus) is not System.Collections.IDictionary dict) return _netSubs;
            foreach (System.Collections.DictionaryEntry e in dict)
            {
                if (e.Key is not Type key || e.Value is null) continue;
                var session = key.IsGenericType && key.GetGenericTypeDefinition().Name == "EntitySessionMessage`1";
                var evType  = session ? key.GetGenericArguments()[0] : key;
                var regs = e.Value.GetType().GetField("BroadcastRegistrations", any)?.GetValue(e.Value) as System.Collections.IEnumerable;
                if (regs is null) continue;
                foreach (var reg in regs)
                {
                    if (reg?.GetType().GetField("Mask", any)?.GetValue(reg) is not { } m) continue;
                    var mask = Convert.ToInt32(m);
                    if ((mask & 2) == 0) continue;                                   // EventSource.Network
                    var kind = session || (mask & 1) != 0 ? SubKind.Shared : SubKind.NetworkOnly;   // Local too → shared
                    // One shared subscription makes the type shared.
                    map[evType] = map.TryGetValue(evType, out var had) && had == SubKind.Shared ? SubKind.Shared : kind;
                }
            }
            return _netSubs = map;
        }
        catch (Exception ex)
        {
            Log.Warn("net", $"event subscriptions: {ex.GetType().Name}: {ex.Message}");
            return _netSubs;
        }
    }

    /// <summary>
    /// Sends <paramref name="draft"/> <paramref name="times"/> times, <paramref name="everyMs"/>
    /// apart: the object is built once (a bad value stops here with a toast), then every copy is
    /// marked Custom. ↑ a NetMessage goes through the net manager, an event through the entity
    /// manager (fresh tick and sequence); ↓ a NetMessage is delivered to our handler, an event is
    /// wrapped in a MsgEntity and delivered the same way.
    /// </summary>
    public static bool Send(PacketDraft draft, int times, int everyMs)
    {
        if (!Armed) return false;
        object obj;
        bool wrap;
        try
        {
            obj = DraftBuilder.Build(draft, out wrap);
        }
        catch (Exception ex)
        {
            var inner = Inner(ex);
            ImGuiRenderer.ShowToast("network", $"{draft.TypeName}: {inner.Message}", 3.5f);
            return false;
        }
        var isMessage = Engine.NetMessage.Type is { } nm && obj.GetType().IsSubclassOf(nm);
        if (!isMessage && !wrap && draft.TypeName.StartsWith("BUI.", StringComparison.Ordinal))
        {
            ImGuiRenderer.ShowToast("network", "a new BUI message needs an entity and a UI key: edit a captured one instead", 3.5f);
            return false;
        }
        Action one = draft.Dir switch
        {
            NetDir.Out when isMessage => () => Put(obj, NetDir.Out, PacketMark.Custom),
            NetDir.Out                => () => SendEvent(obj, PacketMark.Custom),
            _ when isMessage          => () => Put(obj, NetDir.In, PacketMark.Custom),
            _                         => () => { if (WrapEvent(obj) is { } me) Put(me, NetDir.In, PacketMark.Custom); },
        };
        Schedule(times, everyMs, one);
        return true;
    }

    // An event as the client would receive it: a MsgEntity carrying it (tick 0, so it's handled right away).
    private static object? WrapEvent(object entityEvent)
    {
        try
        {
            var t  = Engine.MsgEntity.Type ?? throw new InvalidOperationException("MsgEntity type not found");
            var me = DraftBuilder.Create(t);
            var typeProp = t.GetProperty("Type") ?? throw new InvalidOperationException("MsgEntity.Type not found");
            typeProp.SetValue(me, Enum.Parse(typeProp.PropertyType, "SystemMessage"));
            (t.GetProperty("SystemMessage") ?? throw new InvalidOperationException("MsgEntity.SystemMessage not found")).SetValue(me, entityEvent);
            return me;
        }
        catch (Exception ex)
        {
            ImGuiRenderer.ShowToast("network", $"deliver: {Inner(ex).Message}", 3f);
            return null;
        }
    }

    /// <summary>
    /// Sends a captured packet again, as it was: <paramref name="times"/> copies,
    /// <paramref name="everyMs"/> apart, the first at the next tick. Each shows up in the log as a copy.
    /// </summary>
    public static bool Resend(Packet p, int times, int everyMs)
    {
        if (!Armed) return false;
        var msg = p.Message;
        if (msg is null)
        {
            ImGuiRenderer.ShowToast("network", $"#{p.Seq}: no longer kept, nothing to resend", 2f);
            return false;
        }
        var size = p.Size;
        Schedule(times, everyMs, () => Put(msg, p.Dir, PacketMark.Copy, size: size));
        return true;
    }

    /// <summary>Lets a frozen packet go on its way (sent or delivered as it would have been).</summary>
    public static void Release(Packet p)
    {
        if (!Armed)
        {
            ImGuiRenderer.ShowToast("network", "arm to release packets", 2f);
            return;
        }
        var msg = p.Message;
        if (msg is null)
        {
            // Lost to retention after all: it never went anywhere, so it counts as blocked.
            lock (Sync) Frozen.Remove(p);
            NetCapture.Unpin(p);
            p.Mark = PacketMark.Blocked;
            Interlocked.Increment(ref BlockedCount);
            ImGuiRenderer.ShowToast("network", $"#{p.Seq}: no longer kept, can't release", 2.5f);
            return;
        }
        if (p.Mark == PacketMark.Releasing) return;   // already in the queue
        p.Mark = PacketMark.Releasing;
        var dir = p.Dir;
        // Through the rate limit, like every send; frozen and pinned until it has really gone,
        // so a disarm or a lost connection in between leaves it where it was, not vanished.
        // Its own owner: a release is never cut by another owner's burst.
        var accepted = Schedule(1, 0, () =>
        {
            // Dropped or let go of meanwhile: this send is void.
            bool still;
            lock (Sync) still = p.Mark == PacketMark.Releasing && Frozen.Contains(p);
            if (!still) return;
            if (!Put(msg, dir, PacketMark.None, skipCapture: true)) { p.Mark = PacketMark.Frozen; return; }
            lock (Sync) Frozen.Remove(p);
            NetCapture.Unpin(p);
            p.Mark = PacketMark.None;
        });
        if (accepted == 0) p.Mark = PacketMark.Frozen;
    }

    /// <summary>
    /// A fresh event to the server: the entity manager wraps it in a MsgEntity with the current
    /// tick and sequence (what RaiseNetworkEvent does), and it passes the send hook. Never the
    /// predictive variant: that one re-raises the event on every prediction run.
    /// </summary>
    public static bool SendEvent(object entityEvent, PacketMark mark)
    {
        if (!Engine.SendSystemNetMessage.Ok) { ImGuiRenderer.ShowToast("network", "SendSystemNetworkMessage not found in this engine", 3f); return false; }
        var em = Engine.EntityNet.Instance;
        if (em is null) { ImGuiRenderer.ShowToast("network", "no entity manager", 2f); return false; }
        var net = Engine.ClientNet.Instance;
        if (net is null || Engine.NetIsConnected.Ok && Engine.NetIsConnected.Invoke(net) is false)
        {
            ImGuiRenderer.ShowToast("network", "not connected", 2f);
            return false;
        }
        try
        {
            NetCapture.MarkNext(mark);
            Engine.SendSystemNetMessage.Invoke(em, entityEvent, true);   // recordReplay, as RaiseNetworkEvent passes
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("net", $"send failed: {ex.GetType().Name}: {ex.Message}");
            ImGuiRenderer.ShowToast("network", $"send failed: {Inner(ex).Message}", 3f);
            return false;
        }
        finally
        {
            NetCapture.ClearNext();
        }
    }

    private static Exception Inner(Exception ex) => ex is System.Reflection.TargetInvocationException { InnerException: { } i } ? i : ex;

    // ── sending ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One message out (↑ ClientSendMessage: the capture hook sees it, marked <paramref name="mark"/>)
    /// or in (↓ DispatchLocalNetMessage: its handler runs, the hook doesn't see it). Main thread.
    /// </summary>
    private static bool Put(object message, NetDir dir, PacketMark mark, bool skipCapture = false, int size = 0)
    {
        var net = Engine.ClientNet.Instance;
        if (net is null || Engine.NetIsConnected.Ok && Engine.NetIsConnected.Invoke(net) is false)
        {
            ImGuiRenderer.ShowToast("network", "not connected", 2f);
            return false;
        }
        try
        {
            if (dir == NetDir.Out)
            {
                if (skipCapture) NetCapture.SkipNext();
                else             NetCapture.MarkNext(mark);
                Engine.NetClientSend.Invoke(net, message);
            }
            else
            {
                // The handler runs right here (a message from the past is never deferred); the log
                // row is ours to add, the receive hook doesn't see this path.
                if (!skipCapture) Mirror(message, mark, net, size);
                Engine.NetDispatchLocal.Invoke(net, message);
            }
            return true;
        }
        catch (Exception ex)
        {
            var inner = Inner(ex);
            Log.Warn("net", $"send failed: {inner.GetType().Name}: {inner.Message}");
            ImGuiRenderer.ShowToast("network", $"send failed: {inner.Message}", 3f);
            return false;
        }
        finally
        {
            // An early return in the engine (not connected, unknown message name) leaves the flag
            // unconsumed: it must not label the next packet that comes along.
            NetCapture.ClearNext();
        }
    }

    // A delivered-to-ourselves message doesn't pass the receive hook: a row for it is added by hand.
    private static void Mirror(object message, PacketMark mark, object net, int size)
    {
        if (NetCapture.Paused) return;   // as the hooks: paused means nothing is logged
        NetCapture.MarkNext(mark == PacketMark.None ? PacketMark.Copy : mark);
        NetCapture.Add(new Packet
        {
            Dir = NetDir.In, Type = message.GetType(), Message = message, Size = size, Tick = NetCapture.TickOf(net),
        });
    }

    // ── repeating, with the rate limit ───────────────────────────────────────────────────────

    // Every queued send belongs to an owner: the rule that made it, or the batch of one manual
    // action. A rule's jobs go with the rule (deleted or off); no owner may hold more than half
    // a minute of sends at the rate limit, the rest of a burst is dropped and counted; the whole
    // queue goes when the connection does (a stale send must not reach the next server).
    private readonly record struct Job(long Due, Action Run, object Owner);
    private static List<Job> _jobs = new();
    private static int MaxQueuedPerOwner => RateLimit * 30;

    /// <summary>Sends waiting in the scheduler.</summary>
    public static int PendingSends
    {
        get { lock (Sync) return _jobs.Count; }
    }
    /// <summary>Sends a burst asked for that the queue would not hold.</summary>
    public static int DroppedSends;
    private static long _windowStart;
    private static int  _sentInWindow;

    /// <summary>
    /// <paramref name="times"/> runs of <paramref name="run"/>, <paramref name="everyMs"/> apart, on the
    /// game thread; how many the queue took. The queue stays in due order (each job goes in at its
    /// place), so Pump's one pass is the earliest first.
    /// </summary>
    public static int Schedule(int times, int everyMs, Action run, int delayMs = 0, object? owner = null)
    {
        times = Math.Clamp(times, 1, 1000);
        owner ??= new object();
        var now  = Stopwatch.GetTimestamp();
        var tick = Stopwatch.Frequency / 1000.0;
        var taken = 0;
        lock (Sync)
        {
            var held = 0;
            foreach (var j in _jobs) if (ReferenceEquals(j.Owner, owner)) held++;
            var at = _jobs.Count;   // this call's jobs are ascending: the search starts where the last one went
            for (var i = 0; i < times; i++)
            {
                if (held++ >= MaxQueuedPerOwner) break;
                var job = new Job(now + (long)((delayMs + i * everyMs) * tick), run, owner);
                while (at > 0 && _jobs[at - 1].Due > job.Due) at--;
                while (at < _jobs.Count && _jobs[at].Due <= job.Due) at++;
                _jobs.Insert(at++, job);
                taken++;
            }
        }
        if (taken < times)
        {
            DroppedSends += times - taken;
            ImGuiRenderer.ShowToast("network", $"{times - taken} of {times} sends dropped: the queue holds half a minute at the rate limit", 3f);
        }
        return taken;
    }

    /// <summary>Forgets every queued send; a packet waiting to be released is frozen again.</summary>
    public static void CancelPending()
    {
        lock (Sync)
        {
            _jobs.Clear();
            foreach (var p in Frozen) if (p.Mark == PacketMark.Releasing) p.Mark = PacketMark.Frozen;
        }
    }

    internal static bool AssumeConnected;   // tests: no engine to ask

    private static bool Connected()
    {
        if (AssumeConnected) return true;
        var net = Engine.ClientNet.Instance;
        return net is not null && (!Engine.NetIsConnected.Ok || Engine.NetIsConnected.Invoke(net) is not false);
    }

    /// <summary>Runs the jobs that are due; at most <see cref="RateLimit"/> per second, the rest wait. Called every tick.</summary>
    public static void Pump()
    {
        if (!Armed || !Connected())
        {
            CancelPending();
            return;
        }
        var now = Stopwatch.GetTimestamp();
        if (now - _windowStart > Stopwatch.Frequency) { _windowStart = now; _sentInWindow = 0; }
        var due  = new List<Job>();
        lock (Sync)
        {
            if (_jobs.Count == 0) return;
            var budget = Math.Max(0, RateLimit - _sentInWindow);
            var keep   = new List<Job>(_jobs.Count);
            foreach (var j in _jobs)   // in due order: Schedule keeps the list sorted
            {
                if (j.Owner is NetRule r && (!r.Enabled || !Rules.Contains(r))) continue;   // the rule is gone: so are its sends
                if (j.Due <= now && due.Count < budget) due.Add(j);
                else keep.Add(j);
            }
            _jobs = keep;
        }
        foreach (var j in due)
        {
            _sentInWindow++;
            try { j.Run(); }
            catch (Exception ex) { Log.Warn("net", $"scheduled send failed: {ex.Message}"); }
        }
    }

    // Always at the next tick, never inside the frame that drew the button: handlers then run in the
    // engine's update phase, as they would for a real packet, not in the middle of rendering.
    private static void RunOnGame(Action work) => Lifecycle.Post(work);

    /// <summary>Throws a frozen packet away.</summary>
    public static void Drop(Packet p)
    {
        lock (Sync) Frozen.Remove(p);
        NetCapture.Unpin(p);
        p.Mark = PacketMark.Blocked;
        Interlocked.Increment(ref BlockedCount);
    }

    public static void ReleaseAll()
    {
        foreach (var p in FrozenSnapshot()) Release(p);
    }

    public static void DropAll()
    {
        foreach (var p in FrozenSnapshot()) Drop(p);
    }

    /// <summary>Everything off: called when the feature is disabled.</summary>
    public static void Reset()
    {
        Armed = false;
        MutedCount = 0;
        CancelPending();
        DroppedSends = 0;
        _windowStart = 0; _sentInWindow = 0;
        AssumeConnected = false;
        DropAll();
    }

    // A stub was reached: say so in the overlay, so the UI path can be checked before the logic exists.
    private static void Todo(string what) => ImGuiRenderer.ShowToast("network", $"not wired yet: {what}", 2.5f);
}
