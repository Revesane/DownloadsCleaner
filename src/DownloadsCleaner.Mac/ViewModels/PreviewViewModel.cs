using System.Globalization;
using System.Text;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadsCleaner.Helpers;
using DownloadsCleaner.Localization;
using DownloadsCleaner.Models;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.ViewModels;

public enum PreviewKind { None, Image, Audio, Video, Text, Info }

/// <summary>
/// Предпросмотр файла для macOS. Изображения и текст показываются внутри приложения,
/// аудио и видео открываются во внешней программе (в macOS нет встроенного проигрывателя,
/// который можно встроить в окно без лишних зависимостей).
/// </summary>
public sealed partial class PreviewViewModel : ObservableObject
{
    private const long MaxImageBytes = 200L * 1024 * 1024;
    private const int MaxTextChars = 12_000;

    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { "jpg", "jpeg", "png", "bmp", "gif", "tiff", "tif", "ico", "webp", "heic", "avif" };

    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
        { "txt", "md", "csv", "log", "json", "xml", "yml", "yaml", "ini", "cfg", "conf", "cs", "py", "js", "ts", "html", "css" };

    private readonly ILogService _log;
    private int _version;

    public PreviewViewModel(ILogService log) => _log = log;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasFile))] private FileEntry? _file;
    [ObservableProperty] private PreviewKind _kind = PreviewKind.None;
    [ObservableProperty] private Bitmap? _image;
    [ObservableProperty] private string _text = string.Empty;
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
            if (entry.Category == FileCategory.Audio || entry.Category == FileCategory.Video)
            {
                // В macOS медиа открывается системным плеером (Quick Look / приложение по умолчанию).
                Message = Loc.T("PreviewCannotPlay");
                Kind = PreviewKind.Info;
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
            Message = Loc.T("PreviewFailed");
        }
        finally
        {
            if (version == _version) IsLoading = false;
        }
    }

    private static string BuildInfo(FileEntry e) =>
        Loc.F("PreviewInfoType", $"{FileCategoryInfo.Title(e.Category)} ({(e.Extension.Length == 0 ? Loc.T("PreviewNoExtension") : "." + e.Extension)})") + "\n" +
        Loc.F("PreviewInfoSize", ByteFormatter.Format(e.Size)) + "\n" +
        Loc.F("PreviewInfoCreated", e.Created.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture)) + "\n" +
        Loc.F("PreviewInfoModified", e.Modified.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture));

    private static Bitmap? LoadImage(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new Bitmap(fs);
        }
        catch
        {
            return null;
        }
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
