// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Despada.Modules.Net;

internal enum NetDir : byte { In, Out }

/// <summary>One captured message. Written once by the capture hook, read by the UI/console.</summary>
internal sealed class Packet
{
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

    /// <summary>Open in the inspector: the message and bytes are not dropped (NetCapture.Pin).</summary>
    public bool Pinned;
    /// <summary>Passed by the retention queues while pinned; dropped on Unpin.</summary>
    public bool Untracked;

    /// <summary>One-line description, built lazily on the game thread (see NetDecode.Summary).</summary>
    public string? Summary;

    /// <summary>Row texts cached by the list view, so visible rows don't format new strings every frame.</summary>
    public string? TimeText, SizeText;
}
