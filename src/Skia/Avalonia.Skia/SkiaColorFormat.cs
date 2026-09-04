using System;
using System.Collections.Generic;
using Avalonia.Platform;
using SkiaSharp;

namespace Avalonia.Skia;

/// <summary>
/// Maps Avalonia's platform-level surface color formats onto Skia color types and color spaces.
/// </summary>
internal static class SkiaColorFormat
{
    // SKColorSpace wraps a ref-counted native object, so the well-known ones are created once.
    private static readonly SKColorSpace s_srgb = SKColorSpace.CreateSrgb();
    private static readonly SKColorSpace s_srgbLinear = SKColorSpace.CreateSrgbLinear();

    private static readonly SKColorSpace s_displayP3 =
        SKColorSpace.CreateRgb(SKColorSpaceTransferFn.TwoDotTwo, SKColorSpaceXyz.DisplayP3);

    private static readonly SKColorSpace s_rec2020 =
        SKColorSpace.CreateRgb(SKColorSpaceTransferFn.TwoDotTwo, SKColorSpaceXyz.Rec2020);

    private static readonly SKColorSpace s_rec2020Pq =
        SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Pq, SKColorSpaceXyz.Rec2020);

    private static readonly Dictionary<double, SKColorSpace> s_scaledScRgb = new();

    /// <summary>
    /// Returns the Skia color space for the given platform color space, or null for
    /// <see cref="PlatformColorSpace.Unmanaged"/>, which puts Skia in its legacy, non color managed mode.
    /// </summary>
    public static SKColorSpace? ToSkColorSpace(this PlatformColorSpace colorSpace) => colorSpace switch
    {
        PlatformColorSpace.Srgb => s_srgb,
        PlatformColorSpace.DisplayP3 => s_displayP3,
        PlatformColorSpace.Rec2020 => s_rec2020,
        PlatformColorSpace.ScRgbLinear => s_srgbLinear,
        PlatformColorSpace.Rec2020Pq => s_rec2020Pq,
        _ => null
    };

    /// <summary>
    /// Returns the Skia color type for the given pixel encoding.
    /// </summary>
    /// <param name="encoding">The requested pixel encoding.</param>
    /// <param name="platformDefault">
    /// The color type to use for <see cref="PlatformPixelEncoding.Default"/>. This differs per backend
    /// (Rgba8888 for OpenGL, Bgra8888 for Metal, the CPU-native order for offscreen surfaces).
    /// </param>
    public static SKColorType ToSkColorType(this PlatformPixelEncoding encoding, SKColorType platformDefault) =>
        encoding switch
        {
            PlatformPixelEncoding.Rgba1010102 => SKColorType.Rgba1010102,
            PlatformPixelEncoding.RgbaF16 => SKColorType.RgbaF16,
            _ => platformDefault
        };

    public static SKColorType ToSkColorType(this PlatformSurfaceColorFormat format, SKColorType platformDefault) =>
        format.Encoding.ToSkColorType(platformDefault);

    public static SKColorSpace? ToSkColorSpace(this PlatformSurfaceColorFormat format) =>
        format.ColorSpace.ToSkColorSpace();

    /// <summary>
    /// Returns the logical color space used while drawing a frame. Windows DWM defines numeric 1.0
    /// in scRGB as 80 nits while allowing a different SDR reference white. Scaling the destination's
    /// RGB-to-XYZ matrix makes Skia emit the corresponding extended-range values during normal color
    /// conversion. Other platforms retain their native color-volume handling.
    /// </summary>
    public static SKColorSpace? ToSkColorSpace(this PlatformSurfaceColorFormat format,
        PlatformSurfaceColorVolume? colorVolume)
    {
        var scale = GetReferenceWhiteScale(format, colorVolume);
        if (Math.Abs(scale - 1.0) < 0.000001)
            return format.ToSkColorSpace();

        lock (s_scaledScRgb)
        {
            if (s_scaledScRgb.TryGetValue(scale, out var cached))
                return cached;

            var values = SKColorSpaceXyz.Srgb.Values;
            for (var i = 0; i < values.Length; i++)
                values[i] /= (float)scale;

            var result = SKColorSpace.CreateRgb(
                SKColorSpaceTransferFn.Linear, new SKColorSpaceXyz(values));
            s_scaledScRgb.Add(scale, result);
            return result;
        }
    }

    internal static double GetReferenceWhiteScale(PlatformSurfaceColorFormat format,
        PlatformSurfaceColorVolume? colorVolume)
    {
        if (!OperatingSystem.IsWindows() || format.ColorSpace != PlatformColorSpace.ScRgbLinear ||
            colorVolume is not { } volume)
        {
            return 1.0;
        }

        var primaryWhite = volume.PrimaryLuminance.MaximumNits;
        var referenceWhite = volume.ReferenceWhiteNits;
        if (!double.IsFinite(primaryWhite) || !double.IsFinite(referenceWhite) ||
            primaryWhite <= 0 || referenceWhite <= 0)
        {
            return 1.0;
        }

        var scale = referenceWhite / primaryWhite;
        return double.IsFinite(scale) && scale > 0 ? scale : 1.0;
    }
}
