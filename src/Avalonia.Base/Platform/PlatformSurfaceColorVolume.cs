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
/// <remarks>
/// Unlike <see cref="PlatformSurfaceColorFormat"/>, which describes the stable encoding a surface
/// was created with, this changes at runtime — most commonly when a window is moved to another
/// monitor or the display's HDR configuration changes.
/// </remarks>
[Unstable]
public readonly record struct PlatformSurfaceColorVolume(
    PlatformLuminanceRange PrimaryLuminance,
    double ReferenceWhiteNits,
    PlatformLuminanceRange TargetLuminance)
{
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
