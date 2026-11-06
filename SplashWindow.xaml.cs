using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AltDeck;

/// <summary>Start-up animation: the logo squares pop in, the name slides up, a line fills. About 1.5 seconds.</summary>
public partial class SplashWindow : Window
{
    public SplashWindow() { InitializeComponent(); }

    private static DoubleAnimation Pop(double from, double to, int ms, int delayMs, IEasingFunction? ease = null) =>
        new(from, to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd
        };

    private static void Scale(FrameworkElement el, int delayMs)
    {
        var st = new ScaleTransform(0, 0);
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = st;
        var a = Pop(0, 1, 460, delayMs, new BackEase { Amplitude = 0.7, EasingMode = EasingMode.EaseOut });
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    /// <summary>Starts the animation; the task finishes when it is done.</summary>
    public Task PlayAsync()
    {
        var done = new TaskCompletionSource();

        Scale(SqA, 0);
        Scale(SqB, 140);
        Scale(SqC, 280);

        var smooth = new CubicEase { EasingMode = EasingMode.EaseOut };
        Title1.BeginAnimation(OpacityProperty, Pop(0, 1, 380, 620));
        TitleShift.BeginAnimation(TranslateTransform.YProperty, Pop(12, 0, 420, 620, smooth));
        Tagline.BeginAnimation(OpacityProperty, Pop(0, 1, 380, 860));

        var fill = Pop(0, 1, 1100, 300, new CubicEase { EasingMode = EasingMode.EaseInOut });
        fill.Completed += async (_, _) =>
        {
            await Task.Delay(180);   // a short pause on the finished splash
            done.TrySetResult();
        };
        FillScale.BeginAnimation(ScaleTransform.ScaleXProperty, fill);

        return done.Task;
    }

    /// <summary>Fades the splash out and closes it.</summary>
    public Task FadeOutAsync()
    {
        var done = new TaskCompletionSource();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260));
        fade.Completed += (_, _) => { Close(); done.TrySetResult(); };
        BeginAnimation(OpacityProperty, fade);
        return done.Task;
    }
}
