namespace DownloadsCleaner.Services;

/// <summary>Копирование текста в буфер обмена.</summary>
public interface IClipboardService
{
    void SetText(string text);
}

/// <summary>Выбор папки в диалоге операционной системы.</summary>
public interface IFolderPickerService
{
    /// <summary>Возвращает выбранный путь или null, если пользователь отказался.</summary>
    Task<string?> PickFolderAsync(string title, string? initialDirectory);
}
