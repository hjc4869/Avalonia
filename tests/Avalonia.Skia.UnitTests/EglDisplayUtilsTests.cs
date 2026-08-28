using System;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Xunit;
using static Avalonia.OpenGL.Egl.EglConsts;

namespace Avalonia.Skia.UnitTests;

public class EglDisplayUtilsTests
{
    [Fact]
    public void Missing_Egl_Window_Is_Temporarily_Not_Ready()
    {
        var surface = new EglGlPlatformSurface(new MissingWindowInfo());

        Assert.Throws<RenderTargetNotReadyException>(() => surface.CreateGlRenderTarget(null!));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("EGL_EXT_pixel_format_float EGL_EXT_gl_colorspace_scrgb_linear", false)]
    [InlineData("EGL_KHR_gl_colorspace EGL_EXT_pixel_format_float", false)]
    [InlineData("EGL_KHR_gl_colorspace EGL_EXT_gl_colorspace_scrgb_linear_extra", false)]
    [InlineData("EGL_KHR_gl_colorspace EGL_EXT_gl_colorspace_scrgb_linear", true)]
    public void ScRgb_Requires_Both_Window_Surface_Extensions(string? extensions, bool expected)
    {
        Assert.Equal(expected,
            EglDisplayUtils.SupportsWindowSurfaceColorSpace(PlatformColorSpace.ScRgbLinear, extensions));
    }

    [Fact]
    public void ScRgb_Uses_Linear_Extended_Surface_Color_Space()
    {
        Assert.Equal(
            [EGL_GL_COLORSPACE, EGL_GL_COLORSPACE_SCRGB_LINEAR_EXT, EGL_NONE],
            EglDisplayUtils.GetWindowSurfaceAttributes(PlatformColorSpace.ScRgbLinear));
    }

    [Fact]
    public void Unmanaged_Surface_Has_No_Color_Space_Attribute()
    {
        Assert.True(EglDisplayUtils.SupportsWindowSurfaceColorSpace(PlatformColorSpace.Unmanaged, null));
        Assert.Equal([EGL_NONE], EglDisplayUtils.GetWindowSurfaceAttributes(PlatformColorSpace.Unmanaged));
    }

    [Fact]
    public void ScRgb_Color_Volume_Uses_Egl_Luminance_Scale_And_Current_Headroom()
    {
        var volume = EglDisplayUtils.CreateScRgbColorVolume(
            0.005, 1000, 5, PlatformTransferFunction.Pq);

        Assert.Equal(new PlatformSurfaceColorVolume(
            new PlatformLuminanceRange(0, 80),
            80,
            new PlatformLuminanceRange(0.005, 400),
            PlatformTransferFunction.Pq,
            SurfaceNitsPerUnit: 80), volume);
        Assert.Equal(5, volume?.HeadroomRatio);
        Assert.Equal(1, volume?.ReferenceWhiteScale);
    }

    [Fact]
    public void ScRgb_Color_Volume_Uses_Static_Maximum_When_Current_Headroom_Is_Unavailable()
    {
        var volume = EglDisplayUtils.CreateScRgbColorVolume(
            0.005, 1000, null, PlatformTransferFunction.Power, 2.6);

        Assert.Equal(1000, volume?.TargetLuminance.MaximumNits);
        Assert.Equal(12.5, volume?.HeadroomRatio);
        Assert.Equal(2.6, volume?.TransferExponent);
    }

    [Fact]
    public void ScRgb_Color_Volume_Is_Limited_To_Encoding_Maximum()
    {
        var volume = EglDisplayUtils.CreateScRgbColorVolume(
            0, 20000, null, PlatformTransferFunction.Linear);

        Assert.Equal(10000, volume?.TargetLuminance.MaximumNits);
        Assert.Equal(125, volume?.HeadroomRatio);
    }

    [Fact]
    public void Invalid_Power_Transfer_Is_Not_Reported()
    {
        Assert.Null(EglDisplayUtils.CreateScRgbColorVolume(
            0, 1000, null, PlatformTransferFunction.Power));
    }

    [Theory]
    [InlineData(double.NaN, 1000, 5)]
    [InlineData(0, double.NaN, 5)]
    [InlineData(-1, 1000, 5)]
    [InlineData(0, 79, 1)]
    [InlineData(1000, 100, 5)]
    [InlineData(0, 1000, 0.5)]
    public void Invalid_ScRgb_Color_Volume_Is_Not_Reported(
        double minimumNits, double maximumNits, double headroomRatio)
    {
        Assert.Null(EglDisplayUtils.CreateScRgbColorVolume(
            minimumNits, maximumNits, headroomRatio, PlatformTransferFunction.Pq));
    }

    private sealed class MissingWindowInfo : EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo
    {
        public IntPtr Handle => IntPtr.Zero;
        public PixelSize Size => default;
        public double Scaling => 1;
    }
}
