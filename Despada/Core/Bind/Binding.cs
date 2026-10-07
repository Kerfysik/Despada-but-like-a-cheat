// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace Despada.Core.Bind;

/// <summary>
/// One thing Despada needs from the engine or content, found by name at startup (reflection only:
/// no compile-time reference to Robust). A binding that can't be found doesn't throw; it reports
/// <see cref="Ok"/> = false and an <see cref="Error"/>, and the features needing it stay off.
/// </summary>
internal abstract class Binding
{
    protected Binding(string name) => Name = name;

    /// <summary>Readable name, e.g. "NetManager.ClientSendMessage".</summary>
    public string Name { get; }

    public bool    Ok       { get; protected set; }
    public string? Error    { get; protected set; }
    public bool    Resolved { get; private set; }

    /// <summary>Finds the member; safe to call more than once (later calls are no-ops).</summary>
    public void Resolve()
    {
        if (Resolved) return;
        Resolved = true;
        try
        {
            Error = DoResolve();
            Ok = Error is null;
        }
        catch (Exception ex)
        {
            Ok = false;
            Error = ex.Message;
        }
    }

    /// <summary>Returns null on success, else why it failed.</summary>
    protected abstract string? DoResolve();

    public override string ToString() => Ok ? Name : $"{Name} ({Error ?? "not resolved"})";
}
