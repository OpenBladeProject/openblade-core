namespace OpenBlade.Protocol;

internal sealed class BatteryQuerySequenceRun : IAsyncDisposable
{
    private readonly byte[] transactionIds;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan cleanupDrainTimeout;
    private readonly PendingDisposalRegistry pendingDisposals;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ValidationActor? actor;
    private int nextQuery;
    private int stopRequested;
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
        using CancellationTokenRegistration stopRegistration = cancellationToken.Register(
            static state => ((BatteryQuerySequenceRun)state!).RequestStop(),
            this);

        if (!await EnterAsync(cancellationToken).ConfigureAwait(false))
        {
            RequestStop();
            await StopAsync().ConfigureAwait(false);
            return Failure(HidSessionFailureKind.Canceled);
        }

        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                await TerminateAsync().ConfigureAwait(false);
                return Failure(HidSessionFailureKind.Canceled);
            }

            if (terminal)
            {
                return Failure(HidSessionFailureKind.Disposed);
            }

            if (IsStopRequested)
            {
                await TerminateAsync().ConfigureAwait(false);
                return Failure(HidSessionFailureKind.Disposed);
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
            if (nextQuery == transactionIds.Length || IsStopRequested)
            {
                await TerminateAsync().ConfigureAwait(false);
            }

            return cancellationToken.IsCancellationRequested
                ? Failure(HidSessionFailureKind.Canceled)
                : result;
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

    private async Task StopAsync()
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

    private bool IsStopRequested => Volatile.Read(ref stopRequested) != 0;

    private void RequestStop() => Interlocked.Exchange(ref stopRequested, 1);

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
