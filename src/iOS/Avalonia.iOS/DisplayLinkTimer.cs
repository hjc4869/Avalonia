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
    class DisplayLinkTimer : IRenderTimer
    {
        private const double DistantFutureInterval = (double)50 * 365 * 24 * 3600;
        private static DisplayLinkTimer? s_instance;

        private readonly CADisplayLink _link;
        private readonly Stopwatch _st = Stopwatch.StartNew();
        private volatile Action<TimeSpan>? _tick;
        private volatile bool _inBackground;
        private volatile IntPtr _runLoop;
        private volatile IntPtr _wakeTimer;

        public DisplayLinkTimer()
        {
            s_instance = this;
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

            tick(_st.Elapsed);
        }

        [UnmanagedCallersOnly]
        private static void WakeTimerCallback(IntPtr timer, IntPtr info)
        {
            s_instance?.ApplyState();
        }
    }
}
