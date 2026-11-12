using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using DownloadsCleaner.Services;
using DownloadsCleaner.ViewModels;

namespace DownloadsCleaner.Tests;

/// <summary>Обновление программы из выпусков GitHub - без сети и без настоящего перезапуска.</summary>
public class UpdateServiceTests
{
    /// <summary>Подставной сервер: отвечает заданным JSON или кодом.</summary>
    private sealed class FakeHandler(HttpStatusCode code, string body = "") : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? UserAgent { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            UserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static string Release(string tag, bool withAsset = true) => $$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://github.com/Revesane/DownloadsCleaner/releases/tag/{{tag}}",
          "body": "Что нового",
          "assets": [
            { "name": "notes.txt", "browser_download_url": "https://example/notes.txt", "size": 10 }
            {{(withAsset ? ", { \"name\": \"DownloadsCleaner-win-x64.zip\", \"browser_download_url\": \"https://example/app.zip\", \"size\": 12345 }" : "")}}
          ]
        }
        """;

    [Theory]
    [InlineData("v1.2.3", "1.2.3.0")]
    [InlineData("1.2", "1.2.0.0")]
    [InlineData("v2.0.0-beta.1", "2.0.0.0")]
    [InlineData("1.0.0+3f2a9c", "1.0.0.0")]
    [InlineData("latest", null)]
    [InlineData("", null)]
    public void Versions_from_tags(string tag, string? expected)
    {
        Assert.Equal(expected, GitHubUpdateService.ParseVersion(tag)?.ToString());
    }

    [Fact]
    public void Release_json_picks_the_windows_archive()
    {
        var update = GitHubUpdateService.ParseRelease(Release("v1.1.0"));
        Assert.NotNull(update);
        Assert.Equal(new Version(1, 1, 0, 0), update!.Version);
        Assert.Equal("https://example/app.zip", update.AssetUrl);
        Assert.Equal(12345, update.AssetSize);
        Assert.Equal("Что нового", update.Notes);

        Assert.Null(GitHubUpdateService.ParseRelease(Release("v1.1.0", withAsset: false))!.AssetUrl);
        Assert.Null(GitHubUpdateService.ParseRelease(Release("nightly")));
    }

    [Fact]
    public async Task Check_reports_only_newer_versions()
    {
        async Task<UpdateInfo?> Check(string current, HttpStatusCode code, string body)
        {
            var handler = new FakeHandler(code, body);
            var service = new GitHubUpdateService(new HttpClient(handler), Version.Parse(current), null);
            var result = await service.CheckAsync(CancellationToken.None);
            Assert.Equal("DownloadsCleaner-Updater", handler.UserAgent);   // без личных данных
            return result;
        }

        Assert.NotNull(await Check("1.0.0", HttpStatusCode.OK, Release("v1.1.0")));
        Assert.Null(await Check("1.1.0", HttpStatusCode.OK, Release("v1.1.0")));
        Assert.Null(await Check("1.2.0", HttpStatusCode.OK, Release("v1.1.0")));
        Assert.Null(await Check("1.0.0", HttpStatusCode.NotFound, "{}"));       // выпусков ещё нет
        await Assert.ThrowsAsync<HttpRequestException>(() => Check("1.0.0", HttpStatusCode.InternalServerError, "{}"));
    }

    [Fact]
    public void Can_install_only_a_published_exe_in_a_writable_folder()
    {
        using var tmp = new TempDir();
        var exe = Path.Combine(tmp.Path, "DownloadsCleaner.exe");
        File.WriteAllText(exe, "exe");
        Assert.True(new GitHubUpdateService(new HttpClient(), new Version(1, 0), exe).CanInstall);

        // Сборка для разработки: exe-заглушка рядом с dll.
        File.WriteAllText(Path.Combine(tmp.Path, "DownloadsCleaner.dll"), "dll");
        Assert.False(new GitHubUpdateService(new HttpClient(), new Version(1, 0), exe).CanInstall);
        // Запущено не наше приложение (например, тесты).
        Assert.False(new GitHubUpdateService(new HttpClient(), new Version(1, 0), Path.Combine(tmp.Path, "testhost.exe")).CanInstall);
    }

    [Fact]
    public void Swap_replaces_exe_and_keeps_old_one_or_rolls_back()
    {
        using var tmp = new TempDir();
        var current = Path.Combine(tmp.Path, "DownloadsCleaner.exe");
        var fresh = Path.Combine(tmp.Path, "new.exe");
        File.WriteAllText(current, "old");
        File.WriteAllText(fresh, "new");

        GitHubUpdateService.SwapExecutable(current, fresh);
        Assert.Equal("new", File.ReadAllText(current));
        Assert.Equal("old", File.ReadAllText(current + ".old"));

        // Новый файл не скопировался - прежний exe возвращается на место.
        File.WriteAllText(current, "v2");
        Assert.ThrowsAny<IOException>(() => GitHubUpdateService.SwapExecutable(current, Path.Combine(tmp.Path, "missing.exe")));
        Assert.Equal("v2", File.ReadAllText(current));
    }

    [Fact]
    public void Exe_is_extracted_from_the_release_archive()
    {
        using var tmp = new TempDir();
        var zip = Path.Combine(tmp.Path, "app.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(archive.CreateEntry("DownloadsCleaner.exe").Open());
            w.Write("binary");
        }
        var exe = GitHubUpdateService.ExtractExe(zip, tmp.Path);
        Assert.Equal("binary", File.ReadAllText(exe));

        var empty = Path.Combine(tmp.Path, "empty.zip");
        using (ZipFile.Open(empty, ZipArchiveMode.Create)) { }
        Assert.Throws<InvalidDataException>(() => GitHubUpdateService.ExtractExe(empty, tmp.Path));
    }

    [Fact]
    public void Downloaded_file_must_have_the_promised_version()
    {
        // Сборка приложения имеет версию 1.0.0 из csproj.
        var dll = typeof(GitHubUpdateService).Assembly.Location;
        GitHubUpdateService.VerifyVersion(dll, new Version(1, 0, 0, 0));
        Assert.Throws<InvalidDataException>(() => GitHubUpdateService.VerifyVersion(dll, new Version(9, 0, 0, 0)));
    }
}

public class UpdateViewModelTests
{
    private sealed class FakeUpdates(UpdateInfo? next, bool canInstall = true, Exception? error = null) : IUpdateService
    {
        public Version CurrentVersion => new(1, 0, 0, 0);
        public bool CanInstall => canInstall;
        public UpdateInfo? Installed { get; private set; }
        public Task<UpdateInfo?> CheckAsync(CancellationToken ct) => error != null ? Task.FromException<UpdateInfo?>(error) : Task.FromResult(next);
        public Task InstallAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct) { Installed = update; return Task.CompletedTask; }
    }

    private static UpdateViewModel Create(IUpdateService service, TempDir tmp, out SettingsService settings)
    {
        settings = Fx.Settings(tmp);
        return new UpdateViewModel(service, settings, new ToastService(settings), new DialogService(), NullLogService.Instance);
    }

    private static readonly UpdateInfo NewVersion = new(new Version(1, 1, 0, 0), "Что нового", "https://github.com/x", "https://example/app.zip", 100);

    [Fact]
    public async Task Manual_check_shows_the_new_version()
    {
        using var tmp = new TempDir();
        var vm = Create(new FakeUpdates(NewVersion), tmp, out _);

        await vm.RunCheckAsync(quiet: false);

        Assert.Equal(UpdateState.Available, vm.State);
        Assert.True(vm.HasUpdate);
        Assert.Contains("1.1.0", vm.StatusText);
        Assert.Equal("Что нового", vm.Notes);
        Assert.True(vm.InstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task Up_to_date_and_errors_are_reported_correctly()
    {
        using var tmp = new TempDir();
        var upToDate = Create(new FakeUpdates(null), tmp, out _);
        await upToDate.RunCheckAsync(quiet: false);
        Assert.Equal(UpdateState.UpToDate, upToDate.State);
        Assert.False(upToDate.InstallCommand.CanExecute(null));

        var offline = Create(new FakeUpdates(null, error: new HttpRequestException("нет сети")), tmp, out _);
        await offline.RunCheckAsync(quiet: false);
        Assert.Equal(UpdateState.Error, offline.State);
        // Тихая проверка при запуске без сети ничего не показывает.
        await offline.RunCheckAsync(quiet: true);
        Assert.Equal(UpdateState.Idle, offline.State);
    }

    [Fact]
    public void Release_notes_are_shown_without_markdown()
    {
        var md = "## What's Changed\n* **Новое**: темы by [@me](https://x)\n\n**Full Changelog**: https://github.com/x/compare/v1...v2";
        Assert.Equal("What's Changed\n• Новое: темы by @me", UpdateViewModel.CleanNotes(md));
    }

    [Fact]
    public async Task Startup_check_respects_the_setting()
    {
        using var tmp = new TempDir();
        var service = new FakeUpdates(NewVersion);
        var vm = Create(service, tmp, out var settings);
        settings.Current.CheckUpdatesOnStartup = false;

        await vm.CheckOnStartupAsync();
        Assert.Equal(UpdateState.Idle, vm.State);

        settings.Current.CheckUpdatesOnStartup = true;
        await vm.CheckOnStartupAsync();
        Assert.Equal(UpdateState.Available, vm.State);
    }

    [Fact]
    public async Task Copy_that_cannot_update_itself_offers_the_release_page()
    {
        using var tmp = new TempDir();
        var vm = Create(new FakeUpdates(NewVersion, canInstall: false), tmp, out _);
        await vm.RunCheckAsync(quiet: false);
        Assert.Equal(DownloadsCleaner.Localization.Loc.T("UpdOpenPage"), vm.InstallText);
    }
}
