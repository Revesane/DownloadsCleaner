using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DownloadsCleaner.Helpers;

/// <summary>
/// Принудительно перечитывает все привязки в визуальном дереве.
/// Нужно после смены темы: конвертеры (IValueConverter) не получают уведомлений
/// о замене словаря ресурсов и продолжают отдавать цвета предыдущей темы.
/// </summary>
public static class BindingRefresh
{
    public static void Refresh(DependencyObject? root)
    {
        if (root == null) return;

        if (root is FrameworkElement fe)
        {
            var enumerator = fe.GetLocalValueEnumerator();
            while (enumerator.MoveNext())
            {
                var entry = enumerator.Current;
                if (entry.Property is not DependencyProperty dp) continue;
                var expression = BindingOperations.GetBindingExpression(fe, dp);
                expression?.UpdateTarget();
            }
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            Refresh(VisualTreeHelper.GetChild(root, i));
    }
}
