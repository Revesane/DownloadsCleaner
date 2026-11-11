using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DownloadsCleaner.Animations;

public enum EntranceKind { None, Fade, FadeUp, FromRight, FromLeft, Pop }

public enum ExitKind { Fade, Collapse, SlideRight, Pop }

public enum MotionState { None, Hover, Press, Checked, Selected, Focus }

/// <summary>
/// Библиотека переиспользуемых анимационных поведений (attached properties).
/// Все эффекты проходят через <see cref="Anim"/> и <see cref="MotionSettings"/>, поэтому
/// единообразно отключаются настройкой приложения и системным параметром Reduce Motion.
/// </summary>
public static class Motion
{
    private static DependencyProperty Reg<T>(string name, T def, PropertyChangedCallback? cb = null) =>
        DependencyProperty.RegisterAttached(name, typeof(T), typeof(Motion), new PropertyMetadata(def, cb));

    // ------------------------------------------------------------------ Появление

    public static readonly DependencyProperty EntranceProperty = Reg("Entrance", EntranceKind.None, OnEntranceChanged);
    public static readonly DependencyProperty EntranceDelayProperty = Reg("EntranceDelay", 0.0);
    public static readonly DependencyProperty EntranceRepeatProperty = Reg("EntranceRepeat", false);
    private static readonly DependencyProperty EntrancePlayedProperty = Reg("EntrancePlayed", false);

    public static EntranceKind GetEntrance(DependencyObject o) => (EntranceKind)o.GetValue(EntranceProperty);
    public static void SetEntrance(DependencyObject o, EntranceKind v) => o.SetValue(EntranceProperty, v);
    public static double GetEntranceDelay(DependencyObject o) => (double)o.GetValue(EntranceDelayProperty);
    public static void SetEntranceDelay(DependencyObject o, double v) => o.SetValue(EntranceDelayProperty, v);
    public static bool GetEntranceRepeat(DependencyObject o) => (bool)o.GetValue(EntranceRepeatProperty);
    public static void SetEntranceRepeat(DependencyObject o, bool v) => o.SetValue(EntranceRepeatProperty, v);

    private static void OnEntranceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.Loaded -= OnEntranceLoaded;
        fe.IsVisibleChanged -= OnEntranceVisible;
        if ((EntranceKind)e.NewValue == EntranceKind.None) return;
        fe.Loaded += OnEntranceLoaded;
        fe.IsVisibleChanged += OnEntranceVisible;
        if (fe.IsLoaded && !(bool)fe.GetValue(EntrancePlayedProperty)) PlayEntrance(fe);
    }

    private static void OnEntranceLoaded(object sender, RoutedEventArgs e)
    {
        var fe = (FrameworkElement)sender;
        if (!(bool)fe.GetValue(EntrancePlayedProperty) || GetEntranceRepeat(fe)) PlayEntrance(fe);
    }

    private static void OnEntranceVisible(object sender, DependencyPropertyChangedEventArgs e)
    {
        var fe = (FrameworkElement)sender;
        if (GetEntranceRepeat(fe) && e.NewValue is true && fe.IsLoaded) PlayEntrance(fe);
    }

    private static async void PlayEntrance(FrameworkElement fe)
    {
        fe.SetValue(EntrancePlayedProperty, true);
        if (!MotionSettings.Enabled) return;
        try
        {
            var kind = GetEntrance(fe);
            var delay = GetEntranceDelay(fe);
            var (s, t) = Anim.Transforms(fe);
            double x = 0, y = 0, scale = 1;
            switch (kind)
            {
                case EntranceKind.FadeUp: y = 14; break;
                case EntranceKind.FromRight: x = 40; break;
                case EntranceKind.FromLeft: x = -40; break;
                case EntranceKind.Pop: scale = 0.94; break;
            }

            Anim.Set(fe, UIElement.OpacityProperty, 0);
            Anim.Set(t, TranslateTransform.XProperty, x);
            Anim.Set(t, TranslateTransform.YProperty, y);
            Anim.Set(s, ScaleTransform.ScaleXProperty, scale);
            Anim.Set(s, ScaleTransform.ScaleYProperty, scale);

            if (delay > 0)
            {
                await Task.Delay((int)delay);
                if (!fe.IsLoaded) { ResetVisual(fe); return; }
            }

            var dur = MotionSettings.Slow;
            Anim.To(fe, UIElement.OpacityProperty, 1, dur, Anim.EaseOut, 0);
            if (x != 0) Anim.To(t, TranslateTransform.XProperty, 0, dur, Anim.EaseOut, x);
            if (y != 0) Anim.To(t, TranslateTransform.YProperty, 0, dur, Anim.EaseOut, y);
            if (scale != 1)
            {
                Anim.To(s, ScaleTransform.ScaleXProperty, 1, dur, Anim.EaseOut, scale);
                Anim.To(s, ScaleTransform.ScaleYProperty, 1, dur, Anim.EaseOut, scale);
            }
        }
        catch
        {
            ResetVisual(fe);
        }
    }

    private static void ResetVisual(FrameworkElement fe)
    {
        Anim.Set(fe, UIElement.OpacityProperty, 1);
        if (fe.RenderTransform is TransformGroup { Children.Count: 2 } g
            && g.Children[0] is ScaleTransform s && g.Children[1] is TranslateTransform t)
        {
            Anim.Set(s, ScaleTransform.ScaleXProperty, 1);
            Anim.Set(s, ScaleTransform.ScaleYProperty, 1);
            Anim.Set(t, TranslateTransform.XProperty, 0);
            Anim.Set(t, TranslateTransform.YProperty, 0);
        }
        if (fe.LayoutTransform is ScaleTransform { IsFrozen: false } lt)
            Anim.Set(lt, ScaleTransform.ScaleYProperty, 1);
    }

    // ------------------------------------------------------------------ Исчезновение

    public static readonly DependencyProperty ExitWhenProperty = Reg("ExitWhen", false, OnExitWhenChanged);
    public static readonly DependencyProperty ExitKindProperty = Reg("ExitKind", ExitKind.Fade);

    public static bool GetExitWhen(DependencyObject o) => (bool)o.GetValue(ExitWhenProperty);
    public static void SetExitWhen(DependencyObject o, bool v) => o.SetValue(ExitWhenProperty, v);
    public static ExitKind GetExitKind(DependencyObject o) => (ExitKind)o.GetValue(ExitKindProperty);
    public static void SetExitKind(DependencyObject o, ExitKind v) => o.SetValue(ExitKindProperty, v);

    private static void OnExitWhenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if (e.NewValue is not true)
        {
            // Контейнеры в виртуализированных списках переиспользуются - возвращаем нормальный вид.
            ResetVisual(fe);
            return;
        }
        if (!MotionSettings.Enabled) { fe.Opacity = 0; return; }

        var (s, t) = Anim.Transforms(fe);
        switch (GetExitKind(fe))
        {
            case ExitKind.Collapse:
                if (fe.LayoutTransform is not ScaleTransform { IsFrozen: false })
                    fe.LayoutTransform = new ScaleTransform(1, 1);
                Anim.To(fe, UIElement.OpacityProperty, 0, MotionSettings.Normal, Anim.EaseIn);
                Anim.To((ScaleTransform)fe.LayoutTransform, ScaleTransform.ScaleYProperty, 0, MotionSettings.Normal, Anim.EaseInOut);
                break;
            case ExitKind.SlideRight:
                Anim.To(fe, UIElement.OpacityProperty, 0, MotionSettings.Normal, Anim.EaseIn);
                Anim.To(t, TranslateTransform.XProperty, 48, MotionSettings.Normal, Anim.EaseIn);
                break;
            case ExitKind.Pop:
                Anim.To(fe, UIElement.OpacityProperty, 0, MotionSettings.Fast, Anim.EaseIn);
                Anim.ScaleTo(fe, 0.96, MotionSettings.Fast, Anim.EaseIn);
                break;
            default:
                Anim.To(fe, UIElement.OpacityProperty, 0, MotionSettings.Fast, Anim.EaseIn);
                break;
        }
    }

    // ------------------------------------------------------------------ Наведение (масштаб карточек)

    public static readonly DependencyProperty HoverScaleProperty = Reg("HoverScale", 1.0, OnHoverScaleChanged);
    public static double GetHoverScale(DependencyObject o) => (double)o.GetValue(HoverScaleProperty);
    public static void SetHoverScale(DependencyObject o, double v) => o.SetValue(HoverScaleProperty, v);

    private static void OnHoverScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        el.MouseEnter -= OnHoverEnter;
        el.MouseLeave -= OnHoverLeave;
        if ((double)e.NewValue == 1.0) return;
        el.MouseEnter += OnHoverEnter;
        el.MouseLeave += OnHoverLeave;
    }

    private static void OnHoverEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var el = (UIElement)sender;
        Anim.ScaleTo(el, GetHoverScale(el), MotionSettings.Normal, Anim.EaseOut);
    }

    private static void OnHoverLeave(object sender, System.Windows.Input.MouseEventArgs e) =>
        Anim.ScaleTo((UIElement)sender, 1, MotionSettings.Normal, Anim.EaseOut);

    // ------------------------------------------------------------------ Нажатие (лёгкое «вдавливание»)

    public static readonly DependencyProperty PressScaleProperty = Reg("PressScale", 1.0, OnPressScaleChanged);
    public static double GetPressScale(DependencyObject o) => (double)o.GetValue(PressScaleProperty);
    public static void SetPressScale(DependencyObject o, double v) => o.SetValue(PressScaleProperty, v);

    private static void OnPressScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        el.PreviewMouseLeftButtonDown -= OnPressDown;
        el.PreviewMouseLeftButtonUp -= OnPressUp;
        if ((double)e.NewValue == 1.0) return;
        el.PreviewMouseLeftButtonDown += OnPressDown;
        el.PreviewMouseLeftButtonUp += OnPressUp;
    }

    private static void OnPressDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var el = (UIElement)sender;
        Anim.ScaleTo(el, GetPressScale(el), MotionSettings.Custom(90), Anim.EaseOut);
    }

    private static void OnPressUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var el = (UIElement)sender;
        var hover = GetHoverScale(el);
        Anim.ScaleTo(el, el.IsMouseOver ? hover : 1, MotionSettings.Custom(130), Anim.EaseOut);
    }

    // ------------------------------------------------------------------ Состояния шаблона

    public static readonly DependencyProperty StateProperty = Reg("State", MotionState.None, OnStateChanged);
    public static readonly DependencyProperty StateOffsetXProperty = Reg("StateOffsetX", 0.0);
    public static readonly DependencyProperty StateAffectsOpacityProperty = Reg("StateAffectsOpacity", true);
    public static readonly DependencyProperty StateGrowProperty = Reg("StateGrow", false);
    public static readonly DependencyProperty StateHostProperty = Reg<FrameworkElement?>("StateHost", null);
    private static readonly DependencyProperty WatcherProperty = Reg<StateWatcher?>("Watcher", null);

    public static MotionState GetState(DependencyObject o) => (MotionState)o.GetValue(StateProperty);
    public static void SetState(DependencyObject o, MotionState v) => o.SetValue(StateProperty, v);
    public static double GetStateOffsetX(DependencyObject o) => (double)o.GetValue(StateOffsetXProperty);
    public static void SetStateOffsetX(DependencyObject o, double v) => o.SetValue(StateOffsetXProperty, v);
    public static bool GetStateAffectsOpacity(DependencyObject o) => (bool)o.GetValue(StateAffectsOpacityProperty);
    public static void SetStateAffectsOpacity(DependencyObject o, bool v) => o.SetValue(StateAffectsOpacityProperty, v);
    public static bool GetStateGrow(DependencyObject o) => (bool)o.GetValue(StateGrowProperty);
    public static void SetStateGrow(DependencyObject o, bool v) => o.SetValue(StateGrowProperty, v);
    public static FrameworkElement? GetStateHost(DependencyObject o) => (FrameworkElement?)o.GetValue(StateHostProperty);
    public static void SetStateHost(DependencyObject o, FrameworkElement? v) => o.SetValue(StateHostProperty, v);

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        (fe.GetValue(WatcherProperty) as StateWatcher)?.Dispose();
        fe.ClearValue(WatcherProperty);
        if ((MotionState)e.NewValue == MotionState.None) return;
        fe.SetValue(WatcherProperty, new StateWatcher(fe));
    }

    /// <summary>
    /// Следит за состоянием «хозяина» шаблона (наведение, нажатие, выбор...) и плавно
    /// показывает/скрывает слой-индикатор. Подписки снимаются при выгрузке элемента.
    /// </summary>
    private sealed class StateWatcher : IDisposable
    {
        private readonly FrameworkElement _el;
        private FrameworkElement? _host;
        private bool _attached;

        public StateWatcher(FrameworkElement el)
        {
            _el = el;
            el.Loaded += OnLoaded;
            el.Unloaded += OnUnloaded;
            if (el.IsLoaded) Attach();
        }

        private void OnLoaded(object sender, RoutedEventArgs e) => Attach();
        private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

        private MotionState Kind => GetState(_el);

        private FrameworkElement? ResolveHost()
        {
            var explicitHost = GetStateHost(_el);
            if (explicitHost != null) return explicitHost;
            if (_el.TemplatedParent is FrameworkElement tp) return tp;
            DependencyObject? p = VisualTreeHelper.GetParent(_el);
            while (p != null)
            {
                if (p is Control c) return c;
                p = VisualTreeHelper.GetParent(p);
            }
            return null;
        }

        private DependencyPropertyDescriptor? Descriptor() => Kind switch
        {
            MotionState.Press => DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(ButtonBase)),
            MotionState.Checked => DependencyPropertyDescriptor.FromProperty(ToggleButton.IsCheckedProperty, typeof(ToggleButton)),
            MotionState.Selected => DependencyPropertyDescriptor.FromProperty(Selector.IsSelectedProperty, typeof(FrameworkElement)),
            _ => null
        };

        private void Attach()
        {
            if (_attached) return;
            _host = ResolveHost();
            if (_host == null) return;
            _attached = true;
            switch (Kind)
            {
                case MotionState.Hover:
                    _host.MouseEnter += OnMouse;
                    _host.MouseLeave += OnMouse;
                    _host.IsEnabledChanged += OnEnabled;
                    break;
                case MotionState.Focus:
                    _host.IsKeyboardFocusWithinChanged += OnFocus;
                    break;
                default:
                    Descriptor()?.AddValueChanged(_host, OnProp);
                    break;
            }
            Apply(false);
        }

        private void Detach()
        {
            if (!_attached || _host == null) return;
            _attached = false;
            switch (Kind)
            {
                case MotionState.Hover:
                    _host.MouseEnter -= OnMouse;
                    _host.MouseLeave -= OnMouse;
                    _host.IsEnabledChanged -= OnEnabled;
                    break;
                case MotionState.Focus:
                    _host.IsKeyboardFocusWithinChanged -= OnFocus;
                    break;
                default:
                    Descriptor()?.RemoveValueChanged(_host, OnProp);
                    break;
            }
        }

        private void OnMouse(object sender, System.Windows.Input.MouseEventArgs e) => Apply(true);
        private void OnEnabled(object sender, DependencyPropertyChangedEventArgs e) => Apply(true);
        private void OnFocus(object sender, DependencyPropertyChangedEventArgs e) => Apply(true);
        private void OnProp(object? sender, EventArgs e) => Apply(true);

        private bool IsActive()
        {
            if (_host == null) return false;
            return Kind switch
            {
                MotionState.Hover => _host.IsMouseOver && _host.IsEnabled,
                MotionState.Press => (_host as ButtonBase)?.IsPressed == true,
                MotionState.Checked => (_host as ToggleButton)?.IsChecked == true,
                MotionState.Selected => Selector.GetIsSelected(_host),
                MotionState.Focus => _host.IsKeyboardFocusWithin,
                _ => false
            };
        }

        private void Apply(bool animate)
        {
            var active = IsActive();
            var dur = animate ? (active ? MotionSettings.Fast : MotionSettings.Normal) : TimeSpan.Zero;

            if (GetStateAffectsOpacity(_el))
                Anim.To(_el, UIElement.OpacityProperty, active ? 1 : 0, dur, active ? Anim.EaseOut : Anim.EaseInOut);

            var offset = GetStateOffsetX(_el);
            var grow = GetStateGrow(_el);
            if (offset != 0 || grow)
            {
                var (s, t) = Anim.Transforms(_el);
                if (offset != 0)
                    Anim.To(t, TranslateTransform.XProperty, active ? offset : 0, dur, Anim.EaseInOut);
                if (grow)
                {
                    var target = active ? 1 : 0.6;
                    Anim.To(s, ScaleTransform.ScaleXProperty, target, dur, Anim.EaseOut);
                    Anim.To(s, ScaleTransform.ScaleYProperty, target, dur, Anim.EaseOut);
                }
            }
        }

        public void Dispose()
        {
            _el.Loaded -= OnLoaded;
            _el.Unloaded -= OnUnloaded;
            Detach();
        }
    }

    // ------------------------------------------------------------------ Анимированные размеры и значения

    public static readonly DependencyProperty AnimatedWidthProperty = Reg("AnimatedWidth", -1.0, OnAnimatedWidthChanged);
    public static readonly DependencyProperty AnimatedHeightProperty = Reg("AnimatedHeight", -1.0, OnAnimatedHeightChanged);
    public static readonly DependencyProperty AnimatedValueProperty = Reg("AnimatedValue", -1.0, OnAnimatedValueChanged);

    public static double GetAnimatedWidth(DependencyObject o) => (double)o.GetValue(AnimatedWidthProperty);
    public static void SetAnimatedWidth(DependencyObject o, double v) => o.SetValue(AnimatedWidthProperty, v);
    public static double GetAnimatedHeight(DependencyObject o) => (double)o.GetValue(AnimatedHeightProperty);
    public static void SetAnimatedHeight(DependencyObject o, double v) => o.SetValue(AnimatedHeightProperty, v);
    public static double GetAnimatedValue(DependencyObject o) => (double)o.GetValue(AnimatedValueProperty);
    public static void SetAnimatedValue(DependencyObject o, double v) => o.SetValue(AnimatedValueProperty, v);

    private static void OnAnimatedWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        AnimateSize(d, e, FrameworkElement.WidthProperty, fe => fe.ActualWidth);

    private static void OnAnimatedHeightChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        AnimateSize(d, e, FrameworkElement.HeightProperty, fe => fe.ActualHeight);

    private static void AnimateSize(DependencyObject d, DependencyPropertyChangedEventArgs e,
        DependencyProperty prop, Func<FrameworkElement, double> actual)
    {
        if (d is not FrameworkElement fe) return;
        var to = (double)e.NewValue;
        if (to < 0) return;
        if ((double)e.OldValue < 0 || !fe.IsLoaded)
        {
            Anim.Set(fe, prop, to);
            return;
        }
        Anim.To(fe, prop, to, MotionSettings.Normal, Anim.EaseInOut, actual(fe));
    }

    private static void OnAnimatedValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RangeBase rb) return;
        var to = (double)e.NewValue;
        if (to < 0) return;

        if ((double)e.OldValue < 0)
        {
            // Первое значение: полоска «заполняется» после появления на экране.
            if (!MotionSettings.Enabled) { rb.Value = to; return; }
            Anim.Set(rb, RangeBase.ValueProperty, 0);
            void Go()
            {
                Anim.To(rb, RangeBase.ValueProperty, Math.Max(0, GetAnimatedValue(rb)), MotionSettings.Slow, Anim.EaseOut, 0);
            }
            if (rb.IsLoaded) Go();
            else
            {
                RoutedEventHandler? h = null;
                h = (_, _) => { rb.Loaded -= h; Go(); };
                rb.Loaded += h;
            }
            return;
        }
        Anim.To(rb, RangeBase.ValueProperty, to, MotionSettings.Normal, Anim.EaseInOut);
    }

    // ------------------------------------------------------------------ Вращение значка во время операции

    public static readonly DependencyProperty SpinWhenProperty = Reg("SpinWhen", false, OnSpinChanged);
    public static bool GetSpinWhen(DependencyObject o) => (bool)o.GetValue(SpinWhenProperty);
    public static void SetSpinWhen(DependencyObject o, bool v) => o.SetValue(SpinWhenProperty, v);

    private static void OnSpinChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        var spin = e.NewValue is true && MotionSettings.Enabled;
        if (spin)
        {
            var rt = new RotateTransform();
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            el.RenderTransform = rt;
            rt.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else if (el.RenderTransform is RotateTransform { IsFrozen: false } r)
        {
            r.BeginAnimation(RotateTransform.AngleProperty, null);
            r.Angle = 0;
        }
    }

    // ------------------------------------------------------------------ Бегущая полоса (неопределённый прогресс)

    public static readonly DependencyProperty SweepProperty = Reg("Sweep", false, OnSweepChanged);
    public static bool GetSweep(DependencyObject o) => (bool)o.GetValue(SweepProperty);
    public static void SetSweep(DependencyObject o, bool v) => o.SetValue(SweepProperty, v);

    private static void OnSweepChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        el.Loaded += (_, _) => UpdateSweep(el);
        if (el.IsLoaded) UpdateSweep(el);
    }

    private static void UpdateSweep(FrameworkElement el)
    {
        if (el.TemplatedParent is not ProgressBar pb) return;
        void Refresh()
        {
            var run = pb.IsIndeterminate && pb.IsVisible && pb.ActualWidth > 0;
            var tt = el.RenderTransform as TranslateTransform;
            if (!run)
            {
                tt?.BeginAnimation(TranslateTransform.XProperty, null);
                el.Visibility = pb.IsIndeterminate ? Visibility.Visible : Visibility.Collapsed;
                return;
            }
            el.Visibility = Visibility.Visible;
            if (!MotionSettings.Enabled)
            {
                tt?.BeginAnimation(TranslateTransform.XProperty, null);
                return;
            }
            el.Width = Math.Max(40, pb.ActualWidth * 0.35);
            tt ??= new TranslateTransform();
            el.RenderTransform = tt;
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-el.Width, pb.ActualWidth, TimeSpan.FromSeconds(1.3))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = Anim.EaseInOut
            });
        }
        var dpd = DependencyPropertyDescriptor.FromProperty(ProgressBar.IsIndeterminateProperty, typeof(ProgressBar));
        dpd.AddValueChanged(pb, (_, _) => Refresh());
        pb.IsVisibleChanged += (_, _) => Refresh();
        pb.SizeChanged += (_, _) => Refresh();
        Refresh();
    }

    // ------------------------------------------------------------------ Короткая подсветка (новый файл)

    public static readonly DependencyProperty FlashWhenProperty = Reg("FlashWhen", false, OnFlashChanged);
    public static bool GetFlashWhen(DependencyObject o) => (bool)o.GetValue(FlashWhenProperty);
    public static void SetFlashWhen(DependencyObject o, bool v) => o.SetValue(FlashWhenProperty, v);

    private static void OnFlashChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        if (e.NewValue is true && MotionSettings.Enabled)
        {
            Anim.Set(el, UIElement.OpacityProperty, 0.55);
            Anim.To(el, UIElement.OpacityProperty, 0, MotionSettings.Custom(1800), Anim.EaseOut, 0.55);
        }
        else
        {
            Anim.Set(el, UIElement.OpacityProperty, e.NewValue is true ? 0.25 : 0);
        }
    }
}
