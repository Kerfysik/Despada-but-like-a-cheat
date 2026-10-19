// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Despada.ImGui.Hook;

/// <summary>
/// The GLFW backend: our callbacks on the main window in front of the engine's, which each
/// glfwSet*Callback hands back; the engine's run only for what the overlay lets through. Set and
/// removed on the engine's window thread (GLFW's calls are main-thread only, and that is its main
/// thread). Callbacks run there too, during the engine's event poll.
/// </summary>
internal static unsafe class GlfwInput
{
    public static bool Installed { get; private set; }
    public static bool Asked { get; private set; }
    public static bool GaveUp { get; private set; }

    private static nint _window;
    private static nint _prevKey, _prevChar, _prevButton, _prevCursor, _prevScroll, _prevFocus;
    // Framebuffer pixels per screen unit, asked of GLFW at most four times a second: on X11 each
    // size query is a round trip to the server, and the mouse moves a thousand times a second.
    private static float _scaleX = 1f, _scaleY = 1f;
    private static long  _scaleAt;

    public static bool Install(out string? why)
    {
        why = null;
        if (!Glfw.Bind())
        {
            why = Glfw.Why;
            GaveUp = Glfw.Unusable;
            return false;
        }
        if (ImGuiInputHook.MainWindowReg() is not { } reg) { why = "no main window yet"; return false; }
        _window = ImGuiInputHook.PointerField(reg, "GlfwWindow");
        if (_window == 0) { GaveUp = true; why = "GlfwWindowReg.GlfwWindow not readable"; return false; }
        Asked = true;
        if (ImGuiInputHook.RunOnEngineWindowThread(InstallNow)) { MarseyLogger.Info("[ImGuiInputHook] GLFW bound; installing on the engine's window thread"); return true; }
        MarseyLogger.Info("[ImGuiInputHook] GLFW bound; installing on this thread");
        InstallNow();
        return true;
    }

    private static void InstallNow()
    {
        try
        {
            _prevKey    = Glfw.SetKeyCallback(_window, (nint)(delegate* unmanaged[Cdecl]<nint, int, int, int, int, void>)&OnKey);
            _prevChar   = Glfw.SetCharCallback(_window, (nint)(delegate* unmanaged[Cdecl]<nint, uint, void>)&OnChar);
            _prevButton = Glfw.SetMouseButtonCallback(_window, (nint)(delegate* unmanaged[Cdecl]<nint, int, int, int, void>)&OnMouseButton);
            _prevCursor = Glfw.SetCursorPosCallback(_window, (nint)(delegate* unmanaged[Cdecl]<nint, double, double, void>)&OnCursorPos);
            _prevScroll = Glfw.SetScrollCallback(_window, (nint)(delegate* unmanaged[Cdecl]<nint, double, double, void>)&OnScroll);
            _prevFocus  = Glfw.SetFocusCallback(_window, (nint)(delegate* unmanaged[Cdecl]<nint, int, void>)&OnFocus);
            Installed = true;
            MarseyLogger.Info($"[ImGuiInputHook] GLFW callbacks on (thread {Environment.CurrentManagedThreadId}), in front of the engine's: key {(_prevKey != 0 ? "yes" : "none")}, char {(_prevChar != 0 ? "yes" : "none")}, mouse {(_prevButton != 0 ? "yes" : "none")}");
        }
        catch (Exception ex)
        {
            GaveUp = true;
            try { Core.Lifecycle.Post(() => ImGuiRenderer.Disable($"GLFW callbacks: {ex.Message}")); } catch { }
        }
    }

    public static void Uninstall()
    {
        if (!Installed) return;
        Installed = false;
        if (!ImGuiInputHook.RunOnEngineWindowThread(UninstallNow)) UninstallNow();
    }

    private static void UninstallNow()
    {
        try
        {
            Glfw.SetKeyCallback(_window, _prevKey);
            Glfw.SetCharCallback(_window, _prevChar);
            Glfw.SetMouseButtonCallback(_window, _prevButton);
            Glfw.SetCursorPosCallback(_window, _prevCursor);
            Glfw.SetScrollCallback(_window, _prevScroll);
            Glfw.SetFocusCallback(_window, _prevFocus);
        }
        catch { }
    }

    // Each callback: ours decides, then the engine's runs only when the game gets the event.
    // Never throws out: an exception in a native callback would take the process down.

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnKey(nint window, int key, int scancode, int action, int mods)
    {
        var pass = true;
        try
        {
            if (!ImGuiRenderer.Failed)
            {
                var m = ((mods & Glfw.ModCtrl) != 0 ? ImGuiInputHook.ModCtrl : 0) | ((mods & Glfw.ModShift) != 0 ? ImGuiInputHook.ModShift : 0)
                      | ((mods & Glfw.ModAlt) != 0 ? ImGuiInputHook.ModAlt : 0) | ((mods & Glfw.ModSuper) != 0 ? ImGuiInputHook.ModSuper : 0);
                // GLFW keys are already by position (the US layout's); the key itself is the physical id.
                pass = ImGuiInputHook.Key(InputKeys.FromGlfw(key), (uint)key, m, action != Glfw.Release, action == Glfw.Repeat);
            }
        }
        catch (Exception ex) { ImGuiInputHook.Fault(ex); }
        if (pass && _prevKey != 0)
            try { ((delegate* unmanaged[Cdecl]<nint, int, int, int, int, void>)_prevKey)(window, key, scancode, action, mods); } catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnChar(nint window, uint codepoint)
    {
        var pass = true;
        try
        {
            if (!ImGuiRenderer.Failed)
                pass = ImGuiInputHook.Text(System.Text.Rune.IsValid(codepoint) && codepoint != 0 ? char.ConvertFromUtf32((int)codepoint) : null);
        }
        catch (Exception ex) { ImGuiInputHook.Fault(ex); }
        if (pass && _prevChar != 0)
            try { ((delegate* unmanaged[Cdecl]<nint, uint, void>)_prevChar)(window, codepoint); } catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMouseButton(nint window, int button, int action, int mods)
    {
        var pass = true;
        try
        {
            // GLFW: 0 left, 1 right, 2 middle, 3…7; ImGui numbers the first five the same way.
            if (!ImGuiRenderer.Failed) pass = ImGuiInputHook.Button(button < 5 ? button : -1, (byte)button, action == Glfw.Press);
        }
        catch (Exception ex) { ImGuiInputHook.Fault(ex); }
        if (pass && _prevButton != 0)
            try { ((delegate* unmanaged[Cdecl]<nint, int, int, int, void>)_prevButton)(window, button, action, mods); } catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnCursorPos(nint window, double x, double y)
    {
        try
        {
            if (!ImGuiRenderer.Failed)
            {
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (now - _scaleAt > System.Diagnostics.Stopwatch.Frequency / 4)
                {
                    (_scaleX, _scaleY) = Glfw.PixelScale(window);   // screen units to framebuffer pixels
                    _scaleAt = now;
                }
                ImGuiInputHook.Move((float)x * _scaleX, (float)y * _scaleY);
            }
        }
        catch (Exception ex) { ImGuiInputHook.Fault(ex); }
        if (_prevCursor != 0)
            try { ((delegate* unmanaged[Cdecl]<nint, double, double, void>)_prevCursor)(window, x, y); } catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnScroll(nint window, double xoffset, double yoffset)
    {
        var pass = true;
        try
        {
            if (!ImGuiRenderer.Failed) pass = ImGuiInputHook.Wheel((float)xoffset, (float)yoffset);   // as ImGui's own GLFW backend passes them
        }
        catch (Exception ex) { ImGuiInputHook.Fault(ex); }
        if (pass && _prevScroll != 0)
            try { ((delegate* unmanaged[Cdecl]<nint, double, double, void>)_prevScroll)(window, xoffset, yoffset); } catch { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnFocus(nint window, int focused)
    {
        try { if (!ImGuiRenderer.Failed) ImGuiInputHook.Focus(focused != 0); }
        catch (Exception ex) { ImGuiInputHook.Fault(ex); }
        if (_prevFocus != 0)
            try { ((delegate* unmanaged[Cdecl]<nint, int, void>)_prevFocus)(window, focused); } catch { }
    }
}
