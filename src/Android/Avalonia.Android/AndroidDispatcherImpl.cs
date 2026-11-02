using System;
using System.Diagnostics;
using System.Threading;
using Android.OS;
using Avalonia.Controls.Documents;
using Avalonia.Threading;
using Java.Lang;
using App = Android.App.Application;
using Object = Java.Lang.Object;

namespace Avalonia.Android
{
    internal sealed class AndroidDispatcherImpl : IDispatcherImplWithExplicitBackgroundProcessing,
        IDispatcherImplWithPendingInput
    {
        [ThreadStatic] private static bool? s_isUIThread;
        private readonly Looper _mainLooper;
        private readonly Handler _handler;
        private readonly Runnable _signaler;
        private readonly Runnable _timerSignaler;
        private readonly Runnable _backgroundSignaler;
        private readonly MessageQueue _queue;
        private int _signaled;
        private bool _backgroundProcessingRequested;
        private long? _backgroundProcessingDeadline;
        private const int BackgroundProcessingInterval = 16;
        private const int BackgroundProcessingBudget = 4;
        

        public AndroidDispatcherImpl()
        {
            _mainLooper = App.Context.MainLooper ??
                          throw new InvalidOperationException(
                              "Application.Context.MainLooper was not expected to be null.");
            if (!CurrentThreadIsLoopThread)
                throw new InvalidOperationException("This class should be instanciated from the UI thread");
            _handler = new Handler(_mainLooper);
            _signaler = new Runnable(OnSignaled);
            _timerSignaler = new Runnable(OnTimer);
            _backgroundSignaler = new Runnable(OnBackgroundProcessing);
            _queue = Looper.MyQueue();
            Looper.MyQueue().AddIdleHandler(new IdleHandler(this));
            CanQueryPendingInput = OperatingSystem.IsAndroidVersionAtLeast(23);
        }
        
        public event Action? Timer;
        private void OnTimer() => Timer?.Invoke();

        public event Action? Signaled;
        private void OnSignaled()
        {
            Interlocked.Exchange(ref _signaled, 0);
            Signaled?.Invoke();
        }

        public bool CurrentThreadIsLoopThread
        {
            get
            {
                if (s_isUIThread.HasValue)
                    return s_isUIThread.Value;
                var uiThread = OperatingSystem.IsAndroidVersionAtLeast(23)
                    ? _mainLooper.IsCurrentThread
                    : _mainLooper.Thread.Equals(Java.Lang.Thread.CurrentThread());

                s_isUIThread = uiThread;
                return uiThread;
            }
        }

        public void Signal()
        {
            if (Interlocked.CompareExchange(ref _signaled, 1, 0) != 0)
            {
                return;
            }
            _handler.Post(_signaler);
        }

        readonly Stopwatch _clock = Stopwatch.StartNew();
        public long Now => _clock.ElapsedMilliseconds;
        
        public void UpdateTimer(long? dueTimeInMs)
        {
            _handler.RemoveCallbacks(_timerSignaler);
            if (dueTimeInMs.HasValue)
            {
                var delay = dueTimeInMs.Value - Now;
                if (delay > 0)
                    _handler.PostDelayed(_timerSignaler, delay);
                else
                    _handler.Post(_timerSignaler);
            }
        }

        class IdleHandler : Object, MessageQueue.IIdleHandler
        {
            private readonly AndroidDispatcherImpl _parent;

            public IdleHandler(AndroidDispatcherImpl parent)
            {
                _parent = parent;
            }
            
            public bool QueueIdle()
            {
                _parent.OnIdle();

                return true;
            }
        }

        public event Action? ReadyForBackgroundProcessing;
        
        public void RequestBackgroundProcessing()
        {
            if (_backgroundProcessingRequested)
                return;
            _backgroundProcessingRequested = true;
            _handler.PostDelayed(_backgroundSignaler, BackgroundProcessingInterval);
        }

        private void OnBackgroundProcessing()
        {
            if (!_backgroundProcessingRequested)
                return;
            _handler.RemoveCallbacks(_backgroundSignaler);
            _backgroundProcessingRequested = false;
            _backgroundProcessingDeadline = Now + BackgroundProcessingBudget;
            try
            {
                ReadyForBackgroundProcessing?.Invoke();
            }
            finally
            {
                _backgroundProcessingDeadline = null;
            }
        }

        void OnIdle()
        {
            if (!_backgroundProcessingRequested || (CanQueryPendingInput && HasPendingInput))
                return;
            OnBackgroundProcessing();
        }

        public bool CanQueryPendingInput { get; }
        
        // See check in ctor
#pragma warning disable CA1416
        public bool HasPendingInput => _backgroundProcessingDeadline is { } deadline
            ? Now >= deadline
            : !_queue.IsIdle;
#pragma warning restore CA1416
    }
}
