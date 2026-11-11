using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using DownloadsCleaner.Models;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.Converters;

/// <summary>bool -> Visibility. ConverterParameter="invert" инвертирует.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is true;
        if (parameter is "invert") b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>null -> Collapsed, иначе Visible ("invert" - наоборот).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value != null;
        if (parameter is "invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>bool -> double. Параметр "истина|ложь", например "64|0".</summary>
public sealed class BoolToDoubleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var parts = (parameter as string)?.Split('|') ?? Array.Empty<string>();
        var whenTrue = parts.Length > 0 ? parts[0] : "1";
        var whenFalse = parts.Length > 1 ? parts[1] : "0";
        return double.Parse(value is true ? whenTrue : whenFalse, CultureInfo.InvariantCulture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long l ? DownloadsCleaner.Helpers.ByteFormatter.Format(l) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Умножает число на параметр (для каскада задержек появления).</summary>
public sealed class MultiplyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        System.Convert.ToDouble(value, CultureInfo.InvariantCulture)
        * double.Parse((string)parameter, CultureInfo.InvariantCulture);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Сравнивает value.ToString() с параметром (для enum и переключателей).</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class CategoryToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is FileCategory c ? FileCategoryInfo.Glyph(c) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class CategoryToTitleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is FileCategory c ? FileCategoryInfo.Title(c) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Категория -> цвет. Параметр "soft" возвращает полупрозрачную подложку.</summary>
public sealed class CategoryToBrushConverter : IValueConverter
{
    private static readonly Dictionary<(FileCategory, bool), Brush> Cache = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var c = value is FileCategory fc ? fc : FileCategory.Other;
        var soft = parameter is "soft";
        lock (Cache)
        {
            if (Cache.TryGetValue((c, soft), out var cached)) return cached;
            var color = (Color)ColorConverter.ConvertFromString(FileCategoryInfo.ColorHex(c));
            if (soft) color = Color.FromArgb(0x2A, color.R, color.G, color.B);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Cache[(c, soft)] = brush;
            return brush;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Состояние приложения -> цвет акцента ("soft" - подложка).</summary>
public sealed class AppStateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
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
        var resource = Application.Current?.TryFindResource(key + (soft ? "SoftBrush" : "Brush"));
        if (resource == null && soft) resource = Application.Current?.TryFindResource(key + "Brush");
        return resource ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class AppStateToGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is AppState s ? s : AppState.Ready) switch
        {
            AppState.Scanning or AppState.Sorting or AppState.Cleaning => "",
            AppState.ScanComplete or AppState.SortComplete or AppState.CleanComplete => "",
            AppState.Selection => "",
            AppState.Error => "",
            AppState.Launched => "",
            _ => ""
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class IsBusyStateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is AppState.Scanning or AppState.Sorting or AppState.Cleaning;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Стрелка сортировки для заголовка столбца. Параметр - имя столбца (FileSortKey).</summary>
public sealed class SortGlyphConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] == null) return string.Empty;
        if (!string.Equals(values[0].ToString(), parameter?.ToString(), StringComparison.Ordinal)) return string.Empty;
        return values[1] is true ? "" : "";   // вниз / вверх
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ToastKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            ToastKind.Success => "Success",
            ToastKind.Warning => "Warning",
            ToastKind.Error => "Danger",
            _ => "AccentBlue"
        };
        var soft = parameter is "soft";
        return Application.Current?.TryFindResource(key + (soft ? "SoftBrush" : "Brush")) ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
