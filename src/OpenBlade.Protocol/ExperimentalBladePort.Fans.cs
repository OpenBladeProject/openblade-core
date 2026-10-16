using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    // 029F: Synapse captures tdakhran/razer-ctl 8e12e778/data/README.md,
    // measured 2000..5000 in sqmagellan/razer-ctl 9c66e705/descriptor.rs.
    // 029D: exact profile's 2200..5000 bounds are an experimental extrapolation.
    // No 02A0 fixed targets: its owner report says requested RPM was ignored.
    internal static FanSpeedRange? GetExperimentalFanRange(ExperimentalBladeModel model) => model.ProductId switch
    {
        0x029F => new(2000, 5000, 100),
        0x029D => new(2200, 5000, 100),
        _ => null,
    };

    private async Task<(int First, int Second)> ReadFanTargetsAsync(CancellationToken cancellationToken)
    {
        FanSpeedRange range = GetExperimentalFanRange(model)
            ?? throw new CapabilityUnavailableException("The exact model has no admitted fixed fan range.");
        ReadOnlyMemory<byte> first = await ExchangeAsync("FanTargets", 0x0D, 0x81,
            [0, 1, 0], false, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> second = await ExchangeAsync("FanTargets", 0x0D, 0x81,
            [0, 2, 0], false, cancellationToken).ConfigureAwait(false);
        int firstRpm = first.Span[2] * 100;
        int secondRpm = second.Span[2] * 100;
        if (first.Span[1] != 1 || second.Span[1] != 2
            || !range.Supports(firstRpm) || !range.Supports(secondRpm))
        {
            RecordQuery("FanTargets", "UnsupportedState", failure: "OutsideRepresentableTargets");
            throw ContentRejected("Experimental fan targets cannot form a restorable baseline.",
                DeviceStateQuery.PerformanceAndFans, first.Span, second.Span);
        }
        // Stored targets are not tachometer readings. Never present them as
        // actual RPM; upstream reports slow ramps and noisy actual-RPM samples.
        RecordQuery("FanTargets", "Confirmed", $"Fan1TargetRpm={firstRpm};Fan2TargetRpm={secondRpm}");
        return (firstRpm, secondRpm);
    }

    public Task SetFanModeAsync(FanMode value, CancellationToken cancellationToken) =>
        SetFanControlAsync(FanControlSettings.FromLegacy(value), cancellationToken);

    public Task SetFanControlAsync(FanControlSettings value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Mode switch
        {
            FanMode.Automatic => SetExperimentalFansAsync(null, cancellationToken),
            FanMode.Fixed => SetExperimentalFansAsync((value.FixedSpeedRpm, value.FixedSpeedRpm), cancellationToken),
            _ => Unavailable(),
        };
    }

    public Task SetFanTargetsAsync(int fan1Rpm, int fan2Rpm, CancellationToken cancellationToken) =>
        SetExperimentalFansAsync((fan1Rpm, fan2Rpm), cancellationToken);

    private async Task SetExperimentalFansAsync((int First, int Second)? targets, CancellationToken cancellationToken)
    {
        FanSpeedRange? range = GetExperimentalFanRange(model);
        if (range is null || targets is { } requested
            && (!range.Supports(requested.First) || !range.Supports(requested.Second)))
        {
            throw new CapabilityUnavailableException("The fan request is outside this exact model's admitted range.");
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool submitted = false;
        try
        {
            PowerSource source = capturePowerSource();
            bool SourceUnchanged() => capturePowerSource() == source;
            RequireWriteAdmission(fanMode: targets.HasValue ? FanMode.Fixed : FanMode.Automatic);
            ProductionPerformanceFanState baseline = await ReadPerformanceAsync(cancellationToken).ConfigureAwait(false);
            if (baseline.HyperBoostActive || baseline.Performance is not (PerformancePreset.Balanced
                or PerformancePreset.Performance or PerformancePreset.Silent or PerformancePreset.Custom))
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "Manual fan changes require an ordinary confirmed external-power preset.");
            }
            // Preserve ordinary mode and Custom levels instead of resetting to
            // Balanced just because the original Synapse capture started there.
            // The shared maximum-fan bit can be observed outside Custom; a preset
            // name alone cannot establish that fixed targets have no override.
            if (targets.HasValue && (baseline.CustomPerformance?.MaximumFans == true
                || CreateCustomCapabilities(model).SupportsMaximumFans
                    && await ReadMaximumFansAsync(cancellationToken).ConfigureAwait(false)))
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "Turn off maximum fans in Custom before selecting fixed fan targets.");
            }
            (int First, int Second) saved = await ReadFanTargetsAsync(cancellationToken).ConfigureAwait(false);
            policy.Record("Fans", "Baseline", "Confirmed", requested: targets?.ToString() ?? "Automatic",
                confirmed: $"{baseline.Performance}/{baseline.Fans};Fan1TargetRpm={saved.First};Fan2TargetRpm={saved.Second}");
            if (targets is null && baseline.Fans == FanMode.Automatic
                || targets == saved && baseline.Fans == FanMode.Fixed)
            {
                return;
            }
            byte mode = baseline.Performance switch
            {
                // Product 671 uses DC Balanced raw 6; preserve its battery mode.
                PerformancePreset.Balanced => model.ProductId == 0x029F
                    && source == PowerSource.Battery ? (byte)6 : (byte)0,
                PerformancePreset.Performance => 2,
                PerformancePreset.Silent => 5,
                PerformancePreset.Custom => 4,
                _ => throw new IOException("Unrepresentable fan baseline."),
            };
            if (baseline.Fans != (targets.HasValue ? FanMode.Fixed : FanMode.Automatic))
            {
                foreach (byte zone in new byte[] { 1, 2 })
                {
                    RequireWriteAdmission(fanMode: targets.HasValue ? FanMode.Fixed : FanMode.Automatic);
                    await ExchangeAsync("Fans", 0x0D, 0x02, [1, zone, mode, targets.HasValue ? (byte)1 : (byte)0],
                        true, cancellationToken, writeAdmission: SourceUnchanged).ConfigureAwait(false);
                    submitted = true;
                }
                ProductionPerformanceFanState entered = await ReadPerformanceAsync(cancellationToken).ConfigureAwait(false);
                if (entered.Performance != baseline.Performance
                    || entered.CustomPerformance != baseline.CustomPerformance
                    || entered.Fans != (targets.HasValue ? FanMode.Fixed : FanMode.Automatic))
                {
                    throw new IOException("Independent fan-mode readback did not confirm the requested state.");
                }
            }
            if (targets is { } target)
            {
                foreach (byte zone in new byte[] { 1, 2 })
                {
                    int rpm = zone == 1 ? target.First : target.Second;
                    RequireWriteAdmission(fanMode: FanMode.Fixed);
                    await ExchangeAsync("FanTargets", 0x0D, 0x01, [1, zone, checked((byte)(rpm / 100))],
                        true, cancellationToken, writeAdmission: SourceUnchanged).ConfigureAwait(false);
                    submitted = true;
                    saved = zone == 1 ? (rpm, saved.Second) : (saved.First, rpm);
                    if (await ReadFanTargetsAsync(cancellationToken).ConfigureAwait(false) != saved)
                    {
                        throw new IOException("Independent fan-target readback did not confirm both zones.");
                    }
                }
            }
            ProductionPerformanceFanState confirmed = await ReadPerformanceAsync(cancellationToken).ConfigureAwait(false);
            if (confirmed.Performance != baseline.Performance
                || confirmed.CustomPerformance != baseline.CustomPerformance
                || confirmed.Fans != (targets.HasValue ? FanMode.Fixed : FanMode.Automatic))
            {
                throw new IOException("Independent thermal readback did not preserve the requested fan state.");
            }
            policy.Record("Fans", "Readback", "Confirmed", submitted,
                targets?.ToString() ?? "Automatic", targets?.ToString() ?? "Automatic");
        }
        catch (Exception exception)
        {
            throw MapWriteFailure("Fans", submitted, exception);
        }
        finally
        {
            operationGate.Release();
        }
    }
}
