using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Themes.Simple;
using Avalonia.Threading;
using SkiaSharp;

namespace WideColorGamutDemo;

internal sealed class App : Application
{
    public override void Initialize() => Styles.Add(new SimpleTheme());

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new Window
            {
                Title = "Avalonia wide color gamut probe",
                Width = 900,
                Height = 560,
                Background = Brushes.Black,
                Content = new ColorProbe()
            };
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            singleView.MainView = new ColorProbe();

        base.OnFrameworkInitializationCompleted();
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

    /// <summary>
    /// Where the diagnostics dump goes. Heads that have no console (the browser) replace this.
    /// </summary>
    internal static Action<string> Log { get; set; } = Console.WriteLine;

    private IPlatformSurfaceColorVolumeFeature? _colorVolumeFeature;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        DispatcherTimer.RunOnce(() => ProbeDrawOp.DumpRequested = true, TimeSpan.FromSeconds(2));
        DispatcherTimer.Run(() => { InvalidateVisual(); return true; }, TimeSpan.FromMilliseconds(200));

        // The preferred color volume is a per-window, per-monitor value: it changes when the window
        // is dragged to another display or the display's HDR settings change.
        _colorVolumeFeature = TopLevel.GetTopLevel(this)?.PlatformImpl
            ?.TryGetFeature<IPlatformSurfaceColorVolumeFeature>();
        if (_colorVolumeFeature is null)
        {
            Log("[probe] platform does not report a preferred color volume");
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
        base.OnDetachedFromVisualTree(e);
    }

    private void OnPreferredColorVolumeChanged(object? sender, EventArgs e)
    {
        LogColorVolume("changed");
        InvalidateVisual();
    }

    private void LogColorVolume(string reason) =>
        Log($"[probe] preferred color volume ({reason}): {Describe(_colorVolumeFeature?.PreferredColorVolume)}");

    internal static string Describe(PlatformSurfaceColorVolume? volume) => volume is { } v
        ? string.Format(CultureInfo.InvariantCulture,
            "primary {0:F4}-{1:F0} nits, reference white {2:F0} nits, target {3:F4}-{4:F0} nits (headroom {5:F2}x)",
            v.PrimaryLuminance.MinimumNits, v.PrimaryLuminance.MaximumNits, v.ReferenceWhiteNits,
            v.TargetLuminance.MinimumNits, v.TargetLuminance.MaximumNits, v.HeadroomRatio)
        : "<unknown>";

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        context.Custom(new ProbeDrawOp(new Rect(Bounds.Size)));

        var text = new FormattedText(Describe(_colorVolumeFeature?.PreferredColorVolume),
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 14, Brushes.White);
        context.DrawText(text, new Point(Gap, Gap + 4 * (Patch + Gap)));
    }

    private sealed class ProbeDrawOp : ICustomDrawOperation
    {
        internal static bool DumpRequested;
        private static bool s_dumped;

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

        public ProbeDrawOp(Rect bounds) => Bounds = bounds;

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

            // Row 3: values above the SDR white level. Only representable on an extended range
            // surface. When the platform reports the display's actual peak, the ramp runs from
            // diffuse white up to exactly that peak, so the last patch is the brightest the monitor
            // can physically produce; otherwise it falls back to a fixed 1x-4x ramp.
            var headroom = lease.PreferredColorVolume?.HeadroomRatio;
            for (var col = 0; col < 4; col++)
            {
                var scale = headroom is { } h ? (float)(1 + (h - 1) * col / 3) : 1f + col;
                FillPatch(canvas, paint,
                    SKRect.Create(Gap + col * (Patch + Gap), Gap + 3 * (Patch + Gap), Patch, Patch),
                    new SKColorF(scale, scale, scale), s_srgbLinear);
            }

            if (DumpRequested && !s_dumped)
            {
                s_dumped = true;
                Dump(lease, canvas);
            }
        }

        private static void Dump(ISkiaSharpApiLease lease, SKCanvas canvas)
        {
            Log("[probe] ---------------- surface diagnostics ----------------");
            Log($"[probe] Avalonia color format : {lease.ColorFormat}");
            Log($"[probe] IsColorManaged        : {lease.ColorFormat.IsColorManaged}");
            Log($"[probe] IsWideGamut           : {lease.ColorFormat.IsWideGamut}");
            Log($"[probe] IsExtendedRange       : {lease.ColorFormat.IsExtendedRange}");
            Log($"[probe] Preferred color volume: {Describe(lease.PreferredColorVolume)}");
            Log($"[probe] Skia color space      : {DescribeColorSpace(lease.SkColorSpace)}");

            if (lease.SkSurface is not { } surface)
            {
                Log("[probe] no SkSurface available");
                return;
            }

            using (var snapshot = surface.Snapshot())
                Log($"[probe] SkSurface color type  : {snapshot.ColorType}");
            Log("[probe] readback in the surface's own encoding (no conversion applied):");
            string[] labels = ["sRGB red     ", "DisplayP3 red", "Rec2020 red  ", "linear white "];
            var matrix = canvas.TotalMatrix;
            for (var row = 0; row < labels.Length; row++)
            {
                // Patch centres are in canvas space; the surface is read in device space.
                var centre = matrix.MapPoint(Gap + Patch / 2f, Gap + row * (Patch + Gap) + Patch / 2f);
                Log($"[probe]   {labels[row]} @({centre.X,4:F0},{centre.Y,4:F0}) -> " +
                    ReadPixel(surface, (int)centre.X, (int)centre.Y, lease.SkColorSpace));
            }

            // Brightest patch of row 3: the reported peak (or 4x white when the peak is unknown).
            // Reads back as 1.0 whenever the buffer clamps.
            var hdr = matrix.MapPoint(Gap + 3 * (Patch + Gap) + Patch / 2f, Gap + 3 * (Patch + Gap) + Patch / 2f);
            Log($"[probe]   peak white    @({hdr.X,4:F0},{hdr.Y,4:F0}) -> " +
                ReadPixel(surface, (int)hdr.X, (int)hdr.Y, lease.SkColorSpace));

            Log("[probe] -----------------------------------------------------");
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
