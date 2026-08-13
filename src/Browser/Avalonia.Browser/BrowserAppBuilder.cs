using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Browser.Interop;
using Avalonia.Browser.Rendering;
using Avalonia.Controls;
using Avalonia.Metadata;

namespace Avalonia.Browser;

public enum BrowserRenderingMode
{
    Software2D = 1,
    WebGL1,
    WebGL2
}

public record BrowserPlatformOptions
{
    /// <summary>
    /// Gets or sets Avalonia rendering modes with fallbacks.
    /// The first element in the array has the highest priority.
    /// </summary>
    /// <exception cref="System.InvalidOperationException">Thrown if no values were matched.</exception>
    public IReadOnlyList<BrowserRenderingMode> RenderingMode { get; set; } = new[]
    {
        BrowserRenderingMode.WebGL2, BrowserRenderingMode.WebGL1, BrowserRenderingMode.Software2D
    };

    /// <summary>
    /// Defines paths where avalonia modules and service locator should be resolved.
    /// If null, default path resolved depending on the backend (browser or blazor) is used.
    /// </summary>
    public Func<string, string>? FrameworkAssetPathResolver { get; set; }

    /// <summary>
    /// Defines if the service worker used by Avalonia should be registered.
    /// If registered, service worker can work as a save file picker fallback on the browsers that don't support native implementation.
    /// For more details, see https://github.com/jimmywarting/native-file-system-adapter#a-note-when-downloading-with-the-polyfilled-version.
    /// </summary>
    [Unstable("This property might not work reliably.")]
    public bool RegisterAvaloniaServiceWorker { get; set; }

    /// <summary>
    /// If <see cref="RegisterAvaloniaServiceWorker"/> is enabled, it is possible to redefine scope for the worker.
    /// By default, current domain root is used as a scope.
    /// </summary>
    public string? AvaloniaServiceWorkerScope { get; set; }

    /// <summary>
    /// Avalonia uses "native-file-system-adapter" polyfill for the file dialogs.
    /// If native implementation is available, by default it is used.
    /// This property forces polyfill to be always used.
    /// For more details, see https://github.com/jimmywarting/native-file-system-adapter#a-note-when-downloading-with-the-polyfilled-version.
    /// </summary>
    public bool PreferFileDialogPolyfill { get; set; }

    /// <summary>
    /// Defines if Avalonia should create a controlled dispatcher loop on the web worker thread.
    /// If used only when WasmEnableThreads is set to true. Default value is true.
    /// </summary>
    public bool? PreferManagedThreadDispatcher { get; set; } = true;

    /// <summary>
    /// Opts in to rendering into a wide gamut or extended range surface. Requires a WebGL rendering
    /// mode and matching browser support; when that is missing the backend silently falls back to a
    /// lesser format and ultimately to the standard sRGB surface, so enabling this is always safe.
    /// </summary>
    public BrowserColorMode ColorMode { get; set; } = BrowserColorMode.Standard;
}

/// <summary>
/// The color space Avalonia renders its browser canvas in.
/// </summary>
public enum BrowserColorMode
{
    /// <summary>
    /// Non color managed 8 bit sRGB. This is the default and matches Avalonia's behaviour on every
    /// other backend.
    /// </summary>
    Standard,

    /// <summary>
    /// A Display P3 surface with the sRGB transfer function, i.e. the web platform's
    /// <c>display-p3</c> color space. Existing controls keep their appearance because Skia color
    /// converts their sRGB colors into the wider space, while custom drawing operations can emit
    /// colors outside of the sRGB gamut.
    /// </summary>
    /// <remarks>
    /// This color space is bounded, so colors are still clamped to the SDR white level. Use
    /// <see cref="ExtendedSrgb"/> if you want values above SDR white.
    /// </remarks>
    WideColorGamut,

    /// <summary>
    /// An unbounded 16 bit float sRGB surface. Colors outside the sRGB gamut are expressed with
    /// negative channel values and values above 1 exceed the SDR white level, so this covers both
    /// wide gamut and HDR content.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This relies on floating point WebGL drawing buffers not being clamped, which is verified
    /// behaviour in Chromium based browsers. Browsers that do clamp simply render the content as
    /// SDR, so it degrades gracefully rather than breaking.
    /// </para>
    /// <para>
    /// Unlike the extended linear modes on the desktop backends, the sRGB transfer function is kept,
    /// so blending and gradient interpolation are unchanged from Avalonia's historical behaviour.
    /// The web platform has no extended <em>linear</em> (scRGB) drawing buffer color space, and its
    /// PQ/HLG ones are gated behind a non-default Chromium experiment, so this is the only route to
    /// HDR on the web.
    /// </para>
    /// <para>
    /// Falls back to <see cref="WideColorGamut"/> when a float drawing buffer is unavailable.
    /// </para>
    /// </remarks>
    ExtendedSrgb
}

public static class BrowserAppBuilder
{
    /// <summary>
    /// Configures browser backend, loads avalonia javascript modules and creates a single view lifetime from the passed <see paramref="mainDivId"/> parameter.
    /// </summary>
    /// <param name="builder">Application builder.</param>
    /// <param name="mainDivId">ID of the html element where avalonia content should be rendered.</param>
    /// <param name="options">Browser backend specific options.</param>
    public static async Task StartBrowserAppAsync(
        this AppBuilder builder,
        string mainDivId, BrowserPlatformOptions? options = null)
    {
        if (mainDivId is null)
        {
            throw new ArgumentNullException(nameof(mainDivId));
        }

        builder = await PreSetupBrowser(builder, options);

        var lifetime = new BrowserSingleViewLifetime();
        builder
            .AfterApplicationSetup(_ =>
            {
                lifetime.View = new AvaloniaView(mainDivId);
            });

        if (BrowserWindowingPlatform.IsManagedDispatcherEnabled)
        {
            var tcs = new TaskCompletionSource();
            var thread = new Thread(() =>
            {
                try
                {
                    builder
                        .SetupWithLifetime(lifetime);
                    tcs.TrySetResult();
                    builder.Instance!.Run(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });
#pragma warning disable CA1416
            thread.Start();
#pragma warning restore CA1416
            await tcs.Task;
        }
        else
        {
            builder
                .SetupWithLifetime(lifetime);
        }
    }

    /// <summary>
    /// Loads avalonia javascript modules and configures browser backend.
    /// </summary>
    /// <param name="builder">Application builder.</param>
    /// <param name="options">Browser backend specific options.</param>
    /// <remarks>
    /// This method doesn't creates any avalonia views to be rendered. To do so create an <see cref="AvaloniaView"/> object.
    /// Alternatively, you can call <see cref="StartBrowserAppAsync"/> method instead of <see cref="SetupBrowserAppAsync"/>.
    /// </remarks>
    public static async Task SetupBrowserAppAsync(this AppBuilder builder, BrowserPlatformOptions? options = null)
    {
        builder = await PreSetupBrowser(builder, options);

        var lifetime = new BrowserSingleViewLifetime();
        builder
            .SetupWithLifetime(lifetime);
    }

    internal static async Task<AppBuilder> PreSetupBrowser(AppBuilder builder, BrowserPlatformOptions? options)
    {
        options ??= AvaloniaLocator.Current.GetService<BrowserPlatformOptions>() ?? new BrowserPlatformOptions();
        options.FrameworkAssetPathResolver ??= fileName => $"./{fileName}";

        AvaloniaLocator.CurrentMutable.Bind<BrowserPlatformOptions>().ToConstant(options);

        await AvaloniaModule.ImportMain();

        BrowserWindowingPlatform.GlobalThis = DomHelper.GetGlobalThis();

        if (BrowserWindowingPlatform.IsThreadingEnabled)
        {
            await RenderWorker.InitializeAsync();
        }

        if (builder.WindowingSubsystemInitializer is null)
        {
            builder = builder.UseBrowser();
        }
        
        return builder;
    }

    public static AppBuilder UseBrowser(
        this AppBuilder builder)
    {
        return builder
            .UseBrowserRuntimePlatformSubsystem()
            .UseWindowingSubsystem(BrowserWindowingPlatform.Register)
            .UseSkia()
            .UseHarfBuzz();
    }
}
