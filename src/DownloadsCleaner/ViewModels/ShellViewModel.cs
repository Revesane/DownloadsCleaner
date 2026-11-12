using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadsCleaner.Animations;
using DownloadsCleaner.Helpers;
using DownloadsCleaner.Localization;
using DownloadsCleaner.Models;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.ViewModels;

public sealed partial class NavItemViewModel : ObservableObject
{
    public NavItemViewModel(NavKind kind, string titleKey, string glyph, bool separatorBefore = false)
    {
        Kind = kind;
        TitleKey = titleKey;
        Glyph = glyph;
        SeparatorBefore = separatorBefore;
    }

    public NavKind Kind { get; }
    public string TitleKey { get; }
    public string Title => Loc.T(TitleKey);
    public string Glyph { get; }
    public bool SeparatorBefore { get; }

    internal void RefreshTitle() => OnPropertyChanged(nameof(Title));

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasBadge), nameof(BadgeText))] private int _badge;
    public bool HasBadge => Badge > 0;
    public string BadgeText => Badge > 9999 ? "9999+" : Badge.ToString("N0");
}

/// <summary>Оболочка приложения: навигация, текущая страница, статус, уведомления, диалоги.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IFileLibrary _library;
    private readonly IOperationsCoordinator _operations;
    private readonly IThemeService _theme;
    private readonly ILogService _log;
    private readonly HomeViewModel _home;
    private readonly DuplicatesViewModel _duplicates;
    private readonly HistoryViewModel _history;
    private readonly SettingsViewModel _settingsPage;
    private readonly AnalyticsViewModel _analytics;
    private readonly SmartTipsViewModel? _tips;
    private FilePreset _pendingPreset;
    private string? _pendingSearch;
    private bool _syncingCategory;
    private CancellationTokenSource? _rescanDebounce;

    public ShellViewModel(ISettingsService settings, IFileLibrary library, IOperationsCoordinator operations,
        INavigationService navigation, IThemeService theme, HomeViewModel home, FileListViewModel files,
        DuplicatesViewModel duplicates, HistoryViewModel history, SettingsViewModel settingsPage, AnalyticsViewModel analytics,
        AppStatus status, IToastService toasts, IDialogService dialogs, ILogService log, SmartTipsViewModel? tips = null)
    {
        _tips = tips;
        _settings = settings;
        _library = library;
        _operations = operations;
        _theme = theme;
        _log = log;
        _home = home;
        Files = files;
        _duplicates = duplicates;
        _history = history;
        _settingsPage = settingsPage;
        _analytics = analytics;
        Status = status;
        Toasts = toasts.Toasts;
        Dialogs = dialogs;

        NavItems = new ObservableCollection<NavItemViewModel>
        {
            new(NavKind.Home, "NavHome", ""),
            new(NavKind.All, "NavAllFiles", ""),
            new(NavKind.Audio, "NavAudio", ""),
            new(NavKind.Video, "NavVideo", ""),
            new(NavKind.Images, "NavImages", ""),
            new(NavKind.Documents, "NavDocuments", ""),
            new(NavKind.Archives, "NavArchives", ""),
            new(NavKind.Programs, "NavPrograms", ""),
            new(NavKind.Models3D, "NavModels3D", ""),
            new(NavKind.Other, "NavOther", ""),
            new(NavKind.Analytics, "NavAnalytics", "", separatorBefore: true),
            new(NavKind.Duplicates, "NavDuplicates", ""),
            new(NavKind.History, "NavHistory", ""),
            new(NavKind.Settings, "NavSettings", "")
        };

        // Умные советы есть только там, где зарегистрирован их сервис (Windows).
        if (tips != null)
        {
            var analyticsItem = NavItems.First(n => n.Kind == NavKind.Analytics);
            NavItems.Insert(NavItems.IndexOf(analyticsItem) + 1, new NavItemViewModel(NavKind.Tips, "NavTips", ""));
            tips.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SmartTipsViewModel.TipCount)) UpdateBadges();
            };
        }

        navigation.Requested += OnNavigationRequested;
        _library.Changed += (_, _) => { UpdateBadges(); UpdateDisk(); };
        files.PropertyChanged += OnFilesPropertyChanged;
        _settings.Current.PropertyChanged += OnSettingChanged;
        _settings.Current.ExcludedFolders.CollectionChanged += (_, _) =>
        {
            _operations.ApplyWatcherSettings();
            ScheduleRescan();
        };
        // Список исключений меняется пачкой событий (Clear + Add) - пересканируем один раз.
        _settings.Current.ExcludedExtensions.CollectionChanged += (_, _) => ScheduleRescan();

        _isSidebarCollapsed = _settings.Current.SidebarCollapsed;
        _selectedNav = NavItems[0];
        _currentPage = _home;
        Loc.LanguageChanged += (_, _) => UiThread.Post(RefreshLocalization);
        UpdateBadges();
        UpdateDisk();
    }

    public ObservableCollection<NavItemViewModel> NavItems { get; }
    public ObservableCollection<ToastViewModel> Toasts { get; }
    public IDialogService Dialogs { get; }
    public AppStatus Status { get; }
    public FileListViewModel Files { get; }

    [ObservableProperty] private NavItemViewModel? _selectedNav;
    [ObservableProperty] private object? _currentPage;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(SidebarWidth))]
    private bool _isSidebarCollapsed;

    [ObservableProperty] private string _diskTitle = string.Empty;
    [ObservableProperty] private string _diskDetail = string.Empty;
    [ObservableProperty] private double _diskFraction;

    private void UpdateDisk()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_settings.Current.DownloadsPath));
            if (string.IsNullOrEmpty(root)) return;
            var d = new DriveInfo(root);
            var used = d.TotalSize - d.AvailableFreeSpace;
            DiskTitle = Loc.F("StatusDisk", d.Name.TrimEnd('\\'));
            DiskDetail = Loc.F("StatusDiskDetail", ByteFormatter.Format(used), ByteFormatter.Format(d.TotalSize));
            DiskFraction = d.TotalSize > 0 ? (double)used / d.TotalSize : 0;
        }
        catch { DiskTitle = string.Empty; DiskDetail = string.Empty; DiskFraction = 0; }
    }

    /// <summary>Перечитывает подписи, которые строятся в коде, после смены языка.</summary>
    private void RefreshLocalization()
    {
        foreach (var item in NavItems) item.RefreshTitle();
        OnPropertyChanged(nameof(PageTitle));
        UpdateDisk();
        Status.RefreshLocalization();
    }

    public double SidebarWidth => IsSidebarCollapsed ? 72 : 252;

    public string PageTitle => SelectedNav?.Title ?? string.Empty;

    /// <summary>Запуск: тема, анимации, слежение за папкой и первое сканирование.</summary>
    public async Task InitializeAsync()
    {
        _theme.Apply(_settings.Current.Theme);
        MotionSettings.UserEnabled = _settings.Current.AnimationsEnabled;
        _operations.ApplyWatcherSettings();
        if (_settings.Current.ScanOnStartup)
            await _operations.ScanAsync();
        // Тихая проверка новой версии: без сети или без обновления ничего не показывается.
        if (_settingsPage.Updates is { } updates) await updates.CheckOnStartupAsync();
    }

    // ------------------------------------------------------------ Навигация

    partial void OnSelectedNavChanged(NavItemViewModel? value)
    {
        OnPropertyChanged(nameof(PageTitle));
        if (value == null || _syncingCategory) return;
        var preset = _pendingPreset;
        var search = _pendingSearch;
        _pendingPreset = FilePreset.None;
        _pendingSearch = null;

        switch (value.Kind)
        {
            case NavKind.Home: CurrentPage = _home; break;
            case NavKind.Analytics: CurrentPage = _analytics; break;
            case NavKind.Tips when _tips != null: CurrentPage = _tips; break;
            case NavKind.Duplicates: CurrentPage = _duplicates; break;
            case NavKind.History: CurrentPage = _history; break;
            case NavKind.Settings: CurrentPage = _settingsPage; break;
            default:
                Files.SetCategory(CategoryOf(value.Kind), preset);
                if (search != null) Files.ShowSearch(search);
                CurrentPage = Files;
                break;
        }
        UpdateSelectionStatus();
    }

    /// <summary>Индикатор «Выбрано: N» показываем только на странице со списком файлов.</summary>
    private void UpdateSelectionStatus() =>
        Status.SelectedCount = ReferenceEquals(CurrentPage, Files) ? Files.SelectedCount : 0;

    private void OnNavigationRequested(NavRequest request)
    {
        var item = NavItems.First(n => n.Kind == request.Kind);
        if (ReferenceEquals(SelectedNav, item))
        {
            if (request.Preset != FilePreset.None) Files.ApplyPreset(request.Preset);
            if (request.Search != null) Files.ShowSearch(request.Search);
            return;
        }
        _pendingPreset = request.Preset;
        _pendingSearch = request.Search;
        SelectedNav = item;
    }

    private static FileCategory? CategoryOf(NavKind kind) => kind switch
    {
        NavKind.Audio => FileCategory.Audio,
        NavKind.Video => FileCategory.Video,
        NavKind.Images => FileCategory.Images,
        NavKind.Documents => FileCategory.Documents,
        NavKind.Archives => FileCategory.Archives,
        NavKind.Programs => FileCategory.Programs,
        NavKind.Models3D => FileCategory.Models3D,
        NavKind.Other => FileCategory.Other,
        _ => null
    };

    private static NavKind KindOf(FileCategory? c) => c switch
    {
        FileCategory.Audio => NavKind.Audio,
        FileCategory.Video => NavKind.Video,
        FileCategory.Images => NavKind.Images,
        FileCategory.Documents => NavKind.Documents,
        FileCategory.Archives => NavKind.Archives,
        FileCategory.Programs => NavKind.Programs,
        FileCategory.Models3D => NavKind.Models3D,
        FileCategory.Other => NavKind.Other,
        _ => NavKind.All
    };

    /// <summary>Если категорию сменили фильтром на странице, подсвечиваем соответствующий пункт меню.</summary>
    private void OnFilesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileListViewModel.SelectedCount)) UpdateSelectionStatus();
        if (e.PropertyName != nameof(FileListViewModel.Category) || !ReferenceEquals(CurrentPage, Files)) return;
        var item = NavItems.First(n => n.Kind == KindOf(Files.Category));
        if (ReferenceEquals(item, SelectedNav)) return;
        _syncingCategory = true;
        SelectedNav = item;
        _syncingCategory = false;
    }

    private void UpdateBadges()
    {
        var stats = _library.Stats;
        foreach (var item in NavItems)
        {
            item.Badge = item.Kind switch
            {
                NavKind.All => stats.TotalFiles,
                NavKind.Duplicates => stats.DuplicateCopies,
                NavKind.Tips => _tips?.TipCount ?? 0,
                _ => CategoryOf(item.Kind) is { } c ? stats.Categories.First(x => x.Category == c).Count : 0
            };
        }
    }

    // ------------------------------------------------------------ Настройки

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        var s = _settings.Current;
        switch (e.PropertyName)
        {
            case nameof(AppSettings.Theme):
                _theme.Apply(s.Theme);
                break;
            case nameof(AppSettings.Language):
                Loc.SetLanguage(s.Language);
                break;
            case nameof(AppSettings.AnimationsEnabled):
                MotionSettings.UserEnabled = s.AnimationsEnabled;
                break;
            case nameof(AppSettings.SidebarCollapsed):
                IsSidebarCollapsed = s.SidebarCollapsed;
                break;
            case nameof(AppSettings.WatchNewFiles):
                _operations.ApplyWatcherSettings();
                break;
            case nameof(AppSettings.IncludeSubfolders):
            case nameof(AppSettings.DownloadsPath):
                _operations.ApplyWatcherSettings();
                _ = _operations.ScanAsync();
                break;
            case nameof(AppSettings.OldFilesDays):
                _library.Refresh();
                break;
        }
    }

    // ------------------------------------------------------------ Команды

    /// <summary>
    /// Отложенное пересканирование: список исключений меняется несколькими событиями подряд,
    /// а без перескана в таблице остаются уже исключённые файлы.
    /// </summary>
    private void ScheduleRescan()
    {
        _rescanDebounce?.Cancel();
        _rescanDebounce = new CancellationTokenSource();
        var token = _rescanDebounce.Token;
        _ = Task.Delay(700, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            UiThread.Post(() => _ = _operations.ScanAsync());
        }, TaskScheduler.Default);
    }

    [RelayCommand]
    private void ToggleSidebar() => _settings.Current.SidebarCollapsed = !_settings.Current.SidebarCollapsed;

    [RelayCommand]
    private Task ScanAsync() => _operations.ScanAsync();

    [RelayCommand]
    private Task SortAllAsync() => _operations.SortAsync(null);

    [RelayCommand]
    private void OpenDownloads() => ShellHelper.ShowInExplorer(_settings.Current.DownloadsPath);
}
