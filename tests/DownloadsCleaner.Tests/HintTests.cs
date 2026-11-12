using System.Text.RegularExpressions;
using DownloadsCleaner.Controls;
using DownloadsCleaner.Localization;

namespace DownloadsCleaner.Tests;

/// <summary>Подсказки «Как это работает»: тексты и запоминание закрытых подсказок.</summary>
public class HintTests
{
    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "DownloadsCleaner"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "DownloadsCleaner");
    }

    [Fact]
    public void Every_hint_key_in_markup_is_translated()
    {
        // Ключи подсказок задаются атрибутами (TitleKey="..."), а не {loc:T ...},
        // поэтому общая проверка XAML их не видит - проверяем отдельно.
        var keys = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(SourceRoot(), "Views"), "*.xaml"))
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "(?:TitleKey|TextKey)=\"(?<key>[A-Za-z0-9_]+)\""))
                keys.Add(m.Groups["key"].Value);

        Assert.True(keys.Count >= 4 * 9, $"Найдено всего {keys.Count} ключей подсказок");
        Assert.Empty(keys.Where(k => !Loc.Russian.ContainsKey(k) || !Loc.English.ContainsKey(k)));
    }

    [Fact]
    public void Dismissed_hint_stays_closed_until_shown_again()
    {
        var id = "test-" + Guid.NewGuid().ToString("N");
        string? shown = null;
        void OnShow(string h) => shown = h;
        HintsState.ShowRequested += OnShow;
        try
        {
            Assert.False(HintsState.IsDismissed(id));
            HintsState.Dismiss(id);
            Assert.True(HintsState.IsDismissed(id));

            HintsState.Show(id);
            Assert.False(HintsState.IsDismissed(id));
            Assert.Equal(id, shown);
        }
        finally
        {
            HintsState.ShowRequested -= OnShow;
        }
    }
}
