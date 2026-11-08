using System.Diagnostics;

namespace OpenBlade.Platform;

/// <summary>Obtains fresh CPU readings without allowing native sensor calls into its owner process.</summary>
public sealed class WindowsCpuTemperatureReader : IDisposable
{
    private readonly Func<ICpuTemperatureChild> startChild;
    private readonly TimeSpan readTimeout;
    private readonly TimeSpan stopTimeout;
    private readonly TimeSpan startupTimeout;
    private readonly TimeSpan retryBaseDelay;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object ownership = new();
    private readonly object telemetryStateGate = new();
    private WorkerLease? worker;
    private TelemetryFailure? lastFailure;
    private bool disposed;
    private long sequence;
    private int consecutiveFailures;
    private long lastFailedAt;
    private TimeSpan retryDelay;
    private bool deferColdSampling;

    public WindowsCpuTemperatureReader() : this(WindowsCpuTemperatureChild.Start,
        TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(250))
    { }

    internal WindowsCpuTemperatureReader(Func<ICpuTemperatureChild> startChild,
        TimeSpan readTimeout, TimeSpan stopTimeout, TimeSpan? startupTimeout = null,
        TimeSpan? retryBaseDelay = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(startChild);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(readTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(stopTimeout, TimeSpan.Zero);
        this.startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(10);
        this.retryBaseDelay = retryBaseDelay ?? TimeSpan.FromSeconds(1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(this.startupTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(this.retryBaseDelay, TimeSpan.Zero);
        this.startChild = startChild;
        this.readTimeout = readTimeout;
        this.stopTimeout = stopTimeout;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary>Completes bounded CPU-worker initialization without requesting a temperature.</summary>
    public async ValueTask<bool> PrepareAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        bool entered = false;
        try
        {
            entered = await serial.WaitAsync(startupTimeout, cancellation.Token).ConfigureAwait(false);
            if (!entered) { return false; }
            WorkerLease? lease = GetOrStartLease(out _);
            if (lease is null) { return false; }
            await lease.Ready.WaitAsync(Remaining(started, startupTimeout), cancellation.Token).ConfigureAwait(false);
            ICpuTemperatureChild child = await lease.Startup.ConfigureAwait(false);
            Task exited = ObserveExit(lease, child);
            if (exited.IsCompleted || child.HasExited)
            {
                Poison(lease, failure: exited.IsFaulted || exited.IsCanceled);
                return false;
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException
            or InvalidOperationException or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException or System.Security.SecurityException or OverflowException)
        {
            ReportFailure(TelemetryFailure.Unavailable);
            return false;
        }
        finally { if (entered) { serial.Release(); } }
    }

    public async ValueTask<int?> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        cancellation.CancelAfter(readTimeout);
        bool entered = false;
        bool sampling = false;
        bool warming = false;
        WorkerLease? lease = null;
        try
        {
            // The semaphore owns its timed wait. An outer WaitAsync could abandon
            // a waiter which later acquired the permit without a matching release.
            entered = await serial.WaitAsync(readTimeout, cancellation.Token).ConfigureAwait(false);
            if (!entered) { ReportFailure(TelemetryFailure.Timeout); return null; }

            lease = GetOrStartLease(out warming);
            if (lease is null) { return null; }

            // Caller latency stays bounded while independent cold initialization
            // retains its own budget and process ownership across sampling ticks.
            await lease.Ready.WaitAsync(Remaining(started), cancellation.Token).ConfigureAwait(false);
            if (warming && (deferColdSampling || Stopwatch.GetElapsedTime(started) > readTimeout / 2))
            {
                // Do not squeeze the first sample into a shortened budget after
                // slow readiness. The next call receives the full sampling deadline.
                ReportFailure(TelemetryFailure.Unavailable);
                return null;
            }
            ICpuTemperatureChild child = await lease.Startup.ConfigureAwait(false);
            Task exited = ObserveExit(lease, child);
            if (exited.IsCompleted || child.HasExited)
            {
                // Normal idle exit is recognized before writing to a dead stdin pipe.
                Poison(lease, failure: exited.IsFaulted || exited.IsCanceled);
                return null;
            }

            long requestSequence = Interlocked.Increment(ref sequence);
            sampling = true;
            Task<byte[]> exchange = Task.Run(() => child.ExchangeAsync(
                CpuTemperatureProtocol.EncodeRequest(requestSequence), cancellation.Token));
            ObserveFailure(exchange);
            byte[] frame = await exchange.WaitAsync(Remaining(started), cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) > readTimeout) { throw new TimeoutException(); }
            if (!CpuTemperatureProtocol.TryDecodeReply(frame, requestSequence, out int? value,
                out CpuTemperatureReplyStatus status) || status == CpuTemperatureReplyStatus.Ready)
            {
                Poison(lease);
                ReportFailure(TelemetryFailure.Malformed);
                return null;
            }
            if (status == CpuTemperatureReplyStatus.TerminalUnavailable)
            {
                Poison(lease);
                ReportFailure(TelemetryFailure.Terminal);
                return null;
            }
            if (value is null)
            {
                lock (ownership) { RegisterFailure(); }
                ReportFailure(TelemetryFailure.Unavailable);
            }
            else
            {
                lock (ownership) { consecutiveFailures = 0; retryDelay = TimeSpan.Zero; }
                ReportRecovery();
            }
            return value;
        }
        catch (OperationCanceledException)
        {
            // An abandoned active sample may have entered native code. Initialization,
            // however, belongs to the reader lifetime and survives one caller's timeout.
            if (sampling && lease is not null)
            {
                // A measured cold-call timeout demonstrates that this machine needs
                // its full sampling budget after readiness, even when init looked quick.
                if (warming && !cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
                {
                    deferColdSampling = true;
                }
                Poison(lease);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!lifetime.IsCancellationRequested) { ReportFailure(TelemetryFailure.Timeout); }
            return null;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException
            or InvalidOperationException or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException or System.Security.SecurityException or OverflowException)
        {
            if (sampling && lease is not null)
            {
                if (warming && exception is TimeoutException) { deferColdSampling = true; }
                Poison(lease);
            }
            ReportFailure(exception is TimeoutException ? TelemetryFailure.Timeout : TelemetryFailure.Unavailable);
            return null;
        }
        finally
        {
            if (entered) { serial.Release(); }
        }
    }

    private WorkerLease? GetOrStartLease(out bool warming)
    {
        lock (ownership)
        {
            warming = false;
            if (disposed) { return null; }
            if (worker?.Cleanup is { } cleanup)
            {
                // Completion means actual process exit, not expiry of the short join budget.
                if (!cleanup.IsCompletedSuccessfully) { return null; }
                worker = null;
            }
            if (clock.GetElapsedTime(lastFailedAt) < retryDelay) { return null; }
            if (worker is null)
            {
                var created = new WorkerLease(Task.Run(startChild));
                created.Ready = Task.Run(() => InitializeAsync(created));
                ObserveFailure(created.Ready);
                worker = created;
                warming = true;
            }
            else { warming = !worker.Ready.IsCompletedSuccessfully; }
            return worker;
        }
    }

    private async Task InitializeAsync(WorkerLease lease)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        cancellation.CancelAfter(startupTimeout);
        long started = Stopwatch.GetTimestamp();
        try
        {
            ICpuTemperatureChild child = await lease.Startup.WaitAsync(startupTimeout, cancellation.Token).ConfigureAwait(false);
            _ = ObserveExit(lease, child);
            cancellation.Token.ThrowIfCancellationRequested();
            long requestSequence = Interlocked.Increment(ref sequence);
            // Start() assigns the job before returning the child. Sending this
            // readiness request is the first action that admits native initialization.
            Task<byte[]> exchange = Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                return child.ExchangeAsync(CpuTemperatureProtocol.EncodeRequest(requestSequence), cancellation.Token);
            });
            ObserveFailure(exchange);
            TimeSpan remaining = startupTimeout - Stopwatch.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) { throw new TimeoutException(); }
            byte[] frame = await exchange.WaitAsync(remaining, cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!CpuTemperatureProtocol.TryDecodeReply(frame, requestSequence, out _, out CpuTemperatureReplyStatus status)
                || status != CpuTemperatureReplyStatus.Ready)
            {
                throw new InvalidOperationException("The CPU temperature worker did not confirm initialization readiness.");
            }
        }
        catch
        {
            Poison(lease, failure: !lifetime.IsCancellationRequested);
            throw;
        }
    }

    private Task ObserveExit(WorkerLease lease, ICpuTemperatureChild child)
    {
        lock (ownership)
        {
            try { lease.Exited ??= child.WaitForExitAsync(); }
            catch (Exception exception) { lease.Exited ??= Task.FromException(exception); }
            ObserveFailure(lease.Exited);
            return lease.Exited;
        }
    }

    // Caller holds ownership. Only failures advance backoff; no cached value is
    // returned during the pause. A healthy fresh sample resets the bounded delay.
    private void RegisterFailure()
    {
        consecutiveFailures = Math.Min(consecutiveFailures + 1, 16);
        double multiplier = Math.Pow(2, Math.Max(0, consecutiveFailures - 2));
        retryDelay = consecutiveFailures < 2 ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(Math.Min(30_000, retryBaseDelay.TotalMilliseconds * multiplier));
        lastFailedAt = clock.GetTimestamp();
    }

    private TimeSpan Remaining(long started) => Remaining(started, readTimeout);

    private static TimeSpan Remaining(long started, TimeSpan timeout)
    {
        TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(started);
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
                TelemetryFailure.Terminal => "CPU temperature telemetry unavailable: worker reported a terminal sensor failure.",
                TelemetryFailure.ExitUnconfirmed => "CPU temperature telemetry unavailable: worker exit is not yet confirmed; replacement remains fenced.",
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

    private enum TelemetryFailure { Unavailable, Timeout, Malformed, Terminal, ExitUnconfirmed }

    private void Poison(WorkerLease lease, bool failure = true)
    {
        lock (ownership)
        {
            if (failure && !lease.FailureRegistered)
            {
                lease.FailureRegistered = true;
                RegisterFailure();
            }
            lease.Cleanup ??= Task.Run(() => CleanupAsync(lease));
            ObserveFailure(lease.Cleanup);
        }
    }

    private async Task CleanupAsync(WorkerLease lease)
    {
        ICpuTemperatureChild child;
        try { child = await lease.Startup.ConfigureAwait(false); }
        catch { return; } // The factory must retain any process it fails to return.
        Task exited = ObserveExit(lease, child);
        try { child.RequestStop(); }
        catch { ReportFailure(TelemetryFailure.ExitUnconfirmed); }
        try { await exited.WaitAsync(stopTimeout).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            ReportFailure(TelemetryFailure.ExitUnconfirmed);
            // Keep the original observation alive. A later confirmed exit may safely
            // release the fence; a fault leaves the handle retained and fenced.
            await exited.ConfigureAwait(false);
        }
        catch
        {
            ReportFailure(TelemetryFailure.ExitUnconfirmed);
            throw;
        }
        child.Dispose();
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
        ObserveFailure(lifetime.CancelAsync());
        if (owned is not null) { Poison(owned, failure: false); }
        // Do not wait for stdio, startup, or a native driver on an owner's shutdown path.
    }

    private sealed class WorkerLease(Task<ICpuTemperatureChild> startup)
    {
        internal Task<ICpuTemperatureChild> Startup { get; } = startup;
        internal Task Ready { get; set; } = Task.CompletedTask;
        internal Task? Exited { get; set; }
        internal Task? Cleanup { get; set; }
        internal bool FailureRegistered { get; set; }
    }
}

internal interface ICpuTemperatureChild : IDisposable
{
    bool HasExited { get; }
    Task<byte[]> ExchangeAsync(byte[] request, CancellationToken cancellationToken);
    void RequestStop();
    // Returns a retained observation; must not perform a synchronous join.
    Task WaitForExitAsync();
}
