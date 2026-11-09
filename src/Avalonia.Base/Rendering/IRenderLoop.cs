using Avalonia.Metadata;

namespace Avalonia.Rendering
{
    /// <summary>
    /// The application render loop.
    /// </summary>
    /// <remarks>
    /// The render loop is responsible for advancing the animation timer and updating the scene
    /// graph for visible windows.
    /// </remarks>
    [PrivateApi]
    public interface IRenderLoop
    {
        /// <summary>
        /// Adds an update task.
        /// </summary>
        /// <param name="i">The update task.</param>
        /// <remarks>
        /// Registered update tasks will be polled on each tick of the render loop after the
        /// animation timer has been pulsed.
        /// </remarks>
        internal void Add(IRenderLoopTask i);

        /// <summary>
        /// Removes an update task.
        /// </summary>
        /// <param name="i">The update task.</param>
        internal void Remove(IRenderLoopTask i);

        /// <summary>
        /// Indicates if the rendering is done on a non-UI thread.
        /// </summary>
        internal bool RunsInBackground { get; }

        /// <summary>
        /// Wakes up the render loop to schedule the next tick.
        /// Thread-safe: can be called from any thread.
        /// </summary>
        internal void Wakeup();

        /// <summary>
        /// Gets the display timing of the most recent tick, or null if the timer driving the loop
        /// isn't synchronized with the display.
        /// Thread-safe: can be called from any thread.
        /// </summary>
        internal RenderTimerFrameTiming? LastFrameTiming => null;
    }
}
