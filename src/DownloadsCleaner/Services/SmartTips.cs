using DownloadsCleaner.Models;

namespace DownloadsCleaner.Services;

/// <summary>Почему файл можно удалить без потерь.</summary>
public enum TipKind
{
    /// <summary>Архив уже распакован: все его файлы лежат рядом в распакованной папке.</summary>
    ExtractedArchive,
    /// <summary>Установщик программы, которая уже установлена (той же или более новой версии).</summary>
    InstalledProgram,
    /// <summary>Есть более новая версия того же файла.</summary>
    OldVersion
}

/// <summary>Совет: файл, который можно убрать, и доказательство этого.</summary>
/// <param name="Related">
/// ExtractedArchive - папка с распакованной копией; InstalledProgram - название и версия программы;
/// OldVersion - путь к более новой версии.
/// </param>
public sealed record SmartTip(TipKind Kind, FileEntry File, string Related);

/// <summary>Ищет файлы, которые можно удалить без потерь. Ничего не изменяет на диске.</summary>
public interface ISmartTipsService
{
    /// <param name="downloadsRoot">Папка загрузок: файлы в её корне и в папках категорий считаются скачанными.</param>
    Task<IReadOnlyList<SmartTip>> FindAsync(IReadOnlyList<FileEntry> files, string downloadsRoot, CancellationToken ct);

    /// <summary>
    /// Перепроверяет совет прямо перед удалением: распакованная копия ещё на месте,
    /// более новая версия не удалена. Иначе удаление лишило бы пользователя последней копии.
    /// </summary>
    bool StillValid(SmartTip tip);
}
