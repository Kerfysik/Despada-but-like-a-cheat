// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Despada.ImGui.Hook;

/// <summary>
/// The SDL3 backend: an event filter, called as each event is made, before the engine's event
/// watch; a dropped event never reaches the engine. Set and removed on the engine's window
/// thread (SDL_SetEventFilter takes the watchers lock, which the engine's watch holds while it
/// waits for the game thread).
/// </summary>
internal static unsafe class SdlInput
{
    public static bool Installed { get; private set; }
    public static bool Asked { get; private set; }
    public static bool GaveUp { get; private set; }

    private static nint _previousFilter;
    private static nint _previousUserData;
    private static nint _window;       // SDL_Window* of the main window
    private static uint _mainWindow;   // its id: events of other OS windows (popped-out panels) pass untouched; 0: unknown, all count
    private static bool _textStarted;  // text input is on because of us

    public static bool Install(out string? why)
    {
        why = null;
        if (!Sdl3.Bind())
        {
            why = Sdl3.Why;
            GaveUp = Sdl3.Unusable;
            return false;
        }
        if (ImGuiInputHook.MainWindowReg() is not { } reg) { why = "no main window yet"; return false; }
        _window = ImGuiInputHook.PointerField(reg, "Sdl3Window");
        _mainWindow = Sdl3.WindowId(_window);
        Asked = true;
        if (ImGuiInputHook.RunOnEngineWindowThread(InstallNow)) { MarseyLogger.Info("[ImGuiInputHook] SDL3 bound; installing on the engine's window thread"); return true; }
        if (Sdl3.RunOnEventThread((nint)(delegate* unmanaged[Cdecl]<void*, void>)&DoInstall, null)) { MarseyLogger.Info("[ImGuiInputHook] SDL3 bound; installing via SDL_RunOnMainThread"); return true; }
        MarseyLogger.Info("[ImGuiInputHook] SDL3 bound; installing on this thread");
        InstallNow();
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void DoInstall(void* _) => InstallNow();

    private static void InstallNow()
    {
        try
        {
            (_previousFilter, _previousUserData) = Sdl3.GetEventFilter();
            Sdl3.SetEventFilter((nint)(delegate* unmanaged[Cdecl]<void*, byte*, byte>)&Filter, null);
            Installed = true;
            MarseyLogger.Info($"[ImGuiInputHook] SDL3 event filter on (thread {Environment.CurrentManagedThreadId}){(_previousFilter != 0 ? ", chained to the game's" : "")}{(_mainWindow != 0 ? $", window {_mainWindow}" : "")}");
        }
        catch (Exception ex)
        {
            GaveUp = true;
            try { Core.Lifecycle.Post(() => ImGuiRenderer.Disable($"SDL3 event filter: {ex.Message}")); } catch { }
        }
    }

    public static void Uninstall()
    {
        if (!Installed) return;
        Installed = false;
        if (!ImGuiInputHook.RunOnEngineWindowThread(UninstallNow)) UninstallNow();
    }

    /// <summary>
    /// SDL sends text only while text input is on for the window. The engine turns it on for its
    /// own fields and off whenever their focus changes, our console included; so while an overlay
    /// field wants text, it is turned back on as soon as it is found off, and turned off again
    /// when the field lets go, unless a game field has the keyboard. Called once a frame on the
    /// game thread; the SDL calls themselves go to the window thread.
    /// </summary>
    public static void SyncTextInput(bool want, Func<bool> gameHasFocus)
    {
        if (!Installed || _window == 0 || !Sdl3.HasTextInput) return;
        var window = _window;
        var active = Sdl3.TextInputActive(window);
        if (want && !active)
        {
            _textStarted = true;
            if (!ImGuiInputHook.RunOnEngineWindowThread(() => Sdl3.StartTextInput(window))) Sdl3.StartTextInput(window);
        }
        else if (!want && active && _textStarted && !gameHasFocus())
        {
            _textStarted = false;
            if (!ImGuiInputHook.RunOnEngineWindowThread(() => Sdl3.StopTextInput(window))) Sdl3.StopTextInput(window);
        }
        else if (!want) _textStarted = false;
    }

    private static void UninstallNow()
    {
        try { Sdl3.SetEventFilter(_previousFilter, (void*)_previousUserData); } catch { }
    }

    // Never throws out: an exception in a native callback would take the process down.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte Filter(void* userdata, byte* ev)
    {
        var pass = true;
        try { pass = Handle(ev); }
        catch (Exception ex) { ImGuiInputHook.Fault(ex); }
        if (!pass) return 0;
        if (_previousFilter != 0)
        {
            try { return ((delegate* unmanaged[Cdecl]<void*, byte*, byte>)_previousFilter)((void*)_previousUserData, ev); }
            catch { return 1; }
        }
        return 1;
    }

    private static bool Handle(byte* ev)
    {
        if (ImGuiRenderer.Failed) return true;
        var type = *(uint*)ev;
        if (type is < 0x200 or > 0x403) return true;
        var window = *(uint*)(ev + Sdl3.OffWindowId);
        if (_mainWindow != 0 && window != 0 && window != _mainWindow) return true;   // another OS window: not ours
        switch (type)
        {
            case Sdl3.KeyDown:
            case Sdl3.KeyUp:
            {
                var scancode = *(uint*)(ev + Sdl3.KeyScancode);
                var key      = *(uint*)(ev + Sdl3.KeyKeycode);
                var mod      = *(ushort*)(ev + Sdl3.KeyMod);
                var mods = ((mod & Sdl3.KmodCtrl) != 0 ? ImGuiInputHook.ModCtrl : 0) | ((mod & Sdl3.KmodShift) != 0 ? ImGuiInputHook.ModShift : 0)
                         | ((mod & Sdl3.KmodAlt) != 0 ? ImGuiInputHook.ModAlt : 0) | ((mod & Sdl3.KmodGui) != 0 ? ImGuiInputHook.ModSuper : 0)
                         | ((mod & Sdl3.KmodMode) != 0 ? ImGuiInputHook.ModAltGr : 0);
                return ImGuiInputHook.Key(InputKeys.FromSdl(key, scancode), scancode, mods, type == Sdl3.KeyDown, ev[Sdl3.KeyRepeat] != 0);
            }
            case Sdl3.MouseButtonDown:
            case Sdl3.MouseButtonUp:
            {
                // SDL buttons: 1 left, 2 middle, 3 right, 4 x1, 5 x2; ImGui: 0 left, 1 right, 2 middle, 3, 4.
                var b = ev[Sdl3.ButtonIndex];
                var idx = b switch { 1 => 0, 2 => 2, 3 => 1, 4 => 3, 5 => 4, _ => -1 };
                return ImGuiInputHook.Button(idx, b, type == Sdl3.MouseButtonDown);
            }
            case Sdl3.MouseMotion:
            {
                // Window units to the framebuffer's pixels, by the window's pixel density.
                var (sx, sy) = Sdl3.PixelScale(window);
                ImGuiInputHook.Move(*(float*)(ev + Sdl3.MotionX) * sx, *(float*)(ev + Sdl3.MotionY) * sy);
                return true;
            }
            case Sdl3.MouseWheel:
                return ImGuiInputHook.Wheel(-*(float*)(ev + Sdl3.WheelX), *(float*)(ev + Sdl3.WheelY));   // SDL x: positive right; ImGui: positive left
            case Sdl3.TextInput:
                return ImGuiInputHook.Text(Marshal.PtrToStringUTF8(*(nint*)(ev + Sdl3.TextPtr)));
            case Sdl3.WindowFocusGained:
            case Sdl3.WindowFocusLost:
                ImGuiInputHook.Focus(type == Sdl3.WindowFocusGained);
                return true;
            default:
                return true;
        }
    }
}
