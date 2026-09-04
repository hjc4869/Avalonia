using Avalonia.Platform;
using Avalonia.Win32.WinRT;
using Xunit;

namespace Avalonia.IntegrationTests.Win32;

public class DisplayMonitorColorVolumeTests
{
    [Fact]
    public void Hdr_Color_Volume_Uses_ScRgb_And_Configured_Sdr_White()
    {
        var volume = DisplayMonitorColorVolume.CreateColorVolume(0.005f, 1000f, true, 200);

        Assert.Equal(200, volume?.ReferenceWhiteNits);
        Assert.Equal(1000, volume?.TargetLuminance?.MaximumNits);
        Assert.Equal(80, volume?.SurfaceNitsPerUnit);
        Assert.Equal(2.5, volume?.ReferenceWhiteScale);
        Assert.Equal(5, volume?.HeadroomRatio);
        Assert.Equal(5, volume?.MaximumHeadroomRatio);
        Assert.Equal(PlatformToneMappingMode.Client, volume?.ToneMapping);
        Assert.Equal(PlatformLuminanceBasis.DisplayReported, volume?.LuminanceBasis);
    }

    [Fact]
    public void Sdr_Color_Volume_Is_Display_Referred()
    {
        var volume = DisplayMonitorColorVolume.CreateColorVolume(0.1f, 400f, false, null);

        Assert.Null(volume?.ReferenceWhiteNits);
        Assert.Null(volume?.TargetLuminance);
        Assert.Null(volume?.SurfaceNitsPerUnit);
        Assert.Equal(1, volume?.ReferenceWhiteScale);
        Assert.Equal(1, volume?.HeadroomRatio);
        Assert.Equal(1, volume?.MaximumHeadroomRatio);
    }

    [Theory]
    [InlineData(float.NaN, 1000)]
    [InlineData(0, float.NaN)]
    [InlineData(-1, 1000)]
    [InlineData(1, 0)]
    [InlineData(1000, 100)]
    public void Invalid_DisplayMonitor_Luminance_Preserves_White(float minimumNits, float maximumNits)
    {
        var volume = DisplayMonitorColorVolume.CreateColorVolume(minimumNits, maximumNits, true, 200);
        Assert.Equal(200, volume?.ReferenceWhiteNits);
        Assert.Equal(2.5, volume?.ReferenceWhiteScale);
    }

    [Fact]
    public void Hdr_Color_Volume_Without_Sdr_White_Preserves_Peak_And_Policy()
    {
        var volume = DisplayMonitorColorVolume.CreateColorVolume(0.005f, 1000f, true, null);
        Assert.Equal(1000, volume?.TargetLuminance?.MaximumNits);
        Assert.Null(volume?.ReferenceWhiteNits);
        Assert.Null(volume?.ReferenceWhiteScale);
        Assert.Null(volume?.HeadroomRatio);
        Assert.Null(volume?.MaximumHeadroomRatio);
        Assert.Equal(PlatformToneMappingMode.Client, volume?.ToneMapping);
    }

    [Fact]
    public void Missing_Peak_Is_Not_Required_For_White_Scale()
    {
        var volume = DisplayMonitorColorVolume.CreateColorVolume(null, new(true, 200));
        Assert.Null(volume?.TargetLuminance);
        Assert.Null(volume?.HeadroomRatio);
        Assert.Null(volume?.MaximumHeadroomRatio);
        Assert.Equal(2.5, volume?.ReferenceWhiteScale);
    }
}