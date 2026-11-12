using Microsoft.Win32;

namespace DownloadsCleaner.Services;

/// <summary>
/// Автозапуск через HKCU\...\Run (без прав администратора, только для текущего пользователя).
/// Программа стартует с ключом --minimized - сразу в трей, без окна.
/// </summary>
public sealed class RegistryAutostartService : IAutostartService
{
    internal const string ValueName = "DownloadsCleaner";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string _keyPath;
    private readonly string? _exePath;
    private readonly ILogService _log;

    public RegistryAutostartService(ILogService? log = null) : this(RunKey, Environment.ProcessPath, log) { }

    internal RegistryAutostartService(string keyPath, string? exePath, ILogService? log = null)
    {
        _keyPath = keyPath;
        _exePath = exePath;
        _log = log ?? NullLogService.Instance;
    }

    public bool IsAvailable =>
        !string.IsNullOrEmpty(_exePath)
        && string.Equals(Path.GetFileName(_exePath), "DownloadsCleaner.exe", StringComparison.OrdinalIgnoreCase)
        && !File.Exists(Path.Combine(Path.GetDirectoryName(_exePath)!, "DownloadsCleaner.dll"));

    /// <summary>Команда в реестре: путь в кавычках (в нём бывают пробелы) и запуск в трей.</summary>
    internal string Command => $"\"{_exePath}\" --minimized";

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
                // Запись от другой копии программы (старая папка) не считается.
                return key?.GetValue(ValueName) is string value && _exePath != null
                       && value.Contains(_exePath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (!IsAvailable) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(_keyPath);
            if (enabled) key.SetValue(ValueName, Command);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            _log.Info($"Автозапуск {(enabled ? "включён" : "выключен")}");
        }
        catch (Exception ex)
        {
            _log.Warn($"Не удалось изменить автозапуск: {ex.Message}");
        }
    }
}
