namespace Avalonia.Input;

/// <summary>
/// Identifies the contact and inertia phases of a native touchpad scroll sequence.
/// </summary>
public enum TouchpadGesturePhase
{
    /// <summary>The platform does not provide gesture lifecycle information.</summary>
    None,

    /// <summary>A new contact sequence has started.</summary>
    Began,

    /// <summary>The contacts are moving on the touchpad.</summary>
    Changed,

    /// <summary>The contacts have been released; inertia may follow.</summary>
    Ended,

    /// <summary>The released gesture is continuing under platform inertia.</summary>
    Inertia,

    /// <summary>The contact sequence was interrupted.</summary>
    Cancelled
}