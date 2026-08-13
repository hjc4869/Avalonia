using System;
using Avalonia.Platform;
using SkiaSharp;

namespace Avalonia.Skia
{
    /// <summary>
    /// Custom render session for Skia render target.
    /// </summary>
    public interface ISkiaGpuRenderSession : IDisposable
    {
        /// <summary>
        /// GrContext used by this session.
        /// </summary>
        GRContext GrContext { get; }

        /// <summary>
        /// Canvas that will be used to render.
        /// </summary>
        SKSurface SkSurface { get; }

        /// <summary>
        /// Scaling factor.
        /// </summary>
        double ScaleFactor { get; }
        
        GRSurfaceOrigin SurfaceOrigin { get; }

        /// <summary>
        /// The pixel encoding and color space <see cref="SkSurface"/> was created with.
        /// </summary>
        PlatformSurfaceColorFormat ColorFormat => default;
    }
}
