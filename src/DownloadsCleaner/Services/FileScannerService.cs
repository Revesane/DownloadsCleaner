using System.IO;
using System.Diagnostics;
using System.IO.Enumeration;
using DownloadsCleaner.Helpers;
using DownloadsCleaner.Models;

namespace DownloadsCleaner.Services;

public interface IFileScannerService
{
    /// <summary>Асинхронно сканирует папку. При отмене бросает OperationCanceledException.</summary>
    Task<ScanResult> ScanAsync(string root, ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken ct);

    /// <summary>Создаёт запись для одного файла (или null, если файл недоступен).</summary>
    FileEntry? TryCreateEntry(string path);
}

public sealed class FileScannerService : IFileScannerService
{
    private readonly IFileTypeDetectionService _detector;
    private readonly ILogService _log;

    public FileScannerService(IFileTypeDetectionService detector, ILogService? log = null)
    {
        _detector = detector;
        _log = log ?? NullLogService.Instance;
    }

    public Task<ScanResult> ScanAsync(string root, ScanOptions options, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();
            var list = new List<FileEntry>();
            var inaccessible = 0;

            if (!Directory.Exists(root))
                return new ScanResult { Files = list, Duration = sw.Elapsed };

            var excludedExt = new HashSet<string>(
                options.ExcludedExtensions.Select(_detector.NormalizeExtension).Where(e => e.Length > 0),
                StringComparer.OrdinalIgnoreCase);

            var enumOptions = new EnumerationOptions
            {
                RecurseSubdirectories = options.IncludeSubfolders,
                IgnoreInaccessible = false,           // ошибки считаем сами в ContinueOnError
                // ReparsePoint здесь НЕ пропускаем: облачные «placeholder»-файлы
                // (OneDrive, Google Drive, Dropbox) — это reparse points, и такой фильтр
                // молча терял их целиком. Защиту от junction/symlink-петель делаем
                // вручную в ShouldRecurseIntoEntry — только для каталогов.
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false,
                MatchType = MatchType.Simple
            };

            using var en = new Enumerator(root, enumOptions, _detector, options.ExcludedFolders, excludedExt);
            var lastReport = Stopwatch.StartNew();
            long bytes = 0;

            while (en.MoveNext())
            {
                ct.ThrowIfCancellationRequested();
                var f = en.Current;
                list.Add(f);
                bytes += f.Size;
                if (progress != null && lastReport.ElapsedMilliseconds >= 80)
                {
                    progress.Report(new ScanProgress(list.Count, bytes, f.Directory));
                    lastReport.Restart();
                }
            }
            inaccessible = en.ErrorCount;
            progress?.Report(new ScanProgress(list.Count, bytes, root));
            _log.Info($"Сканирование {root}: {list.Count} файлов, недоступно: {inaccessible}, {sw.ElapsedMilliseconds} мс");
            return new ScanResult { Files = list, InaccessibleCount = inaccessible, Duration = sw.Elapsed };
        }, ct);
    }

    public FileEntry? TryCreateEntry(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            return Create(_detector, info.FullName, info.Name, info.Length,
                info.CreationTime, info.LastWriteTime);
        }
        catch (Exception ex)
        {
            _log.Warn($"Не удалось получить сведения о файле {path}: {ex.Message}");
            return null;
        }
    }

    private static FileEntry Create(IFileTypeDetectionService d, string fullPath, string name, long size, DateTime created, DateTime modified)
    {
        var ext = d.NormalizeExtension(name);
        return new FileEntry(fullPath, name, ext, size, created, modified, d.Detect(ext), d.IsTemporary(ext));
    }

    private sealed class Enumerator : FileSystemEnumerator<FileEntry>
    {
        private readonly IFileTypeDetectionService _detector;
        private readonly IReadOnlyCollection<string> _excludedFolders;
        private readonly HashSet<string> _excludedExt;
        public int ErrorCount { get; private set; }

        public Enumerator(string root, EnumerationOptions options, IFileTypeDetectionService detector,
            IReadOnlyCollection<string> excludedFolders, HashSet<string> excludedExt)
            : base(root, options)
        {
            _detector = detector;
            _excludedFolders = excludedFolders;
            _excludedExt = excludedExt;
        }

        protected override bool ShouldIncludeEntry(ref FileSystemEntry entry)
        {
            if (entry.IsDirectory) return false;
            if ((entry.Attributes & FileAttributes.System) != 0) return false; // desktop.ini и т.п.
            if (_excludedExt.Count > 0)
            {
                var ext = _detector.NormalizeExtension(entry.FileName.ToString());
                if (_excludedExt.Contains(ext)) return false;
            }
            return true;
        }

        protected override bool ShouldRecurseIntoEntry(ref FileSystemEntry entry)
        {
            // Не заходим в junction/symlink-каталоги: иначе возможны петли и выход
            // за пределы сканируемой папки. На файлы это ограничение не распространяется.
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            if (_excludedFolders.Count == 0) return true;
            return !PathHelper.IsExcludedFolder(entry.ToFullPath(), _excludedFolders);
        }

        protected override FileEntry TransformEntry(ref FileSystemEntry entry)
        {
            var name = entry.FileName.ToString();
            return Create(_detector, entry.ToFullPath(), name, entry.Length,
                entry.CreationTimeUtc.LocalDateTime, entry.LastWriteTimeUtc.LocalDateTime);
        }

        protected override bool ContinueOnError(int error)
        {
            ErrorCount++;
            return true;
        }
    }
}
