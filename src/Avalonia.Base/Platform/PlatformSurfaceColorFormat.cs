using Avalonia.Metadata;

namespace Avalonia.Platform;

/// <summary>
/// The color space a render surface is encoded in.
/// </summary>
[Unstable]
public enum PlatformColorSpace : byte
{
    /// <summary>
    /// The surface is not color managed: 8 bit sRGB-encoded values are handed to the display untouched
    /// and no conversion is applied to any drawing operation. This is Avalonia's historical behaviour
    /// and remains the default.
    /// </summary>
    Unmanaged = 0,

    /// <summary>
    /// sRGB primaries with the sRGB transfer function.
    /// </summary>
    Srgb,

    /// <summary>
    /// Display P3 primaries with a pure 2.2 gamma transfer function.
    /// </summary>
    DisplayP3,

    /// <summary>
    /// Rec. 2020 primaries with a pure 2.2 gamma transfer function.
    /// </summary>
    Rec2020,

    /// <summary>
    /// scRGB: sRGB primaries with an extended linear transfer function. Channel values outside of
    /// <c>[0, 1]</c> are meaningful, so this requires a floating point <see cref="PlatformPixelEncoding"/>.
    /// </summary>
    ScRgbLinear,

    /// <summary>
    /// Rec. 2020 primaries with the SMPTE ST 2084 (PQ) transfer function.
    /// </summary>
    Rec2020Pq,

    /// <summary>
    /// Display P3 primaries with the sRGB transfer function. This is the encoding the web platform's
    /// <c>display-p3</c> predefined color space uses, as defined by CSS Color 4.
    /// </summary>
    DisplayP3Srgb,

    /// <summary>
    /// Extended sRGB: sRGB primaries and the sRGB transfer function, but unbounded. Channel values
    /// outside of <c>[0, 1]</c> are meaningful, so this requires a floating point
    /// <see cref="PlatformPixelEncoding"/>. Colors outside the sRGB gamut are expressed with negative
    /// channel values, and values above 1 exceed the SDR white level.
    /// </summary>
    /// <remarks>
    /// This is the same idea as <see cref="ScRgbLinear"/>, but keeps the sRGB transfer function
    /// instead of a linear one, so blending and gradient interpolation still happen in sRGB-encoded
    /// space and match Avalonia's historical appearance.
    /// </remarks>
    ExtendedSrgb
}

/// <summary>
/// The pixel encoding of a render surface.
/// </summary>
[Unstable]
public enum PlatformPixelEncoding : byte
{
    /// <summary>
    /// The platform's default 8 bits per channel encoding.
    /// </summary>
    Default = 0,

    /// <summary>
    /// 10 bits per color channel with 2 bits of alpha.
    /// </summary>
    Rgba1010102,

    /// <summary>
    /// 16 bit floating point per channel.
    /// </summary>
    RgbaF16
}

/// <summary>
/// Describes the pixel encoding and color space a render surface was actually created with.
/// </summary>
/// <remarks>
/// This is reported bottom-up by the platform backend. The default value describes the legacy,
/// non color managed 8 bit sRGB surface every Avalonia backend used before wide color gamut support
/// was introduced, so existing code keeps rendering exactly as it did.
/// </remarks>
[Unstable]
public readonly record struct PlatformSurfaceColorFormat(
    PlatformPixelEncoding Encoding,
    PlatformColorSpace ColorSpace)
{
    /// <summary>
    /// The legacy, non color managed 8 bit sRGB surface format.
    /// </summary>
    public static PlatformSurfaceColorFormat Unmanaged => default;

    /// <summary>
    /// Whether drawing operations are color converted into <see cref="ColorSpace"/>.
    /// </summary>
    public bool IsColorManaged => ColorSpace != PlatformColorSpace.Unmanaged;

    /// <summary>
    /// Whether channel values outside of <c>[0, 1]</c> are meaningful on this surface, i.e. whether
    /// colors outside of the sRGB gamut and above the SDR white level can be expressed.
    /// </summary>
    public bool IsExtendedRange => ColorSpace is PlatformColorSpace.ScRgbLinear
        or PlatformColorSpace.ExtendedSrgb;

    /// <summary>
    /// Whether this format can display colors outside of the sRGB gamut.
    /// </summary>
    /// <remarks>
    /// Extended range formats qualify even though their primaries are sRGB, because negative channel
    /// values reach outside the sRGB gamut.
    /// </remarks>
    public bool IsWideGamut => ColorSpace is PlatformColorSpace.DisplayP3 or PlatformColorSpace.DisplayP3Srgb
        or PlatformColorSpace.Rec2020 or PlatformColorSpace.Rec2020Pq or PlatformColorSpace.ScRgbLinear
        or PlatformColorSpace.ExtendedSrgb;

    public override string ToString() => $"{Encoding}/{ColorSpace}";
}
