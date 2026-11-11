namespace DownloadsCleaner.Models;

/// <summary>Категория файла, определяемая по расширению.</summary>
public enum FileCategory
{
    Audio,
    Video,
    Images,
    Documents,
    Archives,
    Programs,
    Models3D,
    Other
}

/// <summary>Отображаемые данные о категориях (названия, имена папок, значки, цвета).</summary>
public static class FileCategoryInfo
{
    public static IReadOnlyList<FileCategory> All { get; } = Enum.GetValues<FileCategory>();

    public static string Title(FileCategory c) => Localization.Loc.T(Key(c));

    /// <summary>
    /// Имя папки, в которую физически сортируются файлы категории.
    /// Не зависит от языка интерфейса: имена папок - это данные, и после смены
    /// языка уже отсортированные файлы не должны оказаться в «другой» папке.
    /// </summary>
    public static string FolderName(FileCategory c) => c switch
    {
        FileCategory.Audio => "Музыка",
        FileCategory.Video => "Видео",
        FileCategory.Images => "Изображения",
        FileCategory.Documents => "Документы",
        FileCategory.Archives => "Архивы",
        FileCategory.Programs => "Программы",
        FileCategory.Models3D => "3D-модели",
        _ => "Прочее"
    };

    /// <summary>Ключ перевода для отображаемого названия категории.</summary>
    public static string Key(FileCategory c) => c switch
    {
        FileCategory.Audio => "NavAudio",
        FileCategory.Video => "NavVideo",
        FileCategory.Images => "NavImages",
        FileCategory.Documents => "NavDocuments",
        FileCategory.Archives => "NavArchives",
        FileCategory.Programs => "NavPrograms",
        FileCategory.Models3D => "NavModels3D",
        _ => "NavOther"
    };

    /// <summary>Глиф Segoe Fluent Icons / Segoe MDL2 Assets.</summary>
    public static string Glyph(FileCategory c) => c switch
    {
        FileCategory.Audio => "",
        FileCategory.Video => "",
        FileCategory.Images => "",
        FileCategory.Documents => "",
        FileCategory.Archives => "",
        FileCategory.Programs => "",
        FileCategory.Models3D => "",
        _ => ""
    };

    public static string ColorHex(FileCategory c) => c switch
    {
        FileCategory.Audio => "#B794F6",
        FileCategory.Video => "#8B9CF8",
        FileCategory.Images => "#60A5FA",
        FileCategory.Documents => "#5EC8E8",
        FileCategory.Archives => "#C58AF9",
        FileCategory.Programs => "#7C83FD",
        FileCategory.Models3D => "#6EE7C8",
        _ => "#94A3B8"
    };
}
