namespace OpenBlade.Protocol;

internal sealed class BatteryQuerySequenceRun : IAsyncDisposable
{
    private static readonly TimeSpan QuerySpacing = TimeSpan.FromSeconds(1);

    private readonly byte[] transactionIds;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan cleanupDrainTimeout;
    private readonly PendingDisposalRegistry pendingDisposals;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ValidationActor? actor;
    private int nextQuery;
    private bool terminal;

    internal BatteryQuerySequenceRun(
        byte[] transactionIds,
        ValidationActor actor,
        TimeProvider timeProvider,
        TimeSpan cleanupDrainTimeout,
        PendingDisposalRegistry pendingDisposals)
    {
        ArgumentNullException.ThrowIfNull(transactionIds);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(pendingDisposals);
        RazerCommandSessionTimeout.Validate(cleanupDrainTimeout);
        this.transactionIds = transactionIds;
        this.actor = actor;
        this.timeProvider = timeProvider;
        this.cleanupDrainTimeout = cleanupDrainTimeout;
        this.pendingDisposals = pendingDisposals;
    }

    internal async Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(
        CancellationToken cancellationToken)
    {
        if (!await EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!terminal)
                {
                    await TerminateAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                gate.Release();
            }

            return Failure(HidSessionFailureKind.Canceled);
        }

        try
        {
            if (terminal)
            {
                return Failure(HidSessionFailureKind.Disposed);
            }

            if (nextQuery > 0)
            {
                try
                {
                    await Task.Delay(QuerySpacing, timeProvider, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await TerminateAsync().ConfigureAwait(false);
                    return Failure(HidSessionFailureKind.Canceled);
                }
            }

            HidSessionResult<BatteryValidationSetting> result;
            try
            {
                result = await actor!.BatteryCommands.QueryAsync(
                    transactionIds[nextQuery],
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                result = Failure(HidSessionFailureKind.NativeIoFailure);
            }

            if (result.Failure is not null || !IsDefined(result.Value))
            {
                await TerminateAsync().ConfigureAwait(false);
                return result.Failure is null
                    ? Failure(HidSessionFailureKind.ProtocolFailure)
                    : result;
            }

            nextQuery++;
            if (nextQuery == transactionIds.Length)
            {
                await TerminateAsync().ConfigureAwait(false);
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
            await TerminateAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task TerminateAsync()
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

    private static HidSessionResult<BatteryValidationSetting> Failure(
        HidSessionFailureKind failure) => new(default, failure);
}
