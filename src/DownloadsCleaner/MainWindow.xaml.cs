using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using DownloadsCleaner.Controls;
using DownloadsCleaner.Localization;
using DownloadsCleaner.Services;
using DownloadsCleaner.ViewModels;

namespace DownloadsCleaner;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => UpdateWindowState();
        SourceInitialized += (_, _) => ApplyRoundedCorners();
        Loaded += (_, _) => ScheduleTourOnStartup();
        Tour.Finished += OnTourFinished;
        PageHost.SizeChanged += (_, _) => Tour.Refresh();
    }

    // ------------------------------------------------------------------ Тур по программе

    private ShellViewModel? ShellVm => DataContext as ShellViewModel;
    private static ISettingsService? Settings => App.Services.GetService(typeof(ISettingsService)) as ISettingsService;

    private static Version CurrentVersion =>
        (App.Services.GetService(typeof(IUpdateService)) as IUpdateService)?.CurrentVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);

    /// <summary>Тур сам открывается при первом запуске и после обновления программы.</summary>
    private void ScheduleTourOnStartup()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (TourContent.ShouldShow(Settings?.Current.LastTourVersion, CurrentVersion)) StartTour();
        };
        timer.Start();
    }

    private void OnHelp(object sender, RoutedEventArgs e) => StartTour();

    private void StartTour()
    {
        if (ShellVm == null || Tour.IsOpen) return;
        if (ShellVm.Dialogs.Current is { } dialog) dialog.CancelCommand.Execute(null);
        Tour.Start(BuildTourSteps());
    }

    /// <summary>Пройден или пропущен - для этой версии больше не показываем.</summary>
    private void OnTourFinished()
    {
        if (Settings is { } s) s.Current.LastTourVersion = CurrentVersion.ToString(3);
        Navigate(NavKind.Home);
    }

    private void Navigate(NavKind kind)
    {
        if (ShellVm?.NavItems.FirstOrDefault(n => n.Kind == kind) is { } item) ShellVm.SelectedNav = item;
    }

    private IReadOnlyList<TourStep> BuildTourSteps()
    {
        static Rect? Of(FrameworkElement? el, FrameworkElement overlay) =>
            el is { IsVisible: true, ActualWidth: > 0, ActualHeight: > 0 }
                ? el.TransformToVisual(overlay).TransformBounds(new Rect(el.RenderSize))
                : null;

        Rect? Nav(NavKind kind, FrameworkElement overlay) =>
            ShellVm?.NavItems.FirstOrDefault(n => n.Kind == kind) is { } item
                ? Of(NavList.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement, overlay)
                : null;

        // Все пункты категорий одной областью: от «Все файлы» до «Прочее».
        Rect? Categories(FrameworkElement overlay)
        {
            Rect? union = null;
            foreach (var kind in new[] { NavKind.All, NavKind.Audio, NavKind.Video, NavKind.Images, NavKind.Documents,
                         NavKind.Archives, NavKind.Programs, NavKind.Models3D, NavKind.Other })
            {
                if (Nav(kind, overlay) is not { } r) continue;
                union = union is { } u ? Rect.Union(u, r) : r;
            }
            return union;
        }

        return new[]
        {
            new TourStep { Key = "Welcome", Glyph = "", Prepare = () => Navigate(NavKind.Home) },
            new TourStep { Key = "Home", Glyph = "", Target = o => Of(PageHost, o), Prepare = () => Navigate(NavKind.Home) },
            new TourStep { Key = "Scan", Glyph = "", Target = o => Of(ScanButton, o) },
            new TourStep { Key = "Categories", Glyph = "", Target = Categories, Prepare = () => Navigate(NavKind.All) },
            new TourStep { Key = "Sort", Glyph = "", Target = o => Of(SortButton, o) },
            new TourStep { Key = "Analytics", Glyph = "", Target = o => Nav(NavKind.Analytics, o), Prepare = () => Navigate(NavKind.Analytics) },
            new TourStep { Key = "Tips", Glyph = "", Target = o => Nav(NavKind.Tips, o), Prepare = () => Navigate(NavKind.Tips) },
            new TourStep { Key = "Duplicates", Glyph = "", Target = o => Nav(NavKind.Duplicates, o), Prepare = () => Navigate(NavKind.Duplicates) },
            new TourStep { Key = "History", Glyph = "", Target = o => Nav(NavKind.History, o), Prepare = () => Navigate(NavKind.History) },
            new TourStep { Key = "Settings", Glyph = "", Target = o => Nav(NavKind.Settings, o), Prepare = () => Navigate(NavKind.Settings) },
            new TourStep { Key = "Help", Glyph = "", Target = o => Of(HelpButton, o), Prepare = () => Navigate(NavKind.Home) }
        };
    }

    private void UpdateWindowState()
    {
        if (WindowState == WindowState.Maximized)
        {
            // Окно с WindowChrome при разворачивании выходит за края экрана на толщину рамки.
            RootGrid.Margin = SystemParameters.WindowResizeBorderThickness;
            MaxButton.Content = "";
            MaxButton.ToolTip = Loc.T("CommonRestore");
        }
        else
        {
            RootGrid.Margin = new Thickness(0);
            MaxButton.Content = "";
            MaxButton.ToolTip = Loc.T("CommonMaximize");
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void OnClose(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    /// <summary>Клавиши тура (когда он открыт); Esc закрывает текущий диалог.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Tour.IsOpen)
        {
            e.Handled = Tour.HandleKey(e.Key);
            return;
        }
        if (e.Key == Key.Escape && DataContext is ShellViewModel { Dialogs.Current: { } dialog })
        {
            dialog.CancelCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ---- Скруглённые углы окна в Windows 11 (на Windows 10 вызов просто игнорируется) ----

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void ApplyRoundedCorners()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
        catch { /* необязательное украшение */ }
    }
}
