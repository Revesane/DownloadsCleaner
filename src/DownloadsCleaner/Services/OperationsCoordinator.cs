using DownloadsCleaner.Animations;
using DownloadsCleaner.Helpers;
using DownloadsCleaner.Localization;
using DownloadsCleaner.Models;
using DownloadsCleaner.ViewModels;

namespace DownloadsCleaner.Services;

public interface IOperationsCoordinator
{
    /// <summary>Файлы скоро исчезнут из списка (даёт возможность проиграть анимацию удаления).</summary>
    event Action<IReadOnlyCollection<string>>? FilesLeaving;
    /// <summary>Перед изменением файлов нужно освободить их (например, остановить предпросмотр).</summary>
    event Action? ReleaseFileHandles;
    /// <summary>В папке появился новый скачанный файл.</summary>
    event Action<FileEntry>? NewFileArrived;

    Task ScanAsync();
    /// <param name="subset">null - вся библиотека; иначе только указанные файлы.</param>
    Task SortAsync(IReadOnlyList<FileEntry>? subset = null);
    Task DeleteAsync(IReadOnlyList<FileEntry> files);
    Task UndoAsync(HistoryEntry entry);
    void ApplyWatcherSettings();
}

/// <summary>
/// Склеивает сервисы с интерфейсом: диалоги подтверждения, состояние приложения, уведомления,
/// история и библиотека файлов. Бизнес-логика остаётся в сервисах, здесь только сценарии.
/// </summary>
public sealed class OperationsCoordinator : IOperationsCoordinator
{
    private readonly ISettingsService _settings;
    private readonly IFileLibrary _library;
    private readonly IFileScannerService _scanner;
    private readonly IFileOrganizationService _organizer;
    private readonly IFileDeletionService _deletion;
    private readonly IFileWatcherService _watcher;
    private readonly IHistoryService _history;
    private readonly IDialogService _dialogs;
    private readonly IToastService _toasts;
    private readonly AppStatus _status;
    private readonly ILogService _log;
    private readonly IFolderPickerService _folders;

    public event Action<IReadOnlyCollection<string>>? FilesLeaving;
    public event Action? ReleaseFileHandles;
    public event Action<FileEntry>? NewFileArrived;

    public OperationsCoordinator(ISettingsService settings, IFileLibrary library, IFileScannerService scanner,
        IFileOrganizationService organizer, IFileDeletionService deletion, IFileWatcherService watcher,
        IHistoryService history, IDialogService dialogs, IToastService toasts, AppStatus status, ILogService log,
        IFolderPickerService folders)
    {
        _settings = settings;
        _library = library;
        _scanner = scanner;
        _organizer = organizer;
        _deletion = deletion;
        _watcher = watcher;
        _history = history;
        _dialogs = dialogs;
        _toasts = toasts;
        _status = status;
        _log = log;
        _folders = folders;

        _watcher.FileReady += (_, path) => UiThread.Post(() => OnFileReady(path));
        _watcher.FileRemoved += (_, path) => UiThread.Post(() => OnFileRemoved(path));
        _watcher.EventsLost += (_, _) => UiThread.Post(() => _ = ResyncAsync());
    }

    private bool _resyncRunning;
    private bool _resyncAgain;

    private AppSettings S => _settings.Current;

    private bool GuardBusy()
    {
        if (!_status.IsBusy) return false;
        _toasts.Warning(Loc.T("OpBusyTitle"), Loc.T("OpBusyText"));
        return true;
    }

    // ------------------------------------------------------------ Сканирование

    public async Task ScanAsync()
    {
        if (GuardBusy()) return;
        var root = S.DownloadsPath;
        if (!Directory.Exists(root))
        {
            _status.Finish(AppState.Error, Loc.T("OpFolderMissing"), root);
            _toasts.Error(Loc.T("OpDownloadsMissing"), Loc.T("OpChooseFolder"));
            return;
        }

        var ct = _status.Begin(AppState.Scanning, Loc.T("OpScanning"), root, indeterminate: true);
        var progress = new Progress<ScanProgress>(p =>
        {
            _status.LiveCount = p.FilesFound;
            _status.LiveBytes = p.BytesFound;
            _status.Detail = p.CurrentFolder;
        });

        try
        {
            var options = new ScanOptions(S.IncludeSubfolders, S.ExcludedFolders.ToList(), S.ExcludedExtensions.ToList());
            var result = await _scanner.ScanAsync(root, options, progress, ct);
            _library.SetFiles(result.Files);
            _status.LiveCount = result.Files.Count;
            _status.Finish(AppState.ScanComplete, Loc.F("OpScanFound", ByteFormatter.Files(result.Files.Count)),
                Loc.F("OpScanDetail", ByteFormatter.Format(_library.Stats.TotalBytes), result.Duration.TotalSeconds.ToString("0.0")));
            if (result.InaccessibleCount > 0)
                _toasts.Warning(Loc.T("OpScanPartial"),
                    Loc.F("OpScanPartialText", result.InaccessibleCount));
        }
        catch (OperationCanceledException)
        {
            _status.Finish(AppState.Ready, Loc.T("OpScanCancelled"));
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка сканирования", ex);
            _status.Finish(AppState.Error, Loc.T("OpScanError"), ex.Message);
            _toasts.Error(Loc.T("OpScanFailed"), ex.Message);
        }
    }

    // ------------------------------------------------------------ Сортировка

    public async Task SortAsync(IReadOnlyList<FileEntry>? subset = null)
    {
        if (GuardBusy()) return;
        var files = subset ?? _library.Files;
        if (files.Count == 0)
        {
            _toasts.Info(Loc.T("OpNothingToSort"), Loc.T("OpScanFirst"));
            return;
        }

        SortPlan Build(string dest, bool includeSub) => _organizer.BuildPlan(files, new SortOptions(
            S.DownloadsPath, dest, includeSub, S.ExcludedExtensions.ToList()));

        var dialog = new SortPreviewDialogViewModel(Build, S.EffectiveSortDestination,
            includeSubfolders: subset != null, canChangeScope: subset == null, folders: _folders);
        if (!await _dialogs.ShowAsync(dialog) || dialog.Plan is not { Count: > 0 } plan) return;

        ReleaseFileHandles?.Invoke();
        using var suspend = _watcher.Suspend();
        var ct = _status.Begin(AppState.Sorting, Loc.T("OpSorting"), Loc.F("OpZeroOf", plan.Count));
        var progress = new Progress<OperationProgress>(p =>
            _status.Report(p.Fraction, Loc.F("OpProgress", p.Done.ToString("N0"), p.Total.ToString("N0"), p.Current)));

        SortResult result;
        try
        {
            result = await _organizer.ExecuteAsync(plan, progress, ct);
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка сортировки", ex);
            _status.Finish(AppState.Error, Loc.T("OpSortError"), ex.Message);
            _toasts.Error(Loc.T("OpSortFailed"), ex.Message);
            return;
        }

        _library.ApplyMoves(result.Moved);

        HistoryEntry? entry = null;
        if (result.Moved.Count > 0)
        {
            entry = new HistoryEntry
            {
                Kind = HistoryKind.Sort,
                Title = Loc.F("OpSortDone", ByteFormatter.Files(result.Moved.Count)),
                Details = Loc.F("OpSortDestination", plan.Options.DestinationRoot),
                Count = result.Moved.Count,
                Bytes = result.MovedBytes,
                Moves = result.Moved.ToList()
            };
            _history.Add(entry);
        }

        ReportSortOutcome(result, entry);
    }

    private void ReportSortOutcome(SortResult result, HistoryEntry? entry)
    {
        if (result.Moved.Count == 0 && result.Failed.Count > 0)
        {
            _status.Finish(AppState.Error, Loc.T("OpMoveFailedTitle"), Loc.F("OpErrorsCount", result.Failed.Count));
            _toasts.Show(ToastKind.Error, Loc.T("OpSortFailed"), Loc.F("OpErrorsCount", result.Failed.Count),
                Loc.T("CommonDetails"), () => _ = _dialogs.ShowAsync(new ErrorListDialogViewModel(Loc.T("OpSortErrorsTitle"), result.Failed)));
            return;
        }

        var title = result.Cancelled
            ? Loc.F("OpSortCancelled", result.Moved.Count.ToString("N0"))
            : Loc.F("OpSorted", ByteFormatter.Files(result.Moved.Count));
        _status.Finish(AppState.SortComplete, title, ByteFormatter.Format(result.MovedBytes));

        if (result.Failed.Count > 0)
        {
            _toasts.Show(ToastKind.Warning, Loc.F("OpMoveFailedCount", result.Failed.Count),
                Loc.T("OpRestMoved"), Loc.T("CommonDetails"),
                () => _ = _dialogs.ShowAsync(new ErrorListDialogViewModel(Loc.T("OpSortErrorsTitle"), result.Failed)));
        }

        if (entry != null)
            _toasts.Show(ToastKind.Success, title, Loc.T("OpCanUndo"), Loc.T("CommonUndo"), () => _ = UndoAsync(entry));
    }

    // ------------------------------------------------------------ Удаление

    public async Task DeleteAsync(IReadOnlyList<FileEntry> files)
    {
        if (files.Count == 0 || GuardBusy()) return;

        var dialog = new DeleteConfirmDialogViewModel(files, S.AllowPermanentDelete);
        if (!await _dialogs.ShowAsync(dialog)) return;
        var mode = dialog.UsePermanent ? DeleteMode.Permanent : DeleteMode.Recycle;

        ReleaseFileHandles?.Invoke();
        await Task.Delay(120);               // даём MediaElement закрыть файл
        using var suspend = _watcher.Suspend();
        var ct = _status.Begin(AppState.Cleaning,
            mode == DeleteMode.Recycle ? Loc.T("OpSendingToBin") : Loc.T("OpDeleting"), Loc.F("OpZeroOf", files.Count));
        var progress = new Progress<OperationProgress>(p =>
            _status.Report(p.Fraction, Loc.F("OpProgress", p.Done.ToString("N0"), p.Total.ToString("N0"), p.Current)));

        DeletionResult result;
        try
        {
            result = await _deletion.DeleteAsync(files, mode, S.DownloadsPath, progress, ct);
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка удаления", ex);
            _status.Finish(AppState.Error, Loc.T("OpDeleteError"), ex.Message);
            _toasts.Error(Loc.T("OpDeleteFailed"), ex.Message);
            return;
        }

        // Плавно убираем строки, затем обновляем библиотеку.
        var paths = result.Deleted.Select(f => f.FullPath).ToList();
        if (paths.Count > 0)
        {
            FilesLeaving?.Invoke(paths);
            if (MotionSettings.Enabled && paths.Count <= FileListLimits.AnimatedRemovalMax)
                await Task.Delay(MotionSettings.Normal + TimeSpan.FromMilliseconds(60));
            _library.Remove(paths);

            _history.Add(new HistoryEntry
            {
                Kind = mode == DeleteMode.Recycle ? HistoryKind.Recycle : HistoryKind.PermanentDelete,
                Title = mode == DeleteMode.Recycle
                    ? Loc.F("OpToBinDone", ByteFormatter.Files(result.Deleted.Count))
                    : Loc.F("OpDeletedDone", ByteFormatter.Files(result.Deleted.Count)),
                Details = string.Join(", ", result.Deleted.Take(5).Select(f => f.Name))
                          + (result.Deleted.Count > 5 ? "…" : ""),
                Count = result.Deleted.Count,
                Bytes = result.BytesFreed
            });
        }

        if (result.Deleted.Count == 0 && result.Failed.Count > 0)
        {
            // Если все отказы - это переполнение/отключённая Корзина, не называем это
            // «ошибкой удаления»: файлы целы и оставлены на месте.
            var onlyNotRecycled = result.NotRecycled.Count == result.Failed.Count;
            _status.Finish(AppState.Error,
                onlyNotRecycled ? Loc.T("OpNotRecycledTitle") : Loc.T("OpDeleteFailedTitle"),
                onlyNotRecycled ? Loc.T("OpLeftInPlace") : Loc.F("OpErrorsCount", result.Failed.Count));

            if (onlyNotRecycled)
            {
                _toasts.Show(ToastKind.Warning, Loc.F("OpNotRecycledCount", result.NotRecycled.Count),
                    Loc.T("OpNotRecycledText"),
                    Loc.T("CommonDetails"),
                    () => _ = _dialogs.ShowAsync(new ErrorListDialogViewModel(Loc.T("OpNotRecycledDialog"), result.NotRecycled)));
            }
            else
            {
                _toasts.Show(ToastKind.Error, Loc.T("OpDeleteFailed"), Loc.F("OpErrorsCount", result.Failed.Count),
                    Loc.T("CommonDetails"), () => _ = _dialogs.ShowAsync(new ErrorListDialogViewModel(Loc.T("OpDeleteErrorsTitle"), result.Failed)));
            }
            return;
        }

        var title = result.Cancelled
            ? Loc.F("OpCleanCancelled", result.Deleted.Count.ToString("N0"))
            : Loc.F("OpFreed", ByteFormatter.Format(result.BytesFreed));
        _status.Finish(AppState.CleanComplete, title,
            mode == DeleteMode.Recycle ? Loc.T("OpCanRestore") : Loc.T("OpDeletedForever"));

        _toasts.Show(ToastKind.Success, title,
            mode == DeleteMode.Recycle
                ? Loc.F("OpFilesInBin", ByteFormatter.Files(result.Deleted.Count))
                : Loc.F("OpFilesDeleted", ByteFormatter.Files(result.Deleted.Count)),
            mode == DeleteMode.Recycle ? Loc.T("OpOpenBin") : null,
            mode == DeleteMode.Recycle ? () => ShellHelper.OpenRecycleBin() : null);

        if (result.Failed.Count > 0)
        {
            _toasts.Show(ToastKind.Warning, Loc.F("OpDeleteFailedCount", result.Failed.Count), null, Loc.T("CommonDetails"),
                () => _ = _dialogs.ShowAsync(new ErrorListDialogViewModel(Loc.T("OpDeleteErrorsTitle"), result.Failed)));
        }

        // Файлы, которые не поместились в Корзину, остались на месте - сообщаем об этом явно,
        // чтобы не обещать восстановимость там, где файл был уничтожен безвозвратно.
        if (result.NotRecycled.Count > 0)
        {
            _toasts.Show(ToastKind.Warning, Loc.F("OpNotRecycledCount", result.NotRecycled.Count),
                Loc.T("OpNotRecycledText"),
                Loc.T("CommonDetails"),
                () => _ = _dialogs.ShowAsync(new ErrorListDialogViewModel(Loc.T("OpNotRecycledDialog"), result.NotRecycled)));
        }
    }

    // ------------------------------------------------------------ Отмена сортировки

    public async Task UndoAsync(HistoryEntry entry)
    {
        if (!entry.CanUndo || GuardBusy()) return;

        ReleaseFileHandles?.Invoke();
        using var suspend = _watcher.Suspend();
        var ct = _status.Begin(AppState.Sorting, Loc.T("OpReturning"), Loc.F("OpZeroOf", entry.Moves.Count));
        var progress = new Progress<OperationProgress>(p =>
            _status.Report(p.Fraction, Loc.F("OpProgress", p.Done.ToString("N0"), p.Total.ToString("N0"), p.Current)));

        UndoResult result;
        try
        {
            result = await _organizer.UndoAsync(entry.Moves, progress, ct);
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка отмены сортировки", ex);
            _status.Finish(AppState.Error, Loc.T("OpUndoFailed"), ex.Message);
            return;
        }

        _library.ApplyMoves(result.Restored);
        // Файлы, которые были вынесены за пределы папки загрузок, возвращаем в библиотеку.
        var back = result.Restored
            .Where(r => !_library.Contains(r.TargetPath) && PathHelper.IsInside(r.TargetPath, S.DownloadsPath))
            .Select(r => _scanner.TryCreateEntry(r.TargetPath))
            .OfType<FileEntry>().ToList();
        _library.AddOrUpdateMany(back);

        if (result.Restored.Count > 0)
        {
            // Отмечаем возвращёнными только реально восстановленные файлы. Если часть
            // вернуть не удалось, оставляем их в записи, чтобы можно было повторить отмену.
            var restoredFrom = result.Restored
                .Select(r => r.SourcePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var remaining = entry.Moves.Where(m => !restoredFrom.Contains(m.TargetPath)).ToList();
            if (remaining.Count == 0) _history.MarkUndone(entry.Id);
            else _history.SetMoves(entry.Id, remaining);

            _history.Add(new HistoryEntry
            {
                Kind = HistoryKind.Undo,
                Title = Loc.F("OpUndoDone", ByteFormatter.Files(result.Restored.Count)),
                Count = result.Restored.Count,
                Bytes = result.Restored.Sum(r => r.Size)
            });
        }

        if (result.Restored.Count == 0 && result.Failed.Count > 0)
        {
            _status.Finish(AppState.Error, Loc.T("OpRestoreFailedTitle"), Loc.F("OpErrorsCount", result.Failed.Count));
            _toasts.Show(ToastKind.Error, Loc.T("OpUndoNotDone"), Loc.F("OpErrorsCount", result.Failed.Count), Loc.T("CommonDetails"),
                () => _ = _dialogs.ShowAsync(new ErrorListDialogViewModel(Loc.T("OpUndoErrorsTitle"), result.Failed)));
            return;
        }

        if (result.Failed.Count == 0)
        {
            _status.Finish(AppState.SortComplete, Loc.F("OpRestored", ByteFormatter.Files(result.Restored.Count)));
            _toasts.Success(Loc.T("OpSortUndone"), Loc.F("OpRestoredCount", result.Restored.Count.ToString("N0")));
        }
        else
        {
            // Частичный откат: не говорим «всё отменено», если часть файлов осталась на месте.
            _status.Finish(AppState.SortComplete,
                Loc.F("OpPartiallyRestored", result.Restored.Count.ToString("N0"), result.Failed.Count.ToString("N0")));
            _toasts.Show(ToastKind.Warning, Loc.T("OpSortUndonePartial"),
                Loc.F("OpPartialDetail", result.Restored.Count.ToString("N0"), result.Failed.Count.ToString("N0")) + " " +
                Loc.T("OpPartialHint"),
                Loc.T("CommonDetails"),
                () => _ = _dialogs.ShowAsync(new ErrorListDialogViewModel(Loc.T("OpUndoErrorsTitle"), result.Failed)));
        }
    }

    // ------------------------------------------------------------ Слежение за папкой

    public void ApplyWatcherSettings()
    {
        try
        {
            if (S.WatchNewFiles && Directory.Exists(S.DownloadsPath))
                _watcher.Start(S.DownloadsPath, new WatcherOptions
                {
                    IncludeSubfolders = S.IncludeSubfolders,
                    ExcludedFolders = S.ExcludedFolders.ToList()
                });
            else
                _watcher.Stop();
        }
        catch (Exception ex)
        {
            _log.Error("Не удалось запустить отслеживание папки", ex);
        }
    }

    /// <summary>
    /// Тихо сверяет библиотеку с диском после потери уведомлений наблюдателя: добавляет новые
    /// и изменившиеся файлы, убирает исчезнувшие. В отличие от <see cref="ScanAsync"/> не сбрасывает
    /// результаты поиска дубликатов и выделение и не показывает баннер. Автосортировка к таким
    /// файлам не применяется: пачка файлов из распакованного архива не должна разъехаться по папкам.
    /// </summary>
    internal async Task ResyncAsync()
    {
        if (_resyncRunning) { _resyncAgain = true; return; }
        _resyncRunning = true;
        try
        {
            do
            {
                _resyncAgain = false;
                // Своя операция программы сама меняет файлы - сверяемся после её окончания.
                while (_status.IsBusy) await Task.Delay(1000);
                if (!_library.HasScanned) return;      // первое сканирование ещё не выполнено
                await ResyncOnceAsync();
            } while (_resyncAgain);
        }
        finally { _resyncRunning = false; }
    }

    private async Task ResyncOnceAsync()
    {
        var root = S.DownloadsPath;
        if (!Directory.Exists(root)) return;

        ScanResult result;
        try
        {
            var options = new ScanOptions(S.IncludeSubfolders, S.ExcludedFolders.ToList(), S.ExcludedExtensions.ToList());
            result = await _scanner.ScanAsync(root, options, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.Warn($"Сверка списка с диском не удалась: {ex.Message}");
            return;
        }

        // Папку сменили - её пересканирует обработчик настроек. Началась операция - повторим после неё.
        if (!PathHelper.AreSame(root, S.DownloadsPath)) return;
        if (_status.IsBusy) { _resyncAgain = true; return; }

        var known = _library.Files.ToDictionary(f => f.FullPath, StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(result.Files.Select(f => f.FullPath), StringComparer.OrdinalIgnoreCase);
        // Каждое изменение перепроверяем на диске: пока шло сканирование, файл мог появиться или исчезнуть.
        var gone = known.Keys.Where(p => !found.Contains(p) && !File.Exists(p)).ToList();
        var changed = result.Files
            .Where(f => (!known.TryGetValue(f.FullPath, out var k) || k.Size != f.Size || k.Modified != f.Modified)
                        && File.Exists(f.FullPath))
            .ToList();

        if (gone.Count > 0) _library.Remove(gone);
        if (changed.Count > 0) _library.AddOrUpdateMany(changed);
        _log.Info($"Сверка с диском после потери уведомлений: добавлено/обновлено {changed.Count}, убрано {gone.Count}");
    }

    private void OnFileRemoved(string path)
    {
        if (_library.Contains(path))
        {
            _library.Remove(new[] { path });
            return;
        }
        // Удалена или переименована целая папка.
        var inside = _library.Files.Where(f => PathHelper.IsInside(f.FullPath, path)).Select(f => f.FullPath).ToList();
        if (inside.Count > 0) _library.Remove(inside);
    }

    private void OnFileReady(string path)
    {
        if (_library.Contains(path) || !PathHelper.IsInside(path, S.DownloadsPath)) return;
        var entry = _scanner.TryCreateEntry(path);
        if (entry == null) return;
        if (S.ExcludedExtensions.Any(e => string.Equals(e.Trim().TrimStart('.'), entry.Extension, StringComparison.OrdinalIgnoreCase))) return;
        if (PathHelper.IsExcludedFolder(entry.Directory, S.ExcludedFolders)) return;

        _library.AddOrUpdate(entry);
        NewFileArrived?.Invoke(entry);

        var autoSort = S.AutoSortNewFiles && PathHelper.AreSame(entry.Directory, S.DownloadsPath) && !entry.IsTemporary;
        if (autoSort)
        {
            _ = AutoSortAsync(entry);
            return;
        }
        if (S.NotifyOnNewFiles)
            _toasts.Info(Loc.F("OpNewFile", FileCategoryInfo.Title(entry.Category)),
                $"{entry.Name} · {ByteFormatter.Format(entry.Size)}");
    }

    private async Task AutoSortAsync(FileEntry entry)
    {
        try
        {
            var plan = _organizer.BuildPlan(new[] { entry }, new SortOptions(
                S.DownloadsPath, S.EffectiveSortDestination, false, S.ExcludedExtensions.ToList()));
            if (plan.Count == 0) return;

            using var suspend = _watcher.Suspend();
            var result = await _organizer.ExecuteAsync(plan, null, CancellationToken.None);
            _library.ApplyMoves(result.Moved);
            if (result.Moved.Count == 0)
            {
                if (result.Failed.Count > 0)
                    _toasts.Warning(Loc.T("OpAutoSortFailed"), $"{entry.Name}: {result.Failed[0].Reason}");
                return;
            }

            var history = new HistoryEntry
            {
                Kind = HistoryKind.AutoSort,
                Title = Loc.F("OpAutoSort", entry.Name),
                Details = Loc.F("OpToFolder", FileCategoryInfo.FolderName(entry.Category)),
                Count = 1,
                Bytes = entry.Size,
                Moves = result.Moved.ToList()
            };
            _history.Add(history);
            _toasts.Show(ToastKind.Success, Loc.F("OpMovedTo", FileCategoryInfo.FolderName(entry.Category)),
                entry.Name, Loc.T("CommonUndo"), () => _ = UndoAsync(history));
        }
        catch (Exception ex)
        {
            _log.Error("Ошибка автосортировки", ex);
            _toasts.Warning(Loc.T("OpAutoSortFailed"), ex.Message);
        }
    }
}

public static class FileListLimits
{
    /// <summary>Максимум строк, для которых проигрывается анимация удаления.</summary>
    public const int AnimatedRemovalMax = 80;
}
