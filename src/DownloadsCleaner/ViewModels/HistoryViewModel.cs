using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadsCleaner.Helpers;
using DownloadsCleaner.Localization;
using DownloadsCleaner.Models;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.ViewModels;

public sealed class HistoryItemViewModel
{
    public HistoryItemViewModel(HistoryEntry entry)
    {
        Entry = entry;
        TimeText = entry.Time.ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture);
        SizeText = entry.Bytes > 0 ? ByteFormatter.Format(entry.Bytes) : string.Empty;
    }

    public HistoryEntry Entry { get; }
    public string Title => Entry.Title;
    public string Details => Entry.Details;
    public string TimeText { get; }
    public string SizeText { get; }
    public bool CanUndo => Entry.CanUndo;
    public bool WasUndone => Entry.Undone;

    public string Glyph => Entry.Kind switch
    {
        HistoryKind.Sort or HistoryKind.AutoSort => "",
        HistoryKind.Recycle => "",
        HistoryKind.PermanentDelete => "",
        _ => ""
    };

    public bool IsDestructive => Entry.Kind == HistoryKind.PermanentDelete;
}

/// <summary>Корзина / история операций.</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly IHistoryService _history;
    private readonly IOperationsCoordinator _operations;
    private readonly IDialogService _dialogs;

    public HistoryViewModel(IHistoryService history, IOperationsCoordinator operations, IDialogService dialogs)
    {
        _history = history;
        _operations = operations;
        _dialogs = dialogs;
        _history.Changed += (_, _) => UiThread.Post(Reload);
        Reload();
    }

    public ObservableCollection<HistoryItemViewModel> Items { get; } = new();
    [ObservableProperty] private bool _isEmpty = true;

    private void Reload()
    {
        Items.Clear();
        foreach (var e in _history.Entries) Items.Add(new HistoryItemViewModel(e));
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    private async Task UndoAsync(HistoryItemViewModel? item)
    {
        if (item != null) await _operations.UndoAsync(item.Entry);
    }

    [RelayCommand]
    private void OpenRecycleBin() => ShellHelper.OpenRecycleBin();

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (Items.Count == 0) return;
        var confirm = new ConfirmDialogViewModel(Loc.T("HistoryClearTitle"),
            Loc.T("HistoryClearText"), Loc.T("HistoryClearConfirm"), Loc.T("CommonCancel"), true, "");
        if (await _dialogs.ShowAsync(confirm)) _history.Clear();
    }
}
