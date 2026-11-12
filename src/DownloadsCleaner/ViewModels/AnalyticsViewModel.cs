using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DownloadsCleaner.Controls;
using DownloadsCleaner.Helpers;
using DownloadsCleaner.Localization;
using DownloadsCleaner.Models;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.ViewModels;

public sealed record LegendRow(FileCategory Category, string Title, string BytesText, string PercentText, int Count, double Share);

public sealed record AgeRow(string Title, int Count, string BytesText, double Share);

public sealed record BigFileRow(FileEntry Entry, string Name, string Folder, string SizeText, FileCategory Category);

/// <param name="Share">Длина полоски: доля от самого частого источника (0..1).</param>
public sealed record SourceRow(string Host, int Count, string BytesText, double Share);

/// <summary>Аналитика места: все значения считаются из реальных результатов сканирования.</summary>
public sealed partial class AnalyticsViewModel : ObservableObject, IDisposable
{
    private readonly IFileLibrary _library;
    private const int TopSources = 10;

    private readonly ISettingsService _settings;
    private readonly INavigationService _navigation;
    private readonly UiDebouncer _rebuildDebounce;

    public AnalyticsViewModel(IFileLibrary library, ISettingsService settings, INavigationService navigation)
    {
        _library = library;
        _settings = settings;
        _navigation = navigation;
        // Аналитика пересчитывается по всей библиотеке, поэтому пачка изменений
        // (например, тысяча новых файлов) не должна запускать пересчёт на каждый файл.
        _rebuildDebounce = new UiDebouncer(TimeSpan.FromMilliseconds(300), Rebuild);
        _library.Changed += (_, _) => _rebuildDebounce.Schedule();
        Loc.LanguageChanged += (_, _) => UiThread.Post(Rebuild);
        Rebuild();
    }

    public void Dispose() => _rebuildDebounce.Dispose();

    [ObservableProperty] private IReadOnlyList<DonutSegment> _segments = Array.Empty<DonutSegment>();
    [ObservableProperty] private IReadOnlyList<LegendRow> _legend = Array.Empty<LegendRow>();
    [ObservableProperty] private IReadOnlyList<AgeRow> _ageRows = Array.Empty<AgeRow>();
    [ObservableProperty] private IReadOnlyList<BigFileRow> _biggest = Array.Empty<BigFileRow>();
    [ObservableProperty] private double _totalBytes;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string _diskTitle = string.Empty;
    [ObservableProperty] private string _diskDetail = string.Empty;
    [ObservableProperty] private double _diskFraction;
    [ObservableProperty] private double _folderShare;
    [ObservableProperty] private IReadOnlyList<SourceRow> _sources = Array.Empty<SourceRow>();
    [ObservableProperty] private bool _hasSources;
    [ObservableProperty] private string _sourcesCoverage = string.Empty;

    private void Rebuild()
    {
        var files = _library.Files;
        var stats = _library.Stats;
        HasData = files.Count > 0;
        TotalBytes = stats.TotalBytes;

        Segments = stats.Categories
            .Where(c => c.Bytes > 0)
            .Select(c => new DonutSegment(c.Bytes, FileCategoryInfo.ColorHex(c.Category)))
            .ToList();

        Legend = stats.Categories
            .Where(c => c.Count > 0)
            .OrderByDescending(c => c.Bytes)
            .Select(c =>
            {
                var share = stats.TotalBytes > 0 ? (double)c.Bytes / stats.TotalBytes : 0;
                return new LegendRow(c.Category, FileCategoryInfo.Title(c.Category), ByteFormatter.Format(c.Bytes),
                    (share * 100).ToString("0.#") + " %", c.Count, share);
            }).ToList();

        // Возрастные корзины считаем одним проходом: четыре отдельных Where по всей
        // библиотеке заметно тормозили на больших папках.
        var now = DateTime.Now;
        var weekAgo = now.AddDays(-7);
        var monthAgo = now.AddDays(-30);
        var quarterAgo = now.AddDays(-90);
        int c7 = 0, c30 = 0, c90 = 0, cOld = 0;
        long b7 = 0, b30 = 0, b90 = 0, bOld = 0;
        // Источники считаем в том же проходе.
        var bySource = new Dictionary<string, (int Count, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        var withSource = 0;
        foreach (var f in files)
        {
            if (f.Created >= weekAgo) { c7++; b7 += f.Size; }
            else if (f.Created >= monthAgo) { c30++; b30 += f.Size; }
            else if (f.Created >= quarterAgo) { c90++; b90 += f.Size; }
            else { cOld++; bOld += f.Size; }

            if (f.SourceHost is { } host)
            {
                withSource++;
                var s = bySource.GetValueOrDefault(host);
                bySource[host] = (s.Count + 1, s.Bytes + f.Size);
            }
        }

        var top = bySource
            .OrderByDescending(p => p.Value.Count).ThenByDescending(p => p.Value.Bytes)
            .Take(TopSources).ToList();
        var maxCount = top.Count > 0 ? top[0].Value.Count : 1;
        Sources = top.Select(p => new SourceRow(p.Key, p.Value.Count, ByteFormatter.Format(p.Value.Bytes),
            (double)p.Value.Count / maxCount)).ToList();
        HasSources = top.Count > 0;
        SourcesCoverage = Loc.F("AnalyticsSourcesCoverage", withSource.ToString("N0"), files.Count.ToString("N0"),
            bySource.Count.ToString("N0"));

        double Share(long bytes) => stats.TotalBytes > 0 ? (double)bytes / stats.TotalBytes : 0;

        AgeRows = new[]
        {
            new AgeRow(Loc.T("AgeUnder7"), c7, ByteFormatter.Format(b7), Share(b7)),
            new AgeRow(Loc.T("Age7to30"), c30, ByteFormatter.Format(b30), Share(b30)),
            new AgeRow(Loc.T("Age30to90"), c90, ByteFormatter.Format(b90), Share(b90)),
            new AgeRow(Loc.T("AgeOver90"), cOld, ByteFormatter.Format(bOld), Share(bOld))
        };

        // Топ-10 без полной сортировки всей библиотеки: держим отсортированный буфер.
        var biggest = new List<FileEntry>(10);
        foreach (var f in files)
        {
            var index = biggest.FindIndex(x => f.Size > x.Size);
            if (index < 0)
            {
                if (biggest.Count >= 10) continue;
                biggest.Add(f);
            }
            else
            {
                biggest.Insert(index, f);
                if (biggest.Count > 10) biggest.RemoveAt(10);
            }
        }

        Biggest = biggest
            .Select(f => new BigFileRow(f, f.Name, f.Directory, ByteFormatter.Format(f.Size), f.Category)).ToList();

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(_settings.Current.DownloadsPath));
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                var used = drive.TotalSize - drive.AvailableFreeSpace;
                DiskTitle = Loc.F("AnalyticsDisk", drive.Name.TrimEnd('\\'));
                DiskDetail = Loc.F("AnalyticsDiskDetail", ByteFormatter.Format(used), ByteFormatter.Format(drive.TotalSize),
                    ByteFormatter.Format(drive.AvailableFreeSpace));
                DiskFraction = drive.TotalSize > 0 ? (double)used / drive.TotalSize : 0;
                FolderShare = drive.TotalSize > 0 ? (double)stats.TotalBytes / drive.TotalSize : 0;
            }
        }
        catch
        {
            DiskTitle = Loc.T("AnalyticsDiskUnavailable");
            DiskDetail = string.Empty;
            DiskFraction = 0;
        }
    }

    [RelayCommand]
    private void ShowInExplorer(BigFileRow? row)
    {
        if (row != null) ShellHelper.ShowInExplorer(row.Entry.FullPath);
    }

    /// <summary>Открывает список всех файлов, скачанных с этого сайта.</summary>
    [RelayCommand]
    private void ShowSourceFiles(SourceRow? row)
    {
        if (row != null) _navigation.Go(NavKind.All, FilePreset.None, row.Host);
    }
}
