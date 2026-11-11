using System.Diagnostics;
using System.Runtime.InteropServices;
using DownloadsCleaner.Localization;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.Mac.Services;

/// <summary>
/// Корзина macOS. Файл переносится в Trash средствами Finder (AppleScript),
/// поэтому его можно восстановить как на любом Mac.
/// </summary>
public sealed class MacRecycleBin : IRecycleBin
{
    private readonly IRecycleBinQuota? _quota;

    public MacRecycleBin(IRecycleBinQuota? quota = null) => _quota = quota;

    public void MoveToRecycleBin(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException(Loc.T("ErrFileNotFound"), path);

        if (_quota != null && _quota.IsRecycleBinDisabled(path))
            throw new RecycleBinOverflowException(path, 0, Loc.T("ErrBinDisabled"));

        var ok = TryFinderTrash(path) || TryTrashFolder(path);
        if (!ok)
            throw new IOException(Loc.F("ErrRecycleFailed", "trash"));
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException(Loc.T("ErrNotDeleted"));
    }

    /// <summary>Перемещение в Корзину через Finder (работает и с файлами, и с папками).</summary>
    private static bool TryFinderTrash(string path)
    {
        try
        {
            var script = $"tell application \"Finder\" to delete POSIX file \"{Escape(path)}\"";
            var psi = new ProcessStartInfo("/usr/bin/osascript", ["-e", script])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(15000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Запасной вариант: перенос в ~/.Trash вручную.</summary>
    private static bool TryTrashFolder(string path)
    {
        try
        {
            var trash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");
            Directory.CreateDirectory(trash);

            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
            var target = Path.Combine(trash, name);
            var index = 1;
            while (File.Exists(target) || Directory.Exists(target))
            {
                target = Path.Combine(trash, $"{name} {index++}");
            }

            if (Directory.Exists(path)) Directory.Move(path, target);
            else File.Move(path, target);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Escape(string path) => path.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

/// <summary>
/// Квота Корзины macOS неизвестна: Finder сам решает, что помещается в Корзину,
/// и при нехватке места удаляет файлы напрямую. Поэтому проверка отключена.
/// </summary>
public sealed class MacRecycleBinQuota : IRecycleBinQuota
{
    public long GetQuotaBytes(string path) => 0;

    public bool IsRecycleBinDisabled(string path) => false;
}
