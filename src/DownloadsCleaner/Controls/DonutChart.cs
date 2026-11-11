using System.Windows;
using System.Windows.Media;
using DownloadsCleaner.Animations;

namespace DownloadsCleaner.Controls;

/// <summary>Доля категории в кольцевой диаграмме. Цвет хранится в hex, чтобы модель не зависела от UI.</summary>
public sealed record DonutSegment(double Value, string ColorHex);

/// <summary>
/// Кольцевая диаграмма. Рисуется вручную (без сотен элементов), при смене данных дуги плавно «вырастают».
/// Анимация идёт только в момент обновления данных.
/// </summary>
public sealed class DonutChart : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<DonutSegment>), typeof(DonutChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSegmentsChanged));

    public static readonly DependencyProperty RevealProperty = DependencyProperty.Register(
        nameof(Reveal), typeof(double), typeof(DonutChart),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(DonutChart),
        new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<DonutSegment>? Segments
    {
        get => (IReadOnlyList<DonutSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    public double Reveal { get => (double)GetValue(RevealProperty); set => SetValue(RevealProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    private static void OnSegmentsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (DonutChart)d;
        // Начинаем с нуля, иначе анимация не даст эффекта: значение уже равно 1.
        c.SetValue(RevealProperty, 0d);
        Anim.To(c, RevealProperty, 1, MotionSettings.Custom(750), Anim.EaseOut, 0);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var thickness = Math.Min(Thickness, size / 3);
        var radius = size / 2 - thickness / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        var track = TryFindResource("StrokeBrush") as Brush ?? Brushes.Gray;
        dc.DrawEllipse(null, new Pen(track, thickness), center, radius, radius);

        var segs = Segments;
        var total = segs?.Sum(s => Math.Max(0, s.Value)) ?? 0;
        if (segs == null || total <= 0) return;

        var visible = segs.Where(s => s.Value > 0).ToList();
        var gap = visible.Count > 1 ? 2.5 : 0;
        var angle = -90.0;
        foreach (var s in visible)
        {
            var sweep = s.Value / total * 360.0;
            var drawn = Math.Max(0, sweep - gap) * Reveal;
            if (drawn > 0.1)
            {
                var start = angle + gap / 2;
                var geo = Arc(center, radius, start, Math.Min(drawn, 359.9));
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.ColorHex));
                brush.Freeze();
                dc.DrawGeometry(null, new Pen(brush, thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat }, geo);
            }
            angle += sweep;
        }
    }

    private static Geometry Arc(Point c, double r, double startDeg, double sweepDeg)
    {
        Point P(double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(startDeg), false, false);
            ctx.ArcTo(P(startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}
