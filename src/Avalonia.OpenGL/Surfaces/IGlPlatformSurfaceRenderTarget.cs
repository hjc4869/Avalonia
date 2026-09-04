using System;
using Avalonia.Metadata;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;

namespace Avalonia.OpenGL.Surfaces
{
    [PrivateApi]
    public interface IGlPlatformSurfaceRenderTarget : IDisposable, IPlatformRenderSurfaceRenderTarget
    {
        IGlPlatformSurfaceRenderingSession BeginDraw(IRenderTarget.RenderTargetSceneInfo sceneInfo);

        /// <summary>
        /// The pixel encoding and color space every session created from this target will use.
        /// </summary>
        PlatformSurfaceColorFormat ColorFormat => default;
    }
}
