using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinSize = Windows.Foundation.Size;

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

    private static readonly WinSize NaturalMeasureSize = new(
        double.PositiveInfinity,
        double.PositiveInfinity);

    private FrameworkElement? titleElement;
    private StackPanel? profileActions;
    private FrameworkElement? customizeAction;
    private PageHeaderProfileActionPlacement? placement;
    private double constrainedProfileActionsWidth = double.PositiveInfinity;

    public static ResponsivePageHeaderElementRole GetRole(DependencyObject element) =>
        (ResponsivePageHeaderElementRole)element.GetValue(RoleProperty);

    public static void SetRole(
        DependencyObject element,
        ResponsivePageHeaderElementRole value) => element.SetValue(RoleProperty, value);

    protected override WinSize MeasureOverride(WinSize availableSize)
    {
        ResolveRoleElements();
        if (titleElement is null
            || profileActions is null
            || customizeAction is null)
        {
            return base.MeasureOverride(availableSize);
        }

        titleElement.Measure(NaturalMeasureSize);
        customizeAction.Measure(NaturalMeasureSize);
        double profileActionsWidth = MeasureNaturalProfileActions(
            profileActions,
            out bool hasVisibleProfileAction);
        PageHeaderProfileActionPlacement resolved = PageHeaderLayoutPolicy.Resolve(
            availableSize.Width,
            titleElement.DesiredSize.Width,
            profileActionsWidth,
            customizeAction.DesiredSize.Width,
            profileActions.Visibility == Visibility.Visible && hasVisibleProfileAction);

        ApplyProfileActionPlacement(resolved, availableSize.Width);
        return base.MeasureOverride(availableSize);
    }

    private void ResolveRoleElements()
    {
        titleElement ??= FindRole(ResponsivePageHeaderElementRole.Title);
        profileActions ??=
            FindRole(ResponsivePageHeaderElementRole.ProfileActions) as StackPanel;
        customizeAction ??= FindRole(ResponsivePageHeaderElementRole.Customize);
    }

    private FrameworkElement? FindRole(ResponsivePageHeaderElementRole role)
    {
        foreach (UIElement child in Children)
        {
            if (child is FrameworkElement element && GetRole(element) == role)
            {
                return element;
            }
        }

        return null;
    }

    private void ApplyProfileActionPlacement(
        PageHeaderProfileActionPlacement resolved,
        double availableWidth)
    {
        if (profileActions is null)
        {
            return;
        }

        bool useTitleRow = resolved is PageHeaderProfileActionPlacement.Hidden
            or PageHeaderProfileActionPlacement.TitleRow;
        bool stackVertically = resolved ==
            PageHeaderProfileActionPlacement.LowerRowVertical;
        double widthLimit = stackVertically && IsFinitePositive(availableWidth)
            ? availableWidth
            : double.PositiveInfinity;

        if (placement != resolved)
        {
            placement = resolved;
            SetRow(profileActions, useTitleRow ? 0 : 1);
            SetColumn(profileActions, useTitleRow ? 1 : 0);
            SetColumnSpan(profileActions, useTitleRow ? 1 : 3);
            profileActions.HorizontalAlignment = useTitleRow
                ? Microsoft.UI.Xaml.HorizontalAlignment.Right
                : Microsoft.UI.Xaml.HorizontalAlignment.Stretch;
            profileActions.Orientation = stackVertically
                ? Microsoft.UI.Xaml.Controls.Orientation.Vertical
                : Microsoft.UI.Xaml.Controls.Orientation.Horizontal;
            profileActions.Margin = resolved switch
            {
                PageHeaderProfileActionPlacement.Hidden => new Thickness(0),
                PageHeaderProfileActionPlacement.TitleRow => new Thickness(
                    0,
                    0,
                    PageHeaderLayoutPolicy.InterActionGroupGapDip,
                    0),
                _ => new Thickness(0, 12, 0, 0),
            };
        }

        if (!AreClose(constrainedProfileActionsWidth, widthLimit))
        {
            constrainedProfileActionsWidth = widthLimit;
            profileActions.MaxWidth = widthLimit;
        }
    }

    private static double MeasureNaturalProfileActions(
        StackPanel actions,
        out bool hasVisibleAction)
    {
        double width = 0;
        int visibleCount = 0;
        foreach (UIElement child in actions.Children)
        {
            if (child is not FrameworkElement action
                || action.Visibility != Visibility.Visible)
            {
                continue;
            }

            action.Measure(NaturalMeasureSize);
            width += action.DesiredSize.Width;
            visibleCount++;
        }

        hasVisibleAction = visibleCount > 0;
        return width + Math.Max(0, visibleCount - 1) * actions.Spacing;
    }

    private static bool IsFinitePositive(double value) =>
        double.IsFinite(value) && value > 0;

    private static bool AreClose(double left, double right) =>
        left == right || Math.Abs(left - right) < 0.5;
}
