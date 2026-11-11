using System.Diagnostics;
using System.Runtime.InteropServices;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.Helpers;

/// <summary>Системные папки Windows.</summary>
public static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    public static string Downloads
    {
        get
        {
            try
            {
                if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var ptr) == 0)
                {
                    try { return Marshal.PtrToStringUni(ptr) ?? Fallback(); }
                    finally { Marshal.FreeCoTaskMem(ptr); }
                }
            }
            catch { /* используем запасной путь */ }
            return Fallback();
        }
    }

    private static string Fallback() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

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

    public static string AppData => HomeOverride is { Length: > 0 } h
        ? Path.Combine(h, "roaming")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DownloadsCleaner");

    public static string LocalAppData => HomeOverride is { Length: > 0 } h
        ? Path.Combine(h, "local")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DownloadsCleaner");
}

/// <summary>Открытие файлов и папок через оболочку Windows.</summary>
public static class ShellHelper
{
    public static bool OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    public static bool ShowInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else
                return false;
            return true;
        }
        catch { return false; }
    }

    public static bool OpenRecycleBin()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:RecycleBinFolder") { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }
}

/// <summary>Буфер обмена и выбор папки (реализации различаются для Windows и macOS).</summary>
public static class WindowsPlatformServices
{
    public static IClipboardService Clipboard { get; } = new WindowsClipboardService();

    public static IFolderPickerService FolderPicker { get; } = new WindowsFolderPickerService();
}

/// <summary>Буфер обмена Windows.</summary>
public sealed class WindowsClipboardService : IClipboardService
{
    public void SetText(string text)
    {
        System.Windows.Clipboard.SetText(text);
    }
}

/// <summary>Диалог выбора папки Windows.</summary>
public sealed class WindowsFolderPickerService : IFolderPickerService
{
    public Task<string?> PickFolderAsync(string title, string? initialDirectory)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = title,
            InitialDirectory = initialDirectory is { Length: > 0 } d && Directory.Exists(d) ? d : null
        };
        return Task.FromResult(dlg.ShowDialog() == true ? dlg.FolderName : null);
    }
}

/// <summary>Выполнение действий в UI-потоке (в тестах без Application выполняется на месте).</summary>
public static class UiThread
{
    public static void Post(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) action();
        else d.BeginInvoke(action);
    }

    public static Task InvokeAsync(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) { action(); return Task.CompletedTask; }
        return d.InvokeAsync(action).Task;
    }
}
