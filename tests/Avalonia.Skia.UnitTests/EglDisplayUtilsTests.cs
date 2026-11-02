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

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void ScRgb_Color_Volume_Reports_Relative_Headroom_Without_Inventing_Nits(double headroomRatio)
    {
        var volume = EglDisplayUtils.CreateScRgbColorVolume(
            0.005, 1000, headroomRatio, PlatformTransferFunction.Pq, maximumHeadroomRatio: 8);

        Assert.Null(volume.ReferenceWhiteNits);
        Assert.Null(volume.SurfaceNitsPerUnit);
        Assert.Equal(1000, volume.TargetLuminance?.MaximumNits);
        Assert.Equal(headroomRatio, volume.HeadroomRatio);
        Assert.Equal(8, volume.MaximumHeadroomRatio);
        Assert.Equal(1, volume.ReferenceWhiteScale);
        Assert.Equal(PlatformToneMappingMode.Client, volume.ToneMapping);
    }

    [Fact]
    public void Missing_Headroom_Is_Not_Derived_From_Static_Peak()
    {
        var volume = EglDisplayUtils.CreateScRgbColorVolume(
            0.005, 1000, null, PlatformTransferFunction.Power, 2.6);

        Assert.Equal(1000, volume.TargetLuminance?.MaximumNits);
        Assert.Null(volume.HeadroomRatio);
        Assert.Null(volume.MaximumHeadroomRatio);
        Assert.Equal(2.6, volume.TransferExponent);
    }

    [Fact]
    public void Missing_Nits_Do_Not_Hide_Relative_Headroom()
    {
        var volume = EglDisplayUtils.CreateScRgbColorVolume(
            null, null, 2, PlatformTransferFunction.Linear, maximumHeadroomRatio: 4);

        Assert.Null(volume.TargetLuminance);
        Assert.Equal(PlatformLuminanceBasis.Unknown, volume.LuminanceBasis);
        Assert.Equal(2, volume.HeadroomRatio);
        Assert.Equal(4, volume.MaximumHeadroomRatio);
    }

    [Fact]
    public void Invalid_Power_Transfer_Is_Not_Reported()
    {
        var volume = EglDisplayUtils.CreateScRgbColorVolume(0, 1000, 2, PlatformTransferFunction.Power);
        Assert.Equal(PlatformTransferFunction.Unknown, volume.Transfer);
        Assert.Equal(2, volume.HeadroomRatio);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1)]
    [InlineData(1001)]
    public void Invalid_Minimum_Does_Not_Hide_Peak_Or_Policy(double minimumNits)
    {
        var volume = EglDisplayUtils.CreateScRgbColorVolume(minimumNits, 1000, 2, PlatformTransferFunction.Pq);
        Assert.Null(volume.TargetLuminance?.MinimumNits);
        Assert.Equal(1000, volume.TargetLuminance?.MaximumNits);
        Assert.Equal(PlatformToneMappingMode.Client, volume.ToneMapping);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0.5)]
    [InlineData(2d)]
    public void Missing_Or_Invalid_Maximum_Headroom_Falls_Back_To_Current(double? maximumHeadroom)
    {
        var volume = EglDisplayUtils.CreateScRgbColorVolume(null, null, 4, PlatformTransferFunction.Linear,
            maximumHeadroomRatio: maximumHeadroom);
        Assert.Equal(4, volume.HeadroomRatio);
        Assert.Equal(4, volume.MaximumHeadroomRatio);
    }

    [Fact]
    public void Sdr_Current_Headroom_Is_Also_Used_As_Missing_Maximum()
    {
        var volume = EglDisplayUtils.CreateScRgbColorVolume(null, null, 1, PlatformTransferFunction.Linear);
        Assert.Equal(1, volume.HeadroomRatio);
        Assert.Equal(1, volume.MaximumHeadroomRatio);
    }

    [Fact]
    public void Known_Maximum_Does_Not_Fill_Unknown_Current_Headroom()
    {
        var volume = EglDisplayUtils.CreateScRgbColorVolume(null, null, null, PlatformTransferFunction.Linear,
            maximumHeadroomRatio: 4);
        Assert.Null(volume.HeadroomRatio);
        Assert.Equal(4, volume.MaximumHeadroomRatio);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0.5)]
    public void Invalid_Current_Headroom_Preserves_Native_Target_And_Potential(double headroom)
    {
        var volume = EglDisplayUtils.CreateScRgbColorVolume(0, 1000, headroom, PlatformTransferFunction.Linear,
            maximumHeadroomRatio: 4);
        Assert.Null(volume.HeadroomRatio);
        Assert.Equal(4, volume.MaximumHeadroomRatio);
        Assert.Equal(1000, volume.TargetLuminance?.MaximumNits);
    }

    [Fact]
    public void Nominal_Nits_Do_Not_Imply_Headroom_Or_White_Scale()
    {
        var volume = new PlatformSurfaceColorVolume(new(0, 80), 80, new(0, 10000))
        {
            LuminanceBasis = PlatformLuminanceBasis.Nominal,
            ToneMapping = PlatformToneMappingMode.Platform
        }.Normalize();
        Assert.Null(volume.HeadroomRatio);
        Assert.Null(volume.MaximumHeadroomRatio);
        Assert.Null(volume.ReferenceWhiteScale);
        Assert.Equal(10000, volume.TargetLuminance?.MaximumNits);
    }

    [Fact]
    public void Content_Metadata_Is_Independent_Of_Display_Headroom()
    {
        var metadata = new PlatformHdrContentMetadata(8, 203, new(0.005, 2000));
        Assert.Equal(metadata, PlatformHdrContentMetadata.Normalize(true, metadata));
        Assert.Null(PlatformHdrContentMetadata.Normalize(false, metadata));
        Assert.Null(PlatformHdrContentMetadata.Normalize(true, null));
    }

    [Fact]
    public void Invalid_Content_Metadata_Fields_Are_Removed_Independently()
    {
        var metadata = PlatformHdrContentMetadata.Normalize(true,
            new(double.PositiveInfinity, 203, new(double.NaN, 2000)));
        Assert.Null(metadata?.HeadroomRatio);
        Assert.Equal(203, metadata?.ReferenceWhiteNits);
        Assert.Null(metadata?.MasteringLuminance?.MinimumNits);
        Assert.Equal(2000, metadata?.MasteringLuminance?.MaximumNits);
        Assert.Null(PlatformHdrContentMetadata.Normalize(true, new(0.5, -1, new(-1, double.NaN))));
    }

    private sealed class MissingWindowInfo : EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo
    {
        public IntPtr Handle => IntPtr.Zero;
        public PixelSize Size => default;
        public double Scaling => 1;
    }
}
