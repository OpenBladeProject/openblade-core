using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    private static readonly IReadOnlySet<int> FullExperimentalChargeLimits =
        Enumerable.Range(50, 51).ToHashSet();
    private static readonly IReadOnlySet<int> SourceReviewedChargeLimits =
        new HashSet<int> { 50, 55, 60, 65, 70, 75, 80, 100 };
    private static readonly IReadOnlySet<int> NoExperimentalChargeLimits = new HashSet<int>();

    internal static IReadOnlySet<int> GetExperimentalChargeLimits(ExperimentalBladeModel model) =>
        model.SupportsBatteryProtection && model.ThermalPolicy is not null ? SourceReviewedChargeLimits :
        model.SupportsBatteryProtection && model.ProductId is 0x029F or 0x029D or 0x02C5 or 0x02A0
            ? FullExperimentalChargeLimits
            : NoExperimentalChargeLimits;

    public bool SupportsProtectionQuery => GetExperimentalChargeLimits(model).Count > 0;

    public async Task<ConfirmedBatteryState> QueryProtectionAsync(CancellationToken cancellationToken)
    {
        if (GetExperimentalChargeLimits(model).Count == 0)
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

    private async Task<ConfirmedBatteryState> ReadBatteryAsync(CancellationToken cancellationToken) =>
        (await ReadBatteryPolicyAsync(cancellationToken).ConfigureAwait(false)).State;

    private async Task<(ConfirmedBatteryState State, byte Raw)> ReadBatteryPolicyAsync(CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> payload = await ExchangeAsync("BatteryProtection", 0x07, 0x92,
            [0], false, cancellationToken).ConfigureAwait(false);
        byte raw = payload.Span[0];
        int percent = raw & 0x7F;
        bool flag = (raw & 0x80) != 0;
        // The pinned 029F implementation exercises every whole-percent threshold.
        // The other admitted battery models share this encoding; their expanded
        // range remains upstream-derived until physical validation is complete.
        bool admitted = !flag || GetExperimentalChargeLimits(model).Contains(percent)
            && (model.ThermalPolicy is null || percent != 100);
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
        return (new(enabled, enabled ? percent : null, null), raw);
    }

    public Task SetChargeLimitAsync(int percent, CancellationToken cancellationToken) =>
        GetExperimentalChargeLimits(model).Contains(percent)
            ? SetBatteryAsync(percent == 100 ? null : percent, cancellationToken)
            : Task.FromException(new CapabilityUnavailableException(
                "The charge limit must belong to this exact model's admitted thresholds."));

    public Task SetProtectionOffAsync(CancellationToken cancellationToken) => SetBatteryAsync(null, cancellationToken);

    private async Task SetBatteryAsync(int? limit, CancellationToken cancellationToken)
    {
        if (GetExperimentalChargeLimits(model).Count == 0)
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
            (ConfirmedBatteryState baseline, byte baselineRaw) = await ReadBatteryPolicyAsync(cancellationToken).ConfigureAwait(false);
            policy.Record("BatteryProtection", "Baseline", "Confirmed", requested: requested,
                confirmed: baseline.ProtectionEnabled ? baseline.ChargeLimitPercent!.Value.ToString() : "Off");
            if (baseline.ProtectionEnabled == enabled && baseline.ChargeLimitPercent == limit)
            {
                return;
            }
            if (SupportsTemporaryFullChargeQuery
                && ((await ReadTemporaryFullChargeFlagsAsync(cancellationToken).ConfigureAwait(false))
                    & TemporaryFullChargeProtocol.TemporaryFullChargeBit) != 0)
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "Cancel temporary full charge before changing the charge policy.");
            }
            RequireWriteAdmission();
            // Synapse disables protection with its retained threshold. Legacy upstream
            // profiles retain their separately reviewed disabled-80 encoding.
            byte requestedRaw = enabled ? checked((byte)(0x80 | limit!.Value))
                : model.ThermalPolicy is not null ? (byte)(baselineRaw & 0x7F) : (byte)0x50;
            // Exact-model Synapse parsers populate battery JSON only when the
            // successful SET ACK has data (694 bytes 22017..22413; 695 49350..49742).
            // The optional byte is returned state, not a required echo or confirmation.
            // Keep its one-byte geometry and require an independent exact GET below.
            bool statusAck = model.ThermalPolicy is not null;
            await ExchangeAsync("BatteryProtection", 0x07, 0x12, [requestedRaw],
                true, cancellationToken, variableResponse: statusAck,
                payloadValidator: statusAck
                    ? payload => payload.Length <= 1
                    : null,
                requireEcho: !statusAck).ConfigureAwait(false);
            submitted = true;
            // 029F firmware may return the previous valid state for 1-2 seconds after a SET.
            // https://github.com/sqmagellan/razer-ctl/blob/9c66e7055a0e31f5ec25c16185e66fde1f50cf4a/README.md
            // Source-reviewed limiter models use the same bounded read-only window,
            // but only their exact pre-write raw byte is treated as stale. This is
            // software confirmation tolerance, not a claim of observed firmware delay.
            // Never replay the setter or retry failed/invalid reads.
            int maximumConfirmationReads = statusAck || model.ProductId == 0x029F ? 9 : 1;
            for (int attempt = 0; attempt < maximumConfirmationReads; attempt++)
            {
                if (attempt > 0)
                {
                    await delayConfirmation(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                (ConfirmedBatteryState confirmed, byte confirmedRaw) = await ReadBatteryPolicyAsync(cancellationToken).ConfigureAwait(false);
                if (confirmed.ProtectionEnabled == enabled && confirmed.ChargeLimitPercent == limit
                    && (model.ThermalPolicy is null || confirmedRaw == requestedRaw))
                {
                    policy.Record("BatteryProtection", "Readback", "Confirmed", true, requested, requested);
                    return;
                }
                if (statusAck && confirmedRaw != baselineRaw)
                {
                    break;
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
