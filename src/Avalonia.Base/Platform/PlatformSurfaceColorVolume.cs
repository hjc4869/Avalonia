using System;
using Avalonia.Metadata;

namespace Avalonia.Platform;

/// <summary>
/// An absolute luminance range, in candela per square metre (nits).
/// </summary>
/// <param name="MinimumNits">The minimum luminance, or null when unknown.</param>
/// <param name="MaximumNits">The maximum luminance, or null when unknown.</param>
[Unstable]
public readonly record struct PlatformLuminanceRange(
    double? MinimumNits,
    double? MaximumNits)
{
    internal static PlatformLuminanceRange? Normalize(PlatformLuminanceRange? range)
    {
        var minimum = range?.MinimumNits is { } min && double.IsFinite(min) && min >= 0 ? min : (double?)null;
        var maximum = PlatformSurfaceColorVolume.PositiveOrNull(range?.MaximumNits);
        if (minimum > maximum)
            minimum = null;
        return minimum is null && maximum is null ? null : new(minimum, maximum);
    }
}

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
/// Who adapts the surface's highlights to the presentation target.
/// </summary>
[Unstable]
public enum PlatformToneMappingMode : byte
{
    /// <summary>No reliable mapping contract is available.</summary>
    Unknown,
    /// <summary>The client fits highlights to the target; out-of-range values may be clipped.</summary>
    Client,
    /// <summary>The platform adapts highlights; the client must not also map to the display peak.</summary>
    Platform
}

/// <summary>
/// The basis of the reported reference-white and target luminances, not a calibration guarantee.
/// </summary>
[Unstable]
public enum PlatformLuminanceBasis : byte
{
    /// <summary>No reference-white or target luminance is available.</summary>
    Unknown,
    /// <summary>Native display or target information.</summary>
    DisplayReported,
    /// <summary>A native theoretical or reference target, not physical display measurements.</summary>
    Nominal
}

/// <summary>
/// The current presentation contract and optional color-volume information for a surface.
/// Unknown measurements are null and do not imply SDR. Extended-linear input is unchanged.
/// </summary>
/// <param name="PrimaryLuminance">The native encoding's luminance range, if specified.</param>
/// <param name="ReferenceWhiteNits">Native diffuse-white luminance, qualified by <see cref="LuminanceBasis"/>.</param>
/// <param name="TargetLuminance">Native target luminance, qualified by <see cref="LuminanceBasis"/>.</param>
/// <param name="Transfer">Preferred/native transfer information, not the source content's transfer.</param>
/// <param name="TransferExponent">The power exponent for <see cref="PlatformTransferFunction.Power"/>; zero otherwise.</param>
/// <param name="SurfaceNitsPerUnit">Absolute nits represented by surface value 1, or null for a relative mapping.</param>
[Unstable]
public readonly record struct PlatformSurfaceColorVolume(
    PlatformLuminanceRange? PrimaryLuminance = null,
    double? ReferenceWhiteNits = null,
    PlatformLuminanceRange? TargetLuminance = null,
    PlatformTransferFunction Transfer = PlatformTransferFunction.Unknown,
    double TransferExponent = 0,
    double? SurfaceNitsPerUnit = null)
{
    /// <summary>
    /// Diffuse white in extended-linear drawing units, reported independently of absolute
    /// luminance. This is not a code value in a nonlinear native presentation buffer.
    /// </summary>
    public double? ReferenceWhiteScale { get; init; }

    /// <summary>
    /// Current presentation headroom relative to diffuse white. One means no highlight headroom;
    /// null means unknown. A ratio derived from a nominal target is not calibrated display headroom.
    /// </summary>
    public double? HeadroomRatio { get; init; }

    /// <summary>
    /// Native potential headroom, or <see cref="HeadroomRatio"/> when no separate valid maximum
    /// is available. Not a current render limit or a promised resource grant.
    /// </summary>
    public double? MaximumHeadroomRatio { get; init; }

    /// <summary>The actual negotiated highlight-mapping responsibility.</summary>
    public PlatformToneMappingMode ToneMapping { get; init; }

    /// <summary>The basis of <see cref="ReferenceWhiteNits"/> and <see cref="TargetLuminance"/>.</summary>
    public PlatformLuminanceBasis LuminanceBasis { get; init; }

    internal static double? PositiveOrNull(double? value) =>
        value is > 0 && double.IsFinite(value.Value) ? value : null;

    internal static double? HeadroomOrNull(double? value) =>
        value is >= 1 && double.IsFinite(value.Value) ? value : null;

    internal PlatformSurfaceColorVolume Normalize()
    {
        var white = PositiveOrNull(ReferenceWhiteNits);
        var target = PlatformLuminanceRange.Normalize(TargetLuminance);
        var current = HeadroomOrNull(HeadroomRatio);
        var maximum = HeadroomOrNull(MaximumHeadroomRatio);
        var transfer = Transfer;
        if (transfer == PlatformTransferFunction.Power && PositiveOrNull(TransferExponent) is null)
            transfer = PlatformTransferFunction.Unknown;
        return this with
        {
            PrimaryLuminance = PlatformLuminanceRange.Normalize(PrimaryLuminance),
            ReferenceWhiteNits = LuminanceBasis == PlatformLuminanceBasis.Unknown ? null : white,
            TargetLuminance = LuminanceBasis == PlatformLuminanceBasis.Unknown ? null : target,
            SurfaceNitsPerUnit = PositiveOrNull(SurfaceNitsPerUnit),
            ReferenceWhiteScale = PositiveOrNull(ReferenceWhiteScale),
            HeadroomRatio = current,
            MaximumHeadroomRatio = maximum < current ? current : maximum ?? current,
            LuminanceBasis = white is null && target is null ? PlatformLuminanceBasis.Unknown : LuminanceBasis,
            Transfer = transfer,
            TransferExponent = transfer == PlatformTransferFunction.Power ? TransferExponent : 0
        };
    }
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

/// <summary>
/// Source metadata for the composed HDR content, independent of display measurements.
/// </summary>
/// <param name="HeadroomRatio">Content peak relative to its diffuse white, or null when unknown.</param>
/// <param name="ReferenceWhiteNits">The authored content's diffuse white, or null when unknown.</param>
/// <param name="MasteringLuminance">The source mastering luminance range, or null when unknown.</param>
[Unstable]
public readonly record struct PlatformHdrContentMetadata(
    double? HeadroomRatio = null,
    double? ReferenceWhiteNits = null,
    PlatformLuminanceRange? MasteringLuminance = null)
{
    internal static PlatformHdrContentMetadata? Normalize(bool hasHdrContent, PlatformHdrContentMetadata? metadata)
    {
        if (!hasHdrContent || metadata is not { } value)
            return null;

        var normalized = value with
        {
            HeadroomRatio = PlatformSurfaceColorVolume.HeadroomOrNull(value.HeadroomRatio),
            ReferenceWhiteNits = PlatformSurfaceColorVolume.PositiveOrNull(value.ReferenceWhiteNits),
            MasteringLuminance = PlatformLuminanceRange.Normalize(value.MasteringLuminance)
        };
        return normalized == default ? null : normalized;
    }
}

/// <summary>
/// Provides a content hint that lets the platform reserve HDR headroom for a top level.
/// Obtained through <see cref="IOptionalFeatureProvider.TryGetFeature"/>.
/// </summary>
[Unstable]
public interface IPlatformHdrContentFeature
{
    /// <summary>
    /// Sets whether visible content needs luminance above reference white. Call on the UI thread,
    /// and reset to false when that source content is removed or deliberately switched to SDR.
    /// Set true before waiting for headroom; temporarily fitting HDR to SDR while awaiting a
    /// grant must not clear the hint. The default is false.
    /// Metadata describes the composed source content, not the current or potential display
    /// headroom. Null means unknown metadata; false clears it. Metadata-only updates take effect
    /// even when the HDR intent is unchanged. Backends use the fields their native path supports.
    /// </summary>
    void SetHdrContent(bool hasHdrContent, PlatformHdrContentMetadata? metadata);
}
