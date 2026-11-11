namespace DownloadsCleaner.Models;

public sealed record OperationProgress(int Done, int Total, string Current)
{
    public double Fraction => Total <= 0 ? 0 : (double)Done / Total;
}

// ---------- Сканирование ----------

public sealed record ScanOptions(
    bool IncludeSubfolders,
    IReadOnlyCollection<string> ExcludedFolders,
    IReadOnlyCollection<string> ExcludedExtensions);

public sealed record ScanProgress(int FilesFound, long BytesFound, string CurrentFolder);

public sealed class ScanResult
{
    public required IReadOnlyList<FileEntry> Files { get; init; }
    /// <summary>Сколько папок/файлов не удалось прочитать (нет доступа и т.п.).</summary>
    public int InaccessibleCount { get; init; }
    public TimeSpan Duration { get; init; }
}

// ---------- Сортировка ----------

public sealed record SortOptions(
    string SourceRoot,
    string DestinationRoot,
    bool IncludeSubfolders,
    IReadOnlyCollection<string> ExcludedExtensions)
{
    public Func<FileCategory, string> FolderName { get; init; } = FileCategoryInfo.FolderName;
}

public sealed record PlannedMove(FileEntry File, string TargetPath, bool Renamed)
{
    public string TargetFolder => Path.GetDirectoryName(TargetPath) ?? string.Empty;
}

public sealed record SkippedFile(FileEntry File, string Reason);

public sealed class SortPlan
{
    public required SortOptions Options { get; init; }
    public required IReadOnlyList<PlannedMove> Moves { get; init; }
    public required IReadOnlyList<SkippedFile> Skipped { get; init; }
    public int Count => Moves.Count;
    public long TotalBytes => Moves.Sum(m => m.File.Size);
    public int RenamedCount => Moves.Count(m => m.Renamed);

    public IEnumerable<(FileCategory Category, int Count, long Bytes)> ByCategory =>
        Moves.GroupBy(m => m.File.Category)
             .Select(g => (g.Key, g.Count(), g.Sum(x => x.File.Size)))
             .OrderBy(t => t.Key);
}

/// <summary>Одно выполненное перемещение (достаточно для отмены).</summary>
public sealed record MoveRecord(string SourcePath, string TargetPath, FileCategory Category, long Size)
{
    /// <summary>
    /// Папка назначения была создана этой операцией. При отмене удаляем только такие
    /// папки: пустая папка, существовавшая до сортировки, - чужая, её трогать нельзя.
    /// </summary>
    public bool CreatedTargetDirectory { get; init; }
}

public sealed record FailedOperation(string Path, string Reason);

public sealed class SortResult
{
    public required IReadOnlyList<MoveRecord> Moved { get; init; }
    public required IReadOnlyList<FailedOperation> Failed { get; init; }
    public bool Cancelled { get; init; }
    public long MovedBytes => Moved.Sum(m => m.Size);
}

public sealed class UndoResult
{
    public required IReadOnlyList<MoveRecord> Restored { get; init; }
    public required IReadOnlyList<FailedOperation> Failed { get; init; }
    public bool Cancelled { get; init; }
}

// ---------- Удаление ----------

public enum DeleteMode
{
    /// <summary>Корзина Windows (по умолчанию).</summary>
    Recycle,
    /// <summary>Безвозвратно.</summary>
    Permanent
}

public sealed class DeletionResult
{
    public required DeleteMode Mode { get; init; }
    public required IReadOnlyList<FileEntry> Deleted { get; init; }
    public required IReadOnlyList<FailedOperation> Failed { get; init; }
    public bool Cancelled { get; init; }
    /// <summary>
    /// Файлы, которые не поместились в Корзину: они остались на месте, а не были уничтожены.
    /// </summary>
    public IReadOnlyList<FailedOperation> NotRecycled { get; init; } = Array.Empty<FailedOperation>();
    public long BytesFreed => Deleted.Sum(f => f.Size);
}

// ---------- Дубликаты ----------

public sealed record DuplicateGroup(string Hash, long Size, IReadOnlyList<FileEntry> Files)
{
    /// <summary>Сколько места займут лишние копии.</summary>
    public long WastedBytes => Size * (Files.Count - 1);
}

public sealed record DuplicateProgress(string Stage, long BytesDone, long BytesTotal, int FilesDone, int FilesTotal)
{
    public double Fraction => BytesTotal <= 0 ? (FilesTotal <= 0 ? 0 : (double)FilesDone / FilesTotal) : (double)BytesDone / BytesTotal;
}

// ---------- История ----------

public enum HistoryKind
{
    Sort,
    AutoSort,
    Recycle,
    PermanentDelete,
    Undo
}

public sealed class HistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Time { get; set; } = DateTime.Now;
    public HistoryKind Kind { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public int Count { get; set; }
    public long Bytes { get; set; }
    public List<MoveRecord> Moves { get; set; } = new();
    public bool Undone { get; set; }
    public bool CanUndo => !Undone && Moves.Count > 0 && (Kind is HistoryKind.Sort or HistoryKind.AutoSort);

    /// <summary>Копия записи без данных для отмены (для усечения файла истории).</summary>
    public HistoryEntry WithoutMoves() => new()
    {
        Id = Id,
        Time = Time,
        Kind = Kind,
        Title = Title,
        Details = Details,
        Count = Count,
        Bytes = Bytes,
        Moves = new List<MoveRecord>(),
        Undone = Undone
    };
}
