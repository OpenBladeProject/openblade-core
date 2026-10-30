using System.Collections.ObjectModel;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using ButtonBase = Microsoft.UI.Xaml.Controls.Primitives.ButtonBase;
using ContentControl = Microsoft.UI.Xaml.Controls.ContentControl;
using Control = Microsoft.UI.Xaml.Controls.Control;
using KeyEventHandler = Microsoft.UI.Xaml.Input.KeyEventHandler;
using Point = Windows.Foundation.Point;
using TextBox = Microsoft.UI.Xaml.Controls.TextBox;

namespace OpenBlade.Tray;

/// <summary>In-app ordering without replacing live controls or entering an editor.</summary>
internal sealed class LiveReorderBehavior : IDisposable
{
    private readonly Dictionary<FrameworkElement, Registration> registrations = [];
    private readonly PointerEventHandler pointerPressed;
    private readonly PointerEventHandler pointerMoved;
    private readonly PointerEventHandler pointerReleased;
    private readonly PointerEventHandler pointerCanceled;
    private readonly KeyEventHandler keyDown;
    private Registration? pressed;
    private Registration? dragging;
    private Pointer? pointer;
    private Point pressPosition;
    private DependencyObject? pressOrigin;
    private FrameworkElement? indicatorTarget;
    private bool indicatorAfter;
    private FrameworkElement? layoutHost;
    private readonly List<DragRow> layoutRows = [];
    private readonly List<FrameworkElement> members = [];
    private readonly Dictionary<FrameworkElement, double> proposedTops = [];
    private Point grabPosition;
    private int originalZIndex;
    private bool animateReflow;
    private UIElement? keyRoot;
    private double originalOpacity;
    private bool transferringCapture;
    private bool disposed;
    private FrameworkElement? captureOwner;

    public LiveReorderBehavior()
    {
        pointerPressed = OnPointerPressed;
        pointerMoved = OnPointerMoved;
        pointerReleased = OnPointerReleased;
        pointerCanceled = OnPointerCanceled;
        keyDown = OnKeyDown;
    }

    public void Register<T>(FrameworkElement element, ObservableCollection<T> items, T item,
        Func<DependencyObject, bool>? canStart = null)
        where T : class
    {
        // A queued MainWindow refresh can arrive after disposal. It must not reattach input.
        if (disposed)
        {
            return;
        }
        var registration = new Registration(element, items, item!, canStart, (source, after) =>
        {
            int sourceIndex = items.IndexOf((T)source);
            int targetIndex = items.IndexOf(item);
            if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
            {
                return;
            }
            int insertionIndex = targetIndex + (after ? 1 : 0);
            if (sourceIndex < insertionIndex)
            {
                insertionIndex--;
            }
            if (sourceIndex != insertionIndex)
            {
                // Existing observers refresh live controls and save the canonical order,
                // including hidden siblings, through the normal preference queue.
                items.Move(sourceIndex, insertionIndex);
            }
        });
        if (registrations.ContainsKey(element))
        {
            registrations[element] = registration;
            return;
        }
        registrations.Add(element, registration);
        // NavigationView consumes presses. Observe handled events too, and capture only
        // once a passive-surface drag crosses the system threshold. CanDrag's automatic
        // recognizer misses these menu gestures and can steal nested control capture.
        element.AddHandler(UIElement.PointerPressedEvent, pointerPressed, true);
        element.AddHandler(UIElement.PointerMovedEvent, pointerMoved, true);
        element.AddHandler(UIElement.PointerExitedEvent, pointerMoved, true);
        element.AddHandler(UIElement.PointerReleasedEvent, pointerReleased, true);
        element.AddHandler(UIElement.PointerCanceledEvent, pointerCanceled, true);
        element.AddHandler(UIElement.PointerCaptureLostEvent, pointerCanceled, true);
        element.Holding += OnHolding;
        element.Unloaded += OnUnloaded;
    }

    internal bool IsRegistered(FrameworkElement element) => registrations.ContainsKey(element);

    internal static bool CanStartFrom(FrameworkElement element, DependencyObject? origin)
    {
        for (DependencyObject? current = origin; current is not null;
             current = VisualTreeHelper.GetParent(current) ?? (current as FrameworkElement)?.Parent)
        {
            if (ReferenceEquals(current, element))
            {
                return true;
            }
            if (current is TextBlock { IsTextSelectionEnabled: true }
                or RichTextBlock { IsTextSelectionEnabled: true } or TelemetryChart
                || current is Control and not ContentControl && element is not NavigationViewItem
                || current is ButtonBase or Selector or Expander or ToggleSwitch
                    or TextBox or PasswordBox or RichEditBox)
            {
                return false;
            }
        }
        return false;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (disposed || dragging is not null)
        {
            return;
        }
        var element = (FrameworkElement)sender;
        // The innermost registered card owns the gesture. A bubbling press must not
        // replace it with its enclosing section (or clear an interactive child press).
        if (args.OriginalSource is DependencyObject origin)
        {
            for (DependencyObject? current = origin; current is not null && current != element;
                 current = VisualTreeHelper.GetParent(current))
            {
                if (current is FrameworkElement child && registrations.ContainsKey(child))
                {
                    return;
                }
            }
        }
        var point = args.GetCurrentPoint(element);
        pressed = point.IsInContact
            && (args.Pointer.PointerDeviceType == PointerDeviceType.Touch || point.Properties.IsLeftButtonPressed)
            && CanStartFrom(element, args.OriginalSource as DependencyObject)
            && (registrations[element].CanStart?.Invoke((DependencyObject)args.OriginalSource) ?? true)
                ? registrations[element] : null;
        pointer = pressed is null ? null : args.Pointer;
        pressOrigin = pressed is null ? null : args.OriginalSource as DependencyObject;
        pressPosition = point.Position;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!ReferenceEquals(pressed?.Element, sender) || pointer?.PointerId != args.Pointer.PointerId)
        {
            return;
        }
        var element = (FrameworkElement)sender;
        var point = args.GetCurrentPoint(element);
        if (!point.IsInContact
            || args.Pointer.PointerDeviceType != PointerDeviceType.Touch && !point.Properties.IsLeftButtonPressed)
        {
            EndDrag();
            return;
        }
        if (dragging is null)
        {
            // Touch retains normal scrolling until an explicit hold starts reordering.
            if (args.Pointer.PointerDeviceType == PointerDeviceType.Touch)
            {
                return;
            }
            double scale = element.XamlRoot.RasterizationScale;
            System.Drawing.Size threshold = System.Windows.Forms.SystemInformation.DragSize;
            if (Math.Abs(point.Position.X - pressPosition.X) < threshold.Width / scale
                && Math.Abs(point.Position.Y - pressPosition.Y) < threshold.Height / scale)
            {
                return;
            }
            BeginPointerDrag(element, args.Pointer);
        }
        if (dragging is not null)
        {
            UpdateTarget(args.GetCurrentPoint(element.XamlRoot.Content).Position);
            args.Handled = true;
        }
    }

    private void OnHolding(object sender, HoldingRoutedEventArgs args)
    {
        if (args.HoldingState == HoldingState.Started && ReferenceEquals(pressed?.Element, sender)
            && pointer?.PointerDeviceType == PointerDeviceType.Touch)
        {
            BeginPointerDrag((FrameworkElement)sender, pointer);
            args.Handled = dragging is not null;
        }
    }

    private void BeginPointerDrag(FrameworkElement element, Pointer activePointer)
    {
        if (disposed || !IsVisible(element))
        {
            EndDrag();
            return;
        }
        bool started = false;
        transferringCapture = true;
        try
        {
            if (activePointer.PointerDeviceType == PointerDeviceType.Touch)
            {
                // Commit a held touch to reordering instead of the parent's system pan.
                // False also means no manipulation was active; it is not a failure gate.
                element.CancelDirectManipulations();
            }
            // NavigationView's presenter captures on press. Hand that specific descendant
            // capture to the registered item after a reorder gesture has been established.
            ReleaseDescendantCapture(element, activePointer);
            if (!element.CapturePointer(activePointer))
            {
                return;
            }
            captureOwner = element;
            pointer = activePointer;
            if (!TryBeginDrag(element, pressOrigin ?? element))
            {
                return;
            }
            pressed = dragging;
            keyRoot = element.XamlRoot.Content;
            keyRoot.AddHandler(UIElement.KeyDownEvent, keyDown, true);
            started = true;
        }
        finally
        {
            transferringCapture = false;
            if (!started)
            {
                // Also release a capture acquired before admission failed. Never retry a
                // failed initiation on every subsequent move of the same contact.
                EndDrag();
            }
        }
    }


    private static void ReleaseDescendantCapture(DependencyObject parent, Pointer activePointer)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is UIElement element
                && element.PointerCaptures?.Any(capture => capture.PointerId == activePointer.PointerId) == true)
            {
                element.ReleasePointerCapture(activePointer);
            }
            ReleaseDescendantCapture(child, activePointer);
        }
    }

    internal bool TryBeginDrag(FrameworkElement element, DependencyObject origin)
    {
        if (disposed || dragging is not null || !IsVisible(element) || !CanStartFrom(element, origin)
            || !registrations.TryGetValue(element, out Registration? source)
            || !(source.CanStart?.Invoke(origin) ?? true)
            || element.XamlRoot.Content is not Microsoft.UI.Xaml.Controls.Panel)
        {
            return false;
        }
        dragging = source;
        originalOpacity = element.Opacity;
        originalZIndex = Canvas.GetZIndex(element);
        try
        {
            CaptureLayout(element);
            element.Opacity = originalOpacity * 0.5;
            Canvas.SetZIndex(element, 1000);
            return dragging is not null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            EndDrag();
            System.Diagnostics.Trace.TraceWarning("Could not start live reordering: {0}", exception);
            return false;
        }
    }

    private bool CanMoveTo(FrameworkElement target) =>
        dragging is not null && IsVisible(dragging.Element) && IsVisible(target)
        && target != dragging.Element
        && registrations.TryGetValue(target, out Registration? destination)
        && ReferenceEquals(destination.Group, dragging.Group)
        && ReferenceEquals(target.XamlRoot, dragging.Element.XamlRoot)
        && registrations.TryGetValue(dragging.Element, out Registration? source)
        && ReferenceEquals(source.Item, dragging.Item);

    private static bool IsVisible(FrameworkElement element)
    {
        if (!element.IsLoaded || element.ActualHeight <= 0 || element.ActualWidth <= 0)
        {
            return false;
        }
        UIElement? root = element.XamlRoot?.Content;
        for (DependencyObject? current = element; current is not null;)
        {
            if (current is UIElement { Visibility: Visibility.Collapsed })
            {
                return false;
            }
            if (ReferenceEquals(current, root))
            {
                return true;
            }
            DependencyObject? parent = VisualTreeHelper.GetParent(current);
            // Theme removal transitions can retain a visual parent after the item has
            // left its host. Such outgoing visuals must no longer admit a drag or drop.
            if (current is UIElement child
                && (parent is Microsoft.UI.Xaml.Controls.Panel panel && !panel.Children.Contains(child)
                    || parent is ItemsRepeater repeater && repeater.GetElementIndex(child) < 0))
            {
                return false;
            }
            current = parent;
        }
        return false;
    }

    // Pointer coordinates stay relative to the root: the source itself now moves.
    internal void UpdateTarget(Point rootPosition)
    {
        if (disposed || dragging is null || layoutHost is null || !IsLayoutCurrent())
        {
            EndDrag();
            return;
        }
        UIElement root = dragging.Element.XamlRoot.Content;
        Point position = root.TransformToVisual(layoutHost).TransformPoint(rootPosition);
        DragRow source = layoutRows.Single(row => row.Element == dragging.Element);
        source.Offset.X = position.X - grabPosition.X;
        source.Offset.Y = position.Y - grabPosition.Y;
        if (root is FrameworkElement bounds
            && (rootPosition.X < 0 || rootPosition.Y < 0
                || rootPosition.X >= bounds.ActualWidth || rootPosition.Y >= bounds.ActualHeight)
            || position.X < 0 || position.Y < 0
            || position.X >= layoutHost.ActualWidth || position.Y >= layoutHost.ActualHeight)
        {
            ResetProposal();
            return;
        }
        // Ignore the lifted source, which covers the cursor. Everything underneath still
        // participates in normal hit testing, including scroll clips and blocking overlays.
        DependencyObject? hit = VisualTreeHelper.FindElementsInHostCoordinates(rootPosition, root)
            .FirstOrDefault(candidate => !IsDescendantOf(candidate, source.Element));
        if (hit is null || !IsDescendantOf(hit, layoutHost))
        {
            // A null-background host has no ink in the source's vacant slot. Spatial
            // hits include it, in z-order: admit it only before the first normal hit,
            // so a real overlay above the host continues to block dropping.
            foreach (UIElement spatial in VisualTreeHelper.FindElementsInHostCoordinates(rootPosition, root, true))
            {
                if (ReferenceEquals(spatial, hit))
                {
                    break;
                }
                if (ReferenceEquals(spatial, layoutHost))
                {
                    hit = layoutHost;
                    break;
                }
            }
        }
        for (; hit is not null; hit = VisualTreeHelper.GetParent(hit))
        {
            if (!ReferenceEquals(hit, layoutHost))
            {
                continue;
            }
            // Use proposed positions, never the animated/transformed hit bounds. Keeping
            // the source's vacant slot stable prevents a stationary cursor from oscillating.
            double sourceTop = proposedTops[source.Element];
            if (position.Y >= sourceTop && position.Y <= sourceTop + source.Height)
            {
                return;
            }
            FrameworkElement[] others = members.Where(member => member != source.Element)
                .OrderBy(member => proposedTops[member]).ToArray();
            FrameworkElement? target = others.FirstOrDefault(member =>
                position.Y < proposedTops[member] + member.ActualHeight / 2);
            if (target is not null)
            {
                ShowIndicator(target, after: false);
            }
            else if (others.Length > 0)
            {
                ShowIndicator(others[^1], after: true);
            }
            return;
        }
        ResetProposal();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!ReferenceEquals(pressed?.Element, sender) || pointer?.PointerId != args.Pointer.PointerId)
        {
            return;
        }
        if (dragging is not null)
        {
            UpdateTarget(args.GetCurrentPoint(((FrameworkElement)sender).XamlRoot.Content).Position);
            FrameworkElement? target = indicatorTarget;
            if (target is not null)
            {
                TryMove(target, indicatorAfter);
            }
            args.Handled = true;
        }
        EndDrag();
    }

    internal bool TryMove(FrameworkElement target, bool after)
    {
        if (!CanMoveTo(target) || !IsLayoutCurrent())
        {
            EndDrag();
            return false;
        }
        Registration source = dragging!;
        Registration destination = registrations[target];
        EndDrag();
        destination.MoveHere(source.Item, after);
        return true;
    }

    private void OnPointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (!transferringCapture && ReferenceEquals(pressed?.Element, sender)
            && pointer?.PointerId == args.Pointer.PointerId)
        {
            EndDrag();
        }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == Windows.System.VirtualKey.Escape && dragging is not null)
        {
            EndDrag();
            args.Handled = true;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (ReferenceEquals(sender, pressed?.Element) || ReferenceEquals(sender, dragging?.Element)
            || layoutRows.Any(row => ReferenceEquals(row.Element, sender)))
        {
            EndDrag();
        }
    }

    internal void ShowIndicator(FrameworkElement target, bool after)
    {
        if (disposed || !CanMoveTo(target) || !members.Contains(target))
        {
            return;
        }
        indicatorTarget = target;
        indicatorAfter = after;
        var order = members.OrderBy(member => proposedTops[member]).ToList();
        order.Remove(dragging!.Element);
        order.Insert(order.IndexOf(target) + (after ? 1 : 0), dragging.Element);
        ApplyProposal(order);
    }

    private void ClearIndicator()
    {
        indicatorTarget = null;
    }

    internal FrameworkElement? DragPreview => dragging?.Element;

    private static bool IsDescendantOf(DependencyObject candidate, DependencyObject ancestor)
    {
        for (DependencyObject? current = candidate; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }
        return false;
    }

    private void CaptureLayout(FrameworkElement element)
    {
        animateReflow = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        layoutHost = VisualTreeHelper.GetParent(element) as FrameworkElement
            ?? throw new InvalidOperationException("The reorder source has no layout host.");
        members.AddRange(registrations.Values.Where(registration =>
                ReferenceEquals(registration.Group, dragging!.Group) && IsVisible(registration.Element)
                && ReferenceEquals(VisualTreeHelper.GetParent(registration.Element), layoutHost))
            .Select(registration => registration.Element));
        // Keep non-draggable notices in their existing child slots too. Their position can
        // change when unequal-height cards pass them, just as it will after the real drop.
        IEnumerable<FrameworkElement> peers = layoutHost is Microsoft.UI.Xaml.Controls.Panel panel
            ? panel.Children.OfType<FrameworkElement>().Where(IsVisible) : members;
        foreach (FrameworkElement peer in peers.OrderBy(peer => peer.TransformToVisual(layoutHost).TransformPoint(new Point()).Y))
        {
            Point origin = peer.TransformToVisual(layoutHost).TransformPoint(new Point());
            var row = new DragRow(peer, origin.Y, peer.ActualHeight, LayoutInformation.GetLayoutSlot(peer));
            layoutRows.Add(row);
            proposedTops.Add(peer, origin.Y);
        }
        // Direct smoke gestures start at the source's top-left; real input keeps the exact
        // point originally grabbed, even after the source or its neighbors move.
        Point grip = captureOwner == element ? pressPosition : new Point();
        grabPosition = element.TransformToVisual(layoutHost).TransformPoint(grip);
    }

    private bool IsLayoutCurrent() => layoutRows.Count > 0 && layoutRows.All(row =>
        IsVisible(row.Element) && ReferenceEquals(VisualTreeHelper.GetParent(row.Element), layoutHost)
        && LayoutInformation.GetLayoutSlot(row.Element) == row.LayoutSlot
        && row.Element.ActualHeight == row.Height)
        && (layoutHost is not Microsoft.UI.Xaml.Controls.Panel panel
            || panel.Children.OfType<FrameworkElement>().Count(IsVisible) == layoutRows.Count)
        && registrations.Values.Count(registration => ReferenceEquals(registration.Group, dragging!.Group)
            && ReferenceEquals(VisualTreeHelper.GetParent(registration.Element), layoutHost)
            && IsVisible(registration.Element)) == members.Count
        && members.All(member => registrations.TryGetValue(member, out Registration? registration)
            && ReferenceEquals(registration.Group, dragging!.Group))
        && registrations.TryGetValue(dragging!.Element, out Registration? source)
        && ReferenceEquals(source.Item, dragging.Item);

    private void ApplyProposal(IReadOnlyList<FrameworkElement> order)
    {
        int memberIndex = 0;
        double top = layoutRows[0].Top;
        for (int index = 0; index < layoutRows.Count; index++)
        {
            DragRow slot = layoutRows[index];
            FrameworkElement element = members.Contains(slot.Element) ? order[memberIndex++] : slot.Element;
            DragRow row = layoutRows.Single(row => row.Element == element);
            if (index > 0)
            {
                top += slot.Top - (layoutRows[index - 1].Top + layoutRows[index - 1].Height);
            }
            proposedTops[element] = top;
            if (element != dragging!.Element)
            {
                row.MoveTo(top - row.Top, animateReflow);
            }
            top += row.Height;
        }
    }

    private void ResetProposal()
    {
        ClearIndicator();
        ApplyProposal(layoutRows.Where(row => members.Contains(row.Element)).Select(row => row.Element).ToArray());
    }

    internal void EndDrag()
    {
        ClearIndicator();
        Registration? source = dragging;
        FrameworkElement? owner = captureOwner;
        Pointer? captured = pointer;
        dragging = null;
        pressed = null;
        pointer = null;
        pressOrigin = null;
        captureOwner = null;
        keyRoot?.RemoveHandler(UIElement.KeyDownEvent, keyDown);
        keyRoot = null;
        if (source is not null)
        {
            source.Element.Opacity = originalOpacity;
            Canvas.SetZIndex(source.Element, originalZIndex);
        }
        foreach (DragRow row in layoutRows)
        {
            row.Restore();
        }
        layoutRows.Clear();
        members.Clear();
        proposedTops.Clear();
        layoutHost = null;
        if (owner is not null && captured is not null)
        {
            owner.ReleasePointerCapture(captured);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        EndDrag();
        foreach (FrameworkElement element in registrations.Keys)
        {
            element.RemoveHandler(UIElement.PointerPressedEvent, pointerPressed);
            element.RemoveHandler(UIElement.PointerMovedEvent, pointerMoved);
            element.RemoveHandler(UIElement.PointerExitedEvent, pointerMoved);
            element.RemoveHandler(UIElement.PointerReleasedEvent, pointerReleased);
            element.RemoveHandler(UIElement.PointerCanceledEvent, pointerCanceled);
            element.RemoveHandler(UIElement.PointerCaptureLostEvent, pointerCanceled);
            element.Holding -= OnHolding;
            element.Unloaded -= OnUnloaded;
        }
        registrations.Clear();
    }

    private sealed record Registration(
        FrameworkElement Element,
        object Group,
        object Item,
        Func<DependencyObject, bool>? CanStart,
        Action<object, bool> MoveHere);

    private sealed class DragRow
    {
        public FrameworkElement Element { get; }
        public double Top { get; }
        public double Height { get; }
        public Rect LayoutSlot { get; }
        public TranslateTransform Offset { get; } = new();
        private readonly object localTransform;
        private readonly Microsoft.UI.Xaml.Data.Binding? transformBinding;
        private Storyboard? animation;
        private double destination;

        public DragRow(FrameworkElement element, double top, double height, Rect layoutSlot)
        {
            Element = element;
            Top = top;
            Height = height;
            LayoutSlot = layoutSlot;
            localTransform = element.ReadLocalValue(UIElement.RenderTransformProperty);
            transformBinding = element.GetBindingExpression(UIElement.RenderTransformProperty)?.ParentBinding;
            // NavigationView already owns a handout composition visual. RenderTransform
            // composes with it; the newer Translation facade throws on those native items.
            var transform = new TransformGroup();
            if (element.RenderTransform is { } original)
            {
                transform.Children.Add(original);
            }
            transform.Children.Add(Offset);
            element.RenderTransform = transform;
        }

        public void MoveTo(double y, bool animate)
        {
            if (destination == y)
            {
                return;
            }
            double from = Offset.Y;
            animation?.Stop();
            animation = null;
            destination = y;
            Offset.Y = y;
            if (animate)
            {
                var movement = new DoubleAnimation
                {
                    From = from,
                    To = y,
                    Duration = new Duration(TimeSpan.FromMilliseconds(120)),
                };
                Storyboard.SetTarget(movement, Offset);
                Storyboard.SetTargetProperty(movement, nameof(TranslateTransform.Y));
                animation = new Storyboard();
                animation.Children.Add(movement);
                animation.Begin();
            }
        }

        public void Restore()
        {
            animation?.Stop();
            if (transformBinding is not null)
            {
                Element.SetBinding(UIElement.RenderTransformProperty, transformBinding);
            }
            else if (localTransform == DependencyProperty.UnsetValue)
            {
                Element.ClearValue(UIElement.RenderTransformProperty);
            }
            else
            {
                Element.SetValue(UIElement.RenderTransformProperty, localTransform);
            }
        }
    }
}
