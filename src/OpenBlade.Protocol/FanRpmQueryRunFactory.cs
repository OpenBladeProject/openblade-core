namespace OpenBlade.Protocol;

public sealed record FanRpmReadings(int Selector1Rpm, int Selector2Rpm)
{
    /// <summary>The greater of the two selector readings.</summary>
    public int MaxRpm => Math.Max(Selector1Rpm, Selector2Rpm);
}

public interface IFanRpmQueryRunFactory
{
    Task<HidSessionResult<IFanRpmQueryPort>> OpenAsync(
        CancellationToken cancellationToken);
}

public interface IFanRpmQueryPort : IAsyncDisposable
{
    Task<HidSessionResult<FanRpmReadings>> QueryAsync(
        CancellationToken cancellationToken);
}

internal sealed class FanRpmQueryRunFactory : IFanRpmQueryRunFactory
{
    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan exchangeTimeout;
    private readonly TimeSpan cleanupDrainTimeout;
    private readonly PendingDisposalRegistry pendingDisposals;

    internal FanRpmQueryRunFactory(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupDrainTimeout)
        : this(
            createContext,
            actorFactory,
            timeProvider,
            exchangeTimeout,
            cleanupDrainTimeout,
            PendingDisposalRegistry.Shared)
    {
    }

    internal FanRpmQueryRunFactory(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupDrainTimeout,
        PendingDisposalRegistry pendingDisposals)
    {
        ArgumentNullException.ThrowIfNull(createContext);
        ArgumentNullException.ThrowIfNull(actorFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(pendingDisposals);
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        RazerCommandSessionTimeout.Validate(cleanupDrainTimeout);
        this.createContext = createContext;
        this.actorFactory = actorFactory;
        this.timeProvider = timeProvider;
        this.exchangeTimeout = exchangeTimeout;
        this.cleanupDrainTimeout = cleanupDrainTimeout;
        this.pendingDisposals = pendingDisposals;
    }

    public async Task<HidSessionResult<IFanRpmQueryPort>> OpenAsync(
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Failure(HidSessionFailureKind.Canceled);
        }

        ValidationRunContext context;
        try
        {
            context = createContext() ?? throw new InvalidOperationException();
        }
        catch
        {
            return Failure(HidSessionFailureKind.NativeIoFailure);
        }

        if (!context.TryAllocateTransaction(out byte selector1TransactionId)
            || !context.TryAllocateTransaction(out byte selector2TransactionId))
        {
            return Failure(HidSessionFailureKind.Exhausted);
        }

        HidSessionResult<ValidationActor> opened;
        try
        {
            opened = await actorFactory.OpenAsync(context).ConfigureAwait(false);
        }
        catch
        {
            return Failure(HidSessionFailureKind.NativeIoFailure);
        }

        if (opened.Failure is not null || opened.Value is null)
        {
            if (opened.Value is not null)
            {
                await BatteryValidationRun.DisposeBoundedAsync(
                    opened.Value,
                    timeProvider,
                    cleanupDrainTimeout,
                    pendingDisposals).ConfigureAwait(false);
            }

            return Failure(opened.Failure ?? HidSessionFailureKind.NativeIoFailure);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            await BatteryValidationRun.DisposeBoundedAsync(
                opened.Value,
                timeProvider,
                cleanupDrainTimeout,
                pendingDisposals).ConfigureAwait(false);
            return Failure(HidSessionFailureKind.Canceled);
        }

        return new(new FanRpmQueryPort(
            selector1TransactionId,
            selector2TransactionId,
            opened.Value,
            timeProvider,
            exchangeTimeout,
            cleanupDrainTimeout,
            pendingDisposals), null);
    }

    private static HidSessionResult<IFanRpmQueryPort> Failure(
        HidSessionFailureKind failure) => new(null, failure);
}

internal sealed class FanRpmQueryPort : IFanRpmQueryPort
{
    private readonly byte selector1TransactionId;
    private readonly byte selector2TransactionId;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan exchangeTimeout;
    private readonly TimeSpan cleanupDrainTimeout;
    private readonly PendingDisposalRegistry pendingDisposals;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ValidationActor? actor;
    private bool terminal;

    internal FanRpmQueryPort(
        byte selector1TransactionId,
        byte selector2TransactionId,
        ValidationActor actor,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupDrainTimeout,
        PendingDisposalRegistry pendingDisposals)
    {
        this.selector1TransactionId = selector1TransactionId;
        this.selector2TransactionId = selector2TransactionId;
        this.actor = actor ?? throw new ArgumentNullException(nameof(actor));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.pendingDisposals = pendingDisposals
            ?? throw new ArgumentNullException(nameof(pendingDisposals));
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        RazerCommandSessionTimeout.Validate(cleanupDrainTimeout);
        this.exchangeTimeout = exchangeTimeout;
        this.cleanupDrainTimeout = cleanupDrainTimeout;
    }

    public async Task<HidSessionResult<FanRpmReadings>> QueryAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Failure(HidSessionFailureKind.Canceled);
        }

        try
        {
            if (terminal)
            {
                return Failure(HidSessionFailureKind.Disposed);
            }

            HidSessionResult<int> selector1 = await QuerySelectorAsync(
                selector1TransactionId,
                FanRpmSelector.First,
                cancellationToken).ConfigureAwait(false);
            if (selector1.Failure is not null)
            {
                await CloseAsync().ConfigureAwait(false);
                return Failure(selector1.Failure.Value, selector1.Diagnostic);
            }

            HidSessionResult<int> selector2 = await QuerySelectorAsync(
                selector2TransactionId,
                FanRpmSelector.Second,
                cancellationToken).ConfigureAwait(false);
            await CloseAsync().ConfigureAwait(false);
            return selector2.Failure is null
                ? new(new FanRpmReadings(selector1.Value, selector2.Value), null)
                : Failure(selector2.Failure.Value, selector2.Diagnostic);
        }
        catch
        {
            await CloseAsync().ConfigureAwait(false);
            return Failure(HidSessionFailureKind.NativeIoFailure);
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<HidSessionResult<int>> QuerySelectorAsync(
        byte transactionId,
        FanRpmSelector selector,
        CancellationToken cancellationToken)
    {
        var command = new RazerCommandSpec(
            0x0D,
            0x88,
            [0x01, (byte)selector, 0x00],
            3,
            payload => FanRpmProtocol.TryDecodeQuery(payload.Span, selector, out _));
        TransactionIdAssignment assignment = TransactionIdAssignment.Valid(transactionId);
        HidSessionResult<ReadOnlyMemory<byte>> response = await actor!.Session.ExecuteAsync(
            command,
            () => assignment,
            exchangeTimeout,
            cancellationToken).ConfigureAwait(false);
        if (response.Failure is not null)
        {
            return new(default, response.Failure) { Diagnostic = response.Diagnostic };
        }

        return FanRpmProtocol.TryDecodeQuery(response.Value.Span, selector, out int rpm)
            ? new(rpm, null)
            : new(default, HidSessionFailureKind.ProtocolFailure)
            {
                Diagnostic = new HidSessionFailureDiagnostic(
                    HidSessionFailurePhase.ProtocolValidation,
                    HidSessionFailureKind.ProtocolFailure,
                    NativeWin32Error: null,
                    TransferredByteCount: null)
                {
                    ProtocolValidationFailure = HidProtocolValidationFailure.PayloadValidator,
                },
            };
    }

    private async Task CloseAsync()
    {
        terminal = true;
        ValidationActor? current = actor;
        actor = null;
        if (current is not null)
        {
            await BatteryValidationRun.DisposeBoundedAsync(
                current,
                timeProvider,
                cleanupDrainTimeout,
                pendingDisposals).ConfigureAwait(false);
        }
    }

    private static HidSessionResult<FanRpmReadings> Failure(
        HidSessionFailureKind failure,
        HidSessionFailureDiagnostic? diagnostic = null) =>
        new(null, failure) { Diagnostic = diagnostic };
}
