using DownloadsCleaner.Models;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.Tests;

public class FileOrganizationTests
{
    private readonly FileOrganizationService _org = new();

    private static async Task<IReadOnlyList<FileEntry>> Scan(TempDir tmp) =>
        await Fx.ScanAsync(tmp.Downloads, Fx.Detector());

    [Fact]
    public async Task Plan_maps_every_category_to_its_folder_and_changes_nothing_on_disk()
    {
        using var tmp = new TempDir();
        tmp.File("a.mp3"); tmp.File("b.mp4"); tmp.File("c.png"); tmp.File("d.pdf");
        tmp.File("e.zip"); tmp.File("f.exe"); tmp.File("g.stl"); tmp.File("h.unknownext");

        var plan = _org.BuildPlan(await Scan(tmp), Fx.SortOpts(tmp.Downloads));

        Assert.Equal(8, plan.Count);
        string FolderOf(string name) => Path.GetFileName(plan.Moves.Single(m => m.File.Name == name).TargetFolder);
        Assert.Equal("Музыка", FolderOf("a.mp3"));
        Assert.Equal("Видео", FolderOf("b.mp4"));
        Assert.Equal("Изображения", FolderOf("c.png"));
        Assert.Equal("Документы", FolderOf("d.pdf"));
        Assert.Equal("Архивы", FolderOf("e.zip"));
        Assert.Equal("Программы", FolderOf("f.exe"));
        Assert.Equal("3D-модели", FolderOf("g.stl"));
        Assert.Equal("Прочее", FolderOf("h.unknownext"));
        Assert.Equal(8, plan.TotalBytes / 7);                     // "content" = 7 байт
        Assert.Equal(8, Directory.GetFiles(tmp.Downloads).Length);  // физически ничего не перемещено
        Assert.Empty(Directory.GetDirectories(tmp.Downloads));
    }

    [Fact]
    public async Task Plan_skips_temporary_excluded_nested_and_already_sorted_files()
    {
        using var tmp = new TempDir();
        tmp.File("movie.mp4.crdownload");
        tmp.File("keep.iso");
        tmp.File("nested\\inside.mp3");
        tmp.File("Музыка\\already.mp3");
        tmp.File("fresh.mp3");

        var plan = _org.BuildPlan(await Scan(tmp), Fx.SortOpts(tmp.Downloads, excluded: "iso"));

        Assert.Equal(new[] { "fresh.mp3" }, plan.Moves.Select(m => m.File.Name));
        Assert.Equal(4, plan.Skipped.Count);
        Assert.Contains(plan.Skipped, s => s.File.Name == "already.mp3" && s.Reason.Contains("уже"));

        // Если явно разрешены вложенные папки, файл из nested/ тоже попадает в план.
        var withSub = _org.BuildPlan(await Scan(tmp), Fx.SortOpts(tmp.Downloads, null, true, "iso"));
        Assert.Contains(withSub.Moves, m => m.File.Name == "inside.mp3");
    }

    [Fact]
    public async Task Name_collisions_never_overwrite_existing_files()
    {
        using var tmp = new TempDir();
        tmp.File("Музыка\\song.mp3", "ORIGINAL");
        tmp.File("Музыка\\song (1).mp3", "ORIGINAL-1");
        var src = tmp.File("song.mp3", "NEW");

        var plan = _org.BuildPlan(await Scan(tmp), Fx.SortOpts(tmp.Downloads));
        var move = Assert.Single(plan.Moves.Where(m => m.File.FullPath == src));
        Assert.True(move.Renamed);
        Assert.EndsWith("song (2).mp3", move.TargetPath);

        var result = await _org.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Single(result.Moved);
        Assert.Equal("ORIGINAL", File.ReadAllText(Path.Combine(tmp.Downloads, "Музыка", "song.mp3")));
        Assert.Equal("ORIGINAL-1", File.ReadAllText(Path.Combine(tmp.Downloads, "Музыка", "song (1).mp3")));
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(tmp.Downloads, "Музыка", "song (2).mp3")));
        Assert.False(File.Exists(src));
    }

    [Fact]
    public async Task Same_name_files_from_different_folders_get_unique_targets_within_one_plan()
    {
        using var tmp = new TempDir();
        tmp.File("a\\report.pdf", "A");
        tmp.File("b\\report.pdf", "B");
        tmp.File("c\\report.pdf", "C");

        var plan = _org.BuildPlan(await Scan(tmp), Fx.SortOpts(tmp.Downloads, includeSubfolders: true));
        Assert.Equal(3, plan.Moves.Select(m => m.TargetPath.ToLowerInvariant()).Distinct().Count());

        var result = await _org.ExecuteAsync(plan, null, CancellationToken.None);
        Assert.Equal(3, result.Moved.Count);
        var contents = Directory.GetFiles(Path.Combine(tmp.Downloads, "Документы")).Select(File.ReadAllText).OrderBy(x => x);
        Assert.Equal(new[] { "A", "B", "C" }, contents);
    }

    [Fact]
    public async Task Destination_taken_after_planning_is_handled_without_overwrite()
    {
        using var tmp = new TempDir();
        var src = tmp.File("clip.mp4", "NEW");
        var plan = _org.BuildPlan(await Scan(tmp), Fx.SortOpts(tmp.Downloads));

        // Между просмотром и подтверждением в целевой папке появился файл с тем же именем.
        var target = plan.Moves[0].TargetPath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "APPEARED-LATER");

        var result = await _org.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Single(result.Moved);
        Assert.Equal("APPEARED-LATER", File.ReadAllText(target));
        Assert.Equal("NEW", File.ReadAllText(result.Moved[0].TargetPath));
        Assert.NotEqual(target, result.Moved[0].TargetPath);
        Assert.False(File.Exists(src));
    }

    [Fact]
    public void Moving_a_file_onto_itself_is_rejected()
    {
        using var tmp = new TempDir();
        var f = tmp.File("same.txt", "x");
        Assert.Throws<IOException>(() => FileOrganizationService.MoveWithoutOverwrite(f, f));
        Assert.Throws<IOException>(() => FileOrganizationService.MoveWithoutOverwrite(f, f.ToUpperInvariant()));
        Assert.Equal("x", File.ReadAllText(f));
    }

    [Fact]
    public async Task Locked_file_is_reported_as_error_while_others_are_moved()
    {
        using var tmp = new TempDir();
        var locked = tmp.File("busy.mp4", "BUSY");
        tmp.File("free.mp3", "FREE");
        var plan = _org.BuildPlan(await Scan(tmp), Fx.SortOpts(tmp.Downloads));

        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = await _org.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Single(result.Moved);
        var failure = Assert.Single(result.Failed);
        Assert.Equal(locked, failure.Path);
        Assert.False(string.IsNullOrWhiteSpace(failure.Reason));
        Assert.True(File.Exists(locked));                          // файл остался на месте
        Assert.True(File.Exists(Path.Combine(tmp.Downloads, "Музыка", "free.mp3")));
    }

    [Fact]
    public async Task Missing_source_file_is_reported_not_thrown()
    {
        using var tmp = new TempDir();
        var f = tmp.File("gone.pdf");
        var plan = _org.BuildPlan(await Scan(tmp), Fx.SortOpts(tmp.Downloads));
        File.Delete(f);

        var result = await _org.ExecuteAsync(plan, null, CancellationToken.None);
        Assert.Empty(result.Moved);
        Assert.Single(result.Failed);
    }

    [Fact]
    public async Task Sorting_can_be_cancelled_and_reports_partial_progress()
    {
        using var tmp = new TempDir();
        for (var i = 0; i < 30; i++) tmp.File($"f{i:00}.mp3");
        var plan = _org.BuildPlan(await Scan(tmp), Fx.SortOpts(tmp.Downloads));

        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress<OperationProgress>(p => { if (p.Done >= 10) cts.Cancel(); });
        var result = await _org.ExecuteAsync(plan, progress, cts.Token);

        Assert.True(result.Cancelled);
        Assert.InRange(result.Moved.Count, 10, 29);
        Assert.Equal(30, Directory.GetFiles(tmp.Downloads, "*.mp3", SearchOption.AllDirectories).Length);   // ни один файл не потерян
    }

    [Fact]
    public async Task Pre_cancelled_sort_moves_nothing()
    {
        using var tmp = new TempDir();
        tmp.File("a.mp3");
        var plan = _org.BuildPlan(await Scan(tmp), Fx.SortOpts(tmp.Downloads));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _org.ExecuteAsync(plan, null, cts.Token);
        Assert.True(result.Cancelled);
        Assert.Empty(result.Moved);
        Assert.True(File.Exists(Path.Combine(tmp.Downloads, "a.mp3")));
    }

    [Fact]
    public async Task Custom_destination_folder_is_used()
    {
        using var tmp = new TempDir();
        tmp.File("a.pdf");
        var dest = Path.Combine(tmp.Path, "Elsewhere");
        var plan = _org.BuildPlan(await Scan(tmp), Fx.SortOpts(tmp.Downloads, dest));

        var result = await _org.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(dest, "Документы", "a.pdf")));
        Assert.Single(result.Moved);
    }

    [Fact]
    public async Task Undo_restores_files_removes_empty_category_folders_and_never_overwrites()
    {
        using var tmp = new TempDir();
        var a = tmp.File("a.mp3", "A");
        var b = tmp.File("b.pdf", "B");
        var plan = _org.BuildPlan(await Scan(tmp), Fx.SortOpts(tmp.Downloads));
        var sorted = await _org.ExecuteAsync(plan, null, CancellationToken.None);
        Assert.Equal(2, sorted.Moved.Count);

        // На место одного из файлов пока кто-то положил другой файл.
        File.WriteAllText(a, "SOMEONE-ELSE");

        var undo = await _org.UndoAsync(sorted.Moved, null, CancellationToken.None);

        Assert.Equal(2, undo.Restored.Count);
        Assert.Empty(undo.Failed);
        Assert.Equal("SOMEONE-ELSE", File.ReadAllText(a));              // чужой файл не тронут
        Assert.Equal("A", File.ReadAllText(Path.Combine(tmp.Downloads, "a (1).mp3")));
        Assert.Equal("B", File.ReadAllText(b));
        Assert.Empty(Directory.GetDirectories(tmp.Downloads));           // пустые папки категорий убраны
    }

    [Fact]
    public async Task Undo_does_not_delete_empty_folder_that_existed_before_sorting()
    {
        using var tmp = new TempDir();
        var existing = Path.Combine(tmp.Downloads, "Документы");
        Directory.CreateDirectory(existing);              // папка пользователя, не наша
        tmp.File("a.pdf", "A");

        var plan = _org.BuildPlan(await Scan(tmp), Fx.SortOpts(tmp.Downloads));
        var sorted = await _org.ExecuteAsync(plan, null, CancellationToken.None);
        var undo = await _org.UndoAsync(sorted.Moved, null, CancellationToken.None);

        Assert.Single(undo.Restored);
        Assert.True(Directory.Exists(existing));          // чужую пустую папку не удаляем
    }

    [Fact]
    public async Task Sort_remembers_only_directories_it_created()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Downloads, "Документы"));
        tmp.File("a.pdf");
        tmp.File("b.mp3");

        var plan = _org.BuildPlan(await Scan(tmp), Fx.SortOpts(tmp.Downloads));
        var sorted = await _org.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.False(sorted.Moved.Single(m => m.Category == FileCategory.Documents).CreatedTargetDirectory);
        Assert.True(sorted.Moved.Single(m => m.Category == FileCategory.Audio).CreatedTargetDirectory);
    }

    [Fact]
    public async Task Undo_reports_files_that_no_longer_exist()
    {
        using var tmp = new TempDir();
        tmp.File("a.mp3");
        var plan = _org.BuildPlan(await Scan(tmp), Fx.SortOpts(tmp.Downloads));
        var sorted = await _org.ExecuteAsync(plan, null, CancellationToken.None);
        File.Delete(sorted.Moved[0].TargetPath);

        var undo = await _org.UndoAsync(sorted.Moved, null, CancellationToken.None);
        Assert.Empty(undo.Restored);
        Assert.Single(undo.Failed);
    }
}
