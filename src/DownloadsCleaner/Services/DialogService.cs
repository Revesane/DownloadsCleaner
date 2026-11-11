using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadsCleaner.Animations;

namespace DownloadsCleaner.Services;

/// <summary>Базовый класс модальных окон-оверлеев внутри главного окна.</summary>
public abstract partial class DialogViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _tcs = new();

    public Task<bool> Result => _tcs.Task;

    [ObservableProperty] private bool _isClosing;

    public abstract string Title { get; }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    protected virtual void Confirm() => _tcs.TrySetResult(true);

    [RelayCommand]
    protected virtual void Cancel() => _tcs.TrySetResult(false);

    protected virtual bool CanConfirm() => true;

    protected void RaiseCanConfirmChanged() => ConfirmCommand.NotifyCanExecuteChanged();
}

public interface IDialogService
{
    DialogViewModel? Current { get; }

    /// <summary>Показывает диалог и возвращает true, если пользователь подтвердил.</summary>
    Task<bool> ShowAsync(DialogViewModel dialog);
}

public sealed class DialogService : ObservableObject, IDialogService
{
    private DialogViewModel? _current;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DialogViewModel? Current
    {
        get => _current;
        private set => SetProperty(ref _current, value);
    }

    public async Task<bool> ShowAsync(DialogViewModel dialog)
    {
        await _gate.WaitAsync();            // диалоги показываются по одному
        try
        {
            Current = dialog;
            var result = await dialog.Result;
            dialog.IsClosing = true;        // анимация исчезновения
            await Task.Delay(MotionSettings.Normal + TimeSpan.FromMilliseconds(30));
            Current = null;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }
}
