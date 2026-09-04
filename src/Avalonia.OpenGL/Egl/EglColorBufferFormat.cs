using System.Collections.Generic;
using Avalonia.Metadata;
using Avalonia.Platform;

namespace Avalonia.OpenGL.Egl;

/// <summary>
/// A candidate color buffer layout for <c>eglChooseConfig</c>, along with the Avalonia-level
/// surface color format it produces.
/// </summary>
/// <param name="ColorBits">Requested size of the R, G and B channels, in bits.</param>
/// <param name="AlphaBits">Requested size of the alpha channel, in bits.</param>
/// <param name="FloatComponents">
/// Whether floating point channels are required. Needs <c>EGL_EXT_pixel_format_float</c>.
/// </param>
/// <param name="ColorSpace">The color space surfaces created from this config will be interpreted in.</param>
[PrivateApi]
public readonly record struct EglColorBufferFormat(
    int ColorBits,
    int AlphaBits,
    bool FloatComponents,
    PlatformColorSpace ColorSpace)
{
    /// <summary>
    /// The legacy 8 bits per channel, non color managed format used by default.
    /// </summary>
    public static EglColorBufferFormat Standard { get; } = new(8, 8, false, PlatformColorSpace.Unmanaged);

    public PlatformPixelEncoding Encoding => (ColorBits, FloatComponents) switch
    {
        (16, true) => PlatformPixelEncoding.RgbaF16,
        (10, _) => PlatformPixelEncoding.Rgba1010102,
        _ => PlatformPixelEncoding.Default
    };

    public PlatformSurfaceColorFormat SurfaceColorFormat => new(Encoding, ColorSpace);

    /// <summary>
    /// A 16 bit float config in the given color space.
    /// </summary>
    public static EglColorBufferFormat Float16(PlatformColorSpace colorSpace) => new(16, 16, true, colorSpace);

    /// <summary>
    /// A 10 bit per channel config in the given color space.
    /// </summary>
    public static EglColorBufferFormat Rgb10A2(PlatformColorSpace colorSpace) => new(10, 2, false, colorSpace);

    /// <summary>
    /// An 8 bit per channel config in the given color space.
    /// </summary>
    public static EglColorBufferFormat Rgba8(PlatformColorSpace colorSpace) => new(8, 8, false, colorSpace);

    /// <summary>
    /// A single-element list containing only <see cref="Standard"/>, i.e. no advanced color.
    /// </summary>
    public static IReadOnlyList<EglColorBufferFormat> StandardOnly { get; } = new[] { Standard };
}
