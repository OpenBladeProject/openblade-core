namespace OpenBlade.Service;

// Native firmware getters are synchronous. A cancelled caller cannot interrupt
// one in the provider, so retain the single-worker lease until it really exits.
// Further requests wait within their deadline instead of creating more workers.
internal static class CpuFirmwareReadScheduler
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    internal static async Task<T> RunAsync<T>(Func<T> read, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        CancellationToken workerToken = deadline.Token;
        await Gate.WaitAsync(workerToken).ConfigureAwait(false);
        Task<T> worker = Task.Run(() =>
        {
            try
            {
                workerToken.ThrowIfCancellationRequested();
                T result = read();
                workerToken.ThrowIfCancellationRequested();
                return result;
            }
            finally { Gate.Release(); }
        }, CancellationToken.None);
        try { return await worker.WaitAsync(workerToken).ConfigureAwait(false); }
        catch
        {
            _ = worker.ContinueWith(static completed => { _ = completed.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted
                    | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }
}
