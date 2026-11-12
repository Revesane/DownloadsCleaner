using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace DownloadsCleaner.Services;

/// <summary>
/// Обновления из выпусков GitHub (Revesane/DownloadsCleaner). Выпуск собирает GitHub Actions
/// по тегу vX.Y.Z и прикладывает архив DownloadsCleaner-win-x64.zip с одним exe.
/// Обновление: скачать архив → проверить версию нового exe → переименовать текущий exe в .old
/// (Windows разрешает это для работающего файла) → положить новый → запустить его и закрыться.
/// </summary>
public sealed class GitHubUpdateService : IUpdateService
{
    public const string Repository = "Revesane/DownloadsCleaner";
    internal const string AssetName = "DownloadsCleaner-win-x64.zip";
    internal const string ExeName = "DownloadsCleaner.exe";
    private const string LatestReleaseUrl = $"https://api.github.com/repos/{Repository}/releases/latest";

    private readonly HttpClient _http;
    private readonly ILogService _log;
    private readonly string? _exePath;

    public GitHubUpdateService(ILogService? log = null) : this(null, null, null, log) { }

    internal GitHubUpdateService(HttpClient? http, Version? current, string? exePath, ILogService? log = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // Нейтральное имя клиента: GitHub требует User-Agent, личных данных в нём нет.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DownloadsCleaner-Updater");
        _log = log ?? NullLogService.Instance;
        _exePath = exePath ?? Environment.ProcessPath;
        CurrentVersion = Normalize(current ?? Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0));
    }

    public Version CurrentVersion { get; }

    public bool CanInstall
    {
        get
        {
            if (string.IsNullOrEmpty(_exePath) || !string.Equals(Path.GetFileName(_exePath), ExeName, StringComparison.OrdinalIgnoreCase))
                return false;
            var dir = Path.GetDirectoryName(_exePath)!;
            // Сборка для разработки - это exe-заглушка рядом с DownloadsCleaner.dll; её не подменяем.
            if (File.Exists(Path.Combine(dir, "DownloadsCleaner.dll"))) return false;
            return IsWritable(dir);
        }
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;      // выпусков ещё нет
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        var update = ParseRelease(json);
        _log.Info($"Проверка обновлений: текущая {CurrentVersion}, последняя {update?.Version.ToString() ?? "?"}");
        return update != null && update.Version > CurrentVersion ? update : null;
    }

    /// <summary>Разбирает ответ GitHub о выпуске. Null - если версию по тегу не понять.</summary>
    internal static UpdateInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (ParseVersion(tag) is not { } version) return null;

        string? assetUrl = null;
        long assetSize = 0;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (!string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase)) continue;
                assetUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                assetSize = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : 0;
                break;
            }
        }

        return new UpdateInfo(version,
            root.TryGetProperty("body", out var b) ? b.GetString() ?? string.Empty : string.Empty,
            root.TryGetProperty("html_url", out var h) ? h.GetString() ?? string.Empty : $"https://github.com/{Repository}/releases",
            assetUrl, assetSize);
    }

    /// <summary>"v1.2.3" → 1.2.3.0; дополняет нулями, чтобы 1.2 == 1.2.0.0.</summary>
    internal static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim().TrimStart('v', 'V');
        var dash = s.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) s = s[..dash];
        return Version.TryParse(s, out var v) ? Normalize(v) : null;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    public async Task InstallAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct)
    {
        if (!CanInstall || _exePath == null) throw new InvalidOperationException("Самообновление недоступно для этой копии программы.");
        if (update.AssetUrl == null) throw new InvalidOperationException("В выпуске нет архива для Windows.");

        var work = Path.Combine(Path.GetTempPath(), "DownloadsCleaner-update", update.Version.ToString());
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);

        var zip = Path.Combine(work, AssetName);
        await DownloadAsync(update.AssetUrl, zip, update.AssetSize, progress, ct);
        var newExe = ExtractExe(zip, work);
        VerifyVersion(newExe, update.Version);

        ct.ThrowIfCancellationRequested();
        SwapExecutable(_exePath, newExe);
        UpdateInstalledVersion(Path.GetDirectoryName(_exePath)!, update.Version);
        _log.Info($"Обновление до {update.Version} установлено, перезапуск");

        // Новая копия дождётся закрытия этой (иначе сработает защита от второго экземпляра).
        Process.Start(new ProcessStartInfo(_exePath, $"--wait-pid {Environment.ProcessId}") { UseShellExecute = false });
        // Настоящий выход, а не сворачивание в трей по крестику.
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (System.Windows.Application.Current is App app) app.ExitApplication();
            else System.Windows.Application.Current.Shutdown();
        });
    }

    private async Task DownloadAsync(string url, string target, long expected, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? expected;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(target);
        var buffer = new byte[81920];
        long done = 0;
        int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            done += n;
            if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
        }
    }

    /// <summary>Достаёт DownloadsCleaner.exe из архива выпуска.</summary>
    internal static string ExtractExe(string zipPath, string workDir)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.Name, ExeName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException("В архиве обновления нет DownloadsCleaner.exe.");
        var target = Path.Combine(workDir, "new-" + ExeName);
        entry.ExtractToFile(target, overwrite: true);
        return target;
    }

    /// <summary>Не ставим файл, если его версия не та, что обещал выпуск (повреждён или подменён).</summary>
    internal static void VerifyVersion(string exe, Version expected)
    {
        var info = FileVersionInfo.GetVersionInfo(exe);
        var actual = ParseVersion(info.ProductVersion) ?? ParseVersion(info.FileVersion);
        if (actual == null || actual < expected)
            throw new InvalidDataException($"Версия скачанного файла ({actual?.ToString() ?? "?"}) не совпадает с выпуском {expected}.");
    }

    /// <summary>
    /// Подменяет exe: текущий переименовывается в .old (работающий файл переименовать можно),
    /// новый копируется на его место. При ошибке старый файл возвращается обратно.
    /// </summary>
    internal static void SwapExecutable(string currentExe, string newExe)
    {
        var old = currentExe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(currentExe, old);
        try
        {
            File.Copy(newExe, currentExe, overwrite: false);
        }
        catch
        {
            if (File.Exists(currentExe)) File.Delete(currentExe);
            File.Move(old, currentExe);
            throw;
        }
    }

    /// <summary>Идентификатор из installer\DownloadsCleaner.iss (AppId).</summary>
    internal const string InstallerAppId = "6E1B7D42-3F9A-4C1E-9B57-2D8A4C0F7E31";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{" + InstallerAppId + "}_is1";

    /// <summary>
    /// Если программа поставлена установщиком, после самообновления поправляет версию в
    /// «Приложениях» Windows - иначе там навсегда осталась бы версия из установщика.
    /// Копию, распакованную из архива (без записи установщика), не трогает.
    /// </summary>
    internal static void UpdateInstalledVersion(string appDir, Version version, string uninstallKey = UninstallKey)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(uninstallKey, writable: true);
            if (key?.GetValue("InstallLocation") is not string location) return;
            // Запись должна относиться именно к этой папке, а не к другой копии программы.
            if (!string.Equals(Path.GetFullPath(location).TrimEnd('\\'), Path.GetFullPath(appDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return;
            var text = version.ToString(3);
            key.SetValue("DisplayVersion", text);
            key.SetValue("DisplayName", "Downloads Cleaner");
        }
        catch { /* не мешаем обновлению */ }
    }

    /// <summary>
    /// Вызывается при запуске: ждёт закрытия прежней версии (аргумент --wait-pid) и убирает
    /// оставшийся после обновления .old-файл.
    /// </summary>
    public static void HandleStartup(string[] args)
    {
        try
        {
            var i = Array.IndexOf(args, "--wait-pid");
            if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var pid))
            {
                try { Process.GetProcessById(pid).WaitForExit(15000); }
                catch (ArgumentException) { /* уже закрылась */ }
            }
            if (Environment.ProcessPath is { } exe && File.Exists(exe + ".old"))
                File.Delete(exe + ".old");
        }
        catch { /* не мешаем запуску */ }
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
