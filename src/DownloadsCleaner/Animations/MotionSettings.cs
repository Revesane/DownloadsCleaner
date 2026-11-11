using System.Windows;

namespace DownloadsCleaner.Animations;

/// <summary>
/// Единая точка управления анимациями. Анимации включены, только если их не отключил пользователь
/// и Windows не просит уменьшить движение (параметр «Показывать анимации в Windows»).
/// Когда анимации выключены, все длительности равны нулю и состояния применяются мгновенно.
/// </summary>
public static class MotionSettings
{
    private static bool _userEnabled = true;

    static MotionSettings()
    {
        try
        {
            SystemParameters.StaticPropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
                    Changed?.Invoke(null, EventArgs.Empty);
            };
        }
        catch { /* вне UI-среды (тесты) */ }
    }

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

    /// <summary>true, если Windows не просит отключить анимации (Reduce Motion).</summary>
    public static bool SystemAllows
    {
        get
        {
            try { return SystemParameters.ClientAreaAnimation; }
            catch { return true; }
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
