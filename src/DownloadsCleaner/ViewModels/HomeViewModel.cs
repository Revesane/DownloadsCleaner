using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadsCleaner.Helpers;
using DownloadsCleaner.Localization;
using DownloadsCleaner.Models;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.ViewModels;

public enum NavKind
{
    Home, All, Audio, Video, Images, Documents, Archives, Programs, Models3D, Other, Analytics, Duplicates, History, Settings, Tips
}

/// <param name="Search">Строка поиска для списка файлов (null - поиск не меняется).</param>
public sealed record NavRequest(NavKind Kind, FilePreset Preset = FilePreset.None, string? Search = null);

public interface INavigationService
{
    event Action<NavRequest>? Requested;
    void Go(NavKind kind, FilePreset preset = FilePreset.None, string? search = null);
}

public sealed class NavigationService : INavigationService
{
    public event Action<NavRequest>? Requested;
    public void Go(NavKind kind, FilePreset preset = FilePreset.None, string? search = null) =>
        Requested?.Invoke(new NavRequest(kind, preset, search));
}

public sealed partial class CategoryCardViewModel : ObservableObject
{
    public CategoryCardViewModel(FileCategory category) => Category = category;

    public FileCategory Category { get; }
    public string Title => FileCategoryInfo.Title(Category);

    internal void RefreshTitle() => OnPropertyChanged(nameof(Title));

    [ObservableProperty] private double _count;
    [ObservableProperty] private double _bytes;
    /// <summary>Доля категории в общем объёме (0..1) - длина полоски заполнения.</summary>
    [ObservableProperty] private double _share;
}

/// <summary>Главный экран: сводка по папке, карточки категорий, быстрые действия.</summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly IFileLibrary _library;
    private readonly ISettingsService _settings;
    private readonly IOperationsCoordinator _operations;
    private readonly INavigationService _navigation;
    private readonly AppStatus _status;

    /// <param name="tips">Умные советы; null там, где их нет (версия для macOS).</param>
    public HomeViewModel(IFileLibrary library, ISettingsService settings, IOperationsCoordinator operations,
        INavigationService navigation, AppStatus status, SmartTipsViewModel? tips = null)
    {
        _library = library;
        _settings = settings;
        _operations = operations;
        _navigation = navigation;
        _status = status;
        Status = status;
        Tips = tips;

        foreach (var c in FileCategoryInfo.All) Categories.Add(new CategoryCardViewModel(c));
        _library.Changed += (_, _) => UpdateFromLibrary();
        _settings.Current.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.OldFilesDays)) UpdateFromLibrary();
        };
        Loc.LanguageChanged += (_, _) => UiThread.Post(() =>
        {
            foreach (var card in Categories) card.RefreshTitle();
            UpdateFromLibrary();
        });
        UpdateFromLibrary();
    }

    public AppStatus Status { get; }
    public SmartTipsViewModel? Tips { get; }
    public ObservableCollection<CategoryCardViewModel> Categories { get; } = new();

    [ObservableProperty] private bool _hasScanned;
    [ObservableProperty] private double _totalFiles;
    [ObservableProperty] private double _totalBytes;
    [ObservableProperty] private double _sortableCount;
    [ObservableProperty] private double _oldCount;
    [ObservableProperty] private double _duplicateCopies;
    [ObservableProperty] private double _freeableBytes;
    [ObservableProperty] private double _tempCount;
    [ObservableProperty] private double _largeCount;
    [ObservableProperty] private bool _duplicatesKnown;
    [ObservableProperty] private string _folderPath = string.Empty;
    [ObservableProperty] private string _oldCaption = string.Empty;

    private void UpdateFromLibrary()
    {
        var s = _library.Stats;
        HasScanned = _library.HasScanned;
        FolderPath = _settings.Current.DownloadsPath;
        TotalFiles = s.TotalFiles;
        TotalBytes = s.TotalBytes;
        SortableCount = s.SortableCount;
        OldCount = s.OldCount;
        TempCount = s.TempCount;
        LargeCount = s.LargeCount;
        DuplicatesKnown = s.DuplicatesKnown;
        DuplicateCopies = s.DuplicateCopies;
        FreeableBytes = s.FreeableBytes;
        OldCaption = Loc.F("HomeOldCaption", _settings.Current.OldFilesDays);

        foreach (var card in Categories)
        {
            var stat = s.Categories.First(c => c.Category == card.Category);
            card.Count = stat.Count;
            card.Bytes = stat.Bytes;
            card.Share = s.TotalBytes > 0 ? (double)stat.Bytes / s.TotalBytes : 0;
        }
    }

    [RelayCommand] private Task ScanAsync() => _operations.ScanAsync();
    [RelayCommand] private Task SortAsync() => _operations.SortAsync(null);

    [RelayCommand]
    private void OpenCategory(CategoryCardViewModel? card)
    {
        if (card == null) return;
        _navigation.Go(card.Category switch
        {
            FileCategory.Audio => NavKind.Audio,
            FileCategory.Video => NavKind.Video,
            FileCategory.Images => NavKind.Images,
            FileCategory.Documents => NavKind.Documents,
            FileCategory.Archives => NavKind.Archives,
            FileCategory.Programs => NavKind.Programs,
            FileCategory.Models3D => NavKind.Models3D,
            _ => NavKind.Other
        });
    }

    [RelayCommand] private void ShowOld() => _navigation.Go(NavKind.All, FilePreset.Old);
    [RelayCommand] private void ShowLarge() => _navigation.Go(NavKind.All, FilePreset.Large);
    [RelayCommand] private void ShowTemporary() => _navigation.Go(NavKind.All, FilePreset.Temporary);
    [RelayCommand] private void ShowDuplicates() => _navigation.Go(NavKind.Duplicates);
    [RelayCommand] private void ShowAnalytics() => _navigation.Go(NavKind.Analytics);
    [RelayCommand] private void ShowAll() => _navigation.Go(NavKind.All);
    [RelayCommand] private void ShowTips() => _navigation.Go(NavKind.Tips);
}
