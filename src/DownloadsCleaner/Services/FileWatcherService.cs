using System.Diagnostics;
using System.IO;
using System.Collections.Concurrent;
using DownloadsCleaner.Helpers;

namespace DownloadsCleaner.Services;

public sealed record WatcherOptions
{
    public bool IncludeSubfolders { get; init; } = true;
    /// <summary>Сколько файл должен оставаться неизменным и незаблокированным, чтобы считаться скачанным.</summary>
    public TimeSpan StabilityDelay { get; init; } = TimeSpan.FromMilliseconds(1500);
    /// <summary>Максимальное ожидание завершения загрузки.</summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromMinutes(30);
    public IReadOnlyCollection<string> ExcludedFolders { get; init; } = Array.Empty<string>();
}

public interface IFileWatcherService : IDisposable
{
    /// <summary>Новый файл полностью записан и не используется другими процессами.</summary>
    event EventHandler<string>? FileReady;
    /// <summary>Файл удалён или переименован (старый путь).</summary>
    event EventHandler<string>? FileRemoved;
    bool IsRunning { get; }
    void Start(string root, WatcherOptions? options = null);
    void Stop();
    /// <summary>События временно игнорируются (например, пока приложение само перемещает файлы).</summary>
    IDisposable Suspend();
}

/// <summary>
/// Отслеживает папку через FileSystemWatcher. Новый файл попадает в FileReady только после того,
/// как перестаёт расти и освобождается другими приложениями (т. е. загрузка завершена).
/// </summary>
public sealed class FileWatcherService : IFileWatcherService
{
    private readonly IFileTypeDetectionService _detector;
    private readonly ILogService _log;
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Защищает запуск/остановку от гонки с фоновым перезапуском.</summary>
    private readonly object _lifecycleGate = new();
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cts;
    private WatcherOptions _options = new();
    private string _root = string.Empty;
    private int _suspended;
    private int _restartRequested;
    private int _generation;

    public event EventHandler<string>? FileReady;
    public event EventHandler<string>? FileRemoved;
    public bool IsRunning => _watcher != null;

    public FileWatcherService(IFileTypeDetectionService detector, ILogService? log = null)
    {
        _detector = detector;
        _log = log ?? NullLogService.Instance;
    }

    public void Start(string root, WatcherOptions? options = null)
    {
        lock (_lifecycleGate)
        {
            Stop();
            if (!Directory.Exists(root)) return;
            _root = root;
            _options = options ?? new WatcherOptions();
            _cts = new CancellationTokenSource();

            var w = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = _options.IncludeSubfolders,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024
            };
            w.Created += (_, e) => OnCreated(e.FullPath);
            w.Renamed += (_, e) => { OnDeleted(e.OldFullPath); OnCreated(e.FullPath); };
            w.Deleted += (_, e) => OnDeleted(e.FullPath);
            w.Error += (_, e) =>
            {
                _log.Warn($"Ошибка FileSystemWatcher: {e.GetException().Message}. Перезапуск.");
                RequestRestart();
            };
            w.EnableRaisingEvents = true;
            _watcher = w;
            _log.Info($"Отслеживание запущено: {root}");
        }
    }

    /// <summary>
    /// Перезапуск слежения вне обработчика события: вызов Start прямо из Error
    /// приводил к вложенному Stop/Start, утечке watcher'а и потере событий.
    /// </summary>
    private void RequestRestart()
    {
        if (Interlocked.Exchange(ref _restartRequested, 1) == 1) return;
        var root = _root;
        var options = _options;
        var generation = Volatile.Read(ref _generation);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500).ConfigureAwait(false);
                // Пока ждали, слежение могли остановить (или перезапустить с другими
                // настройками). Тогда старый запрос на перезапуск уже неактуален.
                if (Volatile.Read(ref _generation) != generation) return;
                Start(root, options);
            }
            catch (Exception ex)
            {
                _log.Warn($"Не удалось перезапустить слежение: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _restartRequested, 0);
            }
        });
    }

    public void Stop()
    {
        lock (_lifecycleGate)
        {
            Interlocked.Increment(ref _generation);
            var w = _watcher;
            _watcher = null;
            if (w != null)
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _pending.Clear();
            // Счётчик приостановки сбрасываем: иначе после остановки слежение
            // ещё какое-то время игнорировало бы события.
            Interlocked.Exchange(ref _suspended, 0);
        }
    }

    public IDisposable Suspend()
    {
        Interlocked.Increment(ref _suspended);
        return new Resumer(this);
    }

    private sealed class Resumer : IDisposable
    {
        private FileWatcherService? _owner;
        public Resumer(FileWatcherService owner) => _owner = owner;
        public void Dispose()
        {
            // Небольшая задержка: уведомления файловой системы приходят с опозданием.
            var o = Interlocked.Exchange(ref _owner, null);
            if (o == null) return;
            _ = Task.Delay(800).ContinueWith(_ => o.Resume(), TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Уменьшает счётчик приостановки, не уходя в минус. Иначе запоздавший Resume
    /// после Stop мог оставить счётчик отрицательным, и следующий Suspend не
    /// приостанавливал бы слежение.
    /// </summary>
    private void Resume()
    {
        while (true)
        {
            var current = Volatile.Read(ref _suspended);
            if (current <= 0) return;
            if (Interlocked.CompareExchange(ref _suspended, current - 1, current) == current) return;
        }
    }

    private bool IsSuspended => Volatile.Read(ref _suspended) > 0;

    private void OnDeleted(string path)
    {
        if (IsSuspended) return;
        _pending.TryRemove(path, out _);
        FileRemoved?.Invoke(this, path);
    }

    private void OnCreated(string path)
    {
        if (IsSuspended) return;
        var cts = _cts;
        if (cts == null) return;
        if (_options.ExcludedFolders.Count > 0 &&
            PathHelper.IsExcludedFolder(Path.GetDirectoryName(path) ?? path, _options.ExcludedFolders)) return;
        if (Directory.Exists(path)) return;                       // папки не обрабатываем
        if (_detector.IsTemporary(path)) return;                  // ждём переименования в итоговое имя
        if (!_pending.TryAdd(path, 0)) return;                    // уже ожидаем этот файл

        var token = cts.Token;
        // Настройки фиксируем на момент события: если слежение перезапустят,
        // уже запущенное ожидание не должно внезапно читать чужие лимиты.
        var options = _options;
        _ = Task.Run(async () =>
        {
            try
            {
                if (await WaitUntilReadyAsync(path, options, token) && !IsSuspended && !token.IsCancellationRequested)
                    FileReady?.Invoke(this, path);
            }
            catch (OperationCanceledException) { /* остановка */ }
            catch (Exception ex) { _log.Warn($"Ошибка ожидания файла {path}: {ex.Message}"); }
            finally { _pending.TryRemove(path, out _); }
        }, token);
    }

    private async Task<bool> WaitUntilReadyAsync(string path, WatcherOptions options, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        long lastSize = -1;
        TimeSpan? stableSince = null;
        var poll = TimeSpan.FromMilliseconds(Math.Clamp(options.StabilityDelay.TotalMilliseconds / 3, 25, 500));

        while (started.Elapsed < options.MaxWait)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(path)) return false;                // файл исчез (удалён/переименован)
            if (_detector.IsTemporary(path)) return false;

            long size;
            try { size = new FileInfo(path).Length; }
            catch { size = -1; }

            if (size >= 0 && size == lastSize && TryOpenExclusive(path))
            {
                stableSince ??= started.Elapsed;
                if (started.Elapsed - stableSince.Value >= options.StabilityDelay) return true;
            }
            else
            {
                stableSince = null;
            }
            lastSize = size;
            await Task.Delay(poll, ct);
        }

        // Не ждём бесконечно: файл, который так и не освободился, остаётся
        // необработанным, но и не занимает фоновую задачу навсегда.
        _log.Warn($"Файл не освободился за {options.MaxWait.TotalMinutes:0.#} мин: {path}");
        return false;
    }

    /// <summary>
    /// Проверяет, что файл не занят на запись. Раньше здесь был FileShare.None,
    /// из-за чего антивирус или индексатор, открывшие файл на чтение, навсегда
    /// делали его «занятым». Теперь читателям открывать можно, а чужая запись
    /// (загрузчик ещё пишет файл) по-прежнему даёт ошибку совместного доступа.
    /// </summary>
    internal static bool TryOpenExclusive(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1, FileOptions.SequentialScan);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public void Dispose() => Stop();
}
