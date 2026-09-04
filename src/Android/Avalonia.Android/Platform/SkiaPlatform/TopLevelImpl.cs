using System;
using System.Collections.Generic;
using System.Threading;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Runtime;
using Android.Views;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using Avalonia.Android.Platform.Input;
using Avalonia.Android.Platform.Specific.Helpers;
using Avalonia.Android.Platform.Storage;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.Raw;
using Avalonia.Input.TextInput;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Platform.Surfaces;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Java.Lang;
using Java.Util.Functions;
using ClipboardManager = Android.Content.ClipboardManager;

namespace Avalonia.Android.Platform.SkiaPlatform
{
    class TopLevelImpl : ITopLevelImpl, IPlatformSurfaceColorVolumeFeature,
        EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfoWithColorVolume,
        EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfoWithWaitPolicy
    {
        private readonly Context _context;
        private readonly AndroidKeyboardEventsHelper<TopLevelImpl> _keyboardHelper;
        private readonly AndroidMotionEventsHelper _pointerHelper;
        private readonly AndroidInputMethod<AvaloniaView> _textInputMethod;
        private readonly INativeControlHostImpl _nativeControlHost;
        private readonly IStorageProvider? _storageProvider;
        private readonly AndroidSystemNavigationManagerImpl _systemNavigationManager;
        private readonly AndroidInsetsManager? _insetsManager;
        private readonly Clipboard _clipboard;
        private readonly AndroidLauncher? _launcher;
        private AndroidScreens? _screens;
        private readonly AndroidPlatformFeedback _feedback;
        private SurfaceViewImpl? _view;
        private WindowTransparencyLevel _transparencyLevel;
        private Display? _hdrSdrRatioDisplay;
        private HdrSdrRatioChangedListener? _hdrSdrRatioListener;
        private ColorVolumeState _colorVolumeState = new(null);

        public TopLevelImpl(AvaloniaView avaloniaView, bool placeOnTop = false)
        {
            if (avaloniaView.Context is not { } context)
            {
                throw new ArgumentException("AvaloniaView.Context must not be null");
            }

            _context = context;
            _view = new SurfaceViewImpl(context, this, placeOnTop);
            _textInputMethod = new AndroidInputMethod<AvaloniaView>(avaloniaView);
            _keyboardHelper = new AndroidKeyboardEventsHelper<TopLevelImpl>(this);
            _pointerHelper = new AndroidMotionEventsHelper(this);
            _clipboard = new Clipboard(new ClipboardImpl(
                context.GetSystemService(Context.ClipboardService).JavaCast<ClipboardManager>(),
                context));
            _screens = new AndroidScreens(context);
            _screens.DisplaysChanged += OnDisplaysChanged;
            _feedback = new AndroidPlatformFeedback(avaloniaView);

            _view.SurfaceWindowCreated += OnSurfaceWindowCreated;
            _view.SurfaceWindowDestroyed += OnSurfaceWindowDestroyed;

            if (context is Activity mainActivity)
            {
                _insetsManager = new AndroidInsetsManager(mainActivity, this);
                _storageProvider = new AndroidStorageProvider(mainActivity);
                _launcher = new AndroidLauncher(mainActivity);
            }

            _nativeControlHost = new AndroidNativeControlHostImpl(avaloniaView);
            _transparencyLevel = WindowTransparencyLevel.None;

            _systemNavigationManager = new AndroidSystemNavigationManagerImpl(context as IActivityNavigationService);

            var gl = new EglGlPlatformSurface(this);
            var framebuffer = new FramebufferManager(this);
            Surfaces = [gl, framebuffer, _view];
            Handle = new AndroidViewControlHandle(_view);
        }

        public IInputRoot? InputRoot { get; private set; }

        public Size ClientSize => _view?.Size.ToSize(RenderScaling) ?? default;
        public double RenderScaling => _view?.Scaling ?? 1;

        public Action? Closed { get; set; }

        public Action<RawInputEventArgs>? Input { get; set; }

        public Action<Rect>? Paint { get; set; }

        public Action<Size, WindowResizeReason>? Resized { get; set; }

        public Action<double>? ScalingChanged { get; set; }

        public PlatformSurfaceColorVolume? PreferredColorVolume =>
            Volatile.Read(ref _colorVolumeState).Value;

        public event EventHandler? PreferredColorVolumeChanged;

        public View? View => _view;

        internal InvalidationAwareSurfaceView? InternalView => _view;

        public double DesktopScaling => RenderScaling;
        public IPlatformHandle Handle { get; }

        public IPlatformRenderSurface[] Surfaces { get; }

        public Compositor Compositor => AndroidPlatform.Compositor ??
            throw new InvalidOperationException("Android backend wasn't initialized. Make sure .UseAndroid() was executed.");

        public Point PointToClient(PixelPoint point)
        {
            return point.ToPoint(RenderScaling);
        }

        public PixelPoint PointToScreen(Point point)
        {
            return PixelPoint.FromPoint(point, RenderScaling);
        }

        public void SetCursor(ICursorImpl? cursor)
        {
            //still not implemented
        }

        public void SetInputRoot(IInputRoot inputRoot)
        {
            InputRoot = inputRoot;
        }

        public virtual void Dispose()
        {
            StopTrackingHdrSdrRatio();
            if (_screens is { } screens)
            {
                _screens = null;
                screens.DisplaysChanged -= OnDisplaysChanged;
                screens.Dispose();
            }
            _systemNavigationManager.Dispose();
            if (_view is { } view)
            {
                view.SurfaceWindowCreated -= OnSurfaceWindowCreated;
                view.SurfaceWindowDestroyed -= OnSurfaceWindowDestroyed;
                // The view must leave the Java hierarchy before its peer is disposed: Android tears the window down
                // after Activity.OnDestroy() and any override it invokes then would fail to resolve the dead peer.
                (view.Parent as ViewGroup)?.RemoveView(view);
                view.Dispose();
            }
            _view = null;
        }

        protected void OnResized(Size size)
        {
            Resized?.Invoke(size, WindowResizeReason.Unspecified);
        }

        internal void Resize(Size size)
        {
            Resized?.Invoke(size, WindowResizeReason.Layout);
        }

        internal void RefreshColorVolume()
        {
            if (_view is not { } view || !CanRender(view))
            {
                ClearColorVolume();
                return;
            }

            var display = view.Display;
            view.UpdateDesiredHdrHeadroom(display);
            TrackHdrSdrRatio(display);
            PublishColorVolume(AndroidPlatform.GetPreferredColorVolume(display));
        }

        private void OnSurfaceWindowCreated(object? sender, EventArgs e) => RefreshColorVolume();

        private void OnSurfaceWindowDestroyed(object? sender, EventArgs e) => ClearColorVolume();

        private void ClearColorVolume()
        {
            StopTrackingHdrSdrRatio();
            PublishColorVolume(null);
        }

        private void RefreshColorVolumeFromRatioChange() => RefreshColorVolume();

        private void PublishColorVolume(PlatformSurfaceColorVolume? colorVolume)
        {
            if (PreferredColorVolume == colorVolume)
                return;

            Volatile.Write(ref _colorVolumeState, new ColorVolumeState(colorVolume));
            PreferredColorVolumeChanged?.Invoke(this, EventArgs.Empty);
            Dispatcher.UIThread.Post(() =>
            {
                if (_view is { } view && CanRender(view))
                    Paint?.Invoke(new Rect(ClientSize));
            }, DispatcherPriority.Input);
        }

        private static bool CanRender(SurfaceViewImpl view) =>
            view.IsAttachedToWindow && ((IPlatformHandle)view).Handle != IntPtr.Zero;

        private void OnDisplaysChanged(int displayId) =>
            Dispatcher.UIThread.Post(RefreshColorVolume, DispatcherPriority.Input);

        private void TrackHdrSdrRatio(Display? display)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(35))
                return;

            var shouldTrack = display?.IsHdrSdrRatioAvailable == true;
            if (_hdrSdrRatioDisplay?.DisplayId == display?.DisplayId &&
                (_hdrSdrRatioListener is not null) == shouldTrack)
            {
                return;
            }

            StopTrackingHdrSdrRatio();
            if (!shouldTrack || display is null || _view?.Context?.MainExecutor is not { } executor)
                return;

            var listener = new HdrSdrRatioChangedListener(this);
            try
            {
                display.RegisterHdrSdrRatioChangedListener(executor, listener);
                _hdrSdrRatioDisplay = display;
                _hdrSdrRatioListener = listener;
            }
            catch (IllegalStateException)
            {
                listener.Dispose();
            }
        }

        private void StopTrackingHdrSdrRatio()
        {
            var display = _hdrSdrRatioDisplay;
            var listener = _hdrSdrRatioListener;
            _hdrSdrRatioDisplay = null;
            _hdrSdrRatioListener = null;

            if (listener is not null)
            {
                try
                {
                    if (OperatingSystem.IsAndroidVersionAtLeast(35) && display is not null)
                        display.UnregisterHdrSdrRatioChangedListener(listener);
                }
                catch (IllegalStateException)
                {
                }
                finally
                {
                    listener.Dispose();
                }
            }
        }

        private sealed class HdrSdrRatioChangedListener(TopLevelImpl owner) : Java.Lang.Object, IConsumer
        {
            public void Accept(Java.Lang.Object? value) =>
                Dispatcher.UIThread.Post(owner.RefreshColorVolumeFromRatioChange, DispatcherPriority.Input);
        }

        private sealed class ColorVolumeState(PlatformSurfaceColorVolume? value)
        {
            public PlatformSurfaceColorVolume? Value { get; } = value;
        }

        sealed class SurfaceViewImpl : InvalidationAwareSurfaceView
        {
            private readonly TopLevelImpl _tl;
            private Size _oldSize;
            private double _oldScaling;
            private Paint? _clearPaint;

            public SurfaceViewImpl(Context context, TopLevelImpl tl, bool placeOnTop) : base(context)
            {
                _tl = tl;
                if (OperatingSystem.IsAndroidVersionAtLeast(35))
                    UpdateDesiredHdrHeadroom(context.Display);
                if (placeOnTop)
                    SetZOrderOnTop(true);
            }

            internal void UpdateDesiredHdrHeadroom(Display? display)
            {
                if (OperatingSystem.IsAndroidVersionAtLeast(35))
                    SetDesiredHdrHeadroom(AndroidPlatform.GetDesiredHdrHeadroom(display) ?? 0);
            }

            protected override void OnConfigurationChanged(global::Android.Content.Res.Configuration? newConfig)
            {
                base.OnConfigurationChanged(newConfig);
                _tl.RefreshColorVolume();
            }

            protected override void DispatchDraw(global::Android.Graphics.Canvas canvas)
            {
                // Workaround issue #9230 on where screen remains gray after splash screen.
                // base.DispatchDraw should punch a hole into the canvas so the surface
                // can be seen below, but it does not.
                if (OperatingSystem.IsAndroidVersionAtLeast(29))
                {
                    if (_clearPaint == null)
                    {
                        _clearPaint = new Paint();
                        _clearPaint.SetColor(0);
                        _clearPaint.BlendMode = BlendMode.Clear;
                    }
                    canvas.DrawRect(0, 0, Width, Height, _clearPaint);
                }
                else
                {
                    // Android 9 did this
                    canvas.DrawColor(Color.Transparent, PorterDuff.Mode.Clear!);
                }

                base.DispatchDraw(canvas);
            }

            public override void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height)
            {
                base.SurfaceChanged(holder, format, width, height);

                var newSize = Size.ToSize(Scaling);
                var newScaling = Scaling;

                if (newSize != _oldSize)
                {
                    _oldSize = newSize;
                    _tl.OnResized(newSize);
                }
                // ReSharper disable once CompareOfFloatsByEqualityOperator
                if (newScaling != _oldScaling)
                {
                    _oldScaling = newScaling;
                    _tl.ScalingChanged?.Invoke(newScaling);
                }
            }

            public override void SurfaceRedrawNeeded(ISurfaceHolder holder)
            {
                // Compositor Renderer handles Paint event in-sync, which is perfect for sync SurfaceRedrawNeeded
                _tl.Paint?.Invoke(new Rect(new Point(), Size.ToSize(Scaling)));
                base.SurfaceRedrawNeeded(holder);
            }

            public override void SurfaceRedrawNeededAsync(ISurfaceHolder holder, IRunnable drawingFinished)
            {
                _tl.Compositor.RequestCompositionUpdate(drawingFinished.Run);
                base.SurfaceRedrawNeededAsync(holder, drawingFinished);
            }
        }

        public IPopupImpl? CreatePopup() => null;

        public Action? LostFocus { get; set; }
        public Action<WindowTransparencyLevel>? TransparencyLevelChanged { get; set; }

        public WindowTransparencyLevel TransparencyLevel
        {
            get => _transparencyLevel;
            private set
            {
                if (_transparencyLevel != value)
                {
                    _transparencyLevel = value;
                    TransparencyLevelChanged?.Invoke(value);
                }
            }
        }

        public void SetFrameThemeVariant(PlatformThemeVariant? themeVariant)
        {
            if (_insetsManager != null)
            {
                _insetsManager.SystemBarTheme = themeVariant switch
                {
                    PlatformThemeVariant.Light => SystemBarTheme.Light,
                    PlatformThemeVariant.Dark => SystemBarTheme.Dark,
                    _ => null,
                };
            }

            if (_context is AppCompatActivity activity)
            {
                var nightMode = themeVariant == PlatformThemeVariant.Dark ?
                    AppCompatDelegate.ModeNightYes :
                    AppCompatDelegate.ModeNightNo;

                // Don't use AppCompatDelegate.DefaultNightMode: doing so will force the app to use one night mode,
                // ignoring the system's configuration and preventing us from detecting system theme changes.
                activity.Delegate.SetLocalNightMode(nightMode);
            }
        }

        public AcrylicPlatformCompensationLevels AcrylicCompensationLevels => new(1, 1, 1);

        IntPtr EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo.Handle => (_view as IPlatformHandle)?.Handle ?? default;
        PlatformSurfaceColorVolume? EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfoWithColorVolume.PreferredColorVolume =>
            PreferredColorVolume;
        bool EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfoWithWaitPolicy.SkipWaits => true;
        PixelSize EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo.Size => _view?.Size ?? default;
        double EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo.Scaling => _view?.Scaling ?? default;

        internal AndroidInsetsManager? InsetsManager => _insetsManager;
        internal AndroidKeyboardEventsHelper<TopLevelImpl> KeyboardHelper => _keyboardHelper;
        internal AndroidMotionEventsHelper PointerHelper => _pointerHelper;

        public void SetTransparencyLevelHint(IReadOnlyList<WindowTransparencyLevel> transparencyLevels)
        {
            if (_view?.Context is not AvaloniaActivity activity)
                return;

            foreach (var level in transparencyLevels)
            {
                if (!IsSupported(level))
                {
                    continue;
                }

                if (level == TransparencyLevel)
                {
                    return;
                }

                if (level == WindowTransparencyLevel.None)
                {
                    if (OperatingSystem.IsAndroidVersionAtLeast(30))
                    {
                        activity.SetTranslucent(false);
                    }

                    activity.Window?.SetBackgroundDrawable(new ColorDrawable(Color.White));
                }
                else if (level == WindowTransparencyLevel.Transparent)
                {
                    if (OperatingSystem.IsAndroidVersionAtLeast(30))
                    {
                        activity.SetTranslucent(true);
                        SetBlurBehind(activity, 0);
                        activity.Window?.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
                    }
                }
                else if (level == WindowTransparencyLevel.Blur)
                {
                    if (OperatingSystem.IsAndroidVersionAtLeast(31))
                    {
                        activity.SetTranslucent(true);
                        SetBlurBehind(activity, 120);
                        activity.Window?.SetBackgroundDrawable(new ColorDrawable(Color.Transparent));
                    }
                }

                TransparencyLevel = level;
                return;
            }

            // If we get here, we didn't find a supported level. Use the default of None.
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                activity.SetTranslucent(false);
            }

            activity.Window?.SetBackgroundDrawable(new ColorDrawable(Color.White));
        }

        public virtual object? TryGetFeature(Type featureType)
        {
            if (featureType == typeof(IStorageProvider))
            {
                return _storageProvider;
            }

            if (featureType == typeof(ITextInputMethodImpl))
            {
                return _textInputMethod;
            }

            if (featureType == typeof(ISystemNavigationManagerImpl))
            {
                return _systemNavigationManager;
            }

            if (featureType == typeof(INativeControlHostImpl))
            {
                return _nativeControlHost;
            }

            if (featureType == typeof(IInsetsManager) || featureType == typeof(IInputPane))
            {
                return _insetsManager;
            }

            if (featureType == typeof(IClipboard))
            {
                return _clipboard;
            }

            if (featureType == typeof(ILauncher))
            {
                return _launcher;
            }

            if (featureType == typeof(IScreenImpl))
            {
                return _screens;
            }

            if(featureType == typeof(IPlatformFeedback))
            {
                return _feedback;
            }
            if (featureType == typeof(IPlatformSurfaceColorVolumeFeature))
            {
                return this;
            }
            return null;
        }

        private static bool IsSupported(WindowTransparencyLevel level)
        {
            if (level == WindowTransparencyLevel.None)
                return true;
            if (level == WindowTransparencyLevel.Transparent)
                return OperatingSystem.IsAndroidVersionAtLeast(30);
            if (level == WindowTransparencyLevel.Blur)
                return OperatingSystem.IsAndroidVersionAtLeast(31);
            return false;
        }

        private static void SetBlurBehind(AvaloniaActivity activity, int radius)
        {
            if (radius == 0)
                activity.Window?.ClearFlags(WindowManagerFlags.BlurBehind);
            else
                activity.Window?.AddFlags(WindowManagerFlags.BlurBehind);

            if (OperatingSystem.IsAndroidVersionAtLeast(31) && activity.Window?.Attributes is { } attr)
            {
                attr.BlurBehindRadius = radius;
                activity.Window.Attributes = attr;
            }
        }

        internal void TextInput(string text)
        {
            if (Input != null)
            {
                var args = new RawTextInputEventArgs(AndroidKeyboardDevice.Instance!, (ulong)SystemClock.UptimeMillis(), InputRoot!, text);

                Input(args);
            }
        }
    }
}
