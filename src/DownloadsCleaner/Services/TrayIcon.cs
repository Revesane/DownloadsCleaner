using System.Windows;
using DownloadsCleaner.Localization;
using Forms = System.Windows.Forms;

namespace DownloadsCleaner.Services;

/// <summary>
/// Значок в области уведомлений (у часов). Клик левой кнопкой - вернуть окно,
/// правой - меню: открыть, сканировать, выход. Подписи меняются вместе с языком.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Forms.ToolStripMenuItem _open = new();
    private readonly Forms.ToolStripMenuItem _scan = new();
    private readonly Forms.ToolStripMenuItem _exit = new();

    public event Action? OpenRequested;
    public event Action? ScanRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _open.Font = new System.Drawing.Font(_open.Font, System.Drawing.FontStyle.Bold);   // действие по умолчанию
        _open.Click += (_, _) => OpenRequested?.Invoke();
        _scan.Click += (_, _) => ScanRequested?.Invoke();
        _exit.Click += (_, _) => ExitRequested?.Invoke();
        _menu.Items.AddRange(new Forms.ToolStripItem[] { _open, _scan, new Forms.ToolStripSeparator(), _exit });

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke();
        };
        _icon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke();

        Loc.LanguageChanged += OnLanguageChanged;
        RefreshText();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(RefreshText);

    private void RefreshText()
    {
        _icon.Text = "Downloads Cleaner";
        _open.Text = Loc.T("TrayOpen");
        _scan.Text = Loc.T("TrayScan");
        _exit.Text = Loc.T("TrayExit");
    }

    /// <summary>Всплывающее сообщение у значка (например, «программа работает в фоне»).</summary>
    public void ShowHint(string title, string text) =>
        _icon.ShowBalloonTip(6000, title, text, Forms.ToolTipIcon.Info);

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
            if (stream != null)
                using (stream)
                    return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        }
        catch { /* ниже - запасной значок */ }
        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        Loc.LanguageChanged -= OnLanguageChanged;
        // Без явного скрытия значок «висит» у часов, пока на него не наведут мышь.
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
