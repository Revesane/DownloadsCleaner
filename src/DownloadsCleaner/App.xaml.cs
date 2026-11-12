using System.Windows;
using System.Windows.Threading;
using DownloadsCleaner.Animations;
using DownloadsCleaner.Helpers;
using DownloadsCleaner.Localization;
using DownloadsCleaner.Services;
using DownloadsCleaner.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DownloadsCleaner;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private bool _ownsMutex;
    private ServiceProvider? _services;
    private ILogService? _log;

    public static IServiceProvider Services => ((App)Current)._services!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // После самообновления: дождаться закрытия прежней версии и убрать её файл (.old).
        GitHubUpdateService.HandleStartup(e.Args);

        // Мьютекс проверяем через WaitOne: конструктор Mutex(owned: true) не запрашивает
        // владение, и после аварийного завершения прошлого экземпляра приложение
        // не смогло бы запуститься.
        _singleInstance = new Mutex(false, "DownloadsCleaner.SingleInstance");
        try
        {
            _ownsMutex = _singleInstance.WaitOne(0, false);
        }
        catch (AbandonedMutexException)
        {
            // Предыдущий экземпляр упал - мьютекс освобождён и принадлежит нам.
            _ownsMutex = true;
        }

        if (!_ownsMutex)
        {
            MessageBox.Show(Loc.T("AppAlreadyRunning"), "Downloads Cleaner", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _log?.Error("Необработанная ошибка задачи", args.Exception);
            args.SetObserved();
        };

        var sc = new ServiceCollection();
        ConfigureServices(sc);
        _services = sc.BuildServiceProvider();
        _log = _services.GetRequiredService<ILogService>();
        _log.Info("Запуск приложения");

        var settings = _services.GetRequiredService<ISettingsService>();
        // Язык применяем до создания моделей представлений: часть подписей
        // формируется в их конструкторах.
        Loc.SetLanguage(settings.Current.Language);
        _services.GetRequiredService<IThemeService>().Apply(settings.Current.Theme);
        MotionSettings.UserEnabled = settings.Current.AnimationsEnabled;

        var shell = _services.GetRequiredService<ShellViewModel>();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        _ = shell.InitializeAsync();
    }

    private static void ConfigureServices(IServiceCollection s)
    {
        s.AddSingleton<ILogService, FileLogService>();
        s.AddSingleton<ISettingsService>(sp => new SettingsService(null, sp.GetRequiredService<ILogService>()));
        s.AddSingleton<IThemeService, ThemeService>();
        s.AddSingleton<IFileTypeDetectionService, FileTypeDetectionService>();
        s.AddSingleton<IDownloadSourceReader, ZoneIdentifierSourceReader>();
        s.AddSingleton<IFileScannerService, FileScannerService>();
        s.AddSingleton<IFileOrganizationService, FileOrganizationService>();
        s.AddSingleton<IRecycleBinQuota, WindowsRecycleBinQuota>();
        s.AddSingleton<IRecycleBin>(sp => new WindowsRecycleBin(sp.GetRequiredService<IRecycleBinQuota>()));
        s.AddSingleton<IFileDeletionService, FileDeletionService>();
        s.AddSingleton<IDuplicateDetectionService, DuplicateDetectionService>();
        s.AddSingleton<IFileWatcherService, FileWatcherService>();
        s.AddSingleton<IHistoryService>(sp => new HistoryService(null, sp.GetRequiredService<ILogService>()));
        s.AddSingleton<IFileLibrary, FileLibrary>();
        s.AddSingleton<IToastService, ToastService>();
        s.AddSingleton<IDialogService, DialogService>();
        s.AddSingleton<INavigationService, NavigationService>();
        s.AddSingleton<IClipboardService>(_ => WindowsPlatformServices.Clipboard);
        s.AddSingleton<IFolderPickerService>(_ => WindowsPlatformServices.FolderPicker);
        s.AddSingleton<AppStatus>();
        s.AddSingleton<IOperationsCoordinator, OperationsCoordinator>();

        s.AddSingleton<PreviewViewModel>();
        s.AddSingleton<HomeViewModel>();
        s.AddSingleton<FileListViewModel>();
        s.AddSingleton<DuplicatesViewModel>();
        s.AddSingleton<HistoryViewModel>();
        s.AddSingleton<AnalyticsViewModel>();
        s.AddSingleton<SettingsViewModel>();
        s.AddSingleton<IArchiveLister, ZipArchiveLister>();
        s.AddSingleton<IInstalledProgramsProvider, RegistryInstalledPrograms>();
        s.AddSingleton<IInstallerInfoReader, FileVersionInstallerInfoReader>();
        s.AddSingleton<ISmartTipsService, SmartTipsService>();
        s.AddSingleton<SmartTipsViewModel>();
        s.AddSingleton<IUpdateService, GitHubUpdateService>();
        s.AddSingleton<UpdateViewModel>();
        s.AddSingleton<ShellViewModel>();
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("Необработанная ошибка интерфейса", e.Exception);
        try
        {
            _services?.GetService<IToastService>()?.Error(Loc.T("AppUnexpectedError"), e.Exception.Message);
        }
        catch { /* ignore */ }
        e.Handled = true;      // приложение продолжает работать, ошибка записана в журнал
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _services?.GetService<IFileWatcherService>()?.Dispose();
            _services?.Dispose();                 // SettingsService.Dispose сохраняет настройки
            if (_ownsMutex) _singleInstance?.ReleaseMutex();
            _singleInstance?.Dispose();
        }
        catch { /* ignore */ }
        base.OnExit(e);
    }
}
