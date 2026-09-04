using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Logging;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Avalonia.Wayland.Server.Interop;
using Avalonia.Wayland.Server.Persistent;

namespace Avalonia.Wayland.Server.Transient.Rendering;

/// <summary>
/// EGL platform-graphics backend that uses the standard Wayland WSI path:
/// <c>EGL_PLATFORM_WAYLAND_KHR</c> over <c>wl_display*</c>, with per-surface
/// <c>wl_egl_window</c> backing buffers. The driver owns buffer allocation,
/// modifier negotiation, presentation and the implicit <c>wl_surface.commit</c>
/// performed inside <c>eglSwapBuffers</c>.
///
/// This is the default backend; the dmabuf-based
/// <see cref="WaylandEglDmaBufPlatformGraphics"/> path is opt-in via
/// <c>WaylandPlatformOptions.UseDmabufSwapchain</c>.
/// </summary>
internal sealed class WaylandEglWsiPlatformGraphics : WaylandPlatformGraphics.IWaylandGraphics
{
    public EglDisplay Display { get; }

    /// <summary>
    /// The color format surfaces actually report. Starts out as whatever EGL negotiated, but is
    /// downgraded to unmanaged if the compositor ends up rejecting the matching image description:
    /// rendering wide gamut pixels into a surface the compositor reads as sRGB would shift every
    /// color on screen, while rendering sRGB into a higher precision buffer is always safe.
    /// </summary>
    public PlatformSurfaceColorFormat ColorFormat { get; private set; }

    public void DowngradeToUnmanagedColorSpace() =>
        ColorFormat = ColorFormat with { ColorSpace = PlatformColorSpace.Unmanaged };

    public IPlatformGraphicsContext CreateContext() => Display.CreateContext(null);

    public IPlatformRenderSurface CreateRenderSurface(WSurface surface) => new WaylandEglWsiSurface(surface, this);

    private WaylandEglWsiPlatformGraphics(EglDisplay display)
    {
        Display = display;
        ColorFormat = display.ColorFormat;
    }

    // libEGL.so.1 only exports EGL 1.5 core entry points. Extension entry
    // points like eglGetPlatformDisplayEXT are NOT exported as symbols and
    // can only be resolved via eglGetProcAddress; without this, EglInterface
    // would fail to bind it and EGL_PLATFORM_WAYLAND_KHR display creation
    // wouldn't work.
    [DllImport("libEGL.so.1", CharSet = CharSet.Ansi)]
    private static extern IntPtr eglGetProcAddress(string proc);

    private const int EGL_PLATFORM_WAYLAND_KHR = 0x31D8;

    public static WaylandEglWsiPlatformGraphics? TryCreate(WaylandConnection connection, IList<GlVersion> glProfiles,
        IReadOnlyList<EglColorBufferFormat>? colorBufferFormats = null)
    {
        try
        {
            var options = new EglDisplayCreationOptions
            {
                Egl = new EglInterface(eglGetProcAddress),
                PlatformType = EGL_PLATFORM_WAYLAND_KHR,
                PlatformDisplay = connection.Display.Handle,
                SupportsMultipleContexts = true,
                SupportsContextSharing = true,
                GlVersions = glProfiles,
                ColorBufferFormats = colorBufferFormats
            };
            var display = new EglDisplay(options);
            return new WaylandEglWsiPlatformGraphics(display);
        }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Error, "OpenGL")?.Log(null,
                "Unable to initialize Wayland WSI EGL rendering: {0}", e);
            return null;
        }
    }

    public void Dispose() => Display.Dispose();
}
