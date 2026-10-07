// SPDX-FileCopyrightText: 2026 ddepsadd <https://github.com/ddepsadd>
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.Numerics;
using System.Runtime.InteropServices;
using Despada.Config;

namespace Despada.ImGui;

/// <summary>
/// Frosted-glass backdrop: copies the part of Clyde's finished frame under the main window and
/// runs a dual Kawase blur over it. The result (level 1, half resolution) is drawn by the UI as an
/// image under the window tint.
///
/// Frame order: UI build calls <see cref="Request"/> + <see cref="GetUv"/>, then RenderFrame calls
/// <see cref="Run"/> (inside GlStateGuard, before RenderDrawData), so the image is from this frame.
/// A GL error or incomplete FBO right after a (re)allocation is retried a few times (a resize or
/// fullscreen toggle can trip it once); a frame that couldn't be blurred shows only the tint. A
/// persistent failure disables the effect for the session; the UI then falls back to the plain WindowBg.
///
/// Only ints/uints/floats/nint/System.Numerics types are stored here (MarseyLoader GetTypes()).
/// </summary>
internal static class GlassBlur
{
    private delegate void D_GenFramebuffers(int n, out uint framebuffers);
    private delegate void D_DeleteFramebuffers(int n, ref uint framebuffers);
    private delegate void D_BindFramebuffer(uint target, uint framebuffer);
    private delegate void D_FramebufferTexture2D(uint target, uint attachment, uint texTarget, uint texture, int level);
    private delegate uint D_CheckFramebufferStatus(uint target);
    private delegate void D_BlitFramebuffer(int sx0, int sy0, int sx1, int sy1, int dx0, int dy0, int dx1, int dy1, uint mask, uint filter);
    private delegate void D_GetFramebufferAttachmentParameteriv(uint target, uint attachment, uint pname, out int param);
    private delegate void D_TexImage2D(uint target, int level, int internalFormat, int width, int height, int border, uint format, uint type, nint pixels);
    private delegate void D_Viewport(int x, int y, int width, int height);
    private delegate void D_DrawArrays(uint mode, int first, int count);
    private delegate uint D_GetError();
    private delegate void D_GetIntegerv(uint pname, out int data);
    private delegate void D_GetIntegervArr(uint pname, int[] data);
    private delegate void D_Cap(uint cap);
    [return: MarshalAs(UnmanagedType.U1)] private delegate bool D_IsEnabled(uint cap);
    private delegate void D_Uniform1f(int location, float v0);
    private delegate void D_Uniform2f(int location, float v0, float v1);
    private delegate void D_Uniform4f(int location, float v0, float v1, float v2, float v3);
    private delegate void D_DeleteProgram(uint program);
    private delegate void D_DeleteVertexArrays(int n, ref uint arrays);
    private delegate void D_GetFloatv(uint pname, float[] data);
    private delegate void D_ClearColor(float r, float g, float b, float a);
    private delegate void D_Clear(uint mask);

    private static D_GenFramebuffers         glGenFramebuffers         = null!;
    private static D_DeleteFramebuffers      glDeleteFramebuffers      = null!;
    private static D_BindFramebuffer         glBindFramebuffer         = null!;
    private static D_FramebufferTexture2D    glFramebufferTexture2D    = null!;
    private static D_CheckFramebufferStatus  glCheckFramebufferStatus  = null!;
    private static D_BlitFramebuffer         glBlitFramebuffer         = null!;
    private static D_GetFramebufferAttachmentParameteriv? glGetFramebufferAttachmentParameteriv;
    private static D_TexImage2D              glTexImage2D              = null!;
    private static D_Viewport                glViewport                = null!;
    private static D_DrawArrays              glDrawArrays              = null!;
    private static D_GetError                glGetError                = null!;
    private static D_GetIntegerv             glGetIntegerv             = null!;
    private static D_GetIntegervArr          glGetIntegervArr          = null!;
    private static D_Cap                     glEnable                  = null!;
    private static D_Cap                     glDisable                 = null!;
    private static D_IsEnabled               glIsEnabled               = null!;
    private static D_Uniform1f               glUniform1f               = null!;
    private static D_Uniform2f               glUniform2f               = null!;
    private static D_Uniform4f               glUniform4f               = null!;
    private static D_DeleteProgram           glDeleteProgram           = null!;
    private static D_DeleteVertexArrays      glDeleteVertexArrays      = null!;
    private static D_GetFloatv               glGetFloatv               = null!;
    private static D_ClearColor              glClearColor              = null!;
    private static D_Clear                   glClear                   = null!;

    private const uint GL_TEXTURE_2D                 = 0x0DE1;
    private const uint GL_TEXTURE0                   = 0x84C0;
    private const uint GL_TEXTURE_MIN_FILTER         = 0x2801;
    private const uint GL_TEXTURE_MAG_FILTER         = 0x2800;
    private const uint GL_TEXTURE_WRAP_S             = 0x2802;
    private const uint GL_TEXTURE_WRAP_T             = 0x2803;
    private const uint GL_TEXTURE_MAX_LEVEL          = 0x813D;
    private const int  GL_CLAMP_TO_EDGE              = 0x812F;
    private const uint GL_LINEAR                     = 0x2601;
    private const uint GL_NEAREST                    = 0x2600;
    private const int  GL_RGBA8                      = 0x8058;
    private const int  GL_SRGB8_ALPHA8               = 0x8C43;
    private const uint GL_RGBA                       = 0x1908;
    private const uint GL_UNSIGNED_BYTE              = 0x1401;
    private const uint GL_READ_FRAMEBUFFER           = 0x8CA8;
    private const uint GL_DRAW_FRAMEBUFFER           = 0x8CA9;
    private const uint GL_DRAW_FRAMEBUFFER_BINDING   = 0x8CA6;
    private const uint GL_READ_FRAMEBUFFER_BINDING   = 0x8CAA;
    private const uint GL_COLOR_ATTACHMENT0          = 0x8CE0;
    private const uint GL_FRAMEBUFFER_COMPLETE       = 0x8CD5;
    private const uint GL_COLOR_BUFFER_BIT           = 0x4000;
    private const uint GL_SAMPLE_BUFFERS             = 0x80A8;
    private const uint GL_SAMPLES                    = 0x80A9;
    private const uint GL_FRAMEBUFFER_ATTACHMENT_COLOR_ENCODING = 0x8210;
    private const uint GL_BACK                       = 0x0405;
    private const uint GL_BACK_LEFT                  = 0x0402;
    private const int  GL_SRGB                       = 0x8C40;
    private const int  GL_LINEAR_ENC                 = 0x2601;
    private const uint GL_PIXEL_UNPACK_BUFFER        = 0x88EC;
    private const uint GL_PIXEL_UNPACK_BUFFER_BINDING = 0x88EF;
    private const uint GL_VIEWPORT                   = 0x0BA2;
    private const uint GL_COLOR_CLEAR_VALUE          = 0x0C22;
    private const uint GL_BLEND                      = 0x0BE2;
    private const uint GL_SCISSOR_TEST               = 0x0C11;
    private const uint GL_DEPTH_TEST                 = 0x0B71;
    private const uint GL_STENCIL_TEST               = 0x0B90;
    private const uint GL_CULL_FACE                  = 0x0B44;
    private const uint GL_TRIANGLES                  = 0x0004;
    private const uint GL_VERSION                    = 0x1F02;
    private const uint GL_RENDERER                   = 0x1F01;

    // Deepest level we ever use; the region is snapped to multiples of 2^MaxLevels so every level's
    // sub-rect is exact, and the textures are padded to that multiple too.
    private const int MaxLevels = 4;
    private const int Snap      = 1 << MaxLevels;

    // Level i: (TexW >> i) x (TexH >> i). Level 0 (full res) only exists for MSAA resolves.
    private static readonly uint[] _tex = new uint[MaxLevels + 1];
    private static readonly uint[] _fbo = new uint[MaxLevels + 1];
    private static readonly int[]  _vp  = new int[4];
    private static readonly float[] _clearColor = new float[4];

    private static uint _progDown, _progUp, _vao;
    // Uniform locations: tex, rect, texel, clamp, offset.
    private static readonly int[] _locDown = new int[5];
    private static readonly int[] _locUp   = new int[5];

    private static int _texW, _texH;        // padded level-0 size
    private static int _scrW, _scrH;        // screen size the textures were made for
    private static int _internalFormat;
    private static int _encoding;           // GL_SRGB / GL_LINEAR / 0 = unknown
    private static int _sampleBuffers, _samples;
    private static int _checkFrames;        // glGetError checks left after (re)allocation
    private static int _strikes;            // failed checks since the last clean allocation

    // Consecutive failed checks before the effect is disabled for good.
    private const int MaxStrikes = 4;

    private static bool _loaded;
    private static bool _ready;
    private static bool _failed;
    private static bool _releasePending;
    private static bool _broken;            // last attempt failed, retrying: don't draw the texture

    private static bool    _requested;
    private static Vector2 _reqMin, _reqMax;

    /// <summary>Blur is initialised, hasn't failed and is enabled in the settings.</summary>
    public static bool Available => _ready && !_failed && DespadaConfig.Current.GlassBlur > 0f;

    /// <summary>
    /// GL name of the blurred (level 1) texture, as an ImTextureID; 0 while a failed frame is being
    /// retried (draw only the tint then).
    /// </summary>
    public static nint Texture => _ready && !_failed && !_broken ? (nint)_tex[1] : 0;

    /// <summary>Called while building the UI: blur the area under this rect (ImGui logical units) this frame.</summary>
    public static void Request(Vector2 minLogical, Vector2 maxLogical)
    {
        _reqMin    = minLogical;
        _reqMax    = maxLogical;
        _requested = true;
    }

    /// <summary>Texture coordinates of <paramref name="minLogical"/>..<paramref name="maxLogical"/> in <see cref="Texture"/>.</summary>
    public static void GetUv(Vector2 minLogical, Vector2 maxLogical, out Vector2 uv0, out Vector2 uv1)
    {
        // From this frame's screen size, i.e. what Run will (re)allocate for, so the first frame after
        // a resize doesn't sample with the old texture layout.
        var k    = UiScale.K;
        var disp = ImGuiNET.ImGui.GetIO().DisplaySize;
        var scrW = (int)MathF.Round(disp.X * k);
        var scrH = (int)MathF.Round(disp.Y * k);
        var texW = (scrW + Snap - 1) & ~(Snap - 1);
        var texH = (scrH + Snap - 1) & ~(Snap - 1);
        if (texW <= 0 || texH <= 0)
        {
            uv0 = Vector2.Zero;
            uv1 = Vector2.One;
            return;
        }

        // Logical -> framebuffer pixels; GL rows start at the bottom.
        uv0 = new Vector2(minLogical.X * k / texW, (scrH - minLogical.Y * k) / texH);
        uv1 = new Vector2(maxLogical.X * k / texW, (scrH - maxLogical.Y * k) / texH);
    }

    /// <summary>Inside GlStateGuard, between ImGui.Render() and RenderDrawData. Never throws.</summary>
    public static void Run(int screenW, int screenH)
    {
        var requested = _requested;
        _requested = false;

        try
        {
            if (_releasePending)
            {
                _releasePending = false;
                FreeAll();
            }

            if (_failed) return;

            // Lazy init: the UI only asks once Available is true, so the first frame with the overlay
            // open initialises here and draws the plain background.
            bool init = !_ready;
            if (init && (!ImGuiRenderer.OverlayVisible || DespadaConfig.Current.GlassBlur <= 0f)) return;
            if (!init && !requested) return;
            if (screenW <= 0 || screenH <= 0) return;

            if (!_loaded && !LoadFunctions(out var missing))
            {
                Fail($"missing {missing}", deferFree: false);
                return;
            }

            glGetIntegerv(GL_DRAW_FRAMEBUFFER_BINDING, out var drawFbo);
            glGetIntegerv(GL_READ_FRAMEBUFFER_BINDING, out var readFbo);
            glGetIntegervArr(GL_VIEWPORT, _vp);
            var blend = glIsEnabled(GL_BLEND);

            bool produced = false;
            try
            {
                if (init)
                    Init(drawFbo, screenW, screenH);
                else
                    produced = Process(drawFbo, screenW, screenH);
            }
            catch (Exception ex)
            {
                // During Process the UI already references the texture this frame: free it next frame.
                Fail(ex.Message, deferFree: !init);
            }
            finally
            {
                // The UI already references the texture: never let it show a stale or undefined image.
                if (!init && !produced) Invalidate();

                // RenderDrawData draws into whatever is bound and doesn't set the viewport itself.
                glBindFramebuffer(GL_DRAW_FRAMEBUFFER, (uint)drawFbo);
                glBindFramebuffer(GL_READ_FRAMEBUFFER, (uint)readFbo);
                glViewport(_vp[0], _vp[1], _vp[2], _vp[3]);
                if (blend) glEnable(GL_BLEND);
            }
        }
        catch (Exception ex)
        {
            try { Fail(ex.Message, deferFree: true); } catch { /* never throw out of Run */ }
        }
    }

    /// <summary>Frees every GL object. Needs the GL context current.</summary>
    public static void Release()
    {
        try
        {
            FreeAll();
        }
        catch (Exception ex)
        {
            MarseyLogger.Warn($"[GlassBlur] Release failed: {ex.Message}");
        }
        _releasePending = false;
        _requested      = false;
    }

    // --- setup -------------------------------------------------------------------------------

    private static bool LoadFunctions(out string missing)
    {
        missing = "";
        _loaded = true;

        bool ok = true;
        T L<T>(string name, ref string miss) where T : Delegate
        {
            var d = GlLoader.TryLoad<T>(name);
            if (d is null)
            {
                ok = false;
                miss += miss.Length == 0 ? name : ", " + name;
            }
            return d!;
        }

        glGenFramebuffers        = L<D_GenFramebuffers>("glGenFramebuffers", ref missing);
        glDeleteFramebuffers     = L<D_DeleteFramebuffers>("glDeleteFramebuffers", ref missing);
        glBindFramebuffer        = L<D_BindFramebuffer>("glBindFramebuffer", ref missing);
        glFramebufferTexture2D   = L<D_FramebufferTexture2D>("glFramebufferTexture2D", ref missing);
        glCheckFramebufferStatus = L<D_CheckFramebufferStatus>("glCheckFramebufferStatus", ref missing);
        glBlitFramebuffer        = L<D_BlitFramebuffer>("glBlitFramebuffer", ref missing);
        glTexImage2D             = L<D_TexImage2D>("glTexImage2D", ref missing);
        glViewport               = L<D_Viewport>("glViewport", ref missing);
        glDrawArrays             = L<D_DrawArrays>("glDrawArrays", ref missing);
        glGetError               = L<D_GetError>("glGetError", ref missing);
        glGetIntegerv            = L<D_GetIntegerv>("glGetIntegerv", ref missing);
        glGetIntegervArr         = L<D_GetIntegervArr>("glGetIntegerv", ref missing);
        glEnable                 = L<D_Cap>("glEnable", ref missing);
        glDisable                = L<D_Cap>("glDisable", ref missing);
        glIsEnabled              = L<D_IsEnabled>("glIsEnabled", ref missing);
        glUniform1f              = L<D_Uniform1f>("glUniform1f", ref missing);
        glUniform2f              = L<D_Uniform2f>("glUniform2f", ref missing);
        glUniform4f              = L<D_Uniform4f>("glUniform4f", ref missing);
        glDeleteProgram          = L<D_DeleteProgram>("glDeleteProgram", ref missing);
        glDeleteVertexArrays     = L<D_DeleteVertexArrays>("glDeleteVertexArrays", ref missing);
        glGetFloatv              = L<D_GetFloatv>("glGetFloatv", ref missing);
        glClearColor             = L<D_ClearColor>("glClearColor", ref missing);
        glClear                  = L<D_Clear>("glClear", ref missing);
        // Only used for the encoding log/decision; optional.
        glGetFramebufferAttachmentParameteriv =
            GlLoader.TryLoad<D_GetFramebufferAttachmentParameteriv>("glGetFramebufferAttachmentParameteriv");

        if (!ok) _loaded = false;
        return ok;
    }

    private static void Init(int drawFbo, int screenW, int screenH)
    {
        var gles = GlBackend.IsGles;
        DrainErrors();

        // GL_SAMPLE_BUFFERS describes the bound draw framebuffer, i.e. the window target here.
        glGetIntegerv(GL_SAMPLE_BUFFERS, out _sampleBuffers);
        glGetIntegerv(GL_SAMPLES,        out _samples);
        DrainErrors();

        _encoding = QueryEncoding(drawFbo, gles);
        // Desktop: GlStateGuard keeps FRAMEBUFFER_SRGB off, so every copy is raw bytes -> plain RGBA8.
        // GLES has no such switch: an sRGB window decodes/encodes on every access, so keep the copy
        // sRGB too (blit is then a raw copy and the blur happens on linear values).
        _internalFormat = gles && _encoding == GL_SRGB ? GL_SRGB8_ALPHA8 : GL_RGBA8;

        var header = GlBackend.ShaderHeader();
        if (gles) header += "precision highp float;\n";

        try
        {
            _progDown = GlBackend.CreateShader(header + VertSrc, header + FragDownSrc);
            _progUp   = GlBackend.CreateShader(header + VertSrc, header + FragUpSrc);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"shader: {ex.Message}");
        }
        GetLocations(_progDown, _locDown);
        GetLocations(_progUp,   _locUp);

        _vao = GlBackend.GenVertexArray();

        Allocate(screenW, screenH);
        CheckErrors("init");

        _ready   = true;
        _broken  = false;
        _strikes = 0;
        MarseyLogger.Info($"[GlassBlur] ready: {screenW}x{screenH} (tex {_texW}x{_texH}), " +
                          $"format {(_internalFormat == GL_SRGB8_ALPHA8 ? "SRGB8_ALPHA8" : "RGBA8")}, " +
                          $"window encoding {EncodingName(_encoding)}, samples {_sampleBuffers}/{_samples}, " +
                          $"target fbo {drawFbo}; {GlBackend.GetString(GL_VERSION)}");
    }

    private static void GetLocations(uint prog, int[] loc)
    {
        loc[0] = GlBackend.GetUniformLocation(prog, "u_tex");
        loc[1] = GlBackend.GetUniformLocation(prog, "u_rect");
        loc[2] = GlBackend.GetUniformLocation(prog, "u_texel");
        loc[3] = GlBackend.GetUniformLocation(prog, "u_clamp");
        loc[4] = GlBackend.GetUniformLocation(prog, "u_offset");
    }

    private static int QueryEncoding(int drawFbo, bool gles)
    {
        if (glGetFramebufferAttachmentParameteriv is null) return 0;

        // Default framebuffer: GLES names its color buffer GL_BACK, desktop GL_BACK_LEFT.
        var attachment = drawFbo != 0 ? GL_COLOR_ATTACHMENT0 : gles ? GL_BACK : GL_BACK_LEFT;
        glBindFramebuffer(GL_READ_FRAMEBUFFER, (uint)drawFbo);
        glGetFramebufferAttachmentParameteriv(GL_READ_FRAMEBUFFER, attachment,
            GL_FRAMEBUFFER_ATTACHMENT_COLOR_ENCODING, out var enc);
        return DrainErrors() ? 0 : enc;
    }

    private static string EncodingName(int enc) => enc switch
    {
        GL_SRGB       => "sRGB",
        GL_LINEAR_ENC => "linear",
        _             => "unknown",
    };

    // (Re)creates the level textures + FBOs for this screen size. Level 0 only with MSAA.
    private static void Allocate(int screenW, int screenH)
    {
        _scrW = screenW;
        _scrH = screenH;
        _texW = (screenW + Snap - 1) & ~(Snap - 1);
        _texH = (screenH + Snap - 1) & ~(Snap - 1);

        // A bound unpack buffer would turn the null pixel pointer into an offset into it.
        glGetIntegerv(GL_PIXEL_UNPACK_BUFFER_BINDING, out var unpack);
        if (unpack != 0) GlBackend.BindBuffer(GL_PIXEL_UNPACK_BUFFER, 0);

        try
        {
            GlBackend.ActiveTexture(GL_TEXTURE0);
            for (int i = _sampleBuffers > 0 ? 0 : 1; i <= MaxLevels; i++)
            {
                if (_tex[i] == 0) _tex[i] = GlBackend.GenTexture();
                if (_fbo[i] == 0) { glGenFramebuffers(1, out var f); _fbo[i] = f; }

                GlBackend.BindTexture(GL_TEXTURE_2D, _tex[i]);
                GlBackend.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, (int)GL_LINEAR);
                GlBackend.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, (int)GL_LINEAR);
                GlBackend.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
                GlBackend.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
                GlBackend.TexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAX_LEVEL, 0);
                glTexImage2D(GL_TEXTURE_2D, 0, _internalFormat, _texW >> i, _texH >> i, 0,
                    GL_RGBA, GL_UNSIGNED_BYTE, 0);

                glBindFramebuffer(GL_DRAW_FRAMEBUFFER, _fbo[i]);
                glFramebufferTexture2D(GL_DRAW_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D, _tex[i], 0);
                var status = glCheckFramebufferStatus(GL_DRAW_FRAMEBUFFER);
                if (status != GL_FRAMEBUFFER_COMPLETE)
                    throw new InvalidOperationException($"level {i} framebuffer incomplete (0x{status:X4})");
            }
            GlBackend.BindTexture(GL_TEXTURE_2D, 0);
        }
        finally
        {
            if (unpack != 0) GlBackend.BindBuffer(GL_PIXEL_UNPACK_BUFFER, (uint)unpack);
        }

        // Watch the first frames after (re)allocation for errors from the blits/passes.
        _checkFrames = 3;
    }

    // --- per frame ---------------------------------------------------------------------------

    // True if this frame's image was produced; false leaves the texture to Invalidate.
    private static bool Process(int drawFbo, int screenW, int screenH)
    {
        // Sample buffers can change if Clyde recreates the window target.
        glGetIntegerv(GL_SAMPLE_BUFFERS, out var sampleBuffers);
        var msaa = sampleBuffers > 0;
        if (msaa != _sampleBuffers > 0)
        {
            _sampleBuffers = sampleBuffers;
            glGetIntegerv(GL_SAMPLES, out _samples);
            _scrW = 0; // force reallocation (level 0 needed / not needed)
        }
        var realloc = screenW != _scrW || screenH != _scrH;

        // GL errors are sticky and Clyde doesn't read them (display.ogl_check_errors is off): drop
        // whatever it left behind (e.g. while resizing its targets) so the checks below only see ours.
        if (realloc || _checkFrames > 0) DrainErrors();

        // The window target can be briefly unusable while the window is resized or (un)fullscreened.
        glBindFramebuffer(GL_READ_FRAMEBUFFER, (uint)drawFbo);
        if (glCheckFramebufferStatus(GL_READ_FRAMEBUFFER) != GL_FRAMEBUFFER_COMPLETE) return false;

        if (realloc)
        {
            try
            {
                Allocate(screenW, screenH);
            }
            catch (InvalidOperationException ex)
            {
                return Strike(ex.Message, screenW, screenH);
            }
        }

        // Radius in framebuffer pixels, scaled with resolution.
        var s      = Math.Clamp(DespadaConfig.Current.GlassBlur, 0f, 1f);
        var r      = (8f + 56f * s) * screenH / 1080f;
        var levels = Math.Clamp((int)MathF.Ceiling(MathF.Log2(r / 3f)), 2, MaxLevels);
        var offset = r / (1 << levels);

        // Window rect -> framebuffer pixels (Y up), plus a margin so edge pixels see real neighbours.
        var k      = UiScale.K;
        var margin = (int)MathF.Ceiling(2f * r);
        var left   = (int)MathF.Floor(_reqMin.X * k) - margin;
        var right  = (int)MathF.Ceiling(_reqMax.X * k) + margin;
        var bottom = screenH - (int)MathF.Ceiling(_reqMax.Y * k) - margin;
        var top    = screenH - (int)MathF.Floor(_reqMin.Y * k) + margin;

        // Snap to the deepest level's grid so every level's sub-rect is exact.
        var x0 = Math.Clamp(left & ~(Snap - 1), 0, _texW);
        var y0 = Math.Clamp(bottom & ~(Snap - 1), 0, _texH);
        var x1 = Math.Clamp((right + Snap - 1) & ~(Snap - 1), 0, _texW);
        var y1 = Math.Clamp((top + Snap - 1) & ~(Snap - 1), 0, _texH);
        if (x1 <= x0 || y1 <= y0) return false;

        // Part of the region that actually lies on screen (even, for the 2:1 capture).
        var vx1 = Math.Min(x1, screenW) & ~1;
        var vy1 = Math.Min(y1, screenH) & ~1;
        if (vx1 <= x0 || vy1 <= y0) return false;

        glDisable(GL_SCISSOR_TEST);
        glDisable(GL_BLEND);
        glDisable(GL_DEPTH_TEST);
        glDisable(GL_STENCIL_TEST);
        glDisable(GL_CULL_FACE);

        // Capture into level 1 at half size. A multisampled source must be resolved 1:1 first.
        glBindFramebuffer(GL_READ_FRAMEBUFFER, (uint)drawFbo);
        if (msaa)
        {
            glBindFramebuffer(GL_DRAW_FRAMEBUFFER, _fbo[0]);
            glBlitFramebuffer(x0, y0, vx1, vy1, x0, y0, vx1, vy1, GL_COLOR_BUFFER_BIT, GL_NEAREST);
            glBindFramebuffer(GL_READ_FRAMEBUFFER, _fbo[0]);
        }
        glBindFramebuffer(GL_DRAW_FRAMEBUFFER, _fbo[1]);
        glBlitFramebuffer(x0, y0, vx1, vy1, x0 / 2, y0 / 2, vx1 / 2, vy1 / 2, GL_COLOR_BUFFER_BIT, GL_LINEAR);

        // Region and valid area in normalised coords: identical at every level (sizes divide exactly).
        float rx = (float)x0 / _texW, ry = (float)y0 / _texH;
        float rw = (float)(x1 - x0) / _texW, rh = (float)(y1 - y0) / _texH;
        float cx0 = rx, cy0 = ry, cx1 = (float)vx1 / _texW, cy1 = (float)vy1 / _texH;

        GlBackend.BindVertexArray(_vao);
        GlBackend.ActiveTexture(GL_TEXTURE0);

        GlBackend.UseProgram(_progDown);
        GlBackend.Uniform1i(_locDown[0], 0);
        glUniform4f(_locDown[1], rx, ry, rw, rh);
        glUniform1f(_locDown[4], offset);
        for (int i = 1; i < levels; i++)
            Pass(_locDown, i, i + 1, x0, y0, x1, y1, cx0, cy0, cx1, cy1);

        GlBackend.UseProgram(_progUp);
        GlBackend.Uniform1i(_locUp[0], 0);
        glUniform4f(_locUp[1], rx, ry, rw, rh);
        glUniform1f(_locUp[4], offset);
        for (int i = levels; i > 1; i--)
            Pass(_locUp, i, i - 1, x0, y0, x1, y1, cx0, cy0, cx1, cy1);

        if (_checkFrames > 0)
        {
            _checkFrames--;
            var err = glGetError();
            if (err != 0)
            {
                DrainErrors();
                return Strike($"GL error 0x{err:X4} during {(msaa ? "blur (msaa resolve)" : "blur")}", screenW, screenH);
            }
            if (_checkFrames == 0) _strikes = 0;
        }

        _broken = false;
        return true;
    }

    // A check after (re)allocation failed. Reallocate and try again next frame (drawing just the
    // tint meanwhile); only a failure that keeps coming back disables the effect.
    private static bool Strike(string reason, int screenW, int screenH)
    {
        if (++_strikes >= MaxStrikes)
            throw new InvalidOperationException($"{reason} at {screenW}x{screenH}, {_strikes} attempts");

        MarseyLogger.Info($"[GlassBlur] {reason} at {screenW}x{screenH}, retrying ({_strikes}/{MaxStrikes})");
        _scrW   = 0;
        _broken = true;
        return false;
    }

    // Clears the level 1 texture to transparent, so a frame that wasn't blurred shows just the tint.
    private static void Invalidate()
    {
        if (_fbo[1] == 0 || _texW == 0) return;
        try
        {
            glGetFloatv(GL_COLOR_CLEAR_VALUE, _clearColor);
            glBindFramebuffer(GL_DRAW_FRAMEBUFFER, _fbo[1]);
            glDisable(GL_SCISSOR_TEST);
            glClearColor(0f, 0f, 0f, 0f);
            glClear(GL_COLOR_BUFFER_BIT);
            glClearColor(_clearColor[0], _clearColor[1], _clearColor[2], _clearColor[3]);
        }
        catch { /* best effort; never throw out of Run */ }
    }

    private static void Pass(int[] loc, int src, int dst, int x0, int y0, int x1, int y1,
        float cx0, float cy0, float cx1, float cy1)
    {
        glBindFramebuffer(GL_DRAW_FRAMEBUFFER, _fbo[dst]);
        glViewport(x0 >> dst, y0 >> dst, (x1 - x0) >> dst, (y1 - y0) >> dst);
        GlBackend.BindTexture(GL_TEXTURE_2D, _tex[src]);

        var tx = 1f / (_texW >> src);
        var ty = 1f / (_texH >> src);
        glUniform2f(loc[2], tx, ty);

        // Keep taps inside the captured area (half a texel in), so nothing stale bleeds in.
        float minU = cx0 + 0.5f * tx, maxU = cx1 - 0.5f * tx;
        float minV = cy0 + 0.5f * ty, maxV = cy1 - 0.5f * ty;
        if (maxU < minU) minU = maxU = (cx0 + cx1) * 0.5f;
        if (maxV < minV) minV = maxV = (cy0 + cy1) * 0.5f;
        glUniform4f(loc[3], minU, minV, maxU, maxV);

        glDrawArrays(GL_TRIANGLES, 0, 3);
    }

    // --- failure / cleanup -------------------------------------------------------------------

    // True if any error was pending (and clears them all).
    private static bool DrainErrors()
    {
        bool any = false;
        for (int i = 0; i < 16 && glGetError() != 0; i++)
            any = true;
        return any;
    }

    private static void CheckErrors(string where)
    {
        var err = glGetError();
        if (err == 0) return;
        DrainErrors();
        throw new InvalidOperationException($"GL error 0x{err:X4} during {where}");
    }

    private static void Fail(string reason, bool deferFree)
    {
        if (_failed) return;
        _failed = true;

        string version = "?", renderer = "?";
        try
        {
            version  = GlBackend.GetString(GL_VERSION);
            renderer = GlBackend.GetString(GL_RENDERER);
        }
        catch { /* best effort */ }

        MarseyLogger.Warn($"[GlassBlur] disabled: {reason} ({version}, {renderer}, " +
                          $"samples {_sampleBuffers}/{_samples}, encoding {EncodingName(_encoding)})");

        if (deferFree)
            _releasePending = true;
        else
            FreeAll();
    }

    private static void FreeAll()
    {
        _ready = false;
        if (!_loaded) return;

        for (int i = 0; i <= MaxLevels; i++)
        {
            if (_fbo[i] != 0) { var f = _fbo[i]; glDeleteFramebuffers(1, ref f); _fbo[i] = 0; }
            if (_tex[i] != 0) { GlBackend.DeleteTexture(_tex[i]); _tex[i] = 0; }
        }
        if (_progDown != 0) { glDeleteProgram(_progDown); _progDown = 0; }
        if (_progUp   != 0) { glDeleteProgram(_progUp);   _progUp   = 0; }
        if (_vao      != 0) { var v = _vao; glDeleteVertexArrays(1, ref v); _vao = 0; }

        _texW = _texH = _scrW = _scrH = 0;
        _checkFrames = _strikes = 0;
        _broken = false;
    }

    // --- shaders -----------------------------------------------------------------------------

    // Fullscreen triangle from gl_VertexID (empty VAO); the viewport selects the level's sub-rect and
    // u_rect maps it to the same region in the source texture.
    private const string VertSrc = @"
    uniform vec4 u_rect;
    out vec2 v_uv;
    void main() {
        vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
        v_uv = u_rect.xy + p * u_rect.zw;
        gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
    }";

    // Dual Kawase downsample: centre x4 + four diagonal taps. Alpha forced to 1 (backbuffer alpha is garbage).
    private const string FragDownSrc = @"
    uniform sampler2D u_tex;
    uniform vec2 u_texel;
    uniform vec4 u_clamp;
    uniform float u_offset;
    in vec2 v_uv;
    out vec4 o_color;
    vec3 tap(vec2 uv) { return texture(u_tex, clamp(uv, u_clamp.xy, u_clamp.zw)).rgb; }
    void main() {
        vec2 d = u_texel * u_offset;
        vec3 c = tap(v_uv) * 4.0;
        c += tap(v_uv - d);
        c += tap(v_uv + d);
        c += tap(v_uv + vec2(d.x, -d.y));
        c += tap(v_uv - vec2(d.x, -d.y));
        o_color = vec4(c * 0.125, 1.0);
    }";

    // Dual Kawase upsample: four edge taps x1 + four diagonal taps x2.
    private const string FragUpSrc = @"
    uniform sampler2D u_tex;
    uniform vec2 u_texel;
    uniform vec4 u_clamp;
    uniform float u_offset;
    in vec2 v_uv;
    out vec4 o_color;
    vec3 tap(vec2 uv) { return texture(u_tex, clamp(uv, u_clamp.xy, u_clamp.zw)).rgb; }
    void main() {
        vec2 h = u_texel * 0.5 * u_offset;
        vec3 c = tap(v_uv + vec2(-h.x * 2.0, 0.0));
        c += tap(v_uv + vec2(-h.x, h.y)) * 2.0;
        c += tap(v_uv + vec2(0.0, h.y * 2.0));
        c += tap(v_uv + vec2(h.x, h.y)) * 2.0;
        c += tap(v_uv + vec2(h.x * 2.0, 0.0));
        c += tap(v_uv + vec2(h.x, -h.y)) * 2.0;
        c += tap(v_uv + vec2(0.0, -h.y * 2.0));
        c += tap(v_uv + vec2(-h.x, -h.y)) * 2.0;
        o_color = vec4(c / 12.0, 1.0);
    }";
}
