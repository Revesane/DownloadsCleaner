using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadsCleaner.Helpers;
using DownloadsCleaner.Localization;
using DownloadsCleaner.Models;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.ViewModels;

/// <summary>Один совет: файл, причина и отметка «удалить».</summary>
public sealed partial class SmartTipItemViewModel : ObservableObject
{
    private readonly Action _changed;

    public SmartTipItemViewModel(SmartTip tip, bool marked, Action changed)
    {
        Tip = tip;
        _isMarked = marked;
        _changed = changed;
    }

    public SmartTip Tip { get; }
    public FileEntry Entry => Tip.File;
    public string Name => Entry.Name;
    public string FullPath => Entry.FullPath;
    public string SizeText => ByteFormatter.Format(Entry.Size);

    public string Reason => Tip.Kind switch
    {
        TipKind.ExtractedArchive => Loc.F("TipReasonExtracted", Tip.Related),
        TipKind.InstalledProgram => Loc.F("TipReasonInstalled", Tip.Related),
        _ => Loc.F("TipReasonNewer", Path.GetFileName(Tip.Related))
    };

    /// <summary>Есть ли что показать в Проводнике: распакованная папка или новая версия.</summary>
    public bool HasRelatedPath => Tip.Kind != TipKind.InstalledProgram;

    [ObservableProperty] private bool _isMarked;

    partial void OnIsMarkedChanged(bool value) => _changed();

    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Reason));
        OnPropertyChanged(nameof(SizeText));
    }
}

/// <summary>Группа советов одного вида.</summary>
public sealed class SmartTipGroupViewModel : ObservableObject
{
    public SmartTipGroupViewModel(TipKind kind, IReadOnlyList<SmartTipItemViewModel> items)
    {
        Kind = kind;
        Items = items;
    }

    public TipKind Kind { get; }
    public IReadOnlyList<SmartTipItemViewModel> Items { get; }

    public string Title => Loc.T(Kind switch
    {
        TipKind.ExtractedArchive => "TipsGroupExtracted",
        TipKind.InstalledProgram => "TipsGroupInstalled",
        _ => "TipsGroupOld"
    });

    public string Hint => Loc.T(Kind switch
    {
        TipKind.ExtractedArchive => "TipsGroupExtractedHint",
        TipKind.InstalledProgram => "TipsGroupInstalledHint",
        _ => "TipsGroupOldHint"
    });

    public string Glyph => Kind switch
    {
        TipKind.ExtractedArchive => "",
        TipKind.InstalledProgram => "",
        _ => ""
    };

    public string TotalText => $"{ByteFormatter.Files(Items.Count)} · {ByteFormatter.Format(Items.Sum(i => i.Entry.Size))}";

    internal void RefreshLocalization()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(TotalText));
        foreach (var i in Items) i.RefreshLocalization();
    }
}

/// <summary>
/// «Умные советы»: файлы, которые можно удалить без потерь - уже распакованные архивы,
/// установщики уже установленных программ и старые версии. Удаление - только через общее
/// подтверждение и в Корзину; перед удалением каждый совет перепроверяется на диске.
/// </summary>
public sealed partial class SmartTipsViewModel : ObservableObject, IDisposable
{
    private readonly IFileLibrary _library;
    private readonly ISmartTipsService _service;
    private readonly ISettingsService _settings;
    private readonly IOperationsCoordinator _operations;
    private readonly AppStatus _status;
    private readonly IToastService _toasts;
    private readonly ILogService _log;
    private readonly UiDebouncer _pruneDebounce;
    /// <summary>Выбор пользователя по файлам: при перестроении списка он сохраняется.</summary>
    private readonly Dictionary<string, bool> _choices = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<SmartTip> _tips = Array.Empty<SmartTip>();
    private bool _autoRunDone;

    public SmartTipsViewModel(IFileLibrary library, ISmartTipsService service, ISettingsService settings,
        IOperationsCoordinator operations, AppStatus status, IToastService toasts, ILogService log)
    {
        _library = library;
        _service = service;
        _settings = settings;
        _operations = operations;
        _status = status;
        _toasts = toasts;
        _log = log;
        _pruneDebounce = new UiDebouncer(TimeSpan.FromMilliseconds(400), OnLibraryChanged);
        _library.Changed += (_, _) => _pruneDebounce.Schedule();
        Loc.LanguageChanged += (_, _) => UiThread.Post(RefreshLocalization);
    }

    public void Dispose() => _pruneDebounce.Dispose();

    public ObservableCollection<SmartTipGroupViewModel> Groups { get; } = new();

    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _hasSearched;
    [ObservableProperty] private bool _hasTips;
    [ObservableProperty] private int _tipCount;
    /// <summary>Сколько места освободят все советы (для главной страницы).</summary>
    [ObservableProperty] private double _totalBytes;
    [ObservableProperty] private string _summary = Loc.T("TipsHint");
    [ObservableProperty] private int _markedCount;
    [ObservableProperty] private string _markedBytesText = ByteFormatter.Format(0);

    /// <summary>После первого сканирования советы ищутся сами, без баннера; дальше - по кнопке.</summary>
    private void OnLibraryChanged()
    {
        if (!_autoRunDone && _library.HasScanned && !IsSearching)
        {
            _autoRunDone = true;
            _ = RunAsync(quiet: true);
            return;
        }
        Prune();
    }

    [RelayCommand]
    private Task FindAsync() => RunAsync(quiet: false);

    internal async Task RunAsync(bool quiet)
    {
        if (IsSearching) return;
        if (!quiet && _status.IsBusy) { _toasts.Warning(Loc.T("OpBusyTitle")); return; }
        if (_library.Files.Count == 0)
        {
            if (!quiet) _toasts.Info(Loc.T("CommonNoData"), Loc.T("DupNoDataHint"));
            return;
        }

        IsSearching = true;
        var ct = quiet ? CancellationToken.None : _status.Begin(AppState.Scanning, Loc.T("TipsSearching"), indeterminate: true);
        try
        {
            var tips = await _service.FindAsync(_library.Files, _settings.Current.DownloadsPath, ct);
            // Пока шёл анализ, файлы могли исчезнуть - оставляем только существующие в библиотеке.
            _tips = tips.Where(t => _library.Contains(t.File.FullPath)).ToList();
            HasSearched = true;
            Rebuild();
            if (!quiet)
                _status.Finish(AppState.ScanComplete,
                    _tips.Count == 0 ? Loc.T("TipsNone") : Loc.F("TipsFound", _tips.Count.ToString("N0")),
                    _tips.Count == 0 ? string.Empty : Loc.F("TipsCanFree", ByteFormatter.Format(_tips.Sum(t => t.File.Size))));
        }
        catch (OperationCanceledException)
        {
            if (!quiet) _status.Finish(AppState.Ready, Loc.T("TipsCancelled"));
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка поиска умных советов", ex);
            if (!quiet)
            {
                _status.Finish(AppState.Error, Loc.T("TipsFailed"), ex.Message);
                _toasts.Error(Loc.T("TipsFailed"), ex.Message);
            }
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>Убирает советы для файлов, которых больше нет (удалены, перемещены, отсортированы).</summary>
    private void Prune()
    {
        if (_tips.Count == 0) return;
        var alive = _tips.Where(t => _library.Contains(t.File.FullPath)).ToList();
        if (alive.Count == _tips.Count) return;
        _tips = alive;
        Rebuild();
    }

    internal void Rebuild()
    {
        var alive = new HashSet<string>(_tips.Select(t => t.File.FullPath), StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _choices.Keys.Where(p => !alive.Contains(p)).ToList()) _choices.Remove(gone);

        Groups.Clear();
        foreach (var g in _tips.GroupBy(t => t.Kind).OrderBy(g => g.Key))
        {
            var items = g.Select(t => new SmartTipItemViewModel(t,
                _choices.TryGetValue(t.File.FullPath, out var chosen) ? chosen : MarkedByDefault(t), OnMarksChanged)).ToList();
            Groups.Add(new SmartTipGroupViewModel(g.Key, items));
        }

        TipCount = _tips.Count;
        HasTips = _tips.Count > 0;
        TotalBytes = _tips.Sum(t => t.File.Size);
        UpdateSummary();
        UpdateMarked();
    }

    private void UpdateSummary()
    {
        Summary = !HasSearched
            ? Loc.T("TipsHint")
            : _tips.Count == 0
                ? Loc.T("TipsNone")
                : Loc.F("TipsSummary", ByteFormatter.Files(_tips.Count), ByteFormatter.Format(_tips.Sum(t => t.File.Size)));
    }

    /// <summary>
    /// Распакованный архив и старая версия - точно лишние: их содержимое есть рядом. Установщики
    /// многие хранят намеренно (плагины не всегда можно скачать снова), поэтому их отмечает сам пользователь.
    /// </summary>
    private static bool MarkedByDefault(SmartTip tip) => tip.Kind != TipKind.InstalledProgram;

    private void OnMarksChanged()
    {
        foreach (var item in Groups.SelectMany(g => g.Items)) _choices[item.FullPath] = item.IsMarked;
        UpdateMarked();
    }

    private void UpdateMarked()
    {
        var marked = Groups.SelectMany(g => g.Items).Where(i => i.IsMarked).ToList();
        MarkedCount = marked.Count;
        MarkedBytesText = ByteFormatter.Format(marked.Sum(i => i.Entry.Size));
    }

    [RelayCommand]
    private void MarkAll()
    {
        foreach (var item in Groups.SelectMany(g => g.Items)) item.IsMarked = true;
    }

    [RelayCommand]
    private void ClearMarks()
    {
        foreach (var item in Groups.SelectMany(g => g.Items)) item.IsMarked = false;
    }

    [RelayCommand]
    private async Task DeleteMarkedAsync()
    {
        var marked = Groups.SelectMany(g => g.Items).Where(i => i.IsMarked).Select(i => i.Tip).ToList();
        if (marked.Count == 0) return;

        // Перепроверка на диске: распакованную папку или новую версию могли удалить после анализа.
        var valid = marked.Where(_service.StillValid).ToList();
        var stale = marked.Count - valid.Count;
        if (stale > 0)
        {
            var staleSet = marked.Except(valid).Select(t => t.File.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _tips = _tips.Where(t => !staleSet.Contains(t.File.FullPath)).ToList();
            Rebuild();
            _toasts.Warning(Loc.T("TipsStaleTitle"), Loc.F("TipsStaleText", stale.ToString("N0")));
        }
        if (valid.Count == 0) return;
        await _operations.DeleteAsync(valid.Select(t => t.File).ToList());
    }

    [RelayCommand]
    private void OpenLocation(SmartTipItemViewModel? item)
    {
        if (item != null && !ShellHelper.ShowInExplorer(item.FullPath))
            _toasts.Warning(Loc.T("FilesNotFound"));
    }

    [RelayCommand]
    private void OpenRelated(SmartTipItemViewModel? item)
    {
        if (item is { HasRelatedPath: true } && !ShellHelper.ShowInExplorer(item.Tip.Related))
            _toasts.Warning(Loc.T("FilesNotFound"));
    }

    private void RefreshLocalization()
    {
        foreach (var g in Groups) g.RefreshLocalization();
        UpdateSummary();
        MarkedBytesText = ByteFormatter.Format(Groups.SelectMany(g => g.Items).Where(i => i.IsMarked).Sum(i => i.Entry.Size));
    }
}
