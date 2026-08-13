using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Browser;
using WideColorGamutDemo;

internal static class BrowserProgram
{
    private static async Task Main(string[] args)
    {
        // main.js passes the page URL, so the mode is selected with ?mode=standard|wcg|extended.
        var url = args.Length > 0 ? args[0] : "";
        var mode = url.Contains("mode=standard", StringComparison.OrdinalIgnoreCase) ? BrowserColorMode.Standard
            : url.Contains("mode=wcg", StringComparison.OrdinalIgnoreCase) ? BrowserColorMode.WideColorGamut
            : BrowserColorMode.ExtendedSrgb;

        // There is no console window, but index.html can mirror console output back out of the browser.
        ColorProbe.Log = Console.WriteLine;
        ColorProbe.Log($"[demo] requested color mode: {mode}");

        await AppBuilder.Configure<App>()
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont()
            .StartBrowserAppAsync("out", new BrowserPlatformOptions { ColorMode = mode });
    }
}
