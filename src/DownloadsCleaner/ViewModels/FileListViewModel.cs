using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadsCleaner.Helpers;
using DownloadsCleaner.Localization;
using DownloadsCleaner.Models;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.ViewModels;

public enum FileSortKey { Name, Size, Created, Modified }

public enum FilePreset { None, Old, Large, Temporary }

public enum CleanupRuleFilter { None, Temporary, Empty, OldInstallers }

/// <summary>
/// Пункт выпадающего списка. Подпись хранится по ключу перевода и обновляется на лету:
/// сам список не пересоздаётся, поэтому выбранное значение не сбрасывается.
/// </summary>
public sealed class ChoiceOption : ObservableObject
{
    private string _label;

    public ChoiceOption(string key, int value)
    {
        Key = key;
        Value = value;
        _label = Loc.T(key);
    }

    /// <summary>Ключ перевода в словарях <see cref="Loc.Russian"/> и <see cref="Loc.English"/>.</summary>
    public string Key { get; }

    public int Value { get; }

    public string Label
    {
        get => _label;
        private set => SetProperty(ref _label, value);
    }

    /// <summary>Перечитывает подпись после смены языка интерфейса.</summary>
    internal void RefreshLabel() => Label = Loc.T(Key);

    public override string ToString() => Label;
}

/// <summary>Категория в списке фильтров: так же обновляет подпись при смене языка.</summary>
public sealed class CategoryOption : ObservableObject
{
    private string _title;

    public CategoryOption(string key, int value, FileCategory? category = null)
    {
        Key = key;
        Value = value;
        Category = category;
        _title = Loc.T(key);
    }

    public FileCategory? Category { get; }
    public string Key { get; }
    public int Value { get; }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    internal void RefreshTitle() => Title = Loc.T(Key);

    public override string ToString() => Title;
}

/// <summary>Список файлов: поиск, фильтры, сортировка, выделение и действия над файлами.</summary>
public sealed partial class FileListViewModel : ObservableObject
{
    private readonly IFileLibrary _library;
    private readonly ISettingsService _settings;
    private readonly IOperationsCoordinator _operations;
    private readonly AppStatus _status;
    private readonly IToastService _toasts;
    private readonly IClipboardService _clipboard;

    private readonly ConcurrentDictionary<string, FileItemViewModel> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<FileItemViewModel> _selected = new();
    private readonly HashSet<string> _newPaths = new(StringComparer.OrdinalIgnoreCase);
    private int _refreshVersion;
    private bool _bulkSelecting;
    private CancellationTokenSource? _searchDebounce;
    private CancellationTokenSource? _previewDebounce;
    private bool _refreshPending;
    private FileItemViewModel? _lastSelectedItem;

    public FileListViewModel(IFileLibrary library, ISettingsService settings, IOperationsCoordinator operations,
        AppStatus status, IToastService toasts, PreviewViewModel preview, IClipboardService clipboard)
    {
        _library = library;
        _settings = settings;
        _operations = operations;
        _status = status;
        _toasts = toasts;
        _clipboard = clipboard;
        Preview = preview;

        _library.Changed += (_, _) => ScheduleRefresh();
        _operations.FilesLeaving += OnFilesLeaving;
        _operations.ReleaseFileHandles += () => Preview.Clear();
        _operations.NewFileArrived += OnNewFile;

        CategoryOptions = new List<CategoryOption> { new("FilesAllCategories", -1) }
            .Concat(FileCategoryInfo.All.Select(c => new CategoryOption(FileCategoryInfo.Key(c), (int)c, c))).ToList();
        Loc.LanguageChanged += (_, _) => UiThread.Post(RefreshLocalization);
        _ = RefreshAsync();
    }

    public PreviewViewModel Preview { get; }
    public BulkObservableCollection<FileItemViewModel> Items { get; } = new();

    public IReadOnlyList<CategoryOption> CategoryOptions { get; private set; }

    public IReadOnlyList<ChoiceOption> AgeOptions { get; private set; } = CreateAgeOptions();

    public IReadOnlyList<ChoiceOption> SizeOptions { get; private set; } = CreateSizeOptions();

    public IReadOnlyList<ChoiceOption> RuleOptions { get; private set; } = CreateRuleOptions();

    private static IReadOnlyList<ChoiceOption> CreateAgeOptions() => new[]
    {
        new ChoiceOption("FilesAnyAge", 0), new ChoiceOption("FilesOlder7", 7), new ChoiceOption("FilesOlder30", 30),
        new ChoiceOption("FilesOlder60", 60), new ChoiceOption("FilesOlder90", 90)
    };

    private static IReadOnlyList<ChoiceOption> CreateSizeOptions() => new[]
    {
        new ChoiceOption("FilesAnySize", 0), new ChoiceOption("FilesOver100MB", 100), new ChoiceOption("FilesOver500MB", 500),
        new ChoiceOption("FilesOver1GB", 1024)
    };

    private static IReadOnlyList<ChoiceOption> CreateRuleOptions() => new[]
    {
        new ChoiceOption("FilesNoRule", (int)CleanupRuleFilter.None),
        new ChoiceOption("FilesTemporary", (int)CleanupRuleFilter.Temporary),
        new ChoiceOption("FilesEmpty", (int)CleanupRuleFilter.Empty),
        new ChoiceOption("FilesOldInstallers", (int)CleanupRuleFilter.OldInstallers)
    };

    // ---------------------------------------------------------------- Состояние фильтров

    [ObservableProperty] private FileCategory? _category;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private FileSortKey _sortKey = FileSortKey.Modified;
    [ObservableProperty] private bool _sortDescending = true;
    [ObservableProperty] private int _ageDays;
    [ObservableProperty] private int _minSizeMb;
    [ObservableProperty] private int _rule;
    [ObservableProperty] private bool _filtersExpanded;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(PreviewPanelWidth))] private bool _previewOpen;
    [ObservableProperty] private bool _isEmpty = true;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection), nameof(SelectedBytesText), nameof(AllSelected), nameof(SelectionSummary))]
    private int _selectedCount;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SelectedBytesText), nameof(SelectionSummary))]
    private long _selectedBytes;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary))] private int _visibleCount;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Summary))] private long _visibleBytes;

    public double PreviewPanelWidth => PreviewOpen ? 360 : 0;
    public bool HasSelection => SelectedCount > 0;
    public string SelectedBytesText => ByteFormatter.Format(SelectedBytes);
    public string SelectionSummary => Loc.F("FilesSelectedStatus", ByteFormatter.Files(SelectedCount), SelectedBytesText);
    public string Summary => $"{ByteFormatter.Files(VisibleCount)} · {ByteFormatter.Format(VisibleBytes)}";
    public bool HasActiveFilters => AgeDays != 0 || MinSizeMb != 0 || Rule != 0 || SearchText.Length > 0;

    public string Title => Category == null ? Loc.T("NavAllFiles") : FileCategoryInfo.Title(Category.Value);

    /// <summary>Подсказка в поле поиска.</summary>
    public string SearchHint => Loc.T("FilesSearchHint");

    /// <summary>Перечитывает подписи, которые строятся в коде, после смены языка.</summary>
    private void RefreshLocalization()
    {
        // Списки не пересоздаём: подписи обновляются по месту, иначе сбросился бы выбор.
        foreach (var option in CategoryOptions) option.RefreshTitle();
        foreach (var option in AgeOptions) option.RefreshLabel();
        foreach (var option in SizeOptions) option.RefreshLabel();
        foreach (var option in RuleOptions) option.RefreshLabel();
        // Название категории и размер в строках списка тоже переводятся.
        foreach (var item in _cache.Values) item.RefreshLocalizedText();
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SearchHint));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>true - выбраны все видимые файлы; false - ничего; null - частично (для заголовка таблицы).</summary>
    public bool? AllSelected => SelectedCount == 0 ? false : SelectedCount >= Items.Count && Items.Count > 0 ? true : null;

    public bool PermanentDeleteAllowed => _settings.Current.AllowPermanentDelete;

    /// <summary>Значение для ComboBox: -1 - все категории.</summary>
    public int CategoryValue
    {
        get => Category == null ? -1 : (int)Category.Value;
        set => Category = value < 0 ? null : (FileCategory)value;
    }

    partial void OnCategoryChanged(FileCategory? value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CategoryValue));
        _ = RefreshAsync();
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasActiveFilters));
        _searchDebounce?.Cancel();
        _searchDebounce = new CancellationTokenSource();
        var token = _searchDebounce.Token;
        _ = Task.Delay(220, token).ContinueWith(t =>
        {
            if (!t.IsCanceled) UiThread.Post(() => _ = RefreshAsync());
        }, TaskScheduler.Default);
    }

    partial void OnSortKeyChanged(FileSortKey value) => _ = RefreshAsync();
    partial void OnSortDescendingChanged(bool value) => _ = RefreshAsync();
    partial void OnAgeDaysChanged(int value) { OnPropertyChanged(nameof(HasActiveFilters)); _ = RefreshAsync(); }
    partial void OnMinSizeMbChanged(int value) { OnPropertyChanged(nameof(HasActiveFilters)); _ = RefreshAsync(); }
    partial void OnRuleChanged(int value) { OnPropertyChanged(nameof(HasActiveFilters)); _ = RefreshAsync(); }

    partial void OnPreviewOpenChanged(bool value)
    {
        if (value) SchedulePreview();
        else Preview.Clear();
    }

    // ---------------------------------------------------------------- Публичные действия

    public void SetCategory(FileCategory? category, FilePreset preset = FilePreset.None)
    {
        var changed = Category != category;
        Category = category;
        if (preset != FilePreset.None || changed)
            ResetFilters(preset);
        else
            ApplyPreset(preset);
    }

    private void ResetFilters(FilePreset preset)
    {
        _searchDebounce?.Cancel();
        SearchText = string.Empty;
        AgeDays = 0;
        MinSizeMb = 0;
        Rule = 0;
        ApplyPreset(preset);
    }

    /// <summary>Сбрасывает фильтры и ищет по строке (например, по сайту-источнику из аналитики).</summary>
    public void ShowSearch(string search)
    {
        ResetFilters(FilePreset.None);
        SearchText = search;
    }

    public void ApplyPreset(FilePreset preset)
    {
        switch (preset)
        {
            case FilePreset.Old:
                AgeDays = new[] { 7, 30, 60, 90 }.Contains(_settings.Current.OldFilesDays) ? _settings.Current.OldFilesDays : 30;
                SortKey = FileSortKey.Created; SortDescending = false; FiltersExpanded = true;
                break;
            case FilePreset.Large:
                MinSizeMb = 100; SortKey = FileSortKey.Size; SortDescending = true; FiltersExpanded = true;
                break;
            case FilePreset.Temporary:
                Rule = (int)CleanupRuleFilter.Temporary; FiltersExpanded = true;
                break;
        }
    }

    [RelayCommand]
    private void ClearFilters()
    {
        _searchDebounce?.Cancel();
        SearchText = string.Empty;
        AgeDays = 0; MinSizeMb = 0; Rule = 0;
    }

    [RelayCommand]
    private void SortBy(FileSortKey key)
    {
        if (SortKey == key) SortDescending = !SortDescending;
        else
        {
            SortKey = key;
            SortDescending = key != FileSortKey.Name;
        }
    }

    [RelayCommand] private void ToggleFilters() => FiltersExpanded = !FiltersExpanded;
    [RelayCommand] private void TogglePreview() => PreviewOpen = !PreviewOpen;

    [RelayCommand]
    private void SelectAll() => SetAllSelected(true);

    [RelayCommand]
    private void ClearSelection() => SetAllSelected(false);

    /// <summary>Клик по общему чекбоксу в заголовке таблицы.</summary>
    [RelayCommand]
    private void ToggleSelectAll() => SetAllSelected(SelectedCount < Items.Count);

    private void SetAllSelected(bool value)
    {
        _bulkSelecting = true;
        try
        {
            if (value)
                foreach (var i in Items) i.IsSelected = true;
            else
                foreach (var i in _selected.ToList()) i.IsSelected = false;
        }
        finally { _bulkSelecting = false; }
        RecomputeSelection();
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync() => await _operations.DeleteAsync(SelectedFiles());

    [RelayCommand]
    private async Task DeleteOneAsync(FileItemViewModel? item)
    {
        if (item != null) await _operations.DeleteAsync(new[] { item.Entry });
    }

    [RelayCommand]
    private async Task SortSelectedAsync() => await _operations.SortAsync(SelectedFiles());

    [RelayCommand]
    private async Task SortAllAsync() => await _operations.SortAsync(null);

    [RelayCommand]
    private void OpenFile(FileItemViewModel? item)
    {
        if (item == null) return;
        if (!ShellHelper.OpenFile(item.FullPath))
            _toasts.Warning(Loc.T("FilesOpenError"), item.Name);
    }

    [RelayCommand]
    private void ShowInExplorer(FileItemViewModel? item)
    {
        if (item == null) return;
        if (!ShellHelper.ShowInExplorer(item.FullPath))
            _toasts.Warning(Loc.T("FilesNotFound"), Loc.T("FilesNotFoundHint"));
    }

    [RelayCommand]
    private void CopyPath(FileItemViewModel? item)
    {
        if (item == null) return;
        try
        {
            _clipboard.SetText(item.FullPath);
            _toasts.Info(Loc.T("FilesPathCopied"), item.FullPath);
        }
        catch { _toasts.Warning(Loc.T("FilesPathCopyFailed")); }
    }

    [RelayCommand]
    private void PreviewItem(FileItemViewModel? item)
    {
        if (item == null) return;
        PreviewOpen = true;
        _ = Preview.ShowAsync(item.Entry);
    }

    public IReadOnlyList<FileEntry> SelectedFiles() => _selected
        .Select(s => s.Entry)
        .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    // ---------------------------------------------------------------- Выделение

    private void OnItemSelectionChanged(FileItemViewModel item)
    {
        if (item.IsSelected) { _selected.Add(item); _lastSelectedItem = item; }
        else _selected.Remove(item);
        if (_bulkSelecting) return;
        RecomputeSelection();
        SchedulePreview();
    }

    private void RecomputeSelection()
    {
        SelectedCount = _selected.Count;
        SelectedBytes = _selected.Sum(s => s.Size);
        if (SelectedCount == 0 && PreviewOpen) { /* оставляем последний предпросмотр */ }
        OnPropertyChanged(nameof(AllSelected));
    }

    private void SchedulePreview()
    {
        if (!PreviewOpen) return;
        _previewDebounce?.Cancel();
        _previewDebounce = new CancellationTokenSource();
        var token = _previewDebounce.Token;
        _ = Task.Delay(160, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            UiThread.Post(() =>
            {
                if (_selected.Count == 0) return;      // выбор снят - оставляем последний предпросмотр
                var target = _selected.Count == 1
                    ? _selected.First()
                    : _lastSelectedItem != null && _selected.Contains(_lastSelectedItem) ? _lastSelectedItem : _selected.First();
                _ = Preview.ShowAsync(target.Entry);
            });
        }, TaskScheduler.Default);
    }

    // ---------------------------------------------------------------- События библиотеки

    private void OnFilesLeaving(IReadOnlyCollection<string> paths)
    {
        if (paths.Count > FileListLimits.AnimatedRemovalMax) return;
        foreach (var p in paths)
            if (_cache.TryGetValue(p, out var vm)) vm.IsRemoving = true;
    }

    private void OnNewFile(FileEntry entry)
    {
        _newPaths.Add(entry.FullPath);
        _ = Task.Delay(5000).ContinueWith(_ => UiThread.Post(() =>
        {
            _newPaths.Remove(entry.FullPath);
            if (_cache.TryGetValue(entry.FullPath, out var vm)) vm.IsNew = false;
        }), TaskScheduler.Default);
    }

    private void ScheduleRefresh()
    {
        if (_refreshPending) return;
        _refreshPending = true;
        _ = Task.Delay(90).ContinueWith(_ => UiThread.Post(() =>
        {
            _refreshPending = false;
            _ = RefreshAsync();
        }), TaskScheduler.Default);
    }

    // ---------------------------------------------------------------- Построение списка

    private sealed record FilterSpec(FileCategory? Category, string Search, FileSortKey SortKey, bool Desc,
        int AgeDays, int MinSizeMb, CleanupRuleFilter Rule, int OldInstallerDays);

    public async Task RefreshAsync()
    {
        var version = ++_refreshVersion;
        var spec = new FilterSpec(Category, SearchText.Trim(), SortKey, SortDescending, AgeDays, MinSizeMb,
            (CleanupRuleFilter)Rule, Math.Max(1, _settings.Current.OldFilesDays));
        var files = _library.Files;

        var result = await Task.Run(() =>
        {
            var filtered = Filter(files, spec);
            var sorted = Sort(filtered, spec).ToList();
            var items = new List<FileItemViewModel>(sorted.Count);
            long bytes = 0;
            foreach (var e in sorted)
            {
                bytes += e.Size;
                if (!_cache.TryGetValue(e.FullPath, out var vm) || !ReferenceEquals(vm.Entry, e))
                {
                    vm = new FileItemViewModel(e, OnItemSelectionChanged);
                    _cache[e.FullPath] = vm;
                }
                items.Add(vm);
            }
            return (items, bytes);
        });

        if (version != _refreshVersion) return;      // пришёл более свежий запрос

        _bulkSelecting = true;
        try
        {
            var visible = new HashSet<FileItemViewModel>(result.items);
            foreach (var gone in _selected.Where(s => !visible.Contains(s)).ToList())
                gone.IsSelected = false;
            foreach (var vm in result.items)
            {
                vm.IsRemoving = false;
                vm.IsNew = _newPaths.Contains(vm.FullPath);
            }
        }
        finally { _bulkSelecting = false; }

        Items.ReplaceAll(result.items);
        VisibleCount = result.items.Count;
        VisibleBytes = result.bytes;
        IsEmpty = result.items.Count == 0;
        RecomputeSelection();

        // Кэш хранит только существующие файлы.
        if (_cache.Count > files.Count + 64)
        {
            var alive = new HashSet<string>(files.Select(f => f.FullPath), StringComparer.OrdinalIgnoreCase);
            foreach (var key in _cache.Keys.Where(k => !alive.Contains(k)).ToList()) _cache.TryRemove(key, out _);
        }
    }

    private static IEnumerable<FileEntry> Filter(IReadOnlyList<FileEntry> files, FilterSpec s)
    {
        var now = DateTime.Now;
        var ageLimit = s.AgeDays > 0 ? now.AddDays(-s.AgeDays) : (DateTime?)null;
        var sizeLimit = s.MinSizeMb > 0 ? s.MinSizeMb * 1024L * 1024L : (long?)null;
        var search = s.Search;
        var searchExt = search.TrimStart('.');

        foreach (var f in files)
        {
            if (s.Category != null && f.Category != s.Category) continue;
            if (ageLimit != null && f.Created >= ageLimit) continue;
            // Граница как в статистике главной страницы (Size >= 100 МБ), иначе счётчик
            // «Крупные» и отфильтрованный список расходились на файлах ровно 100 МБ.
            if (sizeLimit != null && f.Size < sizeLimit) continue;
            if (search.Length > 0
                && !f.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(f.Extension, searchExt, StringComparison.OrdinalIgnoreCase)
                && !(f.SourceHost?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)) continue;

            switch (s.Rule)
            {
                case CleanupRuleFilter.Temporary:
                    // Файл, изменявшийся в последний час, может ещё скачиваться.
                    if (!f.IsTemporary || f.Modified > now.AddHours(-1)) continue;
                    break;
                case CleanupRuleFilter.Empty:
                    if (f.Size != 0) continue;
                    break;
                case CleanupRuleFilter.OldInstallers:
                    if (f.Category != FileCategory.Programs || f.Created >= now.AddDays(-s.OldInstallerDays)) continue;
                    break;
            }
            yield return f;
        }
    }

    private static IEnumerable<FileEntry> Sort(IEnumerable<FileEntry> files, FilterSpec s)
    {
        return (s.SortKey, s.Desc) switch
        {
            (FileSortKey.Name, false) => files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
            (FileSortKey.Name, true) => files.OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase),
            (FileSortKey.Size, false) => files.OrderBy(f => f.Size),
            (FileSortKey.Size, true) => files.OrderByDescending(f => f.Size),
            (FileSortKey.Created, false) => files.OrderBy(f => f.Created),
            (FileSortKey.Created, true) => files.OrderByDescending(f => f.Created),
            (FileSortKey.Modified, false) => files.OrderBy(f => f.Modified),
            _ => files.OrderByDescending(f => f.Modified)
        };
    }
}
