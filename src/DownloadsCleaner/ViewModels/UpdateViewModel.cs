using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadsCleaner.Helpers;
using DownloadsCleaner.Localization;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.ViewModels;

public enum UpdateState { Idle, Checking, UpToDate, Available, Downloading, Error }

/// <summary>
/// Кнопка «Проверить обновления» в настройках и уведомление о новой версии при запуске.
/// Установка - только после подтверждения; если копия не может обновить себя сама
/// (сборка для разработки, нет прав на папку), открывается страница выпуска.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private const int MaxNotesLength = 1000;

    private readonly IUpdateService _service;
    private readonly ISettingsService _settings;
    private readonly IToastService _toasts;
    private readonly IDialogService _dialogs;
    private readonly ILogService _log;

    public UpdateViewModel(IUpdateService service, ISettingsService settings, IToastService toasts,
        IDialogService dialogs, ILogService log)
    {
        _service = service;
        _settings = settings;
        _toasts = toasts;
        _dialogs = dialogs;
        _log = log;
        Loc.LanguageChanged += (_, _) => UiThread.Post(RefreshText);
        RefreshText();
    }

    public string CurrentVersionText => _service.CurrentVersion.ToString(3);

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsBusy), nameof(HasUpdate), nameof(IsDownloading))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(InstallCommand))]
    private UpdateState _state;

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private UpdateInfo? _available;

    public bool IsBusy => State is UpdateState.Checking or UpdateState.Downloading;
    public bool HasUpdate => State == UpdateState.Available;
    public bool IsDownloading => State == UpdateState.Downloading;

    /// <summary>Подпись главной кнопки: установка или (если нельзя) переход на страницу выпуска.</summary>
    public string InstallText => Loc.T(_service.CanInstall ? "UpdInstall" : "UpdOpenPage");

    private bool CanCheck() => !IsBusy;
    private bool CanInstall() => State == UpdateState.Available;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckAsync() => RunCheckAsync(quiet: false);

    /// <summary>Тихая проверка при запуске: ошибки сети не показываются, новая версия - уведомлением.</summary>
    public Task CheckOnStartupAsync() =>
        _settings.Current.CheckUpdatesOnStartup ? RunCheckAsync(quiet: true) : Task.CompletedTask;

    internal async Task RunCheckAsync(bool quiet)
    {
        if (IsBusy) return;
        State = UpdateState.Checking;
        RefreshText();
        try
        {
            Available = await _service.CheckAsync(CancellationToken.None);
            State = Available != null ? UpdateState.Available : UpdateState.UpToDate;
            if (Available != null && quiet)
                _toasts.Show(ToastKind.Info, Loc.F("UpdToastTitle", Available.Version.ToString(3)), Loc.T("UpdToastText"),
                    Loc.T("UpdToastAction"), () => _ = InstallAsync());
        }
        catch (Exception ex)
        {
            _log.Warn($"Проверка обновлений не удалась: {ex.Message}");
            State = quiet ? UpdateState.Idle : UpdateState.Error;
        }
        RefreshText();
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (Available is not { } update) return;
        if (!_service.CanInstall || update.AssetUrl == null)
        {
            ShellHelper.OpenFile(update.PageUrl);
            return;
        }

        var confirm = new ConfirmDialogViewModel(Loc.F("UpdConfirmTitle", update.Version.ToString(3)),
            Loc.T("UpdConfirmText"), Loc.T("UpdConfirmOk"), Loc.T("CommonCancel"));
        if (!await _dialogs.ShowAsync(confirm)) return;

        State = UpdateState.Downloading;
        Progress = 0;
        RefreshText();
        try
        {
            var progress = new Progress<double>(p =>
            {
                Progress = p;
                StatusText = Loc.F("UpdDownloading", ((int)(p * 100)).ToString());
            });
            await _service.InstallAsync(update, progress, CancellationToken.None);
            // Дальше программа закрывается и запускается новая версия.
        }
        catch (Exception ex)
        {
            _log.Error("Не удалось установить обновление", ex);
            State = UpdateState.Available;
            RefreshText();
            _toasts.Error(Loc.T("UpdInstallFailed"), ex.Message);
        }
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        ShellHelper.OpenFile(Available?.PageUrl ?? $"https://github.com/{GitHubRepository}/releases");
    }

    /// <summary>Адрес выпусков для ссылки; совпадает с репозиторием сервиса обновлений Windows.</summary>
    public const string GitHubRepository = "Revesane/DownloadsCleaner";

    /// <summary>
    /// Описание выпуска с GitHub написано в Markdown: убираем разметку (**, #, `), списки делаем «•»,
    /// от ссылок оставляем текст, служебную строку «Full Changelog» прячем - для неё есть кнопка
    /// «Страница выпуска».
    /// </summary>
    internal static string CleanNotes(string markdown)
    {
        var lines = new List<string>();
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                if (lines.Count > 0 && lines[^1].Length > 0) lines.Add(string.Empty);
                continue;
            }
            if (line.StartsWith("<!--", StringComparison.Ordinal)) continue;
            if (Regex.IsMatch(line, @"^\W*full changelog", RegexOptions.IgnoreCase)) continue;

            line = Regex.Replace(line, @"^#{1,6}\s*", string.Empty);
            line = Regex.Replace(line, @"^[-*+]\s+", "• ");
            line = Regex.Replace(line, @"\[([^\]]+)\]\([^)]*\)", "$1");
            line = line.Replace("**", string.Empty).Replace("__", string.Empty).Replace("`", string.Empty);
            lines.Add(line);
        }
        return string.Join("\n", lines).Trim();
    }

    private void RefreshText()
    {
        StatusText = State switch
        {
            UpdateState.Checking => Loc.T("UpdChecking"),
            UpdateState.UpToDate => Loc.F("UpdUpToDate", CurrentVersionText),
            UpdateState.Available when Available != null => Loc.F("UpdAvailable", Available.Version.ToString(3)),
            UpdateState.Downloading => Loc.F("UpdDownloading", ((int)(Progress * 100)).ToString()),
            UpdateState.Error => Loc.T("UpdFailed"),
            _ => Loc.F("UpdCurrent", CurrentVersionText)
        };
        var notes = CleanNotes(Available?.Notes ?? string.Empty);
        Notes = State == UpdateState.Available && notes.Length > 0
            ? (notes.Length > MaxNotesLength ? notes[..MaxNotesLength] + "…" : notes)
            : string.Empty;
        OnPropertyChanged(nameof(InstallText));
    }
}
