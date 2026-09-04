using System;
using System.Collections.Generic;

namespace Avalonia.OpenGL.Egl;

/// <summary>
/// Given every EGL config matched by eglChooseConfig, returns the one that should be used, or null if none
/// are usable. This lets platforms filter out broken configs that can't be distinguished by their attributes
/// (e.g. nvidia exposes duplicate, partially broken configs) and impose their own preference order between
/// usable ones (e.g. preferring a transparent/32-bit X11 visual, which mesa lists after the opaque ones).
/// </summary>
public delegate IntPtr? EglConfigProbeCallback(EglInterface egl, IntPtr display, IntPtr[] configs);

public class EglDisplayOptions
{
    public EglInterface? Egl { get; set; }
    public bool SupportsContextSharing { get; set; }
    public bool SupportsMultipleContexts { get; set; }
    public bool ContextLossIsDisplayLoss { get; set; }
    public Func<bool>? DeviceLostCheckCallback { get; set; }
    public Action? DisposeCallback { get; set; }
    public IEnumerable<GlVersion>? GlVersions { get; set; }
    public EglConfigProbeCallback? ProbeConfig { get; set; }

    /// <summary>
    /// Candidate color buffer layouts, in priority order. The first one the driver can satisfy is used,
    /// so callers requesting a wide gamut or high bit depth config should always keep
    /// <see cref="EglColorBufferFormat.Standard"/> last as a fallback.
    /// When null, only the standard 8 bit non color managed config is considered.
    /// </summary>
    public IReadOnlyList<EglColorBufferFormat>? ColorBufferFormats { get; set; }

    /// <summary>
    /// Whether managed color formats must be supported by EGL window-surface color-space extensions
    /// and should be passed to <c>eglCreateWindowSurface</c>.
    /// </summary>
    public bool UseEglWindowSurfaceColorSpace { get; set; }
}

public class EglContextOptions
{
    public EglContext? ShareWith { get; set; }
    public EglSurface? OffscreenSurface { get; set; }
    public Action? DisposeCallback { get; set; }
    public Dictionary<Type, Func<EglContext, object>>? ExtraFeatures { get; set; }
}

public class EglDisplayCreationOptions : EglDisplayOptions
{
    public int? PlatformType { get; set; }
    public IntPtr PlatformDisplay { get; set; }
    public int[]? PlatformDisplayAttrs { get; set; }
}
