// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Despada.Modules.Net;

internal enum NetDir : byte { In, Out }

/// <summary>What the active side did with a packet; drawn as a badge on its row.</summary>
internal enum PacketMark
{
    None,
    /// <summary>A rule stopped it (or it was dropped from the frozen queue).</summary>
    Blocked,
    /// <summary>Waiting in <see cref="NetActive.Frozen"/>.</summary>
    Frozen,
    /// <summary>A rule made copies of it (<see cref="Packet.MarkCount"/> of them).</summary>
    Duplicated,
    /// <summary>One of those copies.</summary>
    Copy,
    /// <summary>Changed by a rule or in the editor before it went on.</summary>
    Edited,
    /// <summary>Made and sent from the overlay.</summary>
    Custom,
}

/// <summary>One captured message. Written once by the capture hook, read by the UI/console.</summary>
internal sealed class Packet
{
    /// <summary>What the active side did with it (set by NetActive's implementation).</summary>
    public PacketMark Mark;
    public int MarkCount;
    /// <summary>Sent or delivered by us (a resend, a draft, a rule's copy): rules leave it alone.</summary>
    public bool Ours;

    /// <summary>Running number, shown as #4512.</summary>
    public long   Seq;
    /// <summary>Stopwatch timestamp at capture.</summary>
    public long   Stamp;
    public NetDir Dir;
    public Type   Type = null!;
    /// <summary>Serialised size in bytes (plaintext, with the id byte).</summary>
    public int    Size;
    /// <summary>Client tick at capture (approximate when captured off the game thread).</summary>
    public uint   Tick;
    public int    Delivery;
    public int    Channel;

    /// <summary>For MsgEntity: the event inside (or the BUI message inside a BUI wrapper).</summary>
    public string? Inner;

    /// <summary>NetEntity ids mentioned by a MsgEntity's event (for entity: filters and related packets).</summary>
    public int[]? Entities;

    /// <summary>Serialised bytes; kept and dropped together with <see cref="Message"/>.</summary>
    public byte[]? Bytes;

    /// <summary>The message object; dropped for older packets to keep memory bounded.</summary>
    public object? Message;

    /// <summary>The finder's text of the fields (name, value per line), built once on the first search; goes with <see cref="Message"/>.</summary>
    public string? Index;

    /// <summary>
    /// Holders that need the message and bytes kept (an inspector tab, the frozen queue), counted:
    /// NetCapture.Pin / Unpin. One holder letting go must not drop what another still shows.
    /// </summary>
    public int Pins;
    public bool Pinned => Pins > 0;
    /// <summary>Passed by the retention queues while pinned; dropped on Unpin.</summary>
    public bool Untracked;

    /// <summary>One-line description, built lazily on the game thread (see NetDecode.Summary).</summary>
    public string? Summary;

    /// <summary>Row texts cached by the list view, so visible rows don't format new strings every frame.</summary>
    public string? TimeText, SizeText;
}
