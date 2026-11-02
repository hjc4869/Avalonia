using Avalonia.Metal;
using Avalonia.Platform;
using CoreAnimation;
using CoreGraphics;

namespace Avalonia.iOS.Metal;

internal class MetalRenderTarget : IMetalPlatformSurfaceRenderTarget
{
    private readonly CAMetalLayer _layer;
    private readonly MetalDevice _device;
    private readonly MetalPlatformSurface _surface;
    private (PixelSize size, double scaling) _lastLayout;

    public MetalRenderTarget(CAMetalLayer layer, MetalDevice device, MetalPlatformSurface surface)
    {
        _layer = layer;
        _device = device;
        _surface = surface;
    }

    public (PixelSize size, double scaling) PendingLayout { get; set; } = (new PixelSize(1, 1), 1);

    public PlatformSurfaceColorFormat ColorFormat => _surface.ColorFormat;

    public void Dispose()
    {
    }

    public IMetalPlatformSurfaceRenderingSession BeginRendering()
    {
        var (size, scaling) = PendingLayout;
        if (_lastLayout != (size, scaling))
        {
            _lastLayout = (size, scaling);
            _layer.DrawableSize = new CGSize(size.Width, size.Height);
        }

        var colorVolume = _surface.PreferredColorVolume;
        var drawable = _layer.NextDrawable() ?? throw new PlatformGraphicsContextLostException();
        return new MetalDrawingSession(_device, drawable, size, scaling, ColorFormat, colorVolume);
    }
}
