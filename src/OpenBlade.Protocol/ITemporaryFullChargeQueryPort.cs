namespace OpenBlade.Protocol;

public interface ITemporaryFullChargeQueryRunFactory
{
    Task<HidSessionResult<ITemporaryFullChargeQueryPort>> OpenAsync(
        CancellationToken cancellationToken);
}

public interface ITemporaryFullChargeQueryPort : IAsyncDisposable
{
    Task<HidSessionResult<bool>> QueryAsync(CancellationToken cancellationToken);
}

internal sealed class TemporaryFullChargeQueryPort(
    byte transactionId,
    ValidationActor actor,
    TimeProvider timeProvider,
    TimeSpan cleanupDrainTimeout,
    PendingDisposalRegistry pendingDisposals) : ITemporaryFullChargeQueryPort
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private ValidationActor? actor = actor;
    private bool terminal;

    public async Task<HidSessionResult<bool>> QueryAsync(CancellationToken cancellationToken)
    {
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new(default, HidSessionFailureKind.Canceled);
        }

        try
        {
            if (terminal)
            {
                return new(default, HidSessionFailureKind.Disposed);
            }

            HidSessionResult<bool> result;
            try
            {
                result = await actor!.TemporaryFullChargeCommands.QueryAsync(
                    transactionId,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                result = new(default, HidSessionFailureKind.NativeIoFailure);
            }

            await CloseAsync().ConfigureAwait(false);
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
            await CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
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
}
