using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Timers;
using Avalonia.Logging;
using Avalonia.Rendering;
using Avalonia.Wayland.Server.Persistent;

namespace Avalonia.Wayland.Server;

partial class WaylandWorker
{
    private bool _renderLoopWakeupPending = false;
    private ServerSignaler _renderLoopWakeupSignaler = null!;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan? _renderLoopStarvedSince;
    private readonly object _renderLoopStarvationLock = new object();
    
    
    private readonly RenderLoopImpl _renderLoop = new RenderLoopImpl();
    public IRenderLoop RenderLoop => _renderLoop;
    
    // The target FPS for UI thread animations when Wayland compositor doesn't think
    // that we should be rendering yet
    private const int ThrottledUiThreadFps = 20;
    private static readonly TimeSpan s_RenderLoopStarvationInterval = TimeSpan.FromSeconds(1.0 / ThrottledUiThreadFps);

    private readonly System.Timers.Timer _renderLoopStarvationTimer = new System.Timers.Timer(s_RenderLoopStarvationInterval);

    internal class RenderLoopImpl : IRenderLoop
    {
        private readonly List<IRenderLoopTask> _tasks = new();
        private readonly List<IRenderLoopTask> _tasksCopy = new();
        private int _inTick;
        internal Action? WakeupCallback;

        public bool RunsInBackground => true;
        
        public void Add(IRenderLoopTask i)
        {
            lock (_tasks)
                _tasks.Add(i);
            Wakeup();
        }

        public void Remove(IRenderLoopTask i)
        {
            lock (_tasks)
                _tasks.Remove(i);
        }

        public void Wakeup()
        {
            WakeupCallback?.Invoke();
        }

        public bool DoTick()
        {
            if (Interlocked.CompareExchange(ref _inTick, 1, 0) != 0)
                return false;
            try
            {
                lock (_tasks)
                {
                    _tasksCopy.Clear();
                    _tasksCopy.AddRange(_tasks);
                }

                var needsNextTick = false;
                for (int i = 0; i < _tasksCopy.Count; i++)
                    needsNextTick |= _tasksCopy[i].Render();

                _tasksCopy.Clear();
                return needsNextTick;
            }
            finally
            {
                Interlocked.Exchange(ref _inTick, 0);
            }
        }
    }
    
    public void WakeupRenderLoop() => _renderLoopWakeupPending = true;

    public void AnyThreadWakeupRenderLoop() => _renderLoopWakeupSignaler.Signal();
    
    void InitRenderTimer()
    {
        _renderLoopWakeupSignaler = new ServerSignaler(this, WakeupRenderLoop);
        _renderLoop.WakeupCallback = new ServerSignaler(this, RequestRenderLoopTick).Signal;
        
        Compositor.AfterCommit += delegate
        {
            if (_hasPendingServerJobs)
            {
                _hasPendingServerJobs = false;
                AnyThreadWakeupRenderLoop();
            }
        };
        _renderLoopStarvationTimer.Elapsed += delegate { OnRenderLoopStarved(); };
    }

    private void RequestRenderLoopTick()
    {
        if (_renderLoopWakeupPending)
            return;

        if (!RequestFrameCallbacks())
        {
            WakeupRenderLoop();
            return;
        }

        lock (_renderLoopStarvationLock)
        {
            if (_renderLoopStarvedSince == null)
            {
                _renderLoopStarvedSince = _clock.Elapsed;
                _renderLoopStarvationTimer.Enabled = true;
            }
        }
    }

    private bool RequestFrameCallbacks()
    {
        var pending = false;
        foreach (var persistent in _persistentObjects)
            if (persistent is WSurface surface)
                pending |= surface.EnsureFrameCallback();
        return pending;
    }

    private void OnRenderLoopStarved()
    {
        lock (_renderLoopStarvationLock)
        {
            if (_renderLoopStarvedSince.HasValue &&
                _renderLoopStarvedSince.Value + s_RenderLoopStarvationInterval < _clock.Elapsed)
                AnyThreadWakeupRenderLoop();
        }
    }

    private void TickRenderLoopIfNeeded()
    {
        if (_renderLoopWakeupPending)
        {
            lock (_renderLoopStarvationLock)
            {
                _renderLoopStarvedSince = null;
                _renderLoopStarvationTimer.Stop();
            }

            _renderLoopWakeupPending = false;
            try
            {
                if (_renderLoop.DoTick())
                    RequestFrameCallbacks();
            }
            catch (Exception ex)
            {
                AnyThreadWakeupRenderLoop();
                Logger.TryGet(LogEventLevel.Error, LogArea.Visual)?.Log(this, "Exception in render loop: {Error}", ex);
            }
        }
    }
}