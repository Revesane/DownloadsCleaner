using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using DownloadsCleaner.Animations;
using DownloadsCleaner.Localization;

namespace DownloadsCleaner.Controls;

/// <summary>Шаг тура: тексты по ключу, значок, что подсветить и что открыть перед показом.</summary>
public sealed class TourStep
{
    /// <summary>Ключ шага: тексты берутся из Tour{Key}Title и Tour{Key}Text.</summary>
    public required string Key { get; init; }
    public required string Glyph { get; init; }
    /// <summary>Область для подсветки (в координатах окна) или null - карточка по центру.</summary>
    public Func<FrameworkElement, Rect?>? Target { get; init; }
    /// <summary>Например, переход на нужную страницу, чтобы она была видна за затемнением.</summary>
    public Action? Prepare { get; init; }
}

/// <summary>Содержимое и правила показа тура (без интерфейса - проверяется тестами).</summary>
public static class TourContent
{
    /// <summary>Порядок шагов тура. Тексты: Tour{Key}Title / Tour{Key}Text.</summary>
    public static readonly string[] StepKeys =
        { "Welcome", "Home", "Scan", "Categories", "Sort", "Analytics", "Tips", "Duplicates", "History", "Settings", "Help" };

    /// <summary>Тур показывается при первом запуске и после обновления - пока его не прошли для этой версии.</summary>
    public static bool ShouldShow(string? lastShownVersion, Version current) =>
        !string.Equals(lastShownVersion?.Trim(), current.ToString(3), StringComparison.Ordinal);

    /// <summary>
    /// Где поставить карточку: справа от узкой цели (боковое меню), под целью (кнопки шапки),
    /// над ней, внутри большой цели (страница) или по центру. Всегда в пределах окна.
    /// </summary>
    public static Point PlaceCard(Rect? target, Size card, Size area, double gap = 18, double margin = 16)
    {
        double ClampX(double x) => Math.Max(margin, Math.Min(x, area.Width - card.Width - margin));
        double ClampY(double y) => Math.Max(margin, Math.Min(y, area.Height - card.Height - margin));
        var center = new Point(ClampX((area.Width - card.Width) / 2), ClampY((area.Height - card.Height) / 2));

        if (target is not { } t || t.IsEmpty || t.Width <= 0 || t.Height <= 0) return center;

        // Большая цель (вся страница): карточка внутри неё, в правом нижнем углу.
        if (t.Width * t.Height > area.Width * area.Height * 0.4)
            return new Point(ClampX(t.Right - card.Width - margin * 2), ClampY(t.Bottom - card.Height - margin * 2));

        // Цель в левой части окна (боковое меню): справа от неё.
        if (t.Right + gap + card.Width + margin <= area.Width && t.Left < area.Width * 0.3)
            return new Point(t.Right + gap, ClampY(t.Top + t.Height / 2 - card.Height / 2));

        // Под целью, выровнять по её правому краю.
        if (t.Bottom + gap + card.Height + margin <= area.Height)
            return new Point(ClampX(t.Right - card.Width), t.Bottom + gap);

        // Над целью.
        if (t.Top - gap - card.Height >= margin)
            return new Point(ClampX(t.Right - card.Width), t.Top - gap - card.Height);

        return center;
    }
}

/// <summary>
/// Анимированный тур поверх окна: затемнение с «прожектором» на нужном элементе, светящаяся
/// рамка и карточка с объяснением. Прожектор и карточка плавно перелетают от шага к шагу.
/// Без анимаций (настройка или Reduce Motion) всё меняется мгновенно.
/// </summary>
public sealed class TourOverlay : Grid
{
    private const double CardWidth = 380;
    private const double HolePadding = 6;

    private readonly RectangleGeometry _full = new();
    private readonly RectangleGeometry _hole = new(new Rect(0, 0, 0, 0), 14, 14);
    private readonly System.Windows.Shapes.Path _scrim;
    private readonly Border _ring;
    private readonly Canvas _canvas = new();
    private readonly Border _card;
    private readonly StackPanel _content = new();
    private readonly Border _iconBack;
    private readonly TextBlock _icon;
    private readonly TextBlock _counter;
    private readonly TextBlock _title;
    private readonly TextBlock _text;
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _back;
    private readonly Button _next;
    private readonly Button _skip;

    private IReadOnlyList<TourStep> _steps = Array.Empty<TourStep>();
    private int _index;
    private Rect? _currentTarget;

    /// <summary>Тур пройден или пропущен.</summary>
    public event Action? Finished;

    public bool IsOpen => Visibility == Visibility.Visible;

    public TourOverlay()
    {
        Visibility = Visibility.Collapsed;
        Background = Brushes.Transparent;          // клики не проходят к интерфейсу под туром
        Focusable = true;

        _scrim = new System.Windows.Shapes.Path { Data = new CombinedGeometry(GeometryCombineMode.Exclude, _full, _hole) };
        _scrim.SetResourceReference(Shape.FillProperty, "ScrimBrush");

        _ring = new Border { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(14), IsHitTestVisible = false, Opacity = 0 };
        _ring.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");

        _iconBack = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(14) };
        _iconBack.SetResourceReference(Border.BackgroundProperty, "AccentGradientBrush");
        _icon = new TextBlock { FontSize = 20, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _iconBack.Child = _icon;

        _counter = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        _title = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) };
        _text = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), LineHeight = 19 };

        _back = new Button { Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0) };
        _next = new Button { Padding = new Thickness(16, 7, 16, 7) };
        _skip = new Button { Padding = new Thickness(10, 7, 10, 7) };
        _back.Click += (_, _) => Go(_index - 1);
        _next.Click += (_, _) => { if (_index >= _steps.Count - 1) Finish(); else Go(_index + 1); };
        _skip.Click += (_, _) => Finish();

        // Верх: значок, «Шаг N из M» и «Пропустить» справа (внизу ему не хватало места рядом с «Назад»).
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(_iconBack);
        Grid.SetColumn(_counter, 1);
        top.Children.Add(_counter);
        Grid.SetColumn(_skip, 2);
        _skip.VerticalAlignment = VerticalAlignment.Top;
        top.Children.Add(_skip);

        var buttons = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.Children.Add(_dots);
        Grid.SetColumn(_back, 1);
        buttons.Children.Add(_back);
        Grid.SetColumn(_next, 2);
        buttons.Children.Add(_next);

        _content.Children.Add(top);
        _content.Children.Add(_title);
        _content.Children.Add(_text);
        _content.Children.Add(buttons);

        _card = new Border
        {
            Width = CardWidth,
            Padding = new Thickness(22, 20, 22, 18),
            CornerRadius = new CornerRadius(18),
            BorderThickness = new Thickness(1),
            Child = _content
        };
        _card.SetResourceReference(Border.BackgroundProperty, "BgElevatedBrush");
        _card.SetResourceReference(Border.BorderBrushProperty, "StrokeStrongBrush");

        _canvas.Children.Add(_ring);
        _canvas.Children.Add(_card);
        Children.Add(_scrim);
        Children.Add(_canvas);

        SizeChanged += (_, _) => { if (IsOpen) Layout(animate: false); };
    }

    // ------------------------------------------------------------------ Управление

    public void Start(IReadOnlyList<TourStep> steps)
    {
        if (steps.Count == 0) return;
        _steps = steps;
        ApplyStyles();
        BuildDots();
        Visibility = Visibility.Visible;
        Focus();

        // Затемнение проявляется, карточка «всплывает» по центру, рамка начинает дышать.
        Anim.To(this, OpacityProperty, 1, MotionSettings.Slow, Anim.EaseOut, 0);
        StartRingPulse();
        _index = -1;
        _currentTarget = null;
        Go(0);
    }

    /// <summary>Пересчитывает положение прожектора (например, если под ним сдвинулась страница).</summary>
    public void Refresh()
    {
        if (!IsOpen || _index < 0 || _index >= _steps.Count) return;
        _currentTarget = _steps[_index].Target?.Invoke(this);
        Layout(animate: true);
    }

    /// <summary>Клавиши тура: → / Enter - дальше, ← - назад, Esc - пропустить.</summary>
    public bool HandleKey(Key key)
    {
        if (!IsOpen) return false;
        switch (key)
        {
            case Key.Right:
            case Key.Enter:
                if (_index >= _steps.Count - 1) Finish(); else Go(_index + 1);
                return true;
            case Key.Left:
                Go(_index - 1);
                return true;
            case Key.Escape:
                Finish();
                return true;
        }
        return false;
    }

    private void Finish()
    {
        _ring.BeginAnimation(OpacityProperty, null);
        Anim.To(this, OpacityProperty, 0, MotionSettings.Normal, Anim.EaseIn, done: () =>
        {
            Visibility = Visibility.Collapsed;
            Opacity = 1;
        });
        Finished?.Invoke();
    }

    private void Go(int index)
    {
        if (index < 0 || index >= _steps.Count || index == _index) return;
        _index = index;
        var step = _steps[index];
        step.Prepare?.Invoke();
        // Ждём, пока открытая страница разложится, и только потом меряем цель.
        Dispatcher.InvokeAsync(() => ShowStep(step), DispatcherPriority.Loaded);
    }

    // ------------------------------------------------------------------ Показ шага

    private void ShowStep(TourStep step)
    {
        _icon.Text = step.Glyph;
        _counter.Text = Loc.F("TourStepOf", _index + 1, _steps.Count);
        _title.Text = Loc.T($"Tour{step.Key}Title");
        _text.Text = Loc.T($"Tour{step.Key}Text");
        _back.Content = Loc.T("TourBack");
        _back.Visibility = _index == 0 ? Visibility.Collapsed : Visibility.Visible;
        _next.Content = Loc.T(_index == _steps.Count - 1 ? "TourFinish" : "TourNext");
        _skip.Content = Loc.T("TourSkip");
        _skip.Visibility = _index == _steps.Count - 1 ? Visibility.Hidden : Visibility.Visible;

        _currentTarget = step.Target?.Invoke(this);
        Layout(animate: true);
        UpdateDots();

        // Содержимое карточки проявляется с небольшим сдвигом, значок «выпрыгивает».
        if (MotionSettings.Enabled)
        {
            var (_, ct) = Anim.Transforms(_content);
            Anim.To(_content, OpacityProperty, 1, MotionSettings.Slow, Anim.EaseOut, 0);
            Anim.To(ct, TranslateTransform.YProperty, 0, MotionSettings.Slow, Anim.EaseOut, 10);
            var (s, _) = Anim.Transforms(_iconBack);
            var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 };
            Anim.To(s, ScaleTransform.ScaleXProperty, 1, MotionSettings.Custom(480), spring, 0.5);
            Anim.To(s, ScaleTransform.ScaleYProperty, 1, MotionSettings.Custom(480), spring, 0.5);
        }
    }

    /// <summary>Ставит прожектор, рамку и карточку на свои места (с анимацией перелёта или сразу).</summary>
    private void Layout(bool animate)
    {
        var area = new Size(ActualWidth, ActualHeight);
        if (area.Width <= 0) return;
        _full.Rect = new Rect(area);
        var dur = animate && MotionSettings.Enabled ? MotionSettings.Custom(460) : TimeSpan.Zero;

        Rect hole;
        if (_currentTarget is { } t)
        {
            hole = Rect.Inflate(t, HolePadding, HolePadding);
            hole.Intersect(new Rect(area));
            Anim.To(_ring, OpacityProperty, 1, dur, Anim.EaseOut);
        }
        else
        {
            hole = new Rect(area.Width / 2, area.Height / 2, 0, 0);     // без цели - сплошное затемнение
            Anim.To(_ring, OpacityProperty, 0, dur, Anim.EaseOut);
        }
        AnimateRect(_hole, hole, dur);
        AnimateBox(_ring, Rect.Inflate(hole, 1, 1), dur);

        _card.Measure(new Size(CardWidth, double.PositiveInfinity));
        var pos = TourContent.PlaceCard(_currentTarget is null ? null : hole, _card.DesiredSize, area);
        Anim.To(_card, Canvas.LeftProperty, pos.X, dur, Anim.EaseInOut, double.IsNaN(Canvas.GetLeft(_card)) ? pos.X : null);
        Anim.To(_card, Canvas.TopProperty, pos.Y, dur, Anim.EaseInOut, double.IsNaN(Canvas.GetTop(_card)) ? pos.Y : null);
    }

    private static void AnimateRect(RectangleGeometry geometry, Rect to, TimeSpan duration)
    {
        var from = geometry.Rect;
        geometry.Rect = to;
        if (duration <= TimeSpan.Zero || from.IsEmpty || (from.Width == 0 && to.Width == 0))
        {
            geometry.BeginAnimation(RectangleGeometry.RectProperty, null);
            return;
        }
        geometry.BeginAnimation(RectangleGeometry.RectProperty,
            new RectAnimation(from, to, duration) { EasingFunction = Anim.EaseInOut, FillBehavior = FillBehavior.Stop });
    }

    private static void AnimateBox(FrameworkElement el, Rect to, TimeSpan duration)
    {
        Anim.To(el, Canvas.LeftProperty, to.X, duration, Anim.EaseInOut, double.IsNaN(Canvas.GetLeft(el)) ? to.X : null);
        Anim.To(el, Canvas.TopProperty, to.Y, duration, Anim.EaseInOut, double.IsNaN(Canvas.GetTop(el)) ? to.Y : null);
        Anim.To(el, WidthProperty, Math.Max(0, to.Width), duration, Anim.EaseInOut, double.IsNaN(el.Width) ? to.Width : null);
        Anim.To(el, HeightProperty, Math.Max(0, to.Height), duration, Anim.EaseInOut, double.IsNaN(el.Height) ? to.Height : null);
    }

    /// <summary>Рамка вокруг прожектора мягко «дышит», пока открыт тур.</summary>
    private void StartRingPulse()
    {
        if (!MotionSettings.Enabled) return;
        var glow = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 0, Opacity = 0.9 };
        if (TryFindResource("AccentColor") is Color c) glow.Color = c;
        _ring.Effect = glow;
        glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty,
            new DoubleAnimation(10, 28, TimeSpan.FromMilliseconds(1100)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Anim.EaseInOut });
    }

    // ------------------------------------------------------------------ Точки прогресса

    private void BuildDots()
    {
        _dots.Children.Clear();
        for (var i = 0; i < _steps.Count; i++)
        {
            var dot = new Border { Height = 6, Width = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, 5, 0) };
            dot.SetResourceReference(Border.BackgroundProperty, "StrokeStrongBrush");
            _dots.Children.Add(dot);
        }
    }

    private void UpdateDots()
    {
        for (var i = 0; i < _dots.Children.Count; i++)
        {
            var dot = (Border)_dots.Children[i];
            var active = i == _index;
            dot.SetResourceReference(Border.BackgroundProperty, active ? "AccentBrush" : i < _index ? "AccentSoftBrush" : "StrokeStrongBrush");
            Anim.To(dot, WidthProperty, active ? 18 : 6, MotionSettings.Normal, Anim.EaseOut, double.IsNaN(dot.Width) ? 6 : null);
        }
    }

    private void ApplyStyles()
    {
        if (TryFindResource("IconText") is Style icon) { _icon.Style = icon; _icon.FontSize = 20; _icon.Foreground = Brushes.White; }
        if (TryFindResource("TextCaption") is Style caption) _counter.Style = caption;
        if (TryFindResource("TextSecondary") is Style secondary) { _text.Style = secondary; _text.TextWrapping = TextWrapping.Wrap; }
        if (TryFindResource("SecondaryButton") is Style sec) _back.Style = sec;
        if (TryFindResource("PrimaryButton") is Style prim) _next.Style = prim;
        if (TryFindResource("GhostButton") is Style ghost) _skip.Style = ghost;
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        _card.Effect = new DropShadowEffect { BlurRadius = 36, ShadowDepth = 10, Opacity = 0.45, Color = TryFindResource("ShadowColor") is Color sc ? sc : Colors.Black };
    }
}
