using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DownloadsCleaner.Helpers;
using DownloadsCleaner.Models;

namespace DownloadsCleaner.ViewModels;

/// <summary>Строка списка файлов. Лёгкий объект: тяжёлые данные форматируются один раз.</summary>
public sealed partial class FileItemViewModel : ObservableObject
{
    private readonly Action<FileItemViewModel>? _selectionChanged;

    public FileItemViewModel(FileEntry entry, Action<FileItemViewModel>? selectionChanged)
    {
        Entry = entry;
        _selectionChanged = selectionChanged;
        CreatedText = entry.Created.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);
        ModifiedText = entry.Modified.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);
        ExtensionBadge = entry.Extension.Length == 0 ? "—" : entry.Extension.ToUpperInvariant();
    }

    public FileEntry Entry { get; }
    public string Name => Entry.Name;
    public string FullPath => Entry.FullPath;
    public string Folder => Entry.Directory;

    /// <summary>Сайт-источник с разделителем перед папкой ("github.com · ") или пустая строка.</summary>
    public string SourcePrefix => Entry.SourceHost is { } host ? host + " · " : string.Empty;

    /// <summary>Полный адрес загрузки для подсказки (null - подсказки нет).</summary>
    public string? SourceUrl => Entry.SourceUrl;
    public FileCategory Category => Entry.Category;
    public long Size => Entry.Size;
    public bool IsTemporary => Entry.IsTemporary;
    public string CreatedText { get; }
    public string ModifiedText { get; }

    /// <summary>Размер файла: единицы измерения зависят от языка интерфейса.</summary>
    public string SizeText => ByteFormatter.Format(Entry.Size);

    /// <summary>Название категории: пересчитывается при смене языка интерфейса.</summary>
    public string CategoryTitle => FileCategoryInfo.Title(Category);

    public string ExtensionBadge { get; }

    /// <summary>Обновляет переводимые подписи после смены языка.</summary>
    internal void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(CategoryTitle));
        OnPropertyChanged(nameof(SizeText));
    }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isRemoving;
    [ObservableProperty] private bool _isNew;

    partial void OnIsSelectedChanged(bool value) => _selectionChanged?.Invoke(this);
}
