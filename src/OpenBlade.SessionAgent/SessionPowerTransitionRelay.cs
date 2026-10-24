using System.Diagnostics;
using OpenBlade.Contracts;

namespace OpenBlade.SessionAgent;

/// <summary>
/// Relays native suspend/resume without a dispatcher hop. One asynchronous
/// report may be in flight, with only the latest transition pending. The sink
/// must return its Task promptly, as ServiceClient does.
/// </summary>
internal sealed class SessionPowerTransitionRelay : IDisposable
{
    private readonly object sync = new();
    private readonly Guid instanceId;
    private readonly long instanceStartedQpc;
    private readonly Func<ReportSessionPowerTransitionRequest, CancellationToken, Task<ControlOperationResult>> report;
    private readonly Func<long> timestamp;
    private readonly long frequency;
    private readonly TimeSpan callbackBudget;
    private Transition? active;
    private Transition? pending;
    private bool? lastSuspending;
    private long sequence;
    private bool disposed;

    internal SessionPowerTransitionRelay(
        Guid instanceId,
        long instanceStartedQpc,
        Func<ReportSessionPowerTransitionRequest, CancellationToken, Task<ControlOperationResult>> report,
        Func<long>? timestamp = null,
        long? frequency = null,
        TimeSpan? callbackBudget = null)
    {
        this.instanceId = instanceId;
        this.instanceStartedQpc = instanceStartedQpc;
        this.report = report ?? throw new ArgumentNullException(nameof(report));
        this.timestamp = timestamp ?? Stopwatch.GetTimestamp;
        this.frequency = frequency ?? Stopwatch.Frequency;
        this.callbackBudget = callbackBudget
            ?? TimeSpan.FromMilliseconds(SessionPowerTransition.MaximumAgeMilliseconds);
        if (this.frequency <= 0 || this.callbackBudget <= TimeSpan.Zero
            || this.callbackBudget.TotalMilliseconds > SessionPowerTransition.MaximumAgeMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(callbackBudget));
        }
    }

    internal void OnPowerNotification(uint type)
    {
        bool? suspending = type switch
        {
            4 => true,
            7 or 18 => false,
            _ => null,
        };
        if (suspending is null)
        {
            return;
        }

        Transition transition;
        bool start;
        lock (sync)
        {
            if (disposed || lastSuspending == suspending)
            {
                return;
            }
            lastSuspending = suspending;
            transition = new Transition(new ReportSessionPowerTransitionRequest(
                OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), instanceId,
                instanceStartedQpc, ++sequence, timestamp(), suspending.Value));
            pending?.Completion.TrySetResult();
            pending?.Cancellation.Dispose();
            pending = null;
            start = active is null;
            if (start)
            {
                active = transition;
            }
            else
            {
                pending = transition;
                Cancel(active!);
            }
        }
        if (start)
        {
            // Start the asynchronous pipe attempt on the native callback now.
            // Task.Run alone could postpone the attempt until after suspension.
            _ = SendAsync(transition);
        }

        if (!transition.Request.Suspending)
        {
            // Resume has no preparation deadline for the native callback.
            // Keep the pipe attempt bounded without delaying Windows' wake work.
            _ = ObserveResumeDeadlineAsync(transition);
            return;
        }

        // Windows' suspend budget is shared. This bounded wait gives the
        // service a chance to complete; it cannot guarantee completion.
        TimeSpan remaining = Remaining(transition);
        if (remaining > TimeSpan.Zero && transition.Completion.Task.Wait(remaining))
        {
            return;
        }
        ExpireTransition(transition);
    }

    private async Task ObserveResumeDeadlineAsync(Transition transition)
    {
        TimeSpan remaining = Remaining(transition);
        if (remaining > TimeSpan.Zero)
        {
            try
            {
                await transition.Completion.Task.WaitAsync(remaining).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException)
            {
            }
        }
        ExpireTransition(transition);
    }

    private void ExpireTransition(Transition transition)
    {
        lock (sync)
        {
            if (ReferenceEquals(pending, transition))
            {
                pending = null;
                transition.Cancellation.Dispose();
                transition.Completion.TrySetResult();
            }
            else if (ReferenceEquals(active, transition))
            {
                Cancel(transition);
            }
        }
        Trace.TraceInformation("OpenBlade session power relay: sequence {0}, suspend {1}, callback budget elapsed.",
            transition.Request.Sequence, transition.Request.Suspending);
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            pending?.Completion.TrySetResult();
            pending?.Cancellation.Dispose();
            pending = null;
            if (active is not null)
            {
                Cancel(active);
                active.Completion.TrySetResult();
            }
        }
    }

    private async Task SendAsync(Transition transition)
    {
        while (true)
        {
            await ReportOneAsync(transition).ConfigureAwait(false);
            Task? cancellation;
            lock (sync)
            {
                transition.Completion.TrySetResult();
                transition.Finishing = true;
                cancellation = transition.CancellationTask;
            }
            // Retain the single active slot until its cancellation callbacks
            // finish too. A noncooperative sink cannot accumulate callbacks or
            // cancellation resources by completing reports before cancellation.
            if (cancellation is not null)
            {
                await cancellation.ConfigureAwait(false);
            }
            lock (sync)
            {
                transition.Cancellation.Dispose();
                active = pending;
                pending = null;
                if (active is null)
                {
                    return;
                }
                transition = active;
            }
        }
    }

    private async Task ReportOneAsync(Transition transition)
    {
        try
        {
            Task<ControlOperationResult> operation;
            lock (sync)
            {
                // Check QPC immediately before each send, including pending
                // work after resume. Cancellation timers may pause in sleep.
                if (disposed || Remaining(transition) <= TimeSpan.Zero
                    || transition.Cancellation.IsCancellationRequested)
                {
                    Trace.TraceInformation("OpenBlade session power relay: sequence {0}, expired or superseded before send.",
                        transition.Request.Sequence);
                    return;
                }
                operation = report(transition.Request, transition.Cancellation.Token);
            }
            ControlOperationResult result = await operation.ConfigureAwait(false);
            Trace.TraceInformation("OpenBlade session power relay: sequence {0}, suspend {1}, result {2}.",
                transition.Request.Sequence, transition.Request.Suspending, result.Status);
        }
        catch (OperationCanceledException)
        {
            Trace.TraceInformation("OpenBlade session power relay: sequence {0}, canceled.",
                transition.Request.Sequence);
        }
        catch (Exception exception)
        {
            DiagnosticTrace.TraceException(
                "OpenBlade.SessionAgent.SessionPowerTransitionRelay",
                "ReportTransition",
                exception);
        }
    }
    private TimeSpan Remaining(Transition transition)
    {
        long now = timestamp();
        long captured = transition.Request.TimestampQpc;
        if (now < captured || captured < instanceStartedQpc)
        {
            return TimeSpan.Zero;
        }
        return callbackBudget - TimeSpan.FromSeconds((now - captured) / (double)frequency);
    }

    private static void Cancel(Transition transition)
    {
        // Mark cancellation now without allowing sink cancellation callbacks to
        // hold the native notification thread hostage.
        if (!transition.Finishing && transition.CancellationTask is null)
        {
            transition.CancellationTask =
                ObserveCancellationAsync(transition.Cancellation.CancelAsync());
        }
    }

    private static async Task ObserveCancellationAsync(Task cancellation)
    {
        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            DiagnosticTrace.TraceException(
                "OpenBlade.SessionAgent.SessionPowerTransitionRelay",
                "ObserveCancellation",
                exception);
        }
    }

    private sealed class Transition(ReportSessionPowerTransitionRequest request)
    {
        internal ReportSessionPowerTransitionRequest Request { get; } = request;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal Task? CancellationTask { get; set; }
        internal bool Finishing { get; set; }
        internal TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
