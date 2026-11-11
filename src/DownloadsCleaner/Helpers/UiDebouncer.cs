namespace DownloadsCleaner.Helpers;

/// <summary>
/// Откладывает выполнение действия: повторный вызов сбрасывает таймер.
/// Нужен для тяжёлых пересборок (аналитика, дубликаты): поток изменений библиотеки
/// не должен вызывать полный пересчёт на каждый файл.
/// </summary>
public sealed class UiDebouncer : IDisposable
{
    private readonly Action _action;
    private readonly TimeSpan _delay;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public UiDebouncer(TimeSpan delay, Action action)
    {
        _delay = delay;
        _action = action;
    }

    public void Schedule()
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_disposed) return;
            _cts?.Cancel();
            cts = _cts = new CancellationTokenSource();
        }

        _ = Task.Delay(_delay, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            UiThread.Post(_action);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cts?.Cancel();
            _cts = null;
        }
    }
}
