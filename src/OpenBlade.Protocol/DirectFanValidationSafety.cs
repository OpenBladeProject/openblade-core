using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

// The developer validator temporarily owns the HID collection only while all
// installed OpenBlade processes are stopped. Recheck before every command.
internal sealed class DirectFanValidationAccessGuard(
    IHardwareAccessGuard controllerGuard,
    IRunningProcessSource processes,
    IWindowsServiceSource services) : IHardwareAccessGuard
{
    public HardwareAccessDecision Check()
    {
        try
        {
            HardwareAccessDecision controller = controllerGuard.Check();
            if (!controller.Safe)
            {
                return controller;
            }

            string[] owners = processes.GetProcessNames().Where(name =>
                name.Equals("OpenBlade.Service", StringComparison.OrdinalIgnoreCase)
                || name.Equals("OpenBlade.Tray", StringComparison.OrdinalIgnoreCase)
                || name.Equals("OpenBlade.SessionAgent", StringComparison.OrdinalIgnoreCase)
                || name.Equals("USBPcapCMD", StringComparison.OrdinalIgnoreCase)).ToArray();
            bool serviceActive = services.GetServices().Any(service =>
                service.ServiceName.Equals("OpenBlade", StringComparison.OrdinalIgnoreCase)
                && service.State != WindowsServiceState.Stopped);
            return owners.Length > 0 || serviceActive
                ? new HardwareAccessDecision(["Another OpenBlade or capture owner is active."], null)
                : controller;
        }
        catch
        {
            return new HardwareAccessDecision([], "The validation owner inventory is unavailable.");
        }
    }
}

internal sealed class DirectFanValidationSafetyGate(
    IHardwareAccessGuard accessGuard,
    TimeSpan exchangeTimeout) : IPerformanceFanValidationSafetyGate
{
    public async Task<PerformanceFanValidationSafetyGateResult> ValidateAsync(
        ValidationRunContext context,
        IRazerCommandSession session,
        CancellationToken cancellationToken)
    {
        if (!accessGuard.Check().Safe)
        {
            return Failure(PerformanceFanValidationSafetyFailure.ValidationOwnerUnavailable);
        }

        if (!context.TryAllocateTransaction(out byte transactionId))
        {
            return Failure(PerformanceFanValidationSafetyFailure.AdapterPowerReadFailed);
        }

        HidSessionResult<AdapterPowerState> power = await new AdapterPowerCommandClient(
            session,
            exchangeTimeout).QueryAsync(transactionId, cancellationToken).ConfigureAwait(false);
        if (power.Failure is not null)
        {
            return Failure(cancellationToken.IsCancellationRequested
                ? PerformanceFanValidationSafetyFailure.Canceled
                : PerformanceFanValidationSafetyFailure.AdapterPowerReadFailed);
        }

        if (!HyperBoostValidationPower.IsAdmittedFullPower(power.Value))
        {
            return Failure(PerformanceFanValidationSafetyFailure.FullPowerAdapterRequired);
        }

        return new PerformanceFanValidationSafetyGateResult(
            true, false, false, false, PerformanceFanValidationSafetyFailure.None)
        {
            AdapterPowerState = power.Value,
            CoolingPadNotRequired = true,
        };
    }

    private static PerformanceFanValidationSafetyGateResult Failure(
        PerformanceFanValidationSafetyFailure failure) => new(false, false, false, false, failure);
}
