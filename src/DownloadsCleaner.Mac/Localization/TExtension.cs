using System.ComponentModel;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace DownloadsCleaner.Localization;

/// <summary>
/// Источник привязки для XAML-строк (Avalonia). Индексатор перечитывает словарь при смене языка,
/// а уведомление "Item[]" заставляет обновить все привязки сразу.
/// </summary>
public sealed class TranslationSource : INotifyPropertyChanged
{
    public static TranslationSource Instance { get; } = new();

    private TranslationSource() { }

    public string this[string key] => Loc.T(key);

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void RaiseChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}

/// <summary>
/// Разметка <c>{loc:T KeyName}</c>: возвращает привязку к текущему переводу.
/// Обновляется сама при смене языка интерфейса.
/// </summary>
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrEmpty(Key)) return string.Empty;

        // Avalonia сама создаст выражение привязки и подпишется на уведомления источника.
        return new Binding($"[{Key}]")
        {
            Source = TranslationSource.Instance,
            Mode = BindingMode.OneWay
        };
    }
}
