using DownloadsCleaner.Models;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.Tests;

/// <summary>Потеря уведомлений наблюдателя (переполнение буфера) и тихая сверка списка с диском.</summary>
public class WatcherResyncTests
{
    private sealed class NoFolderPicker : IFolderPickerService
    {
        public Task<string?> PickFolderAsync(string title, string? initialDirectory) => Task.FromResult<string?>(null);
    }

    [Fact]
    public async Task Watcher_raises_EventsLost_after_restart_on_error()
    {
        using var tmp = new TempDir();
        using var w = new FileWatcherService(Fx.Detector());
        var lost = 0;
        w.EventsLost += (_, _) => Interlocked.Increment(ref lost);
        w.Start(tmp.Downloads);

        w.OnWatcherError(new InternalBufferOverflowException());

        Assert.True(await Fx.WaitAsync(() => Volatile.Read(ref lost) == 1, 4000));
        Assert.True(w.IsRunning);
    }

    [Fact]
    public async Task Watcher_stopped_before_restart_does_not_raise_EventsLost()
    {
        using var tmp = new TempDir();
        using var w = new FileWatcherService(Fx.Detector());
        var lost = 0;
        w.EventsLost += (_, _) => Interlocked.Increment(ref lost);
        w.Start(tmp.Downloads);

        w.OnWatcherError(new InternalBufferOverflowException());
        w.Stop();

        await Task.Delay(1200);
        Assert.Equal(0, Volatile.Read(ref lost));
        Assert.False(w.IsRunning);
    }

    private sealed record Setup(OperationsCoordinator Coordinator, FileLibrary Library, AppStatus Status) : IDisposable
    {
        public HistoryService? History { get; init; }
        public void Dispose() => History?.Dispose();
    }

    private static Setup Create(TempDir tmp)
    {
        var settings = Fx.Settings(tmp);
        settings.Current.DownloadsPath = tmp.Downloads;
        var detector = Fx.Detector(settings);
        var library = new FileLibrary(settings);
        var status = new AppStatus();
        var history = new HistoryService(Path.Combine(tmp.Path, "history.json"));
        var coordinator = new OperationsCoordinator(settings, library, new FileScannerService(detector),
            new FileOrganizationService(), new FileDeletionService(new FakeRecycleBin(Path.Combine(tmp.Path, "bin"))),
            new FileWatcherService(detector), history, new DialogService(), new ToastService(settings), status,
            NullLogService.Instance, new NoFolderPicker());
        return new Setup(coordinator, library, status) { History = history };
    }

    [Fact]
    public async Task Resync_adds_new_files_removes_gone_ones_and_keeps_duplicate_groups()
    {
        using var tmp = new TempDir();
        var a = tmp.File("a.txt", "same");
        var b = tmp.File("b.txt", "same");
        var c = tmp.File("c.txt", "other");
        using var s = Create(tmp);
        await s.Coordinator.ScanAsync();
        Assert.Equal(3, s.Library.Files.Count);

        FileEntry Get(string p) => s.Library.Files.Single(f => f.FullPath == p);
        s.Library.SetDuplicates(new[] { new DuplicateGroup("h", 4, new[] { Get(a), Get(b) }) });

        // Уведомления потеряны: c удалён, d появился - библиотека об этом не знает.
        File.Delete(c);
        var d = tmp.File("sub\\d.txt", "new");

        await s.Coordinator.ResyncAsync();

        var paths = s.Library.Files.Select(f => f.FullPath).OrderBy(p => p).ToList();
        Assert.Equal(new[] { a, b, d }.OrderBy(p => p), paths);
        Assert.Single(s.Library.Duplicates);        // группы дубликатов не сброшены, как при полном сканировании
    }

    [Fact]
    public async Task Resync_waits_until_the_current_operation_finishes()
    {
        using var tmp = new TempDir();
        tmp.File("a.txt");
        using var s = Create(tmp);
        await s.Coordinator.ScanAsync();

        s.Status.Begin(AppState.Sorting, "sorting");
        var d = tmp.File("d.txt");
        var resync = s.Coordinator.ResyncAsync();

        await Task.Delay(400);
        Assert.False(resync.IsCompleted);
        Assert.False(s.Library.Contains(d));

        s.Status.Finish(AppState.SortComplete, "done");
        Assert.Same(resync, await Task.WhenAny(resync, Task.Delay(5000)));
        Assert.True(s.Library.Contains(d));
    }
}
