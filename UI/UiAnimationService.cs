namespace RustedShpizhionStudio.UI;

public static class UiAnimationService
{
    public static readonly TimeSpan WindowFadeDuration = TimeSpan.FromMilliseconds(170);
    public static readonly TimeSpan ControlFadeDuration = TimeSpan.FromMilliseconds(145);
    public static readonly TimeSpan CardRevealDuration = TimeSpan.FromMilliseconds(170);
    public static readonly TimeSpan ThumbnailRevealDuration = TimeSpan.FromMilliseconds(145);

    public static bool Enabled { get; private set; } = true;

    public static void SetEnabled(bool enabled) => Enabled = enabled;

    public static void PrepareWindow(Window window, bool? enabled = null)
    {
        var useAnimations = enabled ?? Enabled;
        if (!useAnimations)
        {
            window.Transitions = null;
            window.Opacity = 1;
            return;
        }

        window.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = WindowFadeDuration
            }
        };
        window.Opacity = 0;
        window.Opened += (_, _) =>
            Avalonia.Threading.Dispatcher.UIThread.Post(() => window.Opacity = 1, Avalonia.Threading.DispatcherPriority.Background);
    }

    public static void PrepareOpacityTransition(Control control, bool enabled, TimeSpan? duration = null)
    {
        control.Transitions = enabled
            ? new Transitions
            {
                new DoubleTransition
                {
                    Property = Visual.OpacityProperty,
                    Duration = duration ?? ControlFadeDuration
                }
            }
            : null;
    }
}
