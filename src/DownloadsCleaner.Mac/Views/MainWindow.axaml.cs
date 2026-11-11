using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using DownloadsCleaner.Helpers;

namespace DownloadsCleaner.Mac.Views;

/// <summary>Главное окно приложения для macOS.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        MacWindowAccessor.Current = this;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
