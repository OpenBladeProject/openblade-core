namespace OpenSynapse.Protocol;

internal sealed class BatteryValidationRunFactory : IBatteryValidationRunFactory
{
    private static readonly object RetentionSync = new();
    private static readonly HashSet<Task> RetainedDisposals = [];
    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan cleanupDrainTimeout;

    internal BatteryValidationRunFactory(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        TimeProvider timeProvider,
        TimeSpan cleanupDrainTimeout)
    {
        ArgumentNullException.ThrowIfNull(createContext);
        ArgumentNullException.ThrowIfNull(actorFactory);
        ArgumentNullException.ThrowIfNull(timeProvider);
        RazerCommandSessionTimeout.Validate(cleanupDrainTimeout);
        this.createContext = createContext;
        this.actorFactory = actorFactory;
        this.timeProvider = timeProvider;
        this.cleanupDrainTimeout = cleanupDrainTimeout;
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
                actor, timeProvider, cleanupDrainTimeout, Retain).ConfigureAwait(false);
            return Failure(HidSessionFailureKind.Canceled);
        }

        var run = new BatteryValidationRun(
            context, ids, actor, actorFactory, timeProvider, cleanupDrainTimeout);
        return new(new BatteryValidationPort(run), null);
    }

    private static HidSessionResult<IBatteryValidationPort> Failure(HidSessionFailureKind failure) =>
        new(null, failure);

    private static void Retain(Task disposal)
    {
        lock (RetentionSync)
        {
            RetainedDisposals.Add(disposal);
        }

        _ = disposal.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted)
                {
                    _ = completed.Exception;
                }

                lock (RetentionSync)
                {
                    RetainedDisposals.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
