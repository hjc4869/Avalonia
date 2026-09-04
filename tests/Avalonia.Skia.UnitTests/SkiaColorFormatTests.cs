using Avalonia.Platform;
using Avalonia.Skia;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests;

public class SkiaColorFormatTests
{
    private static readonly PlatformSurfaceColorFormat s_scRgb =
        new(PlatformPixelEncoding.RgbaF16, PlatformColorSpace.ScRgbLinear);

    [Win32Fact("Reference-white remapping is specific to Windows DWM scRGB surfaces.")]
    public void Reference_White_Scale_Is_Ratio_To_Primary_White()
    {
        var volume = new PlatformSurfaceColorVolume(
            new PlatformLuminanceRange(0, 80), 200, new PlatformLuminanceRange(0.005, 1000));

        Assert.Equal(2.5, SkiaColorFormat.GetReferenceWhiteScale(s_scRgb, volume));
    }

    [Win32Fact("Reference-white remapping is specific to Windows DWM scRGB surfaces.")]
    public unsafe void ScRgb_Color_Conversion_Emits_Adjusted_Reference_White()
    {
        var volume = new PlatformSurfaceColorVolume(
            new PlatformLuminanceRange(0, 80), 200, new PlatformLuminanceRange(0.005, 1000));
        var destinationColorSpace = s_scRgb.ToSkColorSpace(volume);
        var imageInfo = new SKImageInfo(
            1, 1, SKColorType.RgbaF16, SKAlphaType.Premul, destinationColorSpace);

        using var surface = SKSurface.Create(imageInfo);
        using var sourceColorSpace = SKColorSpace.CreateSrgbLinear();
        using var shader = SKShader.CreateColor(new SKColorF(1, 1, 1, 1), sourceColorSpace);
        using var paint = new SKPaint { Shader = shader };
        surface.Canvas.DrawRect(SKRect.Create(1, 1), paint);

        var pixels = new float[4];
        var readInfo = new SKImageInfo(
            1, 1, SKColorType.RgbaF32, SKAlphaType.Premul, destinationColorSpace);
        fixed (float* buffer = pixels)
        {
            Assert.True(surface.ReadPixels(readInfo, (nint)buffer, readInfo.RowBytes, 0, 0));
        }

        Assert.Equal(2.5f, pixels[0], 2);
        Assert.Equal(2.5f, pixels[1], 2);
        Assert.Equal(2.5f, pixels[2], 2);
        Assert.Equal(1, pixels[3], 2);
    }

    [Win32Fact("Reference-white remapping is specific to Windows DWM scRGB surfaces.")]
    public unsafe void Untagged_Offscreen_Content_Is_Adjusted_When_Composited()
    {
        using var source = SKSurface.Create(new SKImageInfo(1, 1, SKColorType.Rgba8888));
        source.Canvas.Clear(SKColors.White);
        using var image = source.Snapshot();

        var volume = new PlatformSurfaceColorVolume(
            new PlatformLuminanceRange(0, 80), 200, new PlatformLuminanceRange(0.005, 1000));
        var destinationColorSpace = s_scRgb.ToSkColorSpace(volume);
        using var destination = SKSurface.Create(new SKImageInfo(
            1, 1, SKColorType.RgbaF16, SKAlphaType.Premul, destinationColorSpace));
        destination.Canvas.DrawImage(image, 0, 0);

        var pixels = new float[4];
        var readInfo = new SKImageInfo(
            1, 1, SKColorType.RgbaF32, SKAlphaType.Premul, destinationColorSpace);
        fixed (float* buffer = pixels)
        {
            Assert.True(destination.ReadPixels(readInfo, (nint)buffer, readInfo.RowBytes, 0, 0));
        }

        Assert.Equal(2.5f, pixels[0], 2);
        Assert.Equal(2.5f, pixels[1], 2);
        Assert.Equal(2.5f, pixels[2], 2);
    }
}