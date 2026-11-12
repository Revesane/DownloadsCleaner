using System.Windows;
using System.Windows.Controls;
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

    /// <summary>Файл палитры для темы (Themes/{имя}.xaml). «Системная» - светлая или тёмная по Windows.</summary>
    internal static string PaletteName(AppTheme theme, bool systemLight) => theme switch
    {
        AppTheme.Light => "Light",
        AppTheme.System => systemLight ? "Light" : "Dark",
        AppTheme.Midnight => "Midnight",
        AppTheme.Forest => "Forest",
        AppTheme.Sunset => "Sunset",
        AppTheme.Sakura => "Sakura",
        _ => "Dark"
    };

    private static readonly string[] Palettes = { "Dark", "Light", "Midnight", "Forest", "Sunset", "Sakura" };

    public void Apply(AppTheme theme)
    {
        var app = Application.Current;
        if (app == null) return;

        var name = PaletteName(theme, theme == AppTheme.System && SystemUsesLightTheme());
        var uri = new Uri($"/DownloadsCleaner;component/Themes/{name}.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };

        var merged = app.Resources.MergedDictionaries;
        var existing = merged.FirstOrDefault(d => d.Source != null &&
            Palettes.Any(p => d.Source.OriginalString.EndsWith($"Themes/{p}.xaml", StringComparison.OrdinalIgnoreCase)));
        if (existing?.Source?.OriginalString.EndsWith($"Themes/{name}.xaml", StringComparison.OrdinalIgnoreCase) == true)
            return;                                   // эта палитра уже применена

        // Плавная смена: снимок окна поверх растворяется, открывая уже перекрашенный интерфейс.
        var fade = CaptureForCrossfade(app.MainWindow);
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
        fade?.Invoke();
    }

    /// <summary>
    /// Кладёт поверх окна его снимок со старыми цветами и возвращает действие, которое этот снимок
    /// растворяет (после смены палитры). Без анимаций или до показа окна - ничего не делает.
    /// </summary>
    private static Action? CaptureForCrossfade(Window? window)
    {
        try
        {
            if (!Animations.MotionSettings.Enabled || window is not { IsLoaded: true, Content: Panel root }
                || root.ActualWidth <= 0 || root.ActualHeight <= 0)
                return null;

            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(root);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                (int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(root);
            bitmap.Freeze();

            var snapshot = new System.Windows.Controls.Image
            {
                Source = bitmap,
                Stretch = System.Windows.Media.Stretch.Fill,
                IsHitTestVisible = false
            };
            if (root is Grid grid)
            {
                if (grid.RowDefinitions.Count > 0) Grid.SetRowSpan(snapshot, grid.RowDefinitions.Count);
                if (grid.ColumnDefinitions.Count > 0) Grid.SetColumnSpan(snapshot, grid.ColumnDefinitions.Count);
            }
            root.Children.Add(snapshot);

            return () => Animations.Anim.To(snapshot, UIElement.OpacityProperty, 0, Animations.MotionSettings.Custom(420),
                Animations.Anim.EaseInOut, 1, () => root.Children.Remove(snapshot));
        }
        catch
        {
            return null;                              // снимок - украшение, тема применится и без него
        }
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
