using System.Text.RegularExpressions;
using DownloadsCleaner.Services;
using Microsoft.Win32;

namespace DownloadsCleaner.Tests;

/// <summary>Установщик Windows и его стыки с программой (автозапуск, версия в «Приложениях»).</summary>
public sealed class InstallerTests : IDisposable
{
    private const string TestRoot = @"Software\DownloadsCleanerTests";
    private readonly string _key = TestRoot + @"\Uninstall-" + Guid.NewGuid().ToString("N");

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(TestRoot, throwOnMissingSubKey: false);

    private static string InstallerScript()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "installer", "DownloadsCleaner.iss"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "installer", "DownloadsCleaner.iss"));
    }

    [Fact]
    public void Installer_and_app_share_the_same_app_id()
    {
        var match = Regex.Match(InstallerScript(), "#define AppId \"(?<id>[0-9A-F-]+)\"");
        Assert.True(match.Success);
        Assert.Equal(GitHubUpdateService.InstallerAppId, match.Groups["id"].Value);
    }

    [Fact]
    public void Installer_autostart_entry_matches_the_settings_switch()
    {
        // Переключатель в настройках считает автозапуск включённым, если в Run лежит путь к exe;
        // установщик должен писать то же имя значения и ту же команду (путь в кавычках + --minimized).
        var script = InstallerScript();
        Assert.Contains("ValueName: \"DownloadsCleaner\"", script);
        Assert.Contains("ValueData: \"\"\"{app}\\DownloadsCleaner.exe\"\" --minimized\"", script);
        Assert.Equal("DownloadsCleaner", RegistryAutostartService.ValueName);
        // Установка без прав администратора - иначе самообновление не смогло бы писать в папку программы.
        Assert.Contains("PrivilegesRequired=lowest", script);
        Assert.Contains(@"DefaultDirName={localappdata}\Programs\DownloadsCleaner", script);
    }

    [Fact]
    public void Self_update_fixes_version_in_installed_apps_only_for_its_own_folder()
    {
        var appDir = @"C:\Users\Test\AppData\Local\Programs\DownloadsCleaner";
        using (var key = Registry.CurrentUser.CreateSubKey(_key))
        {
            key.SetValue("InstallLocation", appDir + "\\");
            key.SetValue("DisplayVersion", "1.1.0");
        }

        GitHubUpdateService.UpdateInstalledVersion(@"D:\Portable\DownloadsCleaner", new Version(1, 2, 0, 0), _key);
        using (var key = Registry.CurrentUser.OpenSubKey(_key))
            Assert.Equal("1.1.0", key!.GetValue("DisplayVersion"));      // другая копия - не трогаем

        GitHubUpdateService.UpdateInstalledVersion(appDir, new Version(1, 2, 0, 0), _key);
        using (var key = Registry.CurrentUser.OpenSubKey(_key))
            Assert.Equal("1.2.0", key!.GetValue("DisplayVersion"));

        // Копия из архива (записи установщика нет) - ничего не происходит и не падает.
        GitHubUpdateService.UpdateInstalledVersion(appDir, new Version(1, 3, 0, 0), _key + "-missing");
    }
}
