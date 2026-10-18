using OpenBlade.Contracts;
using OpenBlade.Display;
using OpenBlade.Session;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent;

public enum Blade18FnShortcut
{
    Performance,
    RefreshRate,
    Touchpad,
}

public interface IBladeTouchpadController
{
    Task ToggleAsync(CancellationToken cancellationToken);
}

public sealed class Blade18FnActionExecutor
{
    private const string ModelNumber = "RZ09-0484";

    private readonly IServiceClient serviceClient;
    private readonly IDisplayProfileApplier displayProfileApplier;
    private readonly IBladeTouchpadController touchpadController;

    public Blade18FnActionExecutor(
        IServiceClient serviceClient,
        IDisplayProfileApplier displayProfileApplier,
        IBladeTouchpadController touchpadController)
    {
        this.serviceClient = serviceClient
            ?? throw new ArgumentNullException(nameof(serviceClient));
        this.displayProfileApplier = displayProfileApplier
            ?? throw new ArgumentNullException(nameof(displayProfileApplier));
        this.touchpadController = touchpadController
            ?? throw new ArgumentNullException(nameof(touchpadController));
    }

    public async Task ExecuteAsync(
        Blade18FnShortcut shortcut,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(shortcut))
        {
            throw new ArgumentOutOfRangeException(nameof(shortcut));
        }
        if (shortcut == Blade18FnShortcut.Performance)
        {
            throw new InvalidOperationException(
                "Blade 18 performance shortcuts must use the performance shortcut pump.");
        }

        ServiceStatusSnapshot status = await serviceClient
            .GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        ValidateAdmission(status);

        if (shortcut == Blade18FnShortcut.RefreshRate)
        {
            await CycleRefreshRateAsync(
                    status.ActivePowerSource,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await touchpadController.ToggleAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CycleRefreshRateAsync(
        PowerSource expectedPowerSource,
        CancellationToken cancellationToken)
    {
        SessionDisplayExecutionResult query = await displayProfileApplier
            .QueryAsync(cancellationToken)
            .ConfigureAwait(false);
        EnsureSucceeded(query, "The display refresh rate could not be read.");
        if (!query.State.IsAvailable)
        {
            throw new InvalidOperationException(
                query.State.Message ?? "Display refresh-rate controls are unavailable.");
        }

        DisplayRefreshOption[] fixedOptions = query.State.RefreshOptions
            .Where(option => !option.IsDynamic)
            .ToArray();
        if (fixedOptions.Length < 2)
        {
            return;
        }

        int currentIndex = Array.FindIndex(
            fixedOptions,
            option => string.Equals(
                option.Key,
                query.State.CurrentRefreshKey,
                StringComparison.Ordinal));
        DisplayRefreshOption next = fixedOptions[(currentIndex + 1) % fixedOptions.Length];
        SessionDisplayExecutionResult result = await displayProfileApplier
            .SetRefreshRateAsync(
                expectedPowerSource,
                next.Key,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSucceeded(result, "The display refresh rate could not be changed.");
    }

    private static void ValidateAdmission(ServiceStatusSnapshot status)
    {
        if (!status.DeviceVerified
            || status.WritesBlocked
            || status.ControlAccess != SessionControlAccess.Active
            || !string.Equals(
                status.Firmware?.ModelNumber,
                ModelNumber,
                StringComparison.OrdinalIgnoreCase)
            || status.ActivePowerSource is not (
                PowerSource.PluggedIn or PowerSource.Battery or PowerSource.UsbC))
        {
            throw new InvalidOperationException(
                "Blade 18 Fn shortcut control is not currently available.");
        }
    }

    private static void EnsureSucceeded(
        SessionDisplayExecutionResult result,
        string fallbackMessage)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.Error ?? fallbackMessage);
        }
    }
}
