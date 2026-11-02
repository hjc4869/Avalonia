using System;
using Avalonia.Input.GestureRecognizers;

namespace Avalonia.Wayland;

internal sealed class WaylandScrollInertia
{
    private const double DipsPerWheelDelta = 50;
    private VelocityTracker? _velocityTracker;
    private Vector _position;
    private Vector _velocity;
    private ulong _lastMoveTimestamp;
    private TimeSpan _lastTime;

    public bool IsActive => _velocity != default;

    public void Start(ulong timestamp)
    {
        Cancel();
        _velocityTracker = new VelocityTracker();
        _lastMoveTimestamp = timestamp;
    }

    public void AddDelta(ulong timestamp, Vector delta)
    {
        if (_velocityTracker == null || delta == default)
            return;

        _position += delta * DipsPerWheelDelta;
        _velocityTracker.AddPosition(TimeSpan.FromMilliseconds(timestamp), _position);
        _lastMoveTimestamp = timestamp;
    }

    public bool End(ulong timestamp)
    {
        _velocity = timestamp != 0 && _lastMoveTimestamp != 0 && timestamp - _lastMoveTimestamp <= 200
            ? _velocityTracker?.GetFlingVelocity().PixelsPerSecond ?? default
            : default;
        _velocityTracker = null;
        _lastTime = default;
        return IsActive;
    }

    public Vector GetDelta(TimeSpan elapsed)
    {
        if (!IsActive || elapsed <= _lastTime)
            return default;

        var speed = _velocity * Math.Pow(ScrollGestureRecognizer.InertialResistance, elapsed.TotalSeconds);
        var delta = speed * (elapsed - _lastTime).TotalSeconds / DipsPerWheelDelta;
        _lastTime = elapsed;

        if (Math.Abs(speed.X) <= ScrollGestureRecognizer.InertialScrollSpeedEnd &&
            Math.Abs(speed.Y) <= ScrollGestureRecognizer.InertialScrollSpeedEnd)
            _velocity = default;

        return delta;
    }

    public void Cancel()
    {
        _velocityTracker = null;
        _position = default;
        _velocity = default;
        _lastMoveTimestamp = 0;
        _lastTime = default;
    }
}