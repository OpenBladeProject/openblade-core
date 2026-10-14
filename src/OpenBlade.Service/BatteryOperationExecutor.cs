using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

// The coordinator owns admission, initial/final reconciliation and failure handling.
// This executor owns typed setters and the bounded cancellation follow-up only.
internal sealed class BatteryOperationExecutor(IBladeDevice device)
{
    internal Task ExecuteAsync(BatteryOperation operation, CancellationToken cancellationToken) =>
        operation switch
        {
            BatteryOperation.None => Task.CompletedTask,
            BatteryOperation.ChargeLimit chargeLimit =>
                device.SetChargeLimitAsync(chargeLimit.Policy, cancellationToken),
            BatteryOperation.ProtectionOff => device.SetBatteryProtectionOffAsync(cancellationToken),
            BatteryOperation.TemporaryFullCharge fullCharge =>
                device.SetTemporaryFullChargeAsync(fullCharge.Enabled, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    internal async Task ApplyTransitionAsync(
        DesiredBatteryState desired,
        DeviceCapabilities capabilities,
        ConfirmedBatteryState? initial,
        CancellationToken cancellationToken)
    {
        BatteryOperationPlan plan = BatteryOperationPlanner.CreateBatteryPlan(desired, capabilities, initial);
        await ExecuteAsync(plan.Operation, cancellationToken).ConfigureAwait(false);

        if (initial?.TemporaryFullCharge is not true
            || desired.TemporaryFullCharge)
        {
            return;
        }

        ConfirmedBatteryState? refreshed = (await DeviceStateReadIsolation.ReadAsync(
                device.ReadStateAsync,
                DeviceStateReadIsolation.Battery,
                cancellationToken)
            .ConfigureAwait(false)).Battery;
        if (refreshed?.TemporaryFullCharge is not false)
        {
            return;
        }

        // Keep one intent bounded to the cancellation plus one confirmed follow-up.
        // Any remaining multi-step mismatch is retried by the next automation cycle.
        BatteryOperationPlan followUp = BatteryOperationPlanner.CreateBatteryPlan(
            desired,
            capabilities,
            refreshed);
        if (followUp.Supported && !followUp.InitiallyMatching)
        {
            await ExecuteAsync(followUp.Operation, cancellationToken).ConfigureAwait(false);
        }
    }
}
