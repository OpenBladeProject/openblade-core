namespace OpenBlade.Protocol;

internal sealed class BatteryValidationRunFactory : IBatteryValidationRunFactory
{
    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan cleanupDrainTimeout;
    private readonly PendingDisposalRegistry pendingDisposals;

    internal BatteryValidationRunFactory(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        TimeProvider timeProvider,
        TimeSpan cleanupDrainTimeout)
        : this(createContext, actorFactory, timeProvider, cleanupDrainTimeout, PendingDisposalRegistry.Shared)
    {
    }

    internal BatteryValidationRunFactory(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        TimeProvider timeProvider,
        TimeSpan cleanupDrainTimeout,
        PendingDisposalRegistry pendingDisposals)
    {
        ArgumentNullException.ThrowIfNull(createContext);
        ArgumentNullException.ThrowIfNull(actorFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(pendingDisposals);
        RazerCommandSessionTimeout.Validate(cleanupDrainTimeout);
        this.createContext = createContext;
        this.actorFactory = actorFactory;
        this.timeProvider = timeProvider;
        this.cleanupDrainTimeout = cleanupDrainTimeout;
        this.pendingDisposals = pendingDisposals;
    }

    public async Task<HidSessionResult<IBatteryValidationPort>> OpenAsync(
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

        if (!context.TryReserveBatteryValidation(out BatteryValidationTransactionIds ids))
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

        if (opened.Failure is not null)
        {
            if (opened.Value is not null)
            {
                await BatteryValidationRun.DisposeBoundedAsync(
                    opened.Value,
                    timeProvider,
                    cleanupDrainTimeout,
                    pendingDisposals).ConfigureAwait(false);
            }

            return Failure(opened.Failure.Value);
        }

        ValidationActor? actor = opened.Value;
        if (actor is null)
        {
            return Failure(HidSessionFailureKind.NativeIoFailure);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            await BatteryValidationRun.DisposeBoundedAsync(
                actor, timeProvider, cleanupDrainTimeout, pendingDisposals).ConfigureAwait(false);
            return Failure(HidSessionFailureKind.Canceled);
        }

        var run = new BatteryValidationRun(
            context, ids, actor, actorFactory, timeProvider, cleanupDrainTimeout, pendingDisposals);
        return new(new BatteryValidationPort(run), null);
    }

    private static HidSessionResult<IBatteryValidationPort> Failure(HidSessionFailureKind failure) =>
        new(null, failure);

}
