namespace OpenBlade.Protocol;

internal sealed class ProtectionOffValidationRun : IAsyncDisposable
{
    private readonly ValidationRunContext context;
    private readonly ProtectionOffValidationTransactionIds ids;
    private readonly IValidationActorFactory actorFactory;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan cleanupDrainTimeout;
    private readonly PendingDisposalRegistry pendingDisposals;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ValidationActor? actor;
    private Stage stage = Stage.BaselineProtection;
    private bool disposed;

    internal ProtectionOffValidationRun(
        ValidationRunContext context,
        ProtectionOffValidationTransactionIds ids,
        ValidationActor actor,
        IValidationActorFactory actorFactory,
        TimeProvider timeProvider,
        TimeSpan cleanupDrainTimeout,
        PendingDisposalRegistry pendingDisposals)
    {
        this.context = context;
        this.ids = ids;
        this.actor = actor;
        this.actorFactory = actorFactory;
        this.timeProvider = timeProvider;
        this.cleanupDrainTimeout = cleanupDrainTimeout;
        this.pendingDisposals = pendingDisposals;
    }

    internal async Task<HidSessionResult<BatteryValidationSetting>> QueryProtectionAsync(
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

            byte transactionId;
            Stage next;
            bool closeOnFailure;
            switch (stage)
            {
                case Stage.BaselineProtection:
                    transactionId = ids.BaselineProtectionQuery;
                    next = Stage.BaselineTemporary;
                    closeOnFailure = true;
                    break;
                case Stage.CandidateProtection:
                    transactionId = ids.CandidateProtectionQuery;
                    next = Stage.CandidateTemporary;
                    closeOnFailure = false;
                    break;
                case Stage.RestorationProtection:
                    transactionId = ids.RestorationProtectionQuery;
                    next = Stage.RestorationTemporary;
                    closeOnFailure = false;
                    break;
                default:
                    return Failure<BatteryValidationSetting>(HidSessionFailureKind.ProtocolFailure);
            }

            HidSessionResult<BatteryValidationSetting> result = await InvokeAsync(() =>
                actor!.BatteryCommands.QueryAsync(transactionId, cancellationToken)).ConfigureAwait(false);
            if (result.Failure is null && !Enum.IsDefined(result.Value))
            {
                result = Failure<BatteryValidationSetting>(HidSessionFailureKind.ProtocolFailure);
            }

            stage = next;
            if (closeOnFailure && result.Failure is not null)
            {
                stage = Stage.Terminal;
                await CloseAsync().ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<HidSessionResult<bool>> QueryTemporaryFullChargeAsync(
        CancellationToken cancellationToken)
    {
        if (!await EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            return Failure<bool>(HidSessionFailureKind.Canceled);
        }

        try
        {
            if (disposed || stage == Stage.Terminal)
            {
                return Failure<bool>(HidSessionFailureKind.Disposed);
            }

            byte transactionId;
            Stage next;
            bool terminalAfter;
            switch (stage)
            {
                case Stage.BaselineTemporary:
                    transactionId = ids.BaselineTemporaryQuery;
                    next = Stage.CandidateApply;
                    terminalAfter = false;
                    break;
                case Stage.CandidateTemporary:
                    transactionId = ids.CandidateTemporaryQuery;
                    next = Stage.RestoreReady;
                    terminalAfter = false;
                    break;
                case Stage.RestorationTemporary:
                    transactionId = ids.RestorationTemporaryQuery;
                    next = Stage.Terminal;
                    terminalAfter = true;
                    break;
                default:
                    return Failure<bool>(HidSessionFailureKind.ProtocolFailure);
            }

            HidSessionResult<bool> result = await InvokeAsync(() =>
                actor!.TemporaryFullChargeCommands.QueryAsync(
                    transactionId,
                    cancellationToken)).ConfigureAwait(false);
            stage = result.Failure is not null && stage == Stage.BaselineTemporary
                ? Stage.Terminal
                : next;
            if (terminalAfter || stage == Stage.Terminal)
            {
                await CloseAsync().ConfigureAwait(false);
            }

            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<HidSessionResult<HidUnit>> ApplyProtectionOffAsync(
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

            if (stage != Stage.CandidateApply)
            {
                return Failure<HidUnit>(HidSessionFailureKind.ProtocolFailure);
            }

            // The saved-Limit80 restoration obligation exists before candidate I/O begins.
            stage = Stage.CandidateProtection;
            return await InvokeAsync(() => actor!.BatteryCommands.SetAsync(
                ids.CandidateSet,
                BatteryValidationSetting.ProtectionOff,
                cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    internal async Task<HidSessionResult<HidUnit>> RestoreLimit80Async(
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

            if (stage is not (Stage.CandidateProtection or Stage.CandidateTemporary or Stage.RestoreReady))
            {
                return Failure<HidUnit>(HidSessionFailureKind.ProtocolFailure);
            }

            HidSessionResult<HidUnit> recovery = await EnsureRestoreActorAsync().ConfigureAwait(false);
            if (recovery.Failure is not null)
            {
                stage = Stage.Terminal;
                await CloseAsync().ConfigureAwait(false);
                return recovery;
            }

            stage = Stage.RestorationProtection;
            HidSessionResult<HidUnit> result = await InvokeAsync(() => actor!.BatteryCommands.SetAsync(
                ids.RestoreSet,
                BatteryValidationSetting.Limit80,
                cancellationToken)).ConfigureAwait(false);
            if (result.Failure is not null)
            {
                stage = Stage.Terminal;
                await CloseAsync().ConfigureAwait(false);
            }

            return result;
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
            await CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<HidSessionResult<HidUnit>> EnsureRestoreActorAsync()
    {
        ValidationActor current = actor!;
        if (current.Session.Disposition == RazerCommandSessionDisposition.Healthy)
        {
            return new(new HidUnit(), null);
        }

        await BatteryValidationRun.DisposeBoundedAsync(
            current, timeProvider, cleanupDrainTimeout, pendingDisposals).ConfigureAwait(false);
        actor = null;
        if (current.Session.Disposition != RazerCommandSessionDisposition.FullyDisposed)
        {
            return Failure<HidUnit>(HidSessionFailureKind.Timeout);
        }

        HidSessionResult<ValidationActor> reopened;
        try
        {
            reopened = await actorFactory.OpenAsync(context).ConfigureAwait(false);
        }
        catch
        {
            return Failure<HidUnit>(HidSessionFailureKind.NativeIoFailure);
        }

        if (reopened.Failure is not null || reopened.Value is null)
        {
            if (reopened.Value is not null)
            {
                await BatteryValidationRun.DisposeBoundedAsync(
                    reopened.Value, timeProvider, cleanupDrainTimeout, pendingDisposals).ConfigureAwait(false);
            }

            return Failure<HidUnit>(reopened.Failure ?? HidSessionFailureKind.NativeIoFailure);
        }

        actor = reopened.Value;
        return new(new HidUnit(), null);
    }

    private async Task CloseAsync()
    {
        ValidationActor? current = actor;
        actor = null;
        if (current is not null)
        {
            await BatteryValidationRun.DisposeBoundedAsync(
                current, timeProvider, cleanupDrainTimeout, pendingDisposals).ConfigureAwait(false);
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

    private static async Task<HidSessionResult<T>> InvokeAsync<T>(
        Func<Task<HidSessionResult<T>>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch
        {
            return Failure<T>(HidSessionFailureKind.NativeIoFailure);
        }
    }

    private static HidSessionResult<T> Failure<T>(HidSessionFailureKind failure) =>
        new(default, failure);

    private enum Stage
    {
        BaselineProtection,
        BaselineTemporary,
        CandidateApply,
        CandidateProtection,
        CandidateTemporary,
        RestoreReady,
        RestorationProtection,
        RestorationTemporary,
        Terminal,
    }
}
