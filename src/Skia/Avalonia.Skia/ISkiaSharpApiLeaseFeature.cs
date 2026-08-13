using System;
using Avalonia.Metadata;
using Avalonia.Platform;
using SkiaSharp;

namespace Avalonia.Skia;

[Unstable]
public interface ISkiaSharpApiLeaseFeature
{
    public ISkiaSharpApiLease Lease();
}

[Unstable]
public interface ISkiaSharpApiLease : IDisposable
{
    SKCanvas SkCanvas { get; }
    GRContext? GrContext { get; }
    SKSurface? SkSurface { get; }
    double CurrentOpacity { get; }
    ISkiaSharpPlatformGraphicsApiLease? TryLeasePlatformGraphicsApi();

    /// <summary>
    /// The pixel encoding and color space of the surface being drawn into. Use this to decide whether
    /// wide gamut or extended range colors can be emitted, and to tone map when they can't.
    /// </summary>
    PlatformSurfaceColorFormat ColorFormat => default;

    /// <summary>
    /// The Skia color space of the surface being drawn into, or null when the surface isn't color managed.
    /// </summary>
    SKColorSpace? SkColorSpace => null;
}

[Unstable]
public interface ISkiaSharpPlatformGraphicsApiLease : IDisposable
{
    IPlatformGraphicsContext Context { get; }
}
