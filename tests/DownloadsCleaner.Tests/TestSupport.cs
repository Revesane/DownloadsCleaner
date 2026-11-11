using DownloadsCleaner.Models;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.Tests;

/// <summary>Временная папка для теста. Тесты никогда не трогают реальную папку «Загрузки».</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DC_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>Папка «Загрузки» внутри временной директории.</summary>
    public string Downloads
    {
        get
        {
            var d = System.IO.Path.Combine(Path, "Загрузки");
            Directory.CreateDirectory(d);
            return d;
        }
    }

    public string File(string relative, string content = "content", DateTime? created = null)
    {
        var full = System.IO.Path.Combine(Downloads, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        if (created != null)
        {
            System.IO.File.SetCreationTime(full, created.Value);
            System.IO.File.SetLastWriteTime(full, created.Value);
        }
        return full;
    }

    public string Bytes(string relative, byte[] data)
    {
        var full = System.IO.Path.Combine(Downloads, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllBytes(full, data);
        return full;
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                System.IO.File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Path, true);
        }
        catch { /* временная папка не критична */ }
    }
}

public static class Fx
{
    public static SettingsService Settings(TempDir tmp) =>
        new(System.IO.Path.Combine(tmp.Path, "config", "settings.json"));

    public static FileTypeDetectionService Detector(ISettingsService? settings = null, TempDir? tmp = null) =>
        new(settings ?? new SettingsService(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DC_unused_" + Guid.NewGuid().ToString("N") + ".json")));

    public static async Task<IReadOnlyList<FileEntry>> ScanAsync(string root, IFileTypeDetectionService detector,
        bool includeSubfolders = true, string[]? excludedFolders = null, string[]? excludedExt = null)
    {
        var scanner = new FileScannerService(detector);
        var result = await scanner.ScanAsync(root,
            new ScanOptions(includeSubfolders, excludedFolders ?? Array.Empty<string>(), excludedExt ?? Array.Empty<string>()),
            null, CancellationToken.None);
        return result.Files;
    }

    public static SortOptions SortOpts(string root, string? dest = null, bool includeSubfolders = false, params string[] excluded) =>
        new(root, dest ?? root, includeSubfolders, excluded);

    /// <summary>Ждёт условие с таймаутом (для асинхронных событий).</summary>
    public static async Task<bool> WaitAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }
}

/// <summary>Фейковая квота Корзины: задаётся в байтах.</summary>
public sealed class FakeRecycleBinQuota : IRecycleBinQuota
{
    private readonly long _bytes;

    public FakeRecycleBinQuota(long bytes) => _bytes = bytes;

    /// <summary>Имитирует отключённую для тома Корзину (NukeOnDelete).</summary>
    public bool Disabled { get; init; }

    public long GetQuotaBytes(string path) => _bytes;
    public bool IsRecycleBinDisabled(string path) => Disabled;
}

/// <summary>Корзина-имитация: перемещает файл в отдельную папку, чтобы не засорять настоящую Корзину.</summary>
public sealed class FakeRecycleBin : IRecycleBin
{
    private readonly string _bin;
    public List<string> Calls { get; } = new();
    public Func<string, Exception?>? Failure { get; set; }

    public FakeRecycleBin(string binFolder)
    {
        _bin = binFolder;
        Directory.CreateDirectory(_bin);
    }

    public void MoveToRecycleBin(string path)
    {
        Calls.Add(path);
        var failure = Failure?.Invoke(path);
        if (failure != null) throw failure;
        File.Move(path, System.IO.Path.Combine(_bin, Guid.NewGuid().ToString("N") + "_" + System.IO.Path.GetFileName(path)));
    }

    public int Count => Directory.GetFiles(_bin).Length;
}
