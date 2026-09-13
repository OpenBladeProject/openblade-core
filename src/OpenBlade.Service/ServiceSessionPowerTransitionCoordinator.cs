using OpenBlade.Contracts;

namespace OpenBlade.Service;

/// <summary>
/// Joins native service notifications and the active Session Agent relay into
/// one bounded lifecycle operation. The gate changes before hardware work waits.
/// </summary>
public sealed class ServiceSessionPowerTransitionCoordinator
{
    private static readonly TimeSpan MaximumAge =
        TimeSpan.FromMilliseconds(SessionPowerTransition.MaximumAgeMilliseconds);
    private readonly object sync = new();
    private readonly IServiceControlRuntime runtime;
    private readonly LowPowerGate gate;
    private readonly CancellationToken stoppingToken;
    private AgentInstance? agentInstance;
    private long lastSequence;
    private Work? active;
    private Work? pending;
    private long lastCompletedGeneration = -1;
    private ControlOperationResult? lastCompletedResult;

    public ServiceSessionPowerTransitionCoordinator(
        IServiceControlRuntime runtime,
        LowPowerGate gate,
        IHostApplicationLifetime? applicationLifetime = null)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
        stoppingToken = applicationLifetime?.ApplicationStopping ?? CancellationToken.None;
        gate.TransitionChanged += CancelSupersededWork;
    }

    private void CancelSupersededWork()
    {
        lock (sync)
        {
            if (active is { } work && work.Transition.Generation != gate.Generation)
            {
                work.CancelOnce();
            }
        }
    }

    public async Task<ControlOperationResult> ReportAsync(
        ReportSessionPowerTransitionRequest request,
        ServiceClientContext client,
        Func<bool> stillAuthorized,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(stillAuthorized);
        if (cancellationToken.IsCancellationRequested || !stillAuthorized()
            || request.InstanceId == Guid.Empty || request.InstanceStartedQpc <= 0
            || request.Sequence <= 0 || request.TimestampQpc < request.InstanceStartedQpc
            || !IsTimely(request.TimestampQpc))
        {
            return Rejected("The session power notification is unauthorized, invalid, or expired.");
        }

        var incoming = new AgentInstance(client.UserSid.Value, client.SessionId,
            request.InstanceId, request.InstanceStartedQpc);
        LowPowerTransition transition;
        lock (sync)
        {
            if (agentInstance is { } previous)
            {
                bool sameSession = previous.UserSid == incoming.UserSid
                    && previous.SessionId == incoming.SessionId;
                if (sameSession && (incoming.StartedQpc < previous.StartedQpc
                    || (incoming.StartedQpc == previous.StartedQpc
                        && incoming.InstanceId != previous.InstanceId)
                    || (incoming.InstanceId == previous.InstanceId
                        && (incoming.StartedQpc != previous.StartedQpc
                            || request.Sequence <= lastSequence))))
                {
                    return Rejected("The session power notification belongs to a stale agent or sequence.");
                }
            }

            if (!gate.TryApply(request.Suspending, request.TimestampQpc, out transition,
                newResume: !request.Suspending))
            {
                return Rejected("A newer Windows power transition has already been accepted.");
            }
            agentInstance = incoming;
            lastSequence = request.Sequence;
        }

        bool AuthorizedInstance()
        {
            if (!stillAuthorized())
            {
                return false;
            }
            lock (sync)
            {
                return agentInstance == incoming;
            }
        }

        Task<ControlOperationResult> completion = Queue(transition, AuthorizedInstance,
            request.Suspending ? cancellationToken : stoppingToken);
        TimeSpan remaining = Remaining(request.TimestampQpc);
        if (remaining <= TimeSpan.Zero)
        {
            return Accepted("The notification was accepted but its completion deadline expired.");
        }
        try
        {
            return await completion.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return Accepted("The notification was accepted; lifecycle completion was not confirmed before the deadline.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Accepted("The notification was accepted; the caller stopped waiting for completion.");
        }
    }

    internal Task<ControlOperationResult> ProcessNativeAsync(
        bool suspending,
        CancellationToken cancellationToken)
    {
        LowPowerTransition transition = gate.CurrentTransition;
        if (transition.Suspending != suspending)
        {
            return Task.FromResult(Accepted("The queued native power notification was superseded."));
        }
        if (transition.TimestampQpc == 0)
        {
            gate.TryApply(suspending, gate.GetTimestamp(), out transition);
        }
        return Queue(transition, static () => true, cancellationToken);
    }

    private Task<ControlOperationResult> Queue(
        LowPowerTransition transition,
        Func<bool> stillAuthorized,
        CancellationToken cancellationToken)
    {
        Work? start = null;
        Task<ControlOperationResult> result;
        lock (sync)
        {
            if (transition.Generation != gate.Generation
                || transition.Generation < lastCompletedGeneration
                || (pending is not null && transition.Generation < pending.Transition.Generation))
            {
                return Task.FromResult(Accepted("The accepted power notification was superseded before queuing."));
            }
            if (lastCompletedGeneration == transition.Generation)
            {
                return Task.FromResult(lastCompletedResult!);
            }
            if (active?.Transition.Generation == transition.Generation)
            {
                return active.Completion.Task;
            }
            if (pending?.Transition.Generation == transition.Generation)
            {
                return pending.Completion.Task;
            }
            var work = new Work(transition, stillAuthorized, cancellationToken);
            result = work.Completion.Task;
            if (active is null)
            {
                active = start = work;
            }
            else
            {
                if (pending is not null)
                {
                    pending.Completion.TrySetResult(Accepted("A newer power transition replaced the pending notification."));
                    pending.Cancellation.Dispose();
                }
                pending = work;
                active.CancelOnce();
            }
        }
        if (start is not null)
        {
            // A synchronous native probe must not consume the caller's bounded
            // IPC wait before its first await. There is only one active runner.
            _ = Task.Run(() => ProcessQueueAsync(start));
        }
        return result;
    }

    private async Task ProcessQueueAsync(Work work)
    {
        while (true)
        {
            ControlOperationResult result;
            try
            {
                result = await ProcessAsync(work).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                result = new(ControlOperationStatus.Failed,
                    $"Power notification processing failed: {exception.Message}");
            }
            Task cancellationCompletion;
            lock (sync)
            {
                cancellationCompletion = work.SealCancellation();
            }
            try
            {
                await cancellationCompletion.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                result = new(ControlOperationStatus.Failed,
                    $"Power notification cancellation failed: {exception.Message}");
            }
            work.Completion.TrySetResult(result);
            lock (sync)
            {
                lastCompletedGeneration = work.Transition.Generation;
                lastCompletedResult = result;
                active = pending;
                pending = null;
                work.Cancellation.Dispose();
                if (active is null)
                {
                    return;
                }
                work = active;
            }
        }
    }

    private async Task<ControlOperationResult> ProcessAsync(Work work)
    {
        LowPowerTransition transition = work.Transition;
        bool IsCurrent() => gate.Generation == transition.Generation
            && (!transition.Suspending || IsTimely(transition.TimestampQpc)) && work.StillAuthorized()
            && !work.CancellationToken.IsCancellationRequested;
        if (!IsCurrent()
            && !(transition.Suspending && gate.IsActiveForGeneration(transition.Generation)))
        {
            return Accepted("The accepted power notification expired or was superseded before processing.");
        }
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(work.CancellationToken);
        if (transition.Suspending)
        {
            TimeSpan remaining = Remaining(transition.TimestampQpc);
            if (remaining <= TimeSpan.Zero)
            {
                // Keep expired writes canceled while allowing runtime to retire
                // the original renderer through its generation-only cleanup.
                bounded.Cancel();
            }
            else
            {
                bounded.CancelAfter(remaining);
            }
        }
        try
        {
            if (transition.Suspending)
            {
                ControlOperationResult prepared = await runtime.PrepareForLowPowerAsync(
                    transition.Generation, IsCurrent, bounded.Token).ConfigureAwait(false);
                return IsCurrent()
                    ? prepared
                    : Accepted($"Suspend processing ended after its context expired. {prepared.Message}");
            }

            // Prepare owns retained, no-write invalidation. Resume must never
            // trigger a second generic close with an unguarded Normal restore.
            await runtime.InvalidateLightingRendererForLowPowerAsync(bounded.Token).ConfigureAwait(false);
            if (!IsCurrent())
            {
                return Accepted("Resume processing expired or was superseded during retained-port cleanup.");
            }
            await runtime.BeginResumeValidationAsync(bounded.Token).ConfigureAwait(false);
            if (IsCurrent())
            {
                await runtime.HandlePowerSourceChangedAsync(bounded.Token).ConfigureAwait(false);
            }
            if (IsCurrent())
            {
                await runtime.RestoreLightingRendererAsync(bounded.Token).ConfigureAwait(false);
            }
            if (!IsCurrent())
            {
                await runtime.CancelResumeValidationAsync(CancellationToken.None).ConfigureAwait(false);
                return Accepted("Resume processing was superseded or exceeded its deadline.");
            }
            await runtime.CompleteResumeValidationAsync(bounded.Token).ConfigureAwait(false);
            return new(ControlOperationStatus.Applied, "Resume notification processing completed.");
        }
        catch (OperationCanceledException) when (bounded.IsCancellationRequested)
        {
            if (!transition.Suspending)
            {
                await runtime.CancelResumeValidationAsync(CancellationToken.None).ConfigureAwait(false);
            }
            return Accepted("The notification was accepted but lifecycle processing exceeded its deadline.");
        }
    }

    private bool IsTimely(long timestamp) => timestamp > 0
        && gate.GetTimestamp() >= timestamp && Remaining(timestamp) > TimeSpan.Zero;

    private TimeSpan Remaining(long timestamp) => MaximumAge
        - gate.TimeProvider.GetElapsedTime(timestamp, gate.GetTimestamp());

    private static ControlOperationResult Accepted(string message) =>
        new(ControlOperationStatus.Stored, message);

    private static ControlOperationResult Rejected(string message) =>
        new(ControlOperationStatus.Blocked, message);

    private sealed record AgentInstance(string UserSid, int SessionId, Guid InstanceId, long StartedQpc);

    private sealed class Work(
        LowPowerTransition transition,
        Func<bool> stillAuthorized,
        CancellationToken cancellationToken)
    {
        private bool cancellationRequested;

        internal LowPowerTransition Transition { get; } = transition;
        internal Func<bool> StillAuthorized { get; } = stillAuthorized;
        internal CancellationTokenSource Cancellation { get; } =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        internal CancellationToken CancellationToken => Cancellation.Token;
        internal Task CancellationCompletion { get; private set; } = Task.CompletedTask;
        internal TaskCompletionSource<ControlOperationResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void CancelOnce()
        {
            if (!cancellationRequested)
            {
                cancellationRequested = true;
                CancellationCompletion = Cancellation.CancelAsync();
            }
        }

        internal Task SealCancellation()
        {
            cancellationRequested = true;
            return CancellationCompletion;
        }
    }
}
