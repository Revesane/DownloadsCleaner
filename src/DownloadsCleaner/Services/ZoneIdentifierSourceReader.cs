using DownloadsCleaner.Models;

namespace DownloadsCleaner.Services;

/// <summary>
/// Источник загрузки из метки «Mark of the Web»: браузеры и многие программы записывают
/// в альтернативный поток NTFS "файл:Zone.Identifier" адрес страницы (ReferrerUrl)
/// и самого файла (HostUrl). Метку пишет Windows-оболочка, поэтому читаем её только здесь.
/// </summary>
public sealed class ZoneIdentifierSourceReader : IDownloadSourceReader
{
    private const string StreamSuffix = ":Zone.Identifier";
    /// <summary>Настоящая метка - несколько строк; всё, что больше, - не она.</summary>
    private const long MaxStreamBytes = 64 * 1024;

    public DownloadSource? TryRead(string path)
    {
        try
        {
            var stream = path + StreamSuffix;
            // Exists не бросает исключение для файлов без метки (их большинство) - это
            // заметно быстрее, чем ловить FileNotFoundException на каждом файле.
            if (!File.Exists(stream)) return null;
            using var fs = new FileStream(stream, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            if (fs.Length > MaxStreamBytes) return null;
            using var reader = new StreamReader(fs);
            return Parse(reader.ReadToEnd());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Сайт берётся из адреса страницы (ReferrerUrl), а если его нет - из адреса файла (HostUrl):
    /// для релиза на GitHub страница - github.com, а файл лежит на objects.githubusercontent.com.
    /// Заглушки вроде "about:internet" (так помечаются, например, распакованные файлы) пропускаются.
    /// </summary>
    internal static DownloadSource? Parse(string content)
    {
        string? referrer = null, host = null;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Equals("ReferrerUrl", StringComparison.OrdinalIgnoreCase)) referrer = value;
            else if (key.Equals("HostUrl", StringComparison.OrdinalIgnoreCase)) host = value;
        }

        foreach (var candidate in new[] { referrer, host })
        {
            if (TryGetWebUri(candidate) is { } uri)
                return new DownloadSource(NormalizeHost(uri.Host), uri.ToString());
        }
        return null;
    }

    private static Uri? TryGetWebUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        // Файлы из веб-версий мессенджеров сохраняются как blob:https://web.telegram.org/...
        if (value.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)) value = value[5..];
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeFtp) return null;
        return uri.Host.Length == 0 ? null : uri;
    }

    private static string NormalizeHost(string host)
    {
        host = host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }
}
