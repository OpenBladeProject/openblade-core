namespace OpenBlade.Tray;

internal enum StartOpenBladeResult
{
    Started,
    AlreadyRunning,
    Canceled,
}

internal sealed class TrayServiceStartupCoordinator
{
    private readonly Func<CancellationToken, Task<ObservedServiceState>> readStateAsync;
    private readonly Func<CancellationToken, Task<StartOpenBladeResult>> startAsync;
    private readonly object gate = new();
    private Task<StartOpenBladeResult>? activeStart;
    private bool startupAttempted;
    private bool manualStartRequested;
    private bool startsSuppressed;

    internal TrayServiceStartupCoordinator(
        Func<CancellationToken, Task<ObservedServiceState>> readStateAsync,
        Func<CancellationToken, Task<StartOpenBladeResult>> startAsync)
    {
        this.readStateAsync = readStateAsync
            ?? throw new ArgumentNullException(nameof(readStateAsync));
        this.startAsync = startAsync
            ?? throw new ArgumentNullException(nameof(startAsync));
    }

    internal async Task TryStartOnceAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (startupAttempted || manualStartRequested || startsSuppressed)
            {
                return;
            }

            startupAttempted = true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        ObservedServiceState state = await readStateAsync(cancellationToken)
            .ConfigureAwait(false);
        Task<StartOpenBladeResult> request;
        lock (gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state != ObservedServiceState.Stopped || startsSuppressed || manualStartRequested)
            {
                return;
            }

            request = GetOrStartAsync(cancellationToken);
        }

        await request.ConfigureAwait(false);
    }

    internal Task<StartOpenBladeResult> StartAsync(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            manualStartRequested = true;
            return startsSuppressed
                ? Task.FromResult(StartOpenBladeResult.Canceled)
                : GetOrStartAsync(cancellationToken);
        }
    }

    private Task<StartOpenBladeResult> GetOrStartAsync(CancellationToken cancellationToken)
    {
        // Called under gate. A later explicit manual retry is allowed, while
        // concurrent manual and automatic requests share the current operation.
        if (activeStart is null || activeStart.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            activeStart = startAsync(cancellationToken);
        }

        return activeStart;
    }

    internal async Task SuspendStartsAsync()
    {
        Task<StartOpenBladeResult>? pending;
        lock (gate)
        {
            startsSuppressed = true;
            startupAttempted = true;
            pending = activeStart;
        }

        if (pending is not null)
        {
            try
            {
                // Do not abandon an already-launched elevated helper: shutdown
                // must not race a service start still completing in that process.
                await pending.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The original caller observes the failure. It does not prevent
                // the user's subsequent shutdown from proceeding.
            }
        }
    }

    internal void ResumeManualStarts()
    {
        lock (gate)
        {
            startsSuppressed = false;
        }
    }
}
