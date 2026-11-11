using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadsCleaner.Animations;
using DownloadsCleaner.Helpers;

namespace DownloadsCleaner.Services;

public enum ToastKind { Success, Info, Warning, Error }

public sealed partial class ToastViewModel : ObservableObject
{
    private readonly Action<ToastViewModel> _dismiss;
    private readonly Action? _action;

    public ToastViewModel(ToastKind kind, string title, string? message, string? actionText, Action? action, Action<ToastViewModel> dismiss)
    {
        Kind = kind;
        Title = title;
        Message = message ?? string.Empty;
        ActionText = actionText;
        _action = action;
        _dismiss = dismiss;
    }

    public ToastKind Kind { get; }
    public string Title { get; }
    public string Message { get; }
    public string? ActionText { get; }
    public bool HasMessage => Message.Length > 0;
    public bool HasAction => ActionText != null && _action != null;

    public string Glyph => Kind switch
    {
        ToastKind.Success => "",
        ToastKind.Warning => "",
        ToastKind.Error => "",
        _ => ""
    };

    [ObservableProperty] private bool _isClosing;

    [RelayCommand] private void Dismiss() => _dismiss(this);

    [RelayCommand]
    private void RunAction()
    {
        _action?.Invoke();
        _dismiss(this);
    }
}

public interface IToastService
{
    ObservableCollection<ToastViewModel> Toasts { get; }
    void Show(ToastKind kind, string title, string? message = null, string? actionText = null, Action? action = null);
}

public static class ToastExtensions
{
    public static void Success(this IToastService s, string title, string? message = null, string? actionText = null, Action? action = null) =>
        s.Show(ToastKind.Success, title, message, actionText, action);
    public static void Info(this IToastService s, string title, string? message = null, string? actionText = null, Action? action = null) =>
        s.Show(ToastKind.Info, title, message, actionText, action);
    public static void Warning(this IToastService s, string title, string? message = null) =>
        s.Show(ToastKind.Warning, title, message);
    public static void Error(this IToastService s, string title, string? message = null) =>
        s.Show(ToastKind.Error, title, message);
}

/// <summary>Внутренние уведомления: появляются справа снизу, закрываются сами или вручную.</summary>
public sealed class ToastService : IToastService
{
    private const int MaxVisible = 4;
    private readonly ISettingsService _settings;

    public ObservableCollection<ToastViewModel> Toasts { get; } = new();

    public ToastService(ISettingsService settings) => _settings = settings;

    public void Show(ToastKind kind, string title, string? message = null, string? actionText = null, Action? action = null)
    {
        // Ошибки и предупреждения показываем всегда, остальное - если уведомления включены.
        if (!_settings.Current.ShowNotifications && kind is ToastKind.Info or ToastKind.Success) return;

        UiThread.Post(() =>
        {
            var toast = new ToastViewModel(kind, title, message, actionText, action, Dismiss);
            Toasts.Add(toast);
            while (Toasts.Count(t => !t.IsClosing) > MaxVisible)
                Dismiss(Toasts.First(t => !t.IsClosing));

            var seconds = kind == ToastKind.Error ? Math.Max(8, _settings.Current.ToastSeconds) : _settings.Current.ToastSeconds;
            _ = AutoCloseAsync(toast, TimeSpan.FromSeconds(Math.Clamp(seconds, 2, 30)));
        });
    }

    private async Task AutoCloseAsync(ToastViewModel toast, TimeSpan after)
    {
        await Task.Delay(after);
        Dismiss(toast);
    }

    private async void Dismiss(ToastViewModel toast)
    {
        if (toast.IsClosing) return;
        toast.IsClosing = true;                       // запускает анимацию исчезновения во View
        await Task.Delay(MotionSettings.Normal + TimeSpan.FromMilliseconds(40));
        Toasts.Remove(toast);
    }
}
