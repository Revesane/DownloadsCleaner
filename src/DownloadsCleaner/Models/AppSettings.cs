using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DownloadsCleaner.Models;

public enum AppTheme
{
    Dark,
    Light,
    System
}

/// <summary>Язык интерфейса. Значения фиксированы: они сохраняются в settings.json по имени.</summary>
public enum AppLanguage
{
    Russian,
    English
}

/// <summary>Пользовательское правило: расширение -> категория (расширяет встроенный список).</summary>
public sealed partial class ExtensionRule : ObservableObject
{
    [ObservableProperty] private string _extension = string.Empty;
    [ObservableProperty] private FileCategory _category = FileCategory.Other;

    public ExtensionRule() { }

    public ExtensionRule(string extension, FileCategory category)
    {
        _extension = extension;
        _category = category;
    }
}

/// <summary>Настройки приложения. Сохраняются в JSON между запусками.</summary>
public sealed partial class AppSettings : ObservableObject
{
    [ObservableProperty] private string _downloadsPath = string.Empty;
    /// <summary>Куда физически сортировать файлы. Пусто = внутри папки загрузок.</summary>
    [ObservableProperty] private string _sortDestination = string.Empty;
    [ObservableProperty] private AppTheme _theme = AppTheme.Dark;
    [ObservableProperty] private AppLanguage _language = AppLanguage.Russian;
    [ObservableProperty] private bool _scanOnStartup = true;
    [ObservableProperty] private bool _includeSubfolders = true;
    [ObservableProperty] private bool _watchNewFiles = true;
    /// <summary>Автоматически сортировать новые файлы. По умолчанию выключено.</summary>
    [ObservableProperty] private bool _autoSortNewFiles;
    [ObservableProperty] private bool _showNotifications = true;
    [ObservableProperty] private bool _notifyOnNewFiles = true;
    [ObservableProperty] private int _toastSeconds = 5;
    [ObservableProperty] private int _oldFilesDays = 30;
    [ObservableProperty] private bool _animationsEnabled = true;
    [ObservableProperty] private bool _sidebarCollapsed;
    /// <summary>Разрешение на чтение содержимого файлов (нужно для SHA-256 при поиске дубликатов).</summary>
    [ObservableProperty] private bool _allowContentHashing;
    /// <summary>Показывать опцию безвозвратного удаления (по умолчанию - только Корзина).</summary>
    [ObservableProperty] private bool _allowPermanentDelete;

    public ObservableCollection<string> ExcludedExtensions { get; set; } = new();
    public ObservableCollection<string> ExcludedFolders { get; set; } = new();
    public ObservableCollection<ExtensionRule> CustomRules { get; set; } = new();
    /// <summary>Подсказки «Как это работает», которые пользователь закрыл кнопкой «Понятно».</summary>
    public ObservableCollection<string> DismissedHints { get; set; } = new();

    [JsonIgnore]
    public string EffectiveSortDestination =>
        string.IsNullOrWhiteSpace(SortDestination) ? DownloadsPath : SortDestination;
}
