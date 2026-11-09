using System;
using Avalonia.Metadata;

namespace Avalonia.Rendering
{
    /// <summary>
    /// Defines the interface implemented by an application render timer.
    /// </summary>
    [PrivateApi]
    public interface IRenderTimer
    {
        /// <summary>
        /// Gets or sets the callback to be invoked when the timer ticks.
        /// This property can be set from any thread, but it's guaranteed that it's not set concurrently
        /// (i. e. render loop always does it under a lock).
        /// Setting the value to null suggests the timer to stop ticking, however
        /// timer is allowed to produce ticks on the previously set value as long as it stops doing so
        /// </summary>
        /// <remarks>
        /// The callback can be invoked on any thread
        /// </remarks>
        Action<TimeSpan>? Tick { get; set; }

        /// <summary>
        /// Indicates if the timer ticks on a non-UI thread.
        /// </summary>
        bool RunsInBackground { get; }
    }

    /// <summary>
    /// Implemented by render timers that are driven by the display and know when the frame rendered
    /// from a tick is going to be shown.
    /// </summary>
    /// <remarks>
    /// Animations sampled at the presentation time advance by exactly one frame interval per displayed frame,
    /// while animations sampled at the time a callback happens to run inherit the scheduling jitter of that callback.
    /// </remarks>
    [PrivateApi]
    public interface IRenderTimerWithFrameTiming : IRenderTimer
    {
        /// <summary>
        /// Gets the timing of the most recent tick, or null if the timer hasn't ticked yet.
        /// The value is updated before <see cref="IRenderTimer.Tick"/> is invoked and can be read from any thread.
        /// </summary>
        RenderTimerFrameTiming? LastFrameTiming { get; }
    }

    /// <summary>
    /// Describes the display frame a render timer tick is producing.
    /// </summary>
    [PrivateApi]
    public readonly struct RenderTimerFrameTiming : IEquatable<RenderTimerFrameTiming>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="RenderTimerFrameTiming"/> struct.
        /// </summary>
        /// <param name="targetTimestamp">
        /// The time the frame is expected to be shown, in <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> units.
        /// </param>
        /// <param name="frameInterval">
        /// The interval between display frames, in <see cref="System.Diagnostics.Stopwatch.Frequency"/> ticks.
        /// </param>
        public RenderTimerFrameTiming(long targetTimestamp, long frameInterval)
        {
            TargetTimestamp = targetTimestamp;
            FrameInterval = frameInterval;
        }

        /// <summary>
        /// Gets the time the frame is expected to be shown, in <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> units.
        /// </summary>
        public long TargetTimestamp { get; }

        /// <summary>
        /// Gets the interval between display frames, in <see cref="System.Diagnostics.Stopwatch.Frequency"/> ticks.
        /// </summary>
        public long FrameInterval { get; }

        /// <inheritdoc />
        public bool Equals(RenderTimerFrameTiming other) =>
            TargetTimestamp == other.TargetTimestamp && FrameInterval == other.FrameInterval;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is RenderTimerFrameTiming other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(TargetTimestamp, FrameInterval);

        public static bool operator ==(RenderTimerFrameTiming left, RenderTimerFrameTiming right) => left.Equals(right);

        public static bool operator !=(RenderTimerFrameTiming left, RenderTimerFrameTiming right) => !left.Equals(right);
    }
}
