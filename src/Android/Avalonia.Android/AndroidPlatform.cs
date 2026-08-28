using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia.Android;
using Avalonia.Android.Platform;
using Avalonia.Android.Platform.Input;
using Avalonia.Android.Platform.Vulkan;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.Vulkan;

namespace Avalonia
{
    public static class AndroidApplicationExtensions
    {
        public static AppBuilder UseAndroid(this AppBuilder builder)
        {
            return builder
                .UseAndroidRuntimePlatformSubsystem()
                .UseWindowingSubsystem(() => AndroidPlatform.Initialize(), "Android")
                .UseHarfBuzz()
                .UseSkia();
        }
    }

    /// <summary>
    /// Represents the rendering mode for platform graphics.
    /// </summary>
    public enum AndroidRenderingMode
    {
        /// <summary>
        /// Avalonia is rendered into a framebuffer.
        /// </summary>
        Software = 1,

        /// <summary>
        /// Enables android EGL rendering.
        /// </summary>
        Egl = 2,

        /// <summary>
        /// Enables Vulkan rendering
        /// </summary>
        Vulkan = 3
    }

    /// <summary>
    /// The color space Avalonia renders its Android surfaces in.
    /// </summary>
    public enum AndroidColorMode
    {
        /// <summary>
        /// Non color managed 8 bit sRGB. This is the default and preserves Avalonia's historical behavior.
        /// </summary>
        Standard,

        /// <summary>
        /// A 16 bit float scRGB surface with sRGB primaries and an extended linear transfer function.
        /// Channel values below 0 and above 1 are meaningful, enabling wide gamut and HDR content.
        /// </summary>
        /// <remarks>
        /// Requires Android 15 (API level 35) or newer, an HDR or wide-gamut display,
        /// <see cref="AndroidRenderingMode.Egl"/>, and EGL scRGB linear support.
        /// Unsupported configurations silently fall back to <see cref="Standard"/>.
        /// </remarks>
        ExtendedLinear
    }

    public sealed class AndroidPlatformOptions
    {
        /// <summary>
        /// Gets or sets Avalonia rendering modes with fallbacks.
        /// The first element in the array has the highest priority.
        /// The default value is: <see cref="AndroidRenderingMode.Egl"/>, <see cref="AndroidRenderingMode.Software"/>.
        /// </summary>
        /// <remarks>
        /// If application should work on as wide range of devices as possible, at least add <see cref="AndroidRenderingMode.Software"/> as a fallback value.
        /// </remarks>
        /// <exception cref="System.InvalidOperationException">Thrown if no values were matched.</exception>
        public IReadOnlyList<AndroidRenderingMode> RenderingMode { get; set; } = new[]
        {
            AndroidRenderingMode.Egl, AndroidRenderingMode.Software
        };

        /// <summary>
        /// Gets or sets the color space Avalonia renders Android surfaces in. The default is
        /// <see cref="AndroidColorMode.Standard"/>.
        /// </summary>
        public AndroidColorMode ColorMode { get; set; } = AndroidColorMode.Standard;
    }
}

namespace Avalonia.Android
{
    class AndroidPlatform
    {
        public static readonly AndroidPlatform Instance = new AndroidPlatform();
        public static AndroidPlatformOptions? Options { get; private set; }

        internal static Compositor? Compositor { get; private set; }
        internal static ChoreographerTimer? Timer { get; private set; }
        internal static bool IsExtendedLinearColorActive { get; private set; }

        public static void Initialize()
        {
            Options = AvaloniaLocator.Current.GetService<AndroidPlatformOptions>() ?? new AndroidPlatformOptions();

            Dispatcher.InitializeUIThreadDispatcher(new AndroidDispatcherImpl());
            Timer = new ChoreographerTimer();
            AvaloniaLocator.CurrentMutable
                .Bind<ICursorFactory>().ToTransient<CursorFactory>()
                .Bind<IWindowingPlatform>().ToConstant(new WindowingPlatformStub())
                .Bind<IKeyboardDevice>().ToSingleton<AndroidKeyboardDevice>()
                .Bind<IPlatformSettings>().ToSingleton<AndroidPlatformSettings>()
                .Bind<IPlatformIconLoader>().ToSingleton<PlatformIconLoaderStub>()
                .Bind<IRenderLoop>().ToConstant(RenderLoop.FromTimer(Timer))
                .Bind<PlatformHotkeyConfiguration>().ToSingleton<PlatformHotkeyConfiguration>()
                .Bind<KeyGestureFormatInfo>().ToConstant(new KeyGestureFormatInfo(new Dictionary<Key, string>() { }))
                .Bind<IActivatableLifetime>().ToConstant(new AndroidActivatableLifetime());

            var graphics = InitializeGraphics(Options);
            if (graphics is not null)
            {
                AvaloniaLocator.CurrentMutable.Bind<IPlatformGraphics>().ToConstant(graphics);
            }

            Compositor = new Compositor(graphics);
            AvaloniaLocator.CurrentMutable.Bind<Compositor>().ToConstant(Compositor);
        }
        
        private static IPlatformGraphics? InitializeGraphics(AndroidPlatformOptions opts)
        {
            IsExtendedLinearColorActive = false;

            if (opts.RenderingMode is null || !opts.RenderingMode.Any())
            {
                throw new InvalidOperationException($"{nameof(AndroidPlatformOptions)}.{nameof(AndroidPlatformOptions.RenderingMode)} must not be empty or null");
            }

            foreach (var renderingMode in opts.RenderingMode)
            {
                if (renderingMode == AndroidRenderingMode.Software)
                {
                    return null;
                }

                if (renderingMode == AndroidRenderingMode.Egl)
                {
                    if (TryCreateEgl(opts) is { } egl)
                    {
                        return egl;
                    }
                }

                if (renderingMode == AndroidRenderingMode.Vulkan)
                {
                    var vulkan = VulkanSupport.TryInitialize(AvaloniaLocator.Current.GetService<VulkanOptions>() ?? new());
                    if (vulkan != null)
                        return vulkan;
                }
            }

            throw new InvalidOperationException($"{nameof(AndroidPlatformOptions)}.{nameof(AndroidPlatformOptions.RenderingMode)} has a value of \"{string.Join(", ", opts.RenderingMode)}\", but no options were applied.");
        }

        private static EglPlatformGraphics? TryCreateEgl(AndroidPlatformOptions opts)
        {
            var useExtendedLinear = ShouldUseExtendedLinear(opts);
            EglDisplay? display = null;
            var graphics = EglPlatformGraphics.TryCreate(() => display = new EglDisplay(new EglDisplayCreationOptions
            {
                Egl = new EglInterface(),
                SupportsMultipleContexts = true,
                SupportsContextSharing = true,
                ColorBufferFormats = useExtendedLinear
                    ? [EglColorBufferFormat.Float16(PlatformColorSpace.ScRgbLinear), EglColorBufferFormat.Standard]
                    : null,
                UseEglWindowSurfaceColorSpace = useExtendedLinear
            }));

            IsExtendedLinearColorActive = display?.ColorFormat is
            {
                Encoding: PlatformPixelEncoding.RgbaF16,
                ColorSpace: PlatformColorSpace.ScRgbLinear
            };
            return graphics;
        }

        private static bool ShouldUseExtendedLinear(AndroidPlatformOptions opts)
        {
            if (opts.ColorMode != AndroidColorMode.ExtendedLinear ||
                !OperatingSystem.IsAndroidVersionAtLeast(35))
            {
                return false;
            }

            var configuration = global::Android.App.Application.Context?.Resources?.Configuration;
            return configuration?.IsScreenHdr == true || configuration?.IsScreenWideColorGamut == true;
        }

        internal static float? GetDesiredHdrHeadroom(global::Android.Views.Display? display)
        {
            if (!IsExtendedLinearColorActive ||
                !OperatingSystem.IsAndroidVersionAtLeast(35) ||
                display is not { IsHdr: true })
            {
                return null;
            }

            using var capabilities = display.GetHdrCapabilities();
            if (capabilities is null)
                return null;

            var maximumNits = capabilities.DesiredMaxLuminance;
            if (!float.IsFinite(maximumNits) || maximumNits <= EglDisplayUtils.ScRgbReferenceWhiteNits)
                return null;

            return (float)(Math.Min(maximumNits, EglDisplayUtils.ScRgbMaximumNits) /
                EglDisplayUtils.ScRgbReferenceWhiteNits);
        }

        internal static PlatformSurfaceColorVolume? GetPreferredColorVolume(
            global::Android.Views.Display? display)
        {
            if (!IsExtendedLinearColorActive ||
                !OperatingSystem.IsAndroidVersionAtLeast(35) ||
                display is not { IsHdr: true })
            {
                return null;
            }

            using var capabilities = display.GetHdrCapabilities();
            if (capabilities is null)
                return null;

            double? currentHeadroomRatio = null;
            if (OperatingSystem.IsAndroidVersionAtLeast(34) && display.IsHdrSdrRatioAvailable)
                currentHeadroomRatio = display.HdrSdrRatio;

            var (transfer, transferExponent) = GetPreferredTransfer(display);
            return EglDisplayUtils.CreateScRgbColorVolume(
                capabilities.DesiredMinLuminance,
                capabilities.DesiredMaxLuminance,
                currentHeadroomRatio,
                transfer,
                transferExponent);
        }

        [SupportedOSPlatform("android35.0")]
        private static (PlatformTransferFunction Transfer, double Exponent) GetPreferredTransfer(
            global::Android.Views.Display display)
        {
            // DisplayManager owns and caches this ColorSpace instance.
            var colorSpace = display.PreferredWideGamutColorSpace;
            if (colorSpace is null)
                return (PlatformTransferFunction.Unknown, 0);

            var id = colorSpace.Id;
            if (id == PreferredTransferColorSpaceIds.LinearExtendedSrgb)
                return (PlatformTransferFunction.Linear, 0);
            if (id == PreferredTransferColorSpaceIds.ExtendedSrgb ||
                id == PreferredTransferColorSpaceIds.DisplayP3)
            {
                return (PlatformTransferFunction.Srgb, 0);
            }
            if (id == PreferredTransferColorSpaceIds.Bt2020Pq)
                return (PlatformTransferFunction.Pq, 0);
            if (id == PreferredTransferColorSpaceIds.Bt2020Hlg)
                return (PlatformTransferFunction.Hlg, 0);
            if (id == PreferredTransferColorSpaceIds.DciP3)
                return (PlatformTransferFunction.Power, 2.6);
            return (PlatformTransferFunction.Unknown, 0);
        }

        private static class PreferredTransferColorSpaceIds
        {
            // Built-in ColorSpace IDs are the stable ordinals of ColorSpace.Named.
            public const int ExtendedSrgb = 2;
            public const int LinearExtendedSrgb = 3;
            public const int DciP3 = 6;
            public const int DisplayP3 = 7;
            public const int Bt2020Hlg = 16;
            public const int Bt2020Pq = 17;
        }
    }
}
