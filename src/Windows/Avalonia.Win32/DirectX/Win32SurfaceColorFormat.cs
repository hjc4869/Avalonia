using Avalonia.OpenGL.Egl;
using Avalonia.Platform;

namespace Avalonia.Win32.DirectX;

/// <summary>
/// Maps <see cref="Win32ColorMode"/> onto the EGL color buffer formats we ask ANGLE for, and reads
/// back the format that was actually negotiated.
/// </summary>
internal static class Win32SurfaceColorFormat
{
    /// <summary>
    /// The color buffer formats to offer to <c>eglChooseConfig</c>, in priority order, or null when
    /// the application didn't opt in to advanced color.
    /// </summary>
    /// <remarks>
    /// The DWM interprets FP16 composition surfaces as scRGB, so no additional color space
    /// negotiation is needed on Windows: matching the EGL config to an FP16 composition surface is
    /// all that's required.
    /// </remarks>
    public static EglColorBufferFormat[]? RequestedColorBufferFormats =>
        AvaloniaLocator.Current.GetService<Win32PlatformOptions>()?.ColorMode == Win32ColorMode.ExtendedLinear
            ? [EglColorBufferFormat.Float16(PlatformColorSpace.ScRgbLinear), EglColorBufferFormat.Standard]
            : null;

    /// <summary>
    /// The format ANGLE actually gave us, which is what composition surfaces have to be created with.
    /// Falls back to the legacy, non color managed 8 bit sRGB format for any other graphics backend.
    /// </summary>
    public static PlatformSurfaceColorFormat Negotiated(IPlatformGraphicsContext context) =>
        context is EglContext egl ? egl.Display.ColorFormat : PlatformSurfaceColorFormat.Unmanaged;
}
