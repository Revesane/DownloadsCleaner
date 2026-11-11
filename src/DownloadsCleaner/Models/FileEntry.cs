namespace DownloadsCleaner.Models;

/// <summary>Неизменяемое описание файла, найденного при сканировании.</summary>
/// <param name="Extension">Расширение в нижнем регистре без точки.</param>
public sealed record FileEntry(
    string FullPath,
    string Name,
    string Extension,
    long Size,
    DateTime Created,
    DateTime Modified,
    FileCategory Category,
    bool IsTemporary)
{
    public string Directory => Path.GetDirectoryName(FullPath) ?? string.Empty;

    /// <summary>Возвращает копию записи с новым путём (после перемещения).</summary>
    public FileEntry WithPath(string newPath, FileCategory category) => this with
    {
        FullPath = newPath,
        Name = Path.GetFileName(newPath),
        Category = category
    };
}
