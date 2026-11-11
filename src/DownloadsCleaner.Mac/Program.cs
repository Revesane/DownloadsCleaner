using Avalonia;

namespace DownloadsCleaner.Mac;

/// <summary>Точка входа приложения для macOS (Avalonia).</summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    /// <summary>Сборка приложения используется и при запуске в Visual Studio (дизайнер).</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
