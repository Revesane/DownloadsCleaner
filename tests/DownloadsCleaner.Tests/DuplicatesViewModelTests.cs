using DownloadsCleaner.Models;
using DownloadsCleaner.Services;
using DownloadsCleaner.ViewModels;

namespace DownloadsCleaner.Tests;

/// <summary>Страница дубликатов никогда не должна отправлять на удаление все копии файла.</summary>
public class DuplicatesViewModelTests
{
    private sealed class RecordingOperations : IOperationsCoordinator
    {
        public List<IReadOnlyList<FileEntry>> Deletes { get; } = new();

        public event Action<IReadOnlyCollection<string>>? FilesLeaving { add { } remove { } }
        public event Action? ReleaseFileHandles { add { } remove { } }
        public event Action<FileEntry>? NewFileArrived { add { } remove { } }

        public Task ScanAsync() => Task.CompletedTask;
        public Task SortAsync(IReadOnlyList<FileEntry>? subset = null) => Task.CompletedTask;
        public Task UndoAsync(HistoryEntry entry) => Task.CompletedTask;
        public void ApplyWatcherSettings() { }

        public Task DeleteAsync(IReadOnlyList<FileEntry> files)
        {
            Deletes.Add(files);
            return Task.CompletedTask;
        }
    }

    private sealed record Setup(DuplicatesViewModel Vm, FileLibrary Library, RecordingOperations Operations,
        FileEntry A, FileEntry B, FileEntry C) : IDisposable
    {
        public void Dispose() => Vm.Dispose();
    }

    /// <summary>Три одинаковых файла: A самый старый, затем B, затем C.</summary>
    private static async Task<Setup> CreateAsync(TempDir tmp)
    {
        var content = new byte[2048];
        new Random(7).NextBytes(content);
        var now = DateTime.Now;
        foreach (var (name, age) in new[] { ("a.bin", 30), ("b.bin", 20), ("c.bin", 10) })
        {
            var p = tmp.Bytes(name, content);
            File.SetCreationTime(p, now.AddDays(-age));
        }

        var settings = Fx.Settings(tmp);
        var entries = await Fx.ScanAsync(tmp.Downloads, Fx.Detector(settings));
        FileEntry Get(string n) => entries.Single(e => e.Name == n);

        var library = new FileLibrary(settings);
        library.SetFiles(entries);
        library.SetDuplicates(new[]
        {
            new DuplicateGroup("hash", content.Length, new[] { Get("a.bin"), Get("b.bin"), Get("c.bin") })
        });

        var operations = new RecordingOperations();
        var vm = new DuplicatesViewModel(library, new DuplicateDetectionService(), settings, new DialogService(),
            operations, new AppStatus(), new ToastService(settings), NullLogService.Instance);
        vm.AutoSelectCommand.Execute(null);       // стратегия по умолчанию - оставить самую старую (A)
        return new Setup(vm, library, operations, Get("a.bin"), Get("b.bin"), Get("c.bin"));
    }

    private static IReadOnlyList<DuplicateFileViewModel> Files(DuplicatesViewModel vm) => Assert.Single(vm.Groups).Files;

    [Fact]
    public async Task Auto_selection_keeps_one_copy()
    {
        using var tmp = new TempDir();
        using var s = await CreateAsync(tmp);

        var files = Files(s.Vm);
        Assert.False(files.Single(f => f.FullPath == s.A.FullPath).IsMarked);
        Assert.Equal(2, files.Count(f => f.IsMarked));
    }

    [Fact]
    public async Task When_the_kept_copy_disappears_one_of_the_rest_becomes_kept()
    {
        using var tmp = new TempDir();
        using var s = await CreateAsync(tmp);

        // Оставленную копию удалили вне программы: в группе остались только отмеченные B и C.
        File.Delete(s.A.FullPath);
        s.Library.Remove(new[] { s.A.FullPath });
        s.Vm.Rebuild(keepMarks: true);

        var files = Files(s.Vm);
        Assert.Equal(2, files.Count);
        Assert.False(files.Single(f => f.FullPath == s.B.FullPath).IsMarked);   // B теперь самая старая
        Assert.True(files.Single(f => f.FullPath == s.C.FullPath).IsMarked);
    }

    [Fact]
    public async Task Delete_skips_group_whose_kept_copy_is_gone_from_disk()
    {
        using var tmp = new TempDir();
        using var s = await CreateAsync(tmp);

        // Файл исчез с диска, но список ещё не успел обновиться.
        File.Delete(s.A.FullPath);
        await s.Vm.DeleteMarkedCommand.ExecuteAsync(null);

        Assert.Empty(s.Operations.Deletes);
        Assert.True(File.Exists(s.B.FullPath));
        Assert.True(File.Exists(s.C.FullPath));
    }

    [Fact]
    public async Task Delete_sends_only_marked_copies_when_kept_copy_exists()
    {
        using var tmp = new TempDir();
        using var s = await CreateAsync(tmp);

        await s.Vm.DeleteMarkedCommand.ExecuteAsync(null);

        var sent = Assert.Single(s.Operations.Deletes);
        Assert.Equal(new[] { s.B.FullPath, s.C.FullPath }.OrderBy(p => p), sent.Select(f => f.FullPath).OrderBy(p => p));
    }
}
