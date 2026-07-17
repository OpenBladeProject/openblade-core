using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed class ProductionBatteryPort : IProductionBatteryPort
{
    internal const string Limit50Action = "battery-limit-50";
    internal const string Limit55Action = "battery-limit-55";
    internal const string Limit60Action = "battery-limit-60";
    internal const string Limit65Action = "battery-limit-65";
    internal const string Limit70Action = "battery-limit-70";
    internal const string Limit75Action = "battery-limit-75";
    internal const string Limit80Action = "battery-limit-80";
    internal const string ProtectionOffAction = "battery-protection-off";
    internal const string TemporaryEnableAction = "charge-to-100-enable";
    internal const string TemporaryCancelAction = "charge-to-100-cancel";

    private readonly IRazerCommandSession session;
    private readonly DeviceCommandCatalog catalog;
    private readonly ProductionBatteryReadEvidence readEvidence;
    private readonly ValidationRunContext context;
    private readonly TimeSpan exchangeTimeout;
    private readonly BatteryCommandClient batteryCommands;
    private readonly TemporaryFullChargeCommandClient temporaryCommands;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly object lifecycleSync = new();
    private readonly IAsyncDisposable ownedSession;
    private bool closing;
    private bool disposed;
    private bool poisoned;
    private Task? disposalTask;

    internal ProductionBatteryPort(
        IRazerCommandSession session,
        DeviceCommandCatalog catalog,
        ProductionBatteryReadEvidence readEvidence,
        ValidationRunContext context,
        TimeSpan exchangeTimeout)
        : this(session, session, catalog, readEvidence, context, exchangeTimeout)
    {
    }

    internal ProductionBatteryPort(
        ValidationActor actor,
        DeviceCommandCatalog catalog,
        ProductionBatteryReadEvidence readEvidence,
        ValidationRunContext context,
        TimeSpan exchangeTimeout)
        : this(actor.Session, actor, catalog, readEvidence, context, exchangeTimeout)
    {
    }

    private ProductionBatteryPort(
        IRazerCommandSession session,
        IAsyncDisposable ownedSession,
        DeviceCommandCatalog catalog,
        ProductionBatteryReadEvidence readEvidence,
        ValidationRunContext context,
        TimeSpan exchangeTimeout)
    {
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        this.ownedSession = ownedSession ?? throw new ArgumentNullException(nameof(ownedSession));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.readEvidence = readEvidence ?? throw new ArgumentNullException(nameof(readEvidence));
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        this.exchangeTimeout = exchangeTimeout;
        batteryCommands = new BatteryCommandClient(session, exchangeTimeout);
        temporaryCommands = new TemporaryFullChargeCommandClient(session, exchangeTimeout);
        Capabilities = CreateCapabilities(catalog, readEvidence);
    }

    public DeviceCapabilities Capabilities { get; }

    public bool SupportsTemporaryFullChargeQuery =>
        readEvidence.SupportsTemporaryFullChargeQuery;

    public async Task<ConfirmedBatteryState> QueryProtectionAsync(
        CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            byte transactionId = AllocateTransactionForRead();
            HidSessionResult<BatteryValidationSetting> result =
                await batteryCommands.QueryAsync(transactionId, cancellationToken)
                    .ConfigureAwait(false);
            if (result.Failure is not null)
            {
                ThrowReadFailure(result.Failure.Value, result.Diagnostic, cancellationToken);
            }

            return MapProtectionState(result.Value);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<bool?> QueryTemporaryFullChargeAsync(
        CancellationToken cancellationToken)
    {
        if (!readEvidence.SupportsTemporaryFullChargeQuery)
        {
            ThrowIfClosingOrDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            byte transactionId = AllocateTransactionForRead();
            HidSessionResult<bool> result = await temporaryCommands.QueryAsync(
                transactionId,
                cancellationToken).ConfigureAwait(false);
            if (result.Failure is not null)
            {
                ThrowReadFailure(result.Failure.Value, result.Diagnostic, cancellationToken);
            }

            return result.Value;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<ProductionPerformanceFanState> QueryPerformanceFanStateAsync(
        CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureUsable();
            ReadOnlyMemory<byte> first = await QueryThermalChannelAsync(
                ThermalChannel.First,
                cancellationToken).ConfigureAwait(false);
            ReadOnlyMemory<byte> second = await QueryThermalChannelAsync(
                ThermalChannel.Second,
                cancellationToken).ConfigureAwait(false);

            if (!ThermalModeProtocol.TryDecodeMatchingPair(
                    first.Span,
                    second.Span,
                    out ThermalModeState state))
            {
                poisoned = true;
                throw new RazerProtocolException(
                    "Thermal channels did not report one recognized matching state.",
                    HidProtocolValidationFailure.PayloadValidator);
            }

            return state switch
            {
                ThermalModeState.BalancedAutomatic => new(
                    PerformancePreset.Balanced,
                    FanMode.Automatic),
                ThermalModeState.SilentAutomatic => new(
                    PerformancePreset.Silent,
                    FanMode.Automatic),
                ThermalModeState.PerformanceAutomatic => new(
                    PerformancePreset.Performance,
                    FanMode.Automatic),
                ThermalModeState.FixedMaximum => new(null, FanMode.Maximum),
                _ => throw new RazerProtocolException(
                    "Thermal query returned an undefined state.",
                    HidProtocolValidationFailure.PayloadValidator),
            };
        }
        finally
        {
            operationGate.Release();
        }
    }

    public Task SetChargeLimitAsync(int percent, CancellationToken cancellationToken)
    {
        string? action = ChargeLimitAction(percent);
        if (action is null)
        {
            return Task.FromException(KnownNotApplied(
                $"Charge limit {percent}% is not admitted for production use."));
        }

        return ExecuteSetterAsync(action, cancellationToken);
    }

    public Task SetProtectionOffAsync(CancellationToken cancellationToken)
    {
        if (!readEvidence.SupportsTemporaryFullChargeQuery)
        {
            return Task.FromException(KnownNotApplied(
                "Battery protection-off is unavailable without admitted temporary-state readback."));
        }

        return ExecuteSetterAsync(ProtectionOffAction, cancellationToken);
    }

    public Task SetTemporaryFullChargeAsync(
        bool enabled,
        CancellationToken cancellationToken)
    {
        if (!Capabilities.SupportsTemporaryFullCharge)
        {
            return Task.FromException(KnownNotApplied(
                "Temporary full charge requires admitted enable, cancel, and readback commands."));
        }

        return ExecuteSetterAsync(
            enabled ? TemporaryEnableAction : TemporaryCancelAction,
            cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        lock (lifecycleSync)
        {
            if (disposalTask is null)
            {
                closing = true;
                disposalTask = DisposeCoreAsync();
            }

            return new ValueTask(disposalTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ownedSession.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (lifecycleSync)
            {
                disposed = true;
            }

            operationGate.Release();
        }
    }

    internal static DeviceCapabilities CreateCapabilities(
        DeviceCommandCatalog catalog,
        ProductionBatteryReadEvidence readEvidence)
    {
        var limits = new HashSet<int>();
        foreach ((int percent, string action) in ChargeLimitActions)
        {
            if (catalog.TryGet(action, out _))
            {
                limits.Add(percent);
            }
        }

        bool protectionOff = readEvidence.SupportsTemporaryFullChargeQuery
            && catalog.TryGet(ProtectionOffAction, out _);
        bool temporaryFullCharge = readEvidence.SupportsTemporaryFullChargeQuery
            && catalog.TryGet(TemporaryEnableAction, out _)
            && catalog.TryGet(TemporaryCancelAction, out _);
        return new DeviceCapabilities(
            new HashSet<PerformancePreset>(),
            new HashSet<FanMode>(),
            new HashSet<LightingEffect>(),
            limits,
            protectionOff,
            SupportsTemporaryFullCharge: temporaryFullCharge);
    }

    private static readonly (int Percent, string Action)[] ChargeLimitActions =
    [
        (50, Limit50Action),
        (55, Limit55Action),
        (60, Limit60Action),
        (65, Limit65Action),
        (70, Limit70Action),
        (75, Limit75Action),
        (80, Limit80Action),
    ];

    private static string? ChargeLimitAction(int percent)
    {
        foreach ((int admittedPercent, string action) in ChargeLimitActions)
        {
            if (admittedPercent == percent)
            {
                return action;
            }
        }

        return null;
    }

    private async Task ExecuteSetterAsync(
        string action,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw KnownNotApplied("The battery write was canceled before submission.");
        }

        if (!catalog.TryGet(action, out RazerCommandDefinition? command) || command is null)
        {
            throw KnownNotApplied($"Battery action '{action}' is not admitted for production use.");
        }

        await EnterForWriteAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            EnsureUsableForWrite();
            HidSessionResult<ReadOnlyMemory<byte>> result = await session.ExecuteAsync(
                new RazerCommandSpec(
                    command.CommandClass,
                    command.CommandId,
                    command.Payload.ToArray(),
                    command.Payload.Length,
                    payload => payload.Span.SequenceEqual(command.Payload.AsSpan())),
                AllocateTransaction,
                exchangeTimeout,
                cancellationToken).ConfigureAwait(false);

            if (result.Failure is not null)
            {
                BladeWriteException failure = MapWriteFailure(
                    result.Failure.Value,
                    result.Diagnostic);
                if (failure.SubmissionState == WriteSubmissionState.Indeterminate)
                {
                    poisoned = true;
                }

                throw failure;
            }

            if (!result.Value.Span.SequenceEqual(command.Payload.AsSpan()))
            {
                poisoned = true;
                throw Indeterminate("The acknowledged battery payload did not match the admitted command.");
            }
        }
        catch (BladeWriteException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            poisoned = true;
            throw Indeterminate("The battery write was canceled after submission became possible.");
        }
        catch (Exception exception)
        {
            poisoned = true;
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "The battery write outcome is indeterminate.",
                exception);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private async Task<ReadOnlyMemory<byte>> QueryThermalChannelAsync(
        ThermalChannel channel,
        CancellationToken cancellationToken)
    {
        var command = new RazerCommandSpec(
            ThermalModeProtocol.CommandClass,
            ThermalModeProtocol.QueryCommandId,
            ThermalModeProtocol.CreateQueryPayload(channel),
            4,
            payload => ThermalModeProtocol.TryDecodeQuery(
                payload.Span,
                channel,
                out _));
        HidSessionResult<ReadOnlyMemory<byte>> result = await session.ExecuteAsync(
            command,
            AllocateTransaction,
            exchangeTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result.Failure is not null)
        {
            ThrowReadFailure(result.Failure.Value, result.Diagnostic, cancellationToken);
        }

        if (!ThermalModeProtocol.TryDecodeQuery(result.Value.Span, channel, out _))
        {
            poisoned = true;
            throw new RazerProtocolException(
                "Thermal query failed payload validation.",
                HidProtocolValidationFailure.PayloadValidator);
        }

        return result.Value;
    }

    private TransactionIdAssignment AllocateTransaction() =>
        context.TryAllocateTransaction(out byte transactionId)
            ? TransactionIdAssignment.Valid(transactionId)
            : TransactionIdAssignment.Exhausted();

    private byte AllocateTransactionForRead()
    {
        if (!context.TryAllocateTransaction(out byte transactionId))
        {
            throw new IOException("Battery query transaction identifiers are exhausted.");
        }

        return transactionId;
    }

    private async Task EnterAsync(CancellationToken cancellationToken)
    {
        Task waitForOperation;
        lock (lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(closing || disposed, this);
            waitForOperation = operationGate.WaitAsync(cancellationToken);
        }

        await waitForOperation.ConfigureAwait(false);
        try
        {
            ThrowIfClosingOrDisposed();
        }
        catch
        {
            operationGate.Release();
            throw;
        }
    }

    private async Task EnterForWriteAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnterAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw KnownNotApplied("The battery write was canceled before submission.");
        }
        catch (ObjectDisposedException)
        {
            throw KnownNotApplied("The battery write was blocked because the session is closing.");
        }
    }

    private void EnsureUsable()
    {
        ThrowIfClosingOrDisposed();
        if (poisoned || session.Disposition == RazerCommandSessionDisposition.RecoveryRequired)
        {
            throw new IOException("The battery control session requires a fresh verified reopen.");
        }
    }

    private void EnsureUsableForWrite()
    {
        try
        {
            EnsureUsable();
        }
        catch (ObjectDisposedException)
        {
            throw Indeterminate("The battery control session was disposed during the operation.");
        }
        catch (IOException)
        {
            throw Indeterminate("The battery control session is poisoned and must be reopened.");
        }
    }

    private void ThrowIfClosingOrDisposed()
    {
        lock (lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(closing || disposed, this);
        }
    }

    private void ThrowReadFailure(
        HidSessionFailureKind failure,
        HidSessionFailureDiagnostic? diagnostic,
        CancellationToken cancellationToken)
    {
        if (failure is not (
            HidSessionFailureKind.ConflictBlocked
            or HidSessionFailureKind.ConflictDetectionFailed
            or HidSessionFailureKind.QueuedCanceled))
        {
            poisoned = true;
        }

        switch (failure)
        {
            case HidSessionFailureKind.Canceled when cancellationToken.IsCancellationRequested:
            case HidSessionFailureKind.QueuedCanceled when cancellationToken.IsCancellationRequested:
                throw new OperationCanceledException(cancellationToken);
            case HidSessionFailureKind.Timeout:
                throw new TimeoutException("Battery state query timed out.");
            case HidSessionFailureKind.ProtocolFailure:
                throw new RazerProtocolException(
                    "Battery state query failed protocol validation.",
                    diagnostic?.ProtocolValidationFailure
                        ?? HidProtocolValidationFailure.PayloadValidator);
            default:
                throw new IOException("Battery state query failed closed.");
        }
    }

    private BladeWriteException MapWriteFailure(
        HidSessionFailureKind failure,
        HidSessionFailureDiagnostic? diagnostic)
    {
        bool knownNotApplied = failure is
            HidSessionFailureKind.ConflictBlocked
            or HidSessionFailureKind.ConflictDetectionFailed
            or HidSessionFailureKind.QueuedCanceled
            or HidSessionFailureKind.Exhausted
            || (failure == HidSessionFailureKind.Canceled
                && diagnostic is null
                && session.Disposition == RazerCommandSessionDisposition.Healthy)
            || (failure == HidSessionFailureKind.ProtocolFailure
                && diagnostic?.ProtocolValidationFailure == HidProtocolValidationFailure.Status);

        return new BladeWriteException(
            knownNotApplied
                ? WriteSubmissionState.KnownNotApplied
                : WriteSubmissionState.Indeterminate,
            knownNotApplied
                ? "The battery write was definitively not applied."
                : "The battery write outcome is indeterminate.");
    }

    private static BladeWriteException KnownNotApplied(string message) => new(
        WriteSubmissionState.KnownNotApplied,
        message);

    private static BladeWriteException Indeterminate(string message) => new(
        WriteSubmissionState.Indeterminate,
        message);

    private static ConfirmedBatteryState MapProtectionState(
        BatteryValidationSetting setting) => setting switch
        {
            BatteryValidationSetting.ProtectionOff => new(false, null, null),
            BatteryValidationSetting.Limit50 => new(true, 50, null),
            BatteryValidationSetting.Limit55 => new(true, 55, null),
            BatteryValidationSetting.Limit60 => new(true, 60, null),
            BatteryValidationSetting.Limit65 => new(true, 65, null),
            BatteryValidationSetting.Limit70 => new(true, 70, null),
            BatteryValidationSetting.Limit75 => new(true, 75, null),
            BatteryValidationSetting.Limit80 => new(true, 80, null),
            _ => throw new RazerProtocolException(
                "Battery protection query returned an undefined setting.",
                HidProtocolValidationFailure.PayloadValidator),
        };
}
