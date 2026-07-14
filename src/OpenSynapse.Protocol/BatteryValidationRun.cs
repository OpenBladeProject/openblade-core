namespace OpenSynapse.Protocol;

internal sealed class BatteryValidationRun : IAsyncDisposable
{
    private readonly ValidationRunContext context;
    private readonly BatteryValidationTransactionIds ids;
    private readonly IValidationActorFactory actorFactory;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan cleanupDrainTimeout;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object retentionSync = new();
    private readonly HashSet<Task> retainedDisposals = [];
    private ValidationActor? actor;
    private ValidationActor? restoreActor;
    private BatteryValidationSetting baseline;
    private Stage stage = Stage.AwaitingBaseline;
    private bool disposed;

    internal BatteryValidationRun(
        ValidationRunContext context,
        BatteryValidationTransactionIds ids,
        ValidationActor actor,
        IValidationActorFactory actorFactory,
        TimeProvider timeProvider,
        TimeSpan cleanupDrainTimeout)
    {
        this.context = context;
        this.ids = ids;
        this.actor = actor;
        this.actorFactory = actorFactory;
        this.timeProvider = timeProvider;
        this.cleanupDrainTimeout = cleanupDrainTimeout;
    }

    internal async Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(
        CancellationToken cancellationToken)
    {
        if (!await EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            return Failure<BatteryValidationSetting>(HidSessionFailureKind.Canceled);
        }

        try
        {
            if (disposed || stage == Stage.Terminal)
            {
                return Failure<BatteryValidationSetting>(HidSessionFailureKind.Disposed);
            }

            if (stage == Stage.AwaitingBaseline)
            {
                HidSessionResult<BatteryValidationSetting> result =
                    await actor!.BatteryCommands.QueryAsync(ids.BaselineQuery, cancellationToken).ConfigureAwait(false);
                if (result.Failure is null && IsDefined(result.Value))
                {
                    baseline = result.Value;
                    stage = Stage.AwaitingCandidateApply;
                }
                else
                {
                    stage = Stage.Terminal;
                    await CloseCurrentBoundedAsync().ConfigureAwait(false);
                }

                return result.Failure is null && !IsDefined(result.Value)
                    ? Failure<BatteryValidationSetting>(HidSessionFailureKind.ProtocolFailure)
                    : result;
            }

            if (stage == Stage.AwaitingCandidateQueryOrRestore)
            {
                HidSessionResult<BatteryValidationSetting> result =
                    await actor!.BatteryCommands.QueryAsync(ids.CandidateQuery, cancellationToken).ConfigureAwait(false);
                stage = Stage.CleanupEligible;
                return result;
            }

            if (stage == Stage.AwaitingRestoreQuery)
            {
                HidSessionResult<BatteryValidationSetting> result =
                    await restoreActor!.BatteryCommands.QueryAsync(ids.RestoreQuery, cancellationToken).ConfigureAwait(false);
                stage = Stage.Terminal;
                await CloseCurrentBoundedAsync().ConfigureAwait(false);
                return result;
            }

            return Failure<BatteryValidationSetting>(HidSessionFailureKind.ProtocolFailure);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<HidSessionResult<HidUnit>> ApplyAsync(
        BatteryValidationSetting setting,
        CancellationToken cancellationToken)
    {
        if (!await EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            return Failure<HidUnit>(HidSessionFailureKind.Canceled);
        }

        try
        {
            if (disposed || stage == Stage.Terminal)
            {
                return Failure<HidUnit>(HidSessionFailureKind.Disposed);
            }

            if (stage == Stage.AwaitingCandidateApply)
            {
                if (!IsDefined(setting) || setting == baseline)
                {
                    return Failure<HidUnit>(HidSessionFailureKind.ProtocolFailure);
                }

                // The restoration obligation exists before candidate I/O begins.
                stage = Stage.CleanupEligible;
                HidSessionResult<HidUnit> result =
                    await actor!.BatteryCommands.SetAsync(ids.CandidateSet, setting, cancellationToken).ConfigureAwait(false);
                if (result.Failure is null)
                {
                    stage = Stage.AwaitingCandidateQueryOrRestore;
                }

                return result;
            }

            if ((stage == Stage.AwaitingCandidateQueryOrRestore || stage == Stage.CleanupEligible) &&
                IsDefined(setting) && setting == baseline)
            {
                stage = Stage.RestoreInFlight;
                return await RestoreAsync(setting, cancellationToken).ConfigureAwait(false);
            }

            return Failure<HidUnit>(HidSessionFailureKind.ProtocolFailure);
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
            if (disposed)
            {
                return;
            }

            disposed = true;
            stage = Stage.Terminal;
            await CloseCurrentBoundedAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<HidSessionResult<HidUnit>> RestoreAsync(
        BatteryValidationSetting setting,
        CancellationToken cancellationToken)
    {
        ValidationActor primary = actor!;
        RazerCommandSessionDisposition disposition = primary.Session.Disposition;
        ValidationActor selected;

        if (disposition == RazerCommandSessionDisposition.Healthy)
        {
            selected = primary;
        }
        else
        {
            Task disposal = CaptureDisposal(primary);
            if (!await DisposalWinsAsync(disposal, primary).ConfigureAwait(false))
            {
                actor = null;
                restoreActor = null;
                stage = Stage.Terminal;
                return Failure<HidUnit>(HidSessionFailureKind.Timeout);
            }

            HidSessionResult<ValidationActor> reopened;
            try
            {
                reopened = await actorFactory.OpenAsync(context).ConfigureAwait(false);
            }
            catch
            {
                stage = Stage.Terminal;
                return Failure<HidUnit>(HidSessionFailureKind.NativeIoFailure);
            }

            if (reopened.Failure is not null || reopened.Value is null)
            {
                stage = Stage.Terminal;
                return Failure<HidUnit>(reopened.Failure ?? HidSessionFailureKind.NativeIoFailure);
            }

            selected = reopened.Value;
            actor = selected;
        }

        restoreActor = selected;
        HidSessionResult<HidUnit> result =
            await selected.BatteryCommands.SetAsync(ids.RestoreSet, setting, cancellationToken).ConfigureAwait(false);
        if (result.Failure is null)
        {
            stage = Stage.AwaitingRestoreQuery;
        }
        else
        {
            stage = Stage.Terminal;
            await CloseCurrentBoundedAsync().ConfigureAwait(false);
        }

        return result;
    }

    private async Task<bool> DisposalWinsAsync(Task disposal, ValidationActor primary)
    {
        if (IsFullyDisposed(disposal, primary))
        {
            Observe(disposal);
            return true;
        }

        Task deadline = Task.Delay(cleanupDrainTimeout, timeProvider);
        var winner = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = disposal.ContinueWith(
            completed =>
            {
                Observe(completed);
                if (primary.Session.Disposition == RazerCommandSessionDisposition.FullyDisposed)
                {
                    winner.TrySetResult(true);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        _ = deadline.ContinueWith(
            _ => winner.TrySetResult(false),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        bool disposalWon = await winner.Task.ConfigureAwait(false);
        if (!disposalWon)
        {
            Retain(disposal);
        }

        return disposalWon;
    }

    private static bool IsFullyDisposed(Task disposal, ValidationActor actor) =>
        disposal.IsCompleted && actor.Session.Disposition == RazerCommandSessionDisposition.FullyDisposed;

    private Task CaptureDisposal(ValidationActor target)
    {
        try
        {
            return target.DisposeAsync().AsTask();
        }
        catch (Exception error)
        {
            return Task.FromException(error);
        }
    }

    private async Task CloseCurrentBoundedAsync()
    {
        ValidationActor? current = actor;
        actor = null;
        restoreActor = null;
        if (current is not null)
        {
            await DisposeBoundedAsync(current, timeProvider, cleanupDrainTimeout, Retain).ConfigureAwait(false);
        }
    }

    internal static async Task DisposeBoundedAsync(
        ValidationActor target,
        TimeProvider timeProvider,
        TimeSpan timeout,
        Action<Task>? retain)
    {
        Task disposal;
        try
        {
            disposal = target.DisposeAsync().AsTask();
        }
        catch
        {
            return;
        }

        if (!disposal.IsCompleted)
        {
            Task deadline = Task.Delay(timeout, timeProvider);
            await Task.WhenAny(disposal, deadline).ConfigureAwait(false);
        }

        if (disposal.IsCompleted)
        {
            Observe(disposal);
        }
        else
        {
            retain?.Invoke(disposal);
        }
    }

    private void Retain(Task disposal)
    {
        lock (retentionSync)
        {
            retainedDisposals.Add(disposal);
        }

        _ = disposal.ContinueWith(
            completed =>
            {
                Observe(completed);
                lock (retentionSync)
                {
                    retainedDisposals.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static void Observe(Task task)
    {
        if (task.IsFaulted)
        {
            _ = task.Exception;
        }
    }

    private async Task<bool> EnterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static bool IsDefined(BatteryValidationSetting setting) => setting is
        BatteryValidationSetting.ProtectionOff or
        BatteryValidationSetting.Limit50 or
        BatteryValidationSetting.Limit55 or
        BatteryValidationSetting.Limit60 or
        BatteryValidationSetting.Limit65 or
        BatteryValidationSetting.Limit70 or
        BatteryValidationSetting.Limit75 or
        BatteryValidationSetting.Limit80;

    private static HidSessionResult<T> Failure<T>(HidSessionFailureKind failure) =>
        new(default, failure);

    private enum Stage
    {
        AwaitingBaseline,
        AwaitingCandidateApply,
        AwaitingCandidateQueryOrRestore,
        CleanupEligible,
        RestoreInFlight,
        AwaitingRestoreQuery,
        Terminal,
    }
}
