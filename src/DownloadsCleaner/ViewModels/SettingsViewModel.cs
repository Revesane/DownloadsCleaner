using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadsCleaner.Animations;
using DownloadsCleaner.Helpers;
using DownloadsCleaner.Localization;
using DownloadsCleaner.Models;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.ViewModels;

/// <summary>Карточка темы в галерее: название и цвета для превью (фон, карточка, градиент акцента).</summary>
public sealed class ThemeCard : ObservableObject
{
    private string _label;
    private bool _isSelected;

    public ThemeCard(AppTheme theme, string key, string background, string card, string accentFrom, string accentTo)
    {
        Theme = theme;
        Key = key;
        Background = background;
        Card = card;
        AccentFrom = accentFrom;
        AccentTo = accentTo;
        _label = Loc.T(key);
    }

    public AppTheme Theme { get; }
    public string Key { get; }
    public string Background { get; }
    public string Card { get; }
    public string AccentFrom { get; }
    public string AccentTo { get; }

    public string Label
    {
        get => _label;
        private set => SetProperty(ref _label, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    internal void RefreshLabel() => Label = Loc.T(Key);
}

/// <summary>Страница настроек. Изменения применяются сразу и сохраняются автоматически.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _service;
    private readonly IOperationsCoordinator _operations;
    private readonly ILogService _log;
    private readonly IFolderPickerService _folders;

    /// <param name="updates">Обновления программы; null там, где их нет (версия для macOS).</param>
    public SettingsViewModel(ISettingsService service, IOperationsCoordinator operations, ILogService log,
        IFolderPickerService folders, UpdateViewModel? updates = null, IAutostartService? autostart = null)
    {
        Updates = updates;
        _autostart = autostart;
        _service = service;
        _operations = operations;
        _log = log;
        _folders = folders;
        MotionSettings.Changed += (_, _) => UiThread.Post(() =>
        {
            OnPropertyChanged(nameof(SystemAnimationsBlocked));
        });
        Loc.LanguageChanged += (_, _) => UiThread.Post(RefreshLocalization);
        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.Theme)) UpdateThemeSelection();
        };
        UpdateThemeSelection();
    }

    public AppSettings Settings => _service.Current;

    /// <summary>Галерея тем оформления (Windows): цвета превью совпадают с палитрами Themes/*.xaml.</summary>
    public IReadOnlyList<ThemeCard> ThemeCards { get; } = new[]
    {
        new ThemeCard(AppTheme.Dark, "SettingsThemeDark", "#0D0E13", "#1C1F2A", "#8B5CF6", "#4F8CF0"),
        new ThemeCard(AppTheme.Light, "SettingsThemeLight", "#F3F4F9", "#FFFFFF", "#7C3AED", "#3B6FE0"),
        new ThemeCard(AppTheme.Midnight, "ThemeMidnight", "#0A0F1C", "#16213B", "#0284C7", "#4F46E5"),
        new ThemeCard(AppTheme.Forest, "ThemeForest", "#0B110E", "#17221C", "#059669", "#0D9488"),
        new ThemeCard(AppTheme.Sunset, "ThemeSunset", "#140D10", "#26181E", "#EA580C", "#DB2777"),
        new ThemeCard(AppTheme.Sakura, "ThemeSakura", "#FBF4F7", "#FFFFFF", "#EC4899", "#A855F7"),
        new ThemeCard(AppTheme.System, "SettingsThemeSystem", "#0D0E13", "#FFFFFF", "#8B5CF6", "#4F8CF0")
    };

    [RelayCommand]
    private void SelectTheme(ThemeCard? card)
    {
        if (card != null) ThemeValue = (int)card.Theme;
    }

    private void UpdateThemeSelection()
    {
        foreach (var card in ThemeCards) card.IsSelected = card.Theme == Settings.Theme;
    }
    public UpdateViewModel? Updates { get; }
    public bool HasUpdates => Updates != null;

    private readonly IAutostartService? _autostart;

    /// <summary>Трей и автозапуск есть только там, где есть их реализация (Windows).</summary>
    public bool HasAutostart => _autostart != null;

    /// <summary>Автозапуск можно включить только у установленной копии, не у сборки из исходников.</summary>
    public bool AutostartAvailable => _autostart?.IsAvailable == true;

    /// <summary>Состояние читается из системы (реестр), а не из файла настроек - так оно всегда честное.</summary>
    public bool AutostartEnabled
    {
        get => _autostart?.IsEnabled == true;
        set
        {
            _autostart?.SetEnabled(value);
            OnPropertyChanged();
        }
    }

    // Списки создаются один раз: при смене языка обновляются подписи по месту,
    // иначе ComboBox сбросил бы выбранное значение.
    public IReadOnlyList<ChoiceOption> ThemeOptions { get; } = new[]
    {
        new ChoiceOption("SettingsThemeDark", (int)AppTheme.Dark),
        new ChoiceOption("SettingsThemeLight", (int)AppTheme.Light),
        new ChoiceOption("SettingsThemeSystem", (int)AppTheme.System)
    };

    public IReadOnlyList<ChoiceOption> LanguageOptions { get; } = new[]
    {
        new ChoiceOption("SettingsLanguageRussian", (int)AppLanguage.Russian),
        new ChoiceOption("SettingsLanguageEnglish", (int)AppLanguage.English)
    };

    public IReadOnlyList<ChoiceOption> OldDaysOptions { get; } = new[]
    {
        new ChoiceOption("SettingsDays7", 7), new ChoiceOption("SettingsDays30", 30),
        new ChoiceOption("SettingsDays60", 60), new ChoiceOption("SettingsDays90", 90)
    };

    public IReadOnlyList<ChoiceOption> ToastSecondsOptions { get; } = new[]
    {
        new ChoiceOption("SettingsSeconds3", 3), new ChoiceOption("SettingsSeconds5", 5),
        new ChoiceOption("SettingsSeconds8", 8), new ChoiceOption("SettingsSeconds12", 12)
    };

    public IReadOnlyList<FileCategory> Categories { get; } = FileCategoryInfo.All;

    public int ThemeValue
    {
        get => (int)Settings.Theme;
        set { Settings.Theme = (AppTheme)value; OnPropertyChanged(); }
    }

    public int LanguageValue
    {
        get => (int)Settings.Language;
        set { Settings.Language = (AppLanguage)value; OnPropertyChanged(); }
    }

    public int OldDaysValue
    {
        get => Settings.OldFilesDays;
        set { Settings.OldFilesDays = value; OnPropertyChanged(); }
    }

    public int ToastSecondsValue
    {
        get => Settings.ToastSeconds;
        set { Settings.ToastSeconds = value; OnPropertyChanged(); }
    }

    public string ExcludedExtensionsText
    {
        get => string.Join(", ", Settings.ExcludedExtensions);
        set
        {
            var parsed = (value ?? string.Empty)
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().TrimStart('.').ToLowerInvariant())
                .Where(s => s.Length > 0).Distinct().ToList();
            Settings.ExcludedExtensions.Clear();
            foreach (var p in parsed) Settings.ExcludedExtensions.Add(p);
            OnPropertyChanged();
        }
    }

    public bool SystemAnimationsBlocked => !MotionSettings.SystemAllows;
    public string LogDirectory => _log.LogDirectory;
    public string SettingsPath => _service.FilePath;
    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>Подсказки полей ввода (выводятся, когда поле пустое).</summary>
    public string ExcludedExtensionsPlaceholder => Loc.T("SettingsExtPlaceholder");
    public string RuleExtensionPlaceholder => Loc.T("SettingsRuleExtPlaceholder");

    [RelayCommand]
    private async Task ChangeDownloadsPathAsync()
    {
        var path = await PickFolderAsync(Loc.T("SettingsPickDownloadsTitle"), Settings.DownloadsPath);
        if (path != null) Settings.DownloadsPath = path;
    }

    [RelayCommand]
    private void ResetDownloadsPath() => Settings.DownloadsPath = KnownFolders.Downloads;

    [RelayCommand]
    private async Task ChangeSortDestinationAsync()
    {
        var path = await PickFolderAsync(Loc.T("SettingsPickSortTitle"), Settings.EffectiveSortDestination);
        if (path != null)
        {
            Settings.SortDestination = path;
            OnPropertyChanged(nameof(DestinationText));
        }
    }

    [RelayCommand]
    private void ResetSortDestination()
    {
        Settings.SortDestination = string.Empty;
        OnPropertyChanged(nameof(DestinationText));
    }

    public string DestinationText => string.IsNullOrWhiteSpace(Settings.SortDestination)
        ? Loc.T("SettingsDestinationDefault") : Settings.SortDestination;

    [RelayCommand]
    private async Task AddExcludedFolderAsync()
    {
        var path = await PickFolderAsync(Loc.T("SettingsPickExcludedTitle"), Settings.DownloadsPath);
        if (path != null && !Settings.ExcludedFolders.Contains(path, StringComparer.OrdinalIgnoreCase))
            Settings.ExcludedFolders.Add(path);
    }

    [RelayCommand]
    private void RemoveExcludedFolder(string? path)
    {
        if (path != null) Settings.ExcludedFolders.Remove(path);
    }

    [RelayCommand]
    private void AddRule() => Settings.CustomRules.Add(new ExtensionRule("", FileCategory.Other));

    [RelayCommand]
    private void RemoveRule(ExtensionRule? rule)
    {
        if (rule != null) Settings.CustomRules.Remove(rule);
    }

    [RelayCommand]
    private Task ApplyRulesAsync() => _operations.ScanAsync();

    [RelayCommand]
    private void OpenLogFolder()
    {
        if (Directory.Exists(LogDirectory)) ShellHelper.ShowInExplorer(LogDirectory);
    }

    private void RefreshLocalization()
    {
        foreach (var option in ThemeOptions) option.RefreshLabel();
        foreach (var card in ThemeCards) card.RefreshLabel();
        foreach (var option in LanguageOptions) option.RefreshLabel();
        foreach (var option in OldDaysOptions) option.RefreshLabel();
        foreach (var option in ToastSecondsOptions) option.RefreshLabel();
        OnPropertyChanged(nameof(DestinationText));
        OnPropertyChanged(nameof(ExcludedExtensionsPlaceholder));
        OnPropertyChanged(nameof(RuleExtensionPlaceholder));
    }

    private Task<string?> PickFolderAsync(string title, string? initial) => _folders.PickFolderAsync(title, initial);
}
