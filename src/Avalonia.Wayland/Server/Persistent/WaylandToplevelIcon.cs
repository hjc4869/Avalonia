using System;
using System.Runtime.InteropServices;
using Avalonia.Platform;
using Avalonia.Wayland.Server.Interop;
using Avalonia.X11;
using NWayland.Protocols.Wayland;
using NWayland.Protocols.XdgToplevelIconV1;
using static Avalonia.Wayland.Server.Interop.UnsafeNativeMethods;

namespace Avalonia.Wayland.Server.Persistent;

/// <summary>
/// Immutable icon payload handed from the UI thread to the worker. <c>xdg_toplevel_icon_v1</c>
/// only accepts square wl_shm buffers, so a non-square source is centred inside a transparent
/// square. Pixels are tightly packed Bgra8888 premultiplied (matching <c>wl_shm</c>'s
/// little-endian ARGB8888), stride = <see cref="Size"/> * 4.
/// </summary>
sealed class WaylandIconData
{
    private WaylandIconData(int size, byte[] pixels)
    {
        Size = size;
        Pixels = pixels;
    }

    public int Size { get; }
    public byte[] Pixels { get; }

    /// <summary>
    /// Converts a platform icon into a square premultiplied BGRA buffer, or returns <c>null</c>
    /// when the icon can't be interpreted.
    /// </summary>
    public static WaylandIconData? TryCreate(IWindowIconImpl? icon)
    {
        // X11IconLoader is shared with the X11 backend (and reused for the DBus tray icon), so the
        // data arrives in NET_WM_ICON layout: [width, height, ARGB32 pixels…].
        if (icon is not X11IconData { Data: { Length: >= 2 } data })
            return null;

        var width = (int)data[0].ToUInt32();
        var height = (int)data[1].ToUInt32();
        if (width <= 0 || height <= 0 || data.Length < width * height + 2)
            return null;

        var size = Math.Max(width, height);
        var pixels = new byte[size * size * 4];
        var offsetX = (size - width) / 2;
        var offsetY = (size - height) / 2;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = data[2 + y * width + x].ToUInt32();
                var target = ((y + offsetY) * size + x + offsetX) * 4;
                pixels[target] = (byte)pixel;
                pixels[target + 1] = (byte)(pixel >> 8);
                pixels[target + 2] = (byte)(pixel >> 16);
                pixels[target + 3] = (byte)(pixel >> 24);
            }
        }

        return new WaylandIconData(size, pixels);
    }
}

/// <summary>
/// Worker-side owner of an <c>xdg_toplevel_icon_v1</c> and the wl_shm buffer backing it. The
/// protocol requires the buffer to outlive the icon object, so both are torn down together when
/// the icon is replaced or the connection goes away.
/// </summary>
sealed class WaylandToplevelIcon : IDisposable
{
    private XdgToplevelIconV1? _icon;
    private WlBuffer? _buffer;

    private WaylandToplevelIcon(XdgToplevelIconV1 icon, WlBuffer buffer)
    {
        _icon = icon;
        _buffer = buffer;
    }

    public XdgToplevelIconV1? Icon => _icon;

    public static WaylandToplevelIcon? TryCreate(XdgToplevelIconManagerV1 manager, WlShm shm,
        WaylandConnection connection, WaylandIconData data)
    {
        if (TryCreateShmBuffer(shm, data) is not { } buffer)
            return null;
        var icon = manager.CreateIcon(new IconListener(), connection.Queue);
        icon.AddBuffer(buffer, 1);
        return new WaylandToplevelIcon(icon, buffer);
    }

    private static WlBuffer? TryCreateShmBuffer(WlShm shm, WaylandIconData data)
    {
        var length = data.Pixels.Length;
        var fd = memfd_create("avalonia-wayland-icon", MFD_CLOEXEC);
        IntPtr map;
        if (fd == -1
            || ftruncate(fd, length) != 0
            || new IntPtr(-1) == (map =
                mmap(IntPtr.Zero, length, PROT_READ | PROT_WRITE, MAP_SHARED, fd, IntPtr.Zero)))
        {
            if (fd != -1)
                close(fd);
            return null;
        }

        try
        {
            Marshal.Copy(data.Pixels, 0, map, length);
            munmap(map, length);
            var pool = shm.CreatePool(fd, length);
            try
            {
                return pool.CreateBuffer(0, data.Size, data.Size, data.Size * 4,
                    WlShm.FormatEnum.Argb8888, new BufferListener());
            }
            finally
            {
                // NWayland's Dispose only destroys the local proxy; the generated method is
                // required to marshal the Wayland destructor request to the compositor.
                pool.Destroy();
            }
        }
        finally
        {
            close(fd);
        }
    }

    public void Dispose()
    {
        // Order matters: destroying the buffer while the icon still references it is a
        // 'no_buffer' protocol error.
        _icon?.Destroy();
        _icon = null;
        _buffer?.Destroy();
        _buffer = null;
    }

    // Neither interface emits events we care about (wl_buffer.release is explicitly unused for
    // icon buffers), but NWayland requires a listener instance.
    private sealed class IconListener : XdgToplevelIconV1.Listener;

    private sealed class BufferListener : WlBuffer.Listener;
}
