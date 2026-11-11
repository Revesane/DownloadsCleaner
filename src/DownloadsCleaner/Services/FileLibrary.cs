using DownloadsCleaner.Helpers;
using DownloadsCleaner.Models;

namespace DownloadsCleaner.Services;

public sealed record CategoryStat(FileCategory Category, int Count, long Bytes);

public sealed record LibraryStats(
    int TotalFiles,
    long TotalBytes,
    int SortableCount,
    long SortableBytes,
    int OldCount,
    long OldBytes,
    int TempCount,
    long TempBytes,
    int LargeCount,
    long LargeBytes,
    IReadOnlyList<CategoryStat> Categories,
    bool DuplicatesKnown,
    int DuplicateCopies,
    long DuplicateBytes,
    long FreeableBytes)
{
    public static LibraryStats Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        FileCategoryInfo.All.Select(c => new CategoryStat(c, 0, 0)).ToList(), false, 0, 0, 0);
}

public interface IFileLibrary
{
    IReadOnlyList<FileEntry> Files { get; }
    LibraryStats Stats { get; }
    bool HasScanned { get; }
    IReadOnlyList<DuplicateGroup> Duplicates { get; }
    event EventHandler? Changed;

    bool Contains(string path);
    void SetFiles(IReadOnlyList<FileEntry> files);
    void AddOrUpdate(FileEntry entry);
    void AddOrUpdateMany(IEnumerable<FileEntry> entries);
    void Remove(IEnumerable<string> paths);
    void ApplyMoves(IEnumerable<MoveRecord> moves);
    void SetDuplicates(IReadOnlyList<DuplicateGroup> groups);
    void Refresh();
}

/// <summary>
/// Единый источник правды о файлах в папке загрузок. Изменяется только из UI-потока,
/// поэтому внутри нет блокировок.
///
/// Список файлов и статистика считаются лениво и кэшируются: иначе поток новых
/// файлов давал O(n) на каждую правку (пересборка списка + пересчёт статистики),
/// а список пересобирался заново при каждом обращении.
/// </summary>
public sealed class FileLibrary : IFileLibrary
{
    private readonly ISettingsService _settings;
    private readonly Dictionary<string, FileEntry> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<FileEntry>? _filesCache;
    private LibraryStats? _statsCache;
    private IReadOnlyList<DuplicateGroup> _duplicates = Array.Empty<DuplicateGroup>();
    private bool _duplicatesKnown;
    private bool _publishPending;

    public FileLibrary(ISettingsService settings)
    {
        _settings = settings;
    }

    public IReadOnlyList<FileEntry> Files => _filesCache ??= _byPath.Values.ToList();
    public LibraryStats Stats => _statsCache ??= Compute();
    public bool HasScanned { get; private set; }
    public IReadOnlyList<DuplicateGroup> Duplicates => _duplicates;
    public event EventHandler? Changed;

    public bool Contains(string path) => _byPath.ContainsKey(path);

    public void SetFiles(IReadOnlyList<FileEntry> files)
    {
        _byPath.Clear();
        foreach (var f in files) _byPath[f.FullPath] = f;
        HasScanned = true;
        // Результаты поиска дубликатов устарели: файлы могли измениться.
        _duplicates = Array.Empty<DuplicateGroup>();
        _duplicatesKnown = false;
        Publish();
    }

    public void AddOrUpdate(FileEntry entry)
    {
        if (IsContentChanged(entry)) _duplicatesKnown = false;
        _byPath[entry.FullPath] = entry;
        Publish();
    }

    public void Remove(IEnumerable<string> paths)
    {
        var removed = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var any = false;
        foreach (var p in removed) any |= _byPath.Remove(p);
        if (!any) return;

        if (_duplicates.Count > 0)
        {
            _duplicates = _duplicates
                .Select(g => g with { Files = g.Files.Where(f => !removed.Contains(f.FullPath)).ToList() })
                .Where(g => g.Files.Count > 1)
                .ToList();
        }
        Publish();
    }

    public void AddOrUpdateMany(IEnumerable<FileEntry> entries)
    {
        var any = false;
        var changed = false;
        foreach (var e in entries)
        {
            if (IsContentChanged(e)) changed = true;
            _byPath[e.FullPath] = e;
            any = true;
        }
        if (!any) return;
        // Новый или изменившийся файл мог образовать новую группу дубликатов,
        // поэтому прежний результат поиска больше не считается актуальным.
        if (changed) _duplicatesKnown = false;
        Publish();
    }

    /// <summary>Отличается ли запись от уже известной (появление файла или смена содержимого).</summary>
    private bool IsContentChanged(FileEntry entry) =>
        !_byPath.TryGetValue(entry.FullPath, out var existing) ||
        existing.Size != entry.Size ||
        existing.Modified != entry.Modified;

    /// <summary>
    /// Обновляет пути после перемещения. Файлы, ушедшие за пределы папки загрузок,
    /// исключаются из библиотеки.
    /// </summary>
    public void ApplyMoves(IEnumerable<MoveRecord> moves)
    {
        var root = _settings.Current.DownloadsPath;
        var map = new Dictionary<string, FileEntry?>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in moves)
        {
            if (!_byPath.TryGetValue(m.SourcePath, out var entry)) continue;
            _byPath.Remove(m.SourcePath);
            if (string.IsNullOrWhiteSpace(root) || PathHelper.IsInside(m.TargetPath, root))
            {
                var moved = entry.WithPath(m.TargetPath, entry.Category);
                _byPath[moved.FullPath] = moved;
                map[m.SourcePath] = moved;
            }
            else
            {
                map[m.SourcePath] = null;
            }
        }
        if (map.Count == 0) return;

        if (_duplicates.Count > 0)
        {
            _duplicates = _duplicates
                .Select(g => g with
                {
                    Files = g.Files
                        .Select(f => map.TryGetValue(f.FullPath, out var n) ? n : f)
                        .OfType<FileEntry>().ToList()
                })
                .Where(g => g.Files.Count > 1)
                .ToList();
        }
        Publish();
    }

    public void SetDuplicates(IReadOnlyList<DuplicateGroup> groups)
    {
        // Пока шёл поиск, файлы могли удалить или переместить: оставляем только актуальные,
        // иначе в результатах появились бы уже не существующие копии.
        _duplicates = groups
            .Select(g => g with { Files = g.Files.Where(f => _byPath.ContainsKey(f.FullPath)).ToList() })
            .Where(g => g.Files.Count > 1)
            .ToList();
        _duplicatesKnown = true;
        Publish();
    }

    public void Refresh() => Publish();

    /// <summary>
    /// Инвалидирует кэши и ставит уведомление подписчикам в очередь диспетчера.
    /// Так пачка изменений (например, добавление тысячи новых файлов) приводит
    /// к одному пересчёту, а не к тысяче.
    /// </summary>
    private void Publish()
    {
        _filesCache = null;
        _statsCache = null;
        if (_publishPending) return;
        _publishPending = true;
        UiThread.Post(() =>
        {
            _publishPending = false;
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    private LibraryStats Compute()
    {
        var s = _settings.Current;
        var oldBefore = DateTime.Now.AddDays(-Math.Max(1, s.OldFilesDays));
        var root = string.IsNullOrWhiteSpace(s.DownloadsPath) ? null : PathHelper.Normalize(s.DownloadsPath);
        var excluded = new HashSet<string>(s.ExcludedExtensions.Select(e => e.Trim().TrimStart('.').ToLowerInvariant()));
        const long LargeLimit = 100L * 1024 * 1024;

        int total = 0, sortable = 0, old = 0, temp = 0, large = 0;
        long totalBytes = 0, sortableBytes = 0, oldBytes = 0, tempBytes = 0, largeBytes = 0;
        var counts = new int[FileCategoryInfo.All.Count];
        var bytes = new long[FileCategoryInfo.All.Count];

        foreach (var f in Files)
        {
            total++;
            totalBytes += f.Size;
            counts[(int)f.Category]++;
            bytes[(int)f.Category] += f.Size;

            if (f.Created < oldBefore) { old++; oldBytes += f.Size; }
            if (f.IsTemporary) { temp++; tempBytes += f.Size; }
            if (f.Size >= LargeLimit) { large++; largeBytes += f.Size; }

            if (!f.IsTemporary && !excluded.Contains(f.Extension) && root != null
                && string.Equals(f.Directory.TrimEnd('\\', '/'), root, StringComparison.OrdinalIgnoreCase))
            {
                sortable++;
                sortableBytes += f.Size;
            }
        }

        var cats = FileCategoryInfo.All.Select(c => new CategoryStat(c, counts[(int)c], bytes[(int)c])).ToList();
        var dupCopies = _duplicates.Sum(g => g.Files.Count - 1);
        var dupBytes = _duplicates.Sum(g => g.WastedBytes);

        return new LibraryStats(total, totalBytes, sortable, sortableBytes, old, oldBytes, temp, tempBytes,
            large, largeBytes, cats, _duplicatesKnown, dupCopies, dupBytes, tempBytes + dupBytes);
    }
}
