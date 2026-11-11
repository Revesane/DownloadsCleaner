using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using DownloadsCleaner.Controls;
using DownloadsCleaner.Helpers;

namespace DownloadsCleaner.Mac.Controls;

/// <summary>Число, которое плавно досчитывается до нужного значения.</summary>
public sealed class AnimatedNumber : TextBlock
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<AnimatedNumber, double>(nameof(Value));

    public static readonly StyledProperty<string> FormatProperty =
        AvaloniaProperty.Register<AnimatedNumber, string>(nameof(Format), "Number");

    private double _current;
    private DispatcherTimer? _timer;

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public string Format
    {
        get => GetValue(FormatProperty);
        set => SetValue(FormatProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ValueProperty)
            AnimateTo(Value);
        else if (change.Property == FormatProperty)
            Text = FormatValue(_current);
    }

    private void AnimateTo(double target)
    {
        _timer?.Stop();

        if (!DownloadsCleaner.Animations.MotionSettings.Enabled)
        {
            _current = target;
            Text = FormatValue(target);
            return;
        }

        var from = _current;
        var started = DateTime.UtcNow;
        var duration = DownloadsCleaner.Animations.MotionSettings.Counter;

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
            var t = duration.TotalMilliseconds <= 0 ? 1 : Math.Min(1, elapsed / duration.TotalMilliseconds);
            var eased = 1 - Math.Pow(1 - t, 3);
            _current = from + (target - from) * eased;
            Text = FormatValue(_current);

            if (t >= 1)
            {
                _current = target;
                Text = FormatValue(target);
                _timer?.Stop();
            }
        };
        _timer.Start();
    }

    private string FormatValue(double value) => Format switch
    {
        "Bytes" => ByteFormatter.Format((long)value),
        _ => value.ToString("N0", CultureInfo.CurrentCulture)
    };
}

/// <summary>
/// Кольцевая диаграмма (рисуется вручную, без сотен элементов).
/// Цвета сегментов задаются в hex, поэтому модель не зависит от UI-фреймворка.
/// </summary>
public sealed class DonutChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<DonutSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<DonutChart, IReadOnlyList<DonutSegment>?>(nameof(Segments));

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<DonutChart, double>(nameof(Thickness), 24.0);

    private double _reveal = 1.0;

    public IReadOnlyList<DonutSegment>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public double Thickness
    {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SegmentsProperty)
        {
            _reveal = 0;
            var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            var started = DateTime.UtcNow;
            timer.Tick += (_, _) =>
            {
                var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;
                _reveal = Math.Min(1, elapsed / 750.0);
                InvalidateVisual();
                if (_reveal >= 1) timer.Stop();
            };
            timer.Start();
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0) return;

        var thickness = Math.Min(Thickness, size / 3);
        var radius = size / 2 - thickness / 2;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);

        var track = Avalonia.Application.Current?.Resources.TryGetResource("StrokeBrush", null, out var res) == true
            ? res as IBrush
            : null;
        context.DrawEllipse(null, new Pen(track ?? Brushes.Gray, thickness), center, radius, radius);

        var segs = Segments;
        var total = segs?.Sum(s => Math.Max(0, s.Value)) ?? 0;
        if (segs == null || total <= 0) return;

        var visible = segs.Where(s => s.Value > 0).ToList();
        var gap = visible.Count > 1 ? 2.5 : 0;
        var angle = -90.0;

        foreach (var s in visible)
        {
            var sweep = s.Value / total * 360.0;
            var drawn = Math.Max(0, sweep - gap) * _reveal;
            if (drawn > 0.1)
            {
                var start = angle + gap / 2;
                var geometry = Arc(center, radius, start, Math.Min(drawn, 359.9));
                var brush = new SolidColorBrush(Color.Parse(s.ColorHex));
                context.DrawGeometry(null, new Pen(brush, thickness) { LineCap = PenLineCap.Flat }, geometry);
            }
            angle += sweep;
        }
    }

    private static StreamGeometry Arc(Point c, double r, double startDeg, double sweepDeg)
    {
        Point P(double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(P(startDeg), false);
            ctx.ArcTo(P(startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true);
        }
        return geometry;
    }
}
