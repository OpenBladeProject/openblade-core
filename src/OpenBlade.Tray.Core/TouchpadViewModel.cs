using System.ComponentModel;
using System.Runtime.CompilerServices;
using OpenBlade.Contracts;

namespace OpenBlade.Tray;

public sealed class TouchpadViewModel : INotifyPropertyChanged
{
    private readonly ITouchpadController controller;
    private bool? enabled;
    private bool lastConfirmedEnabled;
    private bool busy;
    private bool needsConfirmation;
    private string? failure;

    public TouchpadViewModel(ITouchpadController controller)
    {
        this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsOn => enabled ?? lastConfirmedEnabled;
    public bool CanEdit => enabled.HasValue && !busy && !needsConfirmation;
    public string Description => failure ?? (enabled.HasValue
        ? TrayText.Get("Turn the touchpad on or off.")
        : TrayText.Get("Touchpad state unavailable."));

    public void Refresh()
    {
        if (busy)
        {
            return;
        }
        enabled = ReadEnabled();
        if (enabled.HasValue)
        {
            needsConfirmation = false;
            failure = null;
        }
        NotifyState();
    }

    public async Task SetEnabledAsync(bool requested)
    {
        if (busy || needsConfirmation)
        {
            return;
        }
        busy = true;
        failure = null;
        NotifyState();
        try
        {
            // Never toggle from cached presentation: Windows Settings and Fn keys
            // can change this setting independently of the tray.
            enabled = ReadEnabled();
            if (!enabled.HasValue || enabled.Value == requested)
            {
                return;
            }
            controller.Toggle();
            // A toggle is non-idempotent. Submit once and only query afterward.
            for (int attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(50);
                enabled = ReadEnabled();
                if (enabled == requested)
                {
                    return;
                }
            }
            needsConfirmation = true;
            failure = TrayText.Get("The touchpad change could not be confirmed. Check Windows Settings.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DiagnosticTrace.TraceException(
                "OpenBlade.Tray.TouchpadViewModel",
                "SetEnabled",
                exception);
            enabled = ReadEnabled();
            needsConfirmation = true;
            failure = TrayText.Get("The touchpad change could not be confirmed. Check Windows Settings.");
        }
        finally
        {
            busy = false;
            NotifyState();
        }
    }

    private bool? ReadEnabled()
    {
        try
        {
            return controller.ReadEnabled();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            DiagnosticTrace.TraceException(
                "OpenBlade.Tray.TouchpadViewModel",
                "ReadEnabled",
                exception);
            return null;
        }
    }

    private void NotifyState()
    {
        if (enabled.HasValue)
        {
            lastConfirmedEnabled = enabled.Value;
        }
        Notify(nameof(IsOn));
        Notify(nameof(CanEdit));
        Notify(nameof(Description));
    }

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
