using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DownloadsCleaner.ViewModels;

namespace DownloadsCleaner.Views;

/// <summary>
/// Панель предпросмотра. Воспроизведение (MediaElement) - чисто «видовая» логика, поэтому
/// находится здесь. Таймер прогресса работает только пока играет файл.
/// </summary>
public partial class PreviewPanel : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private PreviewViewModel? _vm;
    private bool _playing;
    private bool _seeking;

    public PreviewPanel()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => UpdateProgress();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => StopMedia();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
        _vm = e.NewValue as PreviewViewModel;
        if (_vm != null) _vm.PropertyChanged += OnVmChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PreviewViewModel.MediaUri)) return;
        StopMedia();
        if (_vm?.MediaUri == null) return;
        try
        {
            Player.Source = _vm.MediaUri;
            MediaControls.Visibility = Visibility.Visible;
            Seek.Value = 0;
            TimeText.Text = "0:00";
            Player.Play();                 // автоматический запуск при открытии панели не нужен - ставим на паузу после загрузки
            Player.Pause();
            SetPlaying(false);
        }
        catch
        {
            _vm.ReportMediaFailed();
        }
    }

    /// <summary>Освобождает файл: без этого Windows не даст удалить или переместить воспроизводимый файл.</summary>
    private void StopMedia()
    {
        _timer.Stop();
        _playing = false;
        try
        {
            Player.Stop();
            Player.Close();
            Player.Source = null;
        }
        catch { /* ignore */ }
        MediaControls.Visibility = Visibility.Collapsed;
        PlayButton.Content = "";
    }

    private void SetPlaying(bool playing)
    {
        _playing = playing;
        PlayButton.Content = playing ? "" : "";
        if (playing) _timer.Start(); else _timer.Stop();
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (Player.Source == null) return;
        if (_playing) { Player.Pause(); SetPlaying(false); }
        else { Player.Play(); SetPlaying(true); }
    }

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        Seek.Maximum = Player.NaturalDuration.HasTimeSpan ? Player.NaturalDuration.TimeSpan.TotalSeconds : 1;
        UpdateProgress();
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        Player.Stop();
        SetPlaying(false);
        Seek.Value = 0;
    }

    private void OnMediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        StopMedia();
        _vm?.ReportMediaFailed();
    }

    private void OnSeekStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e) => _seeking = true;

    private void OnSeekCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _seeking = false;
        Player.Position = TimeSpan.FromSeconds(Seek.Value);
    }

    private void UpdateProgress()
    {
        if (_seeking) return;
        var pos = Player.Position;
        Seek.Value = Math.Min(Seek.Maximum, pos.TotalSeconds);
        TimeText.Text = $"{(int)pos.TotalMinutes}:{pos.Seconds:00}";
    }
}
