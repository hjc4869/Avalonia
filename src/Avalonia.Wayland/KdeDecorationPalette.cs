using System;
using System.IO;
using Avalonia.Platform;

namespace Avalonia.Wayland;

/// <summary>
/// Maps an Avalonia frame theme variant onto a KDE colour scheme for
/// <c>org_kde_kwin_server_decoration_palette</c>. KWin hands the value straight to KConfig, so it
/// has to be the absolute path of a <c>.colors</c> file; the empty string means "follow the user's
/// global scheme" (<c>kdeglobals</c>).
/// </summary>
internal static class KdeDecorationPalette
{
    private const string FollowSystemScheme = "";

    public static string For(PlatformThemeVariant? themeVariant)
    {
        if (themeVariant is not { } wanted)
            return FollowSystemScheme;

        // The desktop already sits on the requested side of the light/dark split, so leave the
        // user's own scheme alone instead of flattening it down to stock Breeze.
        if (SystemThemeVariant() == wanted)
            return FollowSystemScheme;

        return Locate(wanted == PlatformThemeVariant.Dark ? "BreezeDark" : "BreezeLight");
    }

    private static PlatformThemeVariant? SystemThemeVariant() =>
        AvaloniaLocator.Current.GetService<IPlatformSettings>()?.GetColorValues().ThemeVariant;

    /// <summary>
    /// Resolves <c>color-schemes/{name}.colors</c> against the XDG data dirs. The path is consumed
    /// by the compositor rather than by us, so a miss still yields the canonical system location:
    /// inside a sandbox the schemes aren't visible to the client but do exist on the host.
    /// </summary>
    private static string Locate(string name)
    {
        var relative = Path.Combine("color-schemes", name + ".colors");
        foreach (var dir in DataDirs())
        {
            var candidate = Path.Combine(dir, relative);
            if (File.Exists(candidate))
                return candidate;
        }

        return "/usr/share/" + relative;
    }

    private static string[] DataDirs()
    {
        var home = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(home))
        {
            var userHome = Environment.GetEnvironmentVariable("HOME");
            home = string.IsNullOrEmpty(userHome) ? null : Path.Combine(userHome!, ".local", "share");
        }

        var dirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        if (string.IsNullOrEmpty(dirs))
            dirs = "/usr/local/share:/usr/share";

        var system = dirs!.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (home is null)
            return system;

        var all = new string[system.Length + 1];
        all[0] = home;
        Array.Copy(system, 0, all, 1, system.Length);
        return all;
    }
}
