using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OpenBlade.Tray;

// The Window owns this root for its entire lifetime. Localization replaces its
// child rather than assigning Window.Content and retiring the native root.
internal sealed class WindowContentHost : Grid
{
    private MicaBackdropHost? backdrop;

    internal FrameworkElement? ActiveView => Children.Count switch
    {
        0 => null,
        1 => (FrameworkElement)Children[0],
        _ => throw new InvalidOperationException("A window content host must have one active view."),
    };

    internal static WindowContentHost GetOrCreate(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (window.Content is WindowContentHost existing)
        {
            return existing;
        }
        if (window.Content is not null)
        {
            throw new InvalidOperationException("The native window has an unrecognized content root.");
        }
        var host = new WindowContentHost();
        window.Content = host;
        return host;
    }

    internal void Attach(FrameworkElement view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (ReferenceEquals(ActiveView, view))
        {
            return;
        }
        if (ActiveView is not null)
        {
            throw new InvalidOperationException("The native window already has an active view.");
        }
        Children.Add(view);
    }

    internal void Detach(FrameworkElement view)
    {
        if (ReferenceEquals(ActiveView, view))
        {
            Children.Clear();
        }
    }

    internal void AttachBackdrop(Window window, FrameworkElement themeSource)
    {
        if (backdrop is null)
        {
            backdrop = new MicaBackdropHost(window, themeSource);
        }
        else
        {
            backdrop.SetThemeSource(themeSource);
        }
    }

    internal void DetachBackdrop(FrameworkElement themeSource) =>
        backdrop?.DetachThemeSource(themeSource);

    internal void DisposeBackdrop()
    {
        MicaBackdropHost? previous = backdrop;
        backdrop = null;
        previous?.Dispose();
    }
}
