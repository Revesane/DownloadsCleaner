using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using DownloadsCleaner.Models;
using DownloadsCleaner.Services;
using DownloadsCleaner.ViewModels;

namespace DownloadsCleaner.Tests;

/// <summary>Темы оформления: полнота палитр и выбор в настройках.</summary>
public class ThemeTests
{
    private static readonly string[] Palettes = { "Dark", "Light", "Midnight", "Forest", "Sunset", "Sakura" };

    private static string ThemesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "DownloadsCleaner", "Themes"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "DownloadsCleaner", "Themes");
    }

    private static HashSet<string> Keys(string palette) =>
        Regex.Matches(File.ReadAllText(Path.Combine(ThemesDir(), palette + ".xaml")), "x:Key=\"(?<k>[A-Za-z]+)\"")
            .Select(m => m.Groups["k"].Value).ToHashSet();

    [Fact]
    public void Every_palette_defines_exactly_the_same_tokens_as_dark()
    {
        // Недостающий токен - это невидимый текст или прозрачный фон в новой теме.
        var reference = Keys("Dark");
        Assert.True(reference.Count >= 30);
        foreach (var p in Palettes)
            Assert.True(reference.SetEquals(Keys(p)), $"{p}: токены не совпадают с Dark.xaml");
    }

    [Fact]
    public void Every_palette_parses_into_real_brushes_and_colors()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var p in Palettes)
                {
                    var dict = (ResourceDictionary)XamlReader.Parse(File.ReadAllText(Path.Combine(ThemesDir(), p + ".xaml")));
                    Assert.IsAssignableFrom<Brush>(dict["AccentGradientBrush"]);
                    Assert.IsType<Color>(dict["AccentColor"]);
                    Assert.IsType<SolidColorBrush>(dict["TextPrimaryBrush"]);
                }
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw error;
    }

    [Theory]
    [InlineData(AppTheme.Dark, false, "Dark")]
    [InlineData(AppTheme.Light, false, "Light")]
    [InlineData(AppTheme.System, true, "Light")]
    [InlineData(AppTheme.System, false, "Dark")]
    [InlineData(AppTheme.Midnight, false, "Midnight")]
    [InlineData(AppTheme.Forest, true, "Forest")]
    [InlineData(AppTheme.Sunset, false, "Sunset")]
    [InlineData(AppTheme.Sakura, false, "Sakura")]
    public void Theme_maps_to_its_palette_file(AppTheme theme, bool systemLight, string expected)
    {
        Assert.Equal(expected, ThemeService.PaletteName(theme, systemLight));
        Assert.Contains(expected, Palettes);
    }

    [Fact]
    public void Gallery_offers_every_theme_and_marks_the_selected_one()
    {
        using var tmp = new TempDir();
        var settings = Fx.Settings(tmp);
        var vm = new SettingsViewModel(settings, null!, NullLogService.Instance, null!);

        Assert.Equal(Enum.GetValues<AppTheme>().OrderBy(t => t), vm.ThemeCards.Select(c => c.Theme).OrderBy(t => t));

        vm.SelectThemeCommand.Execute(vm.ThemeCards.Single(c => c.Theme == AppTheme.Sunset));
        Assert.Equal(AppTheme.Sunset, settings.Current.Theme);
        Assert.Equal(new[] { AppTheme.Sunset }, vm.ThemeCards.Where(c => c.IsSelected).Select(c => c.Theme));
    }
}
