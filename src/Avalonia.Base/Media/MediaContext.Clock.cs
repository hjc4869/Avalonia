using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia.Animation;
using Avalonia.Reactive;
using Avalonia.Rendering;

namespace Avalonia.Media;

internal partial class MediaContext
{
    private readonly MediaContextClock _clock;
    public IGlobalClock Clock => _clock;
    private readonly long _timeOrigin = Stopwatch.GetTimestamp();
    private TimeSpan Now => Stopwatch.GetElapsedTime(_timeOrigin);
    
    class MediaContextClock : IGlobalClock
    {
        private readonly MediaContext _parent;
        private readonly List<IObserver<TimeSpan>> _observers = new();
        private readonly List<IObserver<TimeSpan>> _newObservers = new();
        private Queue<Action<TimeSpan>> _queuedAnimationFrames = new();
        private Queue<Action<TimeSpan>> _queuedAnimationFramesNext = new();
        private TimeSpan _currentAnimationTimestamp;
        public bool HasNewSubscriptions => _newObservers.Count > 0;
        public bool HasSubscriptions => _observers.Count > 0 || _queuedAnimationFrames.Count > 0;

        public MediaContextClock(MediaContext parent)
        {
            _parent = parent;
        }

        public IDisposable Subscribe(IObserver<TimeSpan> observer)
        {
            _parent.ScheduleRender(false);
            _parent._dispatcher.VerifyAccess();
            _observers.Add(observer);
            _newObservers.Add(observer);
            return Disposable.Create(() =>
            {
                _parent._dispatcher.VerifyAccess();
                _observers.Remove(observer);
            });
        }
        
        public void RequestAnimationFrame(Action<TimeSpan> action)
        {
            _parent.ScheduleRender(false);
            _queuedAnimationFrames.Enqueue(action);
        }

        public void Pulse(TimeSpan now)
        {
            _newObservers.Clear();
            _currentAnimationTimestamp = now;
            
            // We are swapping the queues before enumeration
            (_queuedAnimationFrames, _queuedAnimationFramesNext) = (_queuedAnimationFramesNext, _queuedAnimationFrames);
            var animationFrames = _queuedAnimationFramesNext;
            while (animationFrames.TryDequeue(out var callback))
                callback(now);
            
            foreach (var observer in _observers.ToArray())
                observer.OnNext(_currentAnimationTimestamp);
        }
        
        public void PulseNewSubscriptions()
        {
            foreach (var observer in _newObservers.ToArray())
                observer.OnNext(_currentAnimationTimestamp);
            _newObservers.Clear();
        }

        public PlayState PlayState
        {
            get => PlayState.Run;
            set => throw new InvalidOperationException();
        }
    }

    public void RequestAnimationFrame(Action<TimeSpan> action) => _clock.RequestAnimationFrame(action);

    private TimeSpan _lastAnimationFrameTime;

    /// <summary>
    /// Gets the time the clock was last pulsed with, on the same time base as animation frame callbacks.
    /// </summary>
    internal TimeSpan LastAnimationFrameTime => _lastAnimationFrameTime;

    /// <summary>
    /// Returns the time to sample animations at for the frame being prepared now.
    /// </summary>
    /// <remarks>
    /// When the render loop is driven by the display, this is the time the frame committed by this pass is
    /// expected to be shown: the target of the first timer tick after the most recent one, moved forward by
    /// whole frame intervals if that tick is already due. Consecutive frames then advance animations by exact
    /// frame intervals regardless of when the dispatcher gets to run the pass. Otherwise it's the current time.
    /// The returned value never goes backwards.
    /// </remarks>
    private TimeSpan GetAnimationFrameTime()
    {
        var frameTime = Now;
        if (TryGetDisplayFrameTiming() is { } timing && timing.FrameInterval > 0)
        {
            var interval = timing.FrameInterval;
            var nowTimestamp = Stopwatch.GetTimestamp();
            var target = timing.TargetTimestamp + interval;
            var earliest = nowTimestamp + interval / 2;
            if (target < earliest)
                target += (earliest - target + interval - 1) / interval * interval;

            // A target far ahead means the timing is bogus, don't let it run animations ahead of time
            if (target - nowTimestamp <= Math.Max(Stopwatch.Frequency / 20, 3 * interval))
                frameTime = Stopwatch.GetElapsedTime(_timeOrigin, target);
        }

        if (frameTime < _lastAnimationFrameTime)
            frameTime = _lastAnimationFrameTime;
        return _lastAnimationFrameTime = frameTime;
    }

    private RenderTimerFrameTiming? TryGetDisplayFrameTiming()
    {
        foreach (var topLevel in _topLevels.Values)
        {
            if (topLevel.Compositor.Loop.LastFrameTiming is { } timing)
                return timing;
        }

        return null;
    }
}
