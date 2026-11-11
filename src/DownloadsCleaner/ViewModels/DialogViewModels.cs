using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadsCleaner.Helpers;
using DownloadsCleaner.Localization;
using DownloadsCleaner.Models;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.ViewModels;

/// <summary>Простое подтверждение (согласия, предупреждения).</summary>
public sealed class ConfirmDialogViewModel : DialogViewModel
{
    private readonly string _title;

    public ConfirmDialogViewModel(string title, string message, string? confirmText = null,
        string? cancelText = null, bool isDanger = false, string glyph = "")
    {
        _title = title;
        Message = message;
        ConfirmText = confirmText ?? Loc.T("DialogContinue");
        CancelText = cancelText ?? Loc.T("CommonCancel");
        IsDanger = isDanger;
        Glyph = glyph;
    }

    public override string Title => _title;
    public string Message { get; }
    public string ConfirmText { get; }
    public string CancelText { get; }
    public bool IsDanger { get; }
    public string Glyph { get; }
}

/// <summary>Подтверждение удаления: количество, объём, список файлов, режим (Корзина / навсегда).</summary>
public sealed partial class DeleteConfirmDialogViewModel : DialogViewModel
{
    public DeleteConfirmDialogViewModel(IReadOnlyList<FileEntry> files, bool permanentAllowed)
    {
        Files = files;
        PermanentAllowed = permanentAllowed;
        Count = files.Count;
        TotalBytes = files.Sum(f => f.Size);
    }

    public override string Title => UsePermanent ? Loc.T("DialogDeletePermanentTitle") : Loc.T("DialogRecycleTitle");
    public IReadOnlyList<FileEntry> Files { get; }
    public bool PermanentAllowed { get; }
    public int Count { get; }
    public long TotalBytes { get; }
    public string Summary => Loc.F("DialogDeleteSummary", ByteFormatter.Files(Count), ByteFormatter.Format(TotalBytes));

    public string Hint => UsePermanent
        ? Loc.T("DialogDeletePermanentHint")
        : Loc.T("DialogRecycleHint");

    public string ConfirmText => UsePermanent ? Loc.T("DialogDeleteForever") : Loc.T("DialogToRecycleBin");

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Title), nameof(Hint), nameof(ConfirmText), nameof(NeedsAcknowledge))]
    private bool _usePermanent;

    [ObservableProperty] private bool _acknowledged;

    public bool NeedsAcknowledge => UsePermanent;

    partial void OnUsePermanentChanged(bool value)
    {
        if (!value) Acknowledged = false;
        RaiseCanConfirmChanged();
    }

    partial void OnAcknowledgedChanged(bool value) => RaiseCanConfirmChanged();

    protected override bool CanConfirm() => !UsePermanent || Acknowledged;
}

public sealed record SortPreviewRow(string Name, string From, string Target, string Category, string Size, bool Renamed);

/// <summary>Предварительный просмотр физической сортировки с выбором папки назначения.</summary>
public sealed partial class SortPreviewDialogViewModel : DialogViewModel
{
    private readonly Func<string, bool, SortPlan> _buildPlan;
    private readonly IFolderPickerService _folders;
    private int _buildVersion;

    public SortPreviewDialogViewModel(Func<string, bool, SortPlan> buildPlan, string destination, bool includeSubfolders,
        bool canChangeScope, IFolderPickerService folders)
    {
        _buildPlan = buildPlan;
        _folders = folders;
        _destination = destination;
        _includeSubfolders = includeSubfolders;
        CanChangeScope = canChangeScope;
        _ = RebuildAsync();
    }

    public override string Title => Loc.T("DialogSortHeader");
    public bool CanChangeScope { get; }

    [ObservableProperty] private string _destination;
    [ObservableProperty] private bool _includeSubfolders;
    [ObservableProperty] private bool _isBuilding = true;
    [ObservableProperty] private SortPlan? _plan;
    [ObservableProperty] private IReadOnlyList<SortPreviewRow> _rows = Array.Empty<SortPreviewRow>();
    [ObservableProperty] private IReadOnlyList<string> _categoryLines = Array.Empty<string>();
    [ObservableProperty] private string _summary = Loc.T("DialogPreparing");
    [ObservableProperty] private string _notes = string.Empty;

    public bool HasMoves => Plan is { Count: > 0 };

    partial void OnIncludeSubfoldersChanged(bool value) => _ = RebuildAsync();

    [RelayCommand]
    private async Task ChangeDestinationAsync()
    {
        var picked = await _folders.PickFolderAsync(Loc.T("DialogDestinationHint"), Destination);
        if (picked != null)
        {
            Destination = picked;
            _ = RebuildAsync();
        }
    }

    private async Task RebuildAsync()
    {
        var version = ++_buildVersion;
        IsBuilding = true;
        var dest = Destination;
        var sub = IncludeSubfolders;
        try
        {
            var plan = await Task.Run(() => _buildPlan(dest, sub));
            if (version != _buildVersion) return;

            Plan = plan;
            Rows = plan.Moves.Take(3000).Select(m => new SortPreviewRow(
                m.File.Name, m.File.Directory, m.TargetPath,
                FileCategoryInfo.Title(m.File.Category), ByteFormatter.Format(m.File.Size), m.Renamed)).ToList();
            CategoryLines = plan.ByCategory
                .Select(c => $"{FileCategoryInfo.Title(c.Category)}: {c.Count:N0} · {ByteFormatter.Format(c.Bytes)}").ToList();
            Summary = plan.Count == 0
                ? Loc.T("DialogNothingToSort")
                : $"{ByteFormatter.Files(plan.Count)} · {ByteFormatter.Format(plan.TotalBytes)}";

            var notes = new List<string>();
            if (plan.RenamedCount > 0)
                notes.Add(Loc.F("DialogRenamedNote", plan.RenamedCount));
            if (plan.Skipped.Count > 0)
                notes.Add(Loc.F("DialogSkippedNote", plan.Skipped.Count));
            if (plan.Count > Rows.Count)
                notes.Add(Loc.F("DialogLimitNote", Rows.Count.ToString("N0"), plan.Count.ToString("N0")));
            Notes = string.Join("\n", notes);
            OnPropertyChanged(nameof(HasMoves));
            RaiseCanConfirmChanged();
        }
        catch (Exception ex)
        {
            Summary = Loc.F("DialogPrepareFailed", ex.Message);
        }
        finally
        {
            if (version == _buildVersion) IsBuilding = false;
        }
    }

    protected override bool CanConfirm() => !IsBuilding && HasMoves;
    partial void OnIsBuildingChanged(bool value) => RaiseCanConfirmChanged();
}

/// <summary>Список ошибок операции.</summary>
public sealed class ErrorListDialogViewModel : DialogViewModel
{
    private readonly string _title;

    public ErrorListDialogViewModel(string title, IReadOnlyList<FailedOperation> failures)
    {
        _title = title;
        Failures = failures;
    }

    public override string Title => _title;
    public IReadOnlyList<FailedOperation> Failures { get; }
}
