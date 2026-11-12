namespace DownloadsCleaner.Services;

/// <summary>Запуск программы вместе с системой.</summary>
public interface IAutostartService
{
    /// <summary>
    /// Можно ли включить автозапуск: это установленная копия программы, а не сборка из исходников
    /// (её exe-заглушка зависит от папки сборки и прописывать её в систему нельзя).
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Автозапуск включён именно для этой копии программы.</summary>
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);
}
