using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadsCleaner.Helpers;
using DownloadsCleaner.Localization;
using DownloadsCleaner.Models;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.ViewModels;

public enum KeepStrategy { Oldest, Newest, ShortestPath }

public sealed partial class DuplicateFileViewModel : ObservableObject
{
    private readonly Func<DuplicateFileViewModel, bool> _canMark;
    private readonly Action _changed;
    private bool _reverting;

    public DuplicateFileViewModel(FileEntry entry, bool marked, Func<DuplicateFileViewModel, bool> canMark, Action changed)
    {
        Entry = entry;
        _isMarked = marked;
        _canMark = canMark;
        _changed = changed;
        CreatedText = entry.Created.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);
    }

    public FileEntry Entry { get; }
    public string Name => Entry.Name;
    public string FullPath => Entry.FullPath;
    public string CreatedText { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsKept))] private bool _isMarked;
    public bool IsKept => !IsMarked;

    partial void OnIsMarkedChanged(bool value)
    {
        if (_reverting) return;
        // В каждой группе должна остаться хотя бы одна копия.
        if (value && !_canMark(this))
        {
            _reverting = true;
            IsMarked = false;
            _reverting = false;
            return;
        }
        _changed();
    }
}

public sealed class DuplicateGroupViewModel
{
    public DuplicateGroupViewModel(DuplicateGroup group, Func<string, bool> isMarked, Action changed)
    {
        Group = group;
        Title = group.Files[0].Name;
        SizeText = ByteFormatter.Format(group.Size);
        CopiesText = $"{group.Files.Count} {Loc.Plural(group.Files.Count, "DupCopyOne", "DupCopyFew", "DupCopyMany")}";
        WastedText = Loc.F("DupExtra", ByteFormatter.Format(group.WastedBytes));
        Files = group.Files.Select(f => new DuplicateFileViewModel(f, isMarked(f.FullPath), CanMark, changed)).ToList();
    }

    public DuplicateGroup Group { get; }
    public string Title { get; }
    public string SizeText { get; }
    public string CopiesText { get; }
    public string WastedText { get; }
    public IReadOnlyList<DuplicateFileViewModel> Files { get; }

    private bool CanMark(DuplicateFileViewModel candidate) =>
        Files.Any(f => !ReferenceEquals(f, candidate) && !f.IsMarked);
}

/// <summary>Поиск дубликатов по SHA-256 и безопасное удаление лишних копий.</summary>
public sealed partial class DuplicatesViewModel : ObservableObject, IDisposable
{
    private readonly IFileLibrary _library;
    private readonly IDuplicateDetectionService _detector;
    private readonly ISettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IOperationsCoordinator _operations;
    private readonly AppStatus _status;
    private readonly IToastService _toasts;
    private readonly ILogService _log;
    private readonly HashSet<string> _marked = new(StringComparer.OrdinalIgnoreCase);
    private readonly UiDebouncer _rebuildDebounce;
    private bool _suppressRebuild;

    public DuplicatesViewModel(IFileLibrary library, IDuplicateDetectionService detector, ISettingsService settings,
        IDialogService dialogs, IOperationsCoordinator operations, AppStatus status, IToastService toasts, ILogService log)
    {
        _library = library;
        _detector = detector;
        _settings = settings;
        _dialogs = dialogs;
        _operations = operations;
        _status = status;
        _toasts = toasts;
        _log = log;
        // Пересборка групп пересоздаёт элементы списка, поэтому при потоке
        // изменений библиотеки её нужно делать один раз, а не на каждый файл.
        _rebuildDebounce = new UiDebouncer(TimeSpan.FromMilliseconds(300), () =>
        {
            if (!_suppressRebuild) Rebuild(keepMarks: true);
        });
        _library.Changed += (_, _) => _rebuildDebounce.Schedule();
        Loc.LanguageChanged += (_, _) => UiThread.Post(() =>
        {
            foreach (var option in StrategyOptions) option.RefreshLabel();
            Rebuild(keepMarks: true);
        });
        Rebuild(keepMarks: false);
    }

    public void Dispose() => _rebuildDebounce.Dispose();

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = new();

    public IReadOnlyList<ChoiceOption> StrategyOptions { get; } = new[]
    {
        new ChoiceOption("DupKeepOldest", (int)KeepStrategy.Oldest),
        new ChoiceOption("DupKeepNewest", (int)KeepStrategy.Newest),
        new ChoiceOption("DupKeepShortest", (int)KeepStrategy.ShortestPath)
    };

    [ObservableProperty] private int _strategy = (int)KeepStrategy.Oldest;
    [ObservableProperty] private bool _hasResults;
    [ObservableProperty] private bool _hasSearched;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _summary = Loc.T("DupHintSearch");
    [ObservableProperty] private int _markedCount;
    [ObservableProperty] private string _markedBytesText = ByteFormatter.Format(0);

    [RelayCommand]
    private async Task FindAsync()
    {
        if (_status.IsBusy) { _toasts.Warning(Loc.T("OpBusyTitle")); return; }

        // Чтение содержимого файлов - только с согласия пользователя.
        if (!_settings.Current.AllowContentHashing)
        {
            var consent = new ConfirmDialogViewModel(
                Loc.T("DupAllowTitle"),
                Loc.T("DupAllowText"),
                Loc.T("DupAllowConfirm"), Loc.T("CommonCancel"), false, "");
            if (!await _dialogs.ShowAsync(consent)) return;
            _settings.Current.AllowContentHashing = true;
        }

        if (_library.Files.Count == 0)
        {
            _toasts.Info(Loc.T("CommonNoData"), Loc.T("DupNoDataHint"));
            return;
        }

        IsSearching = true;
        var ct = _status.Begin(AppState.Scanning, Loc.T("DupSearching"), Loc.T("DupStageSize"));
        var progress = new Progress<DuplicateProgress>(p => _status.Report(p.Fraction, p.Stage));
        try
        {
            var groups = await _detector.FindAsync(_library.Files, progress, ct);
            _marked.Clear();
            _suppressRebuild = true;
            _library.SetDuplicates(groups);
            _suppressRebuild = false;
            HasSearched = true;
            AutoSelect();
            Rebuild(keepMarks: true);
            var wasted = groups.Sum(g => g.WastedBytes);
            _status.Finish(AppState.ScanComplete,
                groups.Count == 0 ? Loc.T("DupNoneFound") : Loc.F("DupFoundGroups", groups.Count.ToString("N0")),
                groups.Count == 0 ? string.Empty : Loc.F("DupWasted", ByteFormatter.Format(wasted)));
        }
        catch (OperationCanceledException)
        {
            _status.Finish(AppState.Ready, Loc.T("DupCancelled"));
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка поиска дубликатов", ex);
            _status.Finish(AppState.Error, Loc.T("DupError"), ex.Message);
            _toasts.Error(Loc.T("DupSearchFailed"), ex.Message);
        }
        finally
        {
            _suppressRebuild = false;
            IsSearching = false;
        }
    }

    [RelayCommand]
    private void AutoSelect()
    {
        _marked.Clear();
        foreach (var g in _library.Duplicates)
        {
            var keep = PickKeep(g);
            foreach (var f in g.Files)
                if (!ReferenceEquals(f, keep)) _marked.Add(f.FullPath);
        }
        Rebuild(keepMarks: true);
    }

    /// <summary>Копия, которую оставляет выбранная стратегия.</summary>
    private FileEntry PickKeep(DuplicateGroup g) => (KeepStrategy)Strategy switch
    {
        KeepStrategy.Newest => g.Files.OrderByDescending(f => f.Created).First(),
        KeepStrategy.ShortestPath => g.Files.OrderBy(f => f.FullPath.Length).First(),
        _ => g.Files.OrderBy(f => f.Created).First()
    };

    [RelayCommand]
    private void ClearMarks()
    {
        _marked.Clear();
        Rebuild(keepMarks: true);
    }

    [RelayCommand]
    private async Task DeleteMarkedAsync()
    {
        var files = new List<FileEntry>();
        var skippedGroups = 0;
        foreach (var g in Groups)
        {
            var marked = g.Files.Where(f => f.IsMarked).Select(f => f.Entry).ToList();
            if (marked.Count == 0) continue;
            // Удаляем копии, только если на диске точно остаётся неотмеченный экземпляр:
            // оставленную копию могли удалить или переместить вне программы, а список
            // ещё не успел обновиться. Иначе пропали бы все копии файла.
            if (!g.Files.Any(f => !f.IsMarked && File.Exists(f.FullPath)))
            {
                skippedGroups++;
                continue;
            }
            files.AddRange(marked);
        }

        if (skippedGroups > 0)
            _toasts.Warning(Loc.T("DupKeptMissingTitle"), Loc.F("DupKeptMissingText", skippedGroups.ToString("N0")));
        if (files.Count == 0) return;
        await _operations.DeleteAsync(files);
    }

    [RelayCommand]
    private void OpenLocation(DuplicateFileViewModel? file)
    {
        if (file != null && !ShellHelper.ShowInExplorer(file.FullPath))
            _toasts.Warning(Loc.T("FilesNotFound"));
    }

    [RelayCommand]
    private void OpenFile(DuplicateFileViewModel? file)
    {
        if (file != null) ShellHelper.OpenFile(file.FullPath);
    }

    private void OnMarksChanged()
    {
        _marked.Clear();
        foreach (var f in Groups.SelectMany(g => g.Files).Where(f => f.IsMarked)) _marked.Add(f.FullPath);
        UpdateMarkedSummary();
    }

    private void UpdateMarkedSummary()
    {
        var files = Groups.SelectMany(g => g.Files).Where(f => f.IsMarked).ToList();
        MarkedCount = files.Count;
        MarkedBytesText = ByteFormatter.Format(files.Sum(f => f.Entry.Size));
    }

    internal void Rebuild(bool keepMarks)
    {
        if (!keepMarks) _marked.Clear();
        var groups = _library.Duplicates;
        // Удалённые файлы больше не отмечены.
        var alive = new HashSet<string>(groups.SelectMany(g => g.Files).Select(f => f.FullPath), StringComparer.OrdinalIgnoreCase);
        _marked.RemoveWhere(p => !alive.Contains(p));

        // В каждой группе должна остаться хотя бы одна копия. Если оставленный экземпляр
        // исчез (удалён вне программы), все оставшиеся копии оказались бы отмеченными -
        // снимаем отметку с той, которую выбрала бы текущая стратегия.
        foreach (var g in groups)
        {
            if (g.Files.Count > 0 && g.Files.All(f => _marked.Contains(f.FullPath)))
                _marked.Remove(PickKeep(g).FullPath);
        }

        Groups.Clear();
        foreach (var g in groups)
            Groups.Add(new DuplicateGroupViewModel(g, p => _marked.Contains(p), OnMarksChanged));

        HasResults = Groups.Count > 0;
        if (HasSearched || groups.Count > 0)
        {
            Summary = groups.Count == 0
                ? Loc.T("DupNoneFoundShort")
                : $"{groups.Count:N0} {Loc.Plural(groups.Count, "DupGroupOne", "DupGroupFew", "DupGroupMany")} · " +
                  Loc.F("DupWastedDetail", ByteFormatter.Format(groups.Sum(g => g.WastedBytes)));
        }
        else
        {
            Summary = Loc.T("DupHintSearch");
        }
        UpdateMarkedSummary();
    }
}
