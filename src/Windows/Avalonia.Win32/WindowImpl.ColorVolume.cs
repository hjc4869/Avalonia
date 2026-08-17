using System;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.Win32.WinRT;
using static Avalonia.Win32.Interop.UnmanagedMethods;
using DisplayColorState = Avalonia.Win32.WinRT.DisplayMonitorColorVolume.DisplayColorState;
using DisplayConfigTarget = Avalonia.Win32.WinRT.DisplayMonitorColorVolume.DisplayConfigTarget;

namespace Avalonia.Win32;

internal partial class WindowImpl : IPlatformSurfaceColorVolumeFeature,
    EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfoWithColorVolume
{
    private static readonly TimeSpan s_colorVolumePollInterval = TimeSpan.FromMilliseconds(250);
    private static IDisposable? s_colorVolumePoll;

    private IntPtr _colorVolumeMonitor;
    private DisplayConfigTarget? _colorVolumeTarget;
    private DisplayColorState? _colorVolumeDynamicState;
    private DisplayColorState? _colorVolumeCandidateState;
    private int _colorVolumeCandidateObservations;
    private PlatformLuminanceRange? _colorVolumeTargetLuminance;
    private IDisposable? _colorVolumeQuery;
    private int _colorVolumeQueryGeneration;
    private ColorVolumeState _colorVolumeState = new(null);

    public PlatformSurfaceColorVolume? PreferredColorVolume =>
        Volatile.Read(ref _colorVolumeState).Value;

    public event EventHandler? PreferredColorVolumeChanged;

    private void StartPreferredColorVolumeTracking()
    {
        RefreshPreferredColorVolume(force: true, checkDynamicState: true);

        lock (s_instances)
        {
            s_colorVolumePoll ??= DispatcherTimer.Run(PollPreferredColorVolumes,
                s_colorVolumePollInterval, DispatcherPriority.Background);
        }
    }

    private static bool PollPreferredColorVolumes()
    {
        WindowImpl[] instances;
        lock (s_instances)
        {
            if (s_instances.Count == 0)
            {
                s_colorVolumePoll = null;
                return false;
            }

            instances = s_instances.ToArray();
        }

        foreach (var instance in instances)
            instance.PollPreferredColorVolume();

        return true;
    }

    private void PollPreferredColorVolume()
    {
        if (_hwnd == IntPtr.Zero || _lastWindowState == WindowState.Minimized ||
            !IsWindowVisible(_hwnd))
        {
            return;
        }

        RefreshPreferredColorVolume(checkDynamicState: true);
    }

    private void RefreshPreferredColorVolume(bool force = false, bool checkDynamicState = false)
    {
        if (_hwnd == IntPtr.Zero)
            return;

        var monitor = GetPreferredColorVolumeMonitor();
        var monitorChanged = monitor != _colorVolumeMonitor;
        if (!force && !monitorChanged && !checkDynamicState)
            return;

        var targetChanged = false;
        DisplayConfigTarget target;

        if (force || monitorChanged || _colorVolumeTarget is not { } cachedTarget)
        {
            _colorVolumeMonitor = monitor;
            if (!DisplayMonitorColorVolume.TryGetDisplayConfigTarget(monitor, out target))
            {
                ResetPreferredColorVolumeTarget();
                return;
            }

            targetChanged = _colorVolumeTarget is not { } oldTarget ||
                !IsSameDisplayTarget(oldTarget, target);
            _colorVolumeTarget = target;
        }
        else
        {
            target = cachedTarget;
        }

        if (!DisplayMonitorColorVolume.TryGetDisplayColorState(target, out var dynamicState))
        {
            // The display topology might have changed without replacing the HMONITOR. Remap once
            // before waiting for the next poll.
            if (!DisplayMonitorColorVolume.TryGetDisplayConfigTarget(monitor, out var remappedTarget) ||
                !DisplayMonitorColorVolume.TryGetDisplayColorState(remappedTarget, out dynamicState))
            {
                return;
            }

            targetChanged |= !IsSameDisplayTarget(target, remappedTarget);
            target = remappedTarget;
            _colorVolumeTarget = target;
        }

        var dynamicStateChanged = false;

        if (targetChanged)
        {
            _colorVolumeTargetLuminance = null;
            _colorVolumeDynamicState = null;
            _colorVolumeCandidateState = null;
            _colorVolumeCandidateObservations = 0;
        }

        if (_colorVolumeCandidateState == dynamicState)
        {
            _colorVolumeCandidateObservations =
                Math.Min(2, _colorVolumeCandidateObservations + 1);
        }
        else
        {
            _colorVolumeCandidateState = dynamicState;
            _colorVolumeCandidateObservations = 1;
        }

        if (_colorVolumeCandidateObservations >= 2)
        {
            dynamicStateChanged = _colorVolumeDynamicState != dynamicState;
            _colorVolumeDynamicState = dynamicState;
        }
        else
        {
            dynamicStateChanged = false;
        }

        if (dynamicStateChanged && _colorVolumeTargetLuminance is { } targetLuminance)
        {
            UpdatePreferredColorVolume(
                DisplayMonitorColorVolume.CreateColorVolume(targetLuminance, dynamicState));
        }

        if (force || monitorChanged || targetChanged || _colorVolumeTargetLuminance is null)
            BeginPreferredColorVolumeQuery(target);
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

    private static bool IsSameDisplayTarget(DisplayConfigTarget left, DisplayConfigTarget right) =>
        left.TargetId == right.TargetId && left.AdapterId.Equals(right.AdapterId) &&
        string.Equals(left.MonitorDevicePath, right.MonitorDevicePath,
            StringComparison.OrdinalIgnoreCase);

    private void BeginPreferredColorVolumeQuery(DisplayConfigTarget target)
    {
        var generation = ++_colorVolumeQueryGeneration;
        _colorVolumeQuery?.Dispose();
        _colorVolumeQuery = DisplayMonitorColorVolume.QueryLuminance(target, luminance =>
        {
            Dispatcher.UIThread.Post(() => CompletePreferredColorVolumeQuery(generation, luminance));
        });
    }

    private void CompletePreferredColorVolumeQuery(int generation, PlatformLuminanceRange? luminance)
    {
        if (generation != _colorVolumeQueryGeneration || _hwnd == IntPtr.Zero)
            return;

        _colorVolumeQuery?.Dispose();
        _colorVolumeQuery = null;

        _colorVolumeTargetLuminance = luminance;
        if (luminance is not { } target)
        {
            UpdatePreferredColorVolume(null);
        }
        else if (_colorVolumeDynamicState is { } dynamicState)
        {
            UpdatePreferredColorVolume(
                DisplayMonitorColorVolume.CreateColorVolume(target, dynamicState));
        }
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

    private void ResetPreferredColorVolumeTarget()
    {
        _colorVolumeTarget = null;
        _colorVolumeDynamicState = null;
        _colorVolumeCandidateState = null;
        _colorVolumeCandidateObservations = 0;
        _colorVolumeTargetLuminance = null;
        ++_colorVolumeQueryGeneration;
        _colorVolumeQuery?.Dispose();
        _colorVolumeQuery = null;
        UpdatePreferredColorVolume(null);
    }

    private void DisposePreferredColorVolumeTracking()
    {
        ++_colorVolumeQueryGeneration;
        _colorVolumeQuery?.Dispose();
        _colorVolumeQuery = null;
        _colorVolumeMonitor = IntPtr.Zero;
        _colorVolumeTarget = null;
        _colorVolumeDynamicState = null;
        _colorVolumeCandidateState = null;
        _colorVolumeCandidateObservations = 0;
        _colorVolumeTargetLuminance = null;

        IDisposable? poll = null;
        lock (s_instances)
        {
            if (s_instances.Count == 0)
            {
                poll = s_colorVolumePoll;
                s_colorVolumePoll = null;
            }
        }

        poll?.Dispose();
    }

    private sealed class ColorVolumeState(PlatformSurfaceColorVolume? value)
    {
        public PlatformSurfaceColorVolume? Value { get; } = value;
    }
}