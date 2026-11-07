using System.Diagnostics;

namespace OpenBlade.Platform;

/// <summary>Obtains fresh CPU readings without allowing native sensor calls into its owner process.</summary>
public sealed class WindowsCpuTemperatureReader : IDisposable
{
    private readonly Func<ICpuTemperatureChild> startChild;
    private readonly TimeSpan readTimeout;
    private readonly TimeSpan stopTimeout;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object ownership = new();
    private readonly object telemetryStateGate = new();
    private WorkerLease? worker;
    private TelemetryFailure? lastFailure;
    private bool disposed;
    private long sequence;

    public WindowsCpuTemperatureReader() : this(WindowsCpuTemperatureChild.Start,
        TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(250))
    { }

    internal WindowsCpuTemperatureReader(Func<ICpuTemperatureChild> startChild,
        TimeSpan readTimeout, TimeSpan stopTimeout)
    {
        ArgumentNullException.ThrowIfNull(startChild);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(readTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stopTimeout, TimeSpan.Zero);
        this.startChild = startChild;
        this.readTimeout = readTimeout;
        this.stopTimeout = stopTimeout;
    }

    public async ValueTask<int?> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        cancellation.CancelAfter(readTimeout);
        bool entered = false;
        WorkerLease? lease = null;
        try
        {
            await serial.WaitAsync(cancellation.Token).WaitAsync(Remaining(started), cancellation.Token).ConfigureAwait(false);
            entered = true;
            bool workerUnavailable = false;
            lock (ownership)
            {
                if (disposed)
                {
                    workerUnavailable = true;
                }
                else if (worker?.Cleanup is { } cleanup)
                {
                    // A failed or still-running join retains ownership and fences replacement.
                    if (!cleanup.IsCompletedSuccessfully || !cleanup.Result) { workerUnavailable = true; }
                    else { worker = null; }
                }
                if (!workerUnavailable)
                {
                    worker ??= new WorkerLease(Task.Run(startChild));
                    lease = worker;
                }
            }
            if (workerUnavailable)
            {
                ReportFailure(TelemetryFailure.Unavailable);
                return null;
            }
            WorkerLease activeLease = lease!;
            ICpuTemperatureChild child = await activeLease.Startup.WaitAsync(Remaining(started), cancellation.Token).ConfigureAwait(false);
            long requestSequence = checked(++sequence);
            // WaitAsync bounds even a broken transport that ignores cancellation. No late
            // completion can publish a value: any abandoned exchange poisons this lease.
            byte[] request = CpuTemperatureProtocol.EncodeRequest(requestSequence);
            Task<byte[]> exchange = Task.Run(() => child.ExchangeAsync(request, cancellation.Token));
            ObserveFailure(exchange);
            byte[] frame = await exchange.WaitAsync(Remaining(started), cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) > readTimeout)
            {
                Poison(activeLease);
                ReportFailure(TelemetryFailure.Timeout);
                return null;
            }
            if (!CpuTemperatureProtocol.TryDecodeReply(frame, requestSequence, out int? value))
            {
                Poison(activeLease);
                ReportFailure(TelemetryFailure.Malformed);
                return null;
            }
            if (value is null) { ReportFailure(TelemetryFailure.Unavailable); }
            else { ReportRecovery(); }
            return value;
        }
        catch (OperationCanceledException)
        {
            if (lease is not null) { Poison(lease); }
            cancellationToken.ThrowIfCancellationRequested();
            if (!lifetime.IsCancellationRequested) { ReportFailure(TelemetryFailure.Timeout); }
            return null;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException
            or InvalidOperationException or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException or System.Security.SecurityException or OverflowException)
        {
            if (lease is not null) { Poison(lease); }
            ReportFailure(exception is TimeoutException ? TelemetryFailure.Timeout : TelemetryFailure.Unavailable);
            return null;
        }
        finally
        {
            if (entered) { serial.Release(); }
        }
    }

    private TimeSpan Remaining(long started)
    {
        TimeSpan remaining = readTimeout - Stopwatch.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero) { throw new TimeoutException(); }
        return remaining;
    }

    private void ReportFailure(TelemetryFailure failure)
    {
        lock (telemetryStateGate)
        {
            if (lastFailure == failure) { return; }
            lastFailure = failure;
        }
        try
        {
            Trace.TraceWarning(failure switch
            {
                TelemetryFailure.Timeout => "CPU temperature telemetry unavailable: read timed out.",
                TelemetryFailure.Malformed => "CPU temperature telemetry unavailable: worker reply was malformed.",
                _ => "CPU temperature telemetry unavailable: worker or sensor did not provide a reading.",
            });
        }
        catch { /* Diagnostic listeners must not affect telemetry availability. */ }
    }

    private void ReportRecovery()
    {
        lock (telemetryStateGate)
        {
            if (lastFailure is null) { return; }
            lastFailure = null;
        }
        try { Trace.TraceInformation("CPU temperature telemetry recovered."); }
        catch { /* Diagnostic listeners must not affect telemetry availability. */ }
    }

    private enum TelemetryFailure { Unavailable, Timeout, Malformed }

    private void Poison(WorkerLease lease)
    {
        lock (ownership)
        {
            lease.Cleanup ??= Task.Run(async () =>
            {
                ICpuTemperatureChild child;
                try { child = await lease.Startup.ConfigureAwait(false); }
                catch { return true; } // A factory must retain/stop any process it fails to return.
                try
                {
                    child.RequestStop();
                    Task exited = child.WaitForExitAsync();
                    ObserveFailure(exited);
                    await exited.WaitAsync(stopTimeout).ConfigureAwait(false);
                    child.Dispose();
                    return true;
                }
                catch { return false; } // Keep the lease and retained handle fenced.
            });
        }
    }

    private static void ObserveFailure(Task task) => _ = task.ContinueWith(
        completed => _ = completed.Exception, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public void Dispose()
    {
        WorkerLease? owned;
        lock (ownership)
        {
            if (disposed) { return; }
            disposed = true;
            owned = worker;
        }
        lifetime.Cancel();
        if (owned is not null) { Poison(owned); }
        // Do not wait for stdio, startup, or a native driver on an owner's shutdown path.
    }

    private sealed class WorkerLease(Task<ICpuTemperatureChild> startup)
    {
        internal Task<ICpuTemperatureChild> Startup { get; } = startup;
        internal Task<bool>? Cleanup { get; set; }
    }
}

internal interface ICpuTemperatureChild : IDisposable
{
    Task<byte[]> ExchangeAsync(byte[] request, CancellationToken cancellationToken);
    void RequestStop();
    Task WaitForExitAsync();
}
