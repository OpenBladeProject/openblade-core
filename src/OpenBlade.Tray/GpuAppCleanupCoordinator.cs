using System.Diagnostics;
using System.Globalization;
using OpenBlade.Session;

namespace OpenBlade.Tray;

internal enum GpuAppCleanupOutcomeKind
{
    AlreadyRunning,
    Cancelled,
    CountersUnavailable,
    PartialList,
    NothingToClose,
    SelectionCancelled,
    GracefulCloseRequestFailed,
    GracefulVerificationUnavailable,
    GracefullyClosed,
    ForceDeclined,
    ForceVerificationUnavailable,
    ForceCompleted,
    SessionAgentFailure,
}

internal sealed record GpuAppCleanupOutcome
{
    internal GpuAppCleanupOutcome(
        GpuAppCleanupOutcomeKind kind,
        IReadOnlyList<SessionGpuApp>? blockedApps = null,
        IReadOnlyList<SessionGpuApp>? otherApps = null,
        IReadOnlyList<SessionGpuApp>? survivors = null,
        IReadOnlyList<int>? failureProcessIds = null)
    {
        Kind = kind;
        BlockedApps = blockedApps?.ToArray() ?? [];
        OtherApps = otherApps?.ToArray() ?? [];
        Survivors = survivors?.ToArray() ?? [];
        FailureProcessIds = failureProcessIds?.ToArray() ?? [];
    }

    internal GpuAppCleanupOutcomeKind Kind { get; }

    internal IReadOnlyList<SessionGpuApp> BlockedApps { get; }

    internal IReadOnlyList<SessionGpuApp> OtherApps { get; }

    internal IReadOnlyList<SessionGpuApp> Survivors { get; }

    internal IReadOnlyList<int> FailureProcessIds { get; }
}

internal sealed class GpuAppCleanupCoordinator
{
    private static readonly TimeSpan DefaultGracefulCloseSettleDelay =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultForceCloseSettleBudget =
        TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultForceCloseRetryInterval =
        TimeSpan.FromMilliseconds(750);

    private readonly Func<CancellationToken, Task<SessionGpuResponse>> queryAsync;
    private readonly Func<
        IReadOnlyList<SessionGpuApp>,
        bool,
        CancellationToken,
        Task<SessionGpuResponse>> closeAsync;
    private readonly Func<bool, CancellationToken, Task> setVisibilityAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan gracefulCloseSettleDelay;
    private readonly TimeSpan forceCloseSettleBudget;
    private readonly TimeSpan forceCloseRetryInterval;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly CancellationTokenSource actionLifetime = new();
    private readonly GpuAppCleanupVisibilityTracker visibilityTracker = new();
    private int actionActive;

    internal GpuAppCleanupCoordinator(
        Func<CancellationToken, Task<SessionGpuResponse>> queryAsync,
        Func<
            IReadOnlyList<SessionGpuApp>,
            bool,
            CancellationToken,
            Task<SessionGpuResponse>> closeAsync,
        Func<bool, CancellationToken, Task> setVisibilityAsync,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        TimeProvider? timeProvider = null,
        TimeSpan? gracefulCloseSettleDelay = null,
        TimeSpan? forceCloseSettleBudget = null,
        TimeSpan? forceCloseRetryInterval = null)
    {
        this.queryAsync = queryAsync ?? throw new ArgumentNullException(nameof(queryAsync));
        this.closeAsync = closeAsync ?? throw new ArgumentNullException(nameof(closeAsync));
        this.setVisibilityAsync = setVisibilityAsync
            ?? throw new ArgumentNullException(nameof(setVisibilityAsync));
        this.delayAsync = delayAsync
            ?? ((delay, cancellationToken) => Task.Delay(delay, cancellationToken));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.gracefulCloseSettleDelay = gracefulCloseSettleDelay
            ?? DefaultGracefulCloseSettleDelay;
        this.forceCloseSettleBudget = forceCloseSettleBudget
            ?? DefaultForceCloseSettleBudget;
        this.forceCloseRetryInterval = forceCloseRetryInterval
            ?? DefaultForceCloseRetryInterval;
        ArgumentOutOfRangeException.ThrowIfLessThan(
            this.gracefulCloseSettleDelay,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            this.forceCloseSettleBudget,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            this.forceCloseRetryInterval,
            TimeSpan.Zero);
    }

    internal void Cancel() => actionLifetime.Cancel();

    internal async Task ObserveAsync(CancellationToken cancellationToken)
    {
        if (!await operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            SessionGpuResponse scan = await QueryAsync(cancellationToken).ConfigureAwait(false);
            await ApplyVisibilityAsync(scan, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task<GpuAppCleanupOutcome> RunAsync(
        Func<
            IReadOnlyList<SessionGpuApp>,
            IReadOnlyList<SessionGpuApp>,
            CancellationToken,
            Task<IReadOnlyList<SessionGpuApp>>> selectAppsAsync,
        Func<SessionGpuApp, CancellationToken, Task<bool>> confirmForceCloseAsync,
        Func<GpuAppCleanupOutcome, Task> presentOutcomeAsync,
        Action? started = null)
    {
        ArgumentNullException.ThrowIfNull(selectAppsAsync);
        ArgumentNullException.ThrowIfNull(confirmForceCloseAsync);
        ArgumentNullException.ThrowIfNull(presentOutcomeAsync);

        // This attended path intentionally captures the caller's context so
        // selection, force confirmation, and outcome presentation stay UI-affine.
        if (Interlocked.CompareExchange(ref actionActive, 1, 0) != 0)
        {
            return new(GpuAppCleanupOutcomeKind.AlreadyRunning);
        }

        CancellationToken actionToken = actionLifetime.Token;
        bool gateAcquired = false;
        try
        {
            started?.Invoke();
            await operationGate.WaitAsync(actionToken);
            gateAcquired = true;

            SessionGpuResponse scan = await QueryAsync(actionToken);
            await ApplyVisibilityAsync(scan, actionToken);
            if (scan.Status == SessionGpuOperationStatus.Failed)
            {
                return await PresentAsync(
                    new(GpuAppCleanupOutcomeKind.CountersUnavailable),
                    presentOutcomeAsync);
            }

            if (scan.Status == SessionGpuOperationStatus.PartiallySucceeded)
            {
                return await PresentAsync(
                    new(GpuAppCleanupOutcomeKind.PartialList),
                    presentOutcomeAsync);
            }

            SessionGpuApp[] eligible = scan.Apps.Where(app => app.CanClose).ToArray();
            SessionGpuApp[] blocked = scan.Apps.Where(app => !app.CanClose).ToArray();
            if (eligible.Length == 0)
            {
                return await PresentAsync(
                    new(GpuAppCleanupOutcomeKind.NothingToClose, blockedApps: blocked),
                    presentOutcomeAsync);
            }

            IReadOnlyList<SessionGpuApp> selected = await selectAppsAsync(
                eligible,
                blocked,
                actionToken);
            actionToken.ThrowIfCancellationRequested();
            if (selected.Count == 0)
            {
                return new(GpuAppCleanupOutcomeKind.SelectionCancelled);
            }

            SessionGpuResponse gracefulClose = await closeAsync(
                selected,
                false,
                actionToken);
            TraceResponseDiagnostics("graceful close", gracefulClose);
            if (GpuAppCleanupPolicy.ShouldAbortGracefulClose(
                gracefulClose.Status == SessionGpuOperationStatus.Failed,
                gracefulClose.Results.Count))
            {
                return await PresentAsync(
                    new(GpuAppCleanupOutcomeKind.GracefulCloseRequestFailed),
                    presentOutcomeAsync);
            }

            await delayAsync(gracefulCloseSettleDelay, actionToken);
            SessionGpuResponse afterClose = await QueryAsync(actionToken);
            await ApplyVisibilityAsync(afterClose, actionToken);
            if (afterClose.Status is SessionGpuOperationStatus.Failed
                or SessionGpuOperationStatus.PartiallySucceeded)
            {
                return await PresentAsync(
                    new(GpuAppCleanupOutcomeKind.GracefulVerificationUnavailable),
                    presentOutcomeAsync);
            }

            SessionGpuApp[] remaining = MatchTargets(afterClose.Apps, selected);
            if (remaining.Length == 0)
            {
                SessionGpuApp[] otherApps = ExceptTargets(afterClose.Apps, selected);
                return await PresentAsync(
                    new(GpuAppCleanupOutcomeKind.GracefullyClosed, otherApps: otherApps),
                    presentOutcomeAsync);
            }

            var forceOutcome = new GpuAppForceCloseOutcomeTracker();
            foreach (SessionGpuApp app in remaining)
            {
                bool approved = await confirmForceCloseAsync(app, actionToken);
                actionToken.ThrowIfCancellationRequested();
                if (!approved)
                {
                    continue;
                }

                GpuAppProcessIdentity forceTarget = ToIdentity(app);
                forceOutcome.Approve(forceTarget);
                SessionGpuResponse forcedApp = await closeAsync(
                    [app],
                    true,
                    actionToken);
                TraceResponseDiagnostics("force close", forcedApp);
                foreach (SessionGpuActionResult result in forcedApp.Results.Where(
                    result => !result.Succeeded))
                {
                    // The tracker suppresses failures for targets that disappear
                    // before the final scan. Invariant PID tokens keep that existing
                    // reconciliation behavior free of UI localization.
                    forceOutcome.RecordFailure(
                        forceTarget,
                        result.ProcessId.ToString(CultureInfo.InvariantCulture));
                }

                if (forcedApp.Status == SessionGpuOperationStatus.Failed
                    && forcedApp.Results.Count == 0)
                {
                    forceOutcome.RecordFailure(
                        forceTarget,
                        app.ProcessId.ToString(CultureInfo.InvariantCulture));
                }
            }

            if (!forceOutcome.HasApprovedTargets)
            {
                return new(GpuAppCleanupOutcomeKind.ForceDeclined);
            }

            SessionGpuResponse finalScan = await GpuAppForceCloseVerification.VerifyAsync(
                QueryAsync,
                response => response.Status != SessionGpuOperationStatus.Succeeded
                    || response.Apps.Any(
                        app => forceOutcome.WasApproved(ToIdentity(app))),
                delayAsync,
                timeProvider,
                forceCloseSettleBudget,
                forceCloseRetryInterval,
                actionToken);
            await ApplyVisibilityAsync(finalScan, actionToken);
            if (finalScan.Status is SessionGpuOperationStatus.Failed
                or SessionGpuOperationStatus.PartiallySucceeded)
            {
                return await PresentAsync(
                    new(GpuAppCleanupOutcomeKind.ForceVerificationUnavailable),
                    presentOutcomeAsync);
            }

            SessionGpuApp[] survivors = finalScan.Apps
                .Where(app => forceOutcome.WasApproved(ToIdentity(app)))
                .ToArray();
            SessionGpuApp[] finalOtherApps = finalScan.Apps
                .Where(app => !forceOutcome.WasApproved(ToIdentity(app)))
                .ToArray();
            int[] failureProcessIds = forceOutcome
                .ReconcileFailures(survivors.Select(ToIdentity))
                // Every token above is produced from an Int32 with invariant culture.
                .Select(value => int.Parse(value, CultureInfo.InvariantCulture))
                .ToArray();
            return await PresentAsync(
                new(
                    GpuAppCleanupOutcomeKind.ForceCompleted,
                    otherApps: finalOtherApps,
                    survivors: survivors,
                    failureProcessIds: failureProcessIds),
                presentOutcomeAsync);
        }
        catch (OperationCanceledException) when (actionToken.IsCancellationRequested)
        {
            return new(GpuAppCleanupOutcomeKind.Cancelled);
        }
        catch (Exception exception) when (IsSessionOperationException(exception))
        {
            Trace.TraceWarning(
                "OpenBlade GPU app cleanup failed: {0}",
                exception);
            return await PresentAsync(
                new(GpuAppCleanupOutcomeKind.SessionAgentFailure),
                presentOutcomeAsync);
        }
        finally
        {
            if (gateAcquired)
            {
                operationGate.Release();
            }

            Interlocked.Exchange(ref actionActive, 0);
        }
    }

    private async Task<SessionGpuResponse> QueryAsync(CancellationToken cancellationToken)
    {
        try
        {
            SessionGpuResponse response = await queryAsync(cancellationToken)
                .ConfigureAwait(false);
            TraceResponseDiagnostics("query", response);
            return response;
        }
        catch (Exception exception) when (
            !cancellationToken.IsCancellationRequested
            && IsSessionOperationException(exception))
        {
            bool visible = visibilityTracker.ObserveFailedScan();
            await setVisibilityAsync(visible, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private Task ApplyVisibilityAsync(
        SessionGpuResponse scan,
        CancellationToken cancellationToken)
    {
        bool visible = scan.Status != SessionGpuOperationStatus.Succeeded
            ? visibilityTracker.ObserveFailedScan()
            : visibilityTracker.ObserveSuccessfulScan(
                scan.Apps.Any(app => app.CanClose));
        return setVisibilityAsync(visible, cancellationToken);
    }

    private static async Task<GpuAppCleanupOutcome> PresentAsync(
        GpuAppCleanupOutcome outcome,
        Func<GpuAppCleanupOutcome, Task> presentOutcomeAsync)
    {
        await presentOutcomeAsync(outcome);
        return outcome;
    }

    private static bool IsSessionOperationException(Exception exception) =>
        exception is IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or OperationCanceledException
            or TimeoutException;

    private static void TraceResponseDiagnostics(
        string operation,
        SessionGpuResponse response)
    {
        if (!string.IsNullOrWhiteSpace(response.Error))
        {
            Trace.TraceWarning(
                "OpenBlade GPU app {0} response: {1}",
                operation,
                response.Error);
        }

        foreach (SessionGpuApp app in response.Apps.Where(
            app => !string.IsNullOrWhiteSpace(app.BlockReason)))
        {
            Trace.TraceInformation(
                "OpenBlade GPU app {0} blocked PID {1}: {2}",
                operation,
                app.ProcessId,
                app.BlockReason);
        }

        foreach (SessionGpuActionResult result in response.Results.Where(
            result => !string.IsNullOrWhiteSpace(result.Error)))
        {
            Trace.TraceWarning(
                "OpenBlade GPU app {0} action PID {1}: {2}",
                operation,
                result.ProcessId,
                result.Error);
        }
    }

    private static SessionGpuApp[] MatchTargets(
        IReadOnlyList<SessionGpuApp> current,
        IReadOnlyList<SessionGpuApp> targets) => GpuAppProcessIdentityFilter.MatchTargets(
        current,
        targets,
        ToIdentity);

    private static SessionGpuApp[] ExceptTargets(
        IReadOnlyList<SessionGpuApp> current,
        IReadOnlyList<SessionGpuApp> targets) => GpuAppProcessIdentityFilter.ExceptTargets(
        current,
        targets,
        ToIdentity);

    private static GpuAppProcessIdentity ToIdentity(SessionGpuApp app) => new(
        app.ProcessId,
        app.StartTimeUtcTicks,
        app.Name);
}
