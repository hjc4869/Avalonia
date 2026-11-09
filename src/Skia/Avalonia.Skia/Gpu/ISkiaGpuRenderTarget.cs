using System;
using Avalonia.Platform;

namespace Avalonia.Skia
{
    /// <summary>
    /// Custom Skia render target.
    /// </summary>
    public interface ISkiaGpuRenderTarget : IDisposable
    {
        /// <summary>
        /// Start rendering to this render target.
        /// </summary>
        /// <param name="sceneInfo">Information about the scene that will be rendered.</param>
        /// <returns>A render session instance.</returns>
        ISkiaGpuRenderSession BeginRenderingSession(IRenderTarget.RenderTargetSceneInfo sceneInfo);
        
        PlatformRenderTargetState State => PlatformRenderTargetState.Ready;

        /// <summary>
        /// The pixel encoding and color space sessions created from this target render into.
        /// </summary>
        PlatformSurfaceColorFormat ColorFormat => default;
    }

    /// <summary>
    /// Implemented by GPU render targets whose sessions may draw into the presented surface itself, with everything
    /// Skia needs available, but which don't keep the previous frame, e.g. Metal drawables.
    /// The compositor redraws such targets directly when most of a frame changes, skipping its intermediate layer.
    /// </summary>
    internal interface ISkiaGpuRenderTargetWithDirectRendering : ISkiaGpuRenderTarget
    {
        bool CanDrawDirectly { get; }
    }
}
