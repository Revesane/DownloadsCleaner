using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using DownloadsCleaner.Models;
using Microsoft.Win32;

namespace DownloadsCleaner.Services;

/// <summary>Файл внутри архива: относительный путь (с '\') и распакованный размер.</summary>
public sealed record ArchiveItem(string Path, long Size);

/// <summary>Программа из списка установленных в Windows.</summary>
public sealed record InstalledProgram(string Name, string? Version);

/// <summary>Сведения из ресурсов exe (вкладка «Подробно» в свойствах файла).</summary>
public sealed record InstallerInfo(string? ProductName, string? ProductVersion, string? Description);

public interface IArchiveLister
{
    /// <summary>Содержимое архива или null, если формат не поддерживается или архив не читается.</summary>
    IReadOnlyList<ArchiveItem>? TryList(string path);
}

public interface IInstalledProgramsProvider
{
    IReadOnlyList<InstalledProgram> GetInstalled();
}

public interface IInstallerInfoReader
{
    InstallerInfo? TryRead(string path);
}

/// <summary>Читает оглавление zip-архива (сами данные не распаковываются).</summary>
public sealed class ZipArchiveLister : IArchiveLister
{
    public IReadOnlyList<ArchiveItem>? TryList(string path)
    {
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return zip.Entries
                .Where(e => e.Name.Length > 0)                 // записи-папки пропускаем
                .Select(e => new ArchiveItem(e.FullName.Replace('/', '\\').TrimStart('\\'), e.Length))
                .ToList();
        }
        catch
        {
            return null;                                       // повреждён, зашифрован, занят
        }
    }
}

/// <summary>Установленные программы из разделов Uninstall реестра (как в «Приложения и возможности»).</summary>
public sealed class RegistryInstalledPrograms : IInstalledProgramsProvider
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<InstalledProgram> GetInstalled()
    {
        var result = new List<InstalledProgram>();
        Read(Registry.LocalMachine, RegistryView.Registry64, result);
        Read(Registry.LocalMachine, RegistryView.Registry32, result);
        Read(Registry.CurrentUser, RegistryView.Default, result);
        return result;
    }

    private static void Read(RegistryKey hive, RegistryView view, List<InstalledProgram> result)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive == Registry.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, view);
            using var uninstall = root.OpenSubKey(UninstallKey);
            if (uninstall == null) return;
            foreach (var name in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key?.GetValue("DisplayName") is not string display || string.IsNullOrWhiteSpace(display)) continue;
                    if (key.GetValue("SystemComponent") is int sys && sys == 1) continue;   // скрытые служебные записи
                    result.Add(new InstalledProgram(display.Trim(), (key.GetValue("DisplayVersion") as string)?.Trim()));
                }
                catch { /* отдельная запись недоступна - пропускаем */ }
            }
        }
        catch { /* раздел недоступен - пропускаем */ }
    }
}

public sealed class FileVersionInstallerInfoReader : IInstallerInfoReader
{
    public InstallerInfo? TryRead(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return new InstallerInfo(v.ProductName, v.ProductVersion, v.FileDescription);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Насколько exe/msi похож на установщик (см. <see cref="SmartTipDetectors.EvidenceOf"/>).</summary>
internal enum InstallerEvidence { None, Weak, Strong }

/// <summary>
/// Правила, по которым файл считается лишним. Только чистые функции: файловая система
/// и реестр передаются снаружи, поэтому каждое правило проверяется тестами.
/// </summary>
internal static class SmartTipDetectors
{
    /// <summary>Архив из одного маленького файла легко «совпадает» со случайным файлом - такие не советуем.</summary>
    internal const long MinSingleFileBytes = 64 * 1024;

    // ------------------------------------------------------------ Распакованные архивы

    /// <summary>
    /// Ищет папку, куда архив распакован полностью: каждый файл архива лежит там же
    /// по тому же относительному пути и с тем же размером. Возвращает эту папку или null.
    /// Кандидатов находим по самому крупному файлу архива (имя + размер), поэтому
    /// распакованная копия может лежать где угодно в библиотеке, а не только рядом с архивом.
    /// </summary>
    internal static string? FindExtractedCopy(IReadOnlyList<ArchiveItem> items,
        IReadOnlyDictionary<(string Name, long Size), List<string>> byNameSize, Func<string, long?> lengthOf)
    {
        if (items.Count == 0) return null;
        // Пути с выходом наверх или абсолютные - не то, что можно честно сравнить.
        if (items.Any(i => i.Path.Length == 0 || i.Path.Contains("..") || i.Path.Contains(':') || Path.IsPathRooted(i.Path)))
            return null;

        var probe = items.MaxBy(i => i.Size)!;
        if (items.Count == 1 && probe.Size < MinSingleFileBytes) return null;
        if (!byNameSize.TryGetValue((Path.GetFileName(probe.Path).ToLowerInvariant(), probe.Size), out var candidates))
            return null;

        var suffix = "\\" + probe.Path;
        foreach (var path in candidates)
        {
            if (!path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var root = path[..^suffix.Length];
            if (root.Length == 0) continue;
            if (items.All(i => lengthOf(Path.Combine(root, i.Path)) == i.Size)) return root;
        }
        return null;
    }

    // ------------------------------------------------------------ Установщики

    private static readonly Regex InstallerWord = new("setup|install|инсталл|установ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Насколько файл похож на установщик. Exe из распакованных программ (часть самой программы)
    /// трогать нельзя. Решение принимается без чтения файла: открытие exe заставляет антивирус
    /// проверять его, и на 200 файлах это занимало до минуты.
    /// <list type="bullet">
    /// <item>Strong - msi, скачан прямо из браузера, называет себя установщиком или лежит в корне
    /// папки загрузок либо в папке категории ("Программы"); для него можно прочитать сведения из exe.</item>
    /// <item>Weak - в имени есть версия ("Vital v1.5.5.exe"); засчитывается, только если имя совпало
    /// с установленной программой, сам exe не открывается.</item>
    /// </list>
    /// </summary>
    internal static InstallerEvidence EvidenceOf(FileEntry f, string? downloadsRoot)
    {
        if (f.IsTemporary) return InstallerEvidence.None;
        if (f.Extension == "msi") return InstallerEvidence.Strong;
        if (f.Extension != "exe") return InstallerEvidence.None;
        var stem = Path.GetFileNameWithoutExtension(f.Name);
        if (f.SourceHost != null || InstallerWord.IsMatch(stem) || IsSortedLocation(f.Directory, downloadsRoot))
            return InstallerEvidence.Strong;
        return ParseVersion(stem) != null ? InstallerEvidence.Weak : InstallerEvidence.None;
    }

    private static readonly HashSet<string> CategoryFolders =
        new(FileCategoryInfo.All.Select(FileCategoryInfo.FolderName), StringComparer.OrdinalIgnoreCase);

    /// <summary>Корень папки загрузок или папка категории в нём - туда файлы попадают скачанными, а не распакованными.</summary>
    private static bool IsSortedLocation(string directory, string? root)
    {
        if (string.IsNullOrEmpty(root)) return false;
        var dir = directory.TrimEnd('\\');
        var r = root.TrimEnd('\\');
        if (dir.Equals(r, StringComparison.OrdinalIgnoreCase)) return true;
        return Path.GetDirectoryName(dir) is { } parent && parent.Equals(r, StringComparison.OrdinalIgnoreCase)
               && CategoryFolders.Contains(Path.GetFileName(dir));
    }

    private static readonly HashSet<string> NoiseWords = new(StringComparer.Ordinal)
    {
        "setup", "install", "installer", "installation", "x64", "x86", "x32", "win", "win32", "win64", "windows",
        "amd64", "arm64", "64bit", "32bit", "bit", "full", "final", "release", "stable", "latest", "rus", "eng",
        "ru", "en", "multi", "multilingual", "web", "offline", "online", "v", "ver", "version", "the", "free",
        "trial", "demo", "portable", "32", "64", "86"
    };

    /// <summary>
    /// Служебные компоненты: "Python Launcher" - не Python, "Foo Updater" - не Foo. Если такое слово
    /// есть только в названии программы, установщик к ней не относится.
    /// </summary>
    private static readonly HashSet<string> ComponentWords = new(StringComparer.Ordinal)
    {
        "launcher", "updater", "update", "helper", "uninstaller", "uninstall", "runtime", "service", "services",
        "sdk", "redistributable", "redist", "documentation", "docs", "extension", "addon", "language", "pack", "agent"
    };

    /// <summary>Слова, которых самих по себе мало для совпадения ("Microsoft" - это не программа).</summary>
    private static readonly HashSet<string> GenericWords = new(StringComparer.Ordinal)
    {
        "microsoft", "google", "adobe", "apple", "intel", "nvidia", "amd", "realtek", "native", "steam", "epic",
        "update", "driver", "drivers", "app", "application", "program", "tool", "tools", "player", "studio", "plugin"
    };

    private static readonly string[] GluedWords = { "installer", "install", "setup" };
    private static readonly Regex TokenSplit = new(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant);
    private static readonly Regex VersionToken = new(@"^v\d+$", RegexOptions.CultureInvariant);
    /// <summary>Версия с точками и хвостом сборки: "1.368-b", "v1.6.4", "3.13.7-amd64", "1.2.0-beta.0".</summary>
    private static readonly Regex DottedVersion = new(@"v?\d+(?:\.\d+)+[a-z0-9.-]*", RegexOptions.CultureInvariant);
    private static readonly Regex CopyMarker = new(@"\(\d+\)", RegexOptions.CultureInvariant);

    /// <summary>
    /// Значимые слова названия: без версий, разрядности, «setup» и прочего шума. Одиночные числа
    /// остаются: в "Serum 2" или "Kontakt 8" это часть названия продукта, а не версия.
    /// </summary>
    internal static List<string> NameTokens(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return tokens;
        var cleaned = DottedVersion.Replace(CopyMarker.Replace(text.ToLowerInvariant(), " "), " ");
        foreach (var raw in TokenSplit.Split(cleaned))
        {
            var t = raw;
            // "ChromeSetup", "setupfoo" - отделяем приклеенное служебное слово.
            foreach (var glued in GluedWords)
            {
                if (t.Length > glued.Length + 2 && t.EndsWith(glued, StringComparison.Ordinal)) t = t[..^glued.Length];
                else if (t.Length > glued.Length + 2 && t.StartsWith(glued, StringComparison.Ordinal)) t = t[glued.Length..];
            }
            if (t.Length == 0 || NoiseWords.Contains(t) || VersionToken.IsMatch(t)) continue;
            if (!tokens.Contains(t)) tokens.Add(t);
        }
        return tokens;
    }

    /// <summary>
    /// Название файла совпадает с программой, если первое слово то же, все слова файла есть
    /// в названии программы, а лишних слов у программы не больше двух ("VLC" ~ "VLC media player").
    /// </summary>
    internal static bool NamesMatch(IReadOnlyList<string> file, IReadOnlyList<string> program)
    {
        if (file.Count == 0 || program.Count == 0) return false;
        if (file[0].Length < 3 || file[0] != program[0]) return false;
        if (file.Count == 1 && GenericWords.Contains(file[0])) return false;
        if (program.Count - file.Count > 2) return false;
        if (!file.All(program.Contains)) return false;
        // Число в названии программы - это её поколение: установщик Serum 1 не относится к "Serum 2".
        return !program.Any(t => (ComponentWords.Contains(t) || t.All(char.IsDigit)) && !file.Contains(t));
    }

    private static readonly Regex VersionRx = new(@"(?<![\d.])(\d+(?:\.\d+){1,3})(?![\d])", RegexOptions.CultureInvariant);

    /// <summary>Первая версия вида 1.2, 1.2.3 или 1.2.3.4 в строке, дополненная нулями.</summary>
    internal static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var m = VersionRx.Match(text);
        if (!m.Success || !Version.TryParse(m.Groups[1].Value, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    }

    /// <summary>
    /// Установленная программа для установщика или null. Если версии известны, совет даётся,
    /// только когда установлена та же или более новая версия: более новый установщик - это обновление.
    /// </summary>
    internal static InstalledProgram? MatchInstalled(FileEntry f, InstallerInfo? info, IReadOnlyList<InstalledProgram> programs) =>
        MatchInstalled(f, () => info, Tokenize(programs));

    /// <summary>Слова названий программ считаются один раз на весь анализ, а не для каждого установщика.</summary>
    internal static IReadOnlyList<(InstalledProgram Program, List<string> Tokens)> Tokenize(IReadOnlyList<InstalledProgram> programs) =>
        programs.Select(p => (p, NameTokens(p.Name))).Where(x => x.Item2.Count > 0).ToList();

    /// <param name="readInfo">
    /// Сведения из exe читаются лениво - только если имени файла не хватило: по имени не нашлось
    /// программы или в имени нет версии (нельзя понять, не новее ли установщик).
    /// </param>
    internal static InstalledProgram? MatchInstalled(FileEntry f, Func<InstallerInfo?> readInfo,
        IReadOnlyList<(InstalledProgram Program, List<string> Tokens)> programs)
    {
        var stem = Path.GetFileNameWithoutExtension(f.Name);
        var stemTokens = NameTokens(stem);
        var stemVersion = ParseVersion(stem);

        if (stemVersion != null && stemTokens.Count > 0 && Find(new[] { stemTokens }, stemVersion) is { } byName)
            return byName;

        var info = f.Extension == "exe" ? readInfo() : null;
        var names = new[] { NameTokens(info?.ProductName), stemTokens }.Where(t => t.Count > 0).ToList();
        if (names.Count == 0) return null;
        return Find(names, ParseVersion(info?.ProductVersion) ?? stemVersion);

        // Лучшее совпадение: сперва программа той же версии ("Serum 1.368" - это Serum 1, а не установленный
        // рядом Serum 2), затем - с наименьшим числом лишних слов ("Sononym" точнее "Sononym Demo").
        InstalledProgram? Find(IReadOnlyList<List<string>> fileNames, Version? fileVersion)
        {
            InstalledProgram? best = null;
            var bestScore = int.MaxValue;
            foreach (var (program, programTokens) in programs)
            {
                var name = fileNames.FirstOrDefault(n => NamesMatch(n, programTokens));
                if (name == null) continue;
                var installed = ParseVersion(program.Version) ?? ParseVersion(program.Name);
                if (fileVersion != null && installed != null && installed < fileVersion) continue;
                var score = (fileVersion != null && fileVersion == installed ? 0 : 100) + programTokens.Count - name.Count;
                if (score < bestScore) { best = program; bestScore = score; }
            }
            return best;
        }
    }

    /// <summary>"Sononym" + "1.6.4" -> "Sononym 1.6.4", но без повтора, если версия уже в названии.</summary>
    internal static string Describe(InstalledProgram p) =>
        string.IsNullOrWhiteSpace(p.Version) || p.Name.Contains(p.Version, StringComparison.OrdinalIgnoreCase)
            ? p.Name
            : $"{p.Name} {p.Version}";

    // ------------------------------------------------------------ Старые версии

    private static readonly Regex VersionInName = new(@"[\s._-]*[vV]?(?<![\d.])\d+(?:\.\d+){1,3}(?![\d])", RegexOptions.CultureInvariant);

    /// <summary>Имя файла без версии ("Setup Foo v1.2.exe" -> "setup foo") или null, если версии нет.</summary>
    internal static string? VersionKey(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (!VersionInName.IsMatch(stem)) return null;
        var key = TokenSplit.Replace(VersionInName.Replace(stem, " ", 1).ToLowerInvariant(), " ").Trim();
        return key.Count(char.IsLetter) >= 3 ? key + "|" + Path.GetExtension(fileName).ToLowerInvariant() : null;
    }

    /// <summary>Пары (старая версия, самая новая версия) среди файлов с одинаковым именем без версии.</summary>
    internal static IEnumerable<(FileEntry Old, FileEntry Newest)> FindOldVersions(IEnumerable<FileEntry> files)
    {
        var groups = files
            .Select(f => (File: f, Key: VersionKey(f.Name), Version: ParseVersion(Path.GetFileNameWithoutExtension(f.Name))))
            .Where(x => x.Key != null && x.Version != null)
            .GroupBy(x => x.Key!, StringComparer.Ordinal);

        foreach (var g in groups)
        {
            var newest = g.OrderByDescending(x => x.Version).ThenByDescending(x => x.File.Modified).First();
            foreach (var x in g)
                if (x.Version! < newest.Version!) yield return (x.File, newest.File);
        }
    }
}

/// <summary>Собирает советы всех видов. Один файл попадает только в один совет.</summary>
public sealed class SmartTipsService : ISmartTipsService
{
    private readonly IArchiveLister _archives;
    private readonly IInstalledProgramsProvider _programs;
    private readonly IInstallerInfoReader _installers;
    private readonly ILogService _log;

    public SmartTipsService(IArchiveLister archives, IInstalledProgramsProvider programs, IInstallerInfoReader installers,
        ILogService? log = null)
    {
        _archives = archives;
        _programs = programs;
        _installers = installers;
        _log = log ?? NullLogService.Instance;
    }

    /// <summary>Сведения из exe по пути, размеру и дате: повторный анализ не открывает файлы заново.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, long Size, DateTime Modified), InstallerInfo?> _infoCache = new();

    private InstallerInfo? ReadInfoCached(FileEntry f) =>
        _infoCache.GetOrAdd((f.FullPath.ToLowerInvariant(), f.Size, f.Modified), _ => _installers.TryRead(f.FullPath));

    public Task<IReadOnlyList<SmartTip>> FindAsync(IReadOnlyList<FileEntry> files, string downloadsRoot, CancellationToken ct) =>
        Task.Run(() => Find(files, downloadsRoot, ct), ct);

    internal IReadOnlyList<SmartTip> Find(IReadOnlyList<FileEntry> files, string? downloadsRoot, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var tips = new List<SmartTip>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(SmartTip t)
        {
            if (used.Add(t.File.FullPath)) tips.Add(t);
        }

        // 1. Распакованные архивы - самое надёжное доказательство, поэтому первыми.
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files) sizes[f.FullPath] = f.Size;
        var byNameSize = files
            .GroupBy(f => (f.Name.ToLowerInvariant(), f.Size))
            .ToDictionary(g => g.Key, g => g.Select(f => f.FullPath).ToList());
        long? LengthOf(string p) => sizes.TryGetValue(p, out var s) ? s : null;

        var archives = files.Where(f => f.Category == FileCategory.Archives && !f.IsTemporary).ToList();
        foreach (var archive in archives)
        {
            ct.ThrowIfCancellationRequested();
            if (_archives.TryList(archive.FullPath) is not { } items) continue;
            if (SmartTipDetectors.FindExtractedCopy(items, byNameSize, LengthOf) is { } root)
                Add(new SmartTip(TipKind.ExtractedArchive, archive, root));
        }

        // 2. Установщики уже установленных программ.
        IReadOnlyList<InstalledProgram> programs;
        try { programs = _programs.GetInstalled(); }
        catch (Exception ex)
        {
            _log.Warn($"Список установленных программ недоступен: {ex.Message}");
            programs = Array.Empty<InstalledProgram>();
        }

        var programTokens = SmartTipDetectors.Tokenize(programs);
        var candidates = files
            .Select(f => (File: f, Evidence: SmartTipDetectors.EvidenceOf(f, downloadsRoot)))
            .Where(x => x.Evidence != InstallerEvidence.None)
            .ToList();
        // Параллельно: если exe всё-таки приходится открыть, его задерживает проверка антивирусом.
        // Слабых кандидатов не открываем вовсе - они сопоставляются только по имени файла.
        var matched = new InstalledProgram?[candidates.Count];
        Parallel.For(0, candidates.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 4), CancellationToken = ct },
            i =>
            {
                var (file, evidence) = candidates[i];
                matched[i] = SmartTipDetectors.MatchInstalled(file,
                    () => evidence == InstallerEvidence.Strong ? ReadInfoCached(file) : null, programTokens);
            });
        for (var i = 0; i < candidates.Count; i++)
            if (matched[i] is { } program)
                Add(new SmartTip(TipKind.InstalledProgram, candidates[i].File, SmartTipDetectors.Describe(program)));
        var installers = candidates.Where(x => x.Evidence == InstallerEvidence.Strong).Select(x => x.File).ToList();

        // 3. Старые версии среди архивов и установщиков.
        foreach (var (old, newest) in SmartTipDetectors.FindOldVersions(archives.Concat(installers)))
            Add(new SmartTip(TipKind.OldVersion, old, newest.FullPath));

        _log.Info($"Умные советы: {tips.Count} (архивов {archives.Count}, установщиков {installers.Count}, " +
                  $"программ в системе {programs.Count}), {sw.ElapsedMilliseconds} мс");
        return tips
            .OrderBy(t => t.Kind)
            .ThenByDescending(t => t.File.Size)
            .ToList();
    }

    public bool StillValid(SmartTip tip)
    {
        try
        {
            if (!File.Exists(tip.File.FullPath)) return false;
            switch (tip.Kind)
            {
                case TipKind.ExtractedArchive:
                    // Сверяем с диском, а не с библиотекой: распакованную папку могли удалить только что.
                    if (_archives.TryList(tip.File.FullPath) is not { Count: > 0 } items) return false;
                    return items.All(i =>
                    {
                        var info = new FileInfo(Path.Combine(tip.Related, i.Path));
                        return info.Exists && info.Length == i.Size;
                    });
                case TipKind.OldVersion:
                    return File.Exists(tip.Related);
                default:
                    return true;
            }
        }
        catch
        {
            return false;
        }
    }
}
