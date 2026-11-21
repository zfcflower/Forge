using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Forge.App.Views;

/// <summary>
/// A glyph that turns while something loads: the icon comes from <see cref="Resources/Icons"/> like every other, and the
/// rotation is a dispatcher timer rather than an animation. The chrome's animations are declarative, and a style-driven
/// render transform makes <c>Window.Show</c> throw in the headless tests (see the notification overlay's note); the one
/// indicator that has to run inside those windows therefore drives its own transform. The timer only runs while the control
/// is attached and visible, so a transcript that stops waiting stops spending frames.
/// </summary>
public sealed class WaitingGlyph : PathIcon
{
    /// <summary>A full turn takes 1.8 s: 7.5° every 16 ms, smooth at the frame rate a timer can promise.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(16);

    private const double Step = 7.5;

    private readonly RotateTransform rotation = new();
    private DispatcherTimer? timer;
    private double angle;
    private bool attached;

    public WaitingGlyph()
    {
        RenderTransform = rotation;
        RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        attached = true;
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        attached = false;
        Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
        {
            UpdateTimer();
        }
    }

    private void UpdateTimer()
    {
        if (IsVisible && attached)
        {
            Start();
        }
        else
        {
            Stop();
        }
    }

    private void Start()
    {
        if (timer is not null)
        {
            return;
        }

        timer = new DispatcherTimer { Interval = Tick };
        timer.Tick += OnTick;
        timer.Start();
    }

    private void Stop()
    {
        if (timer is not { } running)
        {
            return;
        }

        running.Stop();
        running.Tick -= OnTick;
        timer = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        angle = (angle + Step) % 360;
        rotation.Angle = angle;
    }
}
