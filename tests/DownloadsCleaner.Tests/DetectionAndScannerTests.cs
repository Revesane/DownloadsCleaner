using System.ComponentModel;
using System.Diagnostics;
using DownloadsCleaner.Models;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.Tests;

public class FileTypeDetectionTests
{
    private readonly FileTypeDetectionService _d = Fx.Detector();

    [Theory]
    [InlineData("mp3 wav flac aac m4a ogg opus wma aiff alac", FileCategory.Audio)]
    [InlineData("mp4 mkv avi mov wmv flv webm m4v mpeg mpg ts", FileCategory.Video)]
    [InlineData("jpg jpeg png webp gif bmp tiff svg ico heic avif", FileCategory.Images)]
    [InlineData("pdf doc docx xls xlsx ppt pptx txt rtf odt csv md", FileCategory.Documents)]
    [InlineData("zip rar 7z tar gz bz2 xz iso", FileCategory.Archives)]
    [InlineData("exe msi msix appx bat cmd ps1", FileCategory.Programs)]
    [InlineData("blend fbx obj stl glb gltf 3ds dae", FileCategory.Models3D)]
    public void Known_extensions_map_to_their_category(string extensions, FileCategory expected)
    {
        foreach (var ext in extensions.Split(' '))
        {
            Assert.Equal(expected, _d.Detect("file." + ext));
            Assert.Equal(expected, _d.Detect(ext));            // само расширение
            Assert.Equal(expected, _d.Detect("." + ext));
            Assert.Equal(expected, _d.Detect("FILE." + ext.ToUpperInvariant()));   // регистр не важен
        }
    }

    [Theory]
    [InlineData("data.xyz")]
    [InlineData("README")]
    [InlineData("noextension.")]
    [InlineData(".gitignore")]
    [InlineData("weird.тест")]
    [InlineData("")]
    public void Unknown_extensions_go_to_Other(string name)
    {
        Assert.Equal(FileCategory.Other, _d.Detect(name));
    }

    [Fact]
    public void Only_the_last_extension_matters()
    {
        Assert.Equal(FileCategory.Archives, _d.Detect("backup.tar.gz"));
        Assert.Equal(FileCategory.Other, _d.Detect("movie.mp4.part"));
        Assert.Equal(FileCategory.Programs, _d.Detect("mp3.exe"));
    }

    [Fact]
    public void Detection_never_opens_the_file()
    {
        // Файла не существует: определение работает только по имени/расширению.
        Assert.Equal(FileCategory.Audio, _d.Detect(@"C:\no\such\folder\Песня.MP3"));
    }

    [Fact]
    public void Temporary_download_extensions_are_flagged()
    {
        foreach (var n in new[] { "a.crdownload", "b.part", "c.tmp", "d.download", "e.opdownload" })
            Assert.True(_d.IsTemporary(n), n);
        Assert.False(_d.IsTemporary("a.mp4"));
    }

    [Fact]
    public void Custom_rules_extend_and_override_builtin_list()
    {
        using var tmp = new TempDir();
        var settings = Fx.Settings(tmp);
        var d = new FileTypeDetectionService(settings);

        Assert.Equal(FileCategory.Other, d.Detect("x.dwg"));
        settings.Current.CustomRules.Add(new ExtensionRule(".DWG", FileCategory.Documents));
        settings.Current.CustomRules.Add(new ExtensionRule("mp3", FileCategory.Documents));   // переопределение

        Assert.Equal(FileCategory.Documents, d.Detect("plan.dwg"));
        Assert.Equal(FileCategory.Documents, d.Detect("song.mp3"));
        Assert.Equal(FileCategory.Audio, d.Detect("song.flac"));
        settings.Dispose();
    }
}

public class FileScannerTests
{
    [Fact]
    public async Task Scans_recursively_with_categories_sizes_and_unicode_names()
    {
        using var tmp = new TempDir();
        tmp.File("Отчёт 2024.pdf", "1234567890");
        tmp.File("музыка\\Песня №1.mp3", "abc");
        tmp.File("sub\\deep\\photo.JPG", "xyz");
        tmp.File("strange.qqq", "");

        var files = await Fx.ScanAsync(tmp.Downloads, Fx.Detector());

        Assert.Equal(4, files.Count);
        var pdf = Assert.Single(files, f => f.Name == "Отчёт 2024.pdf");
        Assert.Equal(FileCategory.Documents, pdf.Category);
        Assert.Equal(10, pdf.Size);
        Assert.Equal("pdf", pdf.Extension);
        Assert.Equal(FileCategory.Audio, files.Single(f => f.Name == "Песня №1.mp3").Category);
        Assert.Equal(FileCategory.Images, files.Single(f => f.Name == "photo.JPG").Category);
        Assert.Equal(FileCategory.Other, files.Single(f => f.Name == "strange.qqq").Category);
        Assert.All(files, f => Assert.True(File.Exists(f.FullPath)));
    }

    [Fact]
    public async Task Respects_subfolder_folder_and_extension_exclusions()
    {
        using var tmp = new TempDir();
        tmp.File("keep.txt");
        tmp.File("skipme.iso");
        tmp.File("inner\\a.txt");
        tmp.File("secret\\hidden.txt");
        tmp.File("secret\\nested\\hidden2.txt");

        var rootOnly = await Fx.ScanAsync(tmp.Downloads, Fx.Detector(), includeSubfolders: false);
        Assert.Equal(new[] { "keep.txt", "skipme.iso" }, rootOnly.Select(f => f.Name).OrderBy(n => n));

        var filtered = await Fx.ScanAsync(tmp.Downloads, Fx.Detector(),
            excludedFolders: new[] { Path.Combine(tmp.Downloads, "secret") }, excludedExt: new[] { ".ISO" });
        Assert.Equal(new[] { "a.txt", "keep.txt" }, filtered.Select(f => f.Name).OrderBy(n => n));
    }

    [Fact]
    public async Task Skips_system_files_and_marks_temporary_downloads()
    {
        using var tmp = new TempDir();
        var ini = tmp.File("desktop.ini");
        File.SetAttributes(ini, FileAttributes.Hidden | FileAttributes.System);
        tmp.File("movie.mp4.crdownload", "partial");

        var files = await Fx.ScanAsync(tmp.Downloads, Fx.Detector());

        var f = Assert.Single(files);
        Assert.True(f.IsTemporary);
        Assert.Equal(FileCategory.Other, f.Category);
    }

    [Fact]
    public async Task Missing_folder_returns_empty_result()
    {
        var scanner = new FileScannerService(Fx.Detector());
        var r = await scanner.ScanAsync(Path.Combine(Path.GetTempPath(), "dc_missing_" + Guid.NewGuid().ToString("N")),
            new ScanOptions(true, Array.Empty<string>(), Array.Empty<string>()), null, CancellationToken.None);
        Assert.Empty(r.Files);
    }

    [Fact]
    public async Task Scan_can_be_cancelled_before_start_and_midway()
    {
        using var tmp = new TempDir();
        for (var i = 0; i < 2500; i++) tmp.File($"d{i % 25}\\f{i}.txt", "x");

        var scanner = new FileScannerService(Fx.Detector());
        var opts = new ScanOptions(true, Array.Empty<string>(), Array.Empty<string>());

        using var pre = new CancellationTokenSource();
        pre.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.ScanAsync(tmp.Downloads, opts, null, pre.Token));

        using var mid = new CancellationTokenSource();
        var progress = new SyncProgress<ScanProgress>(p => { if (p.FilesFound > 0) mid.Cancel(); });
        // Принудительно часто сообщаем о прогрессе: первый отчёт приходит после 80 мс или в конце, поэтому отменяем по таймеру.
        mid.CancelAfter(1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            for (var i = 0; i < 50; i++) await scanner.ScanAsync(tmp.Downloads, opts, progress, mid.Token);
        });
    }

    [Fact]
    public async Task Progress_is_reported()
    {
        using var tmp = new TempDir();
        for (var i = 0; i < 20; i++) tmp.File($"f{i}.txt", "x");
        var reports = new List<ScanProgress>();
        var scanner = new FileScannerService(Fx.Detector());
        await scanner.ScanAsync(tmp.Downloads, new ScanOptions(true, Array.Empty<string>(), Array.Empty<string>()),
            new SyncProgress<ScanProgress>(reports.Add), CancellationToken.None);

        Assert.NotEmpty(reports);
        Assert.Equal(20, reports[^1].FilesFound);
    }

    [Fact]
    public async Task Reparse_point_files_are_not_skipped()
    {
        // Облачные «placeholder»-файлы (OneDrive/Dropbox) — это reparse points.
        // Раньше сканер пропускал их целиком (AttributesToSkip = ReparsePoint),
        // и папка «Загрузки» в облаке выглядела пустой.
        using var tmp = new TempDir();
        var real = tmp.File("real.txt", "data");

        string link;
        try
        {
            link = Path.Combine(tmp.Downloads, "link.txt");
            File.CreateSymbolicLink(link, real);
        }
        catch (UnauthorizedAccessException)
        {
            return;    // символические ссылки недоступны без прав разработчика — проверять нечего
        }
        catch (IOException)
        {
            return;
        }

        var files = await Fx.ScanAsync(tmp.Downloads, Fx.Detector());

        Assert.Contains(files, f => f.Name == "real.txt");
        Assert.Contains(files, f => f.Name == "link.txt");
    }

    [Fact]
    public async Task Junction_directories_are_not_followed()
    {
        // Junction/symlink-каталоги по-прежнему не обходятся: иначе петли и выход за папку.
        using var tmp = new TempDir();
        var outside = Path.Combine(tmp.Path, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "hidden.txt"), "x");

        var junction = Path.Combine(tmp.Downloads, "junction");
        try
        {
            var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{outside}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p == null) return;
            p.WaitForExit(10_000);
            if (!Directory.Exists(junction)) return;    // mklink не сработал — тест неприменим
        }
        catch (Win32Exception) { return; }

        try
        {
            var files = await Fx.ScanAsync(tmp.Downloads, Fx.Detector());
            Assert.DoesNotContain(files, f => f.Name == "hidden.txt");
        }
        finally
        {
            // Убираем ссылку, чтобы рекурсивное удаление temp-папки не ушло за её пределы.
            try { Directory.Delete(junction, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void TryCreateEntry_returns_null_for_missing_file()
    {
        var scanner = new FileScannerService(Fx.Detector());
        Assert.Null(scanner.TryCreateEntry(Path.Combine(Path.GetTempPath(), "nope_" + Guid.NewGuid().ToString("N") + ".txt")));
    }
}

/// <summary>IProgress без SynchronizationContext: вызывает обработчик синхронно.</summary>
public sealed class SyncProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;
    public SyncProgress(Action<T> handler) => _handler = handler;
    public void Report(T value) => _handler(value);
}
