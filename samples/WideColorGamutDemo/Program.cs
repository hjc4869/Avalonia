using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using SkiaSharp;

namespace WideColorGamutDemo;

internal static class Program
{
    internal static bool SelfTest;
    internal static string RequestedMode = "Standard";
    internal static int Checks;
    internal static int Failures;

    public static void Main(string[] args)
    {
        var extended = args.Any(arg => arg.Equals("extendedlinear", StringComparison.OrdinalIgnoreCase));
        SelfTest = args.Contains("--self-test");
        var preference = args.FirstOrDefault(arg => arg.StartsWith("--hdr=", StringComparison.Ordinal));
        WaylandHdrPresentationMode[]? preferences = preference is null ? null : preference[6..].Split(',')
            .Where(value => value.Length > 0)
            .Select(value => Enum.TryParse<WaylandHdrPresentationMode>(value, true, out var mode) && Enum.IsDefined(mode)
                ? mode : throw new ArgumentException($"Unknown HDR presentation: {value}"))
            .ToArray();
        RequestedMode = preferences is not null ? string.Join(", ", preferences)
            : extended ? "ExtendedLinear" : "Standard";

        Console.WriteLine($"[demo] requested presentation: {RequestedMode}");
        Trace.Listeners.Add(new ConsoleTraceListener());

        var builder = AppBuilder.Configure<App>();

        if (OperatingSystem.IsWindows())
        {
            AvaloniaLocator.CurrentMutable.Bind<Win32PlatformOptions>()
                .ToConstant(new Win32PlatformOptions
                {
                    ColorMode = extended ? Win32ColorMode.ExtendedLinear : Win32ColorMode.Standard
                });
            builder = builder.UseWin32();
        }
        else
        {
            AvaloniaLocator.CurrentMutable.Bind<WaylandPlatformOptions>()
                .ToConstant(new WaylandPlatformOptions
                {
                    ColorMode = extended ? WaylandColorMode.ExtendedLinear : WaylandColorMode.Standard,
                    HdrPresentationPreferences = preferences,
                    UseDmabufSwapchain = args.Contains("--dmabuf")
                });
            builder = builder.UseWayland();
        }

        builder
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont()
            .LogToTrace(LogEventLevel.Information, "Wayland", "OpenGL", "Win32")
            .StartWithClassicDesktopLifetime(Array.Empty<string>());

        if (SelfTest)
        {
            Console.WriteLine($"[self-test] checks={Checks}, failures={Failures}");
            Environment.ExitCode = Checks >= 5 && Failures == 0 ? 0 : 1;
        }
    }
}

internal sealed class App : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new Window
            {
                Title = $"Avalonia HDR probe: {Program.RequestedMode}",
                Width = 900,
                Height = 680,
                MinWidth = 680,
                MinHeight = 620,
                // Background = Brushes.Black,
                Content = CreateProbe()
            };

        base.OnFrameworkInitializationCompleted();
    }

    private static Control CreateProbe()
    {
        var probe = new ColorProbe();
        var hdr = new CheckBox { Content = "HDR content", IsChecked = false };
        var content = new Slider { Minimum = 1, Maximum = 16, Value = 8, Width = 180 };
        var contentLabel = new TextBlock { Text = "Content peak: 8x", VerticalAlignment = VerticalAlignment.Center };
        var manual = new CheckBox { Content = "Explicit client target", IsChecked = false };
        var target = new NumericUpDown { Minimum = 1, Maximum = 16, Value = 4, Width = 100, IsEnabled = false };
        void Update()
        {
            target.IsEnabled = manual.IsChecked == true;
            contentLabel.Text = $"Content peak: {content.Value:F1}x";
            probe.SetContent(hdr.IsChecked == true, content.Value,
                manual.IsChecked == true ? (double)(target.Value ?? 1) : null);
        }
        hdr.IsCheckedChanged += (_, _) => Update();
        manual.IsCheckedChanged += (_, _) => Update();
        target.ValueChanged += (_, _) => Update();
        content.PropertyChanged += (_, args) =>
        {
            if (args.Property == RangeBase.ValueProperty)
                Update();
        };
        var toolbar = new WrapPanel
        {
            Margin = new Thickness(10),
            Children = { hdr, contentLabel, content, manual, target }
        };
        foreach (var child in toolbar.Children)
            child.Margin = new Thickness(0, 0, 12, 8);
        var root = new DockPanel { Children = { toolbar, probe } };
        DockPanel.SetDock(toolbar, Dock.Top);
        if (Program.SelfTest)
        {
            root.AttachedToVisualTree += (_, _) =>
            {
                var step = 0;
                DispatcherTimer.Run(() =>
                {
                    switch (++step)
                    {
                        case 1: probe.RequestDump(); break;
                        case 2: hdr.IsChecked = true; break;
                        case 3: manual.IsChecked = true; break;
                        case 4: content.Value = 12; break;
                        case 5: hdr.IsChecked = false; break;
                        case 6: hdr.IsChecked = true; manual.IsChecked = false; break;
                        case 7: hdr.IsChecked = false; break;
                        default: (TopLevel.GetTopLevel(root) as Window)?.Close(); return false;
                    }
                    Console.WriteLine($"[self-test] step={step}, HDR={hdr.IsChecked}, explicitTarget={manual.IsChecked}, content={content.Value}");
                    return true;
                }, TimeSpan.FromSeconds(1));
            };
        }
        return root;
    }
}

/// <summary>
/// Draws pure primaries defined in several color spaces and reads the resulting surface pixels back,
/// so the effective encoding can be verified without eyeballing the screen.
/// </summary>
internal sealed class ColorProbe : Control
{
    internal const int Patch = 90;
    internal const int Gap = 10;
    private IPlatformSurfaceColorVolumeFeature? _colorVolumeFeature;
    private IPlatformHdrContentFeature? _hdrContentFeature;
    private IDisposable? _redraw;
    private bool _hasHdrContent;
    private double _contentHeadroom = 8;
    private double? _clientHeadroom;

    private PlatformHdrContentMetadata? ContentMetadata => _hasHdrContent
        ? new(_contentHeadroom, 203, new(0, _contentHeadroom * 203)) : null;

    internal void SetContent(bool hasHdrContent, double contentHeadroom, double? clientHeadroom)
    {
        _hasHdrContent = hasHdrContent;
        _contentHeadroom = contentHeadroom;
        _clientHeadroom = clientHeadroom;
        _hdrContentFeature?.SetHdrContent(hasHdrContent, ContentMetadata);
        Console.WriteLine($"[probe] HDR content: {hasHdrContent}, metadata: {ContentMetadata}");
        RequestDump();
    }

    internal void RequestDump()
    {
        ProbeDrawOp.DumpRequested = true;
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        DispatcherTimer.RunOnce(() =>
        {
            if (!ProbeDrawOp.HasDumped)
                ProbeDrawOp.DumpRequested = true;
        }, TimeSpan.FromSeconds(2));
        _redraw = DispatcherTimer.Run(() => { InvalidateVisual(); return true; }, TimeSpan.FromMilliseconds(200));

        _hdrContentFeature = TopLevel.GetTopLevel(this)?.PlatformImpl?.TryGetFeature<IPlatformHdrContentFeature>();
        _hdrContentFeature?.SetHdrContent(_hasHdrContent, ContentMetadata);
        _colorVolumeFeature = TopLevel.GetTopLevel(this)?.PlatformImpl
            ?.TryGetFeature<IPlatformSurfaceColorVolumeFeature>();
        if (_colorVolumeFeature is null)
        {
            Console.WriteLine("[probe] platform does not report a preferred color volume");
            return;
        }

        _colorVolumeFeature.PreferredColorVolumeChanged += OnPreferredColorVolumeChanged;
        LogColorVolume("initial");
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_colorVolumeFeature is not null)
            _colorVolumeFeature.PreferredColorVolumeChanged -= OnPreferredColorVolumeChanged;
        _colorVolumeFeature = null;
        _hdrContentFeature?.SetHdrContent(false, null);
        _hdrContentFeature = null;
        _redraw?.Dispose();
        _redraw = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnPreferredColorVolumeChanged(object? sender, EventArgs e)
    {
        LogColorVolume("changed");
        ProbeDrawOp.DumpRequested = true;
        InvalidateVisual();
    }

    private void LogColorVolume(string reason) =>
        Console.WriteLine($"[probe] preferred color volume ({reason}): {Describe(_colorVolumeFeature?.PreferredColorVolume)}");

    private static string Number(double? value) => value?.ToString("0.####", CultureInfo.InvariantCulture) ?? "unknown";

    internal static string Describe(PlatformSurfaceColorVolume? volume) => volume is { } value
        ? $"Mapping: {value.ToneMapping}; nits basis: {value.LuminanceBasis}\n" +
          $"Current: {Number(value.HeadroomRatio)}x; potential: {Number(value.MaximumHeadroomRatio)}x; white scale: {Number(value.ReferenceWhiteScale)}\n" +
          $"White: {Number(value.ReferenceWhiteNits)} nits; target: {Number(value.TargetLuminance?.MinimumNits)}-{Number(value.TargetLuminance?.MaximumNits)} nits\n" +
          $"Encoding: {Number(value.PrimaryLuminance?.MinimumNits)}-{Number(value.PrimaryLuminance?.MaximumNits)} nits; nits/unit: {Number(value.SurfaceNitsPerUnit)}; transfer: {value.Transfer} {value.TransferExponent:G}"
        : "No color-volume report; SDR rendering";

    public override void Render(DrawingContext context)
    {
        var colorVolume = _colorVolumeFeature?.PreferredColorVolume;
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        context.Custom(new ProbeDrawOp(new Rect(Bounds.Size), colorVolume, _hasHdrContent, _contentHeadroom, _clientHeadroom));

        var text = new FormattedText(Describe(colorVolume),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 14, Brushes.White);
        text.MaxTextWidth = Math.Max(1, Bounds.Width - Gap * 2);
        context.DrawText(text, new Point(Gap, Gap + 4 * (Patch + Gap)));
    }

    private sealed class ProbeDrawOp : ICustomDrawOperation
    {
        internal static bool DumpRequested;
        internal static bool HasDumped;
        private readonly PlatformSurfaceColorVolume? _expectedColorVolume;
        private readonly bool _hasHdrContent;
        private readonly double _contentHeadroom;
        private readonly double? _clientHeadroom;

        private static readonly SKColorSpace s_srgb = SKColorSpace.CreateSrgb();
        private static readonly SKColorSpace s_srgbLinear = SKColorSpace.CreateSrgbLinear();

        private static readonly SKColorSpace s_displayP3 =
            SKColorSpace.CreateRgb(SKColorSpaceTransferFn.TwoDotTwo, SKColorSpaceXyz.DisplayP3);

        private static readonly SKColorSpace s_displayP3Srgb =
            SKColorSpace.CreateRgb(SKColorSpaceTransferFn.Srgb, SKColorSpaceXyz.DisplayP3);

        private static readonly SKColorSpace s_rec2020 =
            SKColorSpace.CreateRgb(SKColorSpaceTransferFn.TwoDotTwo, SKColorSpaceXyz.Rec2020);

        private static readonly SKColorSpace[] s_rowSpaces = [s_srgb, s_displayP3, s_rec2020];

        private static readonly SKColorF[] s_primaries =
        [
            new(1f, 0f, 0f), new(0f, 1f, 0f), new(0f, 0f, 1f),
            new(0f, 1f, 1f), new(1f, 0f, 1f), new(1f, 1f, 0f)
        ];

        public ProbeDrawOp(Rect bounds, PlatformSurfaceColorVolume? expectedColorVolume,
            bool hasHdrContent, double contentHeadroom, double? clientHeadroom)
        {
            Bounds = bounds;
            _expectedColorVolume = expectedColorVolume;
            _hasHdrContent = hasHdrContent;
            _contentHeadroom = contentHeadroom;
            _clientHeadroom = clientHeadroom;
        }

        public Rect Bounds { get; }
        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } leaseFeature)
                return;

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;
            using var paint = new SKPaint { IsAntialias = false };

            // Skia pins constant colors (SKPaint.SetColor and SKShader.CreateColor) into [0, 1], so
            // out of gamut / above-white values have to be emitted through a gradient, which keeps
            // its SKColorF stops unclamped.
            static void FillPatch(SKCanvas canvas, SKPaint paint, SKRect rect, SKColorF color, SKColorSpace cs)
            {
                using var shader = SKShader.CreateLinearGradient(
                    new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Bottom),
                    [color, color], cs, [0f, 1f], SKShaderTileMode.Clamp);
                paint.Shader = shader;
                canvas.DrawRect(rect, paint);
                paint.Shader = null;
            }

            // Row 0 is plain sRGB, the way every existing Avalonia control emits color. Rows 1 and 2
            // define the same primaries in Display P3 and Rec.2020: on a wide gamut surface they are
            // visibly more saturated, on a plain sRGB surface all three rows collapse to the same color.
            for (var row = 0; row < s_rowSpaces.Length; row++)
            for (var col = 0; col < s_primaries.Length; col++)
            {
                FillPatch(canvas, paint,
                    SKRect.Create(Gap + col * (Patch + Gap), Gap + row * (Patch + Gap), Patch, Patch),
                    s_primaries[col], s_rowSpaces[row]);
            }

            var volume = lease.PreferredColorVolume;
            var headroom = 1.0;
            if (_hasHdrContent && lease.ColorFormat.IsExtendedRange && volume?.ReferenceWhiteScale is > 0)
                headroom = volume.Value.ToneMapping == PlatformToneMappingMode.Platform ? _contentHeadroom
                    : Math.Min(_contentHeadroom, _clientHeadroom ?? volume.Value.HeadroomRatio ?? 1);
            for (var col = 0; col < 4; col++)
            {
                var scale = (float)(1 + (headroom - 1) * col / 3);
                FillPatch(canvas, paint,
                    SKRect.Create(Gap + col * (Patch + Gap), Gap + 3 * (Patch + Gap), Patch, Patch),
                    new SKColorF(scale, scale, scale), s_srgbLinear);
            }

            if (DumpRequested)
            {
                if (lease.PreferredColorVolume != _expectedColorVolume)
                    return;

                DumpRequested = false;
                HasDumped = true;
                Dump(lease, canvas, headroom);
            }
        }

        private static void Dump(ISkiaSharpApiLease lease, SKCanvas canvas, double headroom)
        {
            Console.WriteLine("[probe] ---------------- surface diagnostics ----------------");
            Console.WriteLine($"[probe] Avalonia color format : {lease.ColorFormat}");
            Console.WriteLine($"[probe] IsColorManaged        : {lease.ColorFormat.IsColorManaged}");
            Console.WriteLine($"[probe] IsWideGamut           : {lease.ColorFormat.IsWideGamut}");
            Console.WriteLine($"[probe] IsExtendedRange       : {lease.ColorFormat.IsExtendedRange}");
            Console.WriteLine($"[probe] Preferred color volume: {Describe(lease.PreferredColorVolume)}");
            Console.WriteLine($"[probe] Skia color space      : {DescribeColorSpace(lease.SkColorSpace)}");

            if (lease.SkSurface is not { } surface)
            {
                Console.WriteLine("[probe] no SkSurface available");
                return;
            }

            using (var snapshot = surface.Snapshot())
                Console.WriteLine($"[probe] SkSurface color type  : {snapshot.ColorType}");
            Console.WriteLine("[probe] readback in the surface's own encoding (no conversion applied):");
            string[] labels = ["sRGB red     ", "DisplayP3 red", "Rec2020 red  ", "linear white "];
            var matrix = canvas.TotalMatrix;
            for (var row = 0; row < labels.Length; row++)
            {
                // Patch centres are in canvas space; the surface is read in device space.
                var centre = matrix.MapPoint(Gap + Patch / 2f, Gap + row * (Patch + Gap) + Patch / 2f);
                Console.WriteLine($"[probe]   {labels[row]} @({centre.X,4:F0},{centre.Y,4:F0}) -> " +
                                  ReadPixel(surface, (int)centre.X, (int)centre.Y, lease.SkColorSpace));
            }

            // Brightest patch of row 3: the reported peak, or 4x white when unknown.
            var hdr = matrix.MapPoint(Gap + 3 * (Patch + Gap) + Patch / 2f, Gap + 3 * (Patch + Gap) + Patch / 2f);
            Console.WriteLine($"[probe]   peak white    @({hdr.X,4:F0},{hdr.Y,4:F0}) -> " +
                              ReadPixel(surface, (int)hdr.X, (int)hdr.Y, lease.SkColorSpace));

            var white = canvas.TotalMatrix.MapPoint(Gap + Patch / 2f, Gap + 3 * (Patch + Gap) + Patch / 2f);
            var whiteValue = ReadRed(surface, white, lease.SkColorSpace);
            var peakValue = ReadRed(surface, hdr, lease.SkColorSpace);
            var expectedWhite = lease.ColorFormat.IsExtendedRange ? lease.PreferredColorVolume?.ReferenceWhiteScale ?? 1 : 1;
            var expectedPeak = expectedWhite * headroom;
            var valid = Math.Abs(whiteValue - expectedWhite) < 0.025 && Math.Abs(peakValue - expectedPeak) < 0.05;
            Interlocked.Increment(ref Program.Checks);
            if (!valid)
                Interlocked.Increment(ref Program.Failures);
            Console.WriteLine($"[probe] pixel check: {(valid ? "PASS" : "FAIL")}; expected white={expectedWhite:F3}, peak={expectedPeak:F3}; actual white={whiteValue:F3}, peak={peakValue:F3}");

            Console.WriteLine("[probe] -----------------------------------------------------");
        }

        private static float ReadRed(SKSurface surface, SKPoint point, SKColorSpace? colorSpace)
        {
            var info = new SKImageInfo(1, 1, SKColorType.RgbaF32, SKAlphaType.Premul, colorSpace);
            var buffer = Marshal.AllocHGlobal(info.BytesSize);
            try
            {
                if (!surface.ReadPixels(info, buffer, info.RowBytes, (int)point.X, (int)point.Y))
                    return float.NaN;
                var values = new float[4];
                Marshal.Copy(buffer, values, 0, 4);
                return values[0];
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string DescribeColorSpace(SKColorSpace? cs)
        {
            if (cs is null)
                return "<none> (legacy / unmanaged)";
            if (SKColorSpace.Equal(cs, s_srgb))
                return "sRGB";
            if (SKColorSpace.Equal(cs, s_displayP3))
                return "Display P3 (gamma 2.2)";
            if (SKColorSpace.Equal(cs, s_displayP3Srgb))
                return "Display P3 (sRGB transfer)";
            if (SKColorSpace.Equal(cs, s_rec2020))
                return "Rec.2020 (gamma 2.2)";
            if (SKColorSpace.Equal(cs, s_srgbLinear))
                return "scRGB (sRGB primaries, extended linear)";
            return "custom";
        }

        private static string ReadPixel(SKSurface surface, int x, int y, SKColorSpace? colorSpace)
        {
            // Reading as float into the surface's own color space avoids any conversion, so the
            // numbers below are exactly what the compositor receives. Premul is used because
            // unpremultiplying makes Skia clamp the result into [0, 1].
            var info = new SKImageInfo(1, 1, SKColorType.RgbaF32, SKAlphaType.Premul, colorSpace);
            var buffer = Marshal.AllocHGlobal(info.BytesSize);
            try
            {
                if (!surface.ReadPixels(info, buffer, info.RowBytes, x, y))
                    return "<read failed>";

                var values = new float[4];
                Marshal.Copy(buffer, values, 0, 4);
                return string.Format(CultureInfo.InvariantCulture, "R={0,8:F4} G={1,8:F4} B={2,8:F4} A={3:F3}",
                    values[0], values[1], values[2], values[3]);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
