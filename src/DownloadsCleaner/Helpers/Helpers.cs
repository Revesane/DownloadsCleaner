using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;

namespace DownloadsCleaner.Helpers;

/// <summary>Форматирование размера файла.</summary>
public static class ByteFormatter
{
    private static string UnitName(int index) => Localization.Loc.T(index switch
    {
        0 => "UnitByte",
        1 => "UnitKB",
        2 => "UnitMB",
        3 => "UnitGB",
        _ => "UnitTB"
    });

    public static string Format(long bytes) => Format((double)bytes);

    public static string Format(double bytes)
    {
        if (bytes < 0) bytes = 0;
        if (bytes < 1024) return $"{bytes:0} {UnitName(0)}";
        var unit = 0;
        while (bytes >= 1024 && unit < 4)
        {
            bytes /= 1024;
            unit++;
        }
        var format = bytes >= 100 ? "0" : bytes >= 10 ? "0.#" : "0.##";
        return bytes.ToString(format, CultureInfo.CurrentCulture) + " " + UnitName(unit);
    }

    /// <summary>
    /// Склонение по правилам языка интерфейса. Аргументы - ключи переводов:
    /// русский использует три формы, английский - две (см. <see cref="Localization.Loc.Plural"/>).
    /// </summary>
    public static string Files(int n) => $"{n:N0} {Localization.Loc.Plural(n, "FileOne", "FileFew", "FileMany")}";
}

/// <summary>Работа с путями.</summary>
public static class PathHelper
{
    private static readonly StringComparison Cmp = StringComparison.OrdinalIgnoreCase;

    public static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    public static bool AreSame(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), Cmp);

    /// <summary>true, если path находится внутри root (на любой глубине) и не совпадает с ним.</summary>
    public static bool IsInside(string path, string root)
    {
        var p = Normalize(path);
        var r = Normalize(root);
        return p.Length > r.Length
               && p.StartsWith(r, Cmp)
               && (p[r.Length] == Path.DirectorySeparatorChar || p[r.Length] == Path.AltDirectorySeparatorChar);
    }

    /// <summary>Подбирает свободное имя "имя (1).расш", не перезаписывая существующие файлы.</summary>
    public static string GetUniquePath(string desiredPath, Func<string, bool>? alsoTaken = null)
    {
        bool Taken(string p) => File.Exists(p) || Directory.Exists(p) || (alsoTaken?.Invoke(p) ?? false);
        if (!Taken(desiredPath)) return desiredPath;
        var dir = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(desiredPath);
        var ext = Path.GetExtension(desiredPath);
        for (var i = 1; i < 10_000; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!Taken(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{name} ({Guid.NewGuid():N}){ext}");
    }

    public static bool IsExcludedFolder(string path, IEnumerable<string> excluded)
    {
        foreach (var e in excluded)
        {
            if (string.IsNullOrWhiteSpace(e)) continue;
            try
            {
                if (AreSame(path, e) || IsInside(path, e)) return true;
            }
            catch { /* некорректный путь в исключениях - игнорируем */ }
        }
        return false;
    }
}

/// <summary>Коллекция с массовой заменой содержимого одним уведомлением (быстро для десятков тысяч элементов).</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var i in items) Items.Add(i);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void RemoveWhere(Func<T, bool> predicate)
    {
        var keep = Items.Where(i => !predicate(i)).ToList();
        if (keep.Count == Items.Count) return;
        ReplaceAll(keep);
    }
}
