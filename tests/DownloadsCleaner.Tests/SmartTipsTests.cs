using System.IO.Compression;
using DownloadsCleaner.Models;
using DownloadsCleaner.Services;
using DownloadsCleaner.ViewModels;

namespace DownloadsCleaner.Tests;

/// <summary>Правила «Умных советов»: распакованные архивы, установленные программы, старые версии.</summary>
public class SmartTipDetectorTests
{
    private static FileEntry Exe(string path, string? host = null) =>
        new(path, Path.GetFileName(path), Path.GetExtension(path).TrimStart('.').ToLowerInvariant(), 1000,
            DateTime.Now, DateTime.Now, FileCategory.Programs, false) { SourceHost = host };

    [Theory]
    [InlineData("Setup Sononym v1.6.4", "sononym")]
    [InlineData("ChromeSetup", "chrome")]
    [InlineData("Serum 2", "serum 2")]
    [InlineData("Python 3.14.0 (64-bit)", "python")]
    [InlineData("Combobulator 1.2.0-beta.0", "combobulator")]
    [InlineData("vlc-3.0.20-win64", "vlc")]
    [InlineData("Foo Bar (1)", "foo bar")]
    public void Name_tokens_drop_versions_and_noise_but_keep_product_numbers(string text, string expected)
    {
        Assert.Equal(expected, string.Join(" ", SmartTipDetectors.NameTokens(text)));
    }

    [Theory]
    [InlineData("vlc", "VLC media player", true)]
    [InlineData("sononym", "Sononym", true)]
    [InlineData("python", "Python Launcher", false)]        // служебный компонент - другая программа
    [InlineData("serum", "Serum 2", false)]                 // число в названии - другое поколение продукта
    [InlineData("microsoft", "Microsoft Edge", false)]      // одного общего слова мало
    [InlineData("foo", "Foo Pro Studio Max Ultra", false)]  // слишком много лишних слов
    [InlineData("foo bar", "Foo", false)]
    public void Names_match_rules(string file, string program, bool expected)
    {
        Assert.Equal(expected, SmartTipDetectors.NamesMatch(
            SmartTipDetectors.NameTokens(file), SmartTipDetectors.NameTokens(program)));
    }

    [Theory]
    [InlineData("Setup v1.6.4", "1.6.4.0")]
    [InlineData("Beyond-All-Reason-1.2988.0", "1.2988.0.0")]
    [InlineData("1.368-B", "1.368.0.0")]
    [InlineData("no version here", null)]
    [InlineData("Kontakt 8", null)]
    public void Versions_are_parsed_and_padded(string text, string? expected)
    {
        Assert.Equal(expected, SmartTipDetectors.ParseVersion(text)?.ToString());
    }

    [Fact]
    public void Installer_matches_same_or_newer_installed_version_only()
    {
        var file = Exe(@"C:\D\Setup Sononym v1.6.4.exe");
        Assert.NotNull(SmartTipDetectors.MatchInstalled(file, null, new[] { new InstalledProgram("Sononym", "1.6.4") }));
        Assert.NotNull(SmartTipDetectors.MatchInstalled(file, null, new[] { new InstalledProgram("Sononym", "1.7.0") }));
        // Установщик новее установленной версии - это обновление, его не советуем удалять.
        Assert.Null(SmartTipDetectors.MatchInstalled(file, null, new[] { new InstalledProgram("Sononym", "1.5.0") }));
    }

    [Fact]
    public void Installer_prefers_program_with_the_same_version()
    {
        var file = Exe(@"C:\D\Serum Setup 1.368.exe");
        var programs = new[] { new InstalledProgram("Serum FX", "2.0.16"), new InstalledProgram("Serum", "1.368") };
        Assert.Equal("1.368", SmartTipDetectors.MatchInstalled(file, null, programs)?.Version);
    }

    [Fact]
    public void Exe_info_is_read_only_when_the_file_name_is_not_enough()
    {
        var programs = SmartTipDetectors.Tokenize(new[] { new InstalledProgram("Recuva", "1.54"), new InstalledProgram("Vital", "1.5.5") });
        var reads = 0;
        InstallerInfo? Read(InstallerInfo info) { reads++; return info; }

        // Имя с версией совпало - exe не открываем.
        Assert.NotNull(SmartTipDetectors.MatchInstalled(Exe(@"C:\D\Vital v1.5.5.exe"),
            () => Read(new InstallerInfo(null, null, null)), programs));
        Assert.Equal(0, reads);

        // По имени не понять ("rcsetup154") - название продукта берём из exe.
        Assert.Equal("Recuva", SmartTipDetectors.MatchInstalled(Exe(@"C:\D\rcsetup154.exe"),
            () => Read(new InstallerInfo("Recuva", "1.54", "Recuva Setup")), programs)?.Name);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void Installer_evidence_depends_on_name_mark_and_location()
    {
        const string root = @"C:\Downloads";
        Assert.Equal(InstallerEvidence.Strong, SmartTipDetectors.EvidenceOf(Exe(@"C:\Downloads\deep\x\tool.msi"), root));
        Assert.Equal(InstallerEvidence.Strong, SmartTipDetectors.EvidenceOf(Exe(@"C:\Downloads\deep\x\tool.exe", "example.com"), root));
        Assert.Equal(InstallerEvidence.Strong, SmartTipDetectors.EvidenceOf(Exe(@"C:\Downloads\deep\x\Foo Setup.exe"), root));
        Assert.Equal(InstallerEvidence.Strong, SmartTipDetectors.EvidenceOf(Exe(@"C:\Downloads\tool.exe"), root));
        Assert.Equal(InstallerEvidence.Strong, SmartTipDetectors.EvidenceOf(Exe(@"C:\Downloads\Программы\tool.exe"), root));
        Assert.Equal(InstallerEvidence.Weak, SmartTipDetectors.EvidenceOf(Exe(@"C:\Downloads\deep\x\Vital v1.5.5.exe"), root));
        // Exe внутри распакованной программы - её часть, а не установщик.
        Assert.Equal(InstallerEvidence.None, SmartTipDetectors.EvidenceOf(Exe(@"C:\Downloads\SomeApp\app.exe"), root));
        Assert.Equal(InstallerEvidence.None, SmartTipDetectors.EvidenceOf(Exe(@"C:\Downloads\SomeApp\readme.txt"), root));
    }

    private static Dictionary<(string Name, long Size), List<string>> Index(params (string Path, long Size)[] files) =>
        files.GroupBy(f => (Path.GetFileName(f.Path).ToLowerInvariant(), f.Size))
             .ToDictionary(g => g.Key, g => g.Select(f => f.Path).ToList());

    [Fact]
    public void Extracted_copy_is_found_anywhere_when_every_file_matches()
    {
        var items = new[] { new ArchiveItem(@"pack\big.wav", 500_000), new ArchiveItem(@"pack\readme.txt", 10) };
        var disk = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\D\Music\pack\big.wav"] = 500_000,
            [@"C:\D\Music\pack\readme.txt"] = 10
        };
        var index = Index(disk.Select(p => (p.Key, p.Value)).ToArray());
        long? Len(string p) => disk.TryGetValue(p, out var s) ? s : null;

        Assert.Equal(@"C:\D\Music", SmartTipDetectors.FindExtractedCopy(items, index, Len));

        disk[@"C:\D\Music\pack\readme.txt"] = 11;                 // размер не совпал - копия неполная
        Assert.Null(SmartTipDetectors.FindExtractedCopy(items, index, Len));
        disk.Remove(@"C:\D\Music\pack\readme.txt");              // файла нет
        Assert.Null(SmartTipDetectors.FindExtractedCopy(items, index, Len));
    }

    [Fact]
    public void Suspicious_archives_are_never_reported_as_extracted()
    {
        var index = Index((@"C:\D\a.txt", 10), (@"C:\D\x\evil.txt", 100_000));
        long? Len(string p) => p.EndsWith("a.txt") ? 10 : 100_000;
        // Один маленький файл легко совпадает случайно.
        Assert.Null(SmartTipDetectors.FindExtractedCopy(new[] { new ArchiveItem("a.txt", 10) }, index, Len));
        // Путь с выходом наверх.
        Assert.Null(SmartTipDetectors.FindExtractedCopy(new[] { new ArchiveItem(@"..\x\evil.txt", 100_000) }, index, Len));
    }

    [Fact]
    public void Old_versions_are_grouped_by_name_without_version()
    {
        FileEntry F(string name) => Exe(@"C:\D\" + name);
        var files = new[] { F("Setup Foo v1.2.exe"), F("Setup Foo v1.10.exe"), F("Setup Foo v1.9.zip"), F("Bar.exe"), F("Setup Foo (no version).exe") };

        var pairs = SmartTipDetectors.FindOldVersions(files).ToList();

        var (old, newest) = Assert.Single(pairs);                 // 1.10 новее 1.2 (сравнение чисел, а не строк)
        Assert.Equal("Setup Foo v1.2.exe", old.Name);
        Assert.Equal("Setup Foo v1.10.exe", newest.Name);
    }

    [Fact]
    public void Installed_program_description_does_not_repeat_version()
    {
        Assert.Equal("Sononym 1.6.4", SmartTipDetectors.Describe(new InstalledProgram("Sononym", "1.6.4")));
        Assert.Equal("Combobulator 1.2.0-beta.0", SmartTipDetectors.Describe(new InstalledProgram("Combobulator 1.2.0-beta.0", "1.2.0-beta.0")));
        Assert.Equal("Foo", SmartTipDetectors.Describe(new InstalledProgram("Foo", null)));
    }
}

public class SmartTipsServiceTests
{
    private sealed class Programs(params InstalledProgram[] list) : IInstalledProgramsProvider
    {
        public IReadOnlyList<InstalledProgram> GetInstalled() => list;
    }

    private sealed class NoInfo : IInstallerInfoReader
    {
        public InstallerInfo? TryRead(string path) => null;
    }

    [Fact]
    public async Task Finds_all_kinds_on_real_files_and_revalidates_before_delete()
    {
        using var tmp = new TempDir();
        // Архив лежит в «Архивах», а распакован в другую папку - как после сортировки.
        var src = Path.Combine(tmp.Path, "src");
        Directory.CreateDirectory(Path.Combine(src, "pack"));
        File.WriteAllBytes(Path.Combine(src, "pack", "big.wav"), new byte[200_000]);
        File.WriteAllText(Path.Combine(src, "pack", "readme.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(tmp.Downloads, "Архивы"));
        var zip = Path.Combine(tmp.Downloads, "Архивы", "pack.zip");
        ZipFile.CreateFromDirectory(src, zip);
        tmp.Bytes(@"Music\pack\big.wav", new byte[200_000]);
        var readme = tmp.File(@"Music\pack\readme.txt", "hello");

        var installer = tmp.File("Setup Sononym v1.6.4.exe", "not really an exe");
        var oldTool = tmp.File(@"Программы\Setup Tool v1.0.exe", "old");
        var newTool = tmp.File(@"Программы\Setup Tool v2.0.exe", "new");

        var files = await Fx.ScanAsync(tmp.Downloads, Fx.Detector());
        var service = new SmartTipsService(new ZipArchiveLister(), new Programs(new InstalledProgram("Sononym", "1.6.4")), new NoInfo());

        var tips = await service.FindAsync(files, tmp.Downloads, CancellationToken.None);

        var archive = Assert.Single(tips, t => t.Kind == TipKind.ExtractedArchive);
        Assert.Equal(zip, archive.File.FullPath);
        Assert.Equal(Path.Combine(tmp.Downloads, "Music"), archive.Related);
        var installed = Assert.Single(tips, t => t.Kind == TipKind.InstalledProgram);
        Assert.Equal(installer, installed.File.FullPath);
        Assert.Equal("Sononym 1.6.4", installed.Related);
        var old = Assert.Single(tips, t => t.Kind == TipKind.OldVersion);
        Assert.Equal(oldTool, old.File.FullPath);
        Assert.Equal(newTool, old.Related);
        Assert.Equal(3, tips.Count);                               // каждый файл - только в одном совете

        // Перед удалением совет перепроверяется на диске.
        Assert.True(service.StillValid(archive));
        Assert.True(service.StillValid(old));
        File.Delete(readme);
        File.Delete(newTool);
        Assert.False(service.StillValid(archive));
        Assert.False(service.StillValid(old));
    }
}

public class SmartTipsViewModelTests
{
    private sealed class FakeService(IReadOnlyList<SmartTip> tips, Func<SmartTip, bool> valid) : ISmartTipsService
    {
        public Task<IReadOnlyList<SmartTip>> FindAsync(IReadOnlyList<FileEntry> files, string downloadsRoot, CancellationToken ct) =>
            Task.FromResult(tips);
        public bool StillValid(SmartTip tip) => valid(tip);
    }

    private sealed class RecordingOperations : IOperationsCoordinator
    {
        public List<IReadOnlyList<FileEntry>> Deletes { get; } = new();
        public event Action<IReadOnlyCollection<string>>? FilesLeaving { add { } remove { } }
        public event Action? ReleaseFileHandles { add { } remove { } }
        public event Action<FileEntry>? NewFileArrived { add { } remove { } }
        public Task ScanAsync() => Task.CompletedTask;
        public Task SortAsync(IReadOnlyList<FileEntry>? subset = null) => Task.CompletedTask;
        public Task UndoAsync(HistoryEntry entry) => Task.CompletedTask;
        public void ApplyWatcherSettings() { }
        public Task DeleteAsync(IReadOnlyList<FileEntry> files) { Deletes.Add(files); return Task.CompletedTask; }
    }

    private static FileEntry Entry(string name, long size) =>
        new(@"C:\D\" + name, name, Path.GetExtension(name).TrimStart('.'), size, DateTime.Now, DateTime.Now, FileCategory.Archives, false);

    [Fact]
    public async Task Groups_tips_marks_them_and_deletes_only_still_valid_marked_ones()
    {
        using var tmp = new TempDir();
        var settings = Fx.Settings(tmp);
        var a = Entry("a.zip", 100);
        var b = Entry("b.zip", 200);
        var c = Entry("Setup X v1.exe", 300);
        var library = new FileLibrary(settings);
        library.SetFiles(new[] { a, b, c });
        var tips = new[]
        {
            new SmartTip(TipKind.ExtractedArchive, a, @"C:\D\a"),
            new SmartTip(TipKind.ExtractedArchive, b, @"C:\D\b"),
            new SmartTip(TipKind.InstalledProgram, c, "X 1.0")
        };
        var operations = new RecordingOperations();
        // Распакованную копию b успели удалить - совет устарел.
        using var vm = new SmartTipsViewModel(library, new FakeService(tips, t => t.File != b), settings, operations,
            new AppStatus(), new ToastService(settings), NullLogService.Instance);

        await vm.RunAsync(quiet: true);

        Assert.Equal(3, vm.TipCount);
        Assert.Equal(600, vm.TotalBytes);
        Assert.Equal(new[] { TipKind.ExtractedArchive, TipKind.InstalledProgram }, vm.Groups.Select(g => g.Kind));
        // Архивы отмечены сразу, установщик - нет: их часто хранят намеренно.
        Assert.Equal(2, vm.MarkedCount);
        Assert.False(vm.Groups[1].Items[0].IsMarked);

        await vm.DeleteMarkedCommand.ExecuteAsync(null);

        var sent = Assert.Single(operations.Deletes);
        Assert.Equal(new[] { a.FullPath }, sent.Select(f => f.FullPath));   // b устарел, c не отмечен
        Assert.Equal(2, vm.TipCount);                                       // устаревший совет убран из списка
        Assert.False(vm.Groups.SelectMany(g => g.Items).Single(i => i.Entry == c).IsMarked);
    }

    [Fact]
    public async Task User_choice_survives_rebuild_and_mark_all_includes_installers()
    {
        using var tmp = new TempDir();
        var settings = Fx.Settings(tmp);
        var a = Entry("a.zip", 100);
        var c = Entry("Setup X v1.exe", 300);
        var library = new FileLibrary(settings);
        library.SetFiles(new[] { a, c });
        var tips = new[] { new SmartTip(TipKind.ExtractedArchive, a, @"C:\D\a"), new SmartTip(TipKind.InstalledProgram, c, "X 1.0") };
        using var vm = new SmartTipsViewModel(library, new FakeService(tips, _ => true), settings, new RecordingOperations(),
            new AppStatus(), new ToastService(settings), NullLogService.Instance);
        await vm.RunAsync(quiet: true);

        vm.Groups[0].Items[0].IsMarked = false;                   // снял отметку с архива
        vm.Groups[1].Items[0].IsMarked = true;                    // отметил установщик
        vm.Rebuild();
        Assert.False(vm.Groups[0].Items[0].IsMarked);
        Assert.True(vm.Groups[1].Items[0].IsMarked);

        vm.MarkAllCommand.Execute(null);
        Assert.Equal(2, vm.MarkedCount);
    }
}
