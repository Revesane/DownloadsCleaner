using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using DownloadsCleaner.Localization;
using DownloadsCleaner.ViewModels;

namespace DownloadsCleaner;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => UpdateWindowState();
        SourceInitialized += (_, _) => ApplyRoundedCorners();
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

    /// <summary>Esc закрывает текущий диалог.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
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
