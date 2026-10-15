using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    public bool SupportsProtectionQuery => model.SupportsBatteryProtection;

    public bool SupportsTemporaryFullChargeQuery => false;

    public async Task<ConfirmedBatteryState> QueryProtectionAsync(CancellationToken cancellationToken)
    {
        if (!model.SupportsBatteryProtection)
        {
            throw new CapabilityUnavailableException("This model does not implement battery protection.");
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadBatteryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<ConfirmedBatteryState> ReadBatteryAsync(CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> payload = await ExchangeAsync("BatteryProtection", 0x07, 0x92,
            [0], false, cancellationToken).ConfigureAwait(false);
        byte raw = payload.Span[0];
        int percent = raw & 0x7F;
        bool flag = (raw & 0x80) != 0;
        bool admitted = model.ProductId == 0x02A0
            ? !flag || percent is >= 50 and <= 100
            : raw is 0x50 or 0xD0;
        if (!admitted)
        {
            RecordQuery("BatteryProtection", "UnsupportedState",
                confirmed: $"ReportedProtectionEnabled={flag};ReportedLimitPercent={percent};Raw={raw:X2}",
                failure: "OutsideAdmittedBatteryStates");
            throw ContentRejected("Experimental battery readback is outside this model's admitted states.",
                DeviceStateQuery.BatteryProtection, payload.Span);
        }
        // Upstream 100% is no limit: write disabled 0x50. An enabled-at-100
        // baseline is also no limit; retain its actual flag/threshold in diagnostics.
        bool enabled = flag && percent < 100;
        RecordQuery("BatteryProtection", "Confirmed",
            confirmed: $"Effective={(enabled ? percent.ToString() : "Off")};ReportedProtectionEnabled={flag};ReportedLimitPercent={percent};Raw={raw:X2}");
        return new(enabled, enabled ? percent : null, null);
    }

    public Task SetChargeLimitAsync(int percent, CancellationToken cancellationToken) =>
        model.ProductId == 0x02A0 ? percent is >= 50 and <= 100
            ? SetBatteryAsync(percent == 100 ? null : percent, cancellationToken)
            : Task.FromException(new CapabilityUnavailableException("Blade 18 limits must be from 50 to 100 percent."))
        : percent == 80 ? SetBatteryAsync(80, cancellationToken)
        : Task.FromException(new CapabilityUnavailableException("Only the upstream-derived 80% limit is enabled for this model."));

    public Task SetProtectionOffAsync(CancellationToken cancellationToken) => SetBatteryAsync(null, cancellationToken);

    private async Task SetBatteryAsync(int? limit, CancellationToken cancellationToken)
    {
        if (!model.SupportsBatteryProtection)
        {
            throw new CapabilityUnavailableException("Class 07 battery commands are not supported on this model.");
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool submitted = false;
        bool enabled = limit.HasValue;
        string requested = enabled ? limit!.Value.ToString() : "Off";
        try
        {
            RequireWriteAdmission();
            ConfirmedBatteryState baseline = await ReadBatteryAsync(cancellationToken).ConfigureAwait(false);
            policy.Record("BatteryProtection", "Baseline", "Confirmed", requested: requested,
                confirmed: baseline.ProtectionEnabled ? baseline.ChargeLimitPercent!.Value.ToString() : "Off");
            if (baseline.ProtectionEnabled == enabled && baseline.ChargeLimitPercent == limit)
            {
                return;
            }
            RequireWriteAdmission();
            await ExchangeAsync("BatteryProtection", 0x07, 0x12, [enabled ? checked((byte)(0x80 | limit!.Value)) : (byte)0x50],
                true, cancellationToken).ConfigureAwait(false);
            submitted = true;
            // 029F firmware may return the previous valid state for 1-2 seconds after a SET.
            // https://github.com/sqmagellan/razer-ctl/blob/9c66e7055a0e31f5ec25c16185e66fde1f50cf4a/README.md
            // Retry only valid stale GET results. Never replay the setter or retry failed/invalid reads.
            int maximumConfirmationReads = model.ProductId == 0x029F ? 9 : 1;
            for (int attempt = 0; attempt < maximumConfirmationReads; attempt++)
            {
                if (attempt > 0)
                {
                    await delayConfirmation(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                ConfirmedBatteryState confirmed = await ReadBatteryAsync(cancellationToken).ConfigureAwait(false);
                if (confirmed.ProtectionEnabled == enabled && confirmed.ChargeLimitPercent == limit)
                {
                    policy.Record("BatteryProtection", "Readback", "Confirmed", true, requested, requested);
                    return;
                }
            }
            throw new IOException("Independent battery readback did not converge to the requested value.");
        }
        catch (Exception exception)
        {
            throw MapWriteFailure("BatteryProtection", submitted, exception);
        }
        finally
        {
            operationGate.Release();
        }
    }

}
