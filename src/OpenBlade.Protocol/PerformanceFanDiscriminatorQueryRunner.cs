namespace OpenBlade.Protocol;

public sealed record PerformanceFanDiscriminatorReading(
    string Operation,
    string RequestPayloadHex,
    string ResponseHex);

public sealed record PerformanceFanDiscriminatorSnapshot(
    IReadOnlyList<PerformanceFanDiscriminatorReading> Readings);

public interface IPerformanceFanDiscriminatorQueryRunner
{
    Task<HidSessionResult<PerformanceFanDiscriminatorSnapshot>> RunAsync(
        CancellationToken cancellationToken);
}

internal sealed class PerformanceFanDiscriminatorQueryRunner
    : IPerformanceFanDiscriminatorQueryRunner
{
    private static readonly QueryDefinition[] Queries =
    [
        new("0D/8E", 0x0D, 0x8E, [0x00], 1),
        new("0D/8F", 0x0D, 0x8F, [0x00], 1),
        new("0D/80", 0x0D, 0x80, new byte[80], 80),
        new("0D/89", 0x0D, 0x89, [0x00, 0x00], 2),
        new("0D/8B", 0x0D, 0x8B, [0x00, 0x00], 2),
        new("0D/8A", 0x0D, 0x8A, [0x00, 0x00], 2),
        new("0F/98", 0x0F, 0x98, [0x00], 1),
    ];

    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan exchangeTimeout;
    private readonly TimeSpan cleanupDrainTimeout;
    private readonly PendingDisposalRegistry pendingDisposals;
    private int started;

    internal PerformanceFanDiscriminatorQueryRunner(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupDrainTimeout,
        PendingDisposalRegistry? pendingDisposals = null)
    {
        this.createContext = createContext ?? throw new ArgumentNullException(nameof(createContext));
        this.actorFactory = actorFactory ?? throw new ArgumentNullException(nameof(actorFactory));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        RazerCommandSessionTimeout.Validate(cleanupDrainTimeout);
        this.exchangeTimeout = exchangeTimeout;
        this.cleanupDrainTimeout = cleanupDrainTimeout;
        this.pendingDisposals = pendingDisposals ?? PendingDisposalRegistry.Shared;
    }

    public async Task<HidSessionResult<PerformanceFanDiscriminatorSnapshot>> RunAsync(
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return Failure(HidSessionFailureKind.Disposed);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Failure(HidSessionFailureKind.Canceled);
        }

        ValidationRunContext context;
        var transactionIds = new byte[Queries.Length];
        try
        {
            context = createContext() ?? throw new InvalidOperationException();
            for (int index = 0; index < transactionIds.Length; index++)
            {
                if (!context.TryAllocateTransaction(out transactionIds[index]))
                {
                    return Failure(HidSessionFailureKind.Exhausted);
                }
            }
        }
        catch
        {
            return Failure(HidSessionFailureKind.NativeIoFailure);
        }

        ValidationActor? actor = null;
        try
        {
            HidSessionResult<ValidationActor> opened =
                await actorFactory.OpenAsync(context).ConfigureAwait(false);
            actor = opened.Value;
            if (opened.Failure is not null || actor is null)
            {
                return Failure(
                    opened.Failure ?? HidSessionFailureKind.NativeIoFailure,
                    opened.Diagnostic);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var readings = new PerformanceFanDiscriminatorReading[Queries.Length];
            for (int index = 0; index < Queries.Length; index++)
            {
                QueryDefinition query = Queries[index];
                byte transactionId = transactionIds[index];
                HidSessionResult<ReadOnlyMemory<byte>> response =
                    await actor.Session.ExecuteAsync(
                        query.CreateCommand(),
                        () => TransactionIdAssignment.Valid(transactionId),
                        exchangeTimeout,
                        cancellationToken).ConfigureAwait(false);
                if (response.Failure is not null)
                {
                    return Failure(response.Failure.Value, response.Diagnostic);
                }

                readings[index] = new(
                    query.Operation,
                    Convert.ToHexString(query.Arguments),
                    Convert.ToHexString(response.Value.Span));
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new(new PerformanceFanDiscriminatorSnapshot(readings), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(HidSessionFailureKind.Canceled);
        }
        catch
        {
            return Failure(HidSessionFailureKind.NativeIoFailure);
        }
        finally
        {
            if (actor is not null)
            {
                await BatteryValidationRun.DisposeBoundedAsync(
                    actor,
                    timeProvider,
                    cleanupDrainTimeout,
                    pendingDisposals).ConfigureAwait(false);
            }
        }
    }

    private static HidSessionResult<PerformanceFanDiscriminatorSnapshot> Failure(
        HidSessionFailureKind failure,
        HidSessionFailureDiagnostic? diagnostic = null) =>
        new(null, failure) { Diagnostic = diagnostic };

    private sealed record QueryDefinition(
        string Operation,
        byte OperationClass,
        byte OperationId,
        byte[] Arguments,
        int ExpectedResponseLength)
    {
        internal RazerCommandSpec CreateCommand() => new(
            OperationClass,
            OperationId,
            Arguments,
            ExpectedResponseLength);
    }
}
