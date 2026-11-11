using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DownloadsCleaner.ViewModels;

namespace DownloadsCleaner.Views;

public partial class FilesPage : UserControl
{
    public FilesPage() => InitializeComponent();

    private FileListViewModel? Vm => DataContext as FileListViewModel;

    /// <summary>
    /// Ctrl+A выбирает все видимые файлы через ViewModel (контейнеры виртуализированного списка
    /// создаются не для всех строк), Delete удаляет выбранные, Enter открывает файл, Пробел - предпросмотр.
    /// </summary>
    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            vm.SelectAllCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && vm.HasSelection)
        {
            vm.DeleteSelectedCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && vm.HasSelection)
        {
            vm.ClearSelectionCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && Keyboard.FocusedElement is ListBoxItem { DataContext: FileItemViewModel item })
        {
            vm.PreviewItemCommand.Execute(item);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.FocusedElement is ListBoxItem { DataContext: FileItemViewModel open })
        {
            vm.OpenFileCommand.Execute(open);
            e.Handled = true;
        }
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Vm is not { } vm || e.OriginalSource is not DependencyObject source) return;
        // Двойной щелчок по чекбоксу или кнопке не открывает файл.
        for (DependencyObject? d = source; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is ButtonBase) return;
            if (d is ListBoxItem { DataContext: FileItemViewModel item })
            {
                vm.OpenFileCommand.Execute(item);
                return;
            }
        }
    }
}
