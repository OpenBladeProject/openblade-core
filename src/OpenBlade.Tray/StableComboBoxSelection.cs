using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ComboBox = Microsoft.UI.Xaml.Controls.ComboBox;

namespace OpenBlade.Tray;

/// <summary>
/// Keeps a ComboBox aligned with its view-model selection when WinUI rebuilds
/// or replaces the ItemsSource. The normal SelectedItem binding remains the
/// source of truth; Anchor is a one-way copy used only to restore the visual
/// selection after WinUI clears it during initialization or refresh.
/// </summary>
public static class StableComboBoxSelection
{
    public static readonly DependencyProperty AnchorProperty =
        DependencyProperty.RegisterAttached(
            "Anchor",
            typeof(object),
            typeof(StableComboBoxSelection),
            new PropertyMetadata(null, OnAnchorChanged));

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached(
            "State",
            typeof(SelectionState),
            typeof(StableComboBoxSelection),
            new PropertyMetadata(null));

    public static object? GetAnchor(DependencyObject value) =>
        value.GetValue(AnchorProperty);

    public static void SetAnchor(DependencyObject value, object? anchor) =>
        value.SetValue(AnchorProperty, anchor);

    private static void OnAnchorChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        if (sender is not ComboBox comboBox)
        {
            return;
        }

        SelectionState state = EnsureState(comboBox);
        Reconcile(comboBox, state);
        QueueReconcile(comboBox, state);
    }

    private static SelectionState EnsureState(ComboBox comboBox)
    {
        if (comboBox.GetValue(StateProperty) is SelectionState existing)
        {
            return existing;
        }

        var state = new SelectionState();
        comboBox.SetValue(StateProperty, state);
        comboBox.RegisterPropertyChangedCallback(
            ItemsControl.ItemsSourceProperty,
            OnItemsSourceChanged);
        comboBox.Loaded += OnLoaded;
        comboBox.SelectionChanged += OnSelectionChanged;
        return state;
    }

    private static void OnItemsSourceChanged(
        DependencyObject sender,
        DependencyProperty dependencyProperty)
    {
        if (sender is ComboBox comboBox
            && comboBox.GetValue(StateProperty) is SelectionState state)
        {
            Reconcile(comboBox, state);
            QueueReconcile(comboBox, state);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is ComboBox comboBox
            && comboBox.GetValue(StateProperty) is SelectionState state)
        {
            Reconcile(comboBox, state);
            QueueReconcile(comboBox, state);
        }
    }

    private static void OnSelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (sender is not ComboBox comboBox
            || comboBox.SelectedItem is not null
            || comboBox.GetValue(StateProperty) is not SelectionState state
            || state.IsReconciling
            || GetAnchor(comboBox) is not { } anchor
            || !TryFindEquivalentItem(comboBox, anchor, out _))
        {
            return;
        }

        QueueReconcile(comboBox, state);
    }

    private static void QueueReconcile(ComboBox comboBox, SelectionState state)
    {
        if (state.IsQueued)
        {
            return;
        }

        state.IsQueued = true;
        DispatcherQueue dispatcher = comboBox.DispatcherQueue;
        if (!dispatcher.TryEnqueue(() =>
        {
            state.IsQueued = false;
            Reconcile(comboBox, state);
        }))
        {
            state.IsQueued = false;
        }
    }

    private static void Reconcile(ComboBox comboBox, SelectionState state)
    {
        if (state.IsReconciling)
        {
            return;
        }

        object? anchor = GetAnchor(comboBox);
        object? expected = null;
        if (anchor is not null
            && TryFindEquivalentItem(comboBox, anchor, out object? match))
        {
            expected = match;
        }
        if (ReferenceEquals(comboBox.SelectedItem, expected))
        {
            return;
        }

        state.IsReconciling = true;
        try
        {
            comboBox.SelectedItem = expected;
        }
        finally
        {
            state.IsReconciling = false;
        }
    }

    private static bool TryFindEquivalentItem(
        ComboBox comboBox,
        object anchor,
        out object? match)
    {
        foreach (object? item in comboBox.Items)
        {
            if (ReferenceEquals(item, anchor) || Equals(item, anchor))
            {
                match = item;
                return true;
            }
        }

        match = null;
        return false;
    }

    private sealed class SelectionState
    {
        public bool IsQueued { get; set; }

        public bool IsReconciling { get; set; }
    }
}
