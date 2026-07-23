using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ComboBox = Microsoft.UI.Xaml.Controls.ComboBox;

namespace OpenBlade.Tray;

public static class SpecialKeyActionSelection
{
    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.RegisterAttached(
            "Source",
            typeof(SpecialKeyBindingViewModel),
            typeof(SpecialKeyActionSelection),
            new PropertyMetadata(null, OnSourceChanged));

    public static SpecialKeyBindingViewModel? GetSource(DependencyObject value) =>
        (SpecialKeyBindingViewModel?)value.GetValue(SourceProperty);

    public static void SetSource(
        DependencyObject value,
        SpecialKeyBindingViewModel? source) =>
        value.SetValue(SourceProperty, source);

    private static void OnSourceChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        if (sender is not ComboBox comboBox)
        {
            return;
        }

        comboBox.SelectionChanged -= OnSelectionChanged;
        if (args.NewValue is SpecialKeyBindingViewModel)
        {
            comboBox.SelectionChanged += OnSelectionChanged;
        }
    }

    private static void OnSelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (sender is ComboBox
            {
                SelectedItem: SpecialKeyActionOption selectedAction,
            } comboBox
            && GetSource(comboBox) is { } binding
            && binding.SelectedAction != selectedAction)
        {
            binding.SelectedAction = selectedAction;
        }
    }
}
