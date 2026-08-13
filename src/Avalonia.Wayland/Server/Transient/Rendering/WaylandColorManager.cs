using System;
using System.Collections.Generic;
using Avalonia.Logging;
using Avalonia.Platform;
using Avalonia.Wayland.Server.Interop;
using NWayland.Protocols.ColorManagementV1;
using NWayland.Protocols.Wayland;

namespace Avalonia.Wayland.Server.Transient.Rendering;

/// <summary>
/// Wraps <c>wp_color_manager_v1</c>. Owns the single image description describing the color space
/// Avalonia renders its surfaces in, and tags every <c>wl_surface</c> with it so the compositor knows
/// how to convert our pixels for the output it ends up on.
///
/// Lives on the Wayland worker thread together with the rest of the transient objects.
/// </summary>
internal sealed class WaylandColorManager : IDisposable
{
    // Everything Avalonia needs (parametric creator, windows_scrgb, get_surface,
    // set_image_description) exists in version 1, and the version 2 additions only deprecate events
    // we don't consume. Binding v1 keeps us compatible with the widest set of compositors.
    private const uint BindVersion = 1;

    private readonly WaylandConnection _connection;
    private WpColorManagerV1 _manager = null!;
    private readonly HashSet<WpColorManagerV1.FeatureEnum> _features = new();
    private readonly HashSet<WpColorManagerV1.TransferFunctionEnum> _transferFunctions = new();
    private readonly HashSet<WpColorManagerV1.PrimariesEnum> _primaries = new();
    private readonly HashSet<WpColorManagerV1.RenderIntentEnum> _renderIntents = new();

    private WpImageDescriptionV1? _imageDescription;
    private bool _imageDescriptionReady;

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

            case WaylandColorMode.ExtendedLinear:
                if (SupportsParametricExtendedLinear || _features.Contains(WpColorManagerV1.FeatureEnum.WindowsScrgb))
                    return PlatformColorSpace.ScRgbLinear;
                return PlatformColorSpace.Unmanaged;

            default:
                return PlatformColorSpace.Unmanaged;
        }
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
            // Windows-scRGB is only used as a fallback: it pins signal 1.0 to 80 cd/m² rather than to
            // the reference white, which makes ordinary SDR content noticeably dimmer. The parametric
            // description keeps the sRGB default luminances, where 1.0 *is* the reference white,
            // matching EGL_EXT_gl_colorspace_scrgb_linear.
            if (colorSpace == PlatformColorSpace.ScRgbLinear && !SupportsParametricExtendedLinear)
            {
                _imageDescription = _manager.CreateWindowsScrgb(
                    new ImageDescriptionListener(this), _connection.Queue);
            }
            else
            {
                // These two interfaces have no events, but NWayland still requires a listener
                // whenever a target queue is given, so empty ones are supplied.
                var creator = _manager.CreateParametricCreator(new ParamsCreatorListener(), _connection.Queue);
                creator.SetPrimariesNamed(ToWaylandPrimaries(colorSpace));
                creator.SetTfNamed(ToWaylandTransferFunction(colorSpace));
                // `create` is a destructor request. Passing an explicit target queue here makes
                // NWayland route it through a proxy wrapper and then destroy the wrapper, which
                // aborts inside libwayland, so the image description inherits the creator's queue
                // (our own) instead.
                _imageDescription = creator.Create(new ImageDescriptionListener(this), null);
            }

            // ready/failed arrives asynchronously; we need the answer before the first frame is
            // committed, so block here rather than rendering a frame with an unknown color space.
            _connection.Queue.Roundtrip();

            if (!_imageDescriptionReady)
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

    // Perceptual is the only intent the protocol requires every compositor to support.
    private WpColorManagerV1.RenderIntentEnum PreferredRenderIntent =>
        _renderIntents.Contains(WpColorManagerV1.RenderIntentEnum.Relative)
            ? WpColorManagerV1.RenderIntentEnum.Relative
            : WpColorManagerV1.RenderIntentEnum.Perceptual;

    private bool SupportsParametricExtendedLinear =>
        _features.Contains(WpColorManagerV1.FeatureEnum.Parametric)
        && _transferFunctions.Contains(WpColorManagerV1.TransferFunctionEnum.ExtLinear)
        && _primaries.Contains(WpColorManagerV1.PrimariesEnum.Srgb);

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

    private void DestroyImageDescription()
    {
        _imageDescriptionReady = false;
        ActiveColorSpace = PlatformColorSpace.Unmanaged;
        if (_imageDescription != null)
        {
            _imageDescription.Destroy();
            _imageDescription.Dispose();
            _imageDescription = null;
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

    private sealed class ImageDescriptionListener(WaylandColorManager p) : WpImageDescriptionV1.Listener
    {
        protected override void Ready(WpImageDescriptionV1 eventSender, uint identity)
            => p._imageDescriptionReady = true;

        protected override void Failed(WpImageDescriptionV1 eventSender, WpImageDescriptionV1.CauseEnum cause,
            string msg)
        {
            p._imageDescriptionReady = false;
            Logger.TryGet(LogEventLevel.Warning, "Wayland")?.Log(null,
                "Compositor rejected our image description ({0}): {1}", cause, msg);
        }
    }
}
