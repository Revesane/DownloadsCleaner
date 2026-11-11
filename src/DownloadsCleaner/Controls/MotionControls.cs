using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DownloadsCleaner.Animations;
using DownloadsCleaner.Helpers;

namespace DownloadsCleaner.Controls;

public enum NumberFormat { Count, Bytes, Percent }

/// <summary>Число, которое плавно «досчитывается» до нового значения (статистика, счётчики).</summary>
public sealed class AnimatedNumber : TextBlock
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(AnimatedNumber), new PropertyMetadata(0.0, OnValueChanged));

    public static readonly DependencyProperty FormatProperty = DependencyProperty.Register(
        nameof(Format), typeof(NumberFormat), typeof(AnimatedNumber),
        new PropertyMetadata(NumberFormat.Count, (d, _) => ((AnimatedNumber)d).Render()));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(string), typeof(AnimatedNumber),
        new PropertyMetadata(null, (d, _) => ((AnimatedNumber)d).Render()));

    private static readonly DependencyProperty DisplayValueProperty = DependencyProperty.Register(
        nameof(DisplayValue), typeof(double), typeof(AnimatedNumber),
        new PropertyMetadata(0.0, (d, _) => ((AnimatedNumber)d).Render()));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public NumberFormat Format { get => (NumberFormat)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }
    /// <summary>Текст вместо числа (например «—», пока нет данных). Если null - показывается число.</summary>
    public string? Placeholder { get => (string?)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }
    private double DisplayValue => (double)GetValue(DisplayValueProperty);

    public AnimatedNumber() => Render();

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var n = (AnimatedNumber)d;
        Anim.To(n, DisplayValueProperty, (double)e.NewValue, MotionSettings.Counter, Anim.EaseOut);
    }

    private void Render()
    {
        if (Placeholder != null) { Text = Placeholder; return; }
        var v = DisplayValue;
        Text = Format switch
        {
            NumberFormat.Bytes => ByteFormatter.Format(v),
            NumberFormat.Percent => Math.Round(v).ToString("0", CultureInfo.CurrentCulture) + " %",
            _ => Math.Round(v).ToString("N0", CultureInfo.CurrentCulture)
        };
    }
}

/// <summary>ContentControl, плавно показывающий новое содержимое (переключение страниц и состояний).</summary>
public sealed class TransitioningContentControl : ContentControl
{
    public static readonly DependencyProperty OffsetProperty = DependencyProperty.Register(
        nameof(Offset), typeof(double), typeof(TransitioningContentControl), new PropertyMetadata(14.0));

    public double Offset { get => (double)GetValue(OffsetProperty); set => SetValue(OffsetProperty, value); }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        if (!IsLoaded || !MotionSettings.Enabled || newContent == null) return;

        var (_, t) = Anim.Transforms(this);
        Anim.To(this, OpacityProperty, 1, MotionSettings.Slow, Anim.EaseOut, 0);
        Anim.To(t, TranslateTransform.YProperty, 0, MotionSettings.Slow, Anim.EaseOut, Offset);
    }
}

/// <summary>Раскрывающаяся панель: высота плавно меняется между 0 и высотой содержимого.</summary>
public sealed class ExpandablePanel : Decorator
{
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(ExpandablePanel),
        new PropertyMetadata(false, (d, _) => ((ExpandablePanel)d).Update(true)));

    public bool IsExpanded { get => (bool)GetValue(IsExpandedProperty); set => SetValue(IsExpandedProperty, value); }

    public ExpandablePanel()
    {
        ClipToBounds = true;
        Loaded += (_, _) => Update(false);
    }

    private void Update(bool animate)
    {
        var child = Child;
        if (child == null) return;

        if (!animate || !IsLoaded || !MotionSettings.Enabled)
        {
            Anim.Set(this, HeightProperty, double.NaN);
            Visibility = IsExpanded ? Visibility.Visible : Visibility.Collapsed;
            child.Opacity = 1;
            return;
        }

        if (IsExpanded)
        {
            Visibility = Visibility.Visible;
            var width = ActualWidth > 0 ? ActualWidth : double.PositiveInfinity;
            child.Measure(new Size(width, double.PositiveInfinity));
            var target = child.DesiredSize.Height;
            Anim.To(this, HeightProperty, target, MotionSettings.Slow, Anim.EaseInOut, 0, () => Height = double.NaN);
            Anim.To(child, OpacityProperty, 1, MotionSettings.Slow, Anim.EaseOut, 0);
        }
        else
        {
            var from = ActualHeight;
            Anim.To(child, OpacityProperty, 0, MotionSettings.Fast, Anim.EaseIn);
            Anim.To(this, HeightProperty, 0, MotionSettings.Normal, Anim.EaseInOut, from, () =>
            {
                if (!IsExpanded) Visibility = Visibility.Collapsed;
            });
        }
    }
}
