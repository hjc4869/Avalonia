using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Platform;
using static Avalonia.OpenGL.Egl.EglConsts;
namespace Avalonia.OpenGL.Egl;

internal static class EglDisplayUtils
{
    internal const double ScRgbReferenceWhiteNits = 80;
    internal const double ScRgbMaximumNits = 10000;

    public static IntPtr CreateDisplay(EglDisplayCreationOptions options)
    {
        var egl = options.Egl ?? new EglInterface();
        var display = IntPtr.Zero;
        if (options.PlatformType == null)
        {
            if (display == IntPtr.Zero)
                display = egl.GetDisplay(IntPtr.Zero);
        }
        else
        {
            if (!egl.IsGetPlatformDisplayExtAvailable)
                throw new OpenGlException("eglGetPlatformDisplayEXT is not supported by libegl");

            display = egl.GetPlatformDisplayExt(options.PlatformType.Value, options.PlatformDisplay,
                options.PlatformDisplayAttrs);
        }

        if (display == IntPtr.Zero)
            throw OpenGlException.GetFormattedException("eglGetDisplay", egl);
        return display;
    }

    // Enumerates every config matching the attribute list and lets the probe callback pick one (or simply
    // takes the first one if no probe is supplied).
    private static IntPtr? ChooseConfigWithProbe(EglInterface egl, IntPtr display, int[] attribs,
        EglConfigProbeCallback? probe)
    {
        if (!egl.ChooseConfigs(display, attribs, null, 0, out var numConfigs) || numConfigs == 0)
            return null;

        var configs = new IntPtr[numConfigs];
        if (!egl.ChooseConfigs(display, attribs, configs, configs.Length, out numConfigs) || numConfigs == 0)
            return null;
        if (numConfigs != configs.Length)
            Array.Resize(ref configs, numConfigs);

        if (probe == null)
            return configs[0];

        return probe(egl, display, configs);
    }

    public static EglConfigInfo InitializeAndGetConfig(EglInterface egl, IntPtr display,
        IEnumerable<GlVersion>? versions, EglConfigProbeCallback? probeConfig = null,
        IReadOnlyList<EglColorBufferFormat>? colorBufferFormats = null,
        bool useEglWindowSurfaceColorSpace = false)
    {
        if (!egl.Initialize(display, out _, out _))
            throw OpenGlException.GetFormattedException("eglInitialize", egl);

        // TODO: AvaloniaLocator.Current.GetService<AngleOptions>()?.GlProfiles
        versions ??= new[]
        {
            new GlVersion(GlProfileType.OpenGLES, 3, 0),
            new GlVersion(GlProfileType.OpenGLES, 2, 0)
        };

        var cfgs = versions
            .Select(x =>
            {
                if (x.Type == GlProfileType.OpenGLES)
                {
                    var typeBit = EGL_OPENGL_ES3_BIT;

                    switch (x.Major)
                    {
                        case 2:
                            typeBit = EGL_OPENGL_ES2_BIT;
                            break;

                        case 1:
                            typeBit = EGL_OPENGL_ES_BIT;
                            break;
                    }

                    return new
                    {
                        Attributes = new[]
                        {
                            EGL_CONTEXT_MAJOR_VERSION, x.Major,
                            EGL_CONTEXT_MINOR_VERSION, x.Minor,
                            EGL_NONE
                        },
                        Api = EGL_OPENGL_ES_API,
                        RenderableTypeBit = typeBit,
                        Version = x
                    };
                }
                else
                {
                    var attrs = (x.Major > 3 || (x.Major == 3 && x.Minor >= 2))
                        ? new[]
                        {
                            EGL_CONTEXT_MAJOR_VERSION, x.Major,
                            EGL_CONTEXT_MINOR_VERSION, x.Minor,
                            EGL_CONTEXT_OPENGL_PROFILE_MASK,
                            x.IsCompatibilityProfile
                                ? EGL_CONTEXT_OPENGL_COMPATIBILITY_PROFILE_BIT
                                : EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT,
                            EGL_NONE
                        }
                        : new[]
                        {
                            EGL_CONTEXT_MAJOR_VERSION, x.Major,
                            EGL_CONTEXT_MINOR_VERSION, x.Minor,
                            EGL_NONE
                        };

                    return new
                    {
                        Attributes = attrs,
                        Api = EGL_OPENGL_API,
                        RenderableTypeBit = EGL_OPENGL_BIT,
                        Version = x
                    };
                }
            });

        var formats = colorBufferFormats is { Count: > 0 } ? colorBufferFormats : EglColorBufferFormat.StandardOnly;
        var extensions = egl.QueryString(display, EGL_EXTENSIONS);
        var supportsFloatFormats = HasExtension(extensions, "EGL_EXT_pixel_format_float");

        foreach (var cfg in cfgs)
        {
            if (!egl.BindApi(cfg.Api))
                continue;
            foreach (var format in formats)
            {
                if (format.FloatComponents && !supportsFloatFormats)
                    continue;
                if (useEglWindowSurfaceColorSpace && !SupportsWindowSurfaceColorSpace(format.ColorSpace, extensions))
                    continue;
                foreach (var surfaceType in new[] { EGL_PBUFFER_BIT | EGL_WINDOW_BIT, EGL_WINDOW_BIT })
                foreach (var stencilSize in new[] { 8, 1, 0 })
                foreach (var depthSize in new[] { 8, 1, 0 })
                {
                    var attribs = new List<int>
                    {
                        EGL_SURFACE_TYPE, surfaceType,
                        EGL_RENDERABLE_TYPE, cfg.RenderableTypeBit,
                        EGL_RED_SIZE, format.ColorBits,
                        EGL_GREEN_SIZE, format.ColorBits,
                        EGL_BLUE_SIZE, format.ColorBits,
                        EGL_ALPHA_SIZE, format.AlphaBits,
                        EGL_STENCIL_SIZE, stencilSize,
                        EGL_DEPTH_SIZE, depthSize
                    };
                    if (format.FloatComponents)
                    {
                        attribs.Add(EGL_COLOR_COMPONENT_TYPE_EXT);
                        attribs.Add(EGL_COLOR_COMPONENT_TYPE_FLOAT_EXT);
                    }

                    attribs.Add(EGL_NONE);

                    if (ChooseConfigWithProbe(egl, display, attribs.ToArray(), probeConfig) is not { } config)
                        continue;

                    egl.GetConfigAttrib(display, config, EGL_SAMPLES, out var sampleCount);
                    egl.GetConfigAttrib(display, config, EGL_STENCIL_SIZE, out var returnedStencilSize);
                    return new EglConfigInfo(config, cfg.Version, surfaceType, cfg.Attributes, sampleCount,
                        returnedStencilSize, cfg.Api, format);
                }
            }
        }

        throw new OpenGlException("No suitable EGL config was found");
    }

    // EGL_EXT_gl_colorspace_scrgb_linear fixes 1.0 at 80 nits and its representable peak at 10000 nits.
    internal static PlatformSurfaceColorVolume? CreateScRgbColorVolume(
        double minimumNits,
        double maximumNits,
        double? currentHeadroomRatio,
        PlatformTransferFunction transfer,
        double transferExponent = 0)
    {
        if (!double.IsFinite(minimumNits) || !double.IsFinite(maximumNits) ||
            minimumNits < 0 || maximumNits <= 0)
        {
            return null;
        }

        maximumNits = Math.Min(maximumNits, ScRgbMaximumNits);
        if (currentHeadroomRatio is { } headroomRatio)
        {
            if (!double.IsFinite(headroomRatio) || headroomRatio < 1)
                return null;

            maximumNits = Math.Min(maximumNits, headroomRatio * ScRgbReferenceWhiteNits);
        }

        if (maximumNits < ScRgbReferenceWhiteNits || minimumNits > maximumNits ||
            transfer == PlatformTransferFunction.Power &&
            (!double.IsFinite(transferExponent) || transferExponent <= 0))
        {
            return null;
        }

        if (transfer != PlatformTransferFunction.Power)
            transferExponent = 0;

        return new PlatformSurfaceColorVolume(
            new PlatformLuminanceRange(0, ScRgbReferenceWhiteNits),
            ScRgbReferenceWhiteNits,
            new PlatformLuminanceRange(minimumNits, maximumNits),
            transfer,
            transferExponent,
            ScRgbReferenceWhiteNits);
    }

    internal static int[] GetWindowSurfaceAttributes(PlatformColorSpace colorSpace) => colorSpace switch
    {
        PlatformColorSpace.ScRgbLinear =>
            new[] { EGL_GL_COLORSPACE, EGL_GL_COLORSPACE_SCRGB_LINEAR_EXT, EGL_NONE },
        _ => new[] { EGL_NONE }
    };

    internal static bool SupportsWindowSurfaceColorSpace(PlatformColorSpace colorSpace, string? extensions) =>
        colorSpace switch
        {
            PlatformColorSpace.Unmanaged => true,
            PlatformColorSpace.ScRgbLinear =>
                HasExtension(extensions, "EGL_KHR_gl_colorspace") &&
                HasExtension(extensions, "EGL_EXT_gl_colorspace_scrgb_linear"),
            _ => false
        };

    private static bool HasExtension(string? extensions, string extension) =>
        extensions?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(extension, StringComparer.Ordinal) == true;

    
}

internal class EglConfigInfo
{
    public IntPtr Config { get; }
    public GlVersion Version { get; }
    public int SurfaceType { get; }
    public int[] Attributes { get; }
    public int SampleCount { get; }
    public int StencilSize { get; }
    public int Api { get; }
    public EglColorBufferFormat ColorBufferFormat { get; }

    public EglConfigInfo(IntPtr config, GlVersion version, int surfaceType, int[] attributes, int sampleCount,
        int stencilSize, int api, EglColorBufferFormat colorBufferFormat)
    {
        Config = config;
        Version = version;
        SurfaceType = surfaceType;
        Attributes = attributes;
        SampleCount = sampleCount;
        StencilSize = stencilSize;
        Api = api;
        ColorBufferFormat = colorBufferFormat;
    }
}
