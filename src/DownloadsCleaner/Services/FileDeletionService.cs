using DownloadsCleaner.Helpers;
using DownloadsCleaner.Localization;
using DownloadsCleaner.Models;

namespace DownloadsCleaner.Services;

/// <summary>Отправка файла в Корзину операционной системы.</summary>
public interface IRecycleBin
{
    /// <summary>Бросает исключение, если файл не удалось отправить в Корзину.</summary>
    void MoveToRecycleBin(string path);
}

public interface IFileDeletionService
{
    /// <summary>
    /// Удаляет файлы. Файлы вне <paramref name="allowedRoot"/> не удаляются никогда.
    /// </summary>
    Task<DeletionResult> DeleteAsync(IReadOnlyList<FileEntry> files, DeleteMode mode, string allowedRoot,
        IProgress<OperationProgress>? progress, CancellationToken ct);
}

public sealed class FileDeletionService : IFileDeletionService
{
    private readonly IRecycleBin _recycleBin;
    private readonly ILogService _log;

    public FileDeletionService(IRecycleBin recycleBin, ILogService? log = null)
    {
        _recycleBin = recycleBin;
        _log = log ?? NullLogService.Instance;
    }

    public Task<DeletionResult> DeleteAsync(IReadOnlyList<FileEntry> files, DeleteMode mode, string allowedRoot,
        IProgress<OperationProgress>? progress, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var deleted = new List<FileEntry>();
            var failed = new List<FailedOperation>();
            var overflow = new List<FailedOperation>();
            var cancelled = false;
            var total = files.Count;

            for (var i = 0; i < total; i++)
            {
                if (ct.IsCancellationRequested) { cancelled = true; break; }
                var f = files[i];
                progress?.Report(new OperationProgress(i, total, f.Name));
                try
                {
                    if (!PathHelper.IsInside(f.FullPath, allowedRoot))
                        throw new InvalidOperationException(Loc.T("ErrOutsideDownloads"));
                    if (!File.Exists(f.FullPath))
                        throw new FileNotFoundException(Loc.T("ErrAlreadyGone"));

                    if (mode == DeleteMode.Recycle)
                    {
                        _recycleBin.MoveToRecycleBin(f.FullPath);
                    }
                    else
                    {
                        var attrs = File.GetAttributes(f.FullPath);
                        var wasReadOnly = (attrs & FileAttributes.ReadOnly) != 0;
                        if (wasReadOnly)
                            File.SetAttributes(f.FullPath, attrs & ~FileAttributes.ReadOnly);
                        try
                        {
                            File.Delete(f.FullPath);
                        }
                        catch
                        {
                            // Если удаление не вышло - возвращаем read-only, чтобы файл
                            // не остался без защиты.
                            if (wasReadOnly)
                            {
                                try { File.SetAttributes(f.FullPath, File.GetAttributes(f.FullPath) | FileAttributes.ReadOnly); }
                                catch { /* ignore */ }
                            }
                            throw;
                        }
                    }
                    deleted.Add(f);
                }
                catch (RecycleBinOverflowException ex)
                {
                    // Файл не помещается в Корзину (или она отключена): он остаётся на месте,
                    // а не исчезает навсегда.
                    _log.Warn($"Файл оставлен на месте {f.FullPath}: {ex.Message}");
                    failed.Add(new FailedOperation(f.FullPath, ex.Message));
                    overflow.Add(new FailedOperation(f.FullPath, ex.Message));
                }
                catch (OperationCanceledException)
                {
                    failed.Add(new FailedOperation(f.FullPath, Loc.T("ErrUserCancelled")));
                }
                catch (Exception ex)
                {
                    _log.Warn($"Не удалось удалить {f.FullPath}: {ex.Message}");
                    failed.Add(new FailedOperation(f.FullPath, FileOrganizationService.Describe(ex)));
                }
            }

            progress?.Report(new OperationProgress(total, total, string.Empty));
            _log.Info($"Удаление ({mode}): удалено {deleted.Count}, ошибок {failed.Count}, отменено: {cancelled}");
            return new DeletionResult
            {
                Mode = mode,
                Deleted = deleted,
                Failed = failed,
                Cancelled = cancelled,
                NotRecycled = overflow
            };
        });
    }
}
