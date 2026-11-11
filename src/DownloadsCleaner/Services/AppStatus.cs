using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadsCleaner.Animations;
using DownloadsCleaner.Localization;

namespace DownloadsCleaner.Services;

/// <summary>Состояния приложения, каждое из которых визуально отличается.</summary>
public enum AppState
{
    Launched,
    Scanning,
    ScanComplete,
    Ready,
    Selection,
    Sorting,
    SortComplete,
    Cleaning,
    CleanComplete,
    Error
}

/// <summary>Общее состояние приложения: текущая операция, прогресс, возможность отмены.</summary>
public sealed partial class AppStatus : ObservableObject
{
    private CancellationTokenSource? _cts;
    private int _version;
    /// <summary>Текущий заголовок - служебная подпись «Готово», которую нужно переводить при смене языка.</summary>
    private bool _idleTitle = true;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplayState), nameof(DisplayTitle), nameof(IsBusy), nameof(BannerVisible))]
    private AppState _state = AppState.Launched;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplayState), nameof(DisplayTitle))]
    private int _selectedCount;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplayTitle))]
    private string _title = Loc.T("StatusReadyToWork");

    [ObservableProperty] private string _detail = string.Empty;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private bool _bannerDismissed;
    /// <summary>Живой счётчик найденных файлов во время сканирования.</summary>
    [ObservableProperty] private long _liveCount;
    [ObservableProperty] private long _liveBytes;

    public bool IsBusy => State is AppState.Scanning or AppState.Sorting or AppState.Cleaning;

    public bool BannerVisible => !BannerDismissed &&
        State is AppState.Scanning or AppState.Sorting or AppState.Cleaning
            or AppState.ScanComplete or AppState.SortComplete or AppState.CleanComplete or AppState.Error;

    /// <summary>Состояние для индикатора: выбор файлов показывается, когда нет другой операции.</summary>
    public AppState DisplayState
    {
        get
        {
            // Важно: приоритет оператора 'is' выше, чем у '&&', поэтому условие
            // разбито явно - иначе оно читается не так, как выглядит.
            var idle = State is AppState.Ready or AppState.Launched or AppState.ScanComplete;
            return idle && SelectedCount > 0 ? AppState.Selection : State;
        }
    }

    public string DisplayTitle => DisplayState == AppState.Selection
        ? Loc.F("StatusSelected", SelectedCount.ToString("N0"))
        : Title;

    public bool CanCancel => _cts != null && IsBusy;

    partial void OnBannerDismissedChanged(bool value) => OnPropertyChanged(nameof(BannerVisible));

    /// <summary>Запускает длительную операцию. Возвращает токен отмены.</summary>
    public CancellationToken Begin(AppState state, string title, string detail = "", bool indeterminate = false)
    {
        _version++;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        BannerDismissed = false;
        State = state;
        _idleTitle = false;
        Title = title;
        Detail = detail;
        Progress = 0;
        IsIndeterminate = indeterminate;
        OnPropertyChanged(nameof(CanCancel));
        CancelCommand.NotifyCanExecuteChanged();
        return _cts.Token;
    }

    public void Report(double fraction, string? detail = null)
    {
        Progress = Math.Clamp(fraction, 0, 1);
        if (detail != null) Detail = detail;
    }

    /// <summary>Завершает операцию и на несколько секунд показывает итоговое состояние.</summary>
    public async void Finish(AppState state, string title, string detail = "")
    {
        var v = ++_version;
        _cts?.Dispose();
        _cts = null;
        BannerDismissed = false;
        State = state;
        _idleTitle = false;
        Title = title;
        Detail = detail;
        IsIndeterminate = false;
        Progress = state == AppState.Error ? Progress : 1;
        OnPropertyChanged(nameof(CanCancel));
        CancelCommand.NotifyCanExecuteChanged();

        var hold = state == AppState.Error ? 9000 : 4500;
        await Task.Delay(hold);
        if (v != _version) return;       // за это время началась другая операция

        State = AppState.Ready;
        _idleTitle = true;
        Title = Loc.T("StatusDone");
        Detail = string.Empty;
    }

    /// <summary>Перечитывает служебные подписи после смены языка интерфейса.</summary>
    public void RefreshLocalization()
    {
        if (_idleTitle)
            Title = Loc.T(State == AppState.Launched ? "StatusReadyToWork" : "StatusDone");
        OnPropertyChanged(nameof(DisplayTitle));
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void DismissBanner() => BannerDismissed = true;
}
