using System;
using Avalonia.Platform;

namespace Avalonia.OpenGL.Surfaces
{
    public interface IGlPlatformSurfaceRenderingSession : IDisposable
    {
        IGlContext Context { get; }
        PixelSize Size { get; }
        double Scaling { get; }
        bool IsYFlipped { get; }

        /// <summary>
        /// The pixel encoding and color space of the surface being rendered into.
        /// </summary>
        PlatformSurfaceColorFormat ColorFormat => default;

        /// <summary>
        /// The color volume the platform preferred for this surface when the session began, or null
        /// when the platform can't report one.
        /// </summary>
        PlatformSurfaceColorVolume? PreferredColorVolume => null;
    }
}
