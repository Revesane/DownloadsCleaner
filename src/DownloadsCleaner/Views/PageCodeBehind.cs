using System.Windows.Controls;

namespace DownloadsCleaner.Views;

public partial class DuplicatesPage : UserControl
{
    public DuplicatesPage() => InitializeComponent();
}

public partial class HistoryPage : UserControl
{
    public HistoryPage() => InitializeComponent();
}

public partial class SettingsPage : UserControl
{
    public SettingsPage() => InitializeComponent();

    private void OnToggleRules(object sender, System.Windows.RoutedEventArgs e)
    {
        RulesPanel.IsExpanded = !RulesPanel.IsExpanded;
        RulesChevron.Text = RulesPanel.IsExpanded ? "" : "";
    }
}

public partial class AnalyticsPage : UserControl
{
    public AnalyticsPage() => InitializeComponent();
}
