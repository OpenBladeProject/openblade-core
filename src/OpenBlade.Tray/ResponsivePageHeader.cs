using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenBlade.Tray;

public enum ResponsivePageHeaderElementRole
{
    None,
    Title,
    ProfileActions,
    Customize,
}

public sealed class ResponsivePageHeader : Grid
{
    public static readonly DependencyProperty RoleProperty =
        DependencyProperty.RegisterAttached(
            "Role",
            typeof(ResponsivePageHeaderElementRole),
            typeof(ResponsivePageHeader),
            new PropertyMetadata(ResponsivePageHeaderElementRole.None));

    private FrameworkElement? titleElement;
    private StackPanel? profileActions;
    private FrameworkElement? customizeAction;
    private PageHeaderProfileActionPlacement placement =
        PageHeaderProfileActionPlacement.Hidden;

    public ResponsivePageHeader()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public static ResponsivePageHeaderElementRole GetRole(DependencyObject element) =>
        (ResponsivePageHeaderElementRole)element.GetValue(RoleProperty);

    public static void SetRole(
        DependencyObject element,
        ResponsivePageHeaderElementRole value) => element.SetValue(RoleProperty, value);

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        titleElement = FindRole(ResponsivePageHeaderElementRole.Title);
        profileActions = FindRole(ResponsivePageHeaderElementRole.ProfileActions) as StackPanel;
        customizeAction = FindRole(ResponsivePageHeaderElementRole.Customize);

        SizeChanged += OnLayoutInputChanged;
        LayoutUpdated += OnLayoutUpdated;
        UpdateProfileActionPlacement();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        SizeChanged -= OnLayoutInputChanged;
        LayoutUpdated -= OnLayoutUpdated;
        titleElement = null;
        profileActions = null;
        customizeAction = null;
    }

    private FrameworkElement? FindRole(ResponsivePageHeaderElementRole role) =>
        Children
            .OfType<FrameworkElement>()
            .SingleOrDefault(element => GetRole(element) == role);

    private void OnLayoutInputChanged(object sender, SizeChangedEventArgs args) =>
        UpdateProfileActionPlacement();

    private void OnLayoutUpdated(object? sender, object args) =>
        UpdateProfileActionPlacement();

    private void UpdateProfileActionPlacement()
    {
        if (titleElement is null
            || profileActions is null
            || customizeAction is null)
        {
            return;
        }

        double profileActionsWidth = MeasureHorizontalProfileActions(profileActions);
        PageHeaderProfileActionPlacement resolved = PageHeaderLayoutPolicy.Resolve(
            ActualWidth,
            MeasureDesiredWidth(titleElement),
            profileActionsWidth,
            MeasureDesiredWidth(customizeAction),
            profileActions.Visibility == Visibility.Visible);
        if (placement == resolved)
        {
            return;
        }

        placement = resolved;
        bool useTitleRow = resolved == PageHeaderProfileActionPlacement.TitleRow;
        bool stackVertically = resolved == PageHeaderProfileActionPlacement.LowerRowVertical;

        SetRow(profileActions, useTitleRow ? 0 : 1);
        SetColumn(profileActions, useTitleRow ? 1 : 0);
        SetColumnSpan(profileActions, useTitleRow ? 1 : 3);
        profileActions.HorizontalAlignment = useTitleRow
            ? Microsoft.UI.Xaml.HorizontalAlignment.Right
            : Microsoft.UI.Xaml.HorizontalAlignment.Left;
        profileActions.Orientation = stackVertically
            ? Microsoft.UI.Xaml.Controls.Orientation.Vertical
            : Microsoft.UI.Xaml.Controls.Orientation.Horizontal;
        profileActions.Margin = useTitleRow
            ? new Thickness(
                0,
                0,
                PageHeaderLayoutPolicy.InterActionGroupGapDip,
                0)
            : new Thickness(0, 12, 0, 0);
    }

    private static double MeasureHorizontalProfileActions(StackPanel actions)
    {
        FrameworkElement[] visibleActions = actions.Children
            .OfType<FrameworkElement>()
            .Where(action => action.Visibility == Visibility.Visible)
            .ToArray();
        if (visibleActions.Length == 0)
        {
            return 0;
        }

        return visibleActions.Sum(MeasureDesiredWidth)
            + Math.Max(0, visibleActions.Length - 1) * actions.Spacing;
    }

    private static double MeasureDesiredWidth(FrameworkElement element) =>
        element.DesiredSize.Width;
}
