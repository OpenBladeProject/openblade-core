using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed partial class MainWindow
{
    private void OnDisplayBrightnessSliderLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Slider slider)
        {
            return;
        }
        if (slider.Tag is DisplayBrightnessSliderState existing)
        {
            existing.IsLoaded = true;
            return;
        }

        DispatcherQueueTimer timer = dispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.IsRepeating = false;
        var state = new DisplayBrightnessSliderState(slider, timer) { IsLoaded = true };
        slider.Tag = state;
        timer.Tick += async (_, _) => await CommitDisplayBrightnessSliderAsync(state);
        // Thumb input is handled by Slider; observe it without replacing native input behavior.
        slider.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(OnDisplayBrightnessSliderPointerPressed), true);
        slider.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler(OnDisplayBrightnessSliderPointerCompleted), true);
        slider.AddHandler(UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(OnDisplayBrightnessSliderPointerCompleted), true);
        slider.AddHandler(UIElement.PointerCanceledEvent,
            new PointerEventHandler(OnDisplayBrightnessSliderPointerCanceled), true);
        slider.AddHandler(UIElement.KeyDownEvent,
            new Microsoft.UI.Xaml.Input.KeyEventHandler(OnDisplayBrightnessSliderKeyDown), true);
        slider.AddHandler(UIElement.KeyUpEvent,
            new Microsoft.UI.Xaml.Input.KeyEventHandler(OnDisplayBrightnessSliderKeyUp), true);
        slider.LostFocus += OnDisplayBrightnessSliderLostFocus;
    }

    private void OnDisplayBrightnessSliderUnloaded(object sender, RoutedEventArgs args)
    {
        if (sender is Slider { Tag: DisplayBrightnessSliderState state })
        {
            state.IsLoaded = false;
            ResetDisplayBrightnessSlider(state);
        }
    }

    private void OnDisplayBrightnessSliderPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (sender is Slider { Tag: DisplayBrightnessSliderState state }
            && DisplayViewModel.CanChangeBrightness)
        {
            state.Timer.Stop();
            state.Source = DisplayViewModel.ActivePowerSource;
            state.PointerInteraction = true;
        }
    }

    private void OnDisplayBrightnessSliderPointerCompleted(object sender, PointerRoutedEventArgs args)
    {
        if (sender is Slider { Tag: DisplayBrightnessSliderState state })
        {
            state.PointerInteraction = false;
            if (state.HasPendingValue && !state.KeyboardInteraction)
            {
                state.Timer.Stop();
                state.Timer.Start();
            }
        }
    }

    private void OnDisplayBrightnessSliderKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (IsBrightnessSliderAdjustmentKey(args.Key)
            && sender is Slider { Tag: DisplayBrightnessSliderState state })
        {
            state.Timer.Stop();
            if (!state.HasPendingValue)
            {
                state.Source = DisplayViewModel.ActivePowerSource;
            }
            state.KeyboardInteraction = true;
        }
    }

    private void OnDisplayBrightnessSliderKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (IsBrightnessSliderAdjustmentKey(args.Key))
        {
            OnDisplayBrightnessSliderLostFocus(sender, args);
        }
    }

    private void OnDisplayBrightnessSliderLostFocus(object sender, RoutedEventArgs args)
    {
        if (sender is Slider { Tag: DisplayBrightnessSliderState state })
        {
            state.KeyboardInteraction = false;
            if (state.HasPendingValue && !state.PointerInteraction)
            {
                state.Timer.Stop();
                state.Timer.Start();
            }
        }
    }

    private static bool IsBrightnessSliderAdjustmentKey(Windows.System.VirtualKey key) => key is
        Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right
        or Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down
        or Windows.System.VirtualKey.PageUp or Windows.System.VirtualKey.PageDown
        or Windows.System.VirtualKey.Home or Windows.System.VirtualKey.End;

    private void OnDisplayBrightnessSliderPointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (sender is Slider { Tag: DisplayBrightnessSliderState state })
        {
            ResetDisplayBrightnessSlider(state);
        }
    }

    private void OnDisplayBrightnessSliderValueChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (sender is not Slider { Tag: DisplayBrightnessSliderState state }
            || !state.IsLoaded || state.SuppressChanges || localizationSnapshotMode
            || Volatile.Read(ref disposed) != 0 || !DisplayViewModel.CanChangeBrightness
            || !double.IsFinite(args.NewValue))
        {
            return;
        }

        state.Timer.Stop();
        byte requested = checked((byte)Math.Clamp(Math.Round(args.NewValue), 0, 100));
        if (requested == DisplayViewModel.BrightnessPercent)
        {
            state.HasPendingValue = false;
            return;
        }
        if (!state.HasPendingValue && !state.PointerInteraction)
        {
            state.Source = DisplayViewModel.ActivePowerSource;
        }
        state.RequestedValue = requested;
        state.HasPendingValue = true;
        if (!state.PointerInteraction && !state.KeyboardInteraction)
        {
            // Keyboard, wheel and accessibility changes share one bounded quiet-period commit.
            state.Timer.Start();
        }
    }

    private async Task CommitDisplayBrightnessSliderAsync(DisplayBrightnessSliderState state)
    {
        state.Timer.Stop();
        if (!state.HasPendingValue || state.PointerInteraction || state.KeyboardInteraction)
        {
            return;
        }
        byte requested = state.RequestedValue;
        state.HasPendingValue = false;
        if (!state.IsLoaded || localizationSnapshotMode || Volatile.Read(ref disposed) != 0
            || state.Source != DisplayViewModel.ActivePowerSource || !DisplayViewModel.CanChangeBrightness)
        {
            ResetDisplayBrightnessSlider(state);
            return;
        }
        try
        {
            await RunHandlerAsync(() => DisplayViewModel.SetBrightnessAsync(requested),
                nameof(CommitDisplayBrightnessSliderAsync));
        }
        finally
        {
            ResetDisplayBrightnessSlider(state);
        }
    }

    private void ResetDisplayBrightnessSlider(DisplayBrightnessSliderState state)
    {
        state.Timer.Stop();
        state.HasPendingValue = false;
        state.PointerInteraction = false;
        state.KeyboardInteraction = false;
        state.SuppressChanges = true;
        try
        {
            state.Slider.Value = DisplayViewModel.BrightnessSliderValue;
        }
        finally
        {
            state.SuppressChanges = false;
        }
    }

    private sealed class DisplayBrightnessSliderState(Slider slider, DispatcherQueueTimer timer)
    {
        internal Slider Slider { get; } = slider;
        internal DispatcherQueueTimer Timer { get; } = timer;
        internal bool IsLoaded { get; set; }
        internal bool SuppressChanges { get; set; }
        internal bool PointerInteraction { get; set; }
        internal bool KeyboardInteraction { get; set; }
        internal bool HasPendingValue { get; set; }
        internal PowerSource Source { get; set; }
        internal byte RequestedValue { get; set; }
    }
}
