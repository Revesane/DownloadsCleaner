using System.Collections.Concurrent;
using System.Security.Cryptography;
using DownloadsCleaner.Localization;
using DownloadsCleaner.Models;

namespace DownloadsCleaner.Services;

public interface IDuplicateDetectionService
{
    /// <summary>
    /// 1) группировка по размеру, 2) быстрый отсев по первым 64 КБ, 3) полный SHA-256.
    /// Дубликатами считаются только файлы с совпадающим SHA-256.
    /// </summary>
    Task<IReadOnlyList<DuplicateGroup>> FindAsync(IEnumerable<FileEntry> files,
        IProgress<DuplicateProgress>? progress, CancellationToken ct);

    /// <summary>Сколько хешей лежит в кэше (для диагностики и тестов).</summary>
    int CachedHashCount { get; }
    void ClearCache();
}

public sealed class DuplicateDetectionService : IDuplicateDetectionService
{
    private const int PrefixBytes = 64 * 1024;
    private const int BufferSize = 1024 * 1024;
    /// <summary>Предел кэша хешей: иначе долгая сессия съедает память.</summary>
    private const int MaxCachedHashes = 20_000;

    private readonly ILogService _log;
    // Ключ: путь + размер + время изменения. Если файл не менялся, хеш не пересчитывается.
    private readonly ConcurrentDictionary<(string Path, long Size, long Ticks), string> _fullCache = new();
    private readonly ConcurrentDictionary<(string Path, long Size, long Ticks), string> _prefixCache = new();

    public int CachedHashCount => _fullCache.Count;
    public void ClearCache() { _fullCache.Clear(); _prefixCache.Clear(); }

    public DuplicateDetectionService(ILogService? log = null) => _log = log ?? NullLogService.Instance;

    public async Task<IReadOnlyList<DuplicateGroup>> FindAsync(IEnumerable<FileEntry> files,
        IProgress<DuplicateProgress>? progress, CancellationToken ct)
    {
        var candidates = files
            .Where(f => f.Size > 0 && !f.IsTemporary)
            .GroupBy(f => f.Size)
            .Where(g => g.Count() > 1)
            .ToList();

        var totalFiles = candidates.Sum(g => g.Count());
        var totalBytes = candidates.Sum(g => g.Key * g.Count());
        long bytesDone = 0;
        var filesDone = 0;
        var result = new List<DuplicateGroup>();
        var parallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

        void Report(string stage) =>
            progress?.Report(new DuplicateProgress(stage, Interlocked.Read(ref bytesDone), totalBytes,
                Volatile.Read(ref filesDone), totalFiles));

        Report(Loc.T("DupStageSize"));

        foreach (var sizeGroup in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var size = sizeGroup.Key;

            // Шаг 2: отсев по префиксу. Читаем лишь начало файла.
            var byPrefix = new ConcurrentDictionary<string, ConcurrentBag<FileEntry>>();
            await Parallel.ForEachAsync(sizeGroup, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
                async (f, token) =>
                {
                    var p = await TryHashAsync(f, prefixOnly: true, token);
                    if (p != null) byPrefix.GetOrAdd(p, _ => new ConcurrentBag<FileEntry>()).Add(f);
                    else
                    {
                        // Хеш недоступен (файл занят или удалён) - в группу он не попадёт,
                        // но его надо учесть в прогрессе, иначе индикатор не дойдёт до конца.
                        Interlocked.Add(ref bytesDone, size);
                        Interlocked.Increment(ref filesDone);
                    }
                });

            foreach (var prefixGroup in byPrefix.Values.Where(g => g.Count > 1))
            {
                ct.ThrowIfCancellationRequested();
                // Шаг 3: полный SHA-256 только для кандидатов.
                var byHash = new ConcurrentDictionary<string, ConcurrentBag<FileEntry>>();
                await Parallel.ForEachAsync(prefixGroup, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
                    async (f, token) =>
                    {
                        var h = await TryHashAsync(f, prefixOnly: false, token);
                        if (h != null) byHash.GetOrAdd(h, _ => new ConcurrentBag<FileEntry>()).Add(f);
                        Interlocked.Add(ref bytesDone, size);
                        Interlocked.Increment(ref filesDone);
                        Report(Loc.T("DupStageHash"));
                    });

                foreach (var (hash, bag) in byHash)
                {
                    if (bag.Count < 2) continue;
                    var ordered = bag.OrderBy(f => f.Created).ThenBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).ToList();
                    result.Add(new DuplicateGroup(hash, size, ordered));
                }
            }

            // Файлы, отсеянные по префиксу, считаем обработанными для индикатора прогресса.
            var skipped = byPrefix.Values.Where(g => g.Count <= 1).Sum(g => g.Count);
            if (skipped > 0)
            {
                Interlocked.Add(ref bytesDone, size * skipped);
                Interlocked.Add(ref filesDone, skipped);
                Report(Loc.T("DupStageHash"));
            }
        }

        Report(Loc.T("StatusDone"));
        _log.Info($"Поиск дубликатов: групп {result.Count}, кэш хешей {_fullCache.Count}");
        return result.OrderByDescending(g => g.WastedBytes).ToList();
    }

    private async Task<string?> TryHashAsync(FileEntry f, bool prefixOnly, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(f.FullPath);
            if (!info.Exists) return null;
            var key = (f.FullPath, info.Length, info.LastWriteTimeUtc.Ticks);
            var cache = prefixOnly ? _prefixCache : _fullCache;
            if (cache.TryGetValue(key, out var cached)) return cached;

            await using var stream = new FileStream(f.FullPath, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                BufferSize = 4096
            });

            string hash;
            if (prefixOnly)
            {
                var buffer = new byte[(int)Math.Min(PrefixBytes, Math.Max(1, info.Length))];
                var read = 0;
                while (read < buffer.Length)
                {
                    var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
                    if (n == 0) break;
                    read += n;
                }
                hash = Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));
            }
            else
            {
                using var sha = SHA256.Create();
                var buffer = new byte[BufferSize];
                int n;
                while ((n = await stream.ReadAsync(buffer, ct)) > 0)
                    sha.TransformBlock(buffer, 0, n, null, 0);
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                hash = Convert.ToHexString(sha.Hash!);
            }

            cache[key] = hash;
            if (cache.Count > MaxCachedHashes) TrimCache(cache);
            return hash;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warn($"Хеш недоступен для {f.FullPath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Освобождает часть кэша, иначе он растёт бесконечно.</summary>
    private static void TrimCache(ConcurrentDictionary<(string Path, long Size, long Ticks), string> cache)
    {
        var excess = cache.Keys.Take(cache.Count - MaxCachedHashes / 2).ToList();
        foreach (var key in excess) cache.TryRemove(key, out _);
    }
}
