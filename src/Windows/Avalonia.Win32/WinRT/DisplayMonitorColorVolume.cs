using System;
using System.Runtime.InteropServices;
using Avalonia.Logging;
using Avalonia.MicroCom;
using Avalonia.Platform;
using Avalonia.Threading;
using MicroCom.Runtime;

namespace Avalonia.Win32.WinRT;

internal sealed unsafe class DisplayMonitorColorVolume : CallbackBase,
    IDisplayInformationChangedHandler, IDisposable
{
    internal const double ScRgbReferenceWhiteNits = 80.0;
    private static readonly Lazy<bool> s_dispatcherQueueInitialized = new(EnsureDispatcherQueue);

    private IDisplayInformation5? _displayInformation;
    private Action? _changed;
    private readonly long _eventToken;
    private bool _refreshPending;

    private DisplayMonitorColorVolume(IntPtr monitor, Action changed)
    {
        _changed = changed;
        _ = s_dispatcherQueueInitialized.Value;

        using var statics = NativeWinRTMethods.CreateActivationFactory<IDisplayInformationStaticsInterop>(
            "Windows.Graphics.Display.DisplayInformation");
        var iid = MicroComRuntime.GetGuidFor(typeof(IDisplayInformation5));
        _displayInformation = MicroComRuntime.CreateProxyFor<IDisplayInformation5>(
            statics.GetForMonitor(monitor, &iid), true);

        try
        {
            _eventToken = _displayInformation.AddAdvancedColorInfoChanged(this);
        }
        catch
        {
            _changed = null;
            _displayInformation.Dispose();
            _displayInformation = null;
            base.Dispose();
            throw;
        }
    }

    internal static DisplayMonitorColorVolume? TryCreate(IntPtr monitor, Action changed)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) || monitor == IntPtr.Zero)
            return null;

        try
        {
            return new DisplayMonitorColorVolume(monitor, changed);
        }
        catch (Exception exception)
        {
            Logger.TryGet(LogEventLevel.Warning, LogArea.Win32Platform)?.Log(null,
                "Unable to track DisplayInformation advanced color: {0}", exception);
            return null;
        }
    }

    internal PlatformSurfaceColorVolume? GetColorVolume()
    {
        if (_displayInformation is null)
            return null;

        try
        {
            using var info = _displayInformation.AdvancedColorInfo;
            return CreateColorVolume(info.MinLuminanceInNits, info.MaxLuminanceInNits,
                info.CurrentAdvancedColorKind == AdvancedColorKind.HighDynamicRange,
                info.SdrWhiteLevelInNits);
        }
        catch (Exception exception)
        {
            Logger.TryGet(LogEventLevel.Debug, LogArea.Win32Platform)?.Log(null,
                "Unable to read DisplayInformation advanced color: {0}", exception);
            return null;
        }
    }

    public void Invoke(IInspectable? sender, IInspectable? args)
    {
        if (_changed is null || _refreshPending)
            return;

        _refreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshPending = false;
            _changed?.Invoke();
        });
    }

    public new void Dispose()
    {
        _changed = null;
        var displayInformation = _displayInformation;
        _displayInformation = null;
        try
        {
            displayInformation?.RemoveAdvancedColorInfoChanged(_eventToken);
        }
        catch (Exception exception)
        {
            Logger.TryGet(LogEventLevel.Debug, LogArea.Win32Platform)?.Log(null,
                "Unable to unsubscribe DisplayInformation advanced color: {0}", exception);
        }
        finally
        {
            displayInformation?.Dispose();
            base.Dispose();
        }
    }

    internal static PlatformSurfaceColorVolume? CreateColorVolume(
        float minimumNits, float maximumNits, bool hdrActive, double? sdrWhiteLevelInNits)
    {
        return CreateColorVolume(PlatformLuminanceRange.Normalize(new(minimumNits, maximumNits)),
            new DisplayColorState(hdrActive, sdrWhiteLevelInNits));
    }

    internal static PlatformSurfaceColorVolume? CreateColorVolume(
        PlatformLuminanceRange? target, DisplayColorState state)
    {
        if (state.HdrActive)
        {
            var referenceWhite = state.SdrWhiteLevelInNits is > 0 and var reportedWhite &&
                double.IsFinite(reportedWhite) ? reportedWhite : (double?)null;
            return new PlatformSurfaceColorVolume(
                new PlatformLuminanceRange(0, ScRgbReferenceWhiteNits), referenceWhite, target,
                PlatformTransferFunction.Pq,
                SurfaceNitsPerUnit: ScRgbReferenceWhiteNits)
            {
                ReferenceWhiteScale = referenceWhite / ScRgbReferenceWhiteNits,
                HeadroomRatio = referenceWhite is { } white && target?.MaximumNits is > 0 and var peak
                    ? Math.Max(1, peak / white) : null,
                ToneMapping = PlatformToneMappingMode.Client,
                LuminanceBasis = PlatformLuminanceBasis.DisplayReported
            }.Normalize();
        }

        return new PlatformSurfaceColorVolume(Transfer: PlatformTransferFunction.Srgb)
        {
            ReferenceWhiteScale = 1,
            HeadroomRatio = 1,
            ToneMapping = PlatformToneMappingMode.Client
        }.Normalize();
    }

    internal readonly record struct DisplayColorState(bool HdrActive, double? SdrWhiteLevelInNits);

    private static bool EnsureDispatcherQueue()
    {
        using var statics = NativeWinRTMethods.CreateActivationFactory<IDispatcherQueueStatics>(
            "Windows.System.DispatcherQueue");
        using var queue = statics.ForCurrentThread;
        if (queue is null)
            _ = new DispatcherQueueLifetime();
        return true;
    }

    private sealed class DispatcherQueueLifetime : CallbackBase, IAsyncActionCompletedHandler
    {
        private readonly IDispatcherQueueController _controller;
        private DispatcherFrame? _shutdownFrame;

        public DispatcherQueueLifetime()
        {
            _controller = MicroComRuntime.CreateProxyFor<IDispatcherQueueController>(
                NativeWinRTMethods.CreateDispatcherQueueController(new()
                {
                    dwSize = Marshal.SizeOf<NativeWinRTMethods.DispatcherQueueOptions>(),
                    threadType = NativeWinRTMethods.DISPATCHERQUEUE_THREAD_TYPE.DQTYPE_THREAD_CURRENT,
                    apartmentType = NativeWinRTMethods.DISPATCHERQUEUE_THREAD_APARTMENTTYPE.DQTAT_COM_NONE
                }), true);
            Dispatcher.UIThread.ShutdownStarted += OnShutdownStarted;
        }

        private void OnShutdownStarted(object? sender, EventArgs args)
        {
            Dispatcher.UIThread.ShutdownStarted -= OnShutdownStarted;
            try
            {
                _shutdownFrame = new DispatcherFrame(false);
                using var operation = _controller.ShutdownQueueAsync();
                operation.SetCompleted(this);
                Dispatcher.UIThread.PushFrame(_shutdownFrame);
                operation.GetResults();
            }
            catch (Exception exception)
            {
                Logger.TryGet(LogEventLevel.Warning, LogArea.Win32Platform)?.Log(null,
                    "Unable to shut down the display dispatcher queue: {0}", exception);
            }
            finally
            {
                _controller.Dispose();
                base.Dispose();
            }
        }

        public void Invoke(IAsyncAction? asyncInfo, AsyncStatus asyncStatus)
        {
            if (_shutdownFrame is { } frame)
                frame.Continue = false;
        }
    }
}