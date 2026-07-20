using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace OpenBlade.Tray;

/// <summary>
/// Animates conditional settings subcards without leaving blank layout space.
/// Height animation is deliberately short so surrounding rows reposition smoothly.
/// </summary>
public static class AnimatedVisibility
{
    private static readonly Duration AnimationDuration =
        new(TimeSpan.FromMilliseconds(180));

    public static readonly DependencyProperty IsVisibleProperty =
        DependencyProperty.RegisterAttached(
            "IsVisible",
            typeof(bool),
            typeof(AnimatedVisibility),
            new PropertyMetadata(false, OnIsVisibleChanged));

    private static readonly DependencyProperty ActiveStoryboardProperty =
        DependencyProperty.RegisterAttached(
            "ActiveStoryboard",
            typeof(Storyboard),
            typeof(AnimatedVisibility),
            new PropertyMetadata(null));

    public static bool GetIsVisible(DependencyObject value) =>
        (bool)value.GetValue(IsVisibleProperty);

    public static void SetIsVisible(DependencyObject value, bool visible) =>
        value.SetValue(IsVisibleProperty, visible);

    private static void OnIsVisibleChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element || args.NewValue is not bool visible)
        {
            return;
        }

        if (element.GetValue(ActiveStoryboardProperty) is Storyboard active)
        {
            active.Stop();
            element.ClearValue(ActiveStoryboardProperty);
        }

        if (visible)
        {
            AnimateIn(element);
        }
        else
        {
            AnimateOut(element);
        }
    }

    private static void AnimateIn(FrameworkElement element)
    {
        element.Visibility = Visibility.Visible;
        element.Height = double.NaN;
        double availableWidth = element.ActualWidth > 0
            ? element.ActualWidth
            : double.PositiveInfinity;
        element.Measure(new Windows.Foundation.Size(
            availableWidth,
            double.PositiveInfinity));
        double targetHeight = element.DesiredSize.Height;
        if (!double.IsFinite(targetHeight) || targetHeight <= 0)
        {
            SetFinalState(element, visible: true);
            return;
        }

        element.Height = 0;
        element.Opacity = 0;
        Storyboard storyboard = CreateStoryboard(
            element,
            fromHeight: 0,
            toHeight: targetHeight,
            fromOpacity: 0,
            toOpacity: 1);
        storyboard.Completed += (_, _) =>
        {
            if (GetIsVisible(element))
            {
                SetFinalState(element, visible: true);
            }
            element.ClearValue(ActiveStoryboardProperty);
        };
        element.SetValue(ActiveStoryboardProperty, storyboard);
        storyboard.Begin();
    }

    private static void AnimateOut(FrameworkElement element)
    {
        if (element.Visibility == Visibility.Collapsed)
        {
            SetFinalState(element, visible: false);
            return;
        }

        double currentHeight = element.ActualHeight;
        if (!double.IsFinite(currentHeight) || currentHeight <= 0)
        {
            SetFinalState(element, visible: false);
            return;
        }

        element.Height = currentHeight;
        Storyboard storyboard = CreateStoryboard(
            element,
            fromHeight: currentHeight,
            toHeight: 0,
            fromOpacity: element.Opacity,
            toOpacity: 0);
        storyboard.Completed += (_, _) =>
        {
            if (!GetIsVisible(element))
            {
                SetFinalState(element, visible: false);
            }
            element.ClearValue(ActiveStoryboardProperty);
        };
        element.SetValue(ActiveStoryboardProperty, storyboard);
        storyboard.Begin();
    }

    private static Storyboard CreateStoryboard(
        FrameworkElement element,
        double fromHeight,
        double toHeight,
        double fromOpacity,
        double toOpacity)
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var height = new DoubleAnimation
        {
            From = fromHeight,
            To = toHeight,
            Duration = AnimationDuration,
            EasingFunction = easing,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(height, element);
        Storyboard.SetTargetProperty(height, "Height");

        var opacity = new DoubleAnimation
        {
            From = fromOpacity,
            To = toOpacity,
            Duration = AnimationDuration,
            EasingFunction = easing,
        };
        Storyboard.SetTarget(opacity, element);
        Storyboard.SetTargetProperty(opacity, "Opacity");

        var storyboard = new Storyboard();
        storyboard.Children.Add(height);
        storyboard.Children.Add(opacity);
        return storyboard;
    }

    private static void SetFinalState(FrameworkElement element, bool visible)
    {
        element.Height = double.NaN;
        element.Opacity = 1;
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }
}
