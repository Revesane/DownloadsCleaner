using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using DownloadsCleaner.Helpers;
using DownloadsCleaner.Models;

namespace DownloadsCleaner.Services;

public interface ISettingsService
{
    AppSettings Current { get; }
    string FilePath { get; }
    void Load();
    /// <summary>Немедленно записывает настройки на диск.</summary>
    void Save();
}

/// <summary>Хранит настройки в %AppData%\DownloadsCleaner\settings.json. Изменения сохраняются автоматически.</summary>
public sealed class SettingsService : ISettingsService, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ILogService _log;
    private readonly object _gate = new();
    private Timer? _debounce;
    private bool _disposed;

    public string FilePath { get; }
    public AppSettings Current { get; private set; } = new();

    public SettingsService(string? filePath = null, ILogService? log = null)
    {
        FilePath = filePath ?? Path.Combine(KnownFolders.AppData, "settings.json");
        _log = log ?? NullLogService.Instance;
        Load();
    }

    public void Load()
    {
        AppSettings? loaded = null;
        try
        {
            if (File.Exists(FilePath))
                loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
        }
        catch (Exception ex)
        {
            _log.Error("Не удалось прочитать настройки, используются значения по умолчанию", ex);
            try { File.Copy(FilePath, FilePath + ".broken", true); } catch { /* ignore */ }
        }

        Current = loaded ?? new AppSettings();
        if (string.IsNullOrWhiteSpace(Current.DownloadsPath))
            Current.DownloadsPath = KnownFolders.Downloads;
        if (Current.OldFilesDays <= 0) Current.OldFilesDays = 30;
        if (Current.ToastSeconds <= 0) Current.ToastSeconds = 5;
        Attach(Current);
    }

    private void Attach(AppSettings s)
    {
        s.PropertyChanged += OnChanged;
        Hook(s.ExcludedExtensions);
        Hook(s.ExcludedFolders);
        Hook(s.CustomRules);
        foreach (var r in s.CustomRules) r.PropertyChanged += OnChanged;
    }

    private void Hook(INotifyCollectionChanged c) => c.CollectionChanged += (_, e) =>
    {
        if (e.NewItems != null)
            foreach (var item in e.NewItems.OfType<INotifyPropertyChanged>()) item.PropertyChanged += OnChanged;
        ScheduleSave();
    };

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => ScheduleSave();

    private void ScheduleSave()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _debounce ??= new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
            _debounce.Change(400, Timeout.Infinite);
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions));
                File.Move(tmp, FilePath, true);
            }
            catch (Exception ex)
            {
                _log.Error("Не удалось сохранить настройки", ex);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _debounce?.Dispose();
        }
        Save();
    }
}
