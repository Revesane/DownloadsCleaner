using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DownloadsCleaner.Helpers;
using DownloadsCleaner.Localization;
using DownloadsCleaner.Mac.Services;
using DownloadsCleaner.Mac.Views;
using DownloadsCleaner.Services;
using DownloadsCleaner.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DownloadsCleaner.Mac;

/// <summary>Приложение для macOS: та же логика, что и в Windows-версии, но интерфейс на Avalonia.</summary>
public partial class App : Application
{
    private ServiceProvider? _services;

    public static IServiceProvider Services => ((App)Current)._services!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var sc = new ServiceCollection();
        ConfigureServices(sc);
        _services = sc.BuildServiceProvider();

        var log = _services.GetRequiredService<ILogService>();
        log.Info("Запуск приложения (macOS)");

        var settings = _services.GetRequiredService<ISettingsService>();
        // Язык применяем до создания моделей представлений: часть подписей
        // формируется в их конструкторах.
        Loc.SetLanguage(settings.Current.Language);
        _services.GetRequiredService<IThemeService>().Apply(settings.Current.Theme);
        DownloadsCleaner.Animations.MotionSettings.UserEnabled = settings.Current.AnimationsEnabled;

        var shell = _services.GetRequiredService<ShellViewModel>();
        var window = new MainWindow { DataContext = shell };
        window.Show();

        _ = shell.InitializeAsync();
        base.OnFrameworkInitializationCompleted();
    }

    private static void ConfigureServices(IServiceCollection s)
    {
        s.AddSingleton<ILogService, FileLogService>();
        s.AddSingleton<ISettingsService>(sp => new SettingsService(null, sp.GetRequiredService<ILogService>()));
        s.AddSingleton<IThemeService, MacThemeService>();
        s.AddSingleton<IFileTypeDetectionService, FileTypeDetectionService>();
        s.AddSingleton<IFileScannerService, FileScannerService>();
        s.AddSingleton<IFileOrganizationService, FileOrganizationService>();
        s.AddSingleton<IRecycleBinQuota, MacRecycleBinQuota>();
        s.AddSingleton<IRecycleBin>(sp => new MacRecycleBin(sp.GetRequiredService<IRecycleBinQuota>()));
        s.AddSingleton<IFileDeletionService, FileDeletionService>();
        s.AddSingleton<IDuplicateDetectionService, DuplicateDetectionService>();
        s.AddSingleton<IFileWatcherService, FileWatcherService>();
        s.AddSingleton<IHistoryService>(sp => new HistoryService(null, sp.GetRequiredService<ILogService>()));
        s.AddSingleton<IFileLibrary, FileLibrary>();
        s.AddSingleton<IToastService, ToastService>();
        s.AddSingleton<IDialogService, DialogService>();
        s.AddSingleton<INavigationService, NavigationService>();
        s.AddSingleton<IClipboardService>(_ => MacPlatformServices.Clipboard);
        s.AddSingleton<IFolderPickerService>(_ => MacPlatformServices.FolderPicker);
        s.AddSingleton<AppStatus>();
        s.AddSingleton<IOperationsCoordinator, OperationsCoordinator>();

        s.AddSingleton<PreviewViewModel>();
        s.AddSingleton<HomeViewModel>();
        s.AddSingleton<FileListViewModel>();
        s.AddSingleton<DuplicatesViewModel>();
        s.AddSingleton<HistoryViewModel>();
        s.AddSingleton<AnalyticsViewModel>();
        s.AddSingleton<SettingsViewModel>();
        s.AddSingleton<ShellViewModel>();
    }
}
