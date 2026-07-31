using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Brush = Microsoft.UI.Xaml.Media.Brush;

namespace OpenBlade.Tray;

/// <summary>
/// Restores the shared pointer-over fill used by settings rows while leaving
/// their nested controls independently interactive.
/// </summary>
public static class SettingsCardPointerVisual
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(SettingsCardPointerVisual),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty PointerOverBackgroundProperty =
        DependencyProperty.RegisterAttached(
            "PointerOverBackground",
            typeof(Brush),
            typeof(SettingsCardPointerVisual),
            new PropertyMetadata(null));

    public static bool GetIsEnabled(DependencyObject value) =>
        (bool)value.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject value, bool enabled) =>
        value.SetValue(IsEnabledProperty, enabled);

    public static Brush? GetPointerOverBackground(DependencyObject value) =>
        (Brush?)value.GetValue(PointerOverBackgroundProperty);

    public static void SetPointerOverBackground(
        DependencyObject value,
        Brush? background) =>
        value.SetValue(PointerOverBackgroundProperty, background);

    private static void OnIsEnabledChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        if (sender is not Border border || args.NewValue is not bool enabled)
        {
            return;
        }

        if (enabled)
        {
            border.PointerEntered += OnPointerEntered;
            border.PointerMoved += OnPointerMoved;
            border.PointerExited += OnPointerExited;
            border.Unloaded += OnUnloaded;
            return;
        }

        border.PointerEntered -= OnPointerEntered;
        border.PointerMoved -= OnPointerMoved;
        border.PointerExited -= OnPointerExited;
        border.Unloaded -= OnUnloaded;
        RestoreBackground(border);
    }

    private static void OnPointerEntered(
        object sender,
        PointerRoutedEventArgs args) =>
        UpdateBackground(sender as Border, args);

    private static void OnPointerMoved(
        object sender,
        PointerRoutedEventArgs args) =>
        UpdateBackground(sender as Border, args);

    private static void OnPointerExited(
        object sender,
        PointerRoutedEventArgs args) =>
        UpdateBackground(sender as Border, args);

    private static void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is Border border)
        {
            RestoreBackground(border);
        }
    }

    private static void UpdateBackground(
        Border? border,
        PointerRoutedEventArgs args)
    {
        if (border is null)
        {
            return;
        }

        Windows.Foundation.Point position =
            args.GetCurrentPoint(border).Position;
        bool isPointerOver =
            position.X >= 0
            && position.Y >= 0
            && position.X < border.ActualWidth
            && position.Y < border.ActualHeight;

        Brush? background = GetPointerOverBackground(border);
        if (isPointerOver && background is not null)
        {
            border.Background = background;
        }
        else
        {
            RestoreBackground(border);
        }
    }

    private static void RestoreBackground(Border border) =>
        border.ClearValue(Border.BackgroundProperty);
}
