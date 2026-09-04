using System;
using System.Collections.Generic;
using Avalonia.Logging;
using Avalonia.Platform;
using Avalonia.Wayland.Server.Interop;
using NWayland.Protocols.ColorManagementV1;
using NWayland.Protocols.Wayland;

namespace Avalonia.Wayland.Server.Transient.Rendering;

/// <summary>
/// Wraps <c>wp_color_manager_v1</c>. Owns the image descriptions for Avalonia's surface encoding
/// and SDR/HDR content ranges, and tags each <c>wl_surface</c> with its requested range.
///
/// Lives on the Wayland worker thread together with the rest of the transient objects.
/// </summary>
internal sealed class WaylandColorManager : IDisposable
{
    // Everything Avalonia needs (parametric creator, windows_scrgb, get_surface,
    // set_image_description) exists in version 1, and the version 2 additions only deprecate events
    // we don't consume. Binding v1 keeps us compatible with the widest set of compositors.
    private const uint BindVersion = 1;

    // scRGB's own reference white, which is what signal 1.0 stands for on an extended linear surface.
    private const uint ScRgbReferenceWhiteNits = 80;
    private const uint ExtendedLinearTargetPeakNits = 10_000;
    private const uint PqReferenceWhiteNits = 203;

    private readonly WaylandConnection _connection;
    private WpColorManagerV1 _manager = null!;
    private readonly HashSet<WpColorManagerV1.FeatureEnum> _features = new();
    private readonly HashSet<WpColorManagerV1.TransferFunctionEnum> _transferFunctions = new();
    private readonly HashSet<WpColorManagerV1.PrimariesEnum> _primaries = new();
    private readonly HashSet<WpColorManagerV1.RenderIntentEnum> _renderIntents = new();

    private WpImageDescriptionV1? _imageDescription;
    private bool _imageDescriptionReady;
    private WpImageDescriptionV1? _hdrImageDescription;
    private bool _hdrImageDescriptionReady;
    private bool _usesAbsoluteScRgb;
    private WpColorManagerV1.RenderIntentEnum? _selectedRenderIntent;

    internal static readonly IReadOnlyList<WaylandHdrPresentationMode> DefaultHdrPreferences =
    [
        WaylandHdrPresentationMode.LinearRelative,
        WaylandHdrPresentationMode.LinearPerceptual,
        WaylandHdrPresentationMode.WindowsScRgbRelative,
        WaylandHdrPresentationMode.WindowsScRgbPerceptual,
        WaylandHdrPresentationMode.PqRelative,
        WaylandHdrPresentationMode.PqPerceptual,
        WaylandHdrPresentationMode.Sdr
    ];

    private WaylandColorManager(WaylandConnection connection)
    {
        _connection = connection;
    }

    /// <summary>
    /// The color space surfaces are currently tagged with, or <see cref="PlatformColorSpace.Unmanaged"/>
    /// when no image description could be established.
    /// </summary>
    public PlatformColorSpace ActiveColorSpace { get; private set; }

    public static WaylandColorManager? TryCreate(WaylandConnection connection, WlRegistry registry,
        uint globalName, uint globalVersion)
    {
        if (globalVersion < BindVersion)
            return null;

        // NWayland takes the listener at bind time, so the instance has to exist first.
        var result = new WaylandColorManager(connection);
        try
        {
            result._manager = registry.Bind<WpColorManagerV1>(globalName, BindVersion, new Listener(result));
            // The compositor announces its capabilities immediately after bind and terminates the
            // burst with `done`, so a single roundtrip is enough to have the full picture.
            connection.Queue.Roundtrip();
            return result;
        }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Warning, "Wayland")?.Log(null,
                "Unable to bind wp_color_manager_v1: {0}", e);
            return null;
        }
    }

    /// <summary>
    /// Picks the best color space the compositor can accept for the requested mode, or
    /// <see cref="PlatformColorSpace.Unmanaged"/> when the mode can't be satisfied.
    /// </summary>
    public PlatformColorSpace SelectColorSpace(WaylandColorMode mode)
    {
        switch (mode)
        {
            case WaylandColorMode.WideColorGamut:
                if (!_features.Contains(WpColorManagerV1.FeatureEnum.Parametric))
                    return PlatformColorSpace.Unmanaged;
                // Gamma 2.2 keeps blending close to Avalonia's historical sRGB-encoded behaviour,
                // unlike a linear-light target which visibly changes gradients and antialiasing.
                if (!_transferFunctions.Contains(WpColorManagerV1.TransferFunctionEnum.Gamma22))
                    return PlatformColorSpace.Unmanaged;
                if (_primaries.Contains(WpColorManagerV1.PrimariesEnum.DisplayP3))
                    return PlatformColorSpace.DisplayP3;
                if (_primaries.Contains(WpColorManagerV1.PrimariesEnum.Bt2020))
                    return PlatformColorSpace.Rec2020;
                return PlatformColorSpace.Unmanaged;

            default:
                return PlatformColorSpace.Unmanaged;
        }
    }

    internal static (PlatformColorSpace ColorSpace, bool AbsoluteScRgb, WpColorManagerV1.RenderIntentEnum Intent)?
        DescribePresentation(WaylandHdrPresentationMode mode) => mode switch
        {
            WaylandHdrPresentationMode.LinearRelative => (PlatformColorSpace.ScRgbLinear, false, WpColorManagerV1.RenderIntentEnum.Relative),
            WaylandHdrPresentationMode.LinearPerceptual => (PlatformColorSpace.ScRgbLinear, false, WpColorManagerV1.RenderIntentEnum.Perceptual),
            WaylandHdrPresentationMode.WindowsScRgbRelative => (PlatformColorSpace.ScRgbLinear, true, WpColorManagerV1.RenderIntentEnum.Relative),
            WaylandHdrPresentationMode.WindowsScRgbPerceptual => (PlatformColorSpace.ScRgbLinear, true, WpColorManagerV1.RenderIntentEnum.Perceptual),
            WaylandHdrPresentationMode.PqRelative => (PlatformColorSpace.Rec2020Pq, false, WpColorManagerV1.RenderIntentEnum.Relative),
            WaylandHdrPresentationMode.PqPerceptual => (PlatformColorSpace.Rec2020Pq, false, WpColorManagerV1.RenderIntentEnum.Perceptual),
            _ => null
        };

    public bool TrySelectHdrPresentation(WaylandHdrPresentationMode mode, out PlatformColorSpace colorSpace)
    {
        DestroyImageDescription();
        colorSpace = PlatformColorSpace.Unmanaged;
        if (DescribePresentation(mode) is not { } candidate || !_renderIntents.Contains(candidate.Intent))
            return false;
        var supported = candidate.AbsoluteScRgb
            ? _features.Contains(WpColorManagerV1.FeatureEnum.WindowsScrgb)
            : candidate.ColorSpace == PlatformColorSpace.ScRgbLinear
                ? SupportsParametricExtendedLinear
                : _features.Contains(WpColorManagerV1.FeatureEnum.Parametric)
                  && _transferFunctions.Contains(WpColorManagerV1.TransferFunctionEnum.St2084Pq)
                  && _primaries.Contains(WpColorManagerV1.PrimariesEnum.Bt2020);
        if (!supported)
            return false;
        _usesAbsoluteScRgb = candidate.AbsoluteScRgb;
        _selectedRenderIntent = candidate.Intent;
        colorSpace = candidate.ColorSpace;
        return true;
    }

    /// <summary>
    /// Creates the image description for <paramref name="colorSpace"/>. Returns false when the
    /// compositor rejected it, in which case surfaces must be left untagged (and therefore rendered
    /// as plain sRGB) rather than being shown with the wrong color space.
    /// </summary>
    public bool TryCreateImageDescription(PlatformColorSpace colorSpace)
    {
        if (colorSpace == PlatformColorSpace.Unmanaged)
            return false;
        if (_imageDescription != null)
            return _imageDescriptionReady && ActiveColorSpace == colorSpace;

        try
        {
            _imageDescriptionReady = false;
            if (_usesAbsoluteScRgb)
            {
                _imageDescription = _manager.CreateWindowsScrgb(
                    new ImageDescriptionListener(ready => _imageDescriptionReady = ready), _connection.Queue);
            }
            else
            {
                _imageDescription = CreateParametricDescription(colorSpace, ScRgbReferenceWhiteNits,
                    ready => _imageDescriptionReady = ready);
                if (colorSpace == PlatformColorSpace.ScRgbLinear)
                {
                    _hdrImageDescription = CreateParametricDescription(colorSpace, ExtendedLinearTargetPeakNits,
                        ready => _hdrImageDescriptionReady = ready);
                }
            }

            // ready/failed arrives asynchronously; we need the answer before the first frame is
            // committed, so block here rather than rendering a frame with an unknown color space.
            _connection.Queue.Roundtrip();

            if (!_imageDescriptionReady || (_hdrImageDescription != null && !_hdrImageDescriptionReady))
            {
                DestroyImageDescription();
                return false;
            }

            ActiveColorSpace = colorSpace;
            return true;
        }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Warning, "Wayland")?.Log(null,
                "Unable to create a {0} image description: {1}", colorSpace, e);
            DestroyImageDescription();
            return false;
        }
    }

    private WpImageDescriptionV1 CreateParametricDescription(PlatformColorSpace colorSpace, uint targetPeakNits,
        Action<bool> ready, (uint Minimum, uint Maximum, uint? MaxCll)? contentLuminances = null)
    {
        var creator = _manager.CreateParametricCreator(new ParamsCreatorListener(), _connection.Queue);
        creator.SetPrimariesNamed(ToWaylandPrimaries(colorSpace));
        creator.SetTfNamed(ToWaylandTransferFunction(colorSpace));
        if (colorSpace == PlatformColorSpace.ScRgbLinear)
        {
            if (SupportsSetLuminances)
                creator.SetLuminances(0, ScRgbReferenceWhiteNits, ScRgbReferenceWhiteNits);
            creator.SetMasteringDisplayPrimaries(
                708_000, 292_000, 170_000, 797_000,
                131_000, 46_000, 312_700, 329_000);
            if (contentLuminances is null)
                creator.SetMasteringLuminance(0, targetPeakNits);
        }
        if (contentLuminances is { } luminances)
        {
            creator.SetMasteringLuminance(luminances.Minimum, luminances.Maximum);
            if (luminances.MaxCll is { } maxCll)
                creator.SetMaxCll(maxCll);
        }
        return creator.Create(new ImageDescriptionListener(ready), null);
    }

    /// <summary>
    /// Tags a surface with the active image description. The returned object must be destroyed
    /// before the <c>wl_surface</c> it was created from.
    /// </summary>
    public WpColorManagementSurfaceV1? TryAttach(WlSurface surface)
    {
        if (_imageDescription == null || !_imageDescriptionReady)
            return null;

        WpColorManagementSurfaceV1? colorSurface = null;
        try
        {
            colorSurface = _manager.GetSurface(surface, new ColorSurfaceListener(), _connection.Queue);
            colorSurface.SetImageDescription(_imageDescription, PreferredRenderIntent);
            return colorSurface;
        }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Warning, "Wayland")?.Log(null,
                "Unable to attach a color management surface: {0}", e);
            colorSurface?.Destroy();
            colorSurface?.Dispose();
            return null;
        }
    }

    public WpImageDescriptionV1? SetHdrContent(WpColorManagementSurfaceV1 surface, bool hasHdrContent,
        PlatformHdrContentMetadata? metadata)
    {
        if (_imageDescription == null || !_imageDescriptionReady)
            return null;
        var contentDescription = hasHdrContent ? TryCreateContentDescription(metadata) : null;
        var description = contentDescription ?? (hasHdrContent && _hdrImageDescriptionReady ? _hdrImageDescription! : _imageDescription);
        surface.SetImageDescription(description, PreferredRenderIntent);
        return contentDescription;
    }

    private WpImageDescriptionV1? TryCreateContentDescription(PlatformHdrContentMetadata? metadata)
    {
        if (_usesAbsoluteScRgb || !_features.Contains(WpColorManagerV1.FeatureEnum.SetMasteringDisplayPrimaries) ||
            GetContentLuminances(ActiveColorSpace, metadata) is not { } luminances)
            return null;

        WpImageDescriptionV1? description = null;
        try
        {
            var ready = false;
            description = CreateParametricDescription(ActiveColorSpace, luminances.Maximum,
                value => ready = value, luminances);
            _connection.Queue.Roundtrip();
            if (ready)
                return description;
        }
        catch (Exception exception)
        {
            Logger.TryGet(LogEventLevel.Warning, "Wayland")?.Log(this,
                "Unable to create HDR content metadata description: {0}", exception);
        }
        description?.Destroy();
        description?.Dispose();
        return null;
    }

    internal static (uint Minimum, uint Maximum, uint? MaxCll)? GetContentLuminances(
        PlatformColorSpace colorSpace, PlatformHdrContentMetadata? metadata)
    {
        metadata = PlatformHdrContentMetadata.Normalize(true, metadata);
        if (metadata is not { } content || colorSpace is not (PlatformColorSpace.ScRgbLinear or PlatformColorSpace.Rec2020Pq))
            return null;

        var nativeWhite = colorSpace == PlatformColorSpace.ScRgbLinear ? ScRgbReferenceWhiteNits : PqReferenceWhiteNits;
        var contentPeak = content.HeadroomRatio * nativeWhite;
        var masteringPeak = content.MasteringLuminance?.MaximumNits / content.ReferenceWhiteNits * nativeWhite;
        var maximum = masteringPeak ?? contentPeak;
        var minimum = content.MasteringLuminance?.MinimumNits / content.ReferenceWhiteNits * nativeWhite ?? 0;
        if (maximum is not { } peak || !double.IsFinite(peak) || peak <= 0 || peak > ExtendedLinearTargetPeakNits ||
            !double.IsFinite(minimum) || minimum >= peak)
            return null;

        return ((uint)Math.Floor(minimum * 10_000), (uint)Math.Ceiling(peak),
            contentPeak is > 0 and <= ExtendedLinearTargetPeakNits and var maxCll ? (uint)Math.Ceiling(maxCll) : null);
    }

    /// <summary>
    /// Starts tracking the color volume the compositor prefers for <paramref name="surface"/>.
    /// <paramref name="publish"/> is invoked on the Wayland thread whenever the answer changes.
    /// The returned object must be disposed before the <c>wl_surface</c> it was created from.
    /// </summary>
    public WaylandColorVolumeFeedback? TryTrackColorVolume(WlSurface surface,
        Action<PlatformSurfaceColorVolume?> publish)
    {
        if (ActiveColorSpace == PlatformColorSpace.Unmanaged)
            return null;
        void Publish(PlatformSurfaceColorVolume? volume) =>
            publish(CreateColorVolume(volume, _usesAbsoluteScRgb, PreferredRenderIntent));
        Publish(null);
        return WaylandColorVolumeFeedback.TryCreate(_connection, _manager, surface, Publish);
    }

    internal static PlatformSurfaceColorVolume CreateColorVolume(PlatformSurfaceColorVolume? feedback,
        bool absoluteScRgb, WpColorManagerV1.RenderIntentEnum intent)
    {
        var volume = feedback.GetValueOrDefault().Normalize();
        var headroom = intent == WpColorManagerV1.RenderIntentEnum.Relative &&
                       volume.ReferenceWhiteNits is { } white && volume.TargetLuminance?.MaximumNits is { } peak
            ? Math.Max(1, peak / white) : (double?)null;
        return (volume with
        {
            SurfaceNitsPerUnit = absoluteScRgb ? ScRgbReferenceWhiteNits : null,
            ReferenceWhiteScale = absoluteScRgb ? null : 1,
            HeadroomRatio = headroom,
            MaximumHeadroomRatio = null,
            ToneMapping = intent == WpColorManagerV1.RenderIntentEnum.Perceptual
                ? PlatformToneMappingMode.Platform : PlatformToneMappingMode.Client
        }).Normalize();
    }

    // Perceptual is the only intent the protocol requires every compositor to support.
    private WpColorManagerV1.RenderIntentEnum PreferredRenderIntent =>
        _selectedRenderIntent ?? (_renderIntents.Contains(WpColorManagerV1.RenderIntentEnum.Relative)
            ? WpColorManagerV1.RenderIntentEnum.Relative
            : WpColorManagerV1.RenderIntentEnum.Perceptual);

    private bool SupportsParametricExtendedLinear =>
        _features.Contains(WpColorManagerV1.FeatureEnum.Parametric)
        && _features.Contains(WpColorManagerV1.FeatureEnum.SetMasteringDisplayPrimaries)
        && _features.Contains(WpColorManagerV1.FeatureEnum.ExtendedTargetVolume)
        && _transferFunctions.Contains(WpColorManagerV1.TransferFunctionEnum.ExtLinear)
        && _primaries.Contains(WpColorManagerV1.PrimariesEnum.Srgb);

    private bool SupportsSetLuminances =>
        _features.Contains(WpColorManagerV1.FeatureEnum.SetLuminances);

    private static WpColorManagerV1.PrimariesEnum ToWaylandPrimaries(PlatformColorSpace colorSpace) =>
        colorSpace switch
        {
            PlatformColorSpace.DisplayP3 => WpColorManagerV1.PrimariesEnum.DisplayP3,
            PlatformColorSpace.Rec2020 or PlatformColorSpace.Rec2020Pq => WpColorManagerV1.PrimariesEnum.Bt2020,
            _ => WpColorManagerV1.PrimariesEnum.Srgb
        };

    private static WpColorManagerV1.TransferFunctionEnum ToWaylandTransferFunction(PlatformColorSpace colorSpace) =>
        colorSpace switch
        {
            PlatformColorSpace.ScRgbLinear => WpColorManagerV1.TransferFunctionEnum.ExtLinear,
            PlatformColorSpace.Rec2020Pq => WpColorManagerV1.TransferFunctionEnum.St2084Pq,
            _ => WpColorManagerV1.TransferFunctionEnum.Gamma22
        };

    public void DestroyImageDescription()
    {
        _usesAbsoluteScRgb = false;
        _selectedRenderIntent = null;
        _imageDescriptionReady = false;
        _hdrImageDescriptionReady = false;
        ActiveColorSpace = PlatformColorSpace.Unmanaged;
        if (_imageDescription != null)
        {
            _imageDescription.Destroy();
            _imageDescription.Dispose();
            _imageDescription = null;
        }
        if (_hdrImageDescription != null)
        {
            _hdrImageDescription.Destroy();
            _hdrImageDescription.Dispose();
            _hdrImageDescription = null;
        }
    }

    public void Dispose()
    {
        DestroyImageDescription();
        _manager.Destroy();
        _manager.Dispose();
    }

    private sealed class Listener(WaylandColorManager p) : WpColorManagerV1.Listener
    {
        protected override void SupportedFeature(WpColorManagerV1 eventSender, WpColorManagerV1.FeatureEnum feature)
            => p._features.Add(feature);

        protected override void SupportedIntent(WpColorManagerV1 eventSender,
            WpColorManagerV1.RenderIntentEnum renderIntent)
            => p._renderIntents.Add(renderIntent);

        protected override void SupportedPrimariesNamed(WpColorManagerV1 eventSender,
            WpColorManagerV1.PrimariesEnum primaries)
            => p._primaries.Add(primaries);

        protected override void SupportedTfNamed(WpColorManagerV1 eventSender,
            WpColorManagerV1.TransferFunctionEnum tf)
            => p._transferFunctions.Add(tf);
    }

    // wp_image_description_creator_params_v1 and wp_color_management_surface_v1 have no events,
    // but NWayland requires a listener whenever a target queue is specified.
    private sealed class ParamsCreatorListener : WpImageDescriptionCreatorParamsV1.Listener;

    private sealed class ColorSurfaceListener : WpColorManagementSurfaceV1.Listener;

    private sealed class ImageDescriptionListener(Action<bool> ready) : WpImageDescriptionV1.Listener
    {
        protected override void Ready(WpImageDescriptionV1 eventSender, uint identity)
            => ready(true);

        protected override void Failed(WpImageDescriptionV1 eventSender, WpImageDescriptionV1.CauseEnum cause,
            string msg)
        {
            ready(false);
            Logger.TryGet(LogEventLevel.Warning, "Wayland")?.Log(null,
                "Compositor rejected our image description ({0}): {1}", cause, msg);
        }
    }
}
