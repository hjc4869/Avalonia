using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Rendering;
using CoreAnimation;
using Foundation;
using UIKit;

namespace Avalonia.iOS
{
    class DisplayLinkTimer : IRenderTimerWithFrameTiming
    {
        private const double DistantFutureInterval = (double)50 * 365 * 24 * 3600;
        private const double MaxFrameInterval = 0.25;
        private static DisplayLinkTimer? s_instance;

        private readonly CADisplayLink _link;
        private readonly Stopwatch _st = Stopwatch.StartNew();
        private readonly bool _lowLatencyTouchInput;
        private readonly object _frameTimingLock = new();
        private readonly ManualResetEventSlim _inputCommitted = new(false);
        private RenderTimerFrameTiming? _lastFrameTiming;
        private volatile Action<TimeSpan>? _tick;
        private volatile bool _inBackground;
        private volatile IntPtr _runLoop;
        private volatile IntPtr _wakeTimer;

        // Written on the UI thread, read on the render thread
        private long _lastPointerInputTimestamp;
        private long _lastInputCommitTimestamp;
        private volatile bool _pointersDown;

        // UI thread only
        private bool _inputAwaitingCommit;

        // Render thread only
        private long _tickDurationEstimate;
        private long _tickDurationEstimateTimestamp;

        public DisplayLinkTimer()
        {
            s_instance = this;
            _lowLatencyTouchInput = Platform.Options?.LowLatencyTouchInput ?? true;
            _link = CADisplayLink.Create(OnLinkTick);
            ConfigureFrameRate(_link);
            _link.Paused = true;
            TimerThread = new Thread(RunLoop)
            {
                Name = "Avalonia Render Thread",
                IsBackground = true
            };
            TimerThread.Start();
            UIApplication.Notifications.ObserveDidEnterBackground((_, _) => SetBackground(true));
            UIApplication.Notifications.ObserveWillEnterForeground((_, _) => SetBackground(false));
        }

        public Thread TimerThread { get; }

        public bool RunsInBackground => true;

        public Action<TimeSpan>? Tick
        {
            get => _tick;
            set
            {
                _tick = value;
                RequestStateUpdate();
            }
        }

        public RenderTimerFrameTiming? LastFrameTiming
        {
            get
            {
                lock (_frameTimingLock)
                    return _lastFrameTiming;
            }
        }

        internal bool LowLatencyTouchInput => _lowLatencyTouchInput;

        /// <summary>
        /// Records that pointer input was dispatched. Called on the UI thread.
        /// </summary>
        /// <param name="pointersDown">Whether any pointer is still pressed after the input.</param>
        internal void OnPointerInput(bool pointersDown)
        {
            if (!_lowLatencyTouchInput)
                return;

            Volatile.Write(ref _lastPointerInputTimestamp, Stopwatch.GetTimestamp());
            _pointersDown = pointersDown;
            _inputAwaitingCommit = true;
        }

        /// <summary>
        /// Called on the UI thread after the compositor sent a batch to the render loop.
        /// </summary>
        internal void OnCompositorCommitted()
        {
            if (!_inputAwaitingCommit)
                return;

            _inputAwaitingCommit = false;
            Volatile.Write(ref _lastInputCommitTimestamp, Stopwatch.GetTimestamp());
            _inputCommitted.Set();
        }

        private static void ConfigureFrameRate(CADisplayLink link)
        {
            var maxRate = (int)UIScreen.MainScreen.MaximumFramesPerSecond;
            if (maxRate <= 60)
                return;

            if (OperatingSystem.IsIOSVersionAtLeast(15)
                || OperatingSystem.IsTvOSVersionAtLeast(15)
                || OperatingSystem.IsMacCatalystVersionAtLeast(15))
            {
                link.PreferredFrameRateRange = new CAFrameRateRange
                {
                    Minimum = Math.Min(80, maxRate),
                    Maximum = maxRate,
                    Preferred = maxRate
                };
            }
            else
            {
                link.PreferredFramesPerSecond = maxRate;
            }
        }

        private unsafe void RunLoop()
        {
            Interop.pthread_set_qos_class_self_np(Interop.QOS_CLASS_USER_INTERACTIVE, 0);

            _runLoop = Interop.CFRunLoopGetCurrent();
            _wakeTimer = Interop.CFRunLoopTimerCreate(IntPtr.Zero,
                Interop.CFAbsoluteTimeGetCurrent() + DistantFutureInterval,
                DistantFutureInterval, 0, 0, &WakeTimerCallback, IntPtr.Zero);
            Interop.CFRunLoopAddTimer(_runLoop, _wakeTimer, Interop.kCFRunLoopCommonModes);

            _link.AddToRunLoop(NSRunLoop.Current, NSRunLoopMode.Common);
            ApplyState();
            NSRunLoop.Current.Run();
        }

        private void SetBackground(bool inBackground)
        {
            _inBackground = inBackground;
            RequestStateUpdate();
        }

        // The display link is only paused or resumed from its own run loop thread.
        private void RequestStateUpdate()
        {
            if (Thread.CurrentThread == TimerThread)
            {
                ApplyState();
                return;
            }

            var wakeTimer = _wakeTimer;
            if (wakeTimer == IntPtr.Zero)
                return;

            Interop.CFRunLoopTimerSetNextFireDate(wakeTimer, Interop.CFAbsoluteTimeGetCurrent());
            Interop.CFRunLoopWakeUp(_runLoop);
        }

        private void ApplyState()
        {
            _link.Paused = _tick is null || _inBackground;
        }

        private void OnLinkTick()
        {
            var tick = _tick;
            if (tick is null || _inBackground)
            {
                _link.Paused = true;
                return;
            }

            var timing = GetFrameTiming(out var vsyncTimestamp);
            if (_lowLatencyTouchInput)
                WaitForTouchInput(vsyncTimestamp, timing.FrameInterval);

            // Published after waiting: render passes the UI thread runs while this tick waits for input
            // prepare this frame, not the next one.
            lock (_frameTimingLock)
                _lastFrameTiming = timing;

            var started = Stopwatch.GetTimestamp();
            tick(_st.Elapsed + Stopwatch.GetElapsedTime(started, timing.TargetTimestamp));

            // A high estimate stops the wait for input. Taking more than an interval means there is no time left
            // to wait anyway, so cap samples there to recover quickly from one-off stalls like shader compilation.
            var finished = Stopwatch.GetTimestamp();
            var duration = Math.Min(finished - started, timing.FrameInterval);
            _tickDurationEstimate = Math.Max(duration, GetTickDurationEstimate(finished));
            _tickDurationEstimateTimestamp = finished;
        }

        /// <summary>
        /// The longest recent tick duration, halving every 100ms, so that neither a single slow frame nor
        /// a pause after one keeps it high.
        /// </summary>
        private long GetTickDurationEstimate(long now)
        {
            var halfLives = (now - _tickDurationEstimateTimestamp) / (Stopwatch.Frequency / 10.0);
            return (long)(_tickDurationEstimate * Math.Pow(0.5, halfLives));
        }

        /// <summary>
        /// Converts the display link's times, which are on the CACurrentMediaTime clock, to Stopwatch timestamps.
        /// </summary>
        private RenderTimerFrameTiming GetFrameTiming(out long vsyncTimestamp)
        {
            var now = Stopwatch.GetTimestamp();
            var mediaNow = CAAnimation.CurrentMediaTime();
            var target = _link.TargetTimestamp;
            var vsync = _link.Timestamp;

            var interval = target - vsync;
            if (!(interval > 0 && interval <= MaxFrameInterval))
            {
                interval = _link.Duration is > 0 and <= MaxFrameInterval ? _link.Duration : 1.0 / 60;
                vsync = target - interval;
            }

            vsyncTimestamp = now + ToStopwatchTicks(vsync - mediaNow);
            return new RenderTimerFrameTiming(now + ToStopwatchTicks(target - mediaNow), ToStopwatchTicks(interval));
        }

        private static long ToStopwatchTicks(double seconds) => (long)(seconds * Stopwatch.Frequency);

        /// <summary>
        /// While a touch is moving, UIKit delivers its events on the main thread at the start of each display
        /// refresh, at about the same time this callback runs. Rendering right away would show the input only in
        /// the next refresh, so give the UI thread a part of the refresh interval to commit the changes caused by
        /// this refresh's touch events and render them now.
        /// </summary>
        private void WaitForTouchInput(long vsyncTimestamp, long interval)
        {
            var now = Stopwatch.GetTimestamp();
            var lastInput = Volatile.Read(ref _lastPointerInputTimestamp);
            // Touch events of a refresh can also be delivered shortly before its callback runs
            var refreshStart = vsyncTimestamp - interval / 4;

            // Wait while touches keep moving, or for the commit of input that already arrived in this refresh,
            // e.g. a release that starts inertia
            var moving = _pointersDown && now - lastInput <= 2 * interval;
            var uncommitted = lastInput >= refreshStart && Volatile.Read(ref _lastInputCommitTimestamp) < lastInput;
            if (!moving && !uncommitted)
                return;

            // Leave enough of the interval to render and present the frame, assuming the GPU needs about as long as
            // the render thread, plus a margin for the wait itself, which tends to return up to a millisecond late
            var budget = Math.Min(interval / 2, interval - 2 * GetTickDurationEstimate(now) - Stopwatch.Frequency / 500);
            var deadline = vsyncTimestamp + budget;

            while (true)
            {
                _inputCommitted.Reset();
                // Done once the latest touch input belongs to this refresh and has been committed
                var input = Volatile.Read(ref _lastPointerInputTimestamp);
                if (input >= refreshStart && Volatile.Read(ref _lastInputCommitTimestamp) >= input)
                    return;

                // Waits have millisecond granularity, rather stop early than overshoot the deadline
                var timeout = (int)((deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency);
                if (timeout < 1)
                    return;

                _inputCommitted.Wait(timeout);
            }
        }

        [UnmanagedCallersOnly]
        private static void WakeTimerCallback(IntPtr timer, IntPtr info)
        {
            s_instance?.ApplyState();
        }
    }
}
