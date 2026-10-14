using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed class ExperimentalBladePort(
    IRazerCommandSession session,
    ValidationRunContext context,
    ExperimentalBladeModel model,
    ExperimentalFeaturesPolicy policy,
    Func<bool> exactHostStillPresent,
    Func<PowerSource> capturePowerSource,
    Func<TimeSpan, CancellationToken, Task>? confirmationDelay = null) : IProductionBatteryPort
{
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly Func<TimeSpan, CancellationToken, Task> delayConfirmation = confirmationDelay ?? Task.Delay;
    private bool disposed;

    public DeviceCapabilities Capabilities => CreateCapabilities(model, policy.WritesAllowed);

    internal static DeviceCapabilities CreateCapabilities(ExperimentalBladeModel model, bool enabled) =>
        new(enabled ? new HashSet<PerformancePreset> { PerformancePreset.Balanced, PerformancePreset.Silent } : [],
            new HashSet<FanMode>(), new HashSet<LightingEffect>(),
            enabled && model.SupportsBatteryProtection ? new HashSet<int> { 80 } : [],
            enabled && model.SupportsBatteryProtection, false);

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
        if (payload.Span[0] is not (0x50 or 0xD0))
        {
            throw new IOException("Experimental battery readback is outside the admitted 80%/off states.");
        }
        bool enabled = payload.Span[0] == 0xD0;
        policy.Record("BatteryProtection", "Query", "Confirmed", confirmed: enabled ? "80" : "Off");
        return new(enabled, enabled ? 80 : null, null);
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

    private async Task<ProductionPerformanceFanState> ReadPerformanceAsync(CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte> first = await ExchangeAsync("Performance", 0x0D, 0x82,
            [0, 1, 0, 0], false, cancellationToken).ConfigureAwait(false);
        ReadOnlyMemory<byte> second = await ExchangeAsync("Performance", 0x0D, 0x82,
            [0, 2, 0, 0], false, cancellationToken).ConfigureAwait(false);
        if (first.Span[1] != 1 || second.Span[1] != 2
            || first.Span[2] != second.Span[2] || first.Span[3] != second.Span[3]
            || first.Span[3] > 1)
        {
            throw new IOException("Experimental performance selectors returned inconsistent state.");
        }
        PerformancePreset performance = first.Span[2] switch
        {
            0 => PerformancePreset.Balanced,
            4 => PerformancePreset.Custom,
            5 => PerformancePreset.Silent,
            3 when model.ProductId == 0x02C5 => PerformancePreset.BatterySaver,
            6 when model.ProductId == 0x02C5 => PerformancePreset.BatteryBalanced,
            1 when model.ProductId == 0x0255 => PerformancePreset.Performance,
            _ => throw new IOException("Experimental performance mode is outside the upstream model definition."),
        };
        FanMode fans = first.Span[3] == 0 ? FanMode.Automatic : FanMode.Fixed;
        policy.Record("Performance", "Query", "Confirmed", confirmed: $"{performance}/{fans}");
        return new(performance, fans);
    }

    public async Task SetPerformanceAsync(PerformancePreset value, CancellationToken cancellationToken)
    {
        if (value is not (PerformancePreset.Balanced or PerformancePreset.Silent))
        {
            throw new CapabilityUnavailableException("Only upstream-derived Balanced and Silent presets are enabled.");
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool submitted = false;
        try
        {
            RequireWriteAdmission(thermal: true);
            ProductionPerformanceFanState baseline = await ReadPerformanceAsync(cancellationToken).ConfigureAwait(false);
            policy.Record("Performance", "Baseline", "Confirmed", requested: value.ToString(),
                confirmed: $"{baseline.Performance}/{baseline.Fans}");
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
            byte mode = value == PerformancePreset.Balanced ? (byte)0 : (byte)5;
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

    public Task SetChargeLimitAsync(int percent, CancellationToken cancellationToken) => percent == 80
        ? SetBatteryAsync(true, cancellationToken)
        : Task.FromException(new CapabilityUnavailableException("Only the upstream-derived 80% limit is enabled."));

    public Task SetProtectionOffAsync(CancellationToken cancellationToken) => SetBatteryAsync(false, cancellationToken);

    private async Task SetBatteryAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!model.SupportsBatteryProtection)
        {
            throw new CapabilityUnavailableException("Class 07 battery commands are not supported on this model.");
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool submitted = false;
        string requested = enabled ? "80" : "Off";
        try
        {
            RequireWriteAdmission();
            ConfirmedBatteryState baseline = await ReadBatteryAsync(cancellationToken).ConfigureAwait(false);
            policy.Record("BatteryProtection", "Baseline", "Confirmed", requested: requested,
                confirmed: baseline.ProtectionEnabled ? "80" : "Off");
            if (baseline.ProtectionEnabled == enabled)
            {
                return;
            }
            RequireWriteAdmission();
            await ExchangeAsync("BatteryProtection", 0x07, 0x12, [enabled ? (byte)0xD0 : (byte)0x50],
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
                if (confirmed.ProtectionEnabled == enabled)
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

    private void RequireWriteAdmission(bool thermal = false)
    {
        if (!policy.WritesAllowed || !exactHostStillPresent() || thermal && !ExternalPowerPresent())
        {
            throw new BladeWriteException(WriteSubmissionState.KnownNotApplied,
                "Experimental consent, exact identity, or recovery admission blocked the write.");
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
        byte commandId, byte[] arguments, bool write, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var command = new RazerCommandSpec(commandClass, commandId, arguments, arguments.Length)
        {
            // Evaluated at the native submission boundary, including after queueing.
            SubmissionAllowed = write ? () => policy.WritesAllowed && exactHostStillPresent()
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
            policy.Record(operation, write ? "Submission" : "Query", "Failed", possiblySubmitted,
                failure: $"{result.Failure}/{result.Diagnostic?.Phase}/{result.Diagnostic?.ProtocolValidationFailure}");
            if (write)
            {
                throw new BladeWriteException(possiblySubmitted ? WriteSubmissionState.Indeterminate
                    : WriteSubmissionState.KnownNotApplied,
                    $"Experimental {operation} exchange failed: {result.Failure}.");
            }
            throw new IOException($"Experimental {operation} exchange failed: {result.Failure}.");
        }
        if (result.Value.Length != arguments.Length
            || write && !result.Value.Span.SequenceEqual(arguments))
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
    public Task SetCustomPerformanceAsync(CustomPerformanceSettings value, CancellationToken cancellationToken) => Unavailable();
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
