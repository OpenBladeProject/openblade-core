using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort
{
    // Both exact upstream readers expose preset and manual flags independently.
    // Their reviewed model-wide Fixed ranges remain unchanged. Host curves reuse
    // those same admitted ordinary presets and commands on confirmed Full AC.
    internal static bool SupportsStaticSourceHostCurves(ExperimentalBladeModel model) =>
        model.ProductId is 0x029D or 0x029F;

    // Each dynamic envelope intersects the exact model's pinned middleware
    // and UI declarations. Fresh current-preset firmware metadata must further
    // bound every manual operation. Custom is admitted only for 709/711.
    internal static FanSpeedRange? GetExperimentalFanRange(ExperimentalBladeModel model) =>
        GetExperimentalFanRangesByPerformancePreset(model).GetValueOrDefault(PerformancePreset.Balanced)
        ?? (model.ProductId switch
        {
            // Exact upstream captures and descriptor bounds retain their own basis.
            0x029F => new(2000, 5000, 100),
            // 029D retains its separately reviewed upstream extrapolation.
            0x029D => new(2200, 5000, 100),
            _ => null,
        });

    internal static FanSpeedRange? GetExperimentalFanRange(
        ExperimentalBladeModel model, PerformancePreset performance)
    {
        IReadOnlyDictionary<PerformancePreset, FanSpeedRange> ranges = GetExperimentalFanRangesByPerformancePreset(model);
        return ranges.Count > 0 ? ranges.GetValueOrDefault(performance) : GetExperimentalFanRange(model);
    }

    internal static IReadOnlyDictionary<PerformancePreset, FanSpeedRange>
        GetExperimentalFanRangesByPerformancePreset(ExperimentalBladeModel model)
    {
        var ranges = new Dictionary<PerformancePreset, FanSpeedRange>();
        void Add(PerformancePreset preset, int minimum, int maximum) => ranges.Add(preset, new(minimum, maximum, 100));
        switch (model.ProductId)
        {
            case 0x02A0:
                Add(PerformancePreset.Balanced, 2200, 3800);
                Add(PerformancePreset.Silent, 3700, 5300);
                break;
            case 0x0234:
            case 0x024C:
                Add(PerformancePreset.Balanced, 3700, 5200);
                break;
            case 0x0245:
            case 0x0253:
                Add(PerformancePreset.Balanced, 3700, 5300);
                break;
            case 0x0246:
                Add(PerformancePreset.Balanced, 3500, 5500);
                Add(PerformancePreset.Gaming, 3900, 5500);
                Add(PerformancePreset.Creator, 3900, 5500);
                break;
            case 0x0268:
                Add(PerformancePreset.Balanced, 3600, 5200);
                Add(PerformancePreset.Gaming, 3600, 5200);
                Add(PerformancePreset.Creator, 3600, 5200);
                break;
            case 0x0270:
                Add(PerformancePreset.Balanced, 3300, 5400);
                break;
            case 0x0276:
                Add(PerformancePreset.Balanced, 3400, 5200);
                break;
            case 0x028C:
                Add(PerformancePreset.Balanced, 3300, 5400);
                Add(PerformancePreset.Silent, 3300, 5400);
                break;
            case 0x028A:
                Add(PerformancePreset.Balanced, 3400, 5200);
                Add(PerformancePreset.Silent, 3400, 5200);
                break;
            case 0x0255:
                Add(PerformancePreset.Balanced, 4300, 5500);
                Add(PerformancePreset.Performance, 4900, 5500);
                Add(PerformancePreset.Creator, 4900, 5500);
                break;
            case 0x02C5:
                Add(PerformancePreset.Balanced, 3400, 5200);
                Add(PerformancePreset.Silent, 3400, 3800);
                Add(PerformancePreset.MaximumPower, 3300, 5400);
                Add(PerformancePreset.Custom, 4000, 5300);
                Add(PerformancePreset.BatteryBalanced, 3400, 5200);
                break;
            case 0x02B6:
                Add(PerformancePreset.Balanced, 2000, 5400);
                Add(PerformancePreset.Turbo, 2000, 5400);
                Add(PerformancePreset.Silent, 2200, 3800);
                break;
            case 0x02B7:
                Add(PerformancePreset.Balanced, 3400, 5200);
                Add(PerformancePreset.Silent, 3400, 3800);
                Add(PerformancePreset.Turbo, 3300, 5400);
                break;
            case 0x02B8:
                Add(PerformancePreset.Balanced, 3700, 5300);
                Add(PerformancePreset.Silent, 3700, 5300);
                Add(PerformancePreset.Turbo, 3300, 5400);
                break;
            case 0x02C7:
                Add(PerformancePreset.Balanced, 3400, 5200);
                Add(PerformancePreset.Silent, 3400, 3800);
                Add(PerformancePreset.Turbo, 3300, 5400);
                Add(PerformancePreset.MaximumPower, 3300, 5400);
                Add(PerformancePreset.Custom, 4000, 5300);
                Add(PerformancePreset.BatteryBalanced, 3400, 5200);
                break;
            case 0x028B:
                Add(PerformancePreset.Balanced, 2200, 3800);
                Add(PerformancePreset.Silent, 2200, 3800);
                break;
        }
        return ranges;
    }

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
        FanSpeedRange? exactRange = null,
        PerformancePreset? expectedPerformance = null,
        bool readDynamicRange = true)
    {
        if (GetExperimentalFanRange(model) is null)
        {
            throw new CapabilityUnavailableException("The exact model has no admitted fixed fan range.");
        }
        // Product 672 uses class 1 for its target register; 0D/81 is the
        // pinned base source's getter for the observed class-1 0D/01 setters.
        // This query is source-backed, not present in the Observer recording.
        if (readDynamicRange && UsesDynamicExperimentalFanRange(model))
        {
            exactRange = await ReadDynamicExperimentalFanRangeAsync(cancellationToken, expectedPerformance).ConfigureAwait(false);
        }
        byte profile = model.ThermalPolicy?.QueryProfile ?? (model.ProductId == 0x02A0 || UsesDynamicExperimentalFanRange(model) ? (byte)1 : (byte)0);
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
            // A curve enters the current source-backed AC preset at its freshly
            // bounded maximum. The existing service curve loop owns later
            // target-only updates and never needs a new firmware command.
            FanMode.ManualCurve when model.ProductId == 0x02A0 || UsesDynamicExperimentalFanRange(model)
                || SupportsStaticSourceHostCurves(model) =>
                StartCurrentPresetManualCurveAsync(cancellationToken),
            _ => Unavailable(),
        };
    }

    private Task StartCurrentPresetManualCurveAsync(CancellationToken cancellationToken)
    {
        // A curve uses the same typed target updates in the already selected
        // preset. Its fallback starts at that preset's freshly bounded maximum.
        FanSpeedRange range = GetExperimentalFanRange(model)
            ?? throw new CapabilityUnavailableException("The exact model has no admitted fan range.");
        return SetExperimentalFansAsync((range.MaximumRpm, range.MaximumRpm), cancellationToken,
            useDynamicMaximum: true, manualCurve: true);
    }

    public Task SetFanTargetsAsync(int fan1Rpm, int fan2Rpm, CancellationToken cancellationToken) =>
        SetExperimentalFansAsync((fan1Rpm, fan2Rpm), cancellationToken);

    public Task<(int Fan1Rpm, int Fan2Rpm)> SetCurveFanTargetsAsync(
        PerformancePreset expectedPerformance, int fan1Rpm, int fan2Rpm, CancellationToken cancellationToken) =>
        SetExperimentalFansAsync((fan1Rpm, fan2Rpm), cancellationToken,
            curveUpdateExpectedPerformance: expectedPerformance);

    private async Task<(int Fan1Rpm, int Fan2Rpm)> SetExperimentalFansAsync(
        (int First, int Second)? targets,
        CancellationToken cancellationToken,
        bool useDynamicMaximum = false,
        bool manualCurve = false,
        PerformancePreset? curveUpdateExpectedPerformance = null)
    {
        bool curveUpdate = curveUpdateExpectedPerformance.HasValue;
        bool projectCurve = manualCurve || curveUpdate;
        (int First, int Second)? originalCurveTargets = targets;
        FanSpeedRange? range = GetExperimentalFanRange(model);
        if (curveUpdate && (!CreateCapabilities(model, true).FanModes.Contains(FanMode.ManualCurve)
            || targets is not { } curveRequest
            || curveRequest.First < FanControlSettings.MinimumSpeedRpm || curveRequest.First > FanControlSettings.MaximumSupportedSpeedRpm
            || curveRequest.Second < FanControlSettings.MinimumSpeedRpm || curveRequest.Second > FanControlSettings.MaximumSupportedSpeedRpm))
        {
            throw new CapabilityUnavailableException("The curve target request is outside the typed manual fan contract.");
        }
        if (range is null && !(model.ThermalPolicy is not null && targets is null)
            || !projectCurve && targets is { } requested
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
            RequireWriteAdmission(fanMode: projectCurve ? FanMode.ManualCurve : targets.HasValue ? FanMode.Fixed : FanMode.Automatic);
            ProductionPerformanceFanState baseline = await ReadPerformanceAsync(
                cancellationToken, readManualTargets: false).ConfigureAwait(false);
            if (curveUpdateExpectedPerformance is { } expected
                && (baseline.Performance != expected || baseline.Fans != FanMode.Fixed))
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "Curve targets require the expected preset with confirmed manual fans.");
            }
            if (baseline.HyperBoostActive || baseline.Performance is not (PerformancePreset.Balanced
                or PerformancePreset.Performance or PerformancePreset.Silent or PerformancePreset.Custom
                or PerformancePreset.Turbo or PerformancePreset.MaximumPower
                or PerformancePreset.Gaming or PerformancePreset.Creator
                or PerformancePreset.BatterySaver or PerformancePreset.BatteryBalanced))
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "Fan changes require a supported, confirmed performance mode.");
            }
            if (targets.HasValue && UsesDynamicExperimentalFanRange(model)
                && (source == PowerSource.Battery
                    ? !SupportsDynamicBatteryFixedFans(model) || baseline.Performance != PerformancePreset.BatteryBalanced
                    : baseline.Performance == PerformancePreset.BatteryBalanced))
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "Manual fans require agreement between the current preset and its admitted power source.");
            }
            // Exact source admission is preset-specific. No absent Custom or DC
            // range can inherit a different preset's envelope.
            if (GetExperimentalFanRangesByPerformancePreset(model).Count > 0 && targets.HasValue
                && (baseline.Performance is not { } selectedPreset
                    || GetExperimentalFanRange(model, selectedPreset) is null))
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "This performance preset has no admitted manual fan range.");
            }
            FanSpeedRange? activeRange = baseline.Performance is { } performance
                ? GetExperimentalFanRange(model, performance)
                : null;
            FanSpeedRange? sourceEnvelope = activeRange;
            if (projectCurve && targets is { } curveTargets && activeRange is not null)
            {
                targets = (activeRange.Quantize(curveTargets.First), activeRange.Quantize(curveTargets.Second));
            }
            if (useDynamicMaximum && activeRange is not null)
            {
                targets = (activeRange.MaximumRpm, activeRange.MaximumRpm);
            }
            if (targets is { } activeTargets
                && (activeRange is null
                    || !activeRange.Supports(activeTargets.First)
                    || !activeRange.Supports(activeTargets.Second)))
            {
                throw new CapabilityUnavailableException(
                    "The fan request is outside the active performance preset's admitted range.");
            }
            if (targets is { } dynamicTargets && UsesDynamicExperimentalFanRange(model))
            {
                FanSpeedRange dynamicRange = await ReadDynamicExperimentalFanRangeAsync(cancellationToken, baseline.Performance).ConfigureAwait(false);
                activeRange = dynamicRange;
                if (useDynamicMaximum)
                {
                    targets = (dynamicRange.MaximumRpm, dynamicRange.MaximumRpm);
                }
                else if (projectCurve)
                {
                    targets = (dynamicRange.Quantize(dynamicTargets.First), dynamicRange.Quantize(dynamicTargets.Second));
                }
                else if (!dynamicRange.Supports(dynamicTargets.First) || !dynamicRange.Supports(dynamicTargets.Second))
                {
                    throw new CapabilityUnavailableException("The target is outside the freshly reported firmware fan range.");
                }
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
            // Automatic handback changes only the mode flag. Target-free profiles
            // do not query or invent a baseline; admitted manual target paths retain
            // independently read values so an untouched zone is never guessed.
            bool readTargets = targets.HasValue;
            (int First, int Second) saved = !readTargets ? default : await ReadFanTargetsAsync(
                cancellationToken,
                targets.HasValue && baseline.Fans == FanMode.Fixed
                    ? curveUpdate ? sourceEnvelope : activeRange
                    : null, baseline.Performance, readDynamicRange: false).ConfigureAwait(false);
            policy.Record("Fans", "Baseline", "Confirmed", requested: targets?.ToString() ?? "Automatic",
                confirmed: !readTargets ? $"{baseline.Performance}/{baseline.Fans};Targets=NotQueried"
                    : $"{baseline.Performance}/{baseline.Fans};Fan1TargetRpm={saved.First};Fan2TargetRpm={saved.Second}");
            // Matching manual state is observation, not proof of service ownership.
            // Explicit manual actions must submit their admitted targets. Only
            // already-owned typed curve maintenance may complete without a SET.
            if (targets is null && baseline.Fans == FanMode.Automatic
                || curveUpdate && targets == saved && baseline.Fans == FanMode.Fixed)
            {
                return saved;
            }
            byte mode = baseline.Performance switch
            {
                // Products 671/669 use DC Balanced raw 6; preserve their battery mode.
                PerformancePreset.Balanced => SupportsExperimentalHyperBoost(model)
                    && source == PowerSource.Battery ? (byte)6 : (byte)0,
                PerformancePreset.Gaming when model.ThermalPolicy?.SupportsGaming == true => 1,
                PerformancePreset.Creator when model.ThermalPolicy?.SupportsCreator == true || model.ProductId == 0x0255 => 2,
                PerformancePreset.Turbo when model.ThermalPolicy?.SupportsTurbo == true => 1,
                PerformancePreset.MaximumPower when model.ThermalPolicy?.SupportsMaximumPower == true || model.ProductId == 0x02C5 => 2,
                PerformancePreset.BatterySaver when model.ThermalPolicy?.SupportsBatterySaver == true || model.ProductId == 0x02C5 => 3,
                PerformancePreset.BatteryBalanced when model.ThermalPolicy?.SupportsBalancedDc == true || model.ProductId == 0x02C5 => 6,
                PerformancePreset.Performance => model.ProductId == 0x0255 ? (byte)1 : (byte)2,
                PerformancePreset.Silent => 5,
                PerformancePreset.Custom => 4,
                _ => throw new IOException("Unrepresentable fan baseline."),
            };
            bool acceptsStatusOnlyAck = UsesSourceBackedSetAckContract(model);
            byte baselineFlag = baseline.Fans == FanMode.Fixed ? (byte)1 : (byte)0;
            byte requestedFlag = targets.HasValue ? (byte)1 : (byte)0;
            if (baseline.Fans != (targets.HasValue ? FanMode.Fixed : FanMode.Automatic))
            {
                foreach (byte zone in new byte[] { 1, 2 })
                {
                    RequireWriteAdmission(fanMode: targets.HasValue ? FanMode.Fixed : FanMode.Automatic);
                    if (targets is { } flagTargets && UsesDynamicExperimentalFanRange(model))
                    {
                        FanSpeedRange flagRange = await ReadDynamicExperimentalFanRangeAsync(
                            cancellationToken, baseline.Performance,
                            (zone == 1 ? baselineFlag : requestedFlag, baselineFlag, zone == 2 ? requestedFlag : null))
                            .ConfigureAwait(false);
                        if (projectCurve)
                        {
                            targets = useDynamicMaximum
                                ? (flagRange.MaximumRpm, flagRange.MaximumRpm)
                                : (flagRange.Quantize(flagTargets.First), flagRange.Quantize(flagTargets.Second));
                            flagTargets = targets.Value;
                        }
                        if (!flagRange.Supports(flagTargets.First) || !flagRange.Supports(flagTargets.Second))
                        {
                            throw new CapabilityUnavailableException("The targets no longer fit the fresh range before manual entry.");
                        }
                    }
                    await RequirePresetRangedFanStateAsync(mode,
                        zone == 1 ? baselineFlag : requestedFlag,
                        baselineFlag,
                        zone == 2 ? requestedFlag : null,
                        cancellationToken, requireLegacyCurveState: projectCurve).ConfigureAwait(false);
                    await ExchangeAsync("Fans", 0x0D, 0x02, [1, zone, mode, requestedFlag],
                        true, cancellationToken,
                        variableResponse: acceptsStatusOnlyAck,
                        payloadValidator: acceptsStatusOnlyAck ? static payload => payload.Length <= 4 : null,
                        requireEcho: !acceptsStatusOnlyAck,
                        requireAdequateAdapter: projectCurve,
                        writeAdmission: SourceUnchanged).ConfigureAwait(false);
                    submitted = true;
                }
                // The mode flag changes before either retained target is
                // replaced. Accept either admitted preset range only within
                // this staged transition; final readback remains exact.
                ProductionPerformanceFanState entered = await ReadPerformanceAsync(
                    cancellationToken,
                    allowTransitionalFanTargets: true,
                    readManualTargets: false).ConfigureAwait(false);
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
                    int requestedRpm = zone == 1 ? (originalCurveTargets ?? target).First : (originalCurveTargets ?? target).Second;
                    int rpm = zone == 1 ? target.First : target.Second;
                    RequireWriteAdmission(fanMode: FanMode.Fixed);
                    FanSpeedRange? freshRange = activeRange;
                    if (UsesDynamicExperimentalFanRange(model))
                    {
                        freshRange = await ReadDynamicExperimentalFanRangeAsync(cancellationToken, baseline.Performance).ConfigureAwait(false);
                        if (projectCurve)
                        {
                            rpm = useDynamicMaximum ? freshRange.MaximumRpm : freshRange.Quantize(requestedRpm);
                        }
                        if (zone == 2 && !freshRange.Supports(saved.First))
                        {
                            throw new CapabilityUnavailableException("The first confirmed fan target no longer fits the second fresh range; reconcile without replay.");
                        }
                        if (!freshRange.Supports(rpm))
                        {
                            throw new CapabilityUnavailableException("The target is outside the freshly reported firmware fan range.");
                        }
                    }
                    if (projectCurve && !UsesDynamicExperimentalFanRange(model) && freshRange is not null)
                    {
                        rpm = useDynamicMaximum ? freshRange.MaximumRpm : freshRange.Quantize(requestedRpm);
                    }
                    await RequirePresetRangedFanStateAsync(mode, 1, 1, null, cancellationToken,
                        requireLegacyCurveState: projectCurve).ConfigureAwait(false);
                    await ExchangeAsync("FanTargets", 0x0D, 0x01, [1, zone, checked((byte)(rpm / 100))],
                        true, cancellationToken,
                        variableResponse: acceptsStatusOnlyAck,
                        payloadValidator: acceptsStatusOnlyAck ? static payload => payload.Length <= 3 : null,
                        requireEcho: !acceptsStatusOnlyAck,
                        requireAdequateAdapter: projectCurve,
                        writeAdmission: SourceUnchanged).ConfigureAwait(false);
                    submitted = true;
                    saved = zone == 1 ? (rpm, saved.Second) : (saved.First, rpm);
                    // After the first zone write, the untouched zone can still
                    // hold the prior preset's target. Tuple equality proves the
                    // expected staged state until both writes are complete.
                    // The untouched target can belong to another reviewed preset
                    // after an Automatic transition. Its exact saved value remains
                    // independently confirmed; only new SETs use the fresh range.
                    if (await ReadFanTargetsAsync(cancellationToken, expectedPerformance: baseline.Performance,
                        readDynamicRange: false).ConfigureAwait(false) != saved)
                    {
                        throw new IOException("Independent fan-target readback did not confirm both zones.");
                    }
                }
            }
            ProductionPerformanceFanState confirmed = await ReadPerformanceAsync(
                cancellationToken, readManualTargets: false).ConfigureAwait(false);
            if (confirmed.Performance != baseline.Performance
                || confirmed.CustomPerformance != baseline.CustomPerformance
                || confirmed.Fans != (targets.HasValue ? FanMode.Fixed : FanMode.Automatic))
            {
                throw new IOException("Independent thermal readback did not preserve the requested fan state.");
            }
            policy.Record("Fans", "Readback", "Confirmed", submitted,
                targets?.ToString() ?? "Automatic", targets.HasValue ? saved.ToString() : "Automatic");
            return saved;
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
    private async Task RequirePresetRangedFanStateAsync(
        byte mode, byte firstFlag, byte secondFlag, byte? propagatedSecondFlag,
        CancellationToken cancellationToken, bool requireLegacyCurveState = false)
    {
        // The upstream029D/029F reader also exposes independent mode/fan bytes.
        // New AC curves preserve the confirmed raw preset and need these checks.
        // Legacy Fixed retains its separately admitted DC0-to6 selector transition.
        if (GetExperimentalFanRangesByPerformancePreset(model).Count == 0
            && !(requireLegacyCurveState && SupportsStaticSourceHostCurves(model)))
        {
            return;
        }
        // Range metadata has no preset identity. Recheck the paired mode/flags
        // after reading it, immediately before each admitted setter. During a
        // flag transition only our first write may have propagated to zone two.
        (ReadOnlyMemory<byte> first, ReadOnlyMemory<byte> second) =
            await ReadPerformanceSelectorsAsync(cancellationToken).ConfigureAwait(false);
        if (first.Span[2] != mode || second.Span[2] != mode
            || first.Span[3] != firstFlag
            || second.Span[3] != secondFlag && second.Span[3] != propagatedSecondFlag)
        {
            throw ContentRejected("The performance preset or manual fan state changed before submission.",
                DeviceStateQuery.PerformanceAndFans, first.Span, second.Span);
        }
    }

}
