using DownloadsCleaner.Helpers;

namespace DownloadsCleaner.Services;

public interface ILogService
{
    string LogDirectory { get; }
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}

/// <summary>Простой потокобезопасный файловый логгер (ротация по дням, хранение 14 дней).</summary>
public sealed class FileLogService : ILogService
{
    private readonly object _gate = new();
    public string LogDirectory { get; }

    public FileLogService(string? directory = null)
    {
        LogDirectory = directory ?? Path.Combine(KnownFolders.LocalAppData, "logs");
        try
        {
            Directory.CreateDirectory(LogDirectory);
            foreach (var old in Directory.EnumerateFiles(LogDirectory, "app-*.log"))
                if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-14)) File.Delete(old);
        }
        catch { /* логирование не должно ломать приложение */ }
    }

    public void Info(string message) => Write("INFO ", message);
    public void Warn(string message) => Write("WARN ", message);
    public void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}{Environment.NewLine}{ex}");

    private void Write(string level, string message)
    {
        try
        {
            var path = Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            lock (_gate) File.AppendAllText(path, line);
        }
        catch { /* игнорируем */ }
    }
}

public sealed class NullLogService : ILogService
{
    public static readonly NullLogService Instance = new();
    public string LogDirectory => string.Empty;
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? ex = null) { }
}
