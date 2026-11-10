namespace OpenBlade.Service;

// Owns the verified native handle for one published context. The shared gate
// also keeps a sidecar write from crossing an aggregate recovery boundary.
internal sealed class UserConfigurationWriterScope(
    IDisposable lease,
    string markerPath) : IAsyncDisposable
{
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly object retirementGate = new();
    private Task? retirement;
    private int retiring;

    internal void CheckRoot(string expectedMarkerPath)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref retiring) != 0, this);
        if (!string.Equals(markerPath, expectedMarkerPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The writer lease belongs to another user configuration.");
        }
    }

    internal async Task<IDisposable> EnterAsync(
        CancellationToken cancellationToken,
        bool checkAggregateRecovery = true)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref retiring) != 0, this);
        await writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref retiring) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (checkAggregateRecovery)
            {
                BatteryConfigurationStore.CheckRecoveryRequired(markerPath);
            }
            return new WriteAdmission(writes);
        }
        catch
        {
            writes.Release();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (retirementGate)
        {
            return new ValueTask(retirement ??= RetireAsync());
        }
    }

    private async Task RetireAsync()
    {
        Volatile.Write(ref retiring, 1);
        await writes.WaitAsync().ConfigureAwait(false);
        try
        {
            lease.Dispose();
        }
        finally
        {
            writes.Release();
        }
    }

    private sealed class WriteAdmission(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
