using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    // 029F: Synapse captures tdakhran/razer-ctl 8e12e778/data/README.md,
    // measured 2000..5000 in sqmagellan/razer-ctl 9c66e705/descriptor.rs.
    // 029D: exact profile's 2200..5000 bounds are an experimental extrapolation.
    // 02A0: product 672's exact main bundle D523F508... module 26061 declares
    // Balanced 2200..3800 and Silent 3700..5300. Shared module 85225 in
    // 6737.e50bb836... applies one manualFanSpeed to both thermal zones after
    // selecting the preset's raw mode. Custom manual is disabled by the absent
    // customModeSupportsMoreThanAuto flag. See docs/blade18-observer-evidence.md.
    internal static FanSpeedRange? GetExperimentalFanRange(ExperimentalBladeModel model) => model.ProductId switch
    {
        0x029F => new(2000, 5000, 100),
        0x029D => new(2200, 5000, 100),
        0x02A0 => new(2200, 3800, 100),
        _ => null,
    };

    internal static FanSpeedRange? GetExperimentalFanRange(
        ExperimentalBladeModel model,
        PerformancePreset performance) => model.ProductId == 0x02A0
            ? performance switch
            {
                PerformancePreset.Balanced => new(2200, 3800, 100),
                PerformancePreset.Silent => new(3700, 5300, 100),
                _ => null,
            }
            : GetExperimentalFanRange(model);

    internal static IReadOnlyDictionary<PerformancePreset, FanSpeedRange>
        GetExperimentalFanRangesByPerformancePreset(ExperimentalBladeModel model) =>
            model.ProductId == 0x02A0
                ? new Dictionary<PerformancePreset, FanSpeedRange>
                {
                    [PerformancePreset.Balanced] = new(2200, 3800, 100),
                    [PerformancePreset.Silent] = new(3700, 5300, 100),
                }
                : new Dictionary<PerformancePreset, FanSpeedRange>();

    private static bool SupportsExperimentalFanTarget(
        ExperimentalBladeModel model,
        int speedRpm,
        FanSpeedRange? exactRange = null)
    {
        if (exactRange is not null)
        {
            return exactRange.Supports(speedRpm);
        }

        return GetExperimentalFanRangesByPerformancePreset(model).Values.Any(
            range => range.Supports(speedRpm))
            || GetExperimentalFanRange(model)?.Supports(speedRpm) == true;
    }

    private async Task<(int First, int Second)> ReadFanTargetsAsync(
        CancellationToken cancellationToken,
        FanSpeedRange? exactRange = null)
    {
        if (GetExperimentalFanRange(model) is null)
        {
            throw new CapabilityUnavailableException("The exact model has no admitted fixed fan range.");
        }
        // Product 672 uses class 1 for its target register; 0D/81 is the
        // pinned base source's getter for the observed class-1 0D/01 setters.
        // This query is source-backed, not present in the Observer recording.
        byte profile = model.ProductId == 0x02A0 ? (byte)1 : (byte)0;
        ReadOnlyMemory<byte> first = await ExchangeAsync("FanTargets", 0x0D, 0x81,
            [profile, 1, 0], false, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> second = await ExchangeAsync("FanTargets", 0x0D, 0x81,
            [profile, 2, 0], false, cancellationToken).ConfigureAwait(false);
        int firstRpm = first.Span[2] * 100;
        int secondRpm = second.Span[2] * 100;
        if (first.Span[0] != profile || second.Span[0] != profile
            || first.Span[1] != 1 || second.Span[1] != 2
            || !SupportsExperimentalFanTarget(model, firstRpm, exactRange)
            || !SupportsExperimentalFanTarget(model, secondRpm, exactRange))
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
            // A curve enters the same source-backed Balanced manual mode at its
            // safest admitted target. The existing service curve loop owns later
            // target-only updates and never needs a new firmware command.
            FanMode.ManualCurve when model.ProductId == 0x02A0 =>
                StartBlade18ManualCurveAsync(cancellationToken),
            _ => Unavailable(),
        };
    }

    private Task StartBlade18ManualCurveAsync(CancellationToken cancellationToken)
    {
        FanSpeedRange range = GetExperimentalFanRange(model)
            ?? throw new CapabilityUnavailableException("The exact model has no admitted fan range.");
        return SetExperimentalFansAsync(
            (range.MaximumRpm, range.MaximumRpm),
            cancellationToken,
            requiredPerformance: PerformancePreset.Balanced);
    }

    public Task SetFanTargetsAsync(int fan1Rpm, int fan2Rpm, CancellationToken cancellationToken) =>
        SetExperimentalFansAsync((fan1Rpm, fan2Rpm), cancellationToken);

    private async Task SetExperimentalFansAsync(
        (int First, int Second)? targets,
        CancellationToken cancellationToken,
        PerformancePreset? requiredPerformance = null)
    {
        FanSpeedRange? range = GetExperimentalFanRange(model);
        if (range is null || targets is { } requested
            && (!SupportsExperimentalFanTarget(model, requested.First)
                || !SupportsExperimentalFanTarget(model, requested.Second)))
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
                    "Fan changes require a supported, confirmed performance mode.");
            }
            if (requiredPerformance is { } required
                && baseline.Performance != required)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    $"This fan mode requires confirmed {required} performance.");
            }
            // Product 672 exposes one scalar slider for Balanced and Silent and
            // writes it to both zones. Custom manual remains source-disabled.
            if (model.ProductId == 0x02A0 && targets.HasValue
                && baseline.Performance is not (PerformancePreset.Balanced or PerformancePreset.Silent))
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "Fixed fan targets on this model require Balanced or Silent mode.");
            }
            FanSpeedRange? activeRange = baseline.Performance is { } performance
                ? GetExperimentalFanRange(model, performance)
                : null;
            if (targets is { } activeTargets
                && (activeRange is null
                    || !activeRange.Supports(activeTargets.First)
                    || !activeRange.Supports(activeTargets.Second)))
            {
                throw new CapabilityUnavailableException(
                    "The fan request is outside the active performance preset's admitted range.");
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
            (int First, int Second) saved = await ReadFanTargetsAsync(
                cancellationToken,
                targets.HasValue && baseline.Fans == FanMode.Fixed
                    ? activeRange
                    : null).ConfigureAwait(false);
            policy.Record("Fans", "Baseline", "Confirmed", requested: targets?.ToString() ?? "Automatic",
                confirmed: $"{baseline.Performance}/{baseline.Fans};Fan1TargetRpm={saved.First};Fan2TargetRpm={saved.Second}");
            if (targets is null && baseline.Fans == FanMode.Automatic
                || targets == saved && baseline.Fans == FanMode.Fixed)
            {
                return;
            }
            byte mode = baseline.Performance switch
            {
                // Products 671/669 use DC Balanced raw 6; preserve their battery mode.
                PerformancePreset.Balanced => SupportsExperimentalHyperBoost(model)
                    && source == PowerSource.Battery ? (byte)6 : (byte)0,
                PerformancePreset.Performance => 2,
                PerformancePreset.Silent => 5,
                PerformancePreset.Custom => 4,
                _ => throw new IOException("Unrepresentable fan baseline."),
            };
            bool acceptsStatusOnlyAck = UsesSourceBackedSetAckContract(model);
            if (baseline.Fans != (targets.HasValue ? FanMode.Fixed : FanMode.Automatic))
            {
                foreach (byte zone in new byte[] { 1, 2 })
                {
                    RequireWriteAdmission(fanMode: targets.HasValue ? FanMode.Fixed : FanMode.Automatic);
                    await ExchangeAsync("Fans", 0x0D, 0x02, [1, zone, mode, targets.HasValue ? (byte)1 : (byte)0],
                        true, cancellationToken,
                        variableResponse: acceptsStatusOnlyAck,
                        payloadValidator: acceptsStatusOnlyAck ? static payload => payload.Length <= 4 : null,
                        requireEcho: !acceptsStatusOnlyAck,
                        writeAdmission: SourceUnchanged).ConfigureAwait(false);
                    submitted = true;
                }
                // The mode flag changes before either retained target is
                // replaced. Accept either admitted preset range only within
                // this staged transition; final readback remains exact.
                ProductionPerformanceFanState entered = await ReadPerformanceAsync(
                    cancellationToken,
                    allowTransitionalFanTargets: true).ConfigureAwait(false);
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
                        true, cancellationToken,
                        variableResponse: acceptsStatusOnlyAck,
                        payloadValidator: acceptsStatusOnlyAck ? static payload => payload.Length <= 3 : null,
                        requireEcho: !acceptsStatusOnlyAck,
                        writeAdmission: SourceUnchanged).ConfigureAwait(false);
                    submitted = true;
                    saved = zone == 1 ? (rpm, saved.Second) : (saved.First, rpm);
                    // After the first zone write, the untouched zone can still
                    // hold the prior preset's target. Tuple equality proves the
                    // expected staged state until both writes are complete.
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
