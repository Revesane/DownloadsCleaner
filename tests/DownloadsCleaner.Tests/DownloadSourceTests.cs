using DownloadsCleaner.Models;
using DownloadsCleaner.Services;
using DownloadsCleaner.ViewModels;

namespace DownloadsCleaner.Tests;

/// <summary>Источник загрузки из метки Zone.Identifier: разбор, чтение, поиск и аналитика.</summary>
public class DownloadSourceTests
{
    private static string Zone(string? referrer = null, string? host = null) =>
        "[ZoneTransfer]\r\nZoneId=3\r\n" +
        (referrer != null ? $"ReferrerUrl={referrer}\r\n" : "") +
        (host != null ? $"HostUrl={host}\r\n" : "");

    [Fact]
    public void Referrer_page_is_preferred_over_file_host()
    {
        var s = ZoneIdentifierSourceReader.Parse(Zone(
            "https://github.com/owner/repo/releases",
            "https://objects.githubusercontent.com/release-asset/123?sig=abc"));
        Assert.NotNull(s);
        Assert.Equal("github.com", s!.Host);
        Assert.Equal("https://github.com/owner/repo/releases", s.Url);
    }

    [Fact]
    public void File_host_is_used_when_referrer_is_missing_and_www_is_dropped()
    {
        var s = ZoneIdentifierSourceReader.Parse(Zone(host: "https://WWW.Example.COM/files/a.zip"));
        Assert.Equal("example.com", s!.Host);
    }

    [Theory]
    [InlineData("about:internet")]
    [InlineData("dsh-app://something")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Placeholders_and_garbage_give_no_source(string value)
    {
        Assert.Null(ZoneIdentifierSourceReader.Parse(Zone(value, value)));
        Assert.Null(ZoneIdentifierSourceReader.Parse("[ZoneTransfer]\nZoneId=3\n"));
    }

    [Fact]
    public void Blob_urls_from_web_messengers_are_unwrapped()
    {
        var s = ZoneIdentifierSourceReader.Parse(Zone(host: "blob:https://web.telegram.org/0f8e-11aa"));
        Assert.Equal("web.telegram.org", s!.Host);
    }

    [Fact]
    public void Placeholder_referrer_falls_back_to_real_file_host()
    {
        var s = ZoneIdentifierSourceReader.Parse(Zone("about:internet", "http://mirror.example.org/x.iso"));
        Assert.Equal("mirror.example.org", s!.Host);
    }

    [Fact]
    public void Reader_reads_real_ntfs_stream_and_ignores_files_without_mark()
    {
        using var tmp = new TempDir();
        var marked = tmp.File("setup.exe");
        var plain = tmp.File("notes.txt");
        File.WriteAllText(marked + ":Zone.Identifier", Zone("https://www.videolan.org/vlc/", null));

        var reader = new ZoneIdentifierSourceReader();
        Assert.Equal("videolan.org", reader.TryRead(marked)?.Host);
        Assert.Null(reader.TryRead(plain));
        Assert.Null(reader.TryRead(Path.Combine(tmp.Downloads, "missing.bin")));
    }

    [Fact]
    public async Task Scanner_and_single_file_entry_carry_the_source()
    {
        using var tmp = new TempDir();
        var marked = tmp.File("sub\\a.zip");
        tmp.File("b.txt");
        File.WriteAllText(marked + ":Zone.Identifier", Zone("https://github.com/x/y"));

        var scanner = new FileScannerService(Fx.Detector(), sources: new ZoneIdentifierSourceReader());
        var result = await scanner.ScanAsync(tmp.Downloads,
            new ScanOptions(true, Array.Empty<string>(), Array.Empty<string>()), null, CancellationToken.None);

        Assert.Equal("github.com", result.Files.Single(f => f.Name == "a.zip").SourceHost);
        Assert.Null(result.Files.Single(f => f.Name == "b.txt").SourceHost);
        Assert.Equal("github.com", scanner.TryCreateEntry(marked)?.SourceHost);
    }

    [Fact]
    public void Moving_an_entry_keeps_its_source()
    {
        var e = new FileEntry(@"C:\d\a.zip", "a.zip", "zip", 1, DateTime.Now, DateTime.Now, FileCategory.Archives, false)
        {
            SourceHost = "github.com",
            SourceUrl = "https://github.com/x"
        };
        var moved = e.WithPath(@"C:\d\Архивы\a.zip", e.Category);
        Assert.Equal("github.com", moved.SourceHost);
    }

    private static FileEntry Entry(string name, long size, string? host) =>
        new(Path.Combine(@"C:\Downloads", name), name, Path.GetExtension(name).TrimStart('.'), size,
            DateTime.Now, DateTime.Now, FileCategory.Other, false)
        { SourceHost = host };

    private sealed class NoOperations : IOperationsCoordinator
    {
        public event Action<IReadOnlyCollection<string>>? FilesLeaving { add { } remove { } }
        public event Action? ReleaseFileHandles { add { } remove { } }
        public event Action<FileEntry>? NewFileArrived { add { } remove { } }
        public Task ScanAsync() => Task.CompletedTask;
        public Task SortAsync(IReadOnlyList<FileEntry>? subset = null) => Task.CompletedTask;
        public Task DeleteAsync(IReadOnlyList<FileEntry> files) => Task.CompletedTask;
        public Task UndoAsync(HistoryEntry entry) => Task.CompletedTask;
        public void ApplyWatcherSettings() { }
    }

    private sealed class NoClipboard : IClipboardService
    {
        public void SetText(string text) { }
    }

    [Fact]
    public async Task Search_finds_files_by_source_site()
    {
        using var tmp = new TempDir();
        var settings = Fx.Settings(tmp);
        var library = new FileLibrary(settings);
        library.SetFiles(new[]
        {
            Entry("release.zip", 10, "github.com"),
            Entry("photo.jpg", 20, "example.com"),
            Entry("plain.txt", 30, null)
        });
        var vm = new FileListViewModel(library, settings, new NoOperations(), new AppStatus(),
            new ToastService(settings), new PreviewViewModel(NullLogService.Instance), new NoClipboard());

        vm.ShowSearch("github");
        await vm.RefreshAsync();

        Assert.Equal(new[] { "release.zip" }, vm.Items.Select(i => i.Name));
        Assert.Equal("github.com · ", vm.Items[0].SourcePrefix);
    }

    [Fact]
    public void Analytics_counts_top_sources_and_opens_their_files()
    {
        using var tmp = new TempDir();
        var settings = Fx.Settings(tmp);
        settings.Current.DownloadsPath = tmp.Downloads;
        var library = new FileLibrary(settings);
        library.SetFiles(new[]
        {
            Entry("a.zip", 100, "github.com"),
            Entry("b.zip", 200, "github.com"),
            Entry("c.jpg", 50, "example.com"),
            Entry("d.txt", 10, null)
        });
        var navigation = new NavigationService();
        NavRequest? request = null;
        navigation.Requested += r => request = r;

        using var vm = new AnalyticsViewModel(library, settings, navigation);

        Assert.True(vm.HasSources);
        Assert.Equal(new[] { "github.com", "example.com" }, vm.Sources.Select(s => s.Host));
        Assert.Equal(2, vm.Sources[0].Count);
        Assert.Equal(1.0, vm.Sources[0].Share);
        Assert.Equal(0.5, vm.Sources[1].Share);

        vm.ShowSourceFilesCommand.Execute(vm.Sources[0]);
        Assert.Equal(new NavRequest(NavKind.All, FilePreset.None, "github.com"), request);
    }
}
