using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Point = Windows.Foundation.Point;
using Rect = Windows.Foundation.Rect;
using Size = Windows.Foundation.Size;

namespace OpenBlade.Tray;

public enum ContextualToolbarElementRole
{
    None,
    Context,
    Actions,
}

public sealed class ContextualToolbar : Grid
{
    public static readonly DependencyProperty RoleProperty =
        DependencyProperty.RegisterAttached(
            "Role",
            typeof(ContextualToolbarElementRole),
            typeof(ContextualToolbar),
            new PropertyMetadata(ContextualToolbarElementRole.None));

    private static readonly Size NaturalMeasureSize = new(
        double.PositiveInfinity,
        double.PositiveInfinity);

    private FrameworkElement? context;
    private FrameworkElement? actions;
    private bool? stacked;

    public ContextualToolbar()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    }

    public static ContextualToolbarElementRole GetRole(DependencyObject element) =>
        (ContextualToolbarElementRole)element.GetValue(RoleProperty);

    public static void SetRole(
        DependencyObject element,
        ContextualToolbarElementRole value) => element.SetValue(RoleProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        ResolveRoleElements();
        if (context is null || actions is null)
        {
            return base.MeasureOverride(availableSize);
        }

        context.Measure(NaturalMeasureSize);
        actions.Measure(NaturalMeasureSize);
        bool useStackedLayout = double.IsFinite(availableSize.Width)
            && context.DesiredSize.Width + 16 + actions.DesiredSize.Width > availableSize.Width;
        ApplyPlacement(useStackedLayout);
        return base.MeasureOverride(availableSize);
    }

    private void ResolveRoleElements()
    {
        context ??= FindRole(ContextualToolbarElementRole.Context);
        actions ??= FindRole(ContextualToolbarElementRole.Actions);
    }

    private FrameworkElement? FindRole(ContextualToolbarElementRole role)
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

    private void ApplyPlacement(bool useStackedLayout)
    {
        if (stacked == useStackedLayout || context is null || actions is null)
        {
            return;
        }

        stacked = useStackedLayout;
        SetRow(context, 0);
        SetColumn(context, 0);
        SetColumnSpan(context, useStackedLayout ? 2 : 1);
        SetRow(actions, useStackedLayout ? 1 : 0);
        SetColumn(actions, useStackedLayout ? 0 : 1);
        SetColumnSpan(actions, useStackedLayout ? 2 : 1);
        actions.HorizontalAlignment = useStackedLayout
            ? Microsoft.UI.Xaml.HorizontalAlignment.Stretch
            : Microsoft.UI.Xaml.HorizontalAlignment.Right;
        ColumnSpacing = useStackedLayout ? 0 : 16;
        RowSpacing = useStackedLayout ? 8 : 0;
    }

#if OPENBLADE_DEVELOPER_HARNESS
    internal void VerifyLocalizationSnapshotLayout()
    {
        ResolveRoleElements();
        if (context is null || actions is null)
        {
            throw new InvalidOperationException("A contextual toolbar requires context and actions.");
        }

        Rect contextBounds = GetVerifiedBounds(context, "context");
        Rect actionsBounds = GetVerifiedBounds(actions, "actions");
        const double tolerance = 0.5;
        if (Overlaps(contextBounds, actionsBounds, tolerance))
        {
            throw new InvalidOperationException("Contextual toolbar regions overlap.");
        }

        if (actions is ContentWrapPanel commands)
        {
            commands.VerifyLocalizationSnapshotLayout();
        }
    }

    private Rect GetVerifiedBounds(FrameworkElement element, string role)
    {
        Point origin = element.TransformToVisual(this).TransformPoint(new Point());
        var bounds = new Rect(origin, element.RenderSize);
        const double tolerance = 0.5;
        if (!IsFinitePositive(bounds.Width) || !IsFinitePositive(bounds.Height)
            || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y))
        {
            throw new InvalidOperationException($"The contextual toolbar {role} has invalid bounds.");
        }
        if (bounds.Left < -tolerance || bounds.Top < -tolerance
            || bounds.Right > ActualWidth + tolerance || bounds.Bottom > ActualHeight + tolerance)
        {
            throw new InvalidOperationException($"The contextual toolbar {role} extends outside its host.");
        }

        return bounds;
    }

    private static bool IsFinitePositive(double value) => double.IsFinite(value) && value > 0;

    private static bool Overlaps(Rect left, Rect right, double tolerance) =>
        left.Left < right.Right - tolerance && left.Right > right.Left + tolerance
        && left.Top < right.Bottom - tolerance && left.Bottom > right.Top + tolerance;
#endif
}
