using System.Runtime.InteropServices;
using DownloadsCleaner.Localization;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.Services;

/// <summary>
/// Корзина через SHFileOperation. Флаг FOF_WANTNUKEWARNING гарантирует, что Windows предупредит,
/// если файл слишком велик для Корзины и был бы удалён безвозвратно.
/// </summary>
public sealed class WindowsRecycleBin : IRecycleBin
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    private readonly IRecycleBinQuota? _quota;

    public WindowsRecycleBin(IRecycleBinQuota? quota = null) => _quota = quota;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    public void MoveToRecycleBin(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(Loc.T("ErrFileNotFound"), path);

        // Не отправляем в Корзину то, что в неё не поместится: Windows удалил бы такой файл
        // безвозвратно, а пользователь выбрал «В Корзину», а не «удалить навсегда».
        if (_quota != null)
        {
            if (_quota.IsRecycleBinDisabled(path))
                throw new RecycleBinOverflowException(path, 0, Loc.T("ErrBinDisabled"));

            var quotaBytes = _quota.GetQuotaBytes(path);
            if (quotaBytes > 0)
            {
                var size = new FileInfo(path).Length;
                if (size > quotaBytes)
                    throw new RecycleBinOverflowException(path, quotaBytes, Loc.T("ErrExceedsQuota"));
            }
        }

        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + '\0' + '\0',
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING
        };
        var rc = SHFileOperation(ref op);
        if (op.fAnyOperationsAborted) throw new OperationCanceledException(Loc.T("ErrUserCancelled"));
        if (rc != 0) throw new IOException(Loc.F("ErrRecycleFailed", rc.ToString("X")));
        if (File.Exists(path)) throw new IOException(Loc.T("ErrNotDeleted"));
    }
}

