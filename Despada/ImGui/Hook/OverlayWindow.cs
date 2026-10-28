// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Runtime.InteropServices;
using Despada.Modules.Protect;
using ImGuiNET;

namespace Despada.ImGui.Hook;

/// <summary>
/// The stream window: a plain Win32 window laid exactly over the game's client area, with its own
/// OpenGL context that shares objects with the game's.
///
/// Why it exists: a stream or any capture of the game window must show the game and nothing of
/// Despada. So the game keeps drawing only the game into its own backbuffer, we copy that frame
/// into a shared texture (a GPU blit, never a readback), and Despada's menu/ESP is drawn by this
/// window instead — a window that SetWindowDisplayAffinity keeps out of every capture. Locally the
/// player sees this window (the game frame plus the overlay); on stream it simply does not exist.
///
/// The window never takes focus or the mouse: WS_EX_NOACTIVATE, click-through (WS_EX_TRANSPARENT
/// and HTTRANSPARENT), topmost and out of the taskbar — the game's input and SDL's event flow stay
/// exactly as they were. It is shown only while the game is focused and there is something to draw.
///
/// Every failure degrades to the old behaviour (the game window excluded from capture, our drawing
/// suppressed while a capture may be in flight), so a capture never gets a frame with Despada in it.
/// </summary>
internal static class OverlayWindow
{
    /// <summary>
    /// The shared context exists and is usable: from now on Despada draws into this window, never
    /// into the game's framebuffer. Capture protection hangs off this (see <see cref="ScreenshotGuard"/>).
    /// </summary>
    public static bool Ready { get; private set; }

    /// <summary>Why the stream window is off; null while it is fine.</summary>
    public static string? Why { get; private set; }

    /// <summary>The stream window failed for good (this attempt): the caller falls back to hiding the game window.</summary>
    public static bool Failed => _failed;

    /// <summary>The overlay window is on screen this frame.</summary>
    public static bool Shown => _visible;

    /// <summary>
    /// This frame's game image, as an ImGui texture id (0: nothing to draw behind the overlay —
    /// the window is hidden or the frame couldn't be copied).
    /// </summary>
    public static nint GameTexture { get; private set; }

    // --- Win32 ---------------------------------------------------------------------------------

    private const string ClassName = "DespadaOverlayWindow";

    private const int WS_POPUP              = unchecked((int)0x80000000);
    private const int WS_EX_TOPMOST         = 0x00000008;
    private const int WS_EX_TRANSPARENT     = 0x00000020;
    private const int WS_EX_TOOLWINDOW      = 0x00000080;
    private const int WS_EX_LAYERED         = 0x00080000;
    private const int WS_EX_NOACTIVATE      = 0x08000000;

    private const uint LWA_ALPHA            = 0x00000002;

    private const uint SWP_NOSIZE           = 0x0001;
    private const uint SWP_NOMOVE           = 0x0002;
    private const uint SWP_NOACTIVATE       = 0x0010;

    private const int SW_HIDE               = 0;
    private const int SW_SHOWNA             = 8;

    private const uint WM_PAINT             = 0x000F;
    private const uint WM_ERASEBKGND        = 0x0014;
    private const uint WM_MOUSEACTIVATE     = 0x0021;
    private const uint WM_NCHITTEST         = 0x0084;

    private const int  MA_NOACTIVATE        = 3;
    private static readonly nint HTTRANSPARENT = -1;
    private static readonly nint HWND_TOPMOST  = -1;

    private const uint PfdDrawToWindow      = 0x00000004;
    private const uint PfdSupportOpenGl     = 0x00000020;
    private const uint PfdDoubleBuffer      = 0x00000001;
    private const byte PfdTypeRgba          = 0;
    private const byte PfdMainPlane         = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint   cbSize;
        public uint   style;
        public nint   lpfnWndProc;
        public int    cbClsExtra;
        public int    cbWndExtra;
        public nint   hInstance;
        public nint   hIcon;
        public nint   hCursor;
        public nint   hbrBackground;
        public nint   lpszMenuName;
        public nint   lpszClassName;
        public nint   hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize;
        public ushort nVersion;
        public uint   dwFlags;
        public byte   iPixelType;
        public byte   cColorBits;
        public byte   cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift;
        public byte   cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits, cAccumGreenBits;
        public byte   cAccumBlueBits, cAccumAlphaBits;
        public byte   cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
        public uint   dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);
    private delegate nint D_WglCreateContextAttribs(nint hdc, nint share, int[] attribs);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(int exStyle, string className, string windowName, int style,
        int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);

    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref POINT p);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int  ReleaseDC(nint hwnd, nint dc);
    [DllImport("user32.dll")] private static extern bool ValidateRect(nint hwnd, nint rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);
    // kernel32, not user32: this one lives in the process loader.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? name);

    [DllImport("gdi32.dll")] private static extern int  GetPixelFormat(nint dc);
    [DllImport("gdi32.dll")]
    private static extern int DescribePixelFormat(nint dc, int format, uint size, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool SetPixelFormat(nint dc, int format, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")] private static extern int ChoosePixelFormat(nint dc, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")] private static extern bool SwapBuffers(nint dc);

    [DllImport("opengl32.dll")] private static extern nint wglGetCurrentContext();
    [DllImport("opengl32.dll")] private static extern nint wglGetCurrentDC();
    [DllImport("opengl32.dll")] private static extern bool wglMakeCurrent(nint dc, nint ctx);
    [DllImport("opengl32.dll")] private static extern nint wglCreateContext(nint dc);
    [DllImport("opengl32.dll")] private static extern bool wglShareLists(nint src, nint dst);
    [DllImport("opengl32.dll")] private static extern bool wglDeleteContext(nint ctx);

    // --- GL constants --------------------------------------------------------------------------

    private const uint GL_TEXTURE_2D                  = 0x0DE1;
    private const uint GL_TEXTURE0                    = 0x84C0;
    private const uint GL_TEXTURE_MIN_FILTER          = 0x2801;
    private const uint GL_TEXTURE_MAG_FILTER          = 0x2800;
    private const uint GL_TEXTURE_WRAP_S              = 0x2802;
    private const uint GL_TEXTURE_WRAP_T              = 0x2803;
    private const uint GL_LINEAR                      = 0x2601;
    private const uint GL_NEAREST                     = 0x2600;
    private const int  GL_CLAMP_TO_EDGE               = 0x812F;
    private const uint GL_READ_FRAMEBUFFER            = 0x8CA8;
    private const uint GL_DRAW_FRAMEBUFFER            = 0x8CA9;
    private const uint GL_READ_FRAMEBUFFER_BINDING    = 0x8CAA;
    private const uint GL_DRAW_FRAMEBUFFER_BINDING    = 0x8CA6;
    private const uint GL_COLOR_ATTACHMENT0           = 0x8CE0;
    private const uint GL_FRAMEBUFFER_COMPLETE        = 0x8CD5;
    private const uint GL_COLOR_BUFFER_BIT            = 0x4000;
    private const uint GL_SCISSOR_TEST                = 0x0C11;
    private const uint GL_BLEND                       = 0x0BE2;
    private const uint GL_FRAMEBUFFER_SRGB            = 0x8DB9;
    private const uint GL_PIXEL_PACK_BUFFER           = 0x88EB;
    private const uint GL_PIXEL_PACK_BUFFER_BINDING   = 0x88ED;
    private const uint GL_PIXEL_UNPACK_BUFFER         = 0x88EC;
    private const uint GL_PIXEL_UNPACK_BUFFER_BINDING = 0x88EF;
    private const uint GL_MAJOR_VERSION               = 0x821B;
    private const uint GL_MINOR_VERSION               = 0x821C;
    private const uint GL_CONTEXT_PROFILE_MASK        = 0x9126;
    private const uint GL_VERSION                     = 0x1F02;

    private const int WglContextMajorVersion = 0x2091;
    private const int WglContextMinorVersion = 0x2092;
    private const int WglContextProfileMask  = 0x9126;

    // --- state ---------------------------------------------------------------------------------

    private static nint _hwnd, _hdc, _hglrc;
    private static nint _gameWnd, _gameCtx, _gameDc;

    private static uint _tex, _fbo, _vao;
    private static int  _texW, _texH;       // the shared game-frame texture
    private static int  _w, _h;             // the game's client size (what we cover)
    private static int  _lastX = int.MinValue, _lastY, _lastW, _lastH;

    private static bool _wanted;            // the protect feature is in stream mode
    private static bool _visible;           // on screen right now
    private static bool _frameShow;         // on screen this frame (set by BeginFrame)
    private static bool _failed;
    private static ushort _classAtom;
    private static int _ticks;
    private static int _checkFrames;

    private static readonly WndProc _wndProc = OnWndProc;

    private static nint OnWndProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            // Click-through to the game window (same thread), and never activated: the game keeps
            // the focus, the keyboard and the mouse exactly as if this window weren't here.
            if (msg == WM_NCHITTEST) return HTTRANSPARENT;
            if (msg == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
            if (msg == WM_PAINT)
            {
                ValidateRect(hwnd, 0);
                return 0;
            }
            if (msg == WM_ERASEBKGND) return 1;
        }
        catch { /* never throw across the native boundary */ }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // --- lifecycle (game thread) ----------------------------------------------------------------

    /// <summary>The protect feature's mode: true while the stream window is wanted.</summary>
    public static void SetWanted(bool on)
    {
        if (on && !_wanted)
        {
            // Switched back on after a failure: give it another try.
            _failed = false;
            Why    = null;
        }
        _wanted = on;
    }

    /// <summary>
    /// Every tick while the mode is on: creates the window once the game window exists, keeps it on
    /// the game's client area, closes it when the mode goes off. Pure user32/gdi32, no GL calls.
    /// </summary>
    public static void Sync()
    {
        if (!OperatingSystem.IsWindows())
        {
            if (_wanted) Fail("Windows only");
            return;
        }

        if (!_wanted)
        {
            Close();
            return;
        }

        if (_failed) return;

        if (_hwnd == 0)
        {
            _gameWnd = StreamProof.WindowHandle();
            if (_gameWnd == 0) return;   // the game window isn't up yet
            Create();
            if (_hwnd == 0) return;
        }

        // A fullscreen toggle makes a new window: follow it.
        if (++_ticks % 60 == 0)
        {
            var w = StreamProof.WindowHandle();
            if (w != 0) _gameWnd = w;
        }

        Place();
    }

    /// <summary>Destroys the window and its context (feature off, shutdown).</summary>
    public static void Shutdown()
    {
        _wanted = false;
        Close();
    }

    private static void Create()
    {
        try
        {
            if (!RegisterClass())
            {
                Fail("window class registration failed");
                return;
            }

            _hwnd = CreateWindowExW(
                WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE,
                ClassName, "Despada", WS_POPUP,
                0, 0, 8, 8, 0, 0, GetModuleHandleW(null), 0);
            if (_hwnd == 0)
            {
                Fail($"CreateWindowEx failed (error {Marshal.GetLastWin32Error()})");
                return;
            }

            // A layered window with WS_EX_TRANSPARENT is transparent to hit-testing across threads:
            // clicks and the mouse go straight to the game window underneath. HTTRANSPARENT in the
            // window procedure only works inside one thread, and SDL's window lives on the engine's
            // window thread — without this the topmost overlay swallowed every click.
            SetLayeredWindowAttributes(_hwnd, 0, 255, LWA_ALPHA);

            _hdc = GetDC(_hwnd);
            if (_hdc == 0)
            {
                Fail("GetDC failed");
                return;
            }

            if (!SetFormat())
            {
                Fail("no usable pixel format for the overlay window");
                return;
            }

            _w = _h = 0;
            _lastX = int.MinValue;
            MarseyLogger.Info("[OverlayWindow] window created");
        }
        catch (Exception ex)
        {
            Fail($"creating the overlay window failed: {ex.Message}");
        }
    }

    private static bool RegisterClass()
    {
        if (_classAtom != 0) return true;

        // No "tried once" latch: a failed attempt must be retryable, or one bad frame (the class
        // name not registered yet, a transient user32 error) kills the stream window for the session.
        var name = Marshal.StringToHGlobalUni(ClassName);
        try
        {
            var wc = default(WNDCLASSEXW);
            wc.cbSize        = (uint)Marshal.SizeOf<WNDCLASSEXW>();
            wc.lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_wndProc);
            wc.hInstance     = GetModuleHandleW(null);
            wc.lpszClassName = name;

            var atom = RegisterClassExW(ref wc);
            if (atom == 0) return false;
            _classAtom = atom;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }
    }

    /// <summary>
    /// The pixel format of the game window itself: contexts that share objects are the most
    /// compatible when their drawables have the same format, and the copy then goes through with
    /// no colour or alpha reinterpretation. A plain RGBA format is the fallback.
    /// </summary>
    private static bool SetFormat()
    {
        var size = (uint)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>();

        if (_gameWnd != 0)
        {
            var gameDc = GetDC(_gameWnd);
            if (gameDc != 0)
            {
                try
                {
                    var fmt = GetPixelFormat(gameDc);
                    if (fmt > 0)
                    {
                        var pfd = default(PIXELFORMATDESCRIPTOR);
                        pfd.nSize = (ushort)size;
                        if (DescribePixelFormat(gameDc, fmt, size, ref pfd) != 0
                            && SetPixelFormat(_hdc, fmt, ref pfd))
                            return true;
                    }
                }
                finally
                {
                    ReleaseDC(_gameWnd, gameDc);
                }
            }
        }

        var want = default(PIXELFORMATDESCRIPTOR);
        want.nSize        = (ushort)size;
        want.nVersion     = 1;
        want.dwFlags      = PfdDrawToWindow | PfdSupportOpenGl | PfdDoubleBuffer;
        want.iPixelType   = PfdTypeRgba;
        want.cColorBits   = 32;
        want.cAlphaBits   = 8;
        want.cDepthBits   = 24;
        want.cStencilBits = 8;
        want.iLayerType   = PfdMainPlane;

        var chosen = ChoosePixelFormat(_hdc, ref want);
        return chosen > 0 && SetPixelFormat(_hdc, chosen, ref want);
    }

    private static void Place()
    {
        if (_hwnd == 0 || _gameWnd == 0) return;
        if (!GetClientRect(_gameWnd, out var rc)) return;

        var a = new POINT { X = rc.Left,  Y = rc.Top };
        var b = new POINT { X = rc.Right, Y = rc.Bottom };
        if (!ClientToScreen(_gameWnd, ref a) || !ClientToScreen(_gameWnd, ref b)) return;

        int w = b.X - a.X, h = b.Y - a.Y;
        if (w <= 0 || h <= 0) return;

        _w = w;
        _h = h;

        // Re-assert topmost now and then (other overlays, the taskbar's own windows), but don't
        // touch the window every tick: a SetWindowPos on an unchanged rect still costs a message.
        var moved = a.X != _lastX || a.Y != _lastY || w != _lastW || h != _lastH;
        if (!moved && _ticks % 60 != 0) return;

        _lastX = a.X;
        _lastY = a.Y;
        _lastW = w;
        _lastH = h;
        SetWindowPos(_hwnd, HWND_TOPMOST, a.X, a.Y, w, h, SWP_NOACTIVATE);
    }

    // --- the GL context (created inside a frame, when the game's context is current) -------------

    private static bool EnsureContext()
    {
        if (Ready) return true;
        if (_failed || _hwnd == 0 || _hdc == 0) return false;

        var gameCtx = wglGetCurrentContext();
        var gameDc  = wglGetCurrentDC();
        if (gameCtx == 0 || gameDc == 0)
        {
            Fail("no current GL context");
            return false;
        }

        _gameCtx = gameCtx;
        _gameDc  = gameDc;

        nint ctx = 0;
        var proc = GlLoader.GetProc("wglCreateContextAttribsARB");
        if (proc != 0)
        {
            try
            {
                var create = Marshal.GetDelegateForFunctionPointer<D_WglCreateContextAttribs>(proc);

                // Match the game's own context: same profile, same version. A context that shares
                // objects has to look like a sibling, not like a stranger.
                var major = GlBackend.GetInt(GL_MAJOR_VERSION);
                var minor = GlBackend.GetInt(GL_MINOR_VERSION);
                var mask  = GlBackend.GetInt(GL_CONTEXT_PROFILE_MASK);
                GlBackend.DrainErrors();   // the two names are invalid on a pre-3.x context

                if (major > 0)
                {
                    var attribs = mask != 0
                        ? new[] { WglContextProfileMask, mask, WglContextMajorVersion, major, WglContextMinorVersion, minor, 0 }
                        : new[] { WglContextMajorVersion, major, WglContextMinorVersion, minor, 0 };
                    ctx = create(_hdc, gameCtx, attribs);
                }

                if (ctx == 0) ctx = create(_hdc, gameCtx, new[] { 0 });
            }
            catch { ctx = 0; }
        }

        if (ctx == 0)
        {
            // Legacy path: a plain context, then sharing by hand.
            ctx = wglCreateContext(_hdc);
            if (ctx != 0 && !wglShareLists(gameCtx, ctx))
            {
                wglDeleteContext(ctx);
                ctx = 0;
            }
        }

        if (ctx == 0)
        {
            Fail("the driver did not create a GL context for the overlay window");
            return false;
        }

        if (!wglMakeCurrent(_hdc, ctx))
        {
            wglDeleteContext(ctx);
            Fail("wglMakeCurrent failed for the overlay window");
            return false;
        }

        try
        {
            // Raw copy of the game's bytes: the same switch GlStateGuard makes in the game context.
            GlBackend.Disable(GL_FRAMEBUFFER_SRGB);
            _vao = GlBackend.GenVertexArray();

            if (GlBackend.IsProgram(ImGuiRenderer.ShaderId) == false)
            {
                wglMakeCurrent(gameDc, gameCtx);
                wglDeleteContext(ctx);
                Fail("the driver does not share GL objects between contexts");
                return false;
            }

            MarseyLogger.Info($"[OverlayWindow] context ready ({GlBackend.GetString(GL_VERSION)}), shares with the game");
        }
        catch (Exception ex)
        {
            wglMakeCurrent(gameDc, gameCtx);
            wglDeleteContext(ctx);
            Fail($"overlay context setup failed: {ex.Message}");
            return false;
        }

        wglMakeCurrent(gameDc, gameCtx);

        _hglrc = ctx;
        Ready  = true;
        Why    = null;

        // The game window goes back into capture — it now shows the game and nothing else — and
        // this window goes out of it for good.
        if (_gameWnd != 0) StreamProof.Clear(_gameWnd);
        if (!StreamProof.Apply(_hwnd))
        {
            Fail($"the overlay window can't be excluded from capture: {StreamProof.Why}");
            return false;
        }

        return true;
    }

    // --- per frame ------------------------------------------------------------------------------

    /// <summary>
    /// Called inside <see cref="ImGuiRenderer.RenderFrame"/>'s state guard, before the UI is built:
    /// decides whether the window is on screen this frame and copies the game's finished frame into
    /// the shared texture the overlay draws behind itself.
    /// </summary>
    public static void BeginFrame(bool needRender, int screenW, int screenH)
    {
        GameTexture = 0;

        // The engine can recreate its window and GL context (a fullscreen toggle). A context that
        // isn't the one we shared with means our objects belong to another share group: rebuild.
        if (Ready && wglGetCurrentContext() != _gameCtx) Close();

        if (!needRender || _hwnd == 0 || _gameWnd == 0)
        {
            _frameShow = false;
            SetShown(false);
            return;
        }

        // First frame that has something to draw creates the context (the game's is current here).
        if (!EnsureContext())
        {
            _frameShow = false;
            SetShown(false);
            return;
        }

        // Only over the game, and only while it is what the user is looking at: a topmost window
        // left over another application would be worse than useless.
        var show = GetForegroundWindow() == _gameWnd && !IsIconic(_gameWnd) && _w > 0 && _h > 0;
        _frameShow = show;
        SetShown(show);

        if (!show) return;

        CopyGameFrame(screenW, screenH);
    }

    private static void SetShown(bool show)
    {
        if (_hwnd == 0 || _visible == show) return;
        _visible = show;
        ShowWindow(_hwnd, show ? SW_SHOWNA : SW_HIDE);
        if (show) SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// The game's finished frame (the draw framebuffer, exactly what the engine just drew, in the
    /// game's context) into our shared texture. A blit, not a readback: no GPU→CPU→GPU round trip,
    /// and MSAA is resolved by the blit itself.
    /// </summary>
    private static void CopyGameFrame(int screenW, int screenH)
    {
        if (screenW <= 0 || screenH <= 0) return;

        try
        {
            var read   = GlBackend.GetInt(GL_READ_FRAMEBUFFER_BINDING);
            var draw   = GlBackend.GetInt(GL_DRAW_FRAMEBUFFER_BINDING);
            var pack   = GlBackend.GetInt(GL_PIXEL_PACK_BUFFER_BINDING);
            var unpack = GlBackend.GetInt(GL_PIXEL_UNPACK_BUFFER_BINDING);

            try
            {
                // A bound pack/unpack buffer would turn the texture upload into an offset into it.
                if (pack != 0)   GlBackend.BindBuffer(GL_PIXEL_PACK_BUFFER, 0);
                if (unpack != 0) GlBackend.BindBuffer(GL_PIXEL_UNPACK_BUFFER, 0);

                if (_tex == 0 || _texW != screenW || _texH != screenH) AllocTexture(screenW, screenH);

                GlBackend.Disable(GL_SCISSOR_TEST);
                GlBackend.Disable(GL_BLEND);

                // The finished frame is in the draw binding (what Clyde rendered into last, the
                // window target in practice) — the same source GlassBlur blurs from.
                if (_checkFrames > 0) GlBackend.DrainErrors();   // Clyde leaves errors unread

                GlBackend.BindFramebuffer(GL_READ_FRAMEBUFFER, (uint)draw);
                GlBackend.BindFramebuffer(GL_DRAW_FRAMEBUFFER, _fbo);
                GlBackend.BlitFramebuffer(0, 0, screenW, screenH, 0, 0, screenW, screenH,
                    GL_COLOR_BUFFER_BIT, GL_NEAREST);

                if (_checkFrames > 0)
                {
                    _checkFrames--;
                    var err = GlBackend.GetError();
                    if (err != 0)
                    {
                        GlBackend.DrainErrors();
                        Fail($"copying the game frame failed (GL error 0x{err:X4})");
                        return;
                    }
                }
            }
            finally
            {
                GlBackend.BindFramebuffer(GL_DRAW_FRAMEBUFFER, (uint)draw);
                GlBackend.BindFramebuffer(GL_READ_FRAMEBUFFER, (uint)read);
                if (pack != 0)   GlBackend.BindBuffer(GL_PIXEL_PACK_BUFFER, (uint)pack);
                if (unpack != 0) GlBackend.BindBuffer(GL_PIXEL_UNPACK_BUFFER, (uint)unpack);
            }

            GameTexture = (nint)_tex;
        }
        catch (Exception ex)
        {
            Fail($"copying the game frame failed: {ex.Message}");
        }
    }

    private static void AllocTexture(int w, int h)
    {
        if (_tex != 0) { GlBackend.DeleteTexture(_tex); _tex = 0; }
        if (_fbo != 0) { GlBackend.DeleteFramebuffer(_fbo); _fbo = 0; }

        GlBackend.ActiveTexture(GL_TEXTURE0);
        _tex = GlBackend.GenTexture();
        GlBackend.BindTexture(GL_TEXTURE_2D, _tex);
        GlBackend.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_LINEAR);
        GlBackend.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, (int)GL_LINEAR);
        GlBackend.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
        GlBackend.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
        GlBackend.TexImage2DEmpty(w, h);

        _fbo = GlBackend.GenFramebuffer();
        GlBackend.BindFramebuffer(GL_DRAW_FRAMEBUFFER, _fbo);
        GlBackend.FramebufferTexture2D(GL_DRAW_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, _tex, 0);
        var status = GlBackend.CheckFramebufferStatus(GL_DRAW_FRAMEBUFFER);

        GlBackend.BindFramebuffer(GL_DRAW_FRAMEBUFFER, 0);
        GlBackend.BindTexture(GL_TEXTURE_2D, 0);

        if (status != GL_FRAMEBUFFER_COMPLETE)
        {
            _texW = _texH = 0;
            throw new InvalidOperationException($"game frame framebuffer incomplete (0x{status:X4})");
        }

        _texW = w;
        _texH = h;
        _checkFrames = 3;
    }

    /// <summary>
    /// Draws <paramref name="drawData"/> into this window and swaps it. Runs at the end of the
    /// frame, after the game's own work in its context; false means the mirror failed this frame
    /// and the caller must draw into the game's framebuffer instead (drawing is already suppressed
    /// for any capture that may be in flight by then).
    /// </summary>
    public static bool Render(ImDrawDataPtr drawData, int screenW, int screenH)
    {
        if (!Ready) return false;
        if (!_frameShow) return true;
        if (screenW <= 0 || screenH <= 0) return true;

        try
        {
            // Whatever context the engine has current right now is the one to come back to: it may
            // have been recreated since the window was made.
            var gameCtx = wglGetCurrentContext();
            var gameDc  = wglGetCurrentDC();
            if (gameCtx == 0 || gameDc == 0)
                throw new InvalidOperationException("no current GL context to return to");

            _gameCtx = gameCtx;
            _gameDc  = gameDc;

            // Everything the game's context did this frame (our blit, a font atlas rebuild, the
            // glass blur) must be executed before the other context may read the shared objects.
            GlBackend.Finish();

            if (!wglMakeCurrent(_hdc, _hglrc))
                throw new InvalidOperationException("wglMakeCurrent(overlay) failed");

            try
            {
                GlBackend.BindFramebuffer(GL_DRAW_FRAMEBUFFER, 0);
                GlBackend.BindFramebuffer(GL_READ_FRAMEBUFFER, 0);
                GlBackend.Viewport(0, 0, screenW, screenH);
                GlBackend.Disable(GL_FRAMEBUFFER_SRGB);
                GlBackend.Disable(GL_SCISSOR_TEST);
                GlBackend.ClearColor(0f, 0f, 0f, 1f);
                GlBackend.Clear(GL_COLOR_BUFFER_BIT);

                ImGuiRenderer.RenderDrawDataInto(drawData, _vao);

                if (!SwapBuffers(_hdc))
                    throw new InvalidOperationException($"SwapBuffers failed (error {Marshal.GetLastWin32Error()})");
            }
            finally
            {
                if (!wglMakeCurrent(gameDc, gameCtx))
                    MarseyLogger.Fatal("[OverlayWindow] could not switch back to the game's GL context");
            }

            return true;
        }
        catch (Exception ex)
        {
            Fail($"drawing into the overlay window failed: {ex.Message}");
            return false;
        }
    }

    // --- teardown / failure ---------------------------------------------------------------------

    private static void Close()
    {
        _frameShow = false;

        if (_hwnd != 0)
        {
            _visible = false;
            ShowWindow(_hwnd, SW_HIDE);
        }

        if (_hglrc != 0)
        {
            wglDeleteContext(_hglrc);
            _hglrc = 0;
        }

        Ready       = false;
        GameTexture = 0;

        if (_hdc != 0 && _hwnd != 0)
        {
            ReleaseDC(_hwnd, _hdc);
            _hdc = 0;
        }

        if (_hwnd != 0)
        {
            DestroyWindow(_hwnd);
            _hwnd = 0;
        }

        _tex = _fbo = _vao = 0;
        _texW = _texH = _w = _h = 0;
        _lastX = int.MinValue;
        _lastW = _lastH = 0;
        _ticks = 0;
        _checkFrames = 0;
    }

    /// <summary>
    /// The stream window can't work. Before anything else the game window goes back out of capture
    /// and our drawing goes down: from this instant a capture gets the game without Despada, even
    /// for the frames before the feature's tick applies its fallback.
    /// </summary>
    private static void Fail(string why)
    {
        if (_failed) return;
        _failed = true;
        Why     = why;
        MarseyLogger.Warn($"[OverlayWindow] off: {why}");

        try
        {
            var gw = _gameWnd != 0 ? _gameWnd : StreamProof.WindowHandle();
            if (gw != 0 && StreamProof.Apply(gw)) _gameWnd = gw;
        }
        catch { /* best effort */ }

        ImGuiRenderer.SuppressDrawing(500);
        Close();
    }
}