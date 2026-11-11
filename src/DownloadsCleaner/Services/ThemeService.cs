using System.Windows;
using DownloadsCleaner.Helpers;
using DownloadsCleaner.Models;
using Microsoft.Win32;

namespace DownloadsCleaner.Services;

/// <summary>Переключает словарь ресурсов Themes/Dark.xaml ⇄ Themes/Light.xaml.</summary>
public sealed class ThemeService : IThemeService, IDisposable
{
    private readonly ISettingsService _settings;
    private readonly UserPreferenceChangedEventHandler _preferenceChanged;

    public ThemeService(ISettingsService settings)
    {
        _settings = settings;
        // Статическое событие живёт дольше сервиса, поэтому подписку нужно снимать.
        _preferenceChanged = (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && _settings.Current.Theme == AppTheme.System)
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(AppTheme.System));
        };
        SystemEvents.UserPreferenceChanged += _preferenceChanged;
    }

    public void Dispose() => SystemEvents.UserPreferenceChanged -= _preferenceChanged;

    public void Apply(AppTheme theme)
    {
        var app = Application.Current;
        if (app == null) return;

        var light = theme == AppTheme.Light || (theme == AppTheme.System && SystemUsesLightTheme());
        var uri = new Uri($"/DownloadsCleaner;component/Themes/{(light ? "Light" : "Dark")}.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };

        var merged = app.Resources.MergedDictionaries;
        var existing = merged.FirstOrDefault(d => d.Source != null &&
            (d.Source.OriginalString.EndsWith("Themes/Dark.xaml") || d.Source.OriginalString.EndsWith("Themes/Light.xaml")));
        if (existing != null)
        {
            var index = merged.IndexOf(existing);
            merged[index] = dict;
        }
        else
        {
            merged.Insert(0, dict);
        }

        // DynamicResource обновляются сами, а вот цвета, полученные через конвертеры
        // (состояние приложения, тосты) - нет: конвертер не получает уведомления о смене
        // словаря ресурсов. Поэтому перечитываем привязки явно.
        BindingRefresh.Refresh(app.MainWindow);
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }
}
