namespace DownloadsCleaner.Services;

/// <summary>Найденное обновление: версия, описание выпуска и откуда его скачать.</summary>
/// <param name="AssetUrl">Архив с программой для Windows или null, если в выпуске его нет.</param>
public sealed record UpdateInfo(Version Version, string Notes, string PageUrl, string? AssetUrl, long AssetSize);

/// <summary>Проверка и установка обновлений программы.</summary>
public interface IUpdateService
{
    Version CurrentVersion { get; }

    /// <summary>
    /// Может ли программа обновить себя сама: это опубликованный exe (а не сборка для разработки),
    /// и в его папку можно писать. Иначе остаётся открыть страницу выпуска в браузере.
    /// </summary>
    bool CanInstall { get; }

    /// <summary>Новое обновление или null, если установлена последняя версия.</summary>
    Task<UpdateInfo?> CheckAsync(CancellationToken ct);

    /// <summary>Скачивает обновление, подменяет файл программы, запускает новую версию и закрывает текущую.</summary>
    Task InstallAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct);
}
