// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Despada.ImGui.Hook;

/// <summary>
/// The SDL3 the game already has in the process: found by its name among the loaded modules,
/// never loaded anew (a second copy would have its own event queue), its few entry points taken
/// by name. Layouts and constants are those of SDL 3.2+ (the game ships 3.4.8), checked against
/// the headers; 64-bit only (the text pointer's offset differs on 32-bit).
/// </summary>
internal static unsafe class Sdl3
{
    public static bool Ok { get; private set; }
    public static string? Why { get; private set; }
    /// <summary>True once the module was found but an export is missing: no point asking again.</summary>
    public static bool Unusable { get; private set; }

    private static delegate* unmanaged[Cdecl]<nint, void*, void>  _setEventFilter;
    private static delegate* unmanaged[Cdecl]<nint*, void**, byte> _getEventFilter;
    private static delegate* unmanaged[Cdecl]<nint, void*, byte, byte> _runOnMainThread;
    private static delegate* unmanaged[Cdecl]<uint, nint>          _getWindowFromId;
    private static delegate* unmanaged[Cdecl]<nint, uint>          _getWindowId;
    private static delegate* unmanaged[Cdecl]<nint, int*, int*, byte> _getWindowSize;
    private static delegate* unmanaged[Cdecl]<nint, int*, int*, byte> _getWindowSizeInPixels;
    private static delegate* unmanaged[Cdecl]<nint, byte> _startTextInput, _stopTextInput, _textInputActive;

    [DllImport("libc", EntryPoint = "dlopen")]   private static extern nint DlOpenLinux(string file, int mode);
    [DllImport("libSystem", EntryPoint = "dlopen")] private static extern nint DlOpenMac(string file, int mode);
    [DllImport("kernel32", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string name);

    // The handle of the SDL3 already in the process, by name, without loading anything: dlopen
    // with RTLD_NOLOAD, GetModuleHandle on Windows; the module list as a last resort.
    internal static nint FindLoaded()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return GetModuleHandle("SDL3.dll");
            if (OperatingSystem.IsMacOS())
            {
                foreach (var n in new[] { "libSDL3.0.dylib", "libSDL3.dylib" })
                    if (DlOpenMac(n, 0x1 | 0x10) is var h and not 0) return h;   // RTLD_LAZY | RTLD_NOLOAD
            }
            else
            {
                foreach (var n in new[] { "libSDL3.so.0", "libSDL3.so" })
                    if (DlOpenLinux(n, 0x1 | 0x4) is var h and not 0) return h;    // RTLD_LAZY | RTLD_NOLOAD
            }
        }
        catch { }
        try
        {
            using var proc = Process.GetCurrentProcess();
            foreach (ProcessModule m in proc.Modules)
            {
                var name = Path.GetFileName(m.FileName);
                if (name is "libSDL3.so.0" or "libSDL3.so" or "SDL3.dll" or "libSDL3.0.dylib" or "libSDL3.dylib")
                    return NativeLibrary.TryLoad(m.FileName, out var h) ? h : 0;
            }
        }
        catch { }
        return 0;
    }

    /// <summary>Binds to the loaded SDL3; false (with <see cref="Why"/>) when it isn't there yet or lacks an export.</summary>
    public static bool Bind()
    {
        if (Ok) return true;
        if (Unusable) return false;
        if (IntPtr.Size != 8) { Unusable = true; Why = "32-bit process"; return false; }
        var h = FindLoaded();
        if (h == 0) { Why = "SDL3 is not loaded (no libSDL3.so.0 / SDL3.dll / libSDL3.0.dylib among the modules)"; return false; }

        static bool Get(nint h, string name, out nint p) => NativeLibrary.TryGetExport(h, name, out p);
        if (!Get(h, "SDL_SetEventFilter", out var a) || !Get(h, "SDL_GetEventFilter", out var b) || !Get(h, "SDL_GetWindowFromID", out var c)
            || !Get(h, "SDL_GetWindowSize", out var d) || !Get(h, "SDL_GetWindowSizeInPixels", out var e) || !Get(h, "SDL_GetWindowID", out var f))
        {
            Unusable = true;
            Why = "SDL3 lacks an export";
            return false;
        }
        Get(h, "SDL_RunOnMainThread", out var g);   // 3.2+: without it the filter is set from the calling thread
        if (Get(h, "SDL_StartTextInput", out var t1) && Get(h, "SDL_StopTextInput", out var t2) && Get(h, "SDL_TextInputActive", out var t3))
        {
            _startTextInput  = (delegate* unmanaged[Cdecl]<nint, byte>)t1;
            _stopTextInput   = (delegate* unmanaged[Cdecl]<nint, byte>)t2;
            _textInputActive = (delegate* unmanaged[Cdecl]<nint, byte>)t3;
        }
        _setEventFilter        = (delegate* unmanaged[Cdecl]<nint, void*, void>)a;
        _getEventFilter        = (delegate* unmanaged[Cdecl]<nint*, void**, byte>)b;
        _getWindowFromId       = (delegate* unmanaged[Cdecl]<uint, nint>)c;
        _getWindowSize         = (delegate* unmanaged[Cdecl]<nint, int*, int*, byte>)d;
        _getWindowSizeInPixels = (delegate* unmanaged[Cdecl]<nint, int*, int*, byte>)e;
        _getWindowId           = (delegate* unmanaged[Cdecl]<nint, uint>)f;
        _runOnMainThread       = (delegate* unmanaged[Cdecl]<nint, void*, byte, byte>)g;
        Ok = true;
        Why = "bound";
        return true;
    }

    public static void SetEventFilter(nint filter, void* userdata) => _setEventFilter(filter, userdata);

    public static (nint Filter, nint UserData) GetEventFilter()
    {
        nint f = 0; void* u = null;
        return _getEventFilter(&f, &u) != 0 ? (f, (nint)u) : (0, 0);
    }

    /// <summary>
    /// Runs the callback on the thread that pumps SDL's events (the game's window thread when it
    /// has one): right away when that is this thread, else from its next pump. False when SDL has
    /// no such call (then the work runs here and now).
    /// </summary>
    public static bool RunOnEventThread(nint callback, void* userdata)
        => _runOnMainThread is not null && _runOnMainThread(callback, userdata, 0) != 0;

    public static uint WindowId(nint window) => window == 0 ? 0 : _getWindowId(window);

    /// <summary>Text input (the stream of SDL_EVENT_TEXT_INPUT) for the window: SDL has it, read it, turn it on, off.</summary>
    public static bool HasTextInput => _textInputActive is not null;
    public static bool TextInputActive(nint window) => window != 0 && _textInputActive(window) != 0;
    public static void StartTextInput(nint window) { if (window != 0) _startTextInput(window); }
    public static void StopTextInput(nint window)  { if (window != 0) _stopTextInput(window); }

    /// <summary>Pixels per window unit of the window, both axes (1 when unknown).</summary>
    public static (float X, float Y) PixelScale(uint windowId)
    {
        var w = _getWindowFromId(windowId);
        if (w == 0) return (1f, 1f);
        int ww = 0, wh = 0, pw = 0, ph = 0;
        if (_getWindowSize(w, &ww, &wh) == 0 || _getWindowSizeInPixels(w, &pw, &ph) == 0 || ww <= 0 || wh <= 0) return (1f, 1f);
        return ((float)pw / ww, (float)ph / wh);
    }

    // Event types.
    public const uint KeyDown = 0x300, KeyUp = 0x301, TextEditing = 0x302, TextInput = 0x303;
    public const uint MouseMotion = 0x400, MouseButtonDown = 0x401, MouseButtonUp = 0x402, MouseWheel = 0x403;
    public const uint WindowFocusGained = 0x20E, WindowFocusLost = 0x20F;

    // Modifier bits (SDL_Keymod).
    public const ushort KmodShift = 0x0003, KmodCtrl = 0x00C0, KmodAlt = 0x0300, KmodGui = 0x0C00, KmodMode = 0x4000;

    // Field offsets inside SDL_Event on 64-bit.
    public const int OffWindowId = 16;
    public const int KeyScancode = 24, KeyKeycode = 28, KeyMod = 32, KeyDownFlag = 36, KeyRepeat = 37;
    public const int TextPtr = 24;
    public const int MotionX = 28, MotionY = 32;
    public const int ButtonIndex = 24, ButtonDownFlag = 25, ButtonX = 28, ButtonY = 32;
    public const int WheelX = 24, WheelY = 28;
}
