using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Point = Windows.Foundation.Point;
using Rect = Windows.Foundation.Rect;
using Size = Windows.Foundation.Size;
using ButtonBase = Microsoft.UI.Xaml.Controls.Primitives.ButtonBase;
using Panel = Microsoft.UI.Xaml.Controls.Panel;

namespace OpenBlade.Tray;

public sealed class ContentWrapPanel : Panel
{
    private const double Spacing = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        double availableWidth = double.IsFinite(availableSize.Width)
            ? Math.Max(0, availableSize.Width)
            : double.PositiveInfinity;
        double lineWidth = 0;
        double lineHeight = 0;
        double desiredWidth = 0;
        double desiredHeight = 0;

        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            child.Measure(new Size(availableWidth, double.PositiveInfinity));
            Size desired = child.DesiredSize;
            double childWidth = Math.Min(desired.Width, availableWidth);
            double nextWidth = lineWidth == 0
                ? childWidth
                : lineWidth + Spacing + childWidth;
            if (lineWidth > 0 && nextWidth > availableWidth)
            {
                desiredWidth = Math.Max(desiredWidth, lineWidth);
                desiredHeight += lineHeight + Spacing;
                lineWidth = childWidth;
                lineHeight = desired.Height;
            }
            else
            {
                lineWidth = nextWidth;
                lineHeight = Math.Max(lineHeight, desired.Height);
            }
        }

        desiredWidth = Math.Max(desiredWidth, lineWidth);
        desiredHeight += lineHeight;
        return new Size(desiredWidth, desiredHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double availableWidth = Math.Max(0, finalSize.Width);
        double y = 0;
        double rowWidth = 0;
        double rowHeight = 0;
        var row = new List<(UIElement Element, double Width)>();
        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }
            double width = Math.Min(child.DesiredSize.Width, availableWidth);
            double nextWidth = row.Count == 0 ? width : rowWidth + Spacing + width;
            if (row.Count > 0 && nextWidth > availableWidth)
            {
                ArrangeRow(row, y, rowHeight);
                y += rowHeight + Spacing;
                row.Clear();
                rowWidth = width;
                rowHeight = child.DesiredSize.Height;
            }
            else
            {
                rowWidth = nextWidth;
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }
            row.Add((child, width));
        }
        ArrangeRow(row, y, rowHeight);
        return finalSize;
    }

    private static void ArrangeRow(List<(UIElement Element, double Width)> row, double y, double height)
    {
        double x = 0;
        foreach ((UIElement element, double width) in row)
        {
            element.Arrange(new Rect(x, y, width, height));
            x += width + Spacing;
        }
    }

#if OPENBLADE_DEVELOPER_HARNESS
    internal void VerifyLocalizationSnapshotLayout()
    {
        const double tolerance = 0.5;
        Rect? previous = null;
        var occupied = new List<Rect>();
        foreach (UIElement child in Children.Where(value => value.Visibility == Visibility.Visible))
        {
            Point origin = child.TransformToVisual(this).TransformPoint(new Point());
            var bounds = new Rect(origin, child.RenderSize);
            if (!IsFinitePositive(bounds.Width) || !IsFinitePositive(bounds.Height)
                || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y))
            {
                throw new InvalidOperationException("A wrapped command has invalid rendered bounds.");
            }
            if (bounds.Left < -tolerance || bounds.Top < -tolerance
                || bounds.Right > ActualWidth + tolerance || bounds.Bottom > ActualHeight + tolerance)
            {
                throw new InvalidOperationException("A wrapped command extends outside its host.");
            }
            if (occupied.Any(other => Overlaps(other, bounds, tolerance)))
            {
                throw new InvalidOperationException("Wrapped commands overlap.");
            }
            if (previous is { } prior)
            {
                bool sameRow = bounds.Top < prior.Bottom - tolerance && prior.Top < bounds.Bottom - tolerance;
                if (sameRow ? bounds.Left <= prior.Left + tolerance : bounds.Top < prior.Bottom - tolerance)
                {
                    throw new InvalidOperationException("Wrapped commands are not arranged in source order.");
                }
            }

            if (child is ButtonBase button)
            {
                VerifyButton(button, tolerance);
            }

            occupied.Add(bounds);
            previous = bounds;
        }
    }

    private static void VerifyButton(ButtonBase button, double tolerance)
    {
        if (button.MinHeight + tolerance < 40)
        {
            throw new InvalidOperationException("A wrapped command is smaller than the shared touch target.");
        }

        string accessibleName = AutomationProperties.GetName(button);
        if (string.IsNullOrWhiteSpace(accessibleName))
        {
            accessibleName = button.Content switch
            {
                string text => text,
                TextBlock text => text.Text,
                _ => string.Empty,
            };
        }
        if (string.IsNullOrWhiteSpace(accessibleName))
        {
            throw new InvalidOperationException("A wrapped command has no accessible name.");
        }

        if (button.Content is string)
        {
            TextBlock text = FindDescendantText(button, (string)button.Content)
                ?? throw new InvalidOperationException("A wrapped command did not realize its native text template.");
            if (text.TextWrapping == TextWrapping.NoWrap || text.IsTextTrimmed
                || !IsFinitePositive(text.ActualWidth) || !IsFinitePositive(text.ActualHeight))
            {
                throw new InvalidOperationException($"Wrapped label {button.Content}: wrapping={text.TextWrapping}; trimmed={text.IsTextTrimmed}; text={text.Text}; size={text.ActualWidth}x{text.ActualHeight}; button={button.ActualWidth}x{button.ActualHeight}.");
            }
            Point origin = text.TransformToVisual(button).TransformPoint(new Point());
            if (origin.X < -tolerance || origin.Y < -tolerance
                || origin.X + text.ActualWidth > button.ActualWidth + tolerance
                || origin.Y + text.ActualHeight > button.ActualHeight + tolerance)
            {
                throw new InvalidOperationException("A wrapped command label extends outside its button.");
            }
        }
    }

    private static TextBlock? FindDescendantText(DependencyObject root, string expected)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is TextBlock text && text.Text == expected)
            {
                return text;
            }

            if (FindDescendantText(child, expected) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private static bool IsFinitePositive(double value) => double.IsFinite(value) && value > 0;

    private static bool Overlaps(Rect left, Rect right, double tolerance) =>
        left.Left < right.Right - tolerance && left.Right > right.Left + tolerance
        && left.Top < right.Bottom - tolerance && left.Bottom > right.Top + tolerance;
#endif
}
