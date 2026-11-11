using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DownloadsCleaner.Animations;

/// <summary>Низкоуровневые помощники анимации. Единственное место, где создаются DoubleAnimation.</summary>
public static class Anim
{
    public static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction EaseInOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseInOut });
    public static readonly IEasingFunction EaseIn = Freeze(new CubicEase { EasingMode = EasingMode.EaseIn });

    private static IEasingFunction Freeze(CubicEase e) { e.Freeze(); return e; }

    /// <summary>
    /// Плавно меняет числовое свойство. Итоговое значение записывается сразу, а анимация
    /// (FillBehavior.Stop) лишь «проигрывает» путь к нему, поэтому состояние никогда не «залипает».
    /// При выключенных анимациях значение применяется мгновенно.
    /// </summary>
    public static void To(DependencyObject target, DependencyProperty prop, double to,
        TimeSpan duration, IEasingFunction? ease = null, double? from = null, Action? done = null)
    {
        if (target is not IAnimatable animatable)
        {
            target.SetValue(prop, to);
            done?.Invoke();
            return;
        }

        try
        {
            if (!MotionSettings.Enabled || duration <= TimeSpan.Zero)
            {
                animatable.BeginAnimation(prop, null);
                target.SetValue(prop, to);
                done?.Invoke();
                return;
            }

            var start = from ?? Convert.ToDouble(target.GetValue(prop));
            if (double.IsNaN(start)) start = to;
            target.SetValue(prop, to);
            if (Math.Abs(start - to) < 0.0001)
            {
                animatable.BeginAnimation(prop, null);
                done?.Invoke();
                return;
            }

            var a = new DoubleAnimation(start, to, duration)
            {
                EasingFunction = ease ?? EaseOut,
                FillBehavior = FillBehavior.Stop
            };
            if (done != null) a.Completed += (_, _) => done();
            animatable.BeginAnimation(prop, a);
        }
        catch (InvalidOperationException)
        {
            // Замороженный объект: просто выставляем значение.
            try { target.SetValue(prop, to); } catch { /* ignore */ }
            done?.Invoke();
        }
    }

    /// <summary>Сбрасывает анимацию свойства и мгновенно выставляет значение.</summary>
    public static void Set(DependencyObject target, DependencyProperty prop, double value)
    {
        if (target is IAnimatable a)
        {
            try { a.BeginAnimation(prop, null); } catch { /* ignore */ }
        }
        target.SetValue(prop, value);
    }

    /// <summary>Гарантирует, что у элемента есть пара ScaleTransform + TranslateTransform.</summary>
    public static (ScaleTransform Scale, TranslateTransform Translate) Transforms(UIElement el)
    {
        if (el.RenderTransform is TransformGroup { IsFrozen: false } g
            && g.Children.Count == 2
            && g.Children[0] is ScaleTransform s
            && g.Children[1] is TranslateTransform t)
            return (s, t);

        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(translate);
        el.RenderTransform = group;
        if (el.RenderTransformOrigin == default) el.RenderTransformOrigin = new Point(0.5, 0.5);
        return (scale, translate);
    }

    public static void ScaleTo(UIElement el, double scale, TimeSpan duration, IEasingFunction? ease = null)
    {
        var (s, _) = Transforms(el);
        To(s, ScaleTransform.ScaleXProperty, scale, duration, ease);
        To(s, ScaleTransform.ScaleYProperty, scale, duration, ease);
    }
}
