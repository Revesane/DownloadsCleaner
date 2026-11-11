using DownloadsCleaner.Models;

namespace DownloadsCleaner.Services;

public interface IFileTypeDetectionService
{
    /// <summary>Определяет категорию по имени файла или расширению. Содержимое файла не читается.</summary>
    FileCategory Detect(string fileNameOrExtension);

    /// <summary>Признак временного/незавершённого файла (.crdownload, .part, .tmp ...).</summary>
    bool IsTemporary(string fileNameOrExtension);

    /// <summary>Приводит расширение к виду "pdf" (нижний регистр, без точки).</summary>
    string NormalizeExtension(string fileNameOrExtension);
}

/// <summary>
/// Определяет тип файла строго по расширению. Пользовательские правила из настроек
/// имеют приоритет над встроенным списком. Неизвестные расширения -> «Прочее».
/// </summary>
public sealed class FileTypeDetectionService : IFileTypeDetectionService
{
    private static readonly Dictionary<string, FileCategory> BuiltIn = Build();

    public static readonly IReadOnlySet<string> TemporaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "tmp", "temp", "crdownload", "part", "partial", "download", "opdownload", "!ut", "bc!", "aria2"
    };

    private readonly ISettingsService _settings;

    public FileTypeDetectionService(ISettingsService settings) => _settings = settings;

    public string NormalizeExtension(string fileNameOrExtension)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrExtension)) return string.Empty;
        var s = fileNameOrExtension.Trim();
        // "pdf", ".pdf", "report.pdf" -> "pdf"
        var ext = s.Contains('.') ? Path.GetExtension(s) : s;
        return ext.TrimStart('.').Trim().ToLowerInvariant();
    }

    public FileCategory Detect(string fileNameOrExtension)
    {
        var ext = NormalizeExtension(fileNameOrExtension);
        if (ext.Length == 0) return FileCategory.Other;

        foreach (var rule in _settings.Current.CustomRules)
        {
            if (string.Equals(NormalizeExtension(rule.Extension), ext, StringComparison.Ordinal))
                return rule.Category;
        }
        return BuiltIn.TryGetValue(ext, out var cat) ? cat : FileCategory.Other;
    }

    public bool IsTemporary(string fileNameOrExtension) =>
        TemporaryExtensions.Contains(NormalizeExtension(fileNameOrExtension));

    private static Dictionary<string, FileCategory> Build()
    {
        var map = new Dictionary<string, FileCategory>(StringComparer.Ordinal);
        void Add(FileCategory c, string list)
        {
            foreach (var e in list.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                map[e] = c;
        }
        Add(FileCategory.Audio, "mp3 wav flac aac m4a ogg opus wma aiff alac");
        Add(FileCategory.Video, "mp4 mkv avi mov wmv flv webm m4v mpeg mpg ts");
        Add(FileCategory.Images, "jpg jpeg png webp gif bmp tiff svg ico heic avif");
        Add(FileCategory.Documents, "pdf doc docx xls xlsx ppt pptx txt rtf odt csv md");
        Add(FileCategory.Archives, "zip rar 7z tar gz bz2 xz iso");
        Add(FileCategory.Programs, "exe msi msix appx bat cmd ps1");
        Add(FileCategory.Models3D, "blend fbx obj stl glb gltf 3ds dae");
        return map;
    }
}
