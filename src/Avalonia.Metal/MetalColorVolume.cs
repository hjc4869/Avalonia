using Avalonia.Platform;

namespace Avalonia.Metal;

internal static class MetalColorVolume
{
    public static PlatformSurfaceColorFormat GetColorFormat(bool extended) => extended
        ? new(PlatformPixelEncoding.RgbaF16, PlatformColorSpace.ScRgbLinear)
        : new(PlatformPixelEncoding.Default, PlatformColorSpace.Srgb);

    public static PlatformSurfaceColorVolume Create(bool extended, double? headroom = null,
        double? maximumHeadroom = null) => new PlatformSurfaceColorVolume(
        Transfer: extended ? PlatformTransferFunction.Linear : PlatformTransferFunction.Srgb)
    {
        ReferenceWhiteScale = 1,
        HeadroomRatio = extended ? headroom : 1,
        MaximumHeadroomRatio = extended ? maximumHeadroom : 1,
        ToneMapping = PlatformToneMappingMode.Client
    }.Normalize();
}