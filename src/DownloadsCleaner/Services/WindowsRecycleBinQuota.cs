using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DownloadsCleaner.Services;

/// <summary>
/// Квота Корзины Windows из реестра. Для каждого тома там хранится MaxCapacity
/// в мегабайтах; если значения нет, действует стандартная квота 5% объёма тома.
/// </summary>
public sealed class WindowsRecycleBinQuota : IRecycleBinQuota
{
    private const string BucketKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket";
    private const int DefaultCapacityPercent = 5;
    private const long BytesInMegabyte = 1024L * 1024L;

    public long GetQuotaBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return 0;

            // Значение хранится в мегабайтах, а не в процентах: 50841 = ~50 ГБ.
            if (TryReadVolumeValue(root, "MaxCapacity", out var megabytes) && megabytes > 0)
                return megabytes * BytesInMegabyte;

            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.TotalSize * DefaultCapacityPercent / 100 : 0;
        }
        catch
        {
            return 0;      // квота неизвестна - не блокируем удаление
        }
    }

    public bool IsRecycleBinDisabled(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return false;
            return TryReadVolumeValue(root, "NukeOnDelete", out var value) && value == 1;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadVolumeValue(string root, string name, out long value)
    {
        value = 0;
        var volumeGuid = GetVolumeGuid(root);
        if (volumeGuid == null) return false;

        using var key = Registry.CurrentUser.OpenSubKey($@"{BucketKey}\Volume\{volumeGuid}");
        var raw = key?.GetValue(name);
        if (raw == null) return false;

        value = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// Возвращает имя ключа реестра вида "{GUID}" для точки монтирования.
    /// GetVolumeNameForVolumeMountPoint даёт "\\?\Volume{GUID}\".
    /// </summary>
    private static string? GetVolumeGuid(string mountPoint)
    {
        var buffer = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPoint(mountPoint, buffer, buffer.Capacity)) return null;

        var name = buffer.ToString().TrimEnd('\\');
        var marker = name.LastIndexOf("Volume{", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 ? name[(marker + "Volume".Length)..] : null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string mountPoint,
        [Out] StringBuilder volumeName,
        int bufferLength);
}

