using System.Collections.ObjectModel;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
    private Canvas? previewHost;
    private Microsoft.UI.Xaml.Controls.Panel? previewRoot;
    private SpriteVisual? preview;
    private CompositionVisualSurface? previewSurface;
    private CompositionSurfaceBrush? previewBrush;
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
            UpdateTarget(point.Position);
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
            || element.XamlRoot.Content is not Microsoft.UI.Xaml.Controls.Panel root)
        {
            return false;
        }
        dragging = source;
        originalOpacity = element.Opacity;
        try
        {
            CreatePreview(element, root);
            return dragging is not null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            EndDrag();
            System.Diagnostics.Trace.TraceWarning("Could not create the drag preview: {0}", exception);
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

    internal void UpdateTarget(Point position)
    {
        if (disposed || dragging is null || !IsVisible(dragging.Element))
        {
            EndDrag();
            return;
        }
        UIElement root = dragging.Element.XamlRoot.Content;
        Point rootPosition = dragging.Element.TransformToVisual(root).TransformPoint(position);
        if (preview is not null && previewHost is not null)
        {
            Point location = dragging.Element.TransformToVisual(previewHost)
                .TransformPoint(new Point(position.X - pressPosition.X, position.Y - pressPosition.Y));
            preview.Offset = new Vector3((float)location.X, (float)location.Y, 0);
        }
        if (root is FrameworkElement bounds
            && (rootPosition.X < 0 || rootPosition.Y < 0
                || rootPosition.X >= bounds.ActualWidth || rootPosition.Y >= bounds.ActualHeight))
        {
            ClearIndicator();
            return;
        }
        // Hit testing honors scroll clipping and overlays; off-screen cards are not targets.
        DependencyObject? hit = VisualTreeHelper.FindElementsInHostCoordinates(rootPosition, root).FirstOrDefault();
        for (; hit is not null; hit = VisualTreeHelper.GetParent(hit))
        {
            if (hit is not FrameworkElement candidate || !CanMoveTo(candidate))
            {
                continue;
            }
            Point local = dragging.Element.TransformToVisual(candidate).TransformPoint(position);
            ShowIndicator(candidate, local.Y >= candidate.ActualHeight / 2);
            return;
        }
        ClearIndicator();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (!ReferenceEquals(pressed?.Element, sender) || pointer?.PointerId != args.Pointer.PointerId)
        {
            return;
        }
        if (dragging is not null)
        {
            var source = (FrameworkElement)sender;
            UpdateTarget(args.GetCurrentPoint(source).Position);
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
        if (!CanMoveTo(target))
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
            || ReferenceEquals(sender, indicatorTarget))
        {
            EndDrag();
        }
    }

    internal void ShowIndicator(FrameworkElement target, bool after)
    {
        if (disposed)
        {
            return;
        }
        indicatorTarget = target;
        indicatorAfter = after;
    }

    private void ClearIndicator()
    {
        indicatorTarget = null;
    }

    internal SpriteVisual? DragPreview => preview;

    private void CreatePreview(FrameworkElement element, Microsoft.UI.Xaml.Controls.Panel root)
    {
        // Mirror the actual live subtree without reparenting controls or copying their
        // bindings. The hit-test-free root overlay stays outside scroll clipping.
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        previewSurface = compositor.CreateVisualSurface();
        previewSurface.SourceVisual = visual;
        previewSurface.SourceSize = new Vector2((float)element.ActualWidth, (float)element.ActualHeight);
        previewBrush = compositor.CreateSurfaceBrush(previewSurface);
        preview = compositor.CreateSpriteVisual();
        preview.Brush = previewBrush;
        preview.Size = previewSurface.SourceSize;
        preview.Opacity = 0.5f;
        previewHost = new Canvas { IsHitTestVisible = false };
        if (root is Grid grid)
        {
            Grid.SetRowSpan(previewHost, Math.Max(1, grid.RowDefinitions.Count));
            Grid.SetColumnSpan(previewHost, Math.Max(1, grid.ColumnDefinitions.Count));
        }
        // WinUI bounds Canvas.ZIndex; Int32.MaxValue is rejected by native XAML.
        Canvas.SetZIndex(previewHost, 1000);
        previewRoot = root;
        root.Children.Add(previewHost);
        root.UpdateLayout();
        if (dragging is null || previewHost is null || preview is null)
        {
            return;
        }
        Point location = element.TransformToVisual(previewHost).TransformPoint(new Point());
        preview.Offset = new Vector3((float)location.X, (float)location.Y, 0);
        ElementCompositionPreview.SetElementChildVisual(previewHost, preview);
    }

    private void ClearPreview()
    {
        if (previewHost is not null)
        {
            ElementCompositionPreview.SetElementChildVisual(previewHost, null);
            previewRoot?.Children.Remove(previewHost);
        }
        preview?.Dispose();
        previewBrush?.Dispose();
        previewSurface?.Dispose();
        preview = null;
        previewBrush = null;
        previewSurface = null;
        previewHost = null;
        previewRoot = null;
    }

    internal void EndDrag()
    {
        ClearIndicator();
        ClearPreview();
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
        }
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
}
