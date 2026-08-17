using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Logging;
using Avalonia.MicroCom;
using Avalonia.Platform;
using MicroCom.Runtime;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using static Avalonia.Win32.Interop.UnmanagedMethods;

namespace Avalonia.Win32.WinRT;

/// <summary>
/// Resolves a Win32 monitor to <c>Windows.Devices.Display.DisplayMonitor</c> and combines its
/// physical luminance range with the user's current HDR SDR-white setting.
/// </summary>
internal static unsafe class DisplayMonitorColorVolume
{
    internal const double ScRgbReferenceWhiteNits = 80.0;
    private const DISPLAYCONFIG_DEVICE_INFO_TYPE GetAdvancedColorInfo2 =
        (DISPLAYCONFIG_DEVICE_INFO_TYPE)15;

    private static readonly Lazy<IDisplayMonitorStatics?> s_statics = new(CreateStatics);

    internal static IDisposable? QueryLuminance(
        DisplayConfigTarget target, Action<PlatformLuminanceRange?> completed)
    {
        if (s_statics.Value is not { } statics)
        {
            completed(null);
            return null;
        }

        try
        {
            return new QueryOperation(statics, target.MonitorDevicePath, completed);
        }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Debug, LogArea.Win32Platform)?.Log(null,
                "Unable to query DisplayMonitor luminance: {0}", e);
            completed(null);
            return null;
        }
    }

    internal static PlatformSurfaceColorVolume? CreateColorVolume(
        float minimumNits, float maximumNits, bool hdrActive, uint? sdrWhiteLevel)
    {
        if (CreateLuminanceRange(minimumNits, maximumNits) is not { } target)
            return null;

        return CreateColorVolume(target, new DisplayColorState(hdrActive, sdrWhiteLevel));
    }

    internal static PlatformSurfaceColorVolume? CreateColorVolume(
        PlatformLuminanceRange target, DisplayColorState state)
    {
        var maximumNits = target.MaximumNits;

        // DISPLAYCONFIG_SDR_WHITE_LEVEL is available when the output is in HDR mode. In scRGB,
        // numeric 1.0 is always defined as 80 nits, so SDR content needs to be scaled from that
        // primary white to the user-selected reference white. On an SDR output, FP16 composition
        // is display-referred and 1.0 already represents the display's white; use the target range
        // for both primary and reference so no adjustment is applied. The native value is a
        // thousandth of the 80-nit scRGB reference, not a thousandth of a nit.
        if (state.HdrActive)
        {
            if (state.SdrWhiteLevel is not > 0)
                return null;

            var referenceWhite = state.SdrWhiteLevel.Value / 1000.0 * ScRgbReferenceWhiteNits;
            return new PlatformSurfaceColorVolume(
                new PlatformLuminanceRange(0, ScRgbReferenceWhiteNits), referenceWhite, target);
        }

        return new PlatformSurfaceColorVolume(target, maximumNits, target);
    }

    internal static bool TryGetDisplayColorState(
        DisplayConfigTarget target, out DisplayColorState state)
    {
        state = default;
        if (!TryGetHdrActive(target.AdapterId, target.TargetId, out var hdrActive))
            return false;

        var sdrWhiteLevel = hdrActive
            ? GetSdrWhiteLevel(target.AdapterId, target.TargetId)
            : null;

        // Avoid publishing a transient null while Settings is applying a new HDR white level.
        if (hdrActive && sdrWhiteLevel is null)
            return false;

        state = new DisplayColorState(hdrActive, sdrWhiteLevel);
        return true;
    }

    private static PlatformLuminanceRange? CreateLuminanceRange(
        float minimumNits, float maximumNits)
    {
        if (!float.IsFinite(minimumNits) || !float.IsFinite(maximumNits) ||
            minimumNits < 0 || maximumNits <= 0 || minimumNits > maximumNits)
        {
            return null;
        }

        return new PlatformLuminanceRange(minimumNits, maximumNits);
    }

    private static IDisplayMonitorStatics? CreateStatics()
    {
        if (!WinRTApiInformation.IsTypePresent("Windows.Devices.Display.DisplayMonitor"))
            return null;

        try
        {
            return NativeWinRTMethods.CreateActivationFactory<IDisplayMonitorStatics>(
                "Windows.Devices.Display.DisplayMonitor");
        }
        catch (Exception e)
        {
            Logger.TryGet(LogEventLevel.Warning, LogArea.Win32Platform)?.Log(null,
                "Unable to create the DisplayMonitor activation factory: {0}", e);
            return null;
        }
    }

    internal static bool TryGetDisplayConfigTarget(IntPtr hMonitor, out DisplayConfigTarget target)
    {
        target = default;

        var monitorInfo = MONITORINFOEX.Create();
        if (!PInvoke.GetMonitorInfo(new HMONITOR(hMonitor), (MONITORINFO*)&monitorInfo))
            return false;

        var gdiDeviceName = monitorInfo.szDevice.ToString();

        // Display topology can change between the size and query calls. Retry rather than treating
        // the transient ERROR_INSUFFICIENT_BUFFER as a permanent lack of color information.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (PInvoke.GetDisplayConfigBufferSizes(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS,
                    out var pathCount, out var modeCount) != WIN32_ERROR.NO_ERROR)
            {
                return false;
            }

            var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
            var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
            var result = PInvoke.QueryDisplayConfig(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS,
                ref pathCount, paths, ref modeCount, modes);

            if (result == WIN32_ERROR.ERROR_INSUFFICIENT_BUFFER)
                continue;
            if (result != WIN32_ERROR.NO_ERROR)
                return false;

            for (var i = 0; i < pathCount; i++)
            {
                var path = paths[i];
                var sourceName = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
                {
                    header =
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME,
                        size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME),
                        adapterId = path.targetInfo.adapterId,
                        id = path.sourceInfo.id
                    }
                };

                var sourceResult = PInvoke.DisplayConfigGetDeviceInfo(ref sourceName.header);
                var sourceDeviceName = sourceName.viewGdiDeviceName.ToString();
                if (sourceResult != 0 ||
                    !string.Equals(sourceDeviceName, gdiDeviceName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var targetName = new DISPLAYCONFIG_TARGET_DEVICE_NAME
                {
                    header =
                    {
                        type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                        size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME),
                        adapterId = path.targetInfo.adapterId,
                        id = path.targetInfo.id
                    }
                };

                if (PInvoke.DisplayConfigGetDeviceInfo(ref targetName.header) != 0)
                    continue;

                var monitorDevicePath = targetName.monitorDevicePath.ToString();
                if (string.IsNullOrEmpty(monitorDevicePath))
                    continue;

                target = new DisplayConfigTarget(
                    monitorDevicePath, path.targetInfo.adapterId, path.targetInfo.id);
                return true;
            }

            return false;
        }

        return false;
    }

    private static uint? GetSdrWhiteLevel(LUID adapterId, uint targetId)
    {
        var whiteLevel = new DISPLAYCONFIG_SDR_WHITE_LEVEL
        {
            header =
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SDR_WHITE_LEVEL,
                size = (uint)sizeof(DISPLAYCONFIG_SDR_WHITE_LEVEL),
                adapterId = adapterId,
                id = targetId
            }
        };

        return PInvoke.DisplayConfigGetDeviceInfo(ref whiteLevel.header) == 0 && whiteLevel.SDRWhiteLevel > 0
            ? whiteLevel.SDRWhiteLevel
            : null;
    }

    private static bool TryGetHdrActive(LUID adapterId, uint targetId, out bool hdrActive)
    {
        hdrActive = false;

        // GET_ADVANCED_COLOR_INFO_2 distinguishes WCG (display-referred luminance) from HDR
        // (scene-referred luminance). Query it by its SDK value so the assembly can still run on
        // older Windows versions, where DisplayConfigGetDeviceInfo simply rejects the request.
        var info2 = new DisplayConfigAdvancedColorInfo2
        {
            Header =
            {
                type = GetAdvancedColorInfo2,
                size = (uint)sizeof(DisplayConfigAdvancedColorInfo2),
                adapterId = adapterId,
                id = targetId
            }
        };

        if (PInvoke.DisplayConfigGetDeviceInfo(ref info2.Header) == 0)
        {
            hdrActive = info2.ActiveColorMode == AdvancedColorMode.Hdr;
            return true;
        }

        // Before Windows 11 there was no SDR WCG mode, so the original advanced-color enabled bit
        // unambiguously means that HDR is active.
        var info = new DisplayConfigAdvancedColorInfo
        {
            Header =
            {
                type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO,
                size = (uint)sizeof(DisplayConfigAdvancedColorInfo),
                adapterId = adapterId,
                id = targetId
            }
        };

        if (PInvoke.DisplayConfigGetDeviceInfo(ref info.Header) != 0)
            return false;

        hdrActive = (info.Value & 0b10) != 0;
        return true;
    }

    internal readonly record struct DisplayConfigTarget(
        string MonitorDevicePath, LUID AdapterId, uint TargetId);

    internal readonly record struct DisplayColorState(bool HdrActive, uint? SdrWhiteLevel);

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigAdvancedColorInfo
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        public uint Value;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigAdvancedColorInfo2
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER Header;
        public uint Value;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
        public AdvancedColorMode ActiveColorMode;
    }

    private enum AdvancedColorMode
    {
        Sdr,
        Wcg,
        Hdr
    }

    private sealed class QueryOperation : CallbackBase,
        IDisplayMonitorAsyncOperationCompletedHandler, IDisposable
    {
        private Action<PlatformLuminanceRange?>? _completed;
        private IDisplayMonitorAsyncOperation? _operation;

        public QueryOperation(IDisplayMonitorStatics statics, string monitorDevicePath,
            Action<PlatformLuminanceRange?> completed)
        {
            _completed = completed;

            using var id = new HStringInterop(monitorDevicePath);
            try
            {
                _operation = statics.FromInterfaceIdAsync(id.Handle);
                _operation.SetCompleted(this);
            }
            catch
            {
                Interlocked.Exchange(ref _operation, null)?.Dispose();
                base.Dispose();
                throw;
            }
        }

        public void Invoke(IDisplayMonitorAsyncOperation? asyncInfo, AsyncStatus asyncStatus)
        {
            var completed = Interlocked.Exchange(ref _completed, null);
            if (completed is null)
                return;

            PlatformLuminanceRange? result = null;
            try
            {
                if (asyncStatus == AsyncStatus.Completed && asyncInfo is not null)
                {
                    using var monitor = asyncInfo.Results;
                    if (monitor is not null)
                    {
                        result = CreateLuminanceRange(
                            monitor.MinLuminanceInNits, monitor.MaxLuminanceInNits);
                    }
                }
                else if (asyncStatus == AsyncStatus.Error)
                {
                    // GetResults preserves the native HRESULT, which is more useful than the status alone.
                    asyncInfo?.Results?.Dispose();
                }
            }
            catch (Exception e)
            {
                Logger.TryGet(LogEventLevel.Debug, LogArea.Win32Platform)?.Log(null,
                    "Unable to read DisplayMonitor luminance: {0}", e);
            }
            finally
            {
                Interlocked.Exchange(ref _operation, null)?.Dispose();
            }

            try
            {
                completed(result);
            }
            finally
            {
                base.Dispose();
            }
        }

        public new void Dispose()
        {
            Interlocked.Exchange(ref _completed, null);
            Interlocked.Exchange(ref _operation, null)?.Dispose();
            base.Dispose();
        }
    }
}