using System.Security;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.Mac.Services;

/// <summary>
/// Открывать программу при входе в macOS: LaunchAgent в ~/Library/LaunchAgents, который
/// запускает пакет .app через `open -a` (так же, как двойной щелчок в Finder).
/// Доступно только для собранного .app, не для запуска из исходников.
/// </summary>
public sealed class MacAutostartService : IAutostartService
{
    internal const string Label = "com.revesane.downloadscleaner";
    private readonly string _plistPath;
    private readonly string? _exePath;
    private readonly ILogService _log;

    public MacAutostartService(ILogService? log = null)
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", Label + ".plist"),
            Environment.ProcessPath, log) { }

    internal MacAutostartService(string plistPath, string? exePath, ILogService? log = null)
    {
        _plistPath = plistPath;
        _exePath = exePath;
        _log = log ?? NullLogService.Instance;
    }

    /// <summary>Путь к пакету ".../Downloads Cleaner.app" или null, если программа запущена не из пакета.</summary>
    internal string? BundlePath
    {
        get
        {
            if (string.IsNullOrEmpty(_exePath)) return null;
            var i = _exePath.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal);
            return i < 0 ? null : _exePath[..(i + 4)];
        }
    }

    public bool IsAvailable => BundlePath != null;

    public bool IsEnabled
    {
        get
        {
            try
            {
                return BundlePath is { } bundle && File.Exists(_plistPath)
                       && File.ReadAllText(_plistPath).Contains(SecurityElement.Escape(bundle), StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (BundlePath is not { } bundle) return;
        try
        {
            if (enabled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_plistPath)!);
                File.WriteAllText(_plistPath, BuildPlist(bundle));
            }
            else if (File.Exists(_plistPath))
            {
                File.Delete(_plistPath);
            }
            _log.Info($"Открытие при входе {(enabled ? "включено" : "выключено")}");
        }
        catch (Exception ex)
        {
            _log.Warn($"Не удалось изменить открытие при входе: {ex.Message}");
        }
    }

    internal static string BuildPlist(string bundlePath) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>{Label}</string>
            <key>ProgramArguments</key>
            <array>
                <string>/usr/bin/open</string>
                <string>-a</string>
                <string>{SecurityElement.Escape(bundlePath)}</string>
            </array>
            <key>RunAtLoad</key>
            <true/>
        </dict>
        </plist>
        """;
}
