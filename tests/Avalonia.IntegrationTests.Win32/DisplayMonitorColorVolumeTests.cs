using Avalonia.Platform;
using Avalonia.Win32.WinRT;
using Xunit;

namespace Avalonia.IntegrationTests.Win32;

public class DisplayMonitorColorVolumeTests
{
    [Fact]
    public void Hdr_Color_Volume_Uses_ScRgb_And_Configured_Sdr_White()
    {
        var volume = DisplayMonitorColorVolume.CreateColorVolume(0.005f, 1000f, true, 2500);

        Assert.Equal(new PlatformSurfaceColorVolume(
            new PlatformLuminanceRange(0, 80),
            200,
            new PlatformLuminanceRange((double)0.005f, 1000)), volume);
        Assert.Equal(5, volume?.HeadroomRatio);
    }

    [Fact]
    public void Sdr_Color_Volume_Is_Display_Referred()
    {
        var volume = DisplayMonitorColorVolume.CreateColorVolume(0.1f, 400f, false, null);

        Assert.Equal(new PlatformSurfaceColorVolume(
            new PlatformLuminanceRange((double)0.1f, 400),
            400,
            new PlatformLuminanceRange((double)0.1f, 400)), volume);
        Assert.Equal(1, volume?.HeadroomRatio);
    }

    [Theory]
    [InlineData(float.NaN, 1000)]
    [InlineData(0, float.NaN)]
    [InlineData(-1, 1000)]
    [InlineData(1, 0)]
    [InlineData(1000, 100)]
    public void Invalid_DisplayMonitor_Luminance_Is_Not_Reported(float minimumNits, float maximumNits)
    {
        Assert.Null(DisplayMonitorColorVolume.CreateColorVolume(minimumNits, maximumNits, true, 2500));
    }

    [Fact]
    public void Hdr_Color_Volume_Without_Sdr_White_Is_Not_Reported()
    {
        Assert.Null(DisplayMonitorColorVolume.CreateColorVolume(0.005f, 1000f, true, null));
    }
}