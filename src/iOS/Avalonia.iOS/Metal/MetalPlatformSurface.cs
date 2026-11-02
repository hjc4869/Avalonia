using System;
using System.Threading;
using Avalonia.Metal;
using Avalonia.Platform;
using Avalonia.Threading;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using Metal;
using UIKit;

namespace Avalonia.iOS.Metal;

internal class MetalPlatformSurface : IMetalPlatformSurface, IPlatformSurfaceColorVolumeFeature,
    IPlatformHdrContentFeature, IDisposable
{
    private readonly CAMetalLayer _layer;
    private readonly AvaloniaView _avaloniaView;
    private readonly bool _extended;
    private ColorVolumeState _colorVolumeState;
    private CADisplayLink? _displayLink;
    private NSObject? _didBecomeActiveObserver;
    private NSObject? _willResignActiveObserver;
    private bool _hasHdrContent;
    private bool _disposed;

    public MetalPlatformSurface(CAMetalLayer layer, AvaloniaView avaloniaView)
    {
        _layer = layer;
        _avaloniaView = avaloniaView;

        var extended = false;
#if !TVOS
        extended = Platform.Options?.ColorMode == iOSColorMode.ExtendedLinear &&
            (OperatingSystem.IsIOSVersionAtLeast(16) || OperatingSystem.IsMacCatalystVersionAtLeast(16));
#endif
        using var extendedColorSpace = extended
            ? CGColorSpace.CreateWithName(CGColorSpaceNames.ExtendedLinearSrgb)
            : null;
        _extended = extendedColorSpace is not null;
        using var standardColorSpace = _extended ? null : CGColorSpace.CreateSrgb();
        _layer.ColorSpace = extendedColorSpace ?? standardColorSpace;
        _layer.PixelFormat = _extended ? MTLPixelFormat.RGBA16Float : MTLPixelFormat.BGRA8Unorm;
        _layer.FramebufferOnly = false;
#if !TVOS
        if (_extended)
        {
            _layer.WantsExtendedDynamicRangeContent = false;
            _layer.EdrMetadata = null;
        }
#endif
        ColorFormat = MetalColorVolume.GetColorFormat(_extended);
        _colorVolumeState = new ColorVolumeState(MetalColorVolume.Create(_extended));
    }

    public PlatformSurfaceColorFormat ColorFormat { get; }

    public PlatformSurfaceColorVolume? PreferredColorVolume => Volatile.Read(ref _colorVolumeState).Value;

    public PlatformHdrContentMetadata? HdrContentMetadata { get; private set; }

    public event EventHandler? PreferredColorVolumeChanged;

    public IMetalPlatformSurfaceRenderTarget CreateMetalRenderTarget(IMetalDevice device)
    {
        var dev = (MetalDevice)device;
        _layer.Device = dev.Device;

        var target = new MetalRenderTarget(_layer, dev, this);
        _avaloniaView.SetRenderTarget(target);
        return target;
    }

    public void SetHdrContent(bool hasHdrContent, PlatformHdrContentMetadata? metadata)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
            return;

        metadata = PlatformHdrContentMetadata.Normalize(hasHdrContent, metadata);
        if (_hasHdrContent == hasHdrContent && HdrContentMetadata == metadata)
            return;

        _hasHdrContent = hasHdrContent;
        HdrContentMetadata = metadata;
#if !TVOS
        if (_extended)
            _layer.WantsExtendedDynamicRangeContent = hasHdrContent;
#endif
        RefreshColorVolume();
    }

    public void UpdateScreen()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
            return;

        if (_extended && _avaloniaView.Window is not null)
        {
            _didBecomeActiveObserver ??= UIApplication.Notifications.ObserveDidBecomeActive(
                (_, _) => StartDisplayLink());
            _willResignActiveObserver ??= UIApplication.Notifications.ObserveWillResignActive(
                (_, _) => StopDisplayLink());
            if (UIApplication.SharedApplication.ApplicationState == UIApplicationState.Active)
                StartDisplayLink();
        }
        else
            StopObserving();

        RefreshColorVolume();
    }

    public void RefreshColorVolume()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed)
            return;

        double? headroom = null;
        double? maximumHeadroom = null;
#if !TVOS
        if (_extended && (_avaloniaView.Window?.WindowScene?.Screen ?? _avaloniaView.Window?.Screen) is { } screen)
        {
            headroom = _hasHdrContent ? (double)screen.CurrentEdrHeadroom : 1;
            maximumHeadroom = (double)screen.PotentialEdrHeadroom;
        }
#endif
        var volume = MetalColorVolume.Create(_extended, headroom, maximumHeadroom);
        if (PreferredColorVolume == volume)
            return;

        Volatile.Write(ref _colorVolumeState, new ColorVolumeState(volume));
        PreferredColorVolumeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StartDisplayLink()
    {
        if (_disposed || _avaloniaView.Window is null)
            return;

        if (_displayLink is null)
        {
            _displayLink = CADisplayLink.Create(RefreshColorVolume);
            _displayLink.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Common);
        }
        RefreshColorVolume();
    }

    private void StopDisplayLink()
    {
        _displayLink?.Invalidate();
        _displayLink?.Dispose();
        _displayLink = null;
    }

    private void StopObserving()
    {
        StopDisplayLink();
        _didBecomeActiveObserver?.Dispose();
        _didBecomeActiveObserver = null;
        _willResignActiveObserver?.Dispose();
        _willResignActiveObserver = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        StopObserving();
        PreferredColorVolumeChanged = null;
#if !TVOS
        if (_extended)
            _layer.WantsExtendedDynamicRangeContent = false;
#endif
    }

    private sealed class ColorVolumeState(PlatformSurfaceColorVolume value)
    {
        public PlatformSurfaceColorVolume Value { get; } = value;
    }
}
