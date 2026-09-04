using System;

using Avalonia.Metadata;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Surfaces;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;

namespace Avalonia.Win32.DirectX;

public interface IDirect3D11TexturePlatformSurface : IPlatformRenderSurface
{
    public IDirect3D11TextureRenderTarget CreateRenderTarget(IPlatformGraphicsContext graphicsContext, IntPtr d3dDevice);
}

[PrivateApi]
public interface IDirect3D11TexturePlatformSurface2 : IPlatformRenderSurface
{
    IDirect3D11TextureRenderTarget2 CreateRenderTarget(IPlatformGraphicsContext graphicsContext, IntPtr d3dDevice);
}


public interface IDirect3D11TextureRenderTarget : IPlatformRenderSurfaceRenderTarget, IDisposable
{
    IDirect3D11TextureRenderTargetRenderSession BeginDraw();
}

[PrivateApi]
public interface IDirect3D11TextureRenderTarget2 : IPlatformRenderSurfaceRenderTarget, IDisposable
{
    IDirect3D11TextureRenderTargetRenderSession BeginDraw(IRenderTarget.RenderTargetSceneInfo sceneInfo);

    /// <summary>
    /// The pixel encoding and color space the textures handed out by this target are in.
    /// </summary>
    PlatformSurfaceColorFormat ColorFormat => default;

    /// <summary>
    /// The color volume the platform currently prefers for this target. Render sessions snapshot
    /// this value when they begin.
    /// </summary>
    PlatformSurfaceColorVolume? PreferredColorVolume => null;
}

public interface IDirect3D11TextureRenderTargetRenderSession : IDisposable
{
    public IntPtr D3D11Texture2D { get; }
    public PixelSize Size { get; }
    public PixelPoint Offset { get; }
    public double Scaling { get; }
}
