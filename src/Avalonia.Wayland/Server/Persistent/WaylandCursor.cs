using System.Collections.Generic;
using Avalonia.Input;
using Avalonia.SourceGenerator;
using Avalonia.Wayland.Server.Transient;
using NWayland.Protocols.CursorShapeV1;
using NWayland.Protocols.Wayland;

namespace Avalonia.Wayland.Server.Persistent;

/// <summary>The surface + hotspot to hand to <c>wl_pointer.set_cursor</c>.</summary>
[DefinitelyNotARecord]
readonly partial struct WaylandCursorImage(WlSurface Surface, int HotspotX, int HotspotY);

/// <summary>
/// Worker-side representation of a pointer cursor. The UI thread only ever holds the generated
/// <c>WaylandCursorProxy</c> wrapper (see <see cref="IWaylandCursor"/>); resolving the actual
/// <c>wl_surface</c> happens here, on the worker thread.
/// </summary>
abstract class WaylandCursor : IWaylandCursor
{
    /// <summary>
    /// The <c>cursor-shape-v1</c> shape equivalent to this cursor, or <c>null</c> when it has no
    /// named equivalent and has to be drawn from a <c>wl_surface</c> via <see cref="Resolve"/>.
    /// </summary>
    public virtual WpCursorShapeDeviceV1.ShapeEnum? Shape => null;

    /// <summary>
    /// Resolves the surface + hotspot for <c>wl_pointer.set_cursor</c> on the worker thread, or
    /// <c>null</c> to hide the pointer.
    /// </summary>
    public abstract WaylandCursorImage? Resolve(WaylandGlobals globals);

    /// <inheritdoc/>
    public abstract void Destroy();
}

/// <summary>
/// A standard themed cursor. Stateless apart from the requested type — it owns no wl_surface and
/// resolves the themed surface on demand from <see cref="WaylandCursorManager"/>, so it doesn't
/// need to be a persistent object.
/// </summary>
sealed class WaylandStandardCursor(StandardCursorType cursorType) : WaylandCursor
{
    // Shapes come from the CSS cursor names, so they line up with the theme names in
    // WaylandCursorManager. Types with no named equivalent are absent and fall back to a surface.
    private static readonly Dictionary<StandardCursorType, WpCursorShapeDeviceV1.ShapeEnum> s_shapes = new()
    {
        { StandardCursorType.Arrow, WpCursorShapeDeviceV1.ShapeEnum.Default },
        { StandardCursorType.Ibeam, WpCursorShapeDeviceV1.ShapeEnum.Text },
        { StandardCursorType.Wait, WpCursorShapeDeviceV1.ShapeEnum.Wait },
        { StandardCursorType.Cross, WpCursorShapeDeviceV1.ShapeEnum.Crosshair },
        { StandardCursorType.SizeWestEast, WpCursorShapeDeviceV1.ShapeEnum.EwResize },
        { StandardCursorType.SizeNorthSouth, WpCursorShapeDeviceV1.ShapeEnum.NsResize },
        { StandardCursorType.SizeAll, WpCursorShapeDeviceV1.ShapeEnum.AllScroll },
        { StandardCursorType.No, WpCursorShapeDeviceV1.ShapeEnum.NotAllowed },
        { StandardCursorType.Hand, WpCursorShapeDeviceV1.ShapeEnum.Pointer },
        { StandardCursorType.AppStarting, WpCursorShapeDeviceV1.ShapeEnum.Progress },
        { StandardCursorType.Help, WpCursorShapeDeviceV1.ShapeEnum.Help },
        { StandardCursorType.TopSide, WpCursorShapeDeviceV1.ShapeEnum.NResize },
        { StandardCursorType.BottomSide, WpCursorShapeDeviceV1.ShapeEnum.SResize },
        { StandardCursorType.LeftSide, WpCursorShapeDeviceV1.ShapeEnum.WResize },
        { StandardCursorType.RightSide, WpCursorShapeDeviceV1.ShapeEnum.EResize },
        { StandardCursorType.TopLeftCorner, WpCursorShapeDeviceV1.ShapeEnum.NwResize },
        { StandardCursorType.TopRightCorner, WpCursorShapeDeviceV1.ShapeEnum.NeResize },
        { StandardCursorType.BottomLeftCorner, WpCursorShapeDeviceV1.ShapeEnum.SwResize },
        { StandardCursorType.BottomRightCorner, WpCursorShapeDeviceV1.ShapeEnum.SeResize },
        { StandardCursorType.DragMove, WpCursorShapeDeviceV1.ShapeEnum.Grabbing },
        { StandardCursorType.DragCopy, WpCursorShapeDeviceV1.ShapeEnum.Copy },
        { StandardCursorType.DragLink, WpCursorShapeDeviceV1.ShapeEnum.Alias },
    };

    // StandardCursorType.None must stay unmapped: the protocol has no "hidden" shape, hiding the
    // pointer needs wl_pointer.set_cursor with a null surface.
    internal static WpCursorShapeDeviceV1.ShapeEnum? GetShape(StandardCursorType type)
        => s_shapes.TryGetValue(type, out var shape) ? shape : null;

    public override WpCursorShapeDeviceV1.ShapeEnum? Shape => GetShape(cursorType);

    public override WaylandCursorImage? Resolve(WaylandGlobals globals)
        => globals.CursorManager.GetCursor(cursorType) is { } c
            ? new WaylandCursorImage(c.Surface, c.HotspotX, c.HotspotY)
            : null;

    // Nothing to release — the themed surfaces are owned by WaylandCursorManager.
    public override void Destroy()
    {
    }
}
