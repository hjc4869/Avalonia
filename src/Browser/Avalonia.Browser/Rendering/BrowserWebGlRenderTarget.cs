using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.JavaScript;
using System.Threading;
using Avalonia.Browser.Interop;
using Avalonia.Logging;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Surfaces;
using Avalonia.Platform;
using Avalonia.Reactive;

namespace Avalonia.Browser.Rendering;

partial class BrowserWebGlRenderTarget : BrowserRenderTarget, IGlPlatformSurface
{
    private readonly Func<(PixelSize Size, double Scaling)> _sizeGetter;
    private readonly GLInfo _glInfo;
    private readonly PlatformSurfaceColorFormat _colorFormat;
    public IGlContext GlContext { get; }

    [JSImport("WebGlRenderTarget.setColorSpace", AvaloniaModule.MainModuleName)]
    private static partial string SetJsColorSpace(JSObject target, string colorSpace);

    [JSImport("WebGlRenderTarget.tryUseFloat16", AvaloniaModule.MainModuleName)]
    private static partial bool TryUseJsFloat16(JSObject target);

    public BrowserWebGlRenderTarget(JSObject js, Func<(PixelSize, double)> sizeGetter) : base(js)
    {
        _sizeGetter = sizeGetter;
        _glInfo = new GLInfo(
            js.GetPropertyAsInt32("contextHandle")!,
            (uint)js.GetPropertyAsInt32("fboId"),
            js.GetPropertyAsInt32("stencil"),
            js.GetPropertyAsInt32("sample"),
            js.GetPropertyAsInt32("depth"));
        var contextId = js.GetPropertyAsInt32("contextHandle");
        var version = js.GetPropertyAsJSObject("attrs")!.GetPropertyAsInt32("majorVersion");
        GlContext = new WebGlContext(contextId, new GlVersion(GlProfileType.OpenGLES, version > 1 ? 3 : 2, 0),
            _glInfo.Samples, _glInfo.Stencils);
        _colorFormat = NegotiateColorFormat(js);
    }

    private static PlatformSurfaceColorFormat NegotiateColorFormat(JSObject js)
    {
        var mode = AvaloniaLocator.Current.GetService<BrowserPlatformOptions>()?.ColorMode
                   ?? BrowserColorMode.Standard;
        if (mode == BrowserColorMode.Standard)
            return PlatformSurfaceColorFormat.Unmanaged;

        // Extended sRGB needs nothing but a float drawing buffer: the sRGB color space is left as is,
        // because it is the lack of clamping in a float buffer that makes values outside [0, 1]
        // meaningful. Out of gamut colors then ride on negative channel values.
        if (mode == BrowserColorMode.ExtendedSrgb)
        {
            if (TryUseFloat16(js))
                return new PlatformSurfaceColorFormat(PlatformPixelEncoding.RgbaF16,
                    PlatformColorSpace.ExtendedSrgb);

            Logger.TryGet(LogEventLevel.Information, LogArea.BrowserPlatform)
                ?.Log(null, "No float drawing buffer available, falling back to a wide gamut surface");
        }

        string result;
        try
        {
            result = SetJsColorSpace(js, "display-p3");
        }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Warning, LogArea.BrowserPlatform)
                ?.Log(null, "Unable to configure a wide gamut drawing buffer: {Error}", e.Message);
            return PlatformSurfaceColorFormat.Unmanaged;
        }

        // The browser is free to ignore the request, in which case rendering must stay unmanaged so
        // that Skia doesn't color convert into a space the drawing buffer isn't actually in.
        if (result != "display-p3")
        {
            Logger.TryGet(LogEventLevel.Information, LogArea.BrowserPlatform)
                ?.Log(null, "Wide gamut was requested but the drawing buffer stayed '{ColorSpace}'", result);
            return PlatformSurfaceColorFormat.Unmanaged;
        }

        // A wide gamut spread over 8 bits per channel bands noticeably on a high bit depth display,
        // so upgrade to 16 bit float when the browser allows it. This is purely a quality
        // improvement: an 8 bit Display P3 buffer is still correct. Note that display-p3 is a bounded
        // color space, so unlike ExtendedSrgb this still clamps at the SDR white level.
        var encoding = TryUseFloat16(js) ? PlatformPixelEncoding.RgbaF16 : PlatformPixelEncoding.Default;
        return new PlatformSurfaceColorFormat(encoding, PlatformColorSpace.DisplayP3Srgb);
    }

    private static bool TryUseFloat16(JSObject js)
    {
        try
        {
            return TryUseJsFloat16(js);
        }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Verbose, LogArea.BrowserPlatform)
                ?.Log(null, "Unable to use a 16 bit float drawing buffer: {Error}", e.Message);
            return false;
        }
    }

    class GlSession : IGlPlatformSurfaceRenderingSession
    {
        private IDisposable? _restoreContext;

        public GlSession(IGlContext context, PixelSize size, double scaling, IDisposable restoreContext,
            PlatformSurfaceColorFormat colorFormat)
        {
            _restoreContext = restoreContext;
            Context = context;
            Size = size;
            Scaling = scaling;
            ColorFormat = colorFormat;
        }

        public void Dispose()
        {
            _restoreContext?.Dispose();
            _restoreContext = null;
        }

        public IGlContext Context { get; }
        public PixelSize Size { get; }
        // This should technically be delivered via CompositionTarget.Scaling anyway, why do we still have this property
        public double Scaling { get; }
        public bool IsYFlipped => false;
        public PlatformSurfaceColorFormat ColorFormat { get; }
    }
    
    class GlSurface : IGlPlatformSurfaceRenderTarget
    {
        private readonly BrowserWebGlRenderTarget _target;

        public GlSurface(BrowserWebGlRenderTarget target)
        {
            _target = target;
        }

        public bool IsCorrupted => false;

        public PlatformSurfaceColorFormat ColorFormat => _target._colorFormat;

        public void Dispose()
        {
            // No-op
        }

        public IGlPlatformSurfaceRenderingSession BeginDraw(IRenderTarget.RenderTargetSceneInfo sceneInfo)
        {
            var s = _target._sizeGetter();
            _target.UpdateSize(s.Size);
            var restoreContext = _target.GlContext.EnsureCurrent();
            _target.GlContext.GlInterface.BindFramebuffer(GlConsts.GL_FRAMEBUFFER, (int)_target._glInfo.FboId);
            return new GlSession(_target.GlContext, s.Size, s.Scaling, restoreContext, _target._colorFormat);
        }
    }

    public override IPlatformGraphicsContext? PlatformGraphicsContext => GlContext;
    public IGlPlatformSurfaceRenderTarget CreateGlRenderTarget(IGlContext context)
    {
        return new GlSurface(this);
    }
}

partial class WebGlContext : IGlContext, Avalonia.Skia.IGlSkiaSpecificOptionsFeature
{
    [JSImport("WebGlRenderTarget.getCurrentContext", AvaloniaModule.MainModuleName)]
    private static partial int GetCurrentContext();

    [JSImport("WebGlRenderTarget.makeContextCurrent", AvaloniaModule.MainModuleName)]
    private static partial bool MakeContextCurrent(int context);

    [LibraryImport("libSkiaSharp", EntryPoint = "eglGetProcAddress", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr eglGetProcAddress(string name);

    private int _contextId;
    private readonly Thread _thread;

    public WebGlContext(int contextId, GlVersion version, int sampleCount, int stencilSize)
    {
        Version = version;
        SampleCount = sampleCount;
        StencilSize = stencilSize;
        _contextId = contextId;
        _thread = Thread.CurrentThread;

        using (MakeCurrent())
            GlInterface = new GlInterface(version, eglGetProcAddress);
    }

    void VerifyAccess()
    {
        if (_thread != Thread.CurrentThread)
            throw new InvalidOperationException("Call from invalid thread");
    }
    
    public IDisposable EnsureCurrent()
    {
        VerifyAccess();
        if(GetCurrentContext() == _contextId)
            return Disposable.Empty;
        return MakeCurrent();
    }

    class RestoreContext : IDisposable
    {
        private int? _contextId;

        public RestoreContext(int contextId)
        {
            _contextId = contextId;
        }

        public void Dispose()
        {
            if (_contextId != null)
                MakeContextCurrent(_contextId.Value);
            _contextId = null;
        }
    }
    
    public IDisposable MakeCurrent()
    {
        VerifyAccess();
        var old = GetCurrentContext();
        if (!MakeContextCurrent(_contextId))
            throw new OpenGlException("Unable to make the context current");
        return new RestoreContext(old);
    }
    
    public void Dispose()
    {
        // No-op, destroyed with the render target
    }

    public object? TryGetFeature(Type featureType) => null;

    // TODO: Implement
    public bool IsLost => false;
    public GlVersion Version { get; }
    public GlInterface GlInterface { get; }
    public int SampleCount { get; }
    public int StencilSize { get; }


    public bool IsSharedWith(IGlContext context) => false;

    public bool CanCreateSharedContext => false;

    public IGlContext? CreateSharedContext(IEnumerable<GlVersion>? preferredVersions = null) =>
        throw new NotSupportedException();

    public bool UseNativeSkiaGrGlInterface => true;
}
