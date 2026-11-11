using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DownloadsCleaner.Services;

/// <summary>
/// Квота Корзины Windows для тома. Файл, который в неё не помещается, Windows
/// удалит безвозвратно — поэтому такие файлы лучше не отправлять туда вовсе.
/// </summary>
public interface IRecycleBinQuota
{
    /// <summary>Размер квоты в байтах. 0 - квота неизвестна или не ограничена.</summary>
    long GetQuotaBytes(string path);

    /// <summary>Для тома отключена Корзина (NukeOnDelete): удаление будет безвозвратным.</summary>
    bool IsRecycleBinDisabled(string path);
}

/// <summary>
/// Файл не может быть отправлен в Корзину: система удалила бы его безвозвратно.
/// Приложение предпочитает оставить файл на месте.
/// </summary>
public sealed class RecycleBinOverflowException : IOException
{
    public RecycleBinOverflowException(string path, long quotaBytes, string? message = null)
        : base(message ?? Localization.Loc.F("ErrBinQuota", quotaBytes.ToString("N0")))
    {
        Path = path;
        QuotaBytes = quotaBytes;
    }

    public string Path { get; }
    public long QuotaBytes { get; }
}
