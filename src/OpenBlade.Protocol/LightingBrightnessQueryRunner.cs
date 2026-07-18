namespace OpenBlade.Protocol;

public sealed record LightingBrightnessReading(int RawValue)
{
    public int Percent => (int)Math.Round(RawValue * 100d / byte.MaxValue);
}

public interface ILightingBrightnessQueryRunner
{
    Task<HidSessionResult<LightingBrightnessReading>> RunAsync(
        CancellationToken cancellationToken);
}

internal sealed class LightingBrightnessQueryRunner : ILightingBrightnessQueryRunner
{
    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan exchangeTimeout;
    private readonly TimeSpan cleanupDrainTimeout;
    private readonly PendingDisposalRegistry pendingDisposals;
    private int started;

    internal LightingBrightnessQueryRunner(
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

    public async Task<HidSessionResult<LightingBrightnessReading>> RunAsync(
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
        byte transactionId;
        try
        {
            context = createContext() ?? throw new InvalidOperationException();
            if (!context.TryAllocateTransaction(out transactionId))
            {
                return Failure(HidSessionFailureKind.Exhausted);
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

            var command = new RazerCommandSpec(
                LightingBrightnessProtocol.OperationClass,
                LightingBrightnessProtocol.QueryOperationId,
                LightingBrightnessProtocol.QueryArguments.ToArray(),
                3,
                payload => LightingBrightnessProtocol.TryDecodeQuery(payload.Span, out _));
            HidSessionResult<ReadOnlyMemory<byte>> response =
                await actor.Session.ExecuteAsync(
                    command,
                    () => TransactionIdAssignment.Valid(transactionId),
                    exchangeTimeout,
                    cancellationToken).ConfigureAwait(false);
            if (response.Failure is not null)
            {
                return Failure(response.Failure.Value, response.Diagnostic);
            }

            return LightingBrightnessProtocol.TryDecodeQuery(response.Value.Span, out byte value)
                ? new(new LightingBrightnessReading(value), null)
                : Failure(HidSessionFailureKind.ProtocolFailure);
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

    private static HidSessionResult<LightingBrightnessReading> Failure(
        HidSessionFailureKind failure,
        HidSessionFailureDiagnostic? diagnostic = null) =>
        new(null, failure) { Diagnostic = diagnostic };
}
