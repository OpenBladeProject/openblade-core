using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed partial class ExperimentalBladePort(
    IRazerCommandSession session,
    ValidationRunContext context,
    ExperimentalBladeModel model,
    ExperimentalFeaturesPolicy policy,
    Func<bool> exactHostStillPresent,
    Func<PowerSource> capturePowerSource,
    Func<TimeSpan, CancellationToken, Task>? confirmationDelay = null,
    bool recordQueries = true,
    SemaphoreSlim? sharedOperationGate = null,
    ExperimentalLightingOwnershipState? sharedLightingOwnership = null,
    ICoolingPadDiscoverySource? coolingPadDiscovery = null,
    TimeProvider? lightingTimeProvider = null,
    ExperimentalStartupAnimationState? sharedStartupAnimationState = null)
    : IProductionBatteryPort, IProductionLightingPort, ILightingRecoveryWriteAdmission
{
    private readonly SemaphoreSlim operationGate = sharedOperationGate ?? new(1, 1);
    private readonly AdapterPowerCommandClient adapterPowerCommands = new(session, TimeSpan.FromSeconds(3));
    private readonly ExperimentalLightingOwnershipState lightingOwnership =
        sharedLightingOwnership ?? new();
    private readonly Func<TimeSpan, CancellationToken, Task> delayConfirmation = confirmationDelay ?? Task.Delay;
    private bool disposed;
    private int liveTransactionId;
    private bool queryRecordingEnabled = recordQueries;

    internal ExperimentalControlObservation? LastAdmissionFailure { get; private set; }

    internal void EnableQueryRecording() => queryRecordingEnabled = true;

    internal bool IsDisposed => disposed;

    internal bool IsRecoverySessionDrained => session.Disposition == RazerCommandSessionDisposition.FullyDisposed;

    internal bool IsRecoverySessionHealthy => !disposed
        && session.Disposition == RazerCommandSessionDisposition.Healthy;

    internal async Task<ConfirmedDeviceState> ReadRecoveryStateAsync(CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref startupAnimationState.ReconciliationRequired) != 0)
            {
                _ = await ReadStartupAnimationCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            ConfirmedBatteryState? recoveredTemporaryBattery = SupportsTemporaryFullChargeQuery
                ? await ReadTemporaryFullChargeRecoveryAsync(cancellationToken).ConfigureAwait(false) : null;
            ProductionPerformanceFanState performance = await ReadPerformanceAsync(cancellationToken).ConfigureAwait(false);
            if (performance.Performance is null && !performance.HyperBoostActive || performance.Fans is null)
            {
                throw new IOException("Experimental performance readback cannot form a confirmed recovery state.");
            }
            // A thermal-only fault does not require a limiter GET that older firmware
            // may not implement. Leave those fields unknown rather than poisoning
            // the fresh recovery session with an unrelated optional query. Override-
            // capable backends still read their real policy and flag above.
            ConfirmedBatteryState? battery = recoveredTemporaryBattery ?? (model.SupportsBatteryProtection
                && policy.RecoveryScope == ExperimentalRecoveryScope.All
                ? await ReadBatteryAsync(cancellationToken).ConfigureAwait(false) : null);
            // The limiter GET reports only its enable bit and threshold. Without
            // an admitted full-charge query, preserve null rather than infer Off.
            (LightingSettings? lighting, LogoLightingMode? logo) =
                SupportsExperimentalLighting(model)
                    && policy.RecoveryScope == ExperimentalRecoveryScope.All
                    ? await ReadRecoveryLightingStateAsync(cancellationToken).ConfigureAwait(false)
                    : (null, null);
            return new(performance.Performance, performance.Fans, battery, lighting)
            {
                HyperBoostActive = performance.HyperBoostActive,
                CustomPerformance = performance.CustomPerformance,
                LogoLighting = logo,
                IndependentLighting = SupportsIndependentLighting(model)
                    && policy.RecoveryScope != ExperimentalRecoveryScope.ThermalOnly
                    ? await ReadIndependentLightingCoreAsync(cancellationToken).ConfigureAwait(false) : null,
            };
        }
        finally
        {
            operationGate.Release();
        }
    }

    private void RecordQuery(string operation, string outcome, string? confirmed = null, string? failure = null)
    {
        if (outcome is "UnsupportedState" or "InconsistentState")
        {
            switch (operation)
            {
                case "BatteryProtection":
                    session.RetainQueryFailure(0x07, 0x92, FirmwareQueryOutcome.ContentRejected);
                    break;
                case "Performance":
                    session.RetainQueryFailure(0x0D, 0x82, FirmwareQueryOutcome.ContentRejected);
                    break;
                case "MaximumFans":
                    session.RetainQueryFailure(0x07, 0x8F, FirmwareQueryOutcome.ContentRejected);
                    break;
                case "FanRange":
                    session.RetainQueryFailure(0x0D, 0x83, FirmwareQueryOutcome.ContentRejected);
                    break;
                case "FanTargets":
                    session.RetainQueryFailure(0x0D, 0x81, FirmwareQueryOutcome.ContentRejected);
                    break;
                case "KeyboardStaticColor":
                case "KeyboardBreathingColor":
                    session.RetainQueryFailure(0x03, 0x8A, FirmwareQueryOutcome.ContentRejected);
                    break;
                case "CustomPerformance":
                    session.RetainQueryFailure(0x0D, 0x87, FirmwareQueryOutcome.ContentRejected);
                    break;
            }
        }
        if (queryRecordingEnabled)
        {
            policy.Record(operation, "Query", outcome, confirmed: confirmed, failure: failure);
        }
        else if (outcome != "Confirmed")
        {
            LastAdmissionFailure = new(DateTimeOffset.UtcNow, operation, "Query", outcome,
                false, ConfirmedValue: confirmed, FailureCategory: failure);
        }
    }

    private readonly LightingRecoveryWriteGate recoveryWriteGate = new();

    public void SetRecoveryWriteAdmission(Func<bool> admission) => recoveryWriteGate.Set(admission);

    private Func<bool>? additionalWriteAdmission;

    internal void SetAdditionalWriteAdmission(Func<bool> admission) => additionalWriteAdmission = admission;

    public DeviceCapabilities Capabilities => WithConfirmedExperimentalFanRange(
        WithStartupReadAdmission(CreateCapabilities(model, policy.WritesAllowed), startupAnimationState),
        model, policy.ConfirmedFanRanges) with
    {
        Blade18KeyboardLayoutId = blade18KeyboardLayoutId,
    };

    internal static DeviceCapabilities CreateCapabilities(ExperimentalBladeModel model, bool enabled) =>
        new(enabled ? CreatePerformancePresets(model) : new HashSet<PerformancePreset>(),
            enabled && GetExperimentalFanRange(model) is not null
                ? model.ProductId == 0x02A0 || UsesDynamicExperimentalFanRange(model) || SupportsStaticSourceHostCurves(model)
                    ? new HashSet<FanMode> { FanMode.Automatic, FanMode.Fixed, FanMode.ManualCurve }
                    : new HashSet<FanMode> { FanMode.Automatic, FanMode.Fixed }
                : enabled && model.ThermalPolicy is not null ? new HashSet<FanMode> { FanMode.Automatic } : [],
            CreateExperimentalLightingEffects(model, enabled),
            enabled ? GetExperimentalChargeLimits(model) : new HashSet<int>(),
            enabled && GetExperimentalChargeLimits(model).Count > 0, enabled && model.ProductId == 0x02A0)
        {
            CustomPerformance = enabled && SupportsExperimentalCustom(model) ? CreateCustomCapabilities(model) : null,
            FanSpeedRange = enabled ? GetExperimentalFanRange(model) : null,
            FanSpeedRangesByPerformancePreset = enabled
                ? GetExperimentalFanRangesByPerformancePreset(model)
                : new Dictionary<PerformancePreset, FanSpeedRange>(),
            // Preset-ranged models enter manual control in the confirmed preset.
            // Crossing presets needs separate target-range continuity admission.
            SupportsDirectManualFanTransitions = enabled && model.ProductId != 0x02A0 && !UsesDynamicExperimentalFanRange(model)
                && GetExperimentalFanRange(model) is not null,
            HasFanStopSourceEvidence = enabled
                && FanStopModelContract.ForProfile(model.ModelNumber, model.ProductId, model.ProductName).Count != 0,
            // 029F/029D use separate mode/fan bytes in 0D82 and independent
            // Custom getters, unlike validated backends' shared manual selector.
            // See sqmagellan 9c66e705/librazer/src/command.rs and descriptor.rs.
            ReportsPerformanceWithManualFans = enabled && GetExperimentalFanRange(model) is not null,
            SupportsKeyboardBrightness = SupportsExperimentalKeyboardBrightness(model, enabled),
            SupportsIndependentLightingControls = SupportsIndependentLighting(model),
            LogoLightingModes = CreateExperimentalLogoModes(model, enabled),
            SupportsBreathingColorSelection = false,
            SupportedBreathingColorModes =
                CreateExperimentalBreathingColorModes(model, enabled),
            SupportsWaveDirectionSelection =
                SupportsExperimentalWaveDirectionSelection(model),
            SupportsUniformLightingFrames =
                SupportsExperimentalUniformFrames(model, enabled),
            HyperBoostAccess = SupportsExperimentalHyperBoost(model) && enabled ? DeviceFeatureAccess.ReadWrite
                : model.ProductId is 0x02A0 or 0x029F or 0x029D ? DeviceFeatureAccess.ObservedOnly : null,
            HyperBoostWithoutCoolingPadAccess = SupportsExperimentalHyperBoost(model) && enabled ? DeviceFeatureAccess.ReadWrite : null,
            HyperBoostBaseModes = SupportsExperimentalHyperBoost(model) && enabled
                ? new(DeviceFeatureAccess.ReadWrite, DeviceFeatureAccess.ReadWrite,
                    model.ProductId == 0x029F ? DeviceFeatureAccess.ReadWrite : DeviceFeatureAccess.ObservedOnly)
                {
                    Custom = DeviceFeatureAccess.ReadWrite,
                    CoolingPadEntryBaseMode = model.ProductId == 0x029D ? PerformancePreset.Balanced : null,
                } : null,
            OneShotLaptopControls = enabled && model.ProductId == 0x02A0
                ? new HashSet<OneShotLaptopControl> { OneShotLaptopControl.StartupAnimation } : [],
            HostManagedLaptopControls = enabled && model.ProductId == 0x02A0
                ? new HashSet<OneShotLaptopControl> { OneShotLaptopControl.GamingMode, OneShotLaptopControl.FunctionKeyPrimary } : [],
            KeyboardLightingLayoutId = !enabled ? null : model.ProductId switch
            {
                0x02A0 => KeyboardLightingLayoutIds.Rz09_0484,
                0x02C5 => KeyboardLightingLayoutIds.Rz09_0530,
                _ => null,
            },
            ObservedLightingEffects = CreateObservedExperimentalLightingEffects(model),
            ThxAudio = model.ProductId == 0x02C5
                ? new ThxAudioCapabilities(
                    DeviceFeatureAccess.ReadWrite,
                    ThxAudioProductIds.Rznb11)
                : null,
        };

    private static bool SupportsExperimentalCustom(ExperimentalBladeModel model) =>
        model.ThermalPolicy?.SupportsCustom ?? (model.ProductId is 0x029F or 0x029D or 0x02C5 or 0x0255 or 0x02A0);

    // Products 671 and 672 inherit these SET helpers from their exact pinned rzDevice25 sources:
    // 671 rzDevice25.5e330aa9fdf6f29eec62.js
    // (SHA-256 41E7758114AD3605B5B3EE0E65E79AF3055124F775904902BA3285F710AF55C9),
    // 672 rzDevice25.85ce8530211c29f3c304.js
    // (SHA-256 DFB3E3612D36B20910AF02E61A17ED64A1FFD3781E09C63BAAEE7CBEFDEBCA24).
    // Their 0D/01 parsers/setters are at 671 chars 2101/2543 and 672 chars 60353/60797;
    // 0D/02 parsers/setters at 671 chars 2958/3448 and 672 chars 61214/61706;
    // and 0D/07 parsers/setters at 671 chars 7082/7516 and 672 chars 65356/65792.
    // Their 07/0F helpers/public methods are at 671 chars 20394/140294 and 672 chars 52955/141017.
    // These paths accept status ACKs and parse optional payload; every relaxed caller keeps an independent GET.
    private static bool UsesSourceBackedSetAckContract(ExperimentalBladeModel model) =>
        model.ThermalPolicy is not null || model.ProductId is 0x029F or 0x02A0 or 0x0255 or 0x02C5;

    private static IReadOnlySet<PerformancePreset> CreatePerformancePresets(ExperimentalBladeModel model)
    {
        if (model.ThermalPolicy is null && !SupportsExperimentalCustom(model))
        {
            return new HashSet<PerformancePreset>();
        }
        var presets = new HashSet<PerformancePreset>
        {
            PerformancePreset.Balanced,
        };
        if (SupportsExperimentalCustom(model))
        {
            presets.Add(PerformancePreset.Custom);
        }
        if (model.ThermalPolicy is { } thermal)
        {
            if (thermal.SupportsGaming)
            {
                presets.Add(PerformancePreset.Gaming);
            }
            if (thermal.SupportsCreator)
            {
                presets.Add(PerformancePreset.Creator);
            }
            if (thermal.SupportsSilent)
            {
                presets.Add(PerformancePreset.Silent);
            }
            if (thermal.SupportsTurbo)
            {
                presets.Add(PerformancePreset.Turbo);
            }
            if (thermal.SupportsMaximumPower)
            {
                presets.Add(PerformancePreset.MaximumPower);
            }
            if (thermal.SupportsBalancedDc)
            {
                presets.Add(PerformancePreset.BatteryBalanced);
            }
            if (thermal.SupportsBatterySaver)
            {
                presets.Add(PerformancePreset.BatterySaver);
            }
            return presets;
        }
        presets.Add(PerformancePreset.Silent);
        // 0255 Gaming uses code 1. Code 2 has direct 029F/02A0 source evidence;
        // do not infer that distinct preset for the two AMD Blade 14 profiles.
        if (model.ProductId is 0x0255 or 0x029F or 0x02A0)
        {
            presets.Add(PerformancePreset.Performance);
        }
        if (model.ProductId == 0x0255)
        {
            presets.Add(PerformancePreset.Creator);
        }
        if (model.ProductId == 0x02C5)
        {
            presets.Add(PerformancePreset.MaximumPower);
            presets.UnionWith([PerformancePreset.BatterySaver, PerformancePreset.BatteryBalanced]);
        }
        return presets;
    }

    public async Task<ProductionPerformanceFanState> QueryPerformanceFanStateAsync(CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadPerformanceAsync(cancellationToken, readManualTargets: false).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task ValidateControlInterfaceAsync(CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Interface identity depends on independently correlated selector replies,
            // not mutable mode/fan values. Semantic rejection must leave unrelated
            // getters available; thermal setters still require a decoded baseline.
            await ReadPerformanceSelectorsAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<(ReadOnlyMemory<byte> First, ReadOnlyMemory<byte> Second)>
        ReadPerformanceSelectorsAsync(CancellationToken cancellationToken)
    {
        byte profile = model.ThermalPolicy?.QueryProfile ?? (UsesDynamicExperimentalFanRange(model) ? (byte)1 : (byte)0);
        ReadOnlyMemory<byte> first = await ExchangeAsync("Performance", 0x0D, 0x82,
            [profile, 1, 0, 0], false, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> second = await ExchangeAsync("Performance", 0x0D, 0x82,
            [profile, 2, 0, 0], false, cancellationToken).ConfigureAwait(false);
        if (first.Span[1] != 1 || second.Span[1] != 2
            || (model.ThermalPolicy is not null || UsesDynamicExperimentalFanRange(model)) && (first.Span[0] != profile || second.Span[0] != profile))
        {
            RecordQuery("Performance", "InconsistentState", failure: "UnexpectedSelector");
            throw new IOException("Experimental performance reply did not match its requested selector.");
        }
        return (first, second);
    }

    private async Task<ProductionPerformanceFanState> ReadPerformanceAsync(
        CancellationToken cancellationToken,
        bool allowTransitionalFanTargets = false,
        bool readManualTargets = true)
    {
        (ReadOnlyMemory<byte> first, ReadOnlyMemory<byte> second) =
            await ReadPerformanceSelectorsAsync(cancellationToken).ConfigureAwait(false);
        if (first.Span[2] != second.Span[2] || first.Span[3] != second.Span[3]
            || first.Span[3] > 1)
        {
            RecordQuery("Performance", "InconsistentState",
                confirmed: $"Selector1={first.Span[1]};Mode1={first.Span[2]};FanFlag1={first.Span[3]};Selector2={second.Span[1]};Mode2={second.Span[2]};FanFlag2={second.Span[3]}",
                failure: "SelectorAgreement");
            throw ContentRejected("Experimental performance selectors returned inconsistent state.",
                DeviceStateQuery.ThermalChannelAgreement, first.Span, second.Span);
        }
        byte mode = first.Span[2];
        bool hyperBoost = mode == 7 && model.ProductId is 0x02A0 or 0x029F or 0x029D;
        PerformancePreset? performance = mode switch
        {
            0 => PerformancePreset.Balanced,
            4 when SupportsExperimentalCustom(model) => PerformancePreset.Custom,
            1 when model.ThermalPolicy?.SupportsGaming == true => PerformancePreset.Gaming,
            2 when model.ThermalPolicy?.SupportsCreator == true || model.ProductId == 0x0255 => PerformancePreset.Creator,
            5 when model.ThermalPolicy?.SupportsSilent != false => PerformancePreset.Silent,
            1 when model.ThermalPolicy?.SupportsTurbo == true => PerformancePreset.Turbo,
            2 when model.ThermalPolicy?.SupportsMaximumPower == true || model.ProductId == 0x02C5 => PerformancePreset.MaximumPower,
            3 when model.ThermalPolicy?.SupportsBatterySaver == true => PerformancePreset.BatterySaver,
            6 when model.ThermalPolicy?.SupportsBalancedDc == true => PerformancePreset.BatteryBalanced,
            3 when model.ProductId == 0x02C5 => PerformancePreset.BatterySaver,
            6 when model.ProductId == 0x02C5 => PerformancePreset.BatteryBalanced,
            // Products 671/669 map battery Balanced to raw 6 (AC Balanced is 0).
            6 when SupportsExperimentalHyperBoost(model) && capturePowerSource() == PowerSource.Battery => PerformancePreset.Balanced,
            1 when model.ProductId == 0x0255 => PerformancePreset.Performance,
            2 when model.ProductId is 0x02A0 or 0x029F => PerformancePreset.Performance,
            7 when model.ProductId is 0x02A0 or 0x029F or 0x029D => null,
            _ => UnsupportedPerformanceMode(first.Span),
        };
        FanMode fans = first.Span[3] == 0 ? FanMode.Automatic : FanMode.Fixed;
        CustomPerformanceSettings? custom = mode == 4 && SupportsExperimentalCustom(model)
            ? await ReadCustomPerformanceAsync(cancellationToken).ConfigureAwait(false) : null;
        if (readManualTargets && fans == FanMode.Fixed && GetExperimentalFanRange(model) is not null)
        {
            FanSpeedRange? activeRange = performance is { } activePerformance
                ? GetExperimentalFanRange(model, activePerformance)
                : null;
            if (GetExperimentalFanRangesByPerformancePreset(model).Count > 0 && activeRange is null)
            {
                RecordQuery(
                    "FanTargets",
                    "UnsupportedState",
                    failure: "ManualPresetHasNoAdmittedRange");
                throw ContentRejected(
                    "The exact model reported manual fans in a performance preset without an admitted range.",
                    DeviceStateQuery.PerformanceAndFans,
                    first.Span,
                    second.Span);
            }
            await ReadFanTargetsAsync(
                cancellationToken,
                allowTransitionalFanTargets ? null : activeRange, performance).ConfigureAwait(false);
        }
        // Custom without model-specific levels cannot establish a restorable
        // thermal baseline. Reject it like any other unrepresentable state.
        if (performance == PerformancePreset.Custom && custom is null)
        {
            RecordQuery("Performance", "UnsupportedState", $"ReportedMode=4;Fans={fans};CustomLevels=Unavailable",
                "UnrepresentableCustomPerformance");
            throw ContentRejected("Experimental Custom mode has no representable levels for this model.",
                DeviceStateQuery.PerformanceAndFans, first.Span, second.Span);
        }
        else
        {
            RecordQuery("Performance", "Confirmed", confirmed: $"{(hyperBoost ? "HyperBoost" : performance?.ToString())}/{fans}");
        }
        return new(performance, fans) { HyperBoostActive = hyperBoost, CustomPerformance = custom };
    }

    private PerformancePreset? UnsupportedPerformanceMode(ReadOnlySpan<byte> payload)
    {
        byte mode = payload[2];
        RecordQuery("Performance", "UnsupportedState", confirmed: $"ReportedMode={mode}",
            failure: "OutsideUpstreamPerformanceModes");
        throw ContentRejected("Experimental performance mode is outside the upstream model definition.",
            DeviceStateQuery.PerformanceAndFans, payload);
    }

    private static RazerProtocolException ContentRejected(string message, DeviceStateQuery query,
        ReadOnlySpan<byte> first, ReadOnlySpan<byte> second = default) =>
        new(message, HidProtocolValidationFailure.PayloadValidator)
        {
            StateQuery = query,
            RejectedPayloadHex = second.IsEmpty ? DeviceStateContentRejection.FormatPayload(first)
                : DeviceStateContentRejection.FormatPayloads(first, second),
        };

    public async Task SetPerformanceAsync(PerformancePreset value, CancellationToken cancellationToken)
    {
        if (value == PerformancePreset.Custom || !CreatePerformancePresets(model).Contains(value))
        {
            throw new CapabilityUnavailableException("This preset is outside the exact experimental model definition.");
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool submitted = false;
        bool acceptsStatusOnlyAck = UsesSourceBackedSetAckContract(model);
        try
        {
            RequirePerformanceWriteAdmission(value);
            ProductionPerformanceFanState baseline = await ReadPerformanceAsync(cancellationToken).ConfigureAwait(false);
            policy.Record("Performance", "Baseline", "Confirmed", requested: value.ToString(),
                confirmed: $"{(baseline.HyperBoostActive ? "HyperBoost" : baseline.Performance?.ToString())}/{baseline.Fans}");
            if (baseline.Performance == value)
            {
                return;
            }
            // Models without a target getter cannot preserve a manual baseline.
            // 02A0 admits manual targets in Balanced and Silent, but changing
            // presets still requires confirmed Automatic fans.
            if (baseline.Fans != FanMode.Automatic
                && (GetExperimentalFanRange(model) is null || GetExperimentalFanRangesByPerformancePreset(model).Count > 0))
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    model.ThermalPolicy is not null
                        ? "Changing presets cannot establish that both retained manual targets fit the destination's fresh range. Select Automatic fans explicitly."
                        : "Experimental performance changes require confirmed Automatic fans.");
            }
            if (baseline.Fans == FanMode.Fixed
                && (baseline.HyperBoostActive || baseline.CustomPerformance?.MaximumFans == true))
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "The fixed-fan baseline conflicts with a firmware fan override.");
            }
            (int First, int Second)? baselineTargets = baseline.Fans == FanMode.Fixed
                ? await ReadFanTargetsAsync(cancellationToken).ConfigureAwait(false) : null;
            if (CreateCustomCapabilities(model).SupportsMaximumFans)
            {
                // Synapse clears Maximum Fans before leaving Custom; 0D/02
                // does not establish that the shared 07/8F bit was cleared.
                // Reuse the whole-byte guard so cleanup cannot clear another option.
                byte flags = await ReadPowerModeControlAsync(cancellationToken).ConfigureAwait(false);
                if ((flags & TemporaryFullChargeProtocol.MaximumFanSpeedBit) != 0)
                {
                    RequireMaximumFansWriteBaseline(flags);
                    RequirePerformanceWriteAdmission(value);
                    await ExchangeAsync("MaximumFans", 0x07, 0x0F, [0],
                        true, cancellationToken,
                        variableResponse: acceptsStatusOnlyAck,
                        payloadValidator: acceptsStatusOnlyAck ? static payload => payload.Length <= 1 : null,
                        requireEcho: !acceptsStatusOnlyAck).ConfigureAwait(false);
                    submitted = true;
                    if (await ReadMaximumFansAsync(cancellationToken).ConfigureAwait(false))
                    {
                        throw new IOException("Maximum Fans cleanup was not independently confirmed.");
                    }
                }
            }
            byte mode = value switch
            {
                PerformancePreset.Balanced => 0,
                PerformancePreset.Gaming when model.ThermalPolicy?.SupportsGaming == true => 1,
                PerformancePreset.Creator when model.ThermalPolicy?.SupportsCreator == true || model.ProductId == 0x0255 => 2,
                PerformancePreset.Turbo when model.ThermalPolicy?.SupportsTurbo == true => 1,
                PerformancePreset.MaximumPower when model.ThermalPolicy?.SupportsMaximumPower == true || model.ProductId == 0x02C5 => 2,
                PerformancePreset.Silent => 5,
                PerformancePreset.Performance => model.ProductId == 0x0255 ? (byte)1 : (byte)2,
                PerformancePreset.BatterySaver => 3,
                PerformancePreset.BatteryBalanced => 6,
                _ => throw new CapabilityUnavailableException("This preset has no experimental wire value."),
            };
            foreach (byte selector in new byte[] { 1, 2 })
            {
                RequirePerformanceWriteAdmission(value);
                // The admitted 029F/029D shape has independent mode and fan bytes;
                // changing a preset does not request relinquishing fixed targets.
                await ExchangeAsync("Performance", 0x0D, 0x02,
                    [1, selector, mode, baseline.Fans == FanMode.Fixed ? (byte)1 : (byte)0],
                    true, cancellationToken,
                    variableResponse: acceptsStatusOnlyAck,
                    payloadValidator: acceptsStatusOnlyAck ? static payload => payload.Length <= 4 : null,
                    requireEcho: !acceptsStatusOnlyAck).ConfigureAwait(false);
                submitted = true;
            }
            ProductionPerformanceFanState confirmed = await ReadPerformanceAsync(cancellationToken).ConfigureAwait(false);
            if (confirmed.Performance != value || confirmed.Fans != baseline.Fans
                || baselineTargets.HasValue
                    && await ReadFanTargetsAsync(cancellationToken).ConfigureAwait(false) != baselineTargets.Value)
            {
                throw new IOException("Independent performance readback did not confirm the requested value.");
            }
            policy.Record("Performance", "Readback", "Confirmed", true, value.ToString(), value.ToString());
        }
        catch (Exception exception)
        {
            throw MapWriteFailure("Performance", submitted, exception);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private void RequireWriteAdmission(bool thermal = false, FanMode? fanMode = null)
    {
        recoveryWriteGate.ThrowIfBlocked();
        if (!policy.WritesAllowed)
        {
            policy.Record("WriteAdmission", "Guard", "Blocked", failure: "RecoveryRequired");
            throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                "Experimental writes are paused after an uncertain result. Check the current settings, then resume experimental controls.");
        }
        if (!exactHostStillPresent())
        {
            policy.Record("WriteAdmission", "Guard", "Blocked", failure: "ExactHostUnavailable");
            throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                "OpenBlade can no longer confirm the exact admitted laptop identity. Refresh device detection before retrying.");
        }
        if (additionalWriteAdmission?.Invoke() == false)
        {
            policy.Record("WriteAdmission", "Guard", "Blocked", failure: "AdditionalAdmissionRejected");
            throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                "Additional write safety checks did not pass. Check Troubleshooting before retrying.");
        }
        if (thermal)
        {
            PowerSource source = capturePowerSource();
            if (!ExternalPowerReported(source))
            {
                policy.Record("WriteAdmission", "Guard", "Blocked",
                    confirmed: $"PowerSource={source}", failure: "ExternalPowerNotConfirmed");
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    $"Thermal writes require confirmed external power; the current source is {source}. Confirm the external power source and retry.");
            }
        }
        if (fanMode is { } requestedFan)
        {
            PowerSource source = capturePowerSource();
            if (!FanModeSourceIsAdmitted(source, requestedFan))
            {
                policy.Record("WriteAdmission", "Guard", "Blocked",
                    requested: $"FanMode={requestedFan}", confirmed: $"PowerSource={source}",
                    failure: "FanModePowerSourceRejected");
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    $"Fan mode {requestedFan} is not admitted on {source} power. Connect the factory charger or choose a fan mode admitted for that source.");
            }
        }
    }

    // The production callback reports Windows AC presence as ExternalUnknown.
    // This preflight is not write admission: ExchangeAsync must independently
    // classify a known external adapter immediately before every thermal setter.
    private static bool ExternalPowerReported(PowerSource source) =>
        PowerSourcePolicy.IsExternal(source) || source == PowerSource.ExternalUnknown;

    private bool ExternalPowerPresent() => ExternalPowerReported(capturePowerSource());

    private void RequirePerformanceWriteAdmission(PerformancePreset value)
    {
        RequireWriteAdmission();
        if (!CreatePerformancePresets(model).Contains(value)
            || !(ExternalPowerPresent() || PerformancePowerSourcePolicy.IsAllowed(capturePowerSource(), value)))
        {
            throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                "The exact experimental preset is not admitted on the current power source.");
        }
    }

    // The pinned 694/695/696 task runners clear Maximum Fans before their
    // DC-Balanced branch. Only that reviewed source family admits disable-only
    // cleanup on battery; other bits and independent readback retain their guards.
    private bool IsSourceReviewedMaximumFansDisable(
        string operation, byte commandClass, byte commandId, byte[] arguments) =>
        model.ProductId is 0x02B6 or 0x02B7 or 0x02B8
            && model.ThermalPolicy is { SupportsMaximumFans: true, SupportsBalancedDc: true }
            && operation == "MaximumFans" && commandClass == 0x07 && commandId == 0x0F
            && arguments.Length == 1 && arguments[0] == 0;

    private bool ThermalSubmissionAllowed(
        string operation,
        byte commandClass,
        byte commandId,
        byte[] arguments,
        PowerSource? admittedFanSource,
        PowerSource? admittedFanPowerClass)
    {
        // The exact 02A0 full-charge bit changes battery policy, not thermal mode.
        // It retains general submission admission and its own shared-mask guard.
        if (IsTemporaryFullChargeSet(operation, commandClass, commandId, arguments))
        {
            return arguments[0] == 0 || TemporaryFullChargeEnablePowerAdmitted();
        }
        if (IsSourceReviewedMaximumFansDisable(operation, commandClass, commandId, arguments)
            && capturePowerSource() == PowerSource.Battery)
        {
            return true;
        }
        if (IsFanOperation(operation))
        {
            return admittedFanSource is { } source
                && capturePowerSource() == source
                && admittedFanPowerClass is { } powerClass
                && FanCommandSourceIsAdmitted(powerClass, operation, arguments);
        }

        // Balanced/Automatic is the typed HyperBoost low-power handoff. Its
        // adapter/source pair and unchanged source are checked by the caller.
        if (operation == "HyperBoostRestore" && commandClass == 0x0D && commandId == 0x02
            && arguments.Length == 4 && arguments[2] == 6 && arguments[3] == 0
            && capturePowerSource() == PowerSource.Battery)
        {
            return true;
        }
        // 02C5 battery-mode evidence: blade-control issue #2 and pinned
        // bc48c916 PerformanceService.cs. Recheck at native submission, not just
        // before a queued operation; never replay an uncertain mode command.
        if (commandClass == 0x0D && commandId == 0x02
            && arguments.Length == 4 && arguments[2] is 3 or 6)
        {
            return (model.ProductId == 0x02C5
                    || arguments[2] == 3 && model.ThermalPolicy?.SupportsBatterySaver == true
                    || arguments[2] == 6 && model.ThermalPolicy?.SupportsBalancedDc == true)
                && (capturePowerSource() == PowerSource.Battery || ExternalPowerPresent());
        }
        return commandClass != 0x0D && !(commandClass == 0x07 && commandId == 0x0F)
            || ExternalPowerPresent();
    }

    private BladeWriteException MapWriteFailure(string operation, bool submitted, Exception exception)
    {
        submitted |= exception is BladeWriteException { SubmissionState: WriteSubmissionState.Indeterminate };
        if (submitted)
        {
            policy.RequireRecovery(operation is "Performance" or "CustomPerformance" or "Fans"
                or "HyperBoost" or "HyperBoostRestore"
                ? ExperimentalRecoveryScope.ThermalOnly : operation == "IndependentLighting"
                    ? ExperimentalRecoveryScope.LightingFieldsOnly : ExperimentalRecoveryScope.All);
        }
        policy.Record(operation, "Failure", submitted ? "Indeterminate" : "NotApplied", submitted,
            failure: exception.GetType().Name);
        return new(submitted ? WriteSubmissionState.Indeterminate : WriteSubmissionState.KnownNotApplied,
            LightingRecoveryWriteGate.IsRejection(exception) ? BladeWriteFailurePhase.ControlRecoveryAdmission : BladeWriteFailurePhase.Unspecified,
            exception.Message, exception);
    }

    // Admission retains the finite validation-run budget. A live serialized
    // session cycles IDs only after completed exchanges, like the production
    // lighting ports; ambiguity poisons the session and cannot be replayed.
    // Product 672's capture cycles 00..1F. Zero is reserved by the session API.
    private TransactionIdAssignment AllocateTransaction()
    {
        if (!queryRecordingEnabled)
        {
            return context.TryAllocateTransaction(out byte id)
                && (model.ThermalPolicy is null || id <= model.ThermalPolicy.MaximumTransactionId)
                ? TransactionIdAssignment.Valid(id) : TransactionIdAssignment.Exhausted();
        }
        int maximum = model.ThermalPolicy?.MaximumTransactionId ?? (model.ProductId == 0x02A0 ? 0x1F : byte.MaxValue);
        liveTransactionId = liveTransactionId >= maximum ? 1 : liveTransactionId + 1;
        return TransactionIdAssignment.Valid((byte)liveTransactionId);
    }

    private async Task<ReadOnlyMemory<byte>> ExchangeAsync(string operation, byte commandClass,
        byte commandId, byte[] arguments, bool write, CancellationToken cancellationToken,
        bool variableResponse = false, Func<ReadOnlyMemory<byte>, bool>? payloadValidator = null,
        bool requireEcho = true, bool recordAcknowledgedSubmission = true,
        Func<bool>? writeAdmission = null,
        byte? fixedTransactionId = null,
        bool requireFullPowerForFanCurve = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        PowerSource? admittedFanSource = null;
        PowerSource? admittedFanPowerClass = null;
        PowerSource? admittedThermalSource = write && IsThermalSet(commandClass, commandId)
            && !IsTemporaryFullChargeSet(operation, commandClass, commandId, arguments)
                ? capturePowerSource() : null;
        if (write && IsThermalSet(commandClass, commandId)
            && !IsTemporaryFullChargeSet(operation, commandClass, commandId, arguments)
            && (!IsBatteryModeSet(commandClass, commandId, arguments)
                || model.ProductId != 0x02C5 || admittedThermalSource != PowerSource.Battery
                || operation != "Performance"))
        {
            AdapterPowerState adapter = await ReadAdapterPowerCoreAsync(cancellationToken).ConfigureAwait(false);
            bool fanOperation = IsFanOperation(operation);
            PowerSource currentSource = capturePowerSource();
            // Lock the actual classified source through native submission; limited
            // external power is admitted without relabeling it as full power.
            if (admittedThermalSource != currentSource)
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "The power source changed during thermal write admission.");
            }
            PowerSource? fanPowerClass = ResolveThermalPowerClass(adapter, currentSource);
            bool confirmedExternalPower = ExternalPowerReported(currentSource)
                && fanPowerClass is not null;
            // Classify raw Windows AC presence using this fresh adapter read,
            // while preserving each model's independent fan-source admission.
            // Host curves retain their exact source's full-adapter prerequisite;
            // performance mode selection is independent of this fan-only gate.
            bool fanPowerAllowed = fanOperation && fanPowerClass is { } resolvedFanPower
                && (!requireFullPowerForFanCurve || HyperBoostValidationPower.IsAdmittedFullPower(adapter))
                && FanCommandSourceIsAdmitted(resolvedFanPower, operation, arguments);
            if (fanPowerAllowed)
            {
                admittedFanSource = currentSource;
                admittedFanPowerClass = fanPowerClass;
            }
            if (operation == "HyperBoostRestore" && fanPowerClass is null)
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "The HyperBoost exit adapter reading disagrees with the current power source.");
            }
            bool sourceReviewedBatteryPreset = model.ThermalPolicy is not null
                && operation == "Performance" && IsBatteryModeSet(commandClass, commandId, arguments)
                && currentSource == PowerSource.Battery && adapter == AdapterPowerState.Battery;
            if (model.ThermalPolicy is not null && operation == "Performance"
                && IsBatteryModeSet(commandClass, commandId, arguments)
                && !sourceReviewedBatteryPreset && !confirmedExternalPower)
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "Battery performance requires agreement between Windows power and the fresh firmware adapter reading.");
            }
            bool sourceReviewedMaximumFansDisable =
                IsSourceReviewedMaximumFansDisable(operation, commandClass, commandId, arguments);
            bool sourceReviewedBatteryMaximumFansCleanup = sourceReviewedMaximumFansDisable
                && currentSource == PowerSource.Battery && adapter == AdapterPowerState.Battery;
            if (sourceReviewedMaximumFansDisable && currentSource == PowerSource.Battery
                && !sourceReviewedBatteryMaximumFansCleanup)
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "Maximum Fans battery cleanup requires agreement between Windows power and the fresh firmware adapter reading.");
            }
            bool batteryHyperBoostCleanup = operation == "HyperBoostRestore"
                && currentSource == PowerSource.Battery && adapter == AdapterPowerState.Battery
                && commandClass == 0x0D && commandId == 0x02
                && arguments.Length == 4 && arguments[2] == 6 && arguments[3] == 0;
            if (fanOperation && !fanPowerAllowed
                || !fanOperation
                    && !confirmedExternalPower && !batteryHyperBoostCleanup && !sourceReviewedBatteryPreset
                    && !sourceReviewedBatteryMaximumFansCleanup)
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "The adapter does not admit this thermal write on the current power source.");
            }
        }
        var command = new RazerCommandSpec(commandClass, commandId, arguments, arguments.Length, payloadValidator)
        {
            // Evaluated at the native submission boundary, including after queueing.
            AllowVariableResponseLength = variableResponse,
            SubmissionAllowed = write ? () => policy.WritesAllowed && exactHostStillPresent()
                && additionalWriteAdmission?.Invoke() != false
                && (admittedThermalSource is null || capturePowerSource() == admittedThermalSource)
                && writeAdmission?.Invoke() != false
                && ThermalSubmissionAllowed(
                    operation,
                    commandClass,
                    commandId,
                    arguments,
                    admittedFanSource,
                    admittedFanPowerClass) : null,
        };
        LightingRecoveryWriteGate.SubmissionGuard? recoveryGuard = write
            ? recoveryWriteGate.CreateSubmissionGuard(command.SubmissionAllowed) : null;
        if (recoveryGuard is not null)
        {
            command = command with { SubmissionAllowed = recoveryGuard.Allow };
        }
        HidSessionResult<ReadOnlyMemory<byte>> result;
        try
        {
            result = await session.ExecuteAsync(command,
                // Fixed vendor framing applies only to the continuous production
                // session. Developer runs retain their finite, unique-ID allocator.
                queryRecordingEnabled && fixedTransactionId is { } fixedId
                    ? () => TransactionIdAssignment.Valid(fixedId) : AllocateTransaction,
                TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (write)
        {
            throw new BladeWriteException(WriteSubmissionState.Indeterminate,
                "Experimental write failed after submission became possible.", exception);
        }
        recoveryGuard?.ThrowIfRefused(result.Diagnostic);
        if (result.Failure is not null)
        {
            bool possiblySubmitted = write && result.Diagnostic?.Phase != HidSessionFailurePhase.Guard
                && result.Failure is not (HidSessionFailureKind.QueuedCanceled or HidSessionFailureKind.Exhausted
                    or HidSessionFailureKind.Disposed or HidSessionFailureKind.Poisoned);
            string failure = $"{result.Failure}/{result.Diagnostic?.Phase}/{result.Diagnostic?.ProtocolValidationFailure}";
            if (write)
            {
                policy.Record(operation, "Submission", "Failed", possiblySubmitted, failure: failure);
            }
            else
            {
                RecordQuery(operation, "Failed", failure: failure);
            }
            if (write)
            {
                throw new BladeWriteException(possiblySubmitted ? WriteSubmissionState.Indeterminate
                    : WriteSubmissionState.KnownNotApplied,
                    $"Experimental {operation} exchange failed: {result.Failure}.");
            }
            throw new NativeDeviceReadException(result.Failure.Value, result.Diagnostic,
                ReadQueryFor(operation, commandClass, commandId, arguments), result.NativeRetirement);
        }
        if ((!variableResponse && result.Value.Length != arguments.Length)
            || payloadValidator is not null && !payloadValidator(result.Value)
            || write && requireEcho && !result.Value.Span.SequenceEqual(arguments))
        {
            if (write)
            {
                throw new BladeWriteException(WriteSubmissionState.Indeterminate,
                    "Experimental response did not match the command contract.");
            }
            throw new IOException("Experimental response did not match the command contract.");
        }
        if (write && recordAcknowledgedSubmission)
        {
            policy.Record(operation, "Submission", "Acknowledged", true);
        }
        return result.Value;
    }

    private static DeviceStateQuery? ReadQueryFor(
        string operation, byte commandClass, byte commandId, byte[] arguments) =>
        (commandClass, commandId, arguments) switch
        {
            (0x07, 0x92, [0]) => DeviceStateQuery.BatteryProtection,
            (0x07, 0x8F, [0]) when operation == "TemporaryFullCharge" => DeviceStateQuery.TemporaryFullCharge,
            (0x07, 0x8F, [0]) when operation == "MaximumFans" => DeviceStateQuery.PerformanceAndFans,
            (0x0D, 0x82, [0 or 1, 1, 0, 0]) => DeviceStateQuery.ThermalChannelOne,
            (0x0D, 0x82, [0 or 1, 2, 0, 0]) => DeviceStateQuery.ThermalChannelTwo,
            (0x0D, 0x81, [0 or 1, 1 or 2, 0]) => DeviceStateQuery.PerformanceAndFans,
            (0x0D, 0x83, [0, ..]) => DeviceStateQuery.PerformanceAndFans,
            (0x0D, 0x87, [0 or 1, 1, 0]) => DeviceStateQuery.CustomCpuPerformance,
            (0x0D, 0x87, [0 or 1, 2, 0]) => DeviceStateQuery.CustomGpuPerformance,
            _ => null,
        };

    public async Task<AdapterPowerState> QueryAdapterPowerStateAsync(CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return await ReadAdapterPowerCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<AdapterPowerState> ReadAdapterPowerCoreAsync(CancellationToken cancellationToken)
    {
        // Every admitted profile pins its own enabled Synapse command path.
        // A future profile without that evidence cannot inherit the query.
        if (model.AdapterPowerEvidenceSource is null)
        {
            RecordQuery("AdapterPower", "Unavailable", failure: "MissingVendorCommandEvidence");
            return AdapterPowerState.Unknown;
        }
        TransactionIdAssignment assignment = AllocateTransaction();
        if (assignment.Failure is not null)
        {
            throw new IOException("The experimental adapter query exhausted its transaction budget.");
        }
        byte transaction = assignment.TransactionId;
        HidSessionResult<AdapterPowerState> result = await adapterPowerCommands.QueryAsync(
            transaction, cancellationToken).ConfigureAwait(false);
        if (result.Failure is { } failure)
        {
            RecordQuery("AdapterPower", "Failed", failure: failure.ToString());
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException($"Experimental adapter query failed: {failure}.");
        }
        RecordQuery("AdapterPower", result.Value == AdapterPowerState.Unknown ? "UnsupportedState" : "Confirmed",
            confirmed: result.Value.ToString());
        return result.Value;
    }

    private static bool IsThermalSet(byte commandClass, byte commandId) =>
        commandClass == 0x0D && commandId is 0x01 or 0x02 or 0x07
        || commandClass == 0x07 && commandId == 0x0F;

    private static bool IsBatteryModeSet(byte commandClass, byte commandId, byte[] arguments) =>
        commandClass == 0x0D && commandId == 0x02 && arguments.Length == 4 && arguments[2] is 3 or 6;

    private static bool IsLimitedAdapter(AdapterPowerState state) => state is
        AdapterPowerState.LimitedExternal or AdapterPowerState.UsbCPower45W or AdapterPowerState.UsbCPower60W
        or AdapterPowerState.UsbCPower65W or AdapterPowerState.UsbCPower80W or AdapterPowerState.UsbCPower90W
        or AdapterPowerState.UsbCPower100W;

    private static bool IsFanOperation(string operation) =>
        operation is "Fans" or "FanTargets";

    private bool FanModeSourceIsAdmitted(PowerSource source, FanMode mode)
    {
        if (mode == FanMode.ManualCurve && source is not (PowerSource.PluggedIn or PowerSource.ExternalUnknown)
            && (model.ProductId == 0x02A0 || SupportsStaticSourceHostCurves(model)))
        {
            return false;
        }
        // Exact source normally offers manual control only on AC; the reviewed
        // 709/711 Balanced-DC paths separately admit fixed targets on battery.
        if (UsesDynamicExperimentalFanRange(model) && source is not (PowerSource.PluggedIn or PowerSource.ExternalUnknown)
            && (mode == FanMode.ManualCurve
                || mode == FanMode.Fixed && !(source == PowerSource.Battery && SupportsDynamicBatteryFixedFans(model))))
        {
            return false;
        }
        return source switch
        {
            PowerSource.PluggedIn or PowerSource.ExternalUnknown or PowerSource.Battery =>
                mode is FanMode.Automatic or FanMode.Fixed or FanMode.ManualCurve,
            PowerSource.UsbC => mode == FanMode.Automatic,
            _ => false,
        };
    }

    private bool FanCommandSourceIsAdmitted(
        PowerSource source,
        string operation,
        byte[] arguments) => (!UsesDynamicExperimentalFanRange(model)
                || source == PowerSource.PluggedIn
                || source == PowerSource.Battery && SupportsDynamicBatteryFixedFans(model)
                    && (operation == "FanTargets"
                        || operation == "Fans" && arguments.Length == 4 && arguments[2] == 6)
                || operation == "Fans" && arguments.Length == 4 && arguments[3] == 0)
            && (source != PowerSource.UsbC
                || operation == "Fans" && arguments.Length == 4 && arguments[3] == 0);

    // Windows ACLineStatus establishes only external-power presence. Resolve
    // that raw state separately using the successful fresh firmware reading for
    // this SET. Keep the raw source unchanged in the native submission guard.
    private static PowerSource? ResolveThermalPowerClass(AdapterPowerState adapter, PowerSource rawSource) =>
        rawSource == PowerSource.ExternalUnknown
            ? HyperBoostValidationPower.IsAdmittedFullPower(adapter) ? PowerSource.PluggedIn
                : IsLimitedAdapter(adapter) ? PowerSource.UsbC : null
            : FanPowerMatchesSource(adapter, rawSource) ? rawSource : null;

    private static bool FanPowerMatchesSource(
        AdapterPowerState adapter,
        PowerSource source) => source switch
        {
            PowerSource.PluggedIn => HyperBoostValidationPower.IsAdmittedFullPower(adapter),
            PowerSource.UsbC => IsLimitedAdapter(adapter),
            PowerSource.ExternalUnknown => HyperBoostValidationPower.IsAdmittedFullPower(adapter) || IsLimitedAdapter(adapter),
            PowerSource.Battery => adapter == AdapterPowerState.Battery,
            _ => false,
        };

    private static Task Unavailable() => Task.FromException(new CapabilityUnavailableException(
        "This control has no experimental implementation for the exact model."));

    public async ValueTask DisposeAsync()
    {
        await operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!disposed)
            {
                await StopAnimationCoreAsync().ConfigureAwait(false);
                KeyboardLightingFrameStream.Clear(this);
                disposed = true;
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            operationGate.Release();
        }
    }
}
