using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using DownloadsCleaner.Models;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.Mac.Converters;

/// <summary>bool -> IsVisible. ConverterParameter="invert" инвертирует.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = value is true;
        if (parameter is "invert") b = !b;
        return b;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>null -> false, иначе true ("invert" - наоборот).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value != null;
        if (parameter is "invert") visible = !visible;
        return visible;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>bool -> double. Параметр "истина|ложь", например "64|0".</summary>
public sealed class BoolToDoubleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter as string)?.Split('|') ?? Array.Empty<string>();
        var whenTrue = parts.Length > 0 ? parts[0] : "1";
        var whenFalse = parts.Length > 1 ? parts[1] : "0";
        return double.Parse(value is true ? whenTrue : whenFalse, CultureInfo.InvariantCulture);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BytesConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is long l ? DownloadsCleaner.Helpers.ByteFormatter.Format(l) : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Умножает число на параметр (для каскада задержек появления).</summary>
public sealed class MultiplyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        System.Convert.ToDouble(value ?? 0, CultureInfo.InvariantCulture)
        * double.Parse((string)parameter!, CultureInfo.InvariantCulture);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Сравнивает value.ToString() с параметром (для enum и переключателей).</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Категория -> название (с учётом языка интерфейса).</summary>
public sealed class CategoryToTitleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is FileCategory c ? FileCategoryInfo.Title(c) : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Категория -> цвет. Параметр "soft" возвращает полупрозрачную подложку.</summary>
public sealed class CategoryToBrushConverter : IValueConverter
{
    private static readonly Dictionary<(FileCategory, bool), IBrush> Cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var c = value is FileCategory fc ? fc : FileCategory.Other;
        var soft = parameter is "soft";
        lock (Cache)
        {
            if (Cache.TryGetValue((c, soft), out var cached)) return cached;

            var color = Color.Parse(FileCategoryInfo.ColorHex(c));
            if (soft) color = new Color(0x2A, color.R, color.G, color.B);
            var brush = new SolidColorBrush(color);
            Cache[(c, soft)] = brush;
            return brush;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Состояние приложения -> цвет акцента ("soft" - подложка).</summary>
public sealed class AppStateToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var state = value is AppState s ? s : AppState.Ready;
        var soft = parameter is "soft";
        var key = state switch
        {
            AppState.Scanning => "AccentBlue",
            AppState.Sorting => "Accent",
            AppState.Cleaning => "Warning",
            AppState.ScanComplete or AppState.SortComplete or AppState.CleanComplete => "Success",
            AppState.Selection => "Accent",
            AppState.Error => "Danger",
            _ => "TextSecondary"
        };
        return BrushLookup.Find(key, soft) ?? Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Ищет кисть по ключу в ресурсах приложения.</summary>
internal static class BrushLookup
{
    public static IBrush? Find(string key, bool soft)
    {
        var resources = Avalonia.Application.Current?.Resources;
        if (resources == null) return null;

        if (resources.TryGetResource(key + (soft ? "SoftBrush" : "Brush"), null, out var value) && value is IBrush brush)
            return brush;
        if (soft && resources.TryGetResource(key + "Brush", null, out var fallback) && fallback is IBrush plain)
            return plain;
        return null;
    }
}

public sealed class ToastKindToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            ToastKind.Success => "Success",
            ToastKind.Warning => "Warning",
            ToastKind.Error => "Danger",
            _ => "AccentBlue"
        };
        var soft = parameter is "soft";
        return BrushLookup.Find(key, soft) ?? Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
