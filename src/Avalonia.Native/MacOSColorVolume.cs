using Avalonia.Metal;
using Avalonia.Native.Interop;
using Avalonia.Platform;

namespace Avalonia.Native;

internal static class MacOSColorVolume
{
    public static PlatformSurfaceColorFormat GetColorFormat(AvnPixelFormat format) =>
        MetalColorVolume.GetColorFormat(format == AvnPixelFormat.kAvnRgbaF16);

    public static PlatformSurfaceColorVolume FromNative(AvnSurfaceColorInfo info) =>
        MetalColorVolume.Create(info.PixelFormat == AvnPixelFormat.kAvnRgbaF16,
            info.Headroom, info.MaximumHeadroom);
}