using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using DownloadsCleaner.Models;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.Mac.Services;

/// <summary>Переключение светлой и тёмной темы оформления.</summary>
public sealed class MacThemeService : IThemeService
{
    private ResourceInclude? _applied;

    public void Apply(AppTheme theme)
    {
        var app = Application.Current;
        if (app == null) return;

        var variant = theme switch
        {
            AppTheme.Light or AppTheme.Sakura => ThemeVariant.Light,
            // Цветные темы есть только в Windows-версии; здесь - ближайшая тёмная.
            AppTheme.Dark or AppTheme.Midnight or AppTheme.Forest or AppTheme.Sunset => ThemeVariant.Dark,
            _ => SystemUsesLightTheme() ? ThemeVariant.Light : ThemeVariant.Dark
        };

        app.RequestedThemeVariant = variant;

        // Токены цвета лежат в отдельном словаре - меняем его целиком.
        var source = variant == ThemeVariant.Light
            ? "avares://DownloadsCleaner.Mac/Theme/Light.axaml"
            : "avares://DownloadsCleaner.Mac/Theme/Dark.axaml";

        var merged = app.Resources.MergedDictionaries;
        if (_applied != null && merged.Contains(_applied))
        {
            merged.Remove(_applied);
        }

        var replacement = new ResourceInclude(new Uri("avares://DownloadsCleaner.Mac/")) { Source = new Uri(source) };
        merged.Insert(0, replacement);
        _applied = replacement;
    }

    /// <summary>Системная тема macOS (в тёмном режиме Appearance - тёмная).</summary>
    private static bool SystemUsesLightTheme()
    {
        try
        {
            if (!OperatingSystem.IsMacOS()) return true;

            var psi = new System.Diagnostics.ProcessStartInfo("/usr/bin/defaults", "read -g AppleInterfaceStyle")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return true;
            var output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(2000);
            // Значение "Dark" есть только в тёмном оформлении системы.
            return !output.Equals("Dark", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }
}
