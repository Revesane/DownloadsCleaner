using DownloadsCleaner.Services;
using Microsoft.Win32;

namespace DownloadsCleaner.Tests;

/// <summary>Автозапуск: пишет во временный раздел реестра, а не в настоящую автозагрузку.</summary>
public sealed class AutostartTests : IDisposable
{
    private readonly string _key = @"Software\DownloadsCleanerTests\Run-" + Guid.NewGuid().ToString("N");
    private readonly TempDir _tmp = new();

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\DownloadsCleanerTests", throwOnMissingSubKey: false);
        _tmp.Dispose();
    }

    private string PublishedExe()
    {
        var dir = Path.Combine(_tmp.Path, "Program Files", "Downloads Cleaner");     // путь с пробелами
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "DownloadsCleaner.exe");
        File.WriteAllText(exe, "exe");
        return exe;
    }

    [Fact]
    public void Enable_writes_quoted_path_with_minimized_flag_and_disable_removes_it()
    {
        var exe = PublishedExe();
        var autostart = new RegistryAutostartService(_key, exe);
        Assert.True(autostart.IsAvailable);
        Assert.False(autostart.IsEnabled);

        autostart.SetEnabled(true);
        Assert.True(autostart.IsEnabled);
        using (var key = Registry.CurrentUser.OpenSubKey(_key))
            Assert.Equal($"\"{exe}\" --minimized", key!.GetValue(RegistryAutostartService.ValueName));

        autostart.SetEnabled(false);
        Assert.False(autostart.IsEnabled);
        using (var key = Registry.CurrentUser.OpenSubKey(_key))
            Assert.Null(key!.GetValue(RegistryAutostartService.ValueName));
    }

    [Fact]
    public void Entry_of_another_copy_does_not_count_as_enabled()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(_key))
            key.SetValue(RegistryAutostartService.ValueName, "\"D:\\Old\\DownloadsCleaner.exe\" --minimized");
        Assert.False(new RegistryAutostartService(_key, PublishedExe()).IsEnabled);
    }

    [Fact]
    public void Copy_built_from_source_is_never_added_to_startup()
    {
        var exe = PublishedExe();
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(exe)!, "DownloadsCleaner.dll"), "dll");   // сборка для разработки
        var autostart = new RegistryAutostartService(_key, exe);

        Assert.False(autostart.IsAvailable);
        autostart.SetEnabled(true);
        Assert.False(autostart.IsEnabled);
        Assert.Null(Registry.CurrentUser.OpenSubKey(_key));
    }
}
