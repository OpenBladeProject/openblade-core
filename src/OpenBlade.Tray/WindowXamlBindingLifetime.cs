using System.Reflection;
using Microsoft.UI.Xaml;
using OpenBlade.Contracts;
using Windows.Foundation;

namespace OpenBlade.Tray;

internal static class WindowXamlBindingLifetime
{
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
