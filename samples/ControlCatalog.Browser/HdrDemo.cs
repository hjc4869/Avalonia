using System;
using System.Globalization;
using System.Runtime.InteropServices.JavaScript;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using SkiaSharp;

namespace ControlCatalog.Browser;

internal sealed class HdrDemoApp : Application
{
    internal static bool PreferHdr { get; set; }

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new SimpleTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime lifetime)
            lifetime.MainView = CreateView();
        base.OnFrameworkInitializationCompleted();
    }

    private static Control CreateView()
    {
        var diagnostics = new TextBlock { Text = "Surface: waiting for first frame", TextWrapping = TextWrapping.Wrap };
        var readback = new TextBlock { Text = "Pixel readback: pending", TextWrapping = TextWrapping.Wrap };
        var colorVolume = new TextBlock { Text = "Effective color volume: unknown", TextWrapping = TextWrapping.Wrap };
        var level = new TextBlock { Text = "Highlight: 4.0x SDR white" };
        string? lastResult = null;
        var probe = new HdrProbe((format, volume, reference, highlight) => Dispatcher.UIThread.Post(() =>
        {
            var result = FormattableString.Invariant($"Reference: {reference:F3} | Highlight: {highlight:F3} (linear surface values)");
            var key = $"{format}|{volume}|{result}";
            if (key == lastResult)
                return;
            lastResult = key;
            diagnostics.Text = format.IsExtendedRange
                ? $"Surface: {format} | Extended-range output"
                : $"Surface: {format} | SDR{(PreferHdr ? " fallback" : " output")}";
            readback.Text = result;
            colorVolume.Text = volume is { } effective
                ? FormattableString.Invariant($"Effective: {effective.SurfaceNitsPerUnit:F0} nits/unit | Peak: {effective.TargetLuminance.MaximumNits:F1} nits ({effective.HeadroomRatio:F3}x)")
                : "Effective color volume: unknown";
            Console.WriteLine($"[hdr] {diagnostics.Text}; {result}; {colorVolume.Text}");
            JSHost.GlobalThis.SetProperty("avaloniaHdrSurface", format.ToString());
            JSHost.GlobalThis.SetProperty("avaloniaHdrReference", (double)reference);
            JSHost.GlobalThis.SetProperty("avaloniaHdrHighlight", (double)highlight);
            JSHost.GlobalThis.SetProperty("avaloniaHdrSurfaceNitsPerUnit", volume?.SurfaceNitsPerUnit ?? double.NaN);
            JSHost.GlobalThis.SetProperty("avaloniaHdrReferenceWhiteNits", volume?.ReferenceWhiteNits ?? double.NaN);
            JSHost.GlobalThis.SetProperty("avaloniaHdrPeakNits", volume?.TargetLuminance.MaximumNits ?? double.NaN);
        }));
        var hdr = new CheckBox { Content = "Request HDR output", IsChecked = PreferHdr };
        ToolTip.SetTip(hdr, "Reloads the sample with HDR enabled or disabled.");
        hdr.Click += (_, _) =>
        {
            using var location = JSHost.GlobalThis.GetPropertyAsJSObject("location")!;
            location.SetProperty("search", $"?HdrDemo=true&PreferHdr={hdr.IsChecked == true}");
        };
        var requestHeadroom = new Button { Content = "Read display headroom" };
        ToolTip.SetTip(requestHeadroom, "Requests window-management permission for effective luminance values.");
        requestHeadroom.Click += async (_, _) =>
        {
            requestHeadroom.IsEnabled = false;
            try
            {
                if (TopLevel.GetTopLevel(probe)?.Screens is not { } screens || !await screens.RequestScreenDetails())
                    colorVolume.Text = "Effective color volume: permission unavailable";
                probe.InvalidateVisual();
            }
            finally
            {
                requestHeadroom.IsEnabled = true;
            }
        };
        var slider = new Slider { Minimum = 1, Maximum = 8, Value = 4, TickFrequency = 0.5, IsSnapToTickEnabled = true };
        slider.PropertyChanged += (_, change) =>
        {
            if (change.Property != RangeBase.ValueProperty)
                return;
            probe.HighlightLevel = (float)slider.Value;
            level.Text = FormattableString.Invariant($"Highlight: {slider.Value:F1}x SDR white");
            probe.InvalidateVisual();
        };

        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(17, 19, 20)),
            Child = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new StackPanel
                {
                    Margin = new Thickness(24),
                    MaxWidth = 900,
                    Spacing = 14,
                    Children =
                    {
                        new TextBlock { Text = "Avalonia HDR", FontSize = 28, FontWeight = FontWeight.SemiBold },
                        hdr,
                        diagnostics,
                        colorVolume,
                        requestHeadroom,
                        level,
                        slider,
                        probe,
                        readback
                    }
                }
            }
        };
    }
}

internal sealed class HdrProbe : Control
{
    private readonly Action<PlatformSurfaceColorFormat, PlatformSurfaceColorVolume?, float, float> _report;
    private IPlatformSurfaceColorVolumeFeature? _colorVolumeFeature;
    private int _colorVolumeChanges;
    internal float HighlightLevel { get; set; } = 4;

    internal HdrProbe(Action<PlatformSurfaceColorFormat, PlatformSurfaceColorVolume?, float, float> report)
    {
        _report = report;
        Height = 440;
        HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
    {
        base.OnAttachedToVisualTree(args);
        _colorVolumeFeature = TopLevel.GetTopLevel(this)?.PlatformImpl?.TryGetFeature<IPlatformSurfaceColorVolumeFeature>();
        if (_colorVolumeFeature is not null)
            _colorVolumeFeature.PreferredColorVolumeChanged += OnColorVolumeChanged;
        OnColorVolumeChanged(this, EventArgs.Empty);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    {
        if (_colorVolumeFeature is not null)
            _colorVolumeFeature.PreferredColorVolumeChanged -= OnColorVolumeChanged;
        _colorVolumeFeature = null;
        base.OnDetachedFromVisualTree(args);
    }

    private void OnColorVolumeChanged(object? sender, EventArgs args)
    {
        var volume = _colorVolumeFeature?.PreferredColorVolume;
        JSHost.GlobalThis.SetProperty("avaloniaHdrTopLevelPeakNits", volume?.TargetLuminance.MaximumNits ?? double.NaN);
        JSHost.GlobalThis.SetProperty("avaloniaHdrVolumeEvents", ++_colorVolumeChanges);
        JSHost.GlobalThis.SetProperty("avaloniaHdrVolumeEventOnUiThread", Dispatcher.UIThread.CheckAccess());
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var half = (width - 16) / 2;
        context.Custom(new ProbeDrawOp(new Rect(Bounds.Size), HighlightLevel, _report));
        DrawLabel(context, "SDR white / 1x", 0, 0);
        DrawLabel(context, FormattableString.Invariant($"Highlight / {HighlightLevel:0.#}x"), half + 16, 0);
        DrawLabel(context, "Linear-light ramp", 0, 188);
        for (var column = 0; column < 4; column++)
            DrawLabel(context, $"{1 << column}x", column * (width + 8) / 4, 286);
        DrawLabel(context, "sRGB / Display P3", 0, 330);
    }

    private static void DrawLabel(DrawingContext context, string value, double left, double top)
    {
        var text = new FormattedText(value, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, 14, Brushes.White);
        context.DrawText(text, new Point(left, top));
    }

    private sealed class ProbeDrawOp : ICustomDrawOperation
    {
        private static readonly SKColorSpace s_linear = SKColorSpace.CreateSrgbLinear();
        private static readonly SKColorSpace s_srgb = SKColorSpace.CreateSrgb();
        private static readonly SKColorSpace s_p3 = SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3);
        private static readonly SKColorF[] s_primaries = [new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)];
        private readonly float _highlight;
        private readonly Action<PlatformSurfaceColorFormat, PlatformSurfaceColorVolume?, float, float> _report;

        internal ProbeDrawOp(Rect bounds, float highlight,
            Action<PlatformSurfaceColorFormat, PlatformSurfaceColorVolume?, float, float> report)
        {
            Bounds = bounds;
            _highlight = highlight;
            _report = report;
        }

        public Rect Bounds { get; }
        public bool HitTest(Point point) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (Bounds.Width < 32 || context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
                return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            using var paint = new SKPaint();
            var width = (float)Bounds.Width;
            var half = (width - 16) / 2;
            Fill(canvas, paint, SKRect.Create(0, 32, half, 128), new(1, 1, 1), s_linear);
            Fill(canvas, paint, SKRect.Create(half + 16, 32, half, 128), new(_highlight, _highlight, _highlight), s_linear);
            Fill(canvas, paint, SKRect.Create(half + 16 + half / 3, 72, half / 3, 48), new(1, 1, 1), s_linear);

            for (var column = 0; column < 4; column++)
            {
                var brightness = 1 << column;
                Fill(canvas, paint, SKRect.Create(column * (width + 8) / 4, 220, (width - 24) / 4, 56),
                    new(brightness, brightness, brightness), s_linear);
            }

            for (var column = 0; column < s_primaries.Length; column++)
            {
                var left = column * (width + 8) / 3;
                var patchWidth = (width - 16) / 3;
                Fill(canvas, paint, SKRect.Create(left, 362, patchWidth, 30), s_primaries[column], s_srgb);
                Fill(canvas, paint, SKRect.Create(left, 398, patchWidth, 30), s_primaries[column], s_p3);
            }

            if (lease.SkSurface is { } surface)
            {
                var reference = canvas.TotalMatrix.MapPoint(half / 2, 48);
                var highlight = canvas.TotalMatrix.MapPoint(half + 24, 48);
                _report(lease.ColorFormat, lease.PreferredColorVolume,
                    ReadRed(surface, reference, lease.SkColorSpace, lease.ColorFormat.Encoding),
                    ReadRed(surface, highlight, lease.SkColorSpace, lease.ColorFormat.Encoding));
            }
        }

        private static void Fill(SKCanvas canvas, SKPaint paint, SKRect rect, SKColorF color, SKColorSpace colorSpace)
        {
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Bottom),
                [color, color], colorSpace, [0f, 1f], SKShaderTileMode.Clamp);
            paint.Shader = shader;
            canvas.DrawRect(rect, paint);
            paint.Shader = null;
        }

        private static unsafe float ReadRed(SKSurface surface, SKPoint point, SKColorSpace? colorSpace,
            PlatformPixelEncoding encoding)
        {
            var isFloat = encoding == PlatformPixelEncoding.RgbaF16;
            var info = new SKImageInfo(1, 1, isFloat ? SKColorType.RgbaF16 : SKColorType.Rgba8888,
                SKAlphaType.Premul, colorSpace);
            byte* pixel = stackalloc byte[8];
            if (!surface.ReadPixels(info, (IntPtr)pixel, info.RowBytes, (int)point.X, (int)point.Y))
                return float.NaN;
            return isFloat ? (float)*(Half*)pixel : pixel[0] / 255f;
        }
    }
}