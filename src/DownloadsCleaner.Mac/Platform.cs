using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DownloadsCleaner.Services;
namespace DownloadsCleaner.Helpers;

/// <summary>Системные папки macOS.</summary>
public static class KnownFolders
{
    /// <summary>
    /// Переменная окружения DOWNLOADSCLEANER_HOME переносит настройки, историю и журнал в указанную папку
    /// (портативный режим и безопасное тестирование без затрагивания рабочих данных).
    /// </summary>
    private static string? HomeOverride => Environment.GetEnvironmentVariable("DOWNLOADSCLEANER_HOME") is { Length: > 0 } e
        ? e
        : PortableHome;

    /// <summary>Портативный режим: если рядом с exe лежит файл portable.txt, данные хранятся в папке data рядом с exe.</summary>
    private static string? PortableHome
    {
        get
        {
            try
            {
                var dir = Path.GetDirectoryName(Environment.ProcessPath);
                return dir != null && File.Exists(Path.Combine(dir, "portable.txt")) ? Path.Combine(dir, "data") : null;
            }
            catch { return null; }
        }
    }

    public static string Downloads
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var downloads = Path.Combine(home, "Downloads");
            if (Directory.Exists(downloads)) return downloads;

            // В macOS папка «Загрузки» может называться иначе (например, «Загрузки» в iCloud).
            foreach (var candidate in new[] { "Downloads", "Загрузки" })
            {
                var path = Path.Combine(home, candidate);
                if (Directory.Exists(path)) return path;
            }
            return downloads;
        }
    }

    public static string AppData => HomeOverride is { Length: > 0 } h
        ? Path.Combine(h, "roaming")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DownloadsCleaner");

    public static string LocalAppData => HomeOverride is { Length: > 0 } h
        ? Path.Combine(h, "local")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DownloadsCleaner");
}

/// <summary>Открытие файлов и папок средствами macOS.</summary>
public static class ShellHelper
{
    public static bool OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("/usr/bin/open", $"\"{path}\"") { UseShellExecute = false });
            return true;
        }
        catch { return false; }
    }

    /// <summary>Показывает файл в Finder (аналог «Показать в Проводнике»).</summary>
    public static bool ShowInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("/usr/bin/open", $"-R \"{path}\"") { UseShellExecute = false });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("/usr/bin/open", $"\"{path}\"") { UseShellExecute = false });
            else
                return false;
            return true;
        }
        catch { return false; }
    }

    /// <summary>Открывает Корзину Finder.</summary>
    public static bool OpenRecycleBin()
    {
        try
        {
            Process.Start(new ProcessStartInfo("/usr/bin/open", Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash")) { UseShellExecute = false });
            return true;
        }
        catch { return false; }
    }
}

/// <summary>Выполнение действий в UI-потоке Avalonia.</summary>
public static class UiThread
{
    public static void Post(Action action)
    {
        var d = Dispatcher.UIThread;
        if (!d.CheckAccess()) d.Post(action);
        else action();
    }

    public static Task InvokeAsync(Action action)
    {
        var d = Dispatcher.UIThread;
        if (d.CheckAccess()) { action(); return Task.CompletedTask; }
        return d.InvokeAsync(action).GetTask();
    }
}

/// <summary>Платформенные сервисы macOS (буфер обмена, выбор папки).</summary>
public static class MacPlatformServices
{
    public static IClipboardService Clipboard { get; } = new MacClipboardService();

    public static IFolderPickerService FolderPicker { get; } = new MacFolderPickerService();
}

/// <summary>Текущее окно приложения - нужно для системных диалогов.</summary>
public static class MacWindowAccessor
{
    public static Window? Current { get; set; }
}

/// <summary>Буфер обмена macOS (pbcopy).</summary>
public sealed class MacClipboardService : IClipboardService
{
    public void SetText(string text)
    {
        var psi = new ProcessStartInfo("/usr/bin/pbcopy")
        {
            RedirectStandardInput = true,
            UseShellExecute = false
        };
        using var proc = Process.Start(psi);
        if (proc == null) throw new InvalidOperationException("Не удалось скопировать в буфер обмена");
        proc.StandardInput.Write(text);
        proc.StandardInput.Close();
        proc.WaitForExit(2000);
    }
}

/// <summary>Выбор папки средствами Avalonia (системный диалог macOS).</summary>
public sealed class MacFolderPickerService : IFolderPickerService
{
    public async Task<string?> PickFolderAsync(string title, string? initialDirectory)
    {
        var storage = MacWindowAccessor.Current?.StorageProvider;
        if (storage == null) return null;

        IStorageFolder? start = null;
        if (initialDirectory is { Length: > 0 } p && Directory.Exists(p))
        {
            try { start = await storage.TryGetFolderFromPathAsync(p); }
            catch { /* начинаем с папки по умолчанию */ }
        }

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            SuggestedStartLocation = start
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
