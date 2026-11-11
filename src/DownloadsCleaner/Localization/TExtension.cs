using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Markup;

namespace DownloadsCleaner.Localization;

/// <summary>
/// Источник привязки для XAML-строк. Индексатор перечитывает словарь при смене языка,
/// а уведомление "Item[]" заставляет WPF обновить все привязки сразу.
/// </summary>
public sealed class TranslationSource : INotifyPropertyChanged
{
    public static TranslationSource Instance { get; } = new();

    private TranslationSource() { }

    public string this[string key] => Loc.T(key);

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void RaiseChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
}

/// <summary>
/// Разметка <c>{loc:T KeyName}</c>: возвращает привязку к текущему переводу.
/// Работает и в Style, и в ControlTemplate, и в обычных свойствах.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }

    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrEmpty(Key)) return string.Empty;

        var binding = new Binding($"[{Key}]")
        {
            Source = TranslationSource.Instance,
            Mode = BindingMode.OneWay
        };

        // В свойство нельзя положить сам Binding: XAML-парсер ожидает выражение.
        // Binding.ProvideValue умеет создать его, если парсер дал цель (IProvideValueTarget).
        var provided = binding.ProvideValue(serviceProvider);
        if (provided is BindingExpression) return provided;

        // Вне XAML-контекста цели нет: показываем текущий перевод без живой привязки.
        return Loc.T(Key);
    }
}
