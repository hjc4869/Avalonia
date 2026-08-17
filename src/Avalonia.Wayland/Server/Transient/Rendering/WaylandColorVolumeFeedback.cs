using System;
using Avalonia.Logging;
using Avalonia.Platform;
using Avalonia.Wayland.Server.Interop;
using NWayland.Protocols.ColorManagementV1;
using NWayland.Protocols.Wayland;

namespace Avalonia.Wayland.Server.Transient.Rendering;

/// <summary>
/// Wraps <c>wp_color_management_surface_feedback_v1</c> for a single <c>wl_surface</c> and turns the
/// compositor's preferred image description into a <see cref="PlatformSurfaceColorVolume"/>, which
/// is where the peak and reference white luminances of the output the surface is currently shown on
/// come from.
///
/// The value is per-surface rather than global: the compositor re-evaluates it whenever the window
/// moves between outputs, which it announces via <c>preferred_changed</c>.
///
/// Lives on the Wayland worker thread.
/// </summary>
internal sealed class WaylandColorVolumeFeedback : IDisposable
{
    private readonly WaylandConnection _connection;
    private readonly Action<PlatformSurfaceColorVolume?> _publish;
    private WpColorManagementSurfaceFeedbackV1 _feedback = null!;
    private Query? _query;
    private PlatformSurfaceColorVolume? _published;

    private WaylandColorVolumeFeedback(WaylandConnection connection, Action<PlatformSurfaceColorVolume?> publish)
    {
        _connection = connection;
        _publish = publish;
    }

    public static WaylandColorVolumeFeedback? TryCreate(WaylandConnection connection, WpColorManagerV1 manager,
        WlSurface surface, Action<PlatformSurfaceColorVolume?> publish)
    {
        // NWayland takes the listener at creation time, so the instance has to exist first.
        var result = new WaylandColorVolumeFeedback(connection, publish);
        try
        {
            result._feedback = manager.GetSurfaceFeedback(surface, new FeedbackListener(result), connection.Queue);
        }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Warning, "Wayland")?.Log(null,
                "Unable to create a color management surface feedback object: {0}", e);
            return null;
        }

        result.Refresh();
        return result;
    }

    /// <summary>
    /// Asks the compositor for the currently preferred image description. Nothing is published until
    /// the answer is complete, so the previously reported volume stays valid in the meantime.
    /// </summary>
    private void Refresh()
    {
        DestroyQuery();
        var query = new Query();
        _query = query;
        try
        {
            // get_preferred is not a destructor request, so an explicit target queue is fine here.
            query.Description = _feedback.GetPreferred(new DescriptionListener(this, query), _connection.Queue);
        }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Warning, "Wayland")?.Log(null,
                "Unable to query the preferred image description: {0}", e);
            Publish(null);
        }
    }

    private void OnDescriptionReady(Query query)
    {
        if (!ReferenceEquals(_query, query))
            return;

        try
        {
            query.Info = query.Description!.GetInformation(new InfoListener(this, query), _connection.Queue);
        }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Warning, "Wayland")?.Log(null,
                "Unable to read the preferred image description: {0}", e);
            Publish(null);
        }
    }

    private void OnDescriptionFailed(Query query, WpImageDescriptionV1.CauseEnum cause, string msg)
    {
        Logger.TryGet(LogEventLevel.Information, "Wayland")?.Log(null,
            "Compositor could not provide the preferred image description ({0}): {1}", cause, msg);
        if (ReferenceEquals(_query, query))
            Publish(null);
    }

    private void OnInfoDone(Query query, WpImageDescriptionInfoV1 info)
    {
        // `done` is a destructor event: the compositor is done with the object, only the client side
        // proxy is left to free.
        info.Dispose();
        if (!ReferenceEquals(_query, query))
            return;

        query.Info = null;
        // ICC based descriptions only carry an ICC profile, so there is nothing to report.
        Publish(query.Primary is { } primary && query.ReferenceWhiteNits is { } referenceWhite
                                             && query.Target is { } target
            ? new PlatformSurfaceColorVolume(primary, referenceWhite, target)
            : null);
    }

    private void Publish(PlatformSurfaceColorVolume? value)
    {
        if (_published == value)
            return;
        _published = value;
        _publish(value);
    }

    private void DestroyQuery()
    {
        if (_query is not { } query)
            return;
        _query = null;
        query.Info?.Dispose();
        query.Info = null;
        query.Description?.Destroy();
        query.Description?.Dispose();
        query.Description = null;
    }

    public void Dispose()
    {
        DestroyQuery();
        _feedback.Destroy();
        _feedback.Dispose();
    }

    /// <summary>
    /// One in-flight "what does the compositor prefer right now" round trip. A new one is started for
    /// every <c>preferred_changed</c>; results of superseded queries are dropped by identity.
    /// </summary>
    private sealed class Query
    {
        public WpImageDescriptionV1? Description;
        public WpImageDescriptionInfoV1? Info;
        public PlatformLuminanceRange? Primary;
        public double? ReferenceWhiteNits;
        public PlatformLuminanceRange? Target;
    }

    private sealed class FeedbackListener(WaylandColorVolumeFeedback p) : WpColorManagementSurfaceFeedbackV1.Listener
    {
        protected override void PreferredChanged(WpColorManagementSurfaceFeedbackV1 eventSender, uint identity)
            => p.Refresh();
    }

    private sealed class DescriptionListener(WaylandColorVolumeFeedback p, Query query) : WpImageDescriptionV1.Listener
    {
        protected override void Ready(WpImageDescriptionV1 eventSender, uint identity)
            => p.OnDescriptionReady(query);

        protected override void Failed(WpImageDescriptionV1 eventSender, WpImageDescriptionV1.CauseEnum cause,
            string msg)
            => p.OnDescriptionFailed(query, cause, msg);
    }

    private sealed class InfoListener(WaylandColorVolumeFeedback p, Query query) : WpImageDescriptionInfoV1.Listener
    {
        // Minimum luminances are scaled by 10000 to carry 4 decimals, the rest is plain cd/m².
        protected override void Luminances(WpImageDescriptionInfoV1 eventSender, uint minLum, uint maxLum,
            uint referenceLum)
        {
            query.Primary = new PlatformLuminanceRange(minLum / 10000.0, maxLum);
            query.ReferenceWhiteNits = referenceLum;
        }

        protected override void TargetLuminance(WpImageDescriptionInfoV1 eventSender, uint minLum, uint maxLum)
            => query.Target = new PlatformLuminanceRange(minLum / 10000.0, maxLum);

        protected override void Done(WpImageDescriptionInfoV1 eventSender)
            => p.OnInfoDone(query, eventSender);
    }
}
