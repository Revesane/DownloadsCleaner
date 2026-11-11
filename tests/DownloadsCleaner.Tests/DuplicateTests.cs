using System.Security.Cryptography;
using DownloadsCleaner.Models;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.Tests;

public class DuplicateDetectionTests
{
    private static byte[] Data(int size, int seed)
    {
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        return b;
    }

    [Fact]
    public async Task Finds_identical_files_by_sha256_even_with_different_names_and_folders()
    {
        using var tmp = new TempDir();
        var content = Data(3000, 1);
        tmp.Bytes("a.bin", content);
        tmp.Bytes("копия (2).bin", content);
        tmp.Bytes("sub\\c.dat", content);
        tmp.Bytes("unique.bin", Data(3000, 2));

        var files = await Fx.ScanAsync(tmp.Downloads, Fx.Detector());
        var groups = await new DuplicateDetectionService().FindAsync(files, null, CancellationToken.None);

        var g = Assert.Single(groups);
        Assert.Equal(3, g.Files.Count);
        Assert.Equal(3000, g.Size);
        Assert.Equal(6000, g.WastedBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(content)), g.Hash);
        Assert.DoesNotContain(g.Files, f => f.Name == "unique.bin");
    }

    [Fact]
    public async Task Same_size_or_same_name_alone_is_not_a_duplicate()
    {
        using var tmp = new TempDir();
        tmp.Bytes("one\\same-name.bin", Data(2048, 10));
        tmp.Bytes("two\\same-name.bin", Data(2048, 11));    // то же имя и размер, но другое содержимое
        tmp.Bytes("same-size-1.bin", Data(512, 20));
        tmp.Bytes("same-size-2.bin", Data(512, 21));
        tmp.Bytes("different-size.bin", Data(513, 20));

        var files = await Fx.ScanAsync(tmp.Downloads, Fx.Detector());
        var groups = await new DuplicateDetectionService().FindAsync(files, null, CancellationToken.None);

        Assert.Empty(groups);
    }

    [Fact]
    public async Task Files_equal_only_after_the_first_64kb_are_still_distinguished()
    {
        using var tmp = new TempDir();
        var a = Data(200_000, 5);
        var b = (byte[])a.Clone();
        b[^1] ^= 0xFF;                      // отличие только в самом конце файла
        tmp.Bytes("x.bin", a);
        tmp.Bytes("y.bin", b);
        tmp.Bytes("z.bin", a);

        var files = await Fx.ScanAsync(tmp.Downloads, Fx.Detector());
        var groups = await new DuplicateDetectionService().FindAsync(files, null, CancellationToken.None);

        var g = Assert.Single(groups);
        Assert.Equal(new[] { "x.bin", "z.bin" }, g.Files.Select(f => f.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task Empty_and_temporary_files_are_ignored()
    {
        using var tmp = new TempDir();
        tmp.Bytes("e1.bin", Array.Empty<byte>());
        tmp.Bytes("e2.bin", Array.Empty<byte>());
        tmp.Bytes("a.part", Data(100, 1));
        tmp.Bytes("b.part", Data(100, 1));

        var files = await Fx.ScanAsync(tmp.Downloads, Fx.Detector());
        Assert.Empty(await new DuplicateDetectionService().FindAsync(files, null, CancellationToken.None));
    }

    [Fact]
    public async Task Hashes_are_cached_and_recomputed_only_when_the_file_changes()
    {
        using var tmp = new TempDir();
        var content = Data(5000, 7);
        var p1 = tmp.Bytes("1.bin", content);
        tmp.Bytes("2.bin", content);

        var service = new DuplicateDetectionService();
        var files = await Fx.ScanAsync(tmp.Downloads, Fx.Detector());
        await service.FindAsync(files, null, CancellationToken.None);
        var cached = service.CachedHashCount;
        Assert.Equal(2, cached);

        await service.FindAsync(files, null, CancellationToken.None);      // повторный поиск
        Assert.Equal(cached, service.CachedHashCount);

        // Файл изменился -> старый хеш не используется, дубликата больше нет.
        File.WriteAllBytes(p1, Data(5000, 8));
        var files2 = await Fx.ScanAsync(tmp.Downloads, Fx.Detector());
        var groups = await service.FindAsync(files2, null, CancellationToken.None);
        Assert.Empty(groups);
    }

    [Fact]
    public async Task Groups_are_ordered_by_wasted_space_and_progress_is_reported()
    {
        using var tmp = new TempDir();
        var small = Data(1000, 1);
        var big = Data(50_000, 2);
        tmp.Bytes("s1.bin", small); tmp.Bytes("s2.bin", small);
        tmp.Bytes("b1.bin", big); tmp.Bytes("b2.bin", big);

        var reports = new List<DuplicateProgress>();
        var files = await Fx.ScanAsync(tmp.Downloads, Fx.Detector());
        var groups = await new DuplicateDetectionService().FindAsync(files, new SyncProgress<DuplicateProgress>(reports.Add), CancellationToken.None);

        Assert.Equal(2, groups.Count);
        Assert.Equal(50_000, groups[0].Size);
        Assert.NotEmpty(reports);
        Assert.Equal(1.0, reports[^1].Fraction, 3);
    }

    [Fact]
    public async Task Search_can_be_cancelled()
    {
        using var tmp = new TempDir();
        var content = Data(1_000_000, 3);
        for (var i = 0; i < 6; i++) tmp.Bytes($"f{i}.bin", content);
        var files = await Fx.ScanAsync(tmp.Downloads, Fx.Detector());

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DuplicateDetectionService().FindAsync(files, null, cts.Token));
    }

    [Fact]
    public async Task Unreadable_file_is_skipped_instead_of_failing_the_search()
    {
        using var tmp = new TempDir();
        var content = Data(4000, 9);
        tmp.Bytes("a.bin", content);
        tmp.Bytes("b.bin", content);
        var locked = tmp.Bytes("c.bin", content);
        var files = await Fx.ScanAsync(tmp.Downloads, Fx.Detector());

        // Эксклюзивная блокировка файла другим процессом (FileShare.None) - читать его нельзя.
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var groups = await new DuplicateDetectionService().FindAsync(files, null, CancellationToken.None);

        var g = Assert.Single(groups);
        Assert.Equal(2, g.Files.Count);
    }
}
