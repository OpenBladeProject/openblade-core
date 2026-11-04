using System.Reflection;
using Microsoft.UI.Xaml;
using OpenBlade.Contracts;
using Windows.Foundation;

namespace OpenBlade.Tray;

internal static class WindowXamlBindingLifetime
{
    internal static void Release(FrameworkElement element, object bindings, Action stopTracking)
    {
        // UserControl x:Bind hooks Loading on its native FrameworkElement.
        // StopTracking releases property listeners, but leaves this delegate
        // and its strong dataRoot reference in place across content retirement.
        // Remove the exact generated public handler through the public event.
        try
        {
            MethodInfo? loading = bindings.GetType().GetMethod("Loading",
                BindingFlags.Public | BindingFlags.Instance, [typeof(FrameworkElement), typeof(object)]);
            if (loading is not null)
            {
                element.Loading -= loading.CreateDelegate<TypedEventHandler<FrameworkElement, object>>(bindings);
            }
            else
            {
                DiagnosticTrace.TraceException("OpenBlade.Tray.WindowXamlBindingLifetime", "ResolveLoading",
                    new MissingMethodException(bindings.GetType().FullName, "Loading"));
            }
        }
        catch (Exception exception)
        {
            DiagnosticTrace.TraceException("OpenBlade.Tray.WindowXamlBindingLifetime", "DetachLoading", exception);
        }
        try
        {
            stopTracking();
        }
        catch (Exception exception)
        {
            DiagnosticTrace.TraceException("OpenBlade.Tray.WindowXamlBindingLifetime", "StopTracking", exception);
        }
    }

    internal static void Release(Window window, object bindings, Action stopTracking)
    {
        // Window x:Bind emits this subscription but StopTracking does not remove
        // it (microsoft/microsoft-ui-xaml#9960). Its concrete generated type is
        // unavailable during XAML's first compile pass, so bind the exact public
        // signature at disposal. No private event storage or native refcounts.
        try
        {
            MethodInfo? activated = bindings.GetType().GetMethod("Activated",
                BindingFlags.Public | BindingFlags.Instance, [typeof(object), typeof(WindowActivatedEventArgs)]);
            if (activated is not null)
            {
                window.Activated -= activated.CreateDelegate<TypedEventHandler<object, WindowActivatedEventArgs>>(bindings);
            }
            else
            {
                DiagnosticTrace.TraceException("OpenBlade.Tray.WindowXamlBindingLifetime", "ResolveActivation",
                    new MissingMethodException(bindings.GetType().FullName, "Activated"));
            }
        }
        catch (Exception exception)
        {
            // A closed native object or changed compiler must not skip the
            // remaining timers, subscriptions, observers or window close.
            DiagnosticTrace.TraceException("OpenBlade.Tray.WindowXamlBindingLifetime", "DetachActivation", exception);
        }
        try
        {
            stopTracking();
        }
        catch (Exception exception)
        {
            DiagnosticTrace.TraceException("OpenBlade.Tray.WindowXamlBindingLifetime", "StopTracking", exception);
        }
    }
}
