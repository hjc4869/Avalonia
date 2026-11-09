using System;
using Avalonia.Metadata;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;

namespace Avalonia.Metal;


[PrivateApi]
public interface IMetalDevice : IPlatformGraphicsContext
{
    IntPtr Device { get; }
    IntPtr CommandQueue { get; }
}

[PrivateApi]
public interface IMetalPlatformSurface : IPlatformRenderSurface
{
    IMetalPlatformSurfaceRenderTarget CreateMetalRenderTarget(IMetalDevice device);
}

[PrivateApi]
public interface IMetalPlatformSurfaceRenderTarget : IDisposable, IPlatformRenderSurfaceRenderTarget
{
    IMetalPlatformSurfaceRenderingSession BeginRendering();

    /// <summary>The actual negotiated color format of the render target.</summary>
    PlatformSurfaceColorFormat ColorFormat => default;

    /// <summary>
    /// Indicates that the session textures can take any drawing, i.e. they aren't framebuffer-only, so frames that
    /// change almost completely can be drawn into them directly instead of being copied from an intermediate layer.
    /// </summary>
    bool CanDrawDirectly => false;
}

[PrivateApi]
public interface IMetalPlatformSurfaceRenderingSession : IDisposable
{
    IntPtr Texture { get; }
    PixelSize Size { get; }
    double Scaling { get; }
    bool IsYFlipped { get; }

    /// <summary>The actual pixel encoding and color space of the drawable.</summary>
    PlatformSurfaceColorFormat ColorFormat => default;

    /// <summary>The color-volume snapshot captured when this session began.</summary>
    PlatformSurfaceColorVolume? PreferredColorVolume => null;
}
