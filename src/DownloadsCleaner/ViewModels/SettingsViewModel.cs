using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadsCleaner.Animations;
using DownloadsCleaner.Helpers;
using DownloadsCleaner.Localization;
using DownloadsCleaner.Models;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.ViewModels;

/// <summary>Страница настроек. Изменения применяются сразу и сохраняются автоматически.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _service;
    private readonly IOperationsCoordinator _operations;
    private readonly ILogService _log;
    private readonly IFolderPickerService _folders;

    public SettingsViewModel(ISettingsService service, IOperationsCoordinator operations, ILogService log,
        IFolderPickerService folders)
    {
        _service = service;
        _operations = operations;
        _log = log;
        _folders = folders;
        MotionSettings.Changed += (_, _) => UiThread.Post(() =>
        {
            OnPropertyChanged(nameof(SystemAnimationsBlocked));
        });
        Loc.LanguageChanged += (_, _) => UiThread.Post(RefreshLocalization);
    }

    public AppSettings Settings => _service.Current;

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
        foreach (var option in LanguageOptions) option.RefreshLabel();
        foreach (var option in OldDaysOptions) option.RefreshLabel();
        foreach (var option in ToastSecondsOptions) option.RefreshLabel();
        OnPropertyChanged(nameof(DestinationText));
        OnPropertyChanged(nameof(ExcludedExtensionsPlaceholder));
        OnPropertyChanged(nameof(RuleExtensionPlaceholder));
    }

    private Task<string?> PickFolderAsync(string title, string? initial) => _folders.PickFolderAsync(title, initial);
}
