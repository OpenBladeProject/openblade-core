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
    ICoolingPadDiscoverySource? coolingPadDiscovery = null)
    : IProductionBatteryPort, IProductionLightingPort
{
    private readonly SemaphoreSlim operationGate = sharedOperationGate ?? new(1, 1);
    private readonly AdapterPowerCommandClient adapterPowerCommands = new(session, TimeSpan.FromSeconds(3));
    private readonly ExperimentalLightingOwnershipState lightingOwnership =
        sharedLightingOwnership ?? new();
    private readonly Func<TimeSpan, CancellationToken, Task> delayConfirmation = confirmationDelay ?? Task.Delay;
    private bool disposed;
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
            ProductionPerformanceFanState performance = await ReadPerformanceAsync(cancellationToken).ConfigureAwait(false);
            if (performance.Performance is null && !performance.HyperBoostActive || performance.Fans is null)
            {
                throw new IOException("Experimental performance readback cannot form a confirmed recovery state.");
            }
            ConfirmedBatteryState? battery = model.SupportsBatteryProtection
                ? await ReadBatteryAsync(cancellationToken).ConfigureAwait(false) : null;
            if (battery is not null)
            {
                battery = new ConfirmedBatteryState(battery.ProtectionEnabled, battery.ChargeLimitPercent, false);
            }
            (LightingSettings? lighting, LogoLightingMode? logo) =
                await ReadRecoveryLightingStateAsync(cancellationToken).ConfigureAwait(false);
            return new(performance.Performance, performance.Fans, battery, lighting)
            {
                HyperBoostActive = performance.HyperBoostActive,
                CustomPerformance = performance.CustomPerformance,
                LogoLighting = logo,
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

    private Func<bool>? additionalWriteAdmission;

    internal void SetAdditionalWriteAdmission(Func<bool> admission) => additionalWriteAdmission = admission;

    public DeviceCapabilities Capabilities => CreateCapabilities(model, policy.WritesAllowed);

    internal static DeviceCapabilities CreateCapabilities(ExperimentalBladeModel model, bool enabled) =>
        new(enabled ? CreatePerformancePresets(model) : new HashSet<PerformancePreset>(),
            enabled && GetExperimentalFanRange(model) is not null
                ? new HashSet<FanMode> { FanMode.Automatic, FanMode.Fixed } : [],
            CreateExperimentalLightingEffects(model, enabled),
            enabled ? GetExperimentalChargeLimits(model) : new HashSet<int>(),
            enabled && GetExperimentalChargeLimits(model).Count > 0, false)
        {
            CustomPerformance = enabled && SupportsExperimentalCustom(model) ? CreateCustomCapabilities(model) : null,
            FanSpeedRange = enabled ? GetExperimentalFanRange(model) : null,
            SupportsDirectManualFanTransitions = enabled && GetExperimentalFanRange(model) is not null,
            // 029F/029D use separate mode/fan bytes in 0D82 and independent
            // Custom getters, unlike validated backends' shared manual selector.
            // See sqmagellan 9c66e705/librazer/src/command.rs and descriptor.rs.
            ReportsPerformanceWithManualFans = enabled && GetExperimentalFanRange(model) is not null,
            SupportsKeyboardBrightness = SupportsExperimentalKeyboardBrightness(model, enabled),
            LogoLightingModes = CreateExperimentalLogoModes(model, enabled),
            SupportsBreathingColorSelection = false,
            SupportedBreathingColorModes =
                CreateExperimentalBreathingColorModes(model, enabled),
            SupportsWaveDirectionSelection =
                SupportsExperimentalWaveDirectionSelection(model),
            SupportsUniformLightingFrames =
                SupportsExperimentalUniformFrames(model, enabled),
            HyperBoostAccess = model.ProductId == 0x029F && enabled ? DeviceFeatureAccess.ReadWrite
                : model.ProductId is 0x02A0 or 0x029F ? DeviceFeatureAccess.ObservedOnly : null,
            HyperBoostWithoutCoolingPadAccess = model.ProductId == 0x029F && enabled ? DeviceFeatureAccess.ReadWrite : null,
            HyperBoostBaseModes = model.ProductId == 0x029F && enabled
                ? new(DeviceFeatureAccess.ReadWrite, DeviceFeatureAccess.ReadWrite, DeviceFeatureAccess.ReadWrite)
                {
                    Custom = DeviceFeatureAccess.ReadWrite,
                } : null,
            ObservedLightingEffects = CreateExperimentalLightingEffects(model, true),
        };

    private static bool SupportsExperimentalCustom(ExperimentalBladeModel model) =>
        model.ProductId is 0x029F or 0x029D or 0x02C5 or 0x0255 or 0x02A0;

    private static IReadOnlySet<PerformancePreset> CreatePerformancePresets(ExperimentalBladeModel model)
    {
        if (!SupportsExperimentalCustom(model))
        {
            return new HashSet<PerformancePreset>();
        }
        var presets = new HashSet<PerformancePreset>
        {
            PerformancePreset.Balanced, PerformancePreset.Silent, PerformancePreset.Custom,
        };
        // 0255 Gaming uses code 1. Code 2 has direct 029F/02A0 source evidence;
        // do not infer that distinct preset for the two AMD Blade 14 profiles.
        if (model.ProductId is 0x0255 or 0x029F or 0x02A0)
        {
            presets.Add(PerformancePreset.Performance);
        }
        if (model.ProductId == 0x02C5)
        {
            presets.UnionWith([PerformancePreset.BatterySaver, PerformancePreset.BatteryBalanced]);
        }
        return presets;
    }

    public async Task<ProductionPerformanceFanState> QueryPerformanceFanStateAsync(CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadPerformanceAsync(cancellationToken).ConfigureAwait(false);
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
        ReadOnlyMemory<byte> first = await ExchangeAsync("Performance", 0x0D, 0x82,
            [0, 1, 0, 0], false, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> second = await ExchangeAsync("Performance", 0x0D, 0x82,
            [0, 2, 0, 0], false, cancellationToken).ConfigureAwait(false);
        if (first.Span[1] != 1 || second.Span[1] != 2)
        {
            RecordQuery("Performance", "InconsistentState", failure: "UnexpectedSelector");
            throw new IOException("Experimental performance reply did not match its requested selector.");
        }
        return (first, second);
    }

    private async Task<ProductionPerformanceFanState> ReadPerformanceAsync(CancellationToken cancellationToken)
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
        bool hyperBoost = mode == 7 && model.ProductId is 0x02A0 or 0x029F;
        PerformancePreset? performance = mode switch
        {
            0 => PerformancePreset.Balanced,
            4 => PerformancePreset.Custom,
            5 => PerformancePreset.Silent,
            3 when model.ProductId == 0x02C5 => PerformancePreset.BatterySaver,
            6 when model.ProductId == 0x02C5 => PerformancePreset.BatteryBalanced,
            // Product 671 maps battery Balanced to raw 6 (AC Balanced is 0).
            6 when model.ProductId == 0x029F => PerformancePreset.Balanced,
            1 when model.ProductId == 0x0255 => PerformancePreset.Performance,
            2 when model.ProductId is 0x02A0 or 0x029F => PerformancePreset.Performance,
            7 when model.ProductId is 0x02A0 or 0x029F => null,
            _ => UnsupportedPerformanceMode(first.Span),
        };
        FanMode fans = first.Span[3] == 0 ? FanMode.Automatic : FanMode.Fixed;
        CustomPerformanceSettings? custom = mode == 4 && SupportsExperimentalCustom(model)
            ? await ReadCustomPerformanceAsync(cancellationToken).ConfigureAwait(false) : null;
        if (fans == FanMode.Fixed && GetExperimentalFanRange(model) is not null)
        {
            await ReadFanTargetsAsync(cancellationToken).ConfigureAwait(false);
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
        try
        {
            RequirePerformanceWriteAdmission(value);
            ProductionPerformanceFanState baseline = await ReadPerformanceAsync(cancellationToken).ConfigureAwait(false);
            policy.Record("Performance", "Baseline", "Confirmed", requested: value.ToString(),
                confirmed: $"{(baseline.HyperBoostActive ? "HyperBoost" : baseline.Performance?.ToString())}/{baseline.Fans}");
            // Models without a target getter/setter cannot retain a restorable
            // manual baseline. Supported models decoded both targets above.
            if (baseline.Fans != FanMode.Automatic && GetExperimentalFanRange(model) is null)
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "Experimental performance changes require confirmed Automatic fans.");
            }
            if (baseline.Fans == FanMode.Fixed
                && (baseline.HyperBoostActive || baseline.CustomPerformance?.MaximumFans == true))
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "The fixed-fan baseline conflicts with a firmware fan override.");
            }
            (int First, int Second)? baselineTargets = baseline.Fans == FanMode.Fixed
                ? await ReadFanTargetsAsync(cancellationToken).ConfigureAwait(false) : null;
            if (baseline.Performance == value)
            {
                return;
            }
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
                        requireAdequateAdapter: value != PerformancePreset.Balanced).ConfigureAwait(false);
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
                    true, cancellationToken).ConfigureAwait(false);
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
        if (!policy.WritesAllowed || !exactHostStillPresent() || additionalWriteAdmission?.Invoke() == false
            || thermal && !ExternalPowerPresent()
            || fanMode is { } requestedFan
                && !FanModeSourceIsAdmitted(capturePowerSource(), requestedFan))
        {
            throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                "Experimental exact identity or recovery admission blocked the write.");
        }
    }

    private bool ExternalPowerPresent() => capturePowerSource() is
        PowerSource.ExternalUnknown or PowerSource.PluggedIn or PowerSource.UsbC;

    private void RequirePerformanceWriteAdmission(PerformancePreset value)
    {
        RequireWriteAdmission();
        bool batteryMode = value is PerformancePreset.BatterySaver or PerformancePreset.BatteryBalanced;
        if (batteryMode ? model.ProductId != 0x02C5 || capturePowerSource() != PowerSource.Battery
            : !ExternalPowerPresent())
        {
            throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                "The exact experimental preset is not admitted on the current power source.");
        }
    }

    private bool ThermalSubmissionAllowed(
        string operation,
        byte commandClass,
        byte commandId,
        byte[] arguments,
        PowerSource? admittedFanSource)
    {
        if (IsFanOperation(operation))
        {
            return admittedFanSource is { } source
                && capturePowerSource() == source
                && FanCommandSourceIsAdmitted(source, operation, arguments);
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
            return model.ProductId == 0x02C5 && capturePowerSource() == PowerSource.Battery;
        }
        return commandClass != 0x0D && !(commandClass == 0x07 && commandId == 0x0F)
            || ExternalPowerPresent();
    }

    private BladeWriteException MapWriteFailure(string operation, bool submitted, Exception exception)
    {
        submitted |= exception is BladeWriteException { SubmissionState: WriteSubmissionState.Indeterminate };
        if (submitted)
        {
            policy.RequireRecovery();
        }
        policy.Record(operation, "Failure", submitted ? "Indeterminate" : "NotApplied", submitted,
            failure: exception.GetType().Name);
        return new(submitted ? WriteSubmissionState.Indeterminate : WriteSubmissionState.KnownNotApplied,
            exception.Message, exception);
    }

    private async Task<ReadOnlyMemory<byte>> ExchangeAsync(string operation, byte commandClass,
        byte commandId, byte[] arguments, bool write, CancellationToken cancellationToken,
        bool variableResponse = false, Func<ReadOnlyMemory<byte>, bool>? payloadValidator = null,
        bool requireEcho = true, bool recordAcknowledgedSubmission = true,
        bool requireAdequateAdapter = false, Func<bool>? writeAdmission = null)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        PowerSource? admittedFanSource = null;
        if (write && IsThermalSet(commandClass, commandId)
            && (!IsBatteryModeSet(commandClass, commandId, arguments)
                || model.ProductId != 0x02C5 || operation == "HyperBoostRestore"))
        {
            AdapterPowerState adapter = await ReadAdapterPowerCoreAsync(cancellationToken).ConfigureAwait(false);
            bool fanOperation = IsFanOperation(operation);
            PowerSource currentSource = capturePowerSource();
            bool fanPowerAllowed = fanOperation
                && FanPowerMatchesSource(adapter, currentSource)
                && FanCommandSourceIsAdmitted(currentSource, operation, arguments);
            if (fanPowerAllowed)
            {
                admittedFanSource = currentSource;
            }
            bool limitedCleanupAllowed = !requireAdequateAdapter && IsLimitedAdapter(adapter)
                && AllowsLimitedPowerSet(operation, commandClass, commandId, arguments);
            if (operation == "HyperBoostRestore" && !FanPowerMatchesSource(adapter, currentSource))
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "The HyperBoost exit adapter reading disagrees with the current power source.");
            }
            bool batteryHyperBoostCleanup = operation == "HyperBoostRestore"
                && currentSource == PowerSource.Battery && adapter == AdapterPowerState.Battery
                && commandClass == 0x0D && commandId == 0x02
                && arguments.Length == 4 && arguments[2] == 6 && arguments[3] == 0;
            if (fanOperation && !fanPowerAllowed
                || !fanOperation
                    && !HyperBoostValidationPower.IsAdmittedFullPower(adapter)
                    && !limitedCleanupAllowed && !batteryHyperBoostCleanup)
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
                && writeAdmission?.Invoke() != false
                && ThermalSubmissionAllowed(
                    operation,
                    commandClass,
                    commandId,
                    arguments,
                    admittedFanSource) : null,
        };
        HidSessionResult<ReadOnlyMemory<byte>> result;
        try
        {
            result = await session.ExecuteAsync(command,
                () => context.TryAllocateTransaction(out byte transaction)
                    ? TransactionIdAssignment.Valid(transaction) : TransactionIdAssignment.Exhausted(),
                TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (write)
        {
            throw new BladeWriteException(WriteSubmissionState.Indeterminate,
                "Experimental write failed after submission became possible.", exception);
        }
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
            if (result.Diagnostic?.Phase == HidSessionFailurePhase.SharedGateAdmission)
            {
                throw new NativeDeviceReadException(result.Failure.Value, result.Diagnostic, stateQuery: null);
            }
            throw new IOException($"Experimental {operation} exchange failed: {result.Failure}.");
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

    public Task<bool?> QueryTemporaryFullChargeAsync(CancellationToken cancellationToken) => Task.FromResult<bool?>(null);
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
        if (!context.TryAllocateTransaction(out byte transaction))
        {
            throw new IOException("The experimental adapter query exhausted its transaction budget.");
        }
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

    private static bool FanModeSourceIsAdmitted(
        PowerSource source,
        FanMode mode) => source switch
        {
            PowerSource.PluggedIn or PowerSource.Battery =>
                mode is FanMode.Automatic or FanMode.Fixed,
            // The exact 029F/029D low-power vendor path admits Automatic
            // handback, but does not set the separate manual fan flag.
            PowerSource.UsbC => mode == FanMode.Automatic,
            _ => false,
        };

    private static bool FanCommandSourceIsAdmitted(
        PowerSource source,
        string operation,
        byte[] arguments) => source != PowerSource.UsbC
            || operation == "Fans"
                && arguments.Length == 4
                && arguments[3] == 0;

    private static bool FanPowerMatchesSource(
        AdapterPowerState adapter,
        PowerSource source) => source switch
        {
            PowerSource.PluggedIn => HyperBoostValidationPower.IsAdmittedFullPower(adapter),
            PowerSource.UsbC => IsLimitedAdapter(adapter),
            PowerSource.Battery => adapter == AdapterPowerState.Battery,
            _ => false,
        };

    private static bool AllowsLimitedPowerSet(string operation, byte commandClass, byte commandId, byte[] arguments) =>
        commandClass == 0x0D && commandId == 0x02 && arguments.Length == 4 && arguments[3] == 0
            // Automatic handback preserves a freshly confirmed mode before the
            // service selects Balanced. Do not force an intermediate preset.
            && (operation == "Fans" || (operation is "Performance" or "HyperBoostRestore") && arguments[2] == 0)
        || commandClass == 0x07 && commandId == 0x0F && operation == "MaximumFans"
            && arguments.Length == 1 && arguments[0] == 0;
    public Task SetTemporaryFullChargeAsync(bool enabled, CancellationToken cancellationToken) => Unavailable();
    public Task ApplyOneShotLaptopControlAsync(ApplyOneShotLaptopControl value, CancellationToken cancellationToken) => Unavailable();
    private static Task Unavailable() => Task.FromException(new CapabilityUnavailableException(
        "This control has no experimental implementation for the exact model."));

    public async ValueTask DisposeAsync()
    {
        await operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!disposed)
            {
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
