// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Despada.ImGui.Hook;

/// <summary>
/// The GLFW the game already has in the process (its other window library), found by name,
/// never loaded anew. GLFW has no event filter; each glfwSet*Callback returns the callback it
/// replaces, which is what lets ours sit in front of the engine's. GLFW 3.3+.
/// </summary>
internal static unsafe class Glfw
{
    public static bool Ok { get; private set; }
    public static string? Why { get; private set; }
    public static bool Unusable { get; private set; }

    private static delegate* unmanaged[Cdecl]<nint, nint, nint> _setKey, _setChar, _setMouseButton, _setCursorPos, _setScroll, _setFocus;
    private static delegate* unmanaged[Cdecl]<nint, int*, int*, void> _getWindowSize, _getFramebufferSize;

    [DllImport("libc", EntryPoint = "dlopen")]      private static extern nint DlOpenLinux(string file, int mode);
    [DllImport("libSystem", EntryPoint = "dlopen")] private static extern nint DlOpenMac(string file, int mode);
    [DllImport("kernel32", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string name);

    internal static nint FindLoaded()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (var n in new[] { "glfw3.dll", "glfw.dll" }) if (GetModuleHandle(n) is var h and not 0) return h;
            }
            else if (OperatingSystem.IsMacOS())
            {
                foreach (var n in new[] { "libglfw.3.dylib", "libglfw.dylib" }) if (DlOpenMac(n, 0x1 | 0x10) is var h and not 0) return h;
            }
            else
            {
                foreach (var n in new[] { "libglfw.so.3", "libglfw.so" }) if (DlOpenLinux(n, 0x1 | 0x4) is var h and not 0) return h;
            }
        }
        catch { }
        try
        {
            using var proc = Process.GetCurrentProcess();
            foreach (ProcessModule m in proc.Modules)
            {
                var name = Path.GetFileName(m.FileName);
                if (name is "libglfw.so.3" or "libglfw.so" or "glfw3.dll" or "glfw.dll" or "libglfw.3.dylib" or "libglfw.dylib")
                    return NativeLibrary.TryLoad(m.FileName, out var h) ? h : 0;
            }
        }
        catch { }
        return 0;
    }

    public static bool Bind()
    {
        if (Ok) return true;
        if (Unusable) return false;
        var h = FindLoaded();
        if (h == 0) { Why = "GLFW is not loaded"; return false; }
        static bool Get(nint h, string name, out nint p) => NativeLibrary.TryGetExport(h, name, out p);
        if (!Get(h, "glfwSetKeyCallback", out var a) || !Get(h, "glfwSetCharCallback", out var b) || !Get(h, "glfwSetMouseButtonCallback", out var c)
            || !Get(h, "glfwSetCursorPosCallback", out var d) || !Get(h, "glfwSetScrollCallback", out var e) || !Get(h, "glfwSetWindowFocusCallback", out var f)
            || !Get(h, "glfwGetWindowSize", out var g) || !Get(h, "glfwGetFramebufferSize", out var i))
        {
            Unusable = true;
            Why = "GLFW lacks an export";
            return false;
        }
        _setKey = (delegate* unmanaged[Cdecl]<nint, nint, nint>)a;
        _setChar = (delegate* unmanaged[Cdecl]<nint, nint, nint>)b;
        _setMouseButton = (delegate* unmanaged[Cdecl]<nint, nint, nint>)c;
        _setCursorPos = (delegate* unmanaged[Cdecl]<nint, nint, nint>)d;
        _setScroll = (delegate* unmanaged[Cdecl]<nint, nint, nint>)e;
        _setFocus = (delegate* unmanaged[Cdecl]<nint, nint, nint>)f;
        _getWindowSize = (delegate* unmanaged[Cdecl]<nint, int*, int*, void>)g;
        _getFramebufferSize = (delegate* unmanaged[Cdecl]<nint, int*, int*, void>)i;
        Ok = true;
        Why = "bound";
        return true;
    }

    // Each returns the callback that was there before (the engine's), to be called on from ours.
    public static nint SetKeyCallback(nint window, nint cb)         => _setKey(window, cb);
    public static nint SetCharCallback(nint window, nint cb)        => _setChar(window, cb);
    public static nint SetMouseButtonCallback(nint window, nint cb) => _setMouseButton(window, cb);
    public static nint SetCursorPosCallback(nint window, nint cb)   => _setCursorPos(window, cb);
    public static nint SetScrollCallback(nint window, nint cb)      => _setScroll(window, cb);
    public static nint SetFocusCallback(nint window, nint cb)       => _setFocus(window, cb);

    /// <summary>Framebuffer pixels per window unit, both axes (1 when unknown). Window-thread only, as every GLFW call.</summary>
    public static (float X, float Y) PixelScale(nint window)
    {
        int ww = 0, wh = 0, fw = 0, fh = 0;
        _getWindowSize(window, &ww, &wh);
        _getFramebufferSize(window, &fw, &fh);
        return ww > 0 && wh > 0 ? ((float)fw / ww, (float)fh / wh) : (1f, 1f);
    }

    public const int Release = 0, Press = 1, Repeat = 2;
    public const int ModShift = 1, ModCtrl = 2, ModAlt = 4, ModSuper = 8;
}
