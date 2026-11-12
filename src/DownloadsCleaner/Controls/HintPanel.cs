using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DownloadsCleaner.Animations;
using DownloadsCleaner.Localization;
using DownloadsCleaner.Services;

namespace DownloadsCleaner.Controls;

/// <summary>Шаг подсказки: значок и ключи перевода заголовка и текста.</summary>
public sealed class HintStep
{
    public string Glyph { get; set; } = "";
    public string TitleKey { get; set; } = string.Empty;
    public string TextKey { get; set; } = string.Empty;
}

/// <summary>
/// Какие подсказки пользователь уже закрыл. Хранится в настройках, а без запущенного
/// приложения (тесты, дизайнер) - только в памяти.
/// </summary>
public static class HintsState
{
    private static readonly HashSet<string> SessionDismissed = new(StringComparer.Ordinal);

    /// <summary>Пользователь попросил показать подсказку снова (кнопка «Как это работает?»).</summary>
    public static event Action<string>? ShowRequested;

    private static ISettingsService? Settings =>
        Application.Current is App ? App.Services.GetService(typeof(ISettingsService)) as ISettingsService : null;

    public static bool IsDismissed(string id) =>
        Settings is { } s ? s.Current.DismissedHints.Contains(id) : SessionDismissed.Contains(id);

    public static void Dismiss(string id)
    {
        if (Settings is { } s)
        {
            if (!s.Current.DismissedHints.Contains(id)) s.Current.DismissedHints.Add(id);
        }
        else SessionDismissed.Add(id);
    }

    public static void Show(string id)
    {
        if (Settings is { } s) s.Current.DismissedHints.Remove(id);
        else SessionDismissed.Remove(id);
        ShowRequested?.Invoke(id);
    }
}

/// <summary>Кнопка «Как это работает?» в заголовке страницы: снова открывает подсказку.</summary>
public sealed class HintButton : Button
{
    public string HintId { get; set; } = string.Empty;

    public HintButton()
    {
        Cursor = System.Windows.Input.Cursors.Hand;
        // Страницы создаются заново при каждом переходе - подписку на смену языка снимаем при выгрузке.
        Loaded += (_, _) =>
        {
            Loc.LanguageChanged -= OnLanguageChanged;
            Loc.LanguageChanged += OnLanguageChanged;
            Render();
        };
        Unloaded += (_, _) => Loc.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Render);

    private void Render()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new TextBlock { Text = "", FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        if (TryFindResource("IconText") is Style iconStyle) icon.Style = iconStyle;
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        panel.Children.Add(icon);
        panel.Children.Add(new TextBlock { Text = Loc.T("HintHowItWorks"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        Content = panel;
    }

    protected override void OnClick()
    {
        base.OnClick();
        if (HintId.Length > 0) HintsState.Show(HintId);
    }
}

/// <summary>
/// Анимированная подсказка «Как это работает»: шаги появляются по очереди, затем подсветка
/// по кругу переходит от шага к шагу и показывает порядок работы. «Понятно» сворачивает
/// подсказку и запоминает это; кнопка <see cref="HintButton"/> возвращает её.
/// Все эффекты подчиняются <see cref="MotionSettings"/>: без анимаций подсказка просто статична.
/// </summary>
[ContentProperty(nameof(Steps))]
public sealed class HintPanel : UserControl
{
    private const int StaggerMs = 140;
    private static readonly TimeSpan CycleInterval = TimeSpan.FromMilliseconds(2600);

    private readonly List<StepVisual> _visuals = new();
    private readonly DispatcherTimer _cycle = new() { Interval = CycleInterval };
    private Border? _card;
    private int _active = -1;

    public string HintId { get; set; } = string.Empty;
    public string TitleKey { get; set; } = string.Empty;
    public List<HintStep> Steps { get; } = new();

    public HintPanel()
    {
        Visibility = Visibility.Collapsed;
        _cycle.Tick += (_, _) => Activate((_active + 1) % Math.Max(1, _visuals.Count));
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            _cycle.Stop();
            HintsState.ShowRequested -= OnShowRequested;
            Loc.LanguageChanged -= OnLanguageChanged;
        };
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) StartCycle();
            else _cycle.Stop();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HintsState.ShowRequested -= OnShowRequested;
        HintsState.ShowRequested += OnShowRequested;
        Loc.LanguageChanged -= OnLanguageChanged;
        Loc.LanguageChanged += OnLanguageChanged;
        if (HintsState.IsDismissed(HintId)) return;
        Open();
    }

    private void OnShowRequested(string id)
    {
        if (id != HintId) return;
        Dispatcher.InvokeAsync(Open);
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        if (Visibility == Visibility.Visible) { Build(); Activate(Math.Max(0, _active), animate: false); }
    });

    // ------------------------------------------------------------------ Показ и скрытие

    private void Open()
    {
        Build();
        Visibility = Visibility.Visible;
        _active = -1;

        if (!MotionSettings.Enabled)
        {
            Activate(0, animate: false);
            return;
        }

        // Карточка всплывает, шаги выезжают по очереди, значки «выпрыгивают».
        var (cs, ct) = Anim.Transforms(_card!);
        Anim.Set(_card!, OpacityProperty, 0);
        Anim.To(_card!, OpacityProperty, 1, MotionSettings.Slow, Anim.EaseOut, 0);
        Anim.To(ct, TranslateTransform.YProperty, 0, MotionSettings.Slow, Anim.EaseOut, 16);
        Anim.Set(cs, ScaleTransform.ScaleYProperty, 1);

        for (var i = 0; i < _visuals.Count; i++)
            _visuals[i].PlayEntrance(TimeSpan.FromMilliseconds(180 + i * StaggerMs));

        // Подсветка начинает «бежать», когда все шаги уже на месте.
        var firstActivation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260 + _visuals.Count * StaggerMs + 300) };
        firstActivation.Tick += (_, _) =>
        {
            firstActivation.Stop();
            if (Visibility != Visibility.Visible) return;
            Activate(0);
            StartCycle();
        };
        firstActivation.Start();
    }

    private void Close()
    {
        _cycle.Stop();
        HintsState.Dismiss(HintId);
        if (!MotionSettings.Enabled || _card == null)
        {
            Visibility = Visibility.Collapsed;
            return;
        }

        // Сворачиваем по высоте и растворяем, затем убираем из раскладки.
        if (_card.LayoutTransform is not ScaleTransform { IsFrozen: false }) _card.LayoutTransform = new ScaleTransform(1, 1);
        Anim.To(_card, OpacityProperty, 0, MotionSettings.Normal, Anim.EaseIn);
        Anim.To((ScaleTransform)_card.LayoutTransform, ScaleTransform.ScaleYProperty, 0, MotionSettings.Slow, Anim.EaseInOut,
            done: () =>
            {
                Visibility = Visibility.Collapsed;
                Anim.Set((ScaleTransform)_card.LayoutTransform, ScaleTransform.ScaleYProperty, 1);
                Anim.Set(_card, OpacityProperty, 1);
            });
    }

    private void StartCycle()
    {
        if (MotionSettings.Enabled && Visibility == Visibility.Visible && _visuals.Count > 1 && _active >= 0)
            _cycle.Start();
    }

    private void Activate(int index, bool animate = true)
    {
        if (index < 0 || index >= _visuals.Count) return;
        for (var i = 0; i < _visuals.Count; i++) _visuals[i].SetActive(i == index, animate && MotionSettings.Enabled);
        _active = index;
    }

    // ------------------------------------------------------------------ Построение

    private void Build()
    {
        _visuals.Clear();

        var bulbBack = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(10) };
        bulbBack.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
        var bulb = Icon("", 16);
        bulb.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        bulbBack.Child = bulb;

        var title = Text(Loc.T(TitleKey), "TextBody");
        title.FontWeight = FontWeights.SemiBold;
        title.FontSize = 15;
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(12, 0, 12, 0);

        var close = new Button { Content = Loc.T("HintGotIt"), Padding = new Thickness(14, 6, 14, 6) };
        if (TryFindResource("SecondaryButton") is Style btnStyle) close.Style = btnStyle;
        close.Click += (_, _) => Close();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(bulbBack);
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        Grid.SetColumn(close, 2);
        header.Children.Add(close);

        var steps = new UniformGrid { Columns = Math.Max(1, Steps.Count), Margin = new Thickness(0, 14, 0, 0) };
        for (var i = 0; i < Steps.Count; i++)
        {
            var visual = new StepVisual(this, Steps[i], i + 1, isLast: i == Steps.Count - 1);
            _visuals.Add(visual);
            steps.Children.Add(visual.Root);
        }

        var body = new StackPanel();
        body.Children.Add(header);
        body.Children.Add(steps);

        _card = new Border { Padding = new Thickness(20, 16, 20, 16), Margin = new Thickness(0, 0, 0, 14), Child = body };
        if (TryFindResource("Card") is Style card) _card.Style = card;
        Content = _card;
    }

    private TextBlock Icon(string glyph, double size)
    {
        var t = new TextBlock { Text = glyph, FontSize = size, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (TryFindResource("IconText") is Style s) t.Style = s;
        t.FontSize = size;
        return t;
    }

    private TextBlock Text(string text, string style)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        if (TryFindResource(style) is Style s) t.Style = s;
        return t;
    }

    /// <summary>Один шаг: кружок со значком, заголовок, текст и стрелка к следующему шагу.</summary>
    private sealed class StepVisual
    {
        private readonly Border _highlight;
        private readonly Border _circleFill;
        private readonly TextBlock _glyphOnFill;
        private readonly Grid _circle;
        private readonly FrameworkElement _content;
        private readonly TextBlock? _arrow;

        public Grid Root { get; } = new();

        public StepVisual(HintPanel owner, HintStep step, int number, bool isLast)
        {
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(isLast ? 0 : 26) });

            // Подложка активного шага.
            _highlight = new Border { CornerRadius = new CornerRadius(12), Opacity = 0, Margin = new Thickness(-8, -6, 4, -6) };
            _highlight.SetResourceReference(Border.BackgroundProperty, "HoverOverlayBrush");
            Root.Children.Add(_highlight);

            // Кружок: мягкий фон, поверх него - заливка акцентом для активного шага.
            var circleBack = new Border { CornerRadius = new CornerRadius(20) };
            circleBack.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
            var glyph = owner.Icon(step.Glyph, 17);
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
            _circleFill = new Border { CornerRadius = new CornerRadius(20), Opacity = 0 };
            _circleFill.SetResourceReference(Border.BackgroundProperty, "AccentGradientBrush");
            _glyphOnFill = owner.Icon(step.Glyph, 17);
            _glyphOnFill.Foreground = Brushes.White;
            _glyphOnFill.Opacity = 0;
            _circle = new Grid { Width = 40, Height = 40, HorizontalAlignment = HorizontalAlignment.Left };
            _circle.Children.Add(circleBack);
            _circle.Children.Add(glyph);
            _circle.Children.Add(_circleFill);
            _circle.Children.Add(_glyphOnFill);

            var title = owner.Text($"{number}. {Loc.T(step.TitleKey)}", "TextBody");
            title.FontWeight = FontWeights.SemiBold;
            title.Margin = new Thickness(0, 10, 0, 0);
            var text = owner.Text(Loc.T(step.TextKey), "TextCaption");
            text.Margin = new Thickness(0, 4, 0, 0);

            var content = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            content.Children.Add(_circle);
            content.Children.Add(title);
            content.Children.Add(text);
            _content = content;
            Root.Children.Add(content);

            if (!isLast)
            {
                _arrow = owner.Icon("", 12);
                _arrow.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
                _arrow.VerticalAlignment = VerticalAlignment.Top;
                _arrow.Margin = new Thickness(0, 14, 0, 0);
                Grid.SetColumn(_arrow, 1);
                Root.Children.Add(_arrow);
            }
        }

        /// <summary>Шаг выезжает слева, значок «выпрыгивает» с лёгкой пружиной, стрелка проявляется следом.</summary>
        public void PlayEntrance(TimeSpan delay)
        {
            var (_, t) = Anim.Transforms(_content);
            var (cs, _) = Anim.Transforms(_circle);
            Anim.Set(_content, UIElement.OpacityProperty, 0);
            Anim.Set(t, TranslateTransform.XProperty, -22);
            Anim.Set(cs, ScaleTransform.ScaleXProperty, 0.4);
            Anim.Set(cs, ScaleTransform.ScaleYProperty, 0.4);
            if (_arrow != null) Anim.Set(_arrow, UIElement.OpacityProperty, 0);

            var timer = new DispatcherTimer { Interval = delay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Anim.To(_content, UIElement.OpacityProperty, 1, MotionSettings.Slow, Anim.EaseOut, 0);
                Anim.To(t, TranslateTransform.XProperty, 0, MotionSettings.Slow, Anim.EaseOut, -22);
                var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
                Anim.To(cs, ScaleTransform.ScaleXProperty, 1, MotionSettings.Custom(460), spring, 0.4);
                Anim.To(cs, ScaleTransform.ScaleYProperty, 1, MotionSettings.Custom(460), spring, 0.4);
                if (_arrow != null)
                {
                    var (_, at) = Anim.Transforms(_arrow);
                    Anim.To(_arrow, UIElement.OpacityProperty, 1, MotionSettings.Slow, Anim.EaseOut, 0);
                    Anim.To(at, TranslateTransform.XProperty, 0, MotionSettings.Slow, Anim.EaseOut, -8);
                }
            };
            timer.Start();
        }

        /// <summary>Активный шаг: подложка, заливка значка и лёгкий подъём; стрелка за ним «подталкивает» вперёд.</summary>
        public void SetActive(bool active, bool animate)
        {
            var dur = animate ? MotionSettings.Slow : TimeSpan.Zero;
            var (_, ct) = Anim.Transforms(_circle);
            Anim.To(_highlight, UIElement.OpacityProperty, active ? 1 : 0, dur, Anim.EaseInOut);
            Anim.To(_circleFill, UIElement.OpacityProperty, active ? 1 : 0, dur, Anim.EaseInOut);
            Anim.To(_glyphOnFill, UIElement.OpacityProperty, active ? 1 : 0, dur, Anim.EaseInOut);
            Anim.To(ct, TranslateTransform.YProperty, active ? -3 : 0, dur, Anim.EaseOut);

            if (_arrow != null)
            {
                _arrow.SetResourceReference(TextBlock.ForegroundProperty, active ? "AccentBrush" : "TextMutedBrush");
                if (active && animate)
                {
                    var (_, at) = Anim.Transforms(_arrow);
                    Anim.To(at, TranslateTransform.XProperty, 0, MotionSettings.Custom(520), new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 }, -6);
                }
            }
        }
    }
}
