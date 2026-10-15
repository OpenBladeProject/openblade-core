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
    bool recordQueries = true) : IProductionBatteryPort, IProductionLightingPort
{
    private readonly SemaphoreSlim operationGate = new(1, 1);
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
            LightingSettings? lighting = null;
            LogoLightingMode? logo = null;
            if (model.ProductId == 0x02A0)
            {
                lighting = (await ReadKeyboardLightingAsync(cancellationToken).ConfigureAwait(false)).Settings;
                logo = await ReadLogoAsync(cancellationToken).ConfigureAwait(false);
            }
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
        new(enabled ? model.ProductId == 0x02A0
                ? new HashSet<PerformancePreset> { PerformancePreset.Balanced, PerformancePreset.Silent, PerformancePreset.Performance, PerformancePreset.Custom }
                : new HashSet<PerformancePreset> { PerformancePreset.Balanced, PerformancePreset.Silent } : [],
            new HashSet<FanMode>(), enabled && model.ProductId == 0x02A0
                ? new HashSet<LightingEffect> { LightingEffect.Off, LightingEffect.Spectrum, LightingEffect.Wave, LightingEffect.Breathing }
                : new HashSet<LightingEffect>(),
            enabled && model.SupportsBatteryProtection ? model.ProductId == 0x02A0
                ? Enumerable.Range(50, 51).ToHashSet() : new HashSet<int> { 80 } : [],
            enabled && model.SupportsBatteryProtection, false)
        {
            CustomPerformance = enabled && model.ProductId == 0x02A0 ? CreateCustomCapabilities() : null,
            SupportsKeyboardBrightness = enabled && model.ProductId == 0x02A0,
            LogoLightingModes = enabled && model.ProductId == 0x02A0
                ? new HashSet<LogoLightingMode>(Enum.GetValues<LogoLightingMode>()) : new HashSet<LogoLightingMode>(),
            SupportsBreathingColorSelection = model.ProductId != 0x02A0,
            SupportsWaveDirectionSelection = model.ProductId != 0x02A0,
            HyperBoostAccess = model.ProductId == 0x02A0 ? DeviceFeatureAccess.ObservedOnly : null,
            ObservedLightingEffects = model.ProductId == 0x02A0
                ? new HashSet<LightingEffect> { LightingEffect.Off, LightingEffect.Spectrum, LightingEffect.Wave, LightingEffect.Breathing }
                : new HashSet<LightingEffect>(),
        };

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
        bool hyperBoost = mode == 7 && model.ProductId == 0x02A0;
        PerformancePreset? performance = mode switch
        {
            0 => PerformancePreset.Balanced,
            4 => PerformancePreset.Custom,
            5 => PerformancePreset.Silent,
            3 when model.ProductId == 0x02C5 => PerformancePreset.BatterySaver,
            6 when model.ProductId == 0x02C5 => PerformancePreset.BatteryBalanced,
            1 when model.ProductId == 0x0255 => PerformancePreset.Performance,
            2 when model.ProductId == 0x02A0 => PerformancePreset.Performance,
            7 when model.ProductId == 0x02A0 => null,
            _ => UnsupportedPerformanceMode(first.Span),
        };
        FanMode fans = first.Span[3] == 0 ? FanMode.Automatic : FanMode.Fixed;
        CustomPerformanceSettings? custom = mode == 4 && model.ProductId == 0x02A0
            ? await ReadCustomPerformanceAsync(cancellationToken).ConfigureAwait(false) : null;
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
        if (value is not (PerformancePreset.Balanced or PerformancePreset.Silent)
            && !(value == PerformancePreset.Performance && model.ProductId == 0x02A0))
        {
            throw new CapabilityUnavailableException("This preset is outside the exact experimental model definition.");
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool submitted = false;
        try
        {
            RequireWriteAdmission(thermal: true);
            ProductionPerformanceFanState baseline = await ReadPerformanceAsync(cancellationToken).ConfigureAwait(false);
            policy.Record("Performance", "Baseline", "Confirmed", requested: value.ToString(),
                confirmed: $"{(baseline.HyperBoostActive ? "HyperBoost" : baseline.Performance?.ToString())}/{baseline.Fans}");
            // Mode entry also writes fan flag 0 (Automatic). This backend cannot
            // restore a manual-fan baseline, so reject it before submitting a mode SET.
            if (baseline.Fans != FanMode.Automatic)
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "Experimental performance changes require confirmed Automatic fans.");
            }
            if (baseline.Performance is PerformancePreset.BatterySaver or PerformancePreset.BatteryBalanced)
            {
                throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                    "Battery performance codes are not admitted by this experimental backend.");
            }
            if (baseline.Performance == value)
            {
                return;
            }
            byte mode = value switch
            {
                PerformancePreset.Balanced => 0,
                PerformancePreset.Silent => 5,
                PerformancePreset.Performance => 2,
                _ => throw new CapabilityUnavailableException("This preset has no experimental wire value."),
            };
            foreach (byte selector in new byte[] { 1, 2 })
            {
                RequireWriteAdmission(thermal: true);
                await ExchangeAsync("Performance", 0x0D, 0x02, [1, selector, mode, 0],
                    true, cancellationToken).ConfigureAwait(false);
                submitted = true;
            }
            ProductionPerformanceFanState confirmed = await ReadPerformanceAsync(cancellationToken).ConfigureAwait(false);
            if (confirmed.Performance != value || confirmed.Fans != FanMode.Automatic)
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

    private void RequireWriteAdmission(bool thermal = false)
    {
        if (!policy.WritesAllowed || !exactHostStillPresent() || additionalWriteAdmission?.Invoke() == false
            || thermal && !ExternalPowerPresent())
        {
            throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                "Experimental exact identity or recovery admission blocked the write.");
        }
    }

    private bool ExternalPowerPresent() => capturePowerSource() is
        PowerSource.ExternalUnknown or PowerSource.PluggedIn or PowerSource.UsbC;

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
        bool requireEcho = true)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var command = new RazerCommandSpec(commandClass, commandId, arguments, arguments.Length, payloadValidator)
        {
            // Evaluated at the native submission boundary, including after queueing.
            AllowVariableResponseLength = variableResponse,
            SubmissionAllowed = write ? () => policy.WritesAllowed && exactHostStillPresent()
                && additionalWriteAdmission?.Invoke() != false
                && (commandClass != 0x0D || ExternalPowerPresent()) : null,
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
        if (write)
        {
            policy.Record(operation, "Submission", "Acknowledged", true);
        }
        return result.Value;
    }

    public Task<bool?> QueryTemporaryFullChargeAsync(CancellationToken cancellationToken) => Task.FromResult<bool?>(null);
    public Task<AdapterPowerState> QueryAdapterPowerStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PowerSource power = capturePowerSource();
        return Task.FromResult(power switch
        {
            PowerSource.Battery => AdapterPowerState.Battery,
            PowerSource.ExternalUnknown or PowerSource.PluggedIn or PowerSource.UsbC =>
                AdapterPowerState.ExperimentalExternal,
            _ => AdapterPowerState.Unknown,
        });
    }
    public Task SetFanModeAsync(FanMode value, CancellationToken cancellationToken) => Unavailable();
    public Task SetFanControlAsync(FanControlSettings value, CancellationToken cancellationToken) => Unavailable();
    public Task SetFanTargetsAsync(int fan1Rpm, int fan2Rpm, CancellationToken cancellationToken) => Unavailable();
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
