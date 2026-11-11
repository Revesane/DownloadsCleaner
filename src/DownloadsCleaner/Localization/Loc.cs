using DownloadsCleaner.Models;

namespace DownloadsCleaner.Localization;

/// <summary>
/// Единая точка доступа к строкам интерфейса. Язык можно менять на лету:
/// XAML-привязки через <see cref="TExtension"/> обновляются сами, а модели
/// представлений подписываются на <see cref="LanguageChanged"/>.
/// </summary>
public static class Loc
{
    private static IReadOnlyDictionary<string, string> _current = StringsRu.All;

    public static AppLanguage Current { get; private set; } = AppLanguage.Russian;

    /// <summary>Язык изменился. Событие приходит в том потоке, где вызван <see cref="SetLanguage"/>.</summary>
    public static event EventHandler? LanguageChanged;

    public static void SetLanguage(AppLanguage language)
    {
        if (Current == language) return;
        Current = language;
        _current = language == AppLanguage.English ? StringsEn.All : StringsRu.All;
        TranslationSource.Instance.RaiseChanged();
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Строка по ключу. Если ключа нет, возвращается сам ключ - это видно в интерфейсе при разработке.</summary>
    public static string T(string key) =>
        _current.TryGetValue(key, out var value) ? value : key;

    /// <summary>Строка с параметрами: {0}, {1}, ...</summary>
    public static string F(string key, params object?[] args) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, T(key), args);

    /// <summary>Склонение по правилам текущего языка (русский - три формы, английский - две).</summary>
    public static string Plural(long n, string oneKey, string fewKey, string manyKey)
    {
        if (Current == AppLanguage.English)
            return n == 1 ? T(oneKey) : T(manyKey);

        var abs = Math.Abs(n) % 100;
        var last = abs % 10;
        if (abs is > 10 and < 20) return T(manyKey);
        if (last == 1) return T(oneKey);
        if (last is >= 2 and <= 4) return T(fewKey);
        return T(manyKey);
    }

    /// <summary>Словарь русского языка (эталон набора ключей).</summary>
    public static IReadOnlyDictionary<string, string> Russian => StringsRu.All;

    /// <summary>Словарь английского языка.</summary>
    public static IReadOnlyDictionary<string, string> English => StringsEn.All;
}
