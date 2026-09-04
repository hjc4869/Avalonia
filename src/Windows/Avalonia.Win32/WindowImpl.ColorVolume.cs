using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Win32.WinRT;
using static Avalonia.Win32.Interop.UnmanagedMethods;

namespace Avalonia.Win32;

internal partial class WindowImpl : IPlatformSurfaceColorVolumeFeature,
    EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfoWithColorVolume
{
    private IntPtr _colorVolumeMonitor;
    private DisplayMonitorColorVolume? _colorVolumeSubscription;
    private bool _colorVolumeTrackingStarted;
    private ColorVolumeState _colorVolumeState = new(null);

    public PlatformSurfaceColorVolume? PreferredColorVolume =>
        Volatile.Read(ref _colorVolumeState).Value;

    public event EventHandler? PreferredColorVolumeChanged;

    private void StartPreferredColorVolumeTracking()
    {
        _colorVolumeTrackingStarted = true;
        RefreshPreferredColorVolume(force: true, checkDynamicState: true);
    }

    private void RefreshPreferredColorVolume(bool force = false, bool checkDynamicState = false)
    {
        if (!_colorVolumeTrackingStarted || _hwnd == IntPtr.Zero)
            return;

        var monitor = GetPreferredColorVolumeMonitor();
        if (force || monitor != _colorVolumeMonitor || _colorVolumeSubscription is null)
        {
            _colorVolumeSubscription?.Dispose();
            _colorVolumeMonitor = monitor;
            _colorVolumeSubscription = DisplayMonitorColorVolume.TryCreate(monitor,
                () => RefreshPreferredColorVolume(checkDynamicState: true));
        }
        else if (!checkDynamicState)
        {
            return;
        }

        UpdatePreferredColorVolume(_colorVolumeSubscription?.GetColorVolume());
    }

    private IntPtr GetPreferredColorVolumeMonitor()
    {
        // Windows defines a window's main display as the one containing its center. Unlike
        // MonitorFromWindow's largest-intersection rule, this also gives a deterministic handoff
        // while a large window straddles two displays.
        if (_lastWindowState != WindowState.Minimized && GetWindowRect(_hwnd, out var rect))
        {
            var center = new POINT
            {
                X = (int)(((long)rect.left + rect.right) / 2),
                Y = (int)(((long)rect.top + rect.bottom) / 2)
            };
            return MonitorFromPoint(center, MONITOR.MONITOR_DEFAULTTONEAREST);
        }

        return _colorVolumeMonitor != IntPtr.Zero
            ? _colorVolumeMonitor
            : MonitorFromWindow(_hwnd, MONITOR.MONITOR_DEFAULTTONEAREST);
    }

    private void UpdatePreferredColorVolume(PlatformSurfaceColorVolume? colorVolume)
    {
        if (PreferredColorVolume == colorVolume)
            return;

        Volatile.Write(ref _colorVolumeState, new ColorVolumeState(colorVolume));
        PreferredColorVolumeChanged?.Invoke(this, EventArgs.Empty);

        // The frame currently on screen was rendered for the old reference white / peak.
        Paint?.Invoke(new Rect(ClientSize));
    }

    private void DisposePreferredColorVolumeTracking()
    {
        _colorVolumeTrackingStarted = false;
        _colorVolumeSubscription?.Dispose();
        _colorVolumeSubscription = null;
        _colorVolumeMonitor = IntPtr.Zero;
    }

    private sealed class ColorVolumeState(PlatformSurfaceColorVolume? value)
    {
        public PlatformSurfaceColorVolume? Value { get; } = value;
    }
}