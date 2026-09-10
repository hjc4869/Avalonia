using System;
using System.Runtime.InteropServices;
using Avalonia.Logging;
using Avalonia.MicroCom;
using Avalonia.Threading;
using MicroCom.Runtime;
using static Avalonia.Win32.Interop.UnmanagedMethods;

namespace Avalonia.Win32.Input;

internal sealed unsafe class WindowsTouchpad : IDisposable
{
    private readonly IntPtr _hwnd;
    private readonly Action<double, Vector, PixelPoint> _onGesture;
    private readonly DispatcherTimer _timer;
    private IDirectManipulationManager? _manager;
    private IDirectManipulationUpdateManager? _updateManager;
    private IDirectManipulationViewport? _viewport;
    private uint? _handlerCookie;
    private PixelPoint _position;
    private double _scale = 1;
    private Vector _translation;
    private DIRECTMANIPULATION_STATUS _status;
    private bool _interacting;
    private bool _resetting;
    private bool _enabled;
    private bool _disposed;

    private WindowsTouchpad(IntPtr hwnd, Action<double, Vector, PixelPoint> onGesture)
    {
        _hwnd = hwnd;
        _onGesture = onGesture;
        _timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(8)
        };
        _timer.Tick += OnTick;
    }

    public static WindowsTouchpad? TryCreate(IntPtr hwnd, Action<double, Vector, PixelPoint> onGesture)
    {
        if (Win32Platform.WindowsVersion < PlatformConstants.Windows8_1)
            return null;

        var touchpad = new WindowsTouchpad(hwnd, onGesture);
        try
        {
            touchpad.Initialize();
            return touchpad;
        }
        catch (COMException exception)
        {
            touchpad.OnFailure(exception);
            return null;
        }
    }

    private void Initialize()
    {
        var clsid = new Guid("54E211B6-3650-4F75-8334-FA359598E1C5");
        var managerId = MicroComRuntime.GetGuidFor(typeof(IDirectManipulationManager));
        _manager = CreateInstance<IDirectManipulationManager>(clsid, managerId);

        var updateId = MicroComRuntime.GetGuidFor(typeof(IDirectManipulationUpdateManager));
        void* updateManager = null;
        ThrowIfFailed(_manager.GetUpdateManager(&updateId, &updateManager), nameof(IDirectManipulationManager.GetUpdateManager));
        _updateManager = MicroComRuntime.CreateProxyFor<IDirectManipulationUpdateManager>(
            (IntPtr)updateManager, true);
        var viewportId = MicroComRuntime.GetGuidFor(typeof(IDirectManipulationViewport));
        void* viewport = null;
        ThrowIfFailed(_manager.CreateViewport(null, _hwnd, &viewportId, &viewport), nameof(IDirectManipulationManager.CreateViewport));
        _viewport = MicroComRuntime.CreateProxyFor<IDirectManipulationViewport>(
            (IntPtr)viewport, true);

        ThrowIfFailed(_viewport.ActivateConfiguration(DIRECTMANIPULATION_CONFIGURATION.INTERACTION |
            DIRECTMANIPULATION_CONFIGURATION.TRANSLATION_X |
            DIRECTMANIPULATION_CONFIGURATION.TRANSLATION_Y |
            DIRECTMANIPULATION_CONFIGURATION.SCALING |
            DIRECTMANIPULATION_CONFIGURATION.TRANSLATION_INERTIA), nameof(IDirectManipulationViewport.ActivateConfiguration));
        ThrowIfFailed(_viewport.SetViewportOptions(DIRECTMANIPULATION_VIEWPORT_OPTIONS.MANUALUPDATE |
            DIRECTMANIPULATION_VIEWPORT_OPTIONS.EXPLICITHITTEST |
            DIRECTMANIPULATION_VIEWPORT_OPTIONS.DISABLEPIXELSNAPPING), nameof(IDirectManipulationViewport.SetViewportOptions));
        using (var handler = new Handler(this))
        {
            uint cookie = 0;
            ThrowIfFailed(_viewport.AddEventHandler(_hwnd, handler, &cookie), nameof(IDirectManipulationViewport.AddEventHandler));
            _handlerCookie = cookie;
        }

        ResizeCore();
        ThrowIfFailed(_manager.Activate(_hwnd), nameof(IDirectManipulationManager.Activate));
        ThrowIfFailed(_viewport.Enable(), nameof(IDirectManipulationViewport.Enable));
        _enabled = true;
        Update();
    }

    private static void ThrowIfFailed(int result, string operation)
    {
        if (result < 0)
            throw new COMException($"Direct Manipulation {operation} failed.", result);
    }

    private void Update() => ThrowIfFailed(_updateManager!.Update(null), nameof(IDirectManipulationUpdateManager.Update));

    public bool TrySetContact(uint pointerId)
    {
        if (_disposed || !_enabled || _viewport == null ||
            !GetPointerType(pointerId, out var type) || type != PointerInputType.PT_TOUCHPAD ||
            !GetCursorPos(out var position) || !ScreenToClient(_hwnd, ref position))
            return false;

        try
        {
            _position = new PixelPoint(position.X, position.Y);
            _interacting = true;
            ThrowIfFailed(_viewport.SetContact(pointerId), nameof(IDirectManipulationViewport.SetContact));
            UpdateTimer();
            return true;
        }
        catch (COMException exception)
        {
            OnFailure(exception);
            return false;
        }
    }

    public void Resize()
    {
        if (_disposed)
            return;

        try
        {
            ResizeCore();
        }
        catch (COMException exception)
        {
            OnFailure(exception);
        }
    }

    private void ResizeCore()
    {
        if (_viewport == null || !GetClientRect(_hwnd, out var rect))
            return;

        rect.right = Math.Max(1, rect.right);
        rect.bottom = Math.Max(1, rect.bottom);
        ThrowIfFailed(_viewport.SetViewportRect(&rect), nameof(IDirectManipulationViewport.SetViewportRect));
    }

    public void SetEnabled(bool enabled)
    {
        if (_disposed || _viewport == null || _enabled == enabled)
            return;

        _enabled = enabled;
        try
        {
            if (enabled)
                ThrowIfFailed(_viewport.Enable(), nameof(IDirectManipulationViewport.Enable));
            else
            {
                _interacting = false;
                ThrowIfFailed(_viewport.Disable(), nameof(IDirectManipulationViewport.Disable));
                _timer.Stop();
            }
        }
        catch (COMException exception)
        {
            OnFailure(exception);
        }
    }

    private void OnTick(object? sender, EventArgs args)
    {
        if (_disposed)
            return;

        try
        {
            Update();
            if (!_disposed && _resetting)
            {
                DIRECTMANIPULATION_STATUS status = default;
                ThrowIfFailed(_viewport!.GetStatus(&status), nameof(IDirectManipulationViewport.GetStatus));
                if (status == DIRECTMANIPULATION_STATUS.READY)
                    _resetting = false;
            }
            UpdateTimer();
        }
        catch (COMException exception)
        {
            OnFailure(exception);
        }
    }

    private void UpdateTimer()
    {
        if (!_disposed && _enabled && (_interacting || _resetting ||
            _status is DIRECTMANIPULATION_STATUS.RUNNING or DIRECTMANIPULATION_STATUS.INERTIA))
            _timer.Start();
        else
            _timer.Stop();
    }

    private void OnStatusChanged(IDirectManipulationViewport viewport, DIRECTMANIPULATION_STATUS current)
    {
        if (_disposed)
            return;

        _status = current;
        if (current is DIRECTMANIPULATION_STATUS.DISABLED or DIRECTMANIPULATION_STATUS.SUSPENDED)
        {
            _interacting = false;
            _resetting = false;
        }
        if (current == DIRECTMANIPULATION_STATUS.READY)
        {
            if (_resetting)
                _resetting = false;
            else if (_scale != 1 || _translation != default)
            {
                _resetting = true;
                _scale = 1;
                _translation = default;
                try
                {
                    RECT rect = default;
                    ThrowIfFailed(viewport.GetViewportRect(&rect), nameof(IDirectManipulationViewport.GetViewportRect));
                    ThrowIfFailed(viewport.ZoomToRect(rect.left, rect.top, rect.right, rect.bottom, 0),
                        nameof(IDirectManipulationViewport.ZoomToRect));
                }
                catch (COMException exception)
                {
                    OnFailure(exception);
                }
            }
        }
        UpdateTimer();
    }

    internal static (double Magnification, Vector Translation) GetGestureDelta(
        double previousScale, Vector previousTranslation, double scale, Vector translation, PixelPoint position)
    {
        var factor = scale / previousScale;
        return (factor - 1, translation - previousTranslation * factor +
            new Vector(position.X, position.Y) * (factor - 1));
    }

    private void OnContentUpdated(IDirectManipulationContent content)
    {
        if (_disposed || !_enabled || _resetting)
            return;

        var transform = stackalloc float[6];
        try
        {
            ThrowIfFailed(content.GetContentTransform(transform, 6), nameof(IDirectManipulationContent.GetContentTransform));
        }
        catch (COMException exception)
        {
            OnFailure(exception);
            return;
        }

        var scale = (double)transform[0];
        var translation = new Vector(transform[4], transform[5]);
        if (!double.IsFinite(scale) || scale <= 0 ||
            !double.IsFinite(translation.X) || !double.IsFinite(translation.Y))
            return;

        var delta = GetGestureDelta(_scale, _translation, scale, translation, _position);
        _scale = scale;
        _translation = translation;
        if (delta.Magnification != 0 || delta.Translation != default)
            _onGesture(delta.Magnification, delta.Translation, _position);
    }

    private void OnFailure(COMException exception)
    {
        Logger.TryGet(LogEventLevel.Warning, LogArea.Win32Platform)?.Log(this,
            "Direct Manipulation touchpad input failed: {0}", exception);
        Dispose();
    }

    private void ReleaseNative(Func<int> release, string operation)
    {
        try
        {
            ThrowIfFailed(release(), operation);
        }
        catch (COMException exception)
        {
            Logger.TryGet(LogEventLevel.Warning, LogArea.Win32Platform)?.Log(this,
                "Direct Manipulation cleanup failed: {0}", exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Stop();
        if (_viewport != null)
        {
            ReleaseNative(_viewport.Disable, nameof(IDirectManipulationViewport.Disable));
            if (_handlerCookie is { } cookie)
                ReleaseNative(() => _viewport.RemoveEventHandler(cookie), nameof(IDirectManipulationViewport.RemoveEventHandler));
            ReleaseNative(_viewport.Abandon, nameof(IDirectManipulationViewport.Abandon));
            _viewport.Dispose();
            _viewport = null;
        }
        _updateManager?.Dispose();
        _updateManager = null;
        if (_manager != null)
        {
            ReleaseNative(() => _manager.Deactivate(_hwnd), nameof(IDirectManipulationManager.Deactivate));
            _manager.Dispose();
            _manager = null;
        }
    }

    private sealed class Handler(WindowsTouchpad owner) : CallbackBase,
        IDirectManipulationViewportEventHandler, IDirectManipulationInteractionEventHandler
    {
        public void OnViewportStatusChanged(IDirectManipulationViewport viewport,
            DIRECTMANIPULATION_STATUS current, DIRECTMANIPULATION_STATUS previous) =>
            owner.OnStatusChanged(viewport, current);

        public void OnViewportUpdated(IDirectManipulationViewport viewport) { }

        public void OnContentUpdated(IDirectManipulationViewport viewport, IDirectManipulationContent content) =>
            owner.OnContentUpdated(content);

        public void OnInteraction(IDirectManipulationViewport2 viewport, DIRECTMANIPULATION_INTERACTION_TYPE interaction)
        {
            if (!owner._disposed && interaction is
                DIRECTMANIPULATION_INTERACTION_TYPE.BEGIN or DIRECTMANIPULATION_INTERACTION_TYPE.END)
            {
                owner._interacting = interaction == DIRECTMANIPULATION_INTERACTION_TYPE.BEGIN;
                owner.UpdateTimer();
            }
        }
    }
}