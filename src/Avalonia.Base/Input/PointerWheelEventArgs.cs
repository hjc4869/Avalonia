namespace Avalonia.Input
{
    public class PointerWheelEventArgs : PointerEventArgs
    {
        public Vector Delta { get; }

        /// <summary>
        /// Gets whether the platform identifies this scroll input as originating from a touchpad.
        /// </summary>
        public bool IsTouchpad { get; init; }

        /// <summary>
        /// Gets the native touchpad lifecycle phase. Phase-only events can have a zero delta.
        /// </summary>
        public TouchpadGesturePhase GesturePhase { get; init; }

        public PointerWheelEventArgs(object? source, IPointer pointer, Visual rootVisual,
            Point rootVisualPosition, ulong timestamp,
            PointerPointProperties properties, KeyModifiers modifiers, Vector delta)
            : base(InputElement.PointerWheelChangedEvent, source, pointer, rootVisual, rootVisualPosition,
                timestamp, properties, modifiers)
        {
            Delta = delta;
        }
    }
}
