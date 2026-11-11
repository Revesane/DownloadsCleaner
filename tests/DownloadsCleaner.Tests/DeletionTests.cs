using DownloadsCleaner.Helpers;
using DownloadsCleaner.Models;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.Tests;

public class FileDeletionTests
{
    private static async Task<IReadOnlyList<FileEntry>> Scan(TempDir tmp) =>
        await Fx.ScanAsync(tmp.Downloads, Fx.Detector());

    [Fact]
    public async Task Recycle_mode_sends_files_to_the_bin_and_never_deletes_permanently()
    {
        using var tmp = new TempDir();
        tmp.File("a.txt", "12345");
        tmp.File("b.txt", "123");
        var bin = new FakeRecycleBin(Path.Combine(tmp.Path, "bin"));
        var files = await Scan(tmp);

        var result = await new FileDeletionService(bin).DeleteAsync(files, DeleteMode.Recycle, tmp.Downloads, null, CancellationToken.None);

        Assert.Equal(2, result.Deleted.Count);
        Assert.Equal(8, result.BytesFreed);
        Assert.Equal(2, bin.Count);                       // файлы лежат в «корзине», а не удалены
        Assert.Empty(Directory.GetFiles(tmp.Downloads));
    }

    [Fact]
    public async Task If_the_recycle_bin_fails_the_file_is_kept_not_permanently_deleted()
    {
        using var tmp = new TempDir();
        var p = tmp.File("important.docx", "DATA");
        var bin = new FakeRecycleBin(Path.Combine(tmp.Path, "bin")) { Failure = _ => new IOException("bin unavailable") };
        var files = await Scan(tmp);

        var result = await new FileDeletionService(bin).DeleteAsync(files, DeleteMode.Recycle, tmp.Downloads, null, CancellationToken.None);

        Assert.Empty(result.Deleted);
        Assert.Single(result.Failed);
        Assert.Equal("DATA", File.ReadAllText(p));       // безопасный откат: файл на месте
    }

    [Fact]
    public async Task Permanent_mode_deletes_including_read_only_files()
    {
        using var tmp = new TempDir();
        var ro = tmp.File("readonly.txt", "x");
        File.SetAttributes(ro, FileAttributes.ReadOnly);
        var bin = new FakeRecycleBin(Path.Combine(tmp.Path, "bin"));
        var files = await Scan(tmp);

        var result = await new FileDeletionService(bin).DeleteAsync(files, DeleteMode.Permanent, tmp.Downloads, null, CancellationToken.None);

        Assert.Single(result.Deleted);
        Assert.False(File.Exists(ro));
        Assert.Empty(bin.Calls);                          // Корзина не использовалась
    }

    [Fact]
    public async Task Files_outside_the_downloads_folder_are_never_deleted()
    {
        using var tmp = new TempDir();
        var outside = Path.Combine(tmp.Path, "Documents");
        Directory.CreateDirectory(outside);
        var victim = Path.Combine(outside, "precious.docx");
        File.WriteAllText(victim, "PRECIOUS");
        var entry = new FileEntry(victim, "precious.docx", "docx", 8, DateTime.Now, DateTime.Now, FileCategory.Documents, false);
        var bin = new FakeRecycleBin(Path.Combine(tmp.Path, "bin"));

        foreach (var mode in new[] { DeleteMode.Recycle, DeleteMode.Permanent })
        {
            var result = await new FileDeletionService(bin).DeleteAsync(new[] { entry }, mode, tmp.Downloads, null, CancellationToken.None);
            Assert.Empty(result.Deleted);
            Assert.Single(result.Failed);
        }
        Assert.Equal("PRECIOUS", File.ReadAllText(victim));
    }

    [Fact]
    public async Task Path_traversal_cannot_escape_the_allowed_root()
    {
        using var tmp = new TempDir();
        var victim = Path.Combine(tmp.Path, "outside.txt");
        File.WriteAllText(victim, "X");
        var sneaky = Path.Combine(tmp.Downloads, "..", "outside.txt");
        var entry = new FileEntry(sneaky, "outside.txt", "txt", 1, DateTime.Now, DateTime.Now, FileCategory.Documents, false);

        var result = await new FileDeletionService(new FakeRecycleBin(Path.Combine(tmp.Path, "bin")))
            .DeleteAsync(new[] { entry }, DeleteMode.Permanent, tmp.Downloads, null, CancellationToken.None);

        Assert.Empty(result.Deleted);
        Assert.True(File.Exists(victim));
    }

    [Fact]
    public async Task Missing_and_locked_files_are_reported_and_do_not_stop_the_rest()
    {
        using var tmp = new TempDir();
        var locked = tmp.File("locked.txt", "L");
        tmp.File("ok.txt", "O");
        var gone = tmp.File("gone.txt", "G");
        var files = await Scan(tmp);
        File.Delete(gone);

        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = await new FileDeletionService(new FakeRecycleBin(Path.Combine(tmp.Path, "bin")) { Failure = p => p == locked ? new IOException("in use") : null })
            .DeleteAsync(files, DeleteMode.Recycle, tmp.Downloads, null, CancellationToken.None);

        Assert.Equal(new[] { "ok.txt" }, result.Deleted.Select(f => f.Name));
        Assert.Equal(2, result.Failed.Count);
    }

    [Fact]
    public async Task Deletion_can_be_cancelled()
    {
        using var tmp = new TempDir();
        for (var i = 0; i < 20; i++) tmp.File($"f{i:00}.txt");
        var files = await Scan(tmp);
        var bin = new FakeRecycleBin(Path.Combine(tmp.Path, "bin"));

        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<OperationProgress>(p => { if (p.Done >= 5) cts.Cancel(); });
        var result = await new FileDeletionService(bin).DeleteAsync(files, DeleteMode.Recycle, tmp.Downloads, progress, cts.Token);

        Assert.True(result.Cancelled);
        Assert.InRange(result.Deleted.Count, 5, 19);
        Assert.Equal(20, Directory.GetFiles(tmp.Downloads).Length + bin.Count);       // ничего не потеряно
    }

    [Fact]
    public async Task File_that_does_not_fit_the_recycle_bin_quota_is_kept_and_reported()
    {
        using var tmp = new TempDir();
        var p = tmp.File("huge.iso", "DATA");
        var bin = new FakeRecycleBin(Path.Combine(tmp.Path, "bin"))
        {
            Failure = path => new RecycleBinOverflowException(path, 1024)
        };
        var files = await Scan(tmp);

        var result = await new FileDeletionService(bin).DeleteAsync(files, DeleteMode.Recycle, tmp.Downloads, null, CancellationToken.None);

        // Файл остался на месте - он не был уничтожен безвозвратно.
        Assert.Empty(result.Deleted);
        Assert.Single(result.Failed);
        Assert.Single(result.NotRecycled);
        Assert.Equal("DATA", File.ReadAllText(p));
        Assert.Equal(0, result.BytesFreed);
    }

    [Fact]
    public void Real_recycle_bin_refuses_a_file_that_exceeds_the_quota()
    {
        using var tmp = new TempDir();
        var p = tmp.File("too-big-for-bin.bin", "content");

        // Квота 1 байт - файл в неё заведомо не помещается.
        var bin = new WindowsRecycleBin(new FakeRecycleBinQuota(1));

        Assert.Throws<RecycleBinOverflowException>(() => bin.MoveToRecycleBin(p));
        Assert.True(File.Exists(p));          // файл остался на месте
    }

    [Fact]
    public void Real_recycle_bin_refuses_when_it_is_disabled_for_the_volume()
    {
        using var tmp = new TempDir();
        var p = tmp.File("keep-me.bin", "content");

        var bin = new WindowsRecycleBin(new FakeRecycleBinQuota(0) { Disabled = true });

        var ex = Assert.Throws<RecycleBinOverflowException>(() => bin.MoveToRecycleBin(p));
        Assert.Contains("отключена", ex.Message);
        Assert.True(File.Exists(p));          // безвозвратного удаления не произошло
    }

    [Fact]
    public void Recycle_bin_quota_is_read_in_bytes_and_is_plausible()
    {
        var quota = new WindowsRecycleBinQuota();
        var bytes = quota.GetQuotaBytes(Path.GetTempPath());

        Assert.True(bytes >= 0);
        if (bytes > 0)                        // значение из реестра не должно быть «процентами»
            Assert.True(bytes < new DriveInfo(Path.GetPathRoot(Path.GetTempPath())!).TotalSize);
    }

    [Fact]
    public async Task Real_windows_recycle_bin_removes_the_file_from_disk()
    {
        using var tmp = new TempDir();
        var p = tmp.File("to-recycle-" + Guid.NewGuid().ToString("N") + ".txt", "temp test file");
        var files = await Scan(tmp);

        var result = await new FileDeletionService(new WindowsRecycleBin())
            .DeleteAsync(files, DeleteMode.Recycle, tmp.Downloads, null, CancellationToken.None);

        Assert.Single(result.Deleted);
        Assert.False(File.Exists(p));
    }
}

public class HelperTests
{
    [Fact]
    public void PathHelper_IsInside_is_strict_about_boundaries()
    {
        Assert.True(PathHelper.IsInside(@"C:\Users\me\Downloads\a.txt", @"C:\Users\me\Downloads"));
        Assert.True(PathHelper.IsInside(@"C:\Users\me\Downloads\sub\a.txt", @"C:\Users\me\Downloads\"));
        Assert.False(PathHelper.IsInside(@"C:\Users\me\Downloads2\a.txt", @"C:\Users\me\Downloads"));
        Assert.False(PathHelper.IsInside(@"C:\Users\me\Downloads", @"C:\Users\me\Downloads"));
        Assert.False(PathHelper.IsInside(@"C:\Users\me\Downloads\..\secret.txt", @"C:\Users\me\Downloads"));
        Assert.True(PathHelper.IsInside(@"c:\users\ME\downloads\A.TXT", @"C:\Users\me\Downloads"));
    }

    [Fact]
    public void GetUniquePath_adds_numbers_without_touching_existing_files()
    {
        using var tmp = new TempDir();
        var a = tmp.File("x.txt");
        tmp.File("x (1).txt");
        Assert.Equal(Path.Combine(tmp.Downloads, "x (2).txt"), PathHelper.GetUniquePath(a));
        Assert.Equal(Path.Combine(tmp.Downloads, "fresh.txt"), PathHelper.GetUniquePath(Path.Combine(tmp.Downloads, "fresh.txt")));
    }

    [Theory]
    [InlineData(0, "0 Б")]
    [InlineData(1023, "1023 Б")]
    [InlineData(1024, "1 КБ")]
    [InlineData(1536, "1,5 КБ")]
    public void ByteFormatter_formats_sizes(long bytes, string expected)
    {
        var old = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ru-RU");
        try { Assert.Equal(expected, ByteFormatter.Format(bytes)); }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }

    [Theory]
    [InlineData(1, "файл")]
    [InlineData(2, "файла")]
    [InlineData(5, "файлов")]
    [InlineData(11, "файлов")]
    [InlineData(21, "файл")]
    [InlineData(104, "файла")]
    public void Russian_plurals(int n, string word) =>
        Assert.Equal(word, Localization.Loc.Plural(n, "FileOne", "FileFew", "FileMany"));
}
