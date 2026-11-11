using DownloadsCleaner.Helpers;
using DownloadsCleaner.Localization;
using DownloadsCleaner.Models;

namespace DownloadsCleaner.Services;

public interface IFileOrganizationService
{
    /// <summary>Строит предварительный план сортировки. Файловую систему не изменяет.</summary>
    SortPlan BuildPlan(IEnumerable<FileEntry> files, SortOptions options);

    /// <summary>Выполняет план. При отмене возвращает уже выполненные перемещения (Cancelled = true).</summary>
    Task<SortResult> ExecuteAsync(SortPlan plan, IProgress<OperationProgress>? progress, CancellationToken ct);

    /// <summary>Возвращает файлы на прежние места.</summary>
    Task<UndoResult> UndoAsync(IReadOnlyList<MoveRecord> moves, IProgress<OperationProgress>? progress, CancellationToken ct);
}

public sealed class FileOrganizationService : IFileOrganizationService
{
    private readonly ILogService _log;

    public FileOrganizationService(ILogService? log = null) => _log = log ?? NullLogService.Instance;

    public SortPlan BuildPlan(IEnumerable<FileEntry> files, SortOptions options)
    {
        var moves = new List<PlannedMove>();
        var skipped = new List<SkippedFile>();
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceRoot = PathHelper.Normalize(options.SourceRoot);
        var excluded = new HashSet<string>(options.ExcludedExtensions.Select(e => e.TrimStart('.').ToLowerInvariant()));

        foreach (var file in files)
        {
            if (file.IsTemporary)
            {
                skipped.Add(new SkippedFile(file, Loc.T("ErrTempFile")));
                continue;
            }
            if (excluded.Contains(file.Extension))
            {
                skipped.Add(new SkippedFile(file, Loc.T("ErrExcludedExt")));
                continue;
            }
            var targetDir = Path.Combine(options.DestinationRoot, options.FolderName(file.Category));
            var desired = Path.Combine(targetDir, file.Name);

            // Защита от перемещения файла в самого себя / уже на месте.
            if (PathHelper.AreSame(file.FullPath, desired) || PathHelper.AreSame(file.Directory, targetDir))
            {
                skipped.Add(new SkippedFile(file, Loc.T("ErrAlreadyInPlace")));
                continue;
            }

            // Файлы во вложенных папках не трогаем, пока пользователь явно не разрешит.
            if (!options.IncludeSubfolders && !PathHelper.AreSame(file.Directory, sourceRoot))
            {
                skipped.Add(new SkippedFile(file, Loc.T("ErrInSubfolder")));
                continue;
            }

            var unique = PathHelper.GetUniquePath(desired, p => reserved.Contains(p));
            reserved.Add(unique);
            moves.Add(new PlannedMove(file, unique, !string.Equals(unique, desired, StringComparison.OrdinalIgnoreCase)));
        }

        return new SortPlan { Options = options, Moves = moves, Skipped = skipped };
    }

    public Task<SortResult> ExecuteAsync(SortPlan plan, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var moved = new List<MoveRecord>();
            var failed = new List<FailedOperation>();
            var cancelled = false;
            var total = plan.Moves.Count;
            // Папки, которые создали мы сами: только их можно убрать при отмене.
            var createdDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < total; i++)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                var m = plan.Moves[i];
                progress?.Report(new OperationProgress(i, total, m.File.Name));
                try
                {
                    var targetDir = Path.GetDirectoryName(m.TargetPath) ?? string.Empty;
                    if (targetDir.Length > 0 && !Directory.Exists(targetDir)) createdDirs.Add(targetDir);

                    var actual = MoveWithoutOverwrite(m.File.FullPath, m.TargetPath);
                    moved.Add(new MoveRecord(m.File.FullPath, actual, m.File.Category, m.File.Size)
                    {
                        CreatedTargetDirectory = targetDir.Length > 0 && createdDirs.Contains(targetDir)
                    });
                }
                catch (Exception ex)
                {
                    _log.Warn($"Не удалось переместить {m.File.FullPath}: {ex.Message}");
                    failed.Add(new FailedOperation(m.File.FullPath, Describe(ex)));
                }
            }

            progress?.Report(new OperationProgress(total, total, string.Empty));
            _log.Info($"Сортировка: перемещено {moved.Count}, ошибок {failed.Count}, отменена: {cancelled}");
            return new SortResult { Moved = moved, Failed = failed, Cancelled = cancelled };
        });
    }

    public Task<UndoResult> UndoAsync(IReadOnlyList<MoveRecord> moves, IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var restored = new List<MoveRecord>();
            var failed = new List<FailedOperation>();
            var cancelled = false;
            var touchedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = moves.Count - 1, n = 0; i >= 0; i--, n++)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                var m = moves[i];
                progress?.Report(new OperationProgress(n, moves.Count, Path.GetFileName(m.TargetPath)));
                try
                {
                    if (!File.Exists(m.TargetPath))
                        throw new FileNotFoundException(Loc.T("ErrNoLongerInTarget"));
                    var dir = Path.GetDirectoryName(m.SourcePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    var actual = MoveWithoutOverwrite(m.TargetPath, m.SourcePath);
                    // Убираем только те папки категорий, которые создала сама сортировка.
                    if (m.CreatedTargetDirectory)
                    {
                        var targetDir = Path.GetDirectoryName(m.TargetPath);
                        if (!string.IsNullOrEmpty(targetDir)) touchedDirs.Add(targetDir);
                    }
                    restored.Add(new MoveRecord(m.TargetPath, actual, m.Category, m.Size));
                }
                catch (Exception ex)
                {
                    failed.Add(new FailedOperation(m.TargetPath, Describe(ex)));
                }
            }

            // Убираем пустые папки категорий, оставшиеся после возврата файлов.
            foreach (var d in touchedDirs)
            {
                try
                {
                    if (d.Length > 0 && Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any())
                        Directory.Delete(d);
                }
                catch { /* не критично */ }
            }

            progress?.Report(new OperationProgress(moves.Count, moves.Count, string.Empty));
            return new UndoResult { Restored = restored, Failed = failed, Cancelled = cancelled };
        });
    }

    /// <summary>
    /// Перемещает файл, никогда не перезаписывая существующий. Если имя занято
    /// (в том числе в гонке с другим процессом) - подбирает новое.
    /// </summary>
    internal static string MoveWithoutOverwrite(string source, string target)
    {
        if (PathHelper.AreSame(source, target))
            throw new IOException(Loc.T("ErrSourceEqualsTarget"));
        if (!File.Exists(source))
            throw new FileNotFoundException(Loc.T("ErrFileNotFound"), source);

        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var current = target;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                current = PathHelper.GetUniquePath(target);
                continue;
            }
            try
            {
                File.Move(source, current, overwrite: false);
                return current;
            }
            catch (IOException) when (File.Exists(current) && File.Exists(source))
            {
                // Имя заняли между проверкой и перемещением - пробуем другое.
                current = PathHelper.GetUniquePath(target);
            }
        }
        throw new IOException(Loc.T("ErrNoFreeName"));
    }

    internal static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => Loc.T("ErrNoAccess"),
        FileNotFoundException => Loc.T("ErrFileNotFound"),
        DirectoryNotFoundException => Loc.T("ErrFolderNotFound"),
        PathTooLongException => Loc.T("ErrPathTooLong"),
        IOException io when io.Message.Contains("being used", StringComparison.OrdinalIgnoreCase)
                            || io.HResult == unchecked((int)0x80070020) => Loc.T("ErrFileInUse"),
        _ => ex.Message
    };
}
