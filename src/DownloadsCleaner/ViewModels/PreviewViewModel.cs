using System.Globalization;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadsCleaner.Helpers;
using DownloadsCleaner.Localization;
using DownloadsCleaner.Models;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.ViewModels;

public enum PreviewKind { None, Image, Audio, Video, Text, Info }

/// <summary>
/// Предпросмотр одного файла. Запускается только по действию пользователя (открытая панель),
/// большие файлы целиком в память не читаются: изображения декодируются в уменьшенном размере,
/// текст читается первыми килобайтами.
/// </summary>
public sealed partial class PreviewViewModel : ObservableObject
{
    private const long MaxImageBytes = 200L * 1024 * 1024;
    private const int MaxTextChars = 12_000;

    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { "jpg", "jpeg", "png", "bmp", "gif", "tiff", "tif", "ico", "webp", "heic", "avif" };
    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
        { "txt", "md", "csv", "log", "json", "xml", "ini", "cfg", "bat", "cmd", "ps1", "yaml", "yml" };

    private readonly ILogService _log;
    private int _version;

    public PreviewViewModel(ILogService log) => _log = log;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFile))] private FileEntry? _file;
    [ObservableProperty] private PreviewKind _kind = PreviewKind.None;
    [ObservableProperty] private ImageSource? _image;
    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private Uri? _mediaUri;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _infoLines = string.Empty;

    public bool HasFile => File != null;

    /// <summary>Сбрасывает предпросмотр и освобождает файл (важно перед удалением и перемещением).</summary>
    public void Clear()
    {
        _version++;
        File = null;
        Kind = PreviewKind.None;
        Image = null;
        Text = string.Empty;
        MediaUri = null;
        Message = string.Empty;
        InfoLines = string.Empty;
        IsLoading = false;
    }

    public async Task ShowAsync(FileEntry? entry)
    {
        if (entry == null) { Clear(); return; }
        var version = ++_version;

        File = entry;
        Image = null;
        Text = string.Empty;
        MediaUri = null;
        Message = string.Empty;
        Kind = PreviewKind.Info;
        InfoLines = BuildInfo(entry);

        if (!System.IO.File.Exists(entry.FullPath))
        {
            Message = Loc.T("PreviewMissing");
            return;
        }

        try
        {
            if (entry.Category == FileCategory.Audio)
            {
                var uri = TryCreateFileUri(entry.FullPath);
                if (uri == null) { Message = Loc.T("PreviewCannotPlay"); return; }
                MediaUri = uri;
                Kind = PreviewKind.Audio;
            }
            else if (entry.Category == FileCategory.Video)
            {
                var uri = TryCreateFileUri(entry.FullPath);
                if (uri == null) { Message = Loc.T("PreviewCannotPlay"); return; }
                MediaUri = uri;
                Kind = PreviewKind.Video;
            }
            else if (entry.Extension == "svg")
            {
                Message = Loc.T("PreviewSvg");
            }
            else if (ImageExt.Contains(entry.Extension))
            {
                if (entry.Size > MaxImageBytes)
                {
                    Message = Loc.T("PreviewTooLarge");
                    return;
                }
                IsLoading = true;
                var img = await Task.Run(() => LoadImage(entry.FullPath));
                if (version != _version) return;
                Image = img;
                Kind = PreviewKind.Image;
            }
            else if (TextExt.Contains(entry.Extension))
            {
                IsLoading = true;
                var text = await Task.Run(() => ReadText(entry.FullPath));
                if (version != _version) return;
                Text = text;
                Kind = PreviewKind.Text;
            }
            else if (entry.Category == FileCategory.Documents)
            {
                Message = Loc.T("PreviewUnsupportedFormat");
            }
            else
            {
                Message = Loc.T("PreviewUnsupportedType");
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"Предпросмотр {entry.FullPath}: {ex.Message}");
            if (version != _version) return;
            Kind = PreviewKind.Info;
            Image = null;
            MediaUri = null;
            Message = Loc.T("PreviewFailed");
        }
        finally
        {
            if (version == _version) IsLoading = false;
        }
    }

    /// <summary>Вызывается из View, если MediaElement не смог воспроизвести файл.</summary>
    public void ReportMediaFailed()
    {
        MediaUri = null;
        Kind = PreviewKind.Info;
        Message = Loc.T("PreviewPlaybackFailed");
    }

    private static string BuildInfo(FileEntry e) =>
        Loc.F("PreviewInfoType", $"{FileCategoryInfo.Title(e.Category)} ({(e.Extension.Length == 0 ? Loc.T("PreviewNoExtension") : "." + e.Extension)})") + "\n" +
        Loc.F("PreviewInfoSize", ByteFormatter.Format(e.Size)) + "\n" +
        Loc.F("PreviewInfoCreated", e.Created.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture)) + "\n" +
        Loc.F("PreviewInfoModified", e.Modified.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture));

    /// <summary>
    /// Создаёт file-URI для MediaElement. Простой new Uri(path) бросает исключение
    /// на путях с '%' или '#', поэтому такой путь экранируется вручную.
    /// </summary>
    internal static Uri? TryCreateFileUri(string path)
    {
        try
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out var direct) && direct.IsFile) return direct;
        }
        catch { /* переходим к запасному варианту */ }

        try
        {
            var escaped = path.Replace("%", "%25").Replace("#", "%23");
            return Uri.TryCreate(escaped, UriKind.Absolute, out var uri) && uri.IsFile ? uri : null;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource LoadImage(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var width = 0;
        try
        {
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            width = decoder.Frames[0].PixelWidth;
        }
        catch { /* размеры неизвестны - декодируем как есть */ }
        fs.Position = 0;

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        if (width > 900) bmp.DecodePixelWidth = 900;     // не держим в памяти полноразмерное изображение
        bmp.StreamSource = fs;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static string ReadText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs, Encoding.UTF8, true);
        var buffer = new char[MaxTextChars];
        var read = sr.ReadBlock(buffer, 0, buffer.Length);
        var s = new string(buffer, 0, read);
        return sr.Peek() >= 0 ? s + "\n…" : s;
    }

    [RelayCommand]
    private void OpenExternal()
    {
        if (File != null) ShellHelper.OpenFile(File.FullPath);
    }

    [RelayCommand]
    private void ShowInExplorer()
    {
        if (File != null) ShellHelper.ShowInExplorer(File.FullPath);
    }
}
