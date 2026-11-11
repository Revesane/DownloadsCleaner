using System.Text.Json;
using DownloadsCleaner.Helpers;
using DownloadsCleaner.Models;

namespace DownloadsCleaner.Services;

public interface IHistoryService
{
    IReadOnlyList<HistoryEntry> Entries { get; }
    event EventHandler? Changed;
    void Add(HistoryEntry entry);
    void MarkUndone(Guid id);
    /// <summary>Заменяет список перемещений записи (после частичной отмены).</summary>
    void SetMoves(Guid id, IReadOnlyList<MoveRecord> moves);
    void Clear();
}

/// <summary>
/// История операций в %LocalAppData%\DownloadsCleaner\history.json.
/// Запись выполняется в фоне: после сортировки большой папки в записи может быть
/// десятки тысяч перемещений, и синхронная запись JSON подвешивала интерфейс.
/// </summary>
public sealed class HistoryService : IHistoryService, IDisposable
{
    private const int MaxEntries = 300;
    /// <summary>Сколько последних записей сохраняют данные для отмены.</summary>
    private const int KeepMovesFor = 30;

    private readonly string _path;
    private readonly ILogService _log;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private List<HistoryEntry> _entries = new();
    private IReadOnlyList<HistoryEntry> _sorted = Array.Empty<HistoryEntry>();
    private int _saveScheduled;
    private int _dirty;
    private bool _disposed;

    public event EventHandler? Changed;

    public HistoryService(string? path = null, ILogService? log = null)
    {
        _path = path ?? Path.Combine(KnownFolders.LocalAppData, "history.json");
        _log = log ?? NullLogService.Instance;
        Load();
    }

    public IReadOnlyList<HistoryEntry> Entries
    {
        get { lock (_gate) return _sorted; }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
                _entries = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex)
        {
            _log.Error("Не удалось прочитать историю операций", ex);
            _entries = new();
        }
        Resort();
    }

    private void Resort()
    {
        _sorted = _entries.OrderByDescending(e => e.Time).ToList();
    }

    /// <summary>
    /// Готовит данные к записи: у старых записей перемещения выбрасываются,
    /// иначе файл истории после сортировки большой папки разрастается на десятки мегабайт.
    /// </summary>
    private List<HistoryEntry> Snapshot()
    {
        var keep = _entries
            .OrderByDescending(e => e.Time)
            .Take(KeepMovesFor)
            .Select(e => e.Id)
            .ToHashSet();

        return _entries.Select(e => keep.Contains(e.Id) || e.Moves.Count == 0 ? e : e.WithoutMoves()).ToList();
    }

    private void Persist()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Snapshot()));
            File.Move(tmp, _path, true);
        }
        catch (Exception ex)
        {
            _log.Error("Не удалось сохранить историю операций", ex);
        }
    }

    /// <summary>Ставит фоновую запись. Повторные вызовы схлопываются в одну.</summary>
    private void SchedulePersist()
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _dirty, 1);
        if (Interlocked.Exchange(ref _saveScheduled, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400).ConfigureAwait(false);
                if (_disposed) return;
                await _persistGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    // Проверяем флаг в момент записи, а не в момент планирования:
                    // данные могли измениться, пока мы ждали.
                    lock (_gate)
                    {
                        if (Volatile.Read(ref _dirty) == 1)
                        {
                            Persist();
                            Interlocked.Exchange(ref _dirty, 0);
                        }
                    }
                }
                finally { _persistGate.Release(); }
            }
            catch (Exception ex)
            {
                _log.Error("Не удалось сохранить историю операций", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _saveScheduled, 0);
                // Изменения, пришедшие во время записи, требуют ещё одной записи.
                if (Volatile.Read(ref _dirty) == 1) SchedulePersist();
            }
        });
    }

    public void Add(HistoryEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries)
                _entries = _entries.OrderByDescending(e => e.Time).Take(MaxEntries).ToList();
            Resort();
        }
        SchedulePersist();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MarkUndone(Guid id)
    {
        lock (_gate)
        {
            var e = _entries.FirstOrDefault(x => x.Id == id);
            if (e == null) return;
            e.Undone = true;
            Resort();
        }
        PersistNow();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetMoves(Guid id, IReadOnlyList<MoveRecord> moves)
    {
        lock (_gate)
        {
            var e = _entries.FirstOrDefault(x => x.Id == id);
            if (e == null) return;
            e.Moves = moves.ToList();
            if (e.Moves.Count == 0) e.Undone = true;
            Resort();
        }
        PersistNow();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Resort();
        }
        PersistNow();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Немедленная запись на диск (используется при выходе из приложения).</summary>
    private void PersistNow()
    {
        try
        {
            _persistGate.Wait();
            try
            {
                lock (_gate)
                {
                    // Всё, что было изменено до этого момента, сейчас будет записано.
                    Persist();
                    Interlocked.Exchange(ref _dirty, 0);
                }
            }
            finally { _persistGate.Release(); }
        }
        catch (Exception ex)
        {
            _log.Error("Не удалось сохранить историю операций", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PersistNow();
        _persistGate.Dispose();
    }
}
