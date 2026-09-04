using System;
using Avalonia.Metadata;

namespace Avalonia.Platform;

/// <summary>
/// An absolute luminance range, in candela per square metre (nits).
/// </summary>
/// <param name="MinimumNits">The darkest luminance that can be reproduced.</param>
/// <param name="MaximumNits">The brightest luminance that can be reproduced.</param>
[Unstable]
public readonly record struct PlatformLuminanceRange(
    double MinimumNits,
    double MaximumNits);

/// <summary>
/// How the channel values of an encoding map to light.
/// </summary>
[Unstable]
public enum PlatformTransferFunction : byte
{
    /// <summary>The platform did not report one.</summary>
    Unknown = 0,

    /// <summary>
    /// A pure power law, whose exponent is <see cref="PlatformSurfaceColorVolume.TransferExponent"/>.
    /// Gamma 2.2, the ordinary case for an SDR display, is reported this way.
    /// </summary>
    Power,

    /// <summary>The sRGB curve of IEC 61966-2-1: a linear segment, then a 2.4 power.</summary>
    Srgb,

    /// <summary>Rec. ITU-R BT.1886.</summary>
    Bt1886,

    /// <summary>Linear light, which is what an extended range surface stores.</summary>
    Linear,

    /// <summary>SMPTE ST 2084, the perceptual quantizer.</summary>
    Pq,

    /// <summary>Rec. ITU-R BT.2100 hybrid log-gamma.</summary>
    Hlg,

    /// <summary>SMPTE ST 428-1.</summary>
    St428
}

/// <summary>
/// The color volume the platform currently prefers a surface to be rendered for, i.e. what the
/// display the surface is shown on can actually reproduce.
/// </summary>
/// <param name="PrimaryLuminance">The luminance range the color encoding itself is defined against.</param>
/// <param name="ReferenceWhiteNits">
/// The luminance of diffuse white, i.e. the level ordinary SDR content is displayed at.
/// </param>
/// <param name="TargetLuminance">
/// The luminance range that can actually be displayed. Its maximum is the peak luminance available
/// for highlights, and may use a different scale than <paramref name="PrimaryLuminance"/>.
/// </param>
/// <param name="Transfer">
/// The transfer function of the preferred encoding, or <see cref="PlatformTransferFunction.Unknown"/>
/// where the platform does not say.
/// </param>
/// <param name="TransferExponent">
/// The exponent of <paramref name="Transfer"/> when it is <see cref="PlatformTransferFunction.Power"/>,
/// and 0 otherwise.
/// </param>
/// <param name="SurfaceNitsPerUnit">
/// What numeric 1.0 on the surface is worth, in nits, or 0 where the platform does not say.
/// </param>
/// <remarks>
/// <para>
/// Unlike <see cref="PlatformSurfaceColorFormat"/>, which describes the stable encoding a surface
/// was created with, this changes at runtime — most commonly when a window is moved to another
/// monitor or the display's HDR configuration changes.
/// </para>
/// <para>
/// <paramref name="Transfer"/> is what a drawing operation rendering into an extended range surface
/// has to know: the platform converts that surface's linear light into the preferred encoding on the
/// way to the display, so anything wanting to reproduce a signal it was handed has to undo exactly
/// this curve rather than assume one. The platforms disagree on which curve an ordinary surface
/// carries, and the difference is worth several code points in the shadows.
/// </para>
/// <para>
/// <paramref name="SurfaceNitsPerUnit"/> is what an extended range surface has to be scaled by:
/// it is <paramref name="ReferenceWhiteNits"/> wherever the platform re-anchors the surface to the
/// display's diffuse white itself, and scRGB's own 80 cd/m² where 1.0 is instead pinned absolutely,
/// which is what the DWM does on an HDR display. Diffuse white therefore belongs at
/// <paramref name="ReferenceWhiteNits"/> / <paramref name="SurfaceNitsPerUnit"/>, not at 1.0.
/// </para>
/// </remarks>
[Unstable]
public readonly record struct PlatformSurfaceColorVolume(
    PlatformLuminanceRange PrimaryLuminance,
    double ReferenceWhiteNits,
    PlatformLuminanceRange TargetLuminance,
    PlatformTransferFunction Transfer = PlatformTransferFunction.Unknown,
    double TransferExponent = 0,
    double SurfaceNitsPerUnit = 0)
{
    /// <summary>
    /// What diffuse white is worth in the surface's own numbers, i.e. what an extended range
    /// surface has to write for it. 1.0 unless the platform pins 1.0 to something else.
    /// </summary>
    public double ReferenceWhiteScale =>
        SurfaceNitsPerUnit > 0 && ReferenceWhiteNits > 0 &&
        double.IsFinite(SurfaceNitsPerUnit) && double.IsFinite(ReferenceWhiteNits)
            ? ReferenceWhiteNits / SurfaceNitsPerUnit
            : 1.0;

    /// <summary>
    /// How much brighter than diffuse white the display can go, i.e.
    /// <see cref="TargetLuminance"/>'s maximum expressed in multiples of
    /// <see cref="ReferenceWhiteNits"/>. 1.0 means there is no headroom for highlights.
    /// </summary>
    public double HeadroomRatio => ReferenceWhiteNits > 0
        ? Math.Max(1.0, TargetLuminance.MaximumNits / ReferenceWhiteNits)
        : 1.0;
}

/// <summary>
/// Exposes the color volume the platform prefers for a top level's surface. Obtained from the top
/// level's platform implementation via <see cref="IOptionalFeatureProvider.TryGetFeature"/>.
/// </summary>
[Unstable]
public interface IPlatformSurfaceColorVolumeFeature
{
    /// <summary>
    /// The currently preferred color volume, or null when the platform can't report one.
    /// </summary>
    PlatformSurfaceColorVolume? PreferredColorVolume { get; }

    /// <summary>
    /// Raised on the UI thread when <see cref="PreferredColorVolume"/> changes.
    /// </summary>
    event EventHandler? PreferredColorVolumeChanged;
}
