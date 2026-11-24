using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Surfaces;
using Avalonia.Logging;
using Avalonia.MicroCom;
using Avalonia.OpenGL.Egl;
using Avalonia.Rendering;
using Avalonia.Win32.Interop;
using MicroCom.Runtime;

namespace Avalonia.Win32.WinRT.Composition;

internal class WinUiCompositorConnection : IRenderTimer, Win32.IWindowsSurfaceFactory
{
    private readonly WinUiCompositionShared _shared;
    private readonly AutoResetEvent _wakeEvent = new(false);
    private volatile bool _stopped = true;
    private volatile Action<TimeSpan>? _tick;

    public bool RunsInBackground => true;

    public Action<TimeSpan>? Tick
    {
        get => _tick;
        set
        {
            if (value != null)
            {
                _tick = value;
                _stopped = false;
                _wakeEvent.Set();
            }
            else
            {
                _stopped = true;
                _tick = null;
            }
        }
    }

    public WinUiCompositorConnection()
    {
        using var compositor = NativeWinRTMethods.CreateInstance<ICompositor>("Windows.UI.Composition.Compositor");
        /*
        var levels = new[] { D3D_FEATURE_LEVEL.D3D_FEATURE_LEVEL_11_1 };
        DirectXUnmanagedMethods.D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE,
            IntPtr.Zero, 0, levels, (uint)levels.Length, 7, out var pD3dDevice, out var level, null);

        var d3dDevice = MicroComRuntime.CreateProxyFor<IUnknown>(pD3dDevice, true);

        var compositionDevice = compositor.QueryInterface<ICompositorInterop>().CreateGraphicsDevice(d3dDevice);
        var surf = compositionDevice.CreateDrawingSurface(new UnmanagedMethods.SIZE_F { X = 100, Y = 100 },
            DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
        var surfInterop = surf.QueryInterface<ICompositionDrawingSurfaceInterop>();
        var IID_ID3D11Texture2D = Guid.Parse("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        void* texture = null;
        surfInterop.BeginDraw(null, &IID_ID3D11Texture2D, &texture);
        */

        _shared = new WinUiCompositionShared(compositor);
    }

    private static bool TryCreateAndRegisterCore()
    {
        var tcs = new TaskCompletionSource<bool>();
        var th = new Thread(() =>
        {
            WinUiCompositorConnection connect;
            try
            {
                NativeWinRTMethods.CreateDispatcherQueueController(new NativeWinRTMethods.DispatcherQueueOptions
                {
                    apartmentType = NativeWinRTMethods.DISPATCHERQUEUE_THREAD_APARTMENTTYPE.DQTAT_COM_NONE,
                    dwSize = Marshal.SizeOf<NativeWinRTMethods.DispatcherQueueOptions>(),
                    threadType = NativeWinRTMethods.DISPATCHERQUEUE_THREAD_TYPE.DQTYPE_THREAD_CURRENT
                });
                connect = new WinUiCompositorConnection();
                AvaloniaLocator.CurrentMutable.Bind<IWindowsSurfaceFactory>().ToConstant(connect);
                AvaloniaLocator.CurrentMutable.Bind<IRenderLoop>().ToConstant(RenderLoop.FromTimer(connect));
                tcs.SetResult(true);

            }
            catch (Exception e)
            {
                tcs.SetException(e);
                return;
            }

            connect.RunLoop();
        })
        {
            IsBackground = true,
            Name = "DwmRenderTimerLoop"
        };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        return tcs.Task.Result;
    }

    private class RunLoopHandler : CallbackBase, IAsyncActionCompletedHandler
    {
        private readonly WinUiCompositorConnection _parent;
        private readonly Stopwatch _st = Stopwatch.StartNew();
        private TimeSpan? _commitDueAt;
        private IAsyncAction? _currentCommit;
        private bool _commitCompleted;

        public RunLoopHandler(WinUiCompositorConnection parent)
        {
            _parent = parent;
        }

        public void Invoke(IAsyncAction? asyncInfo, AsyncStatus asyncStatus)
        {
            lock (_parent._shared.SyncRoot)
            {
                if (_currentCommit == null || _currentCommit.GetNativeIntPtr() != asyncInfo.GetNativeIntPtr())
                    return;
                OnCommitCompleted();
            }
        }

        private void OnCommitCompleted()
        {
            Debug.Assert(Monitor.IsEntered(_parent._shared.SyncRoot), "Lock should be held");

            _currentCommit?.Dispose();
            _currentCommit = null;
            _parent._tick?.Invoke(_st.Elapsed);
            // Always schedule a commit so the current frame's work reaches DWM.
            ScheduleNextCommit();
            _commitCompleted = true;
        }

        // This method should be called outside the shared lock, as it might wait for a long time.
        public void OnAfterMessageWithoutLock()
        {
            Debug.Assert(!Monitor.IsEntered(_parent._shared.SyncRoot), "Lock should NOT be held");

            if (!_commitCompleted)
                return;

            _commitCompleted = false;

            if (_parent._stopped)
            {
                _parent._wakeEvent.WaitOne();
                // Reset the expected commit callback time since we've paused
                // the render loop due to app being idle
                _commitDueAt = _st.Elapsed + TimeSpan.FromSeconds(1);
            }
        }

        private void ScheduleNextCommit()
        {
            Debug.Assert(Monitor.IsEntered(_parent._shared.SyncRoot), "Lock should be held");

            _commitDueAt = _st.Elapsed + TimeSpan.FromSeconds(1);
            _currentCommit = _parent._shared.Compositor5.RequestCommitAsync();
            _currentCommit.SetCompleted(this);
        }

        public void WatchDog()
        {
            lock (_parent._shared.SyncRoot)
            {
                // This is a workaround for a nasty WinUI composition API bug that prevents
                // RequestCommitAsync to ever complete after D3D device loss event with some systems
                // (A notable example is after pause/resume in Parallels Desktop)
                // We check if we haven't got a commit completion callback for a second
                // And forcefully trigger the next one, which makes the entire thing to unstuck

                if (_st.Elapsed > _commitDueAt && _currentCommit != null)
                {
                    Logger.TryGet(LogEventLevel.Error, LogArea.Visual)?.Log(this,
                        "windows::UI::Composition::ICompositor5.RequestCommitAsync timed out, force-triggering next tick");
                    try
                    {
                        _currentCommit?.GetResults();
                    }
                    catch (Exception e)
                    {
                        Logger.TryGet(LogEventLevel.Error, LogArea.Visual)?.Log(this,
                            "ICompositor5::RequestCommitAsync failed: {HR}, {ERR}", e.HResult, e.ToString());
                    }

                    OnCommitCompleted();
                }
            }
        }

        public void Start()
        {
            lock (_parent._shared.SyncRoot)
                ScheduleNextCommit();
        }
    }

    [DllImport("dcomp", ExactSpelling = true)]
    private static extern uint DCompositionWaitForCompositorClock(uint count, IntPtr handles, uint timeoutInMs);

    /// <summary>
    /// Releases commit operations once the system compositor reports them consumed.
    /// </summary>
    private class PendingCommits : CallbackBase, IAsyncActionCompletedHandler
    {
        private const int MaxPending = 8;
        private readonly List<IAsyncAction> _pending = new();

        public void Add(IAsyncAction commit)
        {
            lock (_pending)
            {
                // A commit may never complete after a device loss, so the oldest one is let go.
                if (_pending.Count == MaxPending)
                {
                    _pending[0].Dispose();
                    _pending.RemoveAt(0);
                }

                _pending.Add(commit);
            }

            commit.SetCompleted(this);
        }

        public void Invoke(IAsyncAction? asyncInfo, AsyncStatus asyncStatus)
        {
            if (asyncInfo is null)
                return;

            var completed = asyncInfo.GetNativeIntPtr();
            lock (_pending)
            {
                for (var i = 0; i < _pending.Count; i++)
                {
                    if (_pending[i].GetNativeIntPtr() == completed)
                    {
                        _pending[i].Dispose();
                        _pending.RemoveAt(i);
                        break;
                    }
                }
            }
        }
    }

    // Ticks on the compositor clock. Frames are presented through swap chains, which queue them for
    // the display, so a tick does not wait for the previous commit to be consumed.
    private void RunClockLoop(CancellationTokenSource cts)
    {
        var stopwatch = Stopwatch.StartNew();
        var commits = new PendingCommits();
        var hasCompositorClock = true;

        while (!cts.IsCancellationRequested)
        {
            try
            {
                if (_stopped)
                    _wakeEvent.WaitOne();

                if (hasCompositorClock)
                {
                    try
                    {
                        DCompositionWaitForCompositorClock(0, IntPtr.Zero, 1000);
                    }
                    catch (EntryPointNotFoundException)
                    {
                        // The compositor clock is available from Windows 11.
                        hasCompositorClock = false;
                    }
                }

                if (!hasCompositorClock)
                    UnmanagedMethods.DwmFlush();

                // Commit completion callbacks are delivered through this thread's message queue.
                while (UnmanagedMethods.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 1))
                    UnmanagedMethods.DispatchMessage(ref msg);

                lock (_shared.SyncRoot)
                {
                    _tick?.Invoke(stopwatch.Elapsed);
                    // Visual tree changes made while rendering reach DWM through a commit.
                    commits.Add(_shared.Compositor5.RequestCommitAsync());
                }
            }
            catch (Exception e)
            {
                Logger.TryGet(LogEventLevel.Error, "WinUIComposition")
                    ?.Log(this, "Render loop tick failed: {0}", e);
            }
        }
    }

    private void RunLoop()
    {
        var cts = new CancellationTokenSource();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            cts.Cancel();

        if (AvaloniaLocator.Current.GetService<Win32PlatformOptions>()?.UseCompositionSwapChain == true)
        {
            RunClockLoop(cts);
            return;
        }

        var handler = new RunLoopHandler(this);
        handler.Start();

        const int watchDogIntervalInMs = 1000;

        using var dw = new SimpleWindow((hwnd, msg, w, l) =>
        {
            if (msg == (uint)UnmanagedMethods.WindowsMessage.WM_TIMER)
            {
                handler.WatchDog();
                UnmanagedMethods.SetTimer(hwnd, IntPtr.Zero, watchDogIntervalInMs, null);
            }
            return UnmanagedMethods.DefWindowProc(hwnd, msg, w, l);
        });
        UnmanagedMethods.SetTimer(dw.Handle, IntPtr.Zero, watchDogIntervalInMs, null);

        // Warning: the completion callback (RunLoopHandler.Invoke) from ICompositor5.RequestCommitAsync()
        // is called in DispatchMessage() on Windows 10, but in GetMessage() on Windows 11!
        // Be careful when changing the scope of the shared lock.

        var result = 0;
        while (!cts.IsCancellationRequested
               && (result = UnmanagedMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0)) > 0)
        {
            UnmanagedMethods.DispatchMessage(ref msg);
            handler.OnAfterMessageWithoutLock();
        }

        if (result < 0)
        {
            Logger.TryGet(LogEventLevel.Error, "WinUIComposition")
                ?.Log(this, "Unmanaged error in {0}. Error Code: {1}", nameof(RunLoop), Marshal.GetLastWin32Error());
        }
    }

    public static bool IsSupported()
    {
        return Win32Platform.WindowsVersion >= WinUiCompositionShared.MinWinCompositionVersion;
    }

    public static bool TryCreateAndRegister()
    {
        if (IsSupported())
        {
            try
            {
                TryCreateAndRegisterCore();
                return true;
            }
            catch (Exception e)
            {
                Logger.TryGet(LogEventLevel.Error, "WinUIComposition")
                    ?.Log(null, "Unable to initialize WinUI compositor: {0}", e);
            }
        }
        else
        {
            var osVersionNotice =
                $"Windows {WinUiCompositionShared.MinWinCompositionVersion} is required. Your machine has Windows {Win32Platform.WindowsVersion} installed.";

            Logger.TryGet(LogEventLevel.Warning, "WinUIComposition")?.Log(null,
                $"Unable to initialize WinUI compositor: {osVersionNotice}");
        }

        return false;
    }

    public bool RequiresNoRedirectionBitmap => true;
    public IPlatformRenderSurface CreateSurface(EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo info) => new WinUiCompositedWindowSurface(_shared, info);
}
