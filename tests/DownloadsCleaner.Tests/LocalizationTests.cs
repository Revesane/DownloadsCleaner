using DownloadsCleaner.Helpers;
using DownloadsCleaner.Localization;
using DownloadsCleaner.Models;

namespace DownloadsCleaner.Tests;

/// <summary>Проверки локализации: наборы ключей, переводы, склонения, стабильность имён папок.</summary>
public sealed class LocalizationTests
{
    private static void WithLanguage(AppLanguage language, Action action)
    {
        var previous = Loc.Current;
        try
        {
            Loc.SetLanguage(language);
            action();
        }
        finally
        {
            Loc.SetLanguage(previous);
        }
    }

    [Fact]
    public void RussianAndEnglishHaveSameKeys()
    {
        var ru = Loc.Russian.Keys.OrderBy(k => k).ToArray();
        var en = Loc.English.Keys.OrderBy(k => k).ToArray();

        var missingInEn = ru.Except(en).ToArray();
        var missingInRu = en.Except(ru).ToArray();

        Assert.Empty(missingInEn);
        Assert.Empty(missingInRu);
    }

    [Fact]
    public void EnglishHasNoCyrillic()
    {
        // «Русский» в английском интерфейсе остаётся по-русски - так понятнее пользователю.
        var allowed = new[] { "SettingsLanguageRussian" };

        var offenders = Loc.English
            .Where(kv => !allowed.Contains(kv.Key))
            .Where(kv => kv.Value.Any(ch => ch >= 'Ѐ' && ch <= 'ӿ'))
            .Select(kv => $"{kv.Key} = {kv.Value}")
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void NoEmptyOrMissingStrings()
    {
        foreach (var (key, value) in Loc.Russian)
        {
            Assert.False(string.IsNullOrWhiteSpace(value), $"Пустая строка для ключа {key}");
        }

        foreach (var (key, value) in Loc.English)
        {
            Assert.False(string.IsNullOrWhiteSpace(value), $"Пустая строка для ключа {key}");
        }
    }

    [Fact]
    public void UnknownKeyReturnsKeyItself()
    {
        WithLanguage(AppLanguage.English, () =>
        {
            Assert.Equal("NoSuchKey", Loc.T("NoSuchKey"));
        });
    }

    [Fact]
    public void LanguageChangeSwitchesStringsAndRaisesEvent()
    {
        var previous = Loc.Current;
        var raised = 0;
        void Handler(object? sender, EventArgs e) => raised++;

        Loc.LanguageChanged += Handler;
        try
        {
            Loc.SetLanguage(AppLanguage.English);
            Assert.Equal(AppLanguage.English, Loc.Current);
            Assert.Equal("Cancel", Loc.T("CommonCancel"));
            Assert.Equal(1, raised);

            // Повторная установка того же языка ничего не меняет.
            Loc.SetLanguage(AppLanguage.English);
            Assert.Equal(1, raised);

            Loc.SetLanguage(AppLanguage.Russian);
            Assert.Equal("Отмена", Loc.T("CommonCancel"));
            Assert.Equal(2, raised);
        }
        finally
        {
            Loc.LanguageChanged -= Handler;
            Loc.SetLanguage(previous);
        }
    }

    [Fact]
    public void RussianPluralForms()
    {
        WithLanguage(AppLanguage.Russian, () =>
        {
            Assert.Equal("файл", Loc.Plural(1, "FileOne", "FileFew", "FileMany"));
            Assert.Equal("файла", Loc.Plural(2, "FileOne", "FileFew", "FileMany"));
            Assert.Equal("файла", Loc.Plural(4, "FileOne", "FileFew", "FileMany"));
            Assert.Equal("файлов", Loc.Plural(5, "FileOne", "FileFew", "FileMany"));
            Assert.Equal("файлов", Loc.Plural(11, "FileOne", "FileFew", "FileMany"));
            Assert.Equal("файл", Loc.Plural(21, "FileOne", "FileFew", "FileMany"));
            Assert.Equal("файл", Loc.Plural(101, "FileOne", "FileFew", "FileMany"));
        });
    }

    [Fact]
    public void EnglishPluralForms()
    {
        WithLanguage(AppLanguage.English, () =>
        {
            Assert.Equal("file", Loc.Plural(1, "FileOne", "FileFew", "FileMany"));
            Assert.Equal("files", Loc.Plural(2, "FileOne", "FileFew", "FileMany"));
            Assert.Equal("files", Loc.Plural(5, "FileOne", "FileFew", "FileMany"));
        });
    }

    [Fact]
    public void ByteFormatterUsesLocalizedUnits()
    {
        WithLanguage(AppLanguage.Russian, () =>
        {
            Assert.Equal("512 Б", ByteFormatter.Format(512));
            Assert.Contains("КБ", ByteFormatter.Format(2048));
        });

        WithLanguage(AppLanguage.English, () =>
        {
            Assert.Equal("512 B", ByteFormatter.Format(512));
            Assert.Contains("KB", ByteFormatter.Format(2048));
        });
    }

    [Fact]
    public void FilesHelperUsesLocalizedPlural()
    {
        WithLanguage(AppLanguage.Russian, () =>
            Assert.Equal("1 файл", ByteFormatter.Files(1)));

        WithLanguage(AppLanguage.English, () =>
            Assert.Equal("1 file", ByteFormatter.Files(1)));
    }

    [Fact]
    public void CategoryTitleFollowsLanguageButFolderNameStays()
    {
        string ruTitle = "", enTitle = "", ruFolderBefore = "", enFolder = "";

        WithLanguage(AppLanguage.Russian, () =>
        {
            ruTitle = FileCategoryInfo.Title(FileCategory.Audio);
            ruFolderBefore = FileCategoryInfo.FolderName(FileCategory.Audio);
        });

        WithLanguage(AppLanguage.English, () =>
        {
            enTitle = FileCategoryInfo.Title(FileCategory.Audio);
            enFolder = FileCategoryInfo.FolderName(FileCategory.Audio);
        });

        Assert.Equal("Музыка", ruTitle);
        Assert.Equal("Music", enTitle);
        // Имя папки - это данные: после смены языка оно не должно меняться.
        Assert.Equal(ruFolderBefore, enFolder);
        Assert.Equal("Музыка", enFolder);
    }

    [Fact]
    public void FormatSubstitutesParameters()
    {
        WithLanguage(AppLanguage.English, () =>
        {
            Assert.Equal("Found 12 files", Loc.F("OpScanFound", "12 files"));
        });

        WithLanguage(AppLanguage.Russian, () =>
        {
            Assert.Contains("12", Loc.F("OpScanFound", "12 файлов"));
        });
    }
}
