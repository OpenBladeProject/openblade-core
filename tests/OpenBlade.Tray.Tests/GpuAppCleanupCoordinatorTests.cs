using OpenBlade.Session;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class GpuAppCleanupCoordinatorTests
{
    [Theory]
    [InlineData(SessionGpuOperationStatus.Failed, (int)GpuAppCleanupOutcomeKind.CountersUnavailable)]
    [InlineData(SessionGpuOperationStatus.PartiallySucceeded, (int)GpuAppCleanupOutcomeKind.PartialList)]
    public async Task IncompleteInitialScanNeverOffersCleanup(SessionGpuOperationStatus status, int expected)
    {
        var flow = new GpuAppCleanupFixture();
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(status, flow.App));

        var outcome = await flow.RunAsync();

        Assert.Equal((GpuAppCleanupOutcomeKind)expected, outcome.Kind);
        Assert.Equal(0, flow.SelectionCalls);
        Assert.Empty(flow.Closes);
    }

    [Fact]
    public async Task ProtectedAppsAreReportedWithoutOfferingCleanup()
    {
        var flow = new GpuAppCleanupFixture();
        var blocked = flow.App with { CanClose = false, BlockReason = "private diagnostic" };
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded, blocked));

        var outcome = await flow.RunAsync();

        Assert.Equal(GpuAppCleanupOutcomeKind.NothingToClose, outcome.Kind);
        Assert.Equal(blocked, Assert.Single(outcome.BlockedApps));
        Assert.Equal(0, flow.SelectionCalls);
        Assert.Empty(flow.Closes);
    }

    [Fact]
    public async Task DecliningSelectionNeverClosesProcesses()
    {
        var flow = new GpuAppCleanupFixture { Select = (_, _, _) => Task.FromResult<IReadOnlyList<SessionGpuApp>>([]) };
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded, flow.App));

        Assert.Equal(GpuAppCleanupOutcomeKind.SelectionCancelled, (await flow.RunAsync()).Kind);
        Assert.Empty(flow.Closes);
        Assert.Empty(flow.Delays);
    }

    [Fact]
    public async Task RequestLevelGracefulFailureDoesNotEscalate()
    {
        var flow = new GpuAppCleanupFixture();
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded, flow.App));
        flow.CloseResponses.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Failed));

        Assert.Equal(GpuAppCleanupOutcomeKind.GracefulCloseRequestFailed, (await flow.RunAsync()).Kind);
        Assert.False(Assert.Single(flow.Closes).Force);
        Assert.Empty(flow.Delays);
        Assert.Equal(0, flow.ConfirmationCalls);
    }

    [Theory]
    [InlineData(SessionGpuOperationStatus.Failed)]
    [InlineData(SessionGpuOperationStatus.PartiallySucceeded)]
    public async Task IncompleteGracefulVerificationNeverOffersForce(SessionGpuOperationStatus status)
    {
        var flow = new GpuAppCleanupFixture();
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded, flow.App));
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(status, flow.App));

        Assert.Equal(GpuAppCleanupOutcomeKind.GracefulVerificationUnavailable, (await flow.RunAsync()).Kind);
        Assert.False(Assert.Single(flow.Closes).Force);
        Assert.Equal(0, flow.ConfirmationCalls);
    }

    [Fact]
    public async Task IndividualGracefulFailureStillVerifiesAndDistinguishesReusedPid()
    {
        var flow = new GpuAppCleanupFixture();
        var replacement = flow.App with { StartTimeUtcTicks = flow.App.StartTimeUtcTicks + 1 };
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded, flow.App));
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded, replacement));
        flow.CloseResponses.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Failed) with
        {
            Results = [new(flow.App.ProcessId, flow.App.StartTimeUtcTicks, false, "private error")],
        });

        var outcome = await flow.RunAsync();

        Assert.Equal(GpuAppCleanupOutcomeKind.GracefullyClosed, outcome.Kind);
        Assert.Equal(replacement, Assert.Single(outcome.OtherApps));
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(flow.Delays));
        Assert.Equal(0, flow.ConfirmationCalls);
    }

    [Fact]
    public async Task EveryForceCloseRequiresItsOwnConfirmation()
    {
        var flow = new GpuAppCleanupFixture();
        var kept = flow.App with { ProcessId = 202, Name = "kept.exe" };
        flow.Confirm = (app, _) => Task.FromResult(app.ProcessId == flow.App.ProcessId);
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded, flow.App, kept));
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded, flow.App, kept));
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded, kept));

        var outcome = await flow.RunAsync();

        Assert.Equal(GpuAppCleanupOutcomeKind.ForceCompleted, outcome.Kind);
        Assert.Equal(2, flow.ConfirmationCalls);
        Assert.Equal(2, flow.Closes.Count);
        Assert.True(flow.Closes[1].Force);
        Assert.Equal(flow.App, Assert.Single(flow.Closes[1].Apps));
        Assert.Equal(kept, Assert.Single(outcome.OtherApps));
        Assert.Empty(outcome.Survivors);
    }

    [Fact]
    public async Task DecliningAllForcePromptsDoesNotCloseOrRescan()
    {
        var flow = new GpuAppCleanupFixture { Confirm = (_, _) => Task.FromResult(false) };
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded, flow.App));
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded, flow.App));

        Assert.Equal(GpuAppCleanupOutcomeKind.ForceDeclined, (await flow.RunAsync()).Kind);
        Assert.Single(flow.Closes);
        Assert.Equal(2, flow.QueryCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForceFailuresAreReconciledAgainstConfirmedSurvivors(bool survives)
    {
        var flow = new GpuAppCleanupFixture();
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded, flow.App));
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded, flow.App));
        flow.FallbackScan = GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded,
            survives ? [flow.App with { Name = flow.App.Name.ToUpperInvariant() }] : []);
        flow.CloseResponses.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded));
        flow.CloseResponses.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Failed));

        var outcome = await flow.RunAsync();

        Assert.Equal(GpuAppCleanupOutcomeKind.ForceCompleted, outcome.Kind);
        Assert.Equal(survives ? [flow.App.ProcessId] : [], outcome.FailureProcessIds);
        Assert.Equal(survives ? 1 : 0, outcome.Survivors.Count);
        Assert.True(flow.Time.Elapsed <= TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(SessionGpuOperationStatus.Failed)]
    [InlineData(SessionGpuOperationStatus.PartiallySucceeded)]
    public async Task IncompleteForceVerificationRetriesWithinBudgetAndNeverReportsSuccess(SessionGpuOperationStatus status)
    {
        var flow = new GpuAppCleanupFixture();
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded, flow.App));
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded, flow.App));
        flow.FallbackScan = GpuAppCleanupFixture.Scan(status);

        Assert.Equal(GpuAppCleanupOutcomeKind.ForceVerificationUnavailable, (await flow.RunAsync()).Kind);
        Assert.True(flow.QueryCalls > 3);
        Assert.Equal(TimeSpan.FromSeconds(10), flow.Time.Elapsed);
    }

    [Fact]
    public async Task ActionGateRejectsDuplicatesAndSkipsBackgroundObservation()
    {
        var flow = new GpuAppCleanupFixture();
        var selection = new TaskCompletionSource<IReadOnlyList<SessionGpuApp>>(TaskCreationOptions.RunContinuationsAsynchronously);
        flow.Select = (_, _, _) => selection.Task;
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded, flow.App));

        Task<GpuAppCleanupOutcome> active = flow.RunAsync();
        Assert.Equal(GpuAppCleanupOutcomeKind.AlreadyRunning, (await flow.RunAsync()).Kind);
        await flow.Coordinator.ObserveAsync(CancellationToken.None);
        Assert.Equal(1, flow.QueryCalls);
        selection.SetResult([]);
        await active;
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded));
        await flow.Coordinator.ObserveAsync(CancellationToken.None);
        Assert.Equal(2, flow.QueryCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TeardownAfterConfirmationNeverSubmitsNextClose(bool duringForce)
    {
        var flow = new GpuAppCleanupFixture();
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded, flow.App));
        if (duringForce)
        {
            flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded, flow.App));
            flow.Confirm = (_, _) => { flow.Coordinator.Cancel(); return Task.FromResult(true); };
        }
        else
        {
            flow.Select = (apps, _, _) => { flow.Coordinator.Cancel(); return Task.FromResult(apps); };
        }

        Assert.Equal(GpuAppCleanupOutcomeKind.Cancelled, (await flow.RunAsync()).Kind);
        Assert.Equal(duringForce ? 1 : 0, flow.Closes.Count);
        Assert.DoesNotContain(flow.Closes, close => close.Force);
    }

    [Fact]
    public async Task ResultDialogKeepsActionAndObservationGatedUntilDismissed()
    {
        var flow = new GpuAppCleanupFixture();
        var dismissed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        flow.Present = _ => dismissed.Task;

        Task<GpuAppCleanupOutcome> active = flow.RunAsync();
        Assert.False(active.IsCompleted);
        Assert.Equal(GpuAppCleanupOutcomeKind.AlreadyRunning, (await flow.RunAsync()).Kind);
        await flow.Coordinator.ObserveAsync(CancellationToken.None);
        Assert.Equal(1, flow.QueryCalls);
        dismissed.SetResult();
        await active;
        await flow.Coordinator.ObserveAsync(CancellationToken.None);
        Assert.Equal(2, flow.QueryCalls);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0.1, false)]
    [InlineData(0.11, true)]
    public async Task ConfirmedInactiveGpuHidesCardDespiteClosableApps(
        double utilizationPercent,
        bool expectedVisible)
    {
        var flow = new GpuAppCleanupFixture();
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded, flow.App));
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(
            SessionGpuOperationStatus.Succeeded,
            flow.App with { UtilizationPercent = utilizationPercent }));

        await flow.Coordinator.ObserveAsync(CancellationToken.None);
        await flow.Coordinator.ObserveAsync(CancellationToken.None);

        Assert.Equal([true, expectedVisible], flow.Visibility);
        Assert.Empty(flow.Closes);
    }

    [Theory]
    [InlineData(SessionGpuOperationStatus.Failed)]
    [InlineData(SessionGpuOperationStatus.PartiallySucceeded)]
    public async Task ObservationRetainsVisibilityThroughTransientFailureThenHides(
        SessionGpuOperationStatus incompleteStatus)
    {
        var flow = new GpuAppCleanupFixture();
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded, flow.App));
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(incompleteStatus));
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded));
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Failed));

        for (int i = 0; i < 4; i++)
        {
            await flow.Coordinator.ObserveAsync(CancellationToken.None);
        }

        Assert.Equal([true, true, false, false], flow.Visibility);
        Assert.Empty(flow.Closes);
    }

    [Fact]
    public async Task FailedObservationUsesVisibilityHysteresisAndReleasesGate()
    {
        var flow = new GpuAppCleanupFixture();
        flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded, flow.App));
        await flow.Coordinator.ObserveAsync(CancellationToken.None);
        flow.QueryFailure = new IOException("private diagnostic");
        for (int i = 0; i < 3; i++)
        {
            await Assert.ThrowsAsync<IOException>(() => flow.Coordinator.ObserveAsync(CancellationToken.None));
        }
        Assert.Equal([true, true, true, false], flow.Visibility);
        flow.QueryFailure = null;
        Assert.Equal(GpuAppCleanupOutcomeKind.NothingToClose, (await flow.RunAsync()).Kind);
    }

    [Fact]
    public async Task CanceledObservationDoesNotCountAsFailedScan()
    {
        var flow = new GpuAppCleanupFixture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flow.Coordinator.ObserveAsync(cancelled.Token));

        Assert.Empty(flow.Visibility);
        Assert.Empty(flow.Closes);
    }

    [Fact]
    public async Task TransportFailureProducesTypedOutcomeAndAllowsNextAction()
    {
        var flow = new GpuAppCleanupFixture { QueryFailure = new TimeoutException("private diagnostic") };
        var presented = new List<GpuAppCleanupOutcomeKind>();
        flow.Present = outcome => { presented.Add(outcome.Kind); return Task.CompletedTask; };

        Assert.Equal(GpuAppCleanupOutcomeKind.SessionAgentFailure, (await flow.RunAsync()).Kind);
        flow.QueryFailure = null;
        Assert.Equal(GpuAppCleanupOutcomeKind.NothingToClose, (await flow.RunAsync()).Kind);
        Assert.Equal([GpuAppCleanupOutcomeKind.SessionAgentFailure, GpuAppCleanupOutcomeKind.NothingToClose], presented);
    }

    [Fact]
    public void AttendedCallbacksResumeOnCallerContextAfterAsynchronousQueries()
    {
        PumpingSynchronizationContext.Run(async context =>
        {
            var flow = new GpuAppCleanupFixture { YieldQueries = true };
            flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded, flow.App));
            flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded, flow.App));
            flow.Scans.Enqueue(GpuAppCleanupFixture.Scan(SessionGpuOperationStatus.Succeeded));
            flow.Select = (apps, _, _) =>
            {
                Assert.Same(context, SynchronizationContext.Current);
                return Task.FromResult(apps);
            };
            flow.Confirm = (_, _) =>
            {
                Assert.Same(context, SynchronizationContext.Current);
                return Task.FromResult(true);
            };
            flow.Present = _ =>
            {
                Assert.Same(context, SynchronizationContext.Current);
                return Task.CompletedTask;
            };

            Assert.Equal(GpuAppCleanupOutcomeKind.ForceCompleted, (await flow.RunAsync()).Kind);
        });
    }

    [Fact]
    public void BackgroundObservationDoesNotCaptureUiContext()
    {
        PumpingSynchronizationContext.Run(async context =>
        {
            var flow = new GpuAppCleanupFixture { YieldQueries = true };
            flow.VisibilityApplied = () => Assert.NotSame(context, SynchronizationContext.Current);

            await flow.Coordinator.ObserveAsync(CancellationToken.None);

            Assert.Single(flow.Visibility);
        });
    }
}

internal sealed class GpuAppCleanupFixture
{
    internal SessionGpuApp App { get; } = new(101, 1001, "app.exe", 5, true, null);
    internal Queue<SessionGpuResponse> Scans { get; } = new();
    internal Queue<SessionGpuResponse> CloseResponses { get; } = new();
    internal List<(IReadOnlyList<SessionGpuApp> Apps, bool Force)> Closes { get; } = [];
    internal List<TimeSpan> Delays { get; } = [];
    internal List<bool> Visibility { get; } = [];
    internal ManualGpuTimeProvider Time { get; } = new();
    internal SessionGpuResponse FallbackScan { get; set; } = Scan(SessionGpuOperationStatus.Succeeded);
    internal Func<IReadOnlyList<SessionGpuApp>, IReadOnlyList<SessionGpuApp>, CancellationToken, Task<IReadOnlyList<SessionGpuApp>>> Select { get; set; } = (apps, _, _) => Task.FromResult(apps);
    internal Func<SessionGpuApp, CancellationToken, Task<bool>> Confirm { get; set; } = (_, _) => Task.FromResult(true);
    internal Func<GpuAppCleanupOutcome, Task> Present { get; set; } = _ => Task.CompletedTask;
    internal int SelectionCalls { get; private set; }
    internal int ConfirmationCalls { get; private set; }
    internal int QueryCalls { get; private set; }
    internal bool YieldQueries { get; set; }
    internal Exception? QueryFailure { get; set; }
    internal Action? VisibilityApplied { get; set; }
    internal GpuAppCleanupCoordinator Coordinator { get; }

    internal GpuAppCleanupFixture()
    {
        Coordinator = new GpuAppCleanupCoordinator(
            async token =>
            {
                token.ThrowIfCancellationRequested();
                QueryCalls++;
                if (QueryFailure is not null)
                {
                    throw QueryFailure;
                }
                if (YieldQueries)
                {
                    await Task.Delay(1, token).ConfigureAwait(false);
                }
                return Scans.TryDequeue(out var response) ? response : FallbackScan;
            },
            (apps, force, token) =>
            {
                token.ThrowIfCancellationRequested();
                Closes.Add((apps, force));
                return Task.FromResult(CloseResponses.TryDequeue(out var response)
                    ? response : Scan(SessionGpuOperationStatus.Succeeded));
            },
            (visible, _) => { Visibility.Add(visible); VisibilityApplied?.Invoke(); return Task.CompletedTask; },
            (duration, token) =>
            {
                token.ThrowIfCancellationRequested();
                Delays.Add(duration);
                Time.Advance(duration);
                return Task.CompletedTask;
            },
            Time);
    }

    internal Task<GpuAppCleanupOutcome> RunAsync() => Coordinator.RunAsync(
        (eligible, blocked, token) => { SelectionCalls++; return Select(eligible, blocked, token); },
        (app, token) => { ConfirmationCalls++; return Confirm(app, token); },
        outcome => Present(outcome));

    internal static SessionGpuResponse Scan(SessionGpuOperationStatus status, params SessionGpuApp[] apps) =>
        new(SessionGpuIpc.ProtocolVersion, Guid.NewGuid(), status, null, apps, []);
}

internal sealed class ManualGpuTimeProvider : TimeProvider
{
    internal TimeSpan Elapsed { get; private set; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Elapsed.Ticks;
    internal void Advance(TimeSpan duration) => Elapsed += duration;
}
