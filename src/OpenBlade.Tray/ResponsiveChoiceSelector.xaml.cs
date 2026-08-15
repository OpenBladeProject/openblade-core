using System.Collections;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace OpenBlade.Tray;

public sealed class ResponsiveChoiceSelectionChangedEventArgs(object? selectedItem)
    : EventArgs
{
    public object? SelectedItem { get; } = selectedItem;
}

public sealed partial class ResponsiveChoiceSelector
    : Microsoft.UI.Xaml.Controls.UserControl
{
    private const double EstimatedCharacterWidthDip = 7;
    private const double EstimatedItemChromeWidthDip = 28;
    private const double MinimumEstimatedItemWidthDip = 72;
    private const double MaximumEstimatedItemWidthDip = 220;
    private const double EstimatedItemSpacingDip = 8;

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(object),
            typeof(ResponsiveChoiceSelector),
            new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty SelectedItemProperty =
        DependencyProperty.Register(
            nameof(SelectedItem),
            typeof(object),
            typeof(ResponsiveChoiceSelector),
            new PropertyMetadata(null, OnSelectedItemChanged));

    public static readonly DependencyProperty DisplayMemberPathProperty =
        DependencyProperty.Register(
            nameof(DisplayMemberPath),
            typeof(string),
            typeof(ResponsiveChoiceSelector),
            new PropertyMetadata(string.Empty, OnDisplayMemberPathChanged));

    public static readonly DependencyProperty PlaceholderTextProperty =
        DependencyProperty.Register(
            nameof(PlaceholderText),
            typeof(string),
            typeof(ResponsiveChoiceSelector),
            new PropertyMetadata(string.Empty, OnPlaceholderTextChanged));

    public static readonly DependencyProperty AccessibleNameProperty =
        DependencyProperty.Register(
            nameof(AccessibleName),
            typeof(string),
            typeof(ResponsiveChoiceSelector),
            new PropertyMetadata(string.Empty, OnAccessibleNameChanged));

    public static readonly DependencyProperty CompactMinWidthProperty =
        DependencyProperty.Register(
            nameof(CompactMinWidth),
            typeof(double),
            typeof(ResponsiveChoiceSelector),
            new PropertyMetadata(150d, OnCompactMinWidthChanged));

    public static readonly DependencyProperty ExpandedMinWindowWidthProperty =
        DependencyProperty.Register(
            nameof(ExpandedMinWindowWidth),
            typeof(double),
            typeof(ResponsiveChoiceSelector),
            new PropertyMetadata(800d, OnResponsiveConstraintChanged));

    public static readonly DependencyProperty ExpandedWidthAllowanceProperty =
        DependencyProperty.Register(
            nameof(ExpandedWidthAllowance),
            typeof(double),
            typeof(ResponsiveChoiceSelector),
            new PropertyMetadata(360d, OnResponsiveConstraintChanged));

    private readonly List<object> projectedOptions = [];
    private XamlRoot? observedXamlRoot;
    private FrameworkElement? responsiveHost;
    private double measuredExpandedWidth;
    private bool synchronizingSelection;
    private bool expandedPresentationActive;

    public ResponsiveChoiceSelector()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event EventHandler<ResponsiveChoiceSelectionChangedEventArgs>? SelectionChanged;

    public event EventHandler<object>? DropDownOpened;

    public event EventHandler<object>? DropDownClosed;

    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public string DisplayMemberPath
    {
        get => (string)GetValue(DisplayMemberPathProperty);
        set => SetValue(DisplayMemberPathProperty, value);
    }

    public string PlaceholderText
    {
        get => (string)GetValue(PlaceholderTextProperty);
        set => SetValue(PlaceholderTextProperty, value);
    }

    public string AccessibleName
    {
        get => (string)GetValue(AccessibleNameProperty);
        set => SetValue(AccessibleNameProperty, value);
    }

    public double CompactMinWidth
    {
        get => (double)GetValue(CompactMinWidthProperty);
        set => SetValue(CompactMinWidthProperty, value);
    }

    public double ExpandedMinWindowWidth
    {
        get => (double)GetValue(ExpandedMinWindowWidthProperty);
        set => SetValue(ExpandedMinWindowWidthProperty, value);
    }

    public double ExpandedWidthAllowance
    {
        get => (double)GetValue(ExpandedWidthAllowanceProperty);
        set => SetValue(ExpandedWidthAllowanceProperty, value);
    }

    private static void OnItemsSourceChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args) =>
        ((ResponsiveChoiceSelector)dependencyObject).RebuildOptions();

    private static void OnSelectedItemChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        var selector = (ResponsiveChoiceSelector)dependencyObject;
        selector.SynchronizeSelection(args.NewValue);
        selector.UpdatePresentationMode();
    }

    private static void OnDisplayMemberPathChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        var selector = (ResponsiveChoiceSelector)dependencyObject;
        selector.CompactSelector.DisplayMemberPath = args.NewValue as string ?? string.Empty;
        selector.RebuildOptions();
    }

    private static void OnPlaceholderTextChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args) =>
        ((ResponsiveChoiceSelector)dependencyObject).CompactSelector.PlaceholderText =
            args.NewValue as string ?? string.Empty;

    private static void OnAccessibleNameChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        var selector = (ResponsiveChoiceSelector)dependencyObject;
        string name = args.NewValue as string ?? string.Empty;
        AutomationProperties.SetName(selector.ExpandedSelector, name);
        AutomationProperties.SetName(selector.CompactSelector, name);
    }

    private static void OnCompactMinWidthChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args) =>
        ((ResponsiveChoiceSelector)dependencyObject).CompactSelector.MinWidth =
            args.NewValue is double value && double.IsFinite(value) && value >= 0
                ? value
                : 0;

    private static void OnResponsiveConstraintChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args) =>
        ((ResponsiveChoiceSelector)dependencyObject).UpdatePresentationMode();

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        ObserveXamlRoot();
        ObserveResponsiveHost();
        ApplyStaticProperties();
        RebuildOptions();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (observedXamlRoot is not null)
        {
            observedXamlRoot.Changed -= OnXamlRootChanged;
            observedXamlRoot = null;
        }

        if (responsiveHost is not null)
        {
            responsiveHost.SizeChanged -= OnResponsiveHostSizeChanged;
            responsiveHost = null;
        }
    }

    private void ObserveXamlRoot()
    {
        if (ReferenceEquals(observedXamlRoot, XamlRoot))
        {
            return;
        }

        if (observedXamlRoot is not null)
        {
            observedXamlRoot.Changed -= OnXamlRootChanged;
        }

        observedXamlRoot = XamlRoot;
        if (observedXamlRoot is not null)
        {
            observedXamlRoot.Changed += OnXamlRootChanged;
        }
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) =>
        UpdatePresentationMode();

    private void ObserveResponsiveHost()
    {
        FrameworkElement? host = FindResponsiveHost();
        if (ReferenceEquals(responsiveHost, host))
        {
            return;
        }

        if (responsiveHost is not null)
        {
            responsiveHost.SizeChanged -= OnResponsiveHostSizeChanged;
        }

        responsiveHost = host;
        if (responsiveHost is not null)
        {
            responsiveHost.SizeChanged += OnResponsiveHostSizeChanged;
        }
    }

    private FrameworkElement? FindResponsiveHost()
    {
        DependencyObject current = this;
        while (VisualTreeHelper.GetParent(current) is DependencyObject parent)
        {
            if (current is FrameworkElement element
                && parent is Grid grid
                && Grid.GetColumn(element) > 0)
            {
                return grid;
            }

            current = parent;
        }

        return null;
    }

    private void OnResponsiveHostSizeChanged(object sender, SizeChangedEventArgs args) =>
        UpdatePresentationMode();

    private void OnExpandedSelectorSizeChanged(object sender, SizeChangedEventArgs args)
    {
        double width = args.NewSize.Width;
        if (!double.IsFinite(width)
            || width <= 0
            || width <= measuredExpandedWidth + 0.5)
        {
            return;
        }

        measuredExpandedWidth = width;
        UpdatePresentationMode();
    }

    private void ApplyStaticProperties()
    {
        CompactSelector.DisplayMemberPath = DisplayMemberPath;
        CompactSelector.PlaceholderText = PlaceholderText;
        CompactSelector.MinWidth = Math.Max(0, CompactMinWidth);
        AutomationProperties.SetName(ExpandedSelector, AccessibleName);
        AutomationProperties.SetName(CompactSelector, AccessibleName);
    }

    private void RebuildOptions()
    {
        object? selectedItem = SelectedItem;
        measuredExpandedWidth = 0;
        synchronizingSelection = true;
        try
        {
            projectedOptions.Clear();
            ExpandedSelector.Items.Clear();
            CompactSelector.ItemsSource = ItemsSource;

            if (ItemsSource is IEnumerable items)
            {
                foreach (object? item in items)
                {
                    if (item is null)
                    {
                        continue;
                    }

                    projectedOptions.Add(item);
                    ExpandedSelector.Items.Add(
                        new SelectorBarItem
                        {
                            Text = DisplayText(item),
                            Tag = item,
                        });
                }
            }
        }
        finally
        {
            synchronizingSelection = false;
        }

        SynchronizeSelection(selectedItem);
        UpdatePresentationMode();
    }

    private string DisplayText(object option)
    {
        if (string.IsNullOrWhiteSpace(DisplayMemberPath))
        {
            return option.ToString() ?? string.Empty;
        }

        PropertyInfo? property = option.GetType().GetProperty(
            DisplayMemberPath,
            BindingFlags.Instance | BindingFlags.Public);
        return property?.GetValue(option)?.ToString() ?? option.ToString() ?? string.Empty;
    }

    private void SynchronizeSelection(object? selectedItem)
    {
        if (synchronizingSelection)
        {
            return;
        }

        synchronizingSelection = true;
        try
        {
            object? projectedSelection = selectedItem is null
                ? null
                : projectedOptions.FirstOrDefault(option =>
                    OptionsEqual(option, selectedItem));
            CompactSelector.SelectedItem = projectedSelection;
            StableComboBoxSelection.SetAnchor(CompactSelector, selectedItem);
            ExpandedSelector.SelectedItem = ExpandedSelector.Items
                .OfType<SelectorBarItem>()
                .FirstOrDefault(item => OptionsEqual(item.Tag, projectedSelection));
        }
        finally
        {
            synchronizingSelection = false;
        }
    }

    private void OnCompactSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!synchronizingSelection)
        {
            PublishSelection(CompactSelector.SelectedItem);
        }
    }

    private void OnExpandedSelectionChanged(
        SelectorBar sender,
        SelectorBarSelectionChangedEventArgs args)
    {
        if (!synchronizingSelection)
        {
            PublishSelection(sender.SelectedItem?.Tag);
        }
    }

    private void PublishSelection(object? selectedItem)
    {
        if (selectedItem is null)
        {
            // WinUI may report a deferred null while rebuilding ComboBox items.
            // Only the owning binding may authoritatively clear the selection.
            if (SelectedItem is not null)
            {
                SynchronizeSelection(SelectedItem);
            }
            return;
        }
        if (OptionsEqual(selectedItem, SelectedItem))
        {
            // A source refresh may replace an option with an equal instance.
            // Keep that native selection without publishing a user change.
            return;
        }

        SetValue(SelectedItemProperty, selectedItem);
        SelectionChanged?.Invoke(
            this,
            new ResponsiveChoiceSelectionChangedEventArgs(selectedItem));
    }

    private void OnCompactDropDownOpened(object sender, object args) =>
        DropDownOpened?.Invoke(this, args);

    private void OnCompactDropDownClosed(object sender, object args) =>
        DropDownClosed?.Invoke(this, args);

    private void UpdatePresentationMode()
    {
        double windowWidth = XamlRoot?.Size.Width ?? 0;
        double availableWidth = responsiveHost is null
            ? windowWidth
            : responsiveHost.ActualWidth;
        double minimumWindowWidth = Math.Max(0, ExpandedMinWindowWidth);
        double expandedOptionsWidth = measuredExpandedWidth > 0
            ? measuredExpandedWidth
            : EstimateExpandedOptionsWidth();
        double minimumHostWidth = expandedOptionsWidth
            + Math.Max(0, ExpandedWidthAllowance);
        bool hasProjectedSelection = SelectedItem is not null
            && projectedOptions.Any(option => OptionsEqual(option, SelectedItem));
        bool useExpanded = projectedOptions.Count > 0
            && hasProjectedSelection
            && windowWidth >= minimumWindowWidth
            && availableWidth >= minimumHostWidth;
        if (expandedPresentationActive == useExpanded)
        {
            return;
        }

        expandedPresentationActive = useExpanded;
        ExpandedSelector.Visibility = useExpanded
            ? Visibility.Visible
            : Visibility.Collapsed;
        CompactSelector.Visibility = useExpanded
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private double EstimateExpandedOptionsWidth()
    {
        double estimatedOptionsWidth = projectedOptions.Sum(option =>
            Math.Clamp(
                DisplayText(option).Length * EstimatedCharacterWidthDip
                    + EstimatedItemChromeWidthDip,
                MinimumEstimatedItemWidthDip,
                MaximumEstimatedItemWidthDip));
        estimatedOptionsWidth += Math.Max(0, projectedOptions.Count - 1)
            * EstimatedItemSpacingDip;
        return estimatedOptionsWidth;
    }

    private static bool OptionsEqual(object? left, object? right) =>
        ReferenceEquals(left, right) || Equals(left, right);
}
