using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>
/// A read-only opaque byte returned by the exact cooling-pad fan query. The
/// value has not been assigned to active mode, target, physical speed, or
/// ownership semantics.
/// </summary>
public readonly struct CoolingPadFanObservation
{
    public CoolingPadFanObservation(byte opaqueValue)
    {
        OpaqueValue = opaqueValue;
    }

    /// <summary>
    /// Gets the opaque observed value without assigning target or speed units.
    /// </summary>
    public byte OpaqueValue { get; }
}

public interface IProductionCoolingPadPort : IAsyncDisposable
{
    Task<HidSessionResult<CoolingPadFanObservation>> ObserveFanAsync(
        CancellationToken cancellationToken);
}

public interface IProductionCoolingPadPortFactory
{
    Task<HidSessionResult<IProductionCoolingPadPort>> OpenAsync(
        CancellationToken cancellationToken);
}

internal sealed class ProductionCoolingPadPort : IProductionCoolingPadPort
{
    private readonly IRazerCommandSession session;
    private readonly IAsyncDisposable ownedSession;
    private readonly ValidationRunContext context;
    private readonly TimeSpan exchangeTimeout;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private int poisoned;
    private int disposed;

    internal ProductionCoolingPadPort(
        IRazerCommandSession session,
        IAsyncDisposable ownedSession,
        ValidationRunContext context,
        TimeSpan exchangeTimeout)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(ownedSession);
        ArgumentNullException.ThrowIfNull(context);
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        this.session = session;
        this.ownedSession = ownedSession;
        this.context = context;
        this.exchangeTimeout = exchangeTimeout;
    }

    public async Task<HidSessionResult<CoolingPadFanObservation>> ObserveFanAsync(
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return Failure(HidSessionFailureKind.Disposed);
        }

        try
        {
            await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Failure(HidSessionFailureKind.Canceled);
        }

        try
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return Failure(HidSessionFailureKind.Disposed);
            }
            if (Volatile.Read(ref poisoned) != 0)
            {
                return Failure(HidSessionFailureKind.Poisoned);
            }

            // Reserve before the session is invoked so Exhausted is a
            // definitive pre-submission result. The service may safely rotate
            // this read-only port once on that specific outcome.
            if (!context.TryAllocateTransaction(out byte transactionId))
            {
                return Failure(HidSessionFailureKind.Exhausted);
            }

            HidSessionResult<ReadOnlyMemory<byte>> result = await session.ExecuteAsync(
                CoolingPadFanObservedRawProtocol.CreateQuery(),
                () => TransactionIdAssignment.Valid(transactionId),
                exchangeTimeout,
                cancellationToken).ConfigureAwait(false);
            if (result.Failure is not null)
            {
                return new(default, result.Failure)
                {
                    Diagnostic = result.Diagnostic,
                };
            }

            if (!CoolingPadFanObservedRawProtocol.TryDecodeResponse(
                    result.Value.Span,
                    out CoolingPadFanObservedRawByte observation))
            {
                return Failure(HidSessionFailureKind.ProtocolFailure);
            }

            return new(new CoolingPadFanObservation(observation.RawByte), null);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        await operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ownedSession.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal ICoolingPadLightingExecutor CreateDormantLightingExecutor() =>
        new CoolingPadLightingExecutor(
            session,
            context,
            operationGate,
            exchangeTimeout,
            isUnavailable: () => Volatile.Read(ref disposed) != 0,
            isPoisoned: () => Volatile.Read(ref poisoned) != 0,
            poison: () => Volatile.Write(ref poisoned, 1));

    internal CoolingPadHostControlExecutor CreateDormantHostControlExecutor() =>
        new(
            session,
            context,
            operationGate,
            exchangeTimeout,
            poison: () => Volatile.Write(ref poisoned, 1));

    private static HidSessionResult<CoolingPadFanObservation> Failure(
        HidSessionFailureKind failure) => new(default, failure);
}
