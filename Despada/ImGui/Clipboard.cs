// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Despada.Core;

namespace Despada.ImGui;

/// <summary>
/// ImGui's clipboard wired to the system one through the engine (IClipboardManager). Reading is
/// asynchronous there (it goes through the window thread), so ImGui reads a cached copy that is
/// refreshed when the overlay opens, when Ctrl goes down (before the V of a paste) and every second
/// while a text field is active. Writes go straight through.
/// </summary>
/// <remarks>
/// Without this ImGui used its own, initially empty clipboard: GetClipboardText returned NULL and
/// ImGui.NET's string wrapper dereferenced it (game crash on paste).
/// </remarks>
internal static unsafe class Clipboard
{
    private static nint   _buffer;      // UTF-8, NUL-terminated, owned; what ImGui reads
    private static string _text = "";
    private static int    _pending;     // a GetText() is in flight
    private static double _lastRefresh;

    public static string Text => _text;

    public static void Install()
    {
        Store("");
        var pio = ImGuiNET.ImGui.GetPlatformIO();
        pio.Platform_GetClipboardTextFn = (nint)(delegate* unmanaged[Cdecl]<nint, byte*>)&GetFn;
        pio.Platform_SetClipboardTextFn = (nint)(delegate* unmanaged[Cdecl]<nint, byte*, void>)&SetFn;
        Refresh();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte* GetFn(nint ctx) => (byte*)_buffer;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void SetFn(nint ctx, byte* text)
    {
        try
        {
            Set(Marshal.PtrToStringUTF8((nint)text) ?? "");
        }
        catch (Exception ex)
        {
            Log.Warn("clipboard", $"set: {ex.Message}");
        }
    }

    /// <summary>Puts text on the system clipboard (and in the cached copy).</summary>
    public static void Set(string text)
    {
        Store(text);
        if (Engine.Clipboard.Instance is { } mgr && Engine.ClipboardSetText.Ok)
            Engine.ClipboardSetText.Invoke(mgr, text);
    }

    /// <summary>Asks the engine for the current system clipboard; the cache updates when it answers.</summary>
    public static void Refresh()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        try
        {
            if (Engine.Clipboard.Instance is not { } mgr || !Engine.ClipboardGetText.Ok
                || Engine.ClipboardGetText.Invoke(mgr) is not Task<string> task)
            {
                _pending = 0;
                return;
            }
            task.ContinueWith(t =>
            {
                var text = t.IsCompletedSuccessfully ? t.Result ?? "" : null;
                Lifecycle.Post(() =>
                {
                    if (text is not null) Store(text);
                    _pending = 0;
                });
            });
        }
        catch (Exception ex)
        {
            _pending = 0;
            Log.Warn("clipboard", $"get: {ex.Message}");
        }
    }

    /// <summary>Called every frame: keeps the cache fresh while something can be pasted into.</summary>
    public static void Tick(bool textInput, double now)
    {
        if (!textInput || now - _lastRefresh < 1.0) return;
        _lastRefresh = now;
        Refresh();
    }

    private static void Store(string text)
    {
        if (text == _text && _buffer != 0) return;
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var buf   = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, buf, bytes.Length);
        ((byte*)buf)[bytes.Length] = 0;

        var old = _buffer;
        _buffer = buf;
        _text   = text;
        if (old != 0) Marshal.FreeHGlobal(old);
    }
}
