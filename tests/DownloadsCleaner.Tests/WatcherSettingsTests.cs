using DownloadsCleaner.Models;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.Tests;

public class FileWatcherTests
{
    private static readonly WatcherOptions Fast = new() { StabilityDelay = TimeSpan.FromMilliseconds(250), MaxWait = TimeSpan.FromSeconds(20) };

    private static FileWatcherService Create() => new(Fx.Detector());

    [Fact]
    public async Task New_file_is_reported_once_it_is_complete()
    {
        using var tmp = new TempDir();
        using var w = Create();
        var ready = new List<string>();
        w.FileReady += (_, p) => { lock (ready) ready.Add(p); };
        w.Start(tmp.Downloads, Fast);

        var path = tmp.File("Новый файл.mp3", "data");

        Assert.True(await Fx.WaitAsync(() => { lock (ready) return ready.Count == 1; }, 8000));
        Assert.Equal(path, ready[0], ignoreCase: true);
    }

    [Fact]
    public async Task File_that_is_still_being_written_is_not_reported_until_it_is_released()
    {
        using var tmp = new TempDir();
        using var w = Create();
        var ready = new List<string>();
        w.FileReady += (_, p) => { lock (ready) ready.Add(p); };
        w.Start(tmp.Downloads, Fast);

        var path = Path.Combine(tmp.Downloads, "big.zip");
        // «Скачивающее» приложение держит файл открытым и дописывает данные.
        var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        for (var i = 0; i < 8; i++)
        {
            fs.Write(new byte[1000]);
            fs.Flush();
            await Task.Delay(150);
        }
        lock (ready) Assert.Empty(ready);                      // 1,2 с: файл ещё занят

        fs.Dispose();                                          // загрузка завершена
        Assert.True(await Fx.WaitAsync(() => { lock (ready) return ready.Count == 1; }, 8000));
    }

    [Fact]
    public void Readiness_check_ignores_readers_but_sees_writers()
    {
        using var tmp = new TempDir();
        var path = tmp.File("download.bin", "data");

        // Антивирус/индексатор держат файл открытым на чтение - это не повод считать его занятым.
        using (var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            Assert.True(FileWatcherService.TryOpenExclusive(path));

        // Загрузчик ещё пишет файл - вот это действительно занято.
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
            Assert.False(FileWatcherService.TryOpenExclusive(path));
    }

    [Fact]
    public async Task Temporary_download_is_ignored_until_it_is_renamed_to_the_final_name()
    {
        using var tmp = new TempDir();
        using var w = Create();
        var ready = new List<string>();
        w.FileReady += (_, p) => { lock (ready) ready.Add(Path.GetFileName(p)); };
        w.Start(tmp.Downloads, Fast);

        var partial = tmp.File("video.mp4.crdownload", "partial");
        await Task.Delay(900);
        lock (ready) Assert.Empty(ready);

        File.Move(partial, Path.Combine(tmp.Downloads, "video.mp4"));
        Assert.True(await Fx.WaitAsync(() => { lock (ready) return ready.Contains("video.mp4"); }, 8000));
        lock (ready) Assert.DoesNotContain("video.mp4.crdownload", ready);
    }

    [Fact]
    public async Task Removal_is_reported()
    {
        using var tmp = new TempDir();
        var p = tmp.File("old.txt");
        using var w = Create();
        string? removed = null;
        w.FileRemoved += (_, path) => removed = path;
        w.Start(tmp.Downloads, Fast);

        File.Delete(p);
        Assert.True(await Fx.WaitAsync(() => removed != null, 5000));
        Assert.Equal(p, removed, ignoreCase: true);
    }

    [Fact]
    public async Task Suspended_watcher_ignores_changes_made_by_the_app_itself()
    {
        using var tmp = new TempDir();
        using var w = Create();
        var ready = new List<string>();
        w.FileReady += (_, p) => { lock (ready) ready.Add(Path.GetFileName(p)); };
        w.Start(tmp.Downloads, Fast);

        using (w.Suspend())
        {
            tmp.File("moved-by-app.mp3");
            await Task.Delay(900);
        }
        await Task.Delay(1500);
        lock (ready) Assert.DoesNotContain("moved-by-app.mp3", ready);

        tmp.File("external.mp3");
        Assert.True(await Fx.WaitAsync(() => { lock (ready) return ready.Contains("external.mp3"); }, 8000));
    }

    [Fact]
    public async Task Excluded_folders_are_not_watched()
    {
        using var tmp = new TempDir();
        var skip = Path.Combine(tmp.Downloads, "skip");
        Directory.CreateDirectory(skip);
        using var w = Create();
        var ready = new List<string>();
        w.FileReady += (_, p) => { lock (ready) ready.Add(Path.GetFileName(p)); };
        w.Start(tmp.Downloads, Fast with { ExcludedFolders = new[] { skip } });

        File.WriteAllText(Path.Combine(skip, "hidden.txt"), "x");
        tmp.File("visible.txt");

        Assert.True(await Fx.WaitAsync(() => { lock (ready) return ready.Contains("visible.txt"); }, 8000));
        lock (ready) Assert.DoesNotContain("hidden.txt", ready);
    }

    [Fact]
    public async Task Stop_prevents_further_events()
    {
        using var tmp = new TempDir();
        using var w = Create();
        var count = 0;
        w.FileReady += (_, _) => Interlocked.Increment(ref count);
        w.Start(tmp.Downloads, Fast);
        w.Stop();
        Assert.False(w.IsRunning);

        tmp.File("after-stop.txt");
        await Task.Delay(1000);
        Assert.Equal(0, count);
    }
}

public class SettingsServiceTests
{
    [Fact]
    public void Defaults_are_safe()
    {
        using var tmp = new TempDir();
        using var s = Fx.Settings(tmp);
        Assert.False(s.Current.AutoSortNewFiles);          // автосортировка выключена по умолчанию
        Assert.False(s.Current.AllowPermanentDelete);      // безвозвратное удаление скрыто
        Assert.False(s.Current.AllowContentHashing);       // чтение содержимого только с согласия
        Assert.True(s.Current.AnimationsEnabled);
        Assert.False(string.IsNullOrWhiteSpace(s.Current.DownloadsPath));
        Assert.Equal(30, s.Current.OldFilesDays);
    }

    [Fact]
    public void Settings_survive_restart()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "cfg", "settings.json");
        using (var s = new SettingsService(path))
        {
            s.Current.DownloadsPath = @"D:\Тест\Загрузки";
            s.Current.Theme = AppTheme.Light;
            s.Current.AutoSortNewFiles = true;
            s.Current.OldFilesDays = 90;
            s.Current.AnimationsEnabled = false;
            s.Current.ExcludedExtensions.Add("iso");
            s.Current.ExcludedFolders.Add(@"D:\Тест\Загрузки\skip");
            s.Current.CustomRules.Add(new ExtensionRule("dwg", FileCategory.Documents));
            s.Save();
        }

        using var again = new SettingsService(path);
        Assert.Equal(@"D:\Тест\Загрузки", again.Current.DownloadsPath);
        Assert.Equal(AppTheme.Light, again.Current.Theme);
        Assert.True(again.Current.AutoSortNewFiles);
        Assert.Equal(90, again.Current.OldFilesDays);
        Assert.False(again.Current.AnimationsEnabled);
        Assert.Equal(new[] { "iso" }, again.Current.ExcludedExtensions);
        Assert.Equal(new[] { @"D:\Тест\Загрузки\skip" }, again.Current.ExcludedFolders);
        var rule = Assert.Single(again.Current.CustomRules);
        Assert.Equal(("dwg", FileCategory.Documents), (rule.Extension, rule.Category));
    }

    [Fact]
    public async Task Changes_are_saved_automatically()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "cfg", "settings.json");
        using var s = new SettingsService(path);
        s.Current.ShowNotifications = false;
        s.Current.ExcludedExtensions.Add("tmp2");

        Assert.True(await Fx.WaitAsync(() => File.Exists(path) && File.ReadAllText(path).Contains("tmp2"), 4000));
    }

    [Fact]
    public void Corrupted_file_falls_back_to_defaults_and_keeps_a_backup()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "cfg", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ this is not json");

        using var s = new SettingsService(path);
        Assert.False(s.Current.AutoSortNewFiles);
        Assert.True(File.Exists(path + ".broken"));
    }
}

public class HistoryAndLibraryTests
{
    [Fact]
    public void History_is_persisted_and_can_mark_entries_undone()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "history.json");
        var h = new HistoryService(path);
        var e = new HistoryEntry
        {
            Kind = HistoryKind.Sort, Title = "Сортировка", Count = 1,
            Moves = { new MoveRecord(@"C:\a\x.mp3", @"C:\a\Музыка\x.mp3", FileCategory.Audio, 5) }
        };
        h.Add(e);
        Assert.True(h.Entries[0].CanUndo);

        h.MarkUndone(e.Id);
        var reloaded = new HistoryService(path);
        var loaded = Assert.Single(reloaded.Entries);
        Assert.True(loaded.Undone);
        Assert.False(loaded.CanUndo);
        Assert.Equal(@"C:\a\Музыка\x.mp3", loaded.Moves[0].TargetPath);

        reloaded.Clear();
        Assert.Empty(new HistoryService(path).Entries);
    }

    [Fact]
    public void History_keeps_only_unrestored_moves_after_partial_undo()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "history.json");
        using var h = new HistoryService(path);
        var first = new MoveRecord(@"C:\DL\a.mp3", @"C:\DL\Музыка\a.mp3", FileCategory.Audio, 5);
        var second = new MoveRecord(@"C:\DL\b.mp3", @"C:\DL\Музыка\b.mp3", FileCategory.Audio, 7);
        var e = new HistoryEntry { Kind = HistoryKind.Sort, Title = "Сортировка", Moves = { first, second } };
        h.Add(e);

        // Один файл вернуть не удалось - он остаётся в записи и доступен для повторной попытки.
        h.SetMoves(e.Id, new[] { second });
        Assert.True(h.Entries[0].CanUndo);
        Assert.Equal(second, Assert.Single(h.Entries[0].Moves));

        h.SetMoves(e.Id, Array.Empty<MoveRecord>());
        Assert.False(h.Entries[0].CanUndo);
        Assert.True(h.Entries[0].Undone);
    }

    [Fact]
    public void Duplicates_result_drops_files_removed_during_the_search()
    {
        using var tmp = new TempDir();
        using var s = Fx.Settings(tmp);
        s.Current.DownloadsPath = @"C:\DL";
        var lib = new FileLibrary(s);
        var a = Entry(@"C:\DL\a.bin", 10, 1);
        var b = Entry(@"C:\DL\b.bin", 10, 2);
        var c = Entry(@"C:\DL\c.bin", 10, 3);
        lib.SetFiles(new[] { a, b, c });
        lib.Remove(new[] { b.FullPath });                 // файл исчез, пока шёл поиск

        lib.SetDuplicates(new[] { new DuplicateGroup("H", 10, new[] { a, b, c }) });

        var group = Assert.Single(lib.Duplicates);
        Assert.Equal(2, group.Files.Count);
        Assert.DoesNotContain(group.Files, f => f.FullPath == b.FullPath);
    }

    private static FileEntry Entry(string path, long size, int ageDays, FileCategory cat = FileCategory.Other, bool temp = false) =>
        new(path, Path.GetFileName(path), Path.GetExtension(path).TrimStart('.'), size,
            DateTime.Now.AddDays(-ageDays), DateTime.Now.AddDays(-ageDays), cat, temp);

    [Fact]
    public void Library_statistics_count_old_temporary_sortable_and_categories()
    {
        using var tmp = new TempDir();
        using var s = Fx.Settings(tmp);
        s.Current.DownloadsPath = @"C:\DL";
        s.Current.OldFilesDays = 30;
        var lib = new FileLibrary(s);

        lib.SetFiles(new[]
        {
            Entry(@"C:\DL\a.mp3", 100, 1, FileCategory.Audio),
            Entry(@"C:\DL\b.mp3", 200, 40, FileCategory.Audio),
            Entry(@"C:\DL\c.part", 50, 2, FileCategory.Other, temp: true),
            Entry(@"C:\DL\Музыка\d.mp3", 400, 90, FileCategory.Audio),
        });

        var st = lib.Stats;
        Assert.Equal(4, st.TotalFiles);
        Assert.Equal(750, st.TotalBytes);
        Assert.Equal(2, st.OldCount);                  // b и d старше 30 дней
        Assert.Equal(1, st.TempCount);
        Assert.Equal(2, st.SortableCount);             // a и b: в корне и не временные
        Assert.Equal(3, st.Categories.Single(c => c.Category == FileCategory.Audio).Count);
        Assert.False(st.DuplicatesKnown);
    }

    [Fact]
    public void Library_removes_files_updates_duplicates_and_handles_moves()
    {
        using var tmp = new TempDir();
        using var s = Fx.Settings(tmp);
        s.Current.DownloadsPath = @"C:\DL";
        var lib = new FileLibrary(s);
        var a = Entry(@"C:\DL\a.bin", 10, 1);
        var b = Entry(@"C:\DL\b.bin", 10, 2);
        var c = Entry(@"C:\DL\c.bin", 10, 3);
        lib.SetFiles(new[] { a, b, c });
        lib.SetDuplicates(new[] { new DuplicateGroup("H", 10, new[] { a, b, c }) });
        Assert.Equal(2, lib.Stats.DuplicateCopies);
        Assert.Equal(20, lib.Stats.DuplicateBytes);
        Assert.True(lib.Stats.DuplicatesKnown);

        // Новый файл мог образовать новую группу - прежний результат поиска устарел.
        lib.AddOrUpdate(Entry(@"C:\DL\new.bin", 10, 4));
        Assert.False(lib.Stats.DuplicatesKnown);
        lib.Remove(new[] { @"C:\DL\new.bin" });

        lib.Remove(new[] { @"C:\DL\a.bin" });
        Assert.Equal(1, lib.Stats.DuplicateCopies);

        lib.Remove(new[] { @"C:\DL\b.bin" });           // осталась одна копия - группы больше нет
        Assert.Empty(lib.Duplicates);

        // перемещение внутри папки обновляет путь, перемещение наружу убирает файл из библиотеки
        lib.ApplyMoves(new[] { new MoveRecord(@"C:\DL\c.bin", @"C:\DL\Прочее\c.bin", FileCategory.Other, 10) });
        Assert.True(lib.Contains(@"C:\DL\Прочее\c.bin"));
        Assert.False(lib.Contains(@"C:\DL\c.bin"));
        lib.ApplyMoves(new[] { new MoveRecord(@"C:\DL\Прочее\c.bin", @"D:\Elsewhere\c.bin", FileCategory.Other, 10) });
        Assert.Empty(lib.Files);
    }
}
