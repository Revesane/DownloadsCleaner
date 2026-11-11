using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using DownloadsCleaner.Localization;
using DownloadsCleaner.Models;
using DownloadsCleaner.ViewModels;

namespace DownloadsCleaner.Tests;

/// <summary>
/// Проверки XAML: разметка {loc:T ...} работает, все страницы приложения загружаются.
/// </summary>
public sealed class XamlLocalizationTests
{
    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw new InvalidOperationException("Ошибка в STA-потоке", error);
    }

    [Fact]
    public void AllUsedKeysExist()
    {
        var root = FindSourceRoot();
        Assert.NotNull(root);

        var missing = new List<string>();
        var found = new HashSet<string>(StringComparer.Ordinal);

        // Ключи из разметки {loc:T Key} - без кавычек.
        foreach (var file in Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories))
        {
            if (file.Contains(@"\obj\") || file.Contains(@"\bin\")) continue;
            var text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, @"\{loc:T\s+(?<key>[A-Za-z0-9_]+)\s*\}"))
            {
                var key = match.Groups["key"].Value;
                found.Add(key);
                if (!Loc.Russian.ContainsKey(key)) missing.Add($"{Path.GetFileName(file)}: {key}");
                else if (!Loc.English.ContainsKey(key)) missing.Add($"{Path.GetFileName(file)}: {key} (en)");
            }
        }

        // Ключи из кода: Loc.T("Key"), Loc.F("Key", ...), Loc.Plural(n, "Key", ...).
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(@"\obj\") || file.Contains(@"\bin\")) continue;
            var text = File.ReadAllText(file);
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text, "Loc\\.(?:T|F|Plural)\\([^)]*?\"(?<key>[A-Za-z0-9_]+)\""))
            {
                var key = match.Groups["key"].Value;
                found.Add(key);
                if (!Loc.Russian.ContainsKey(key)) missing.Add($"{Path.GetFileName(file)}: {key}");
                else if (!Loc.English.ContainsKey(key)) missing.Add($"{Path.GetFileName(file)}: {key} (en)");
            }
        }

        Assert.Empty(missing);
        // Проверка должна реально что-то находить, иначе она ничего не проверяет.
        Assert.True(found.Count > 100, $"Найдено всего {found.Count} ключей");
    }

    /// <summary>Ищет папку с исходниками приложения (вверх по дереву от тестов).</summary>
    private static string? FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "DownloadsCleaner");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void MarkupExtensionResolvesTranslation()
    {
        RunOnSta(() =>
        {
            var xaml =
                "<TextBlock xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                "xmlns:loc='clr-namespace:DownloadsCleaner.Localization;assembly=DownloadsCleaner' " +
                "Text='{loc:T CommonCancel}' />";

            var block = (TextBlock)XamlReader.Parse(xaml);
            Assert.Equal(Loc.T("CommonCancel"), block.Text);
        });
    }

    [Fact]
    public void MarkupExtensionFollowsLanguageChange()
    {
        RunOnSta(() =>
        {
            var xaml =
                "<TextBlock xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                "xmlns:loc='clr-namespace:DownloadsCleaner.Localization;assembly=DownloadsCleaner' " +
                "Text='{loc:T CommonCancel}' />";

            var block = (TextBlock)XamlReader.Parse(xaml);
            var previous = Loc.Current;
            try
            {
                Loc.SetLanguage(AppLanguage.English);
                Assert.Equal("Cancel", block.Text);

                Loc.SetLanguage(AppLanguage.Russian);
                Assert.Equal("Отмена", block.Text);
            }
            finally
            {
                Loc.SetLanguage(previous);
            }
        });
    }

    [Fact]
    public void AllPagesLoadWithApplicationResources()
    {
        RunOnSta(() =>
        {
            EnsureApplication();

            var pages = new Func<FrameworkElement>[]
            {
                () => new Views.HomePage(),
                () => new Views.FilesPage(),
                () => new Views.DuplicatesPage(),
                () => new Views.HistoryPage(),
                () => new Views.AnalyticsPage(),
                () => new Views.SettingsPage()
            };

            foreach (var factory in pages)
            {
                var page = factory();
                Assert.NotNull(page);
            }
        });
    }

    [Fact]
    public void ComboBoxShowsUpdatedLabelAfterLanguageChange()
    {
        RunOnSta(() =>
        {
            EnsureApplication();

            var options = new[] { new ChoiceOption("SettingsThemeDark", 0) };
            var combo = new ComboBox
            {
                ItemsSource = options,
                SelectedIndex = 0,
                Width = 200,
                Style = (Style)Application.Current.FindResource("ModernComboBox")
            };
            var factory = new System.Windows.FrameworkElementFactory(typeof(TextBlock));
            factory.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("Label"));
            var template = new DataTemplate { VisualTree = factory };
            combo.ItemTemplate = template;

            combo.Measure(new Size(200, 60));
            combo.Arrange(new Rect(0, 0, 200, 60));
            combo.UpdateLayout();

            var previous = Loc.Current;
            try
            {
                Loc.SetLanguage(AppLanguage.English);
                options[0].RefreshLabel();
                combo.UpdateLayout();

                var text = FindText(combo);
                Assert.NotNull(text);
                Assert.Equal("Dark", text.Text);
            }
            finally
            {
                Loc.SetLanguage(previous);
            }
        });
    }

    private static void PrintTree(DependencyObject root, int depth)
    {
        if (depth > 6) return;
        var name = root.GetType().Name;
        var extra = root is TextBlock tb ? " text=" + tb.Text : "";
        Console.WriteLine(new string(' ', depth * 2) + name + extra);
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++) PrintTree(VisualTreeHelper.GetChild(root, i), depth + 1);
    }

    private static TextBlock? FindText(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock block) return block;
            var found = FindText(child);
            if (found != null) return found;
        }
        return null;
    }

    private static void EnsureApplication()
    {
        if (Application.Current != null) return;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var resources = new[]
        {
            "Themes/Dark.xaml",
            "Themes/Converters.xaml",
            "Themes/Controls.xaml",
            "Views/ViewTemplates.xaml",
            "Views/Dialogs.xaml"
        };

        foreach (var path in resources)
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/DownloadsCleaner;component/{path}", UriKind.Absolute)
            });
        }
    }
}
