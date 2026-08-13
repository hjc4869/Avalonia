using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Logging;

namespace WideColorGamutDemo;

internal static class Program
{
    public static void Main(string[] args)
    {
        var extended = args.Length > 0 &&
                       args[0].Equals("extendedlinear", StringComparison.OrdinalIgnoreCase);

        Console.WriteLine($"[demo] requested color mode: {(extended ? "ExtendedLinear" : "Standard")}");
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
                    ColorMode = extended ? WaylandColorMode.ExtendedLinear : WaylandColorMode.Standard
                });
            builder = builder.UseWayland();
        }

        builder
            .UseSkia()
            .UseHarfBuzz()
            .WithInterFont()
            .LogToTrace(LogEventLevel.Information, "Wayland", "OpenGL", "Win32")
            .StartWithClassicDesktopLifetime(Array.Empty<string>());
    }
}
