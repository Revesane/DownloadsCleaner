using System.Diagnostics;

namespace DownloadsCleaner.Animations;

/// <summary>
/// Единая точка управления анимациями. Анимации включены, только если их не отключил пользователь
/// и система не просит уменьшить движение (в macOS - «Уменьшить движение» в настройках доступности).
/// Когда анимации выключены, все длительности равны нулю и состояния применяются мгновенно.
/// </summary>
public static class MotionSettings
{
    private static bool _userEnabled = true;

    public static event EventHandler? Changed;

    public static bool UserEnabled
    {
        get => _userEnabled;
        set
        {
            if (_userEnabled == value) return;
            _userEnabled = value;
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    /// <summary>true, если система не просит отключить анимации (Reduce Motion).</summary>
    public static bool SystemAllows
    {
        get
        {
            try
            {
                if (!OperatingSystem.IsMacOS()) return true;

                var psi = new ProcessStartInfo("/usr/bin/defaults", "read com.apple.universalaccess reduceMotion")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                using var proc = Process.Start(psi);
                if (proc == null) return true;
                var output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(2000);
                return !output.Equals("1", StringComparison.Ordinal);
            }
            catch
            {
                return true;
            }
        }
    }

    public static bool Enabled => _userEnabled && SystemAllows;

    private static TimeSpan Ms(int ms) => Enabled ? TimeSpan.FromMilliseconds(ms) : TimeSpan.Zero;

    public static TimeSpan Instant => TimeSpan.Zero;
    public static TimeSpan Fast => Ms(140);
    public static TimeSpan Normal => Ms(220);
    public static TimeSpan Slow => Ms(340);
    public static TimeSpan Counter => Ms(700);
    public static TimeSpan Custom(int ms) => Ms(ms);
}
