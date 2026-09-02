using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class GpuAppForceCloseOutcomeTrackerTests
{
    private static readonly GpuAppProcessIdentity Approved = new(101, 1001, "approved.exe");
    private static readonly GpuAppProcessIdentity KeptRunning = new(202, 2002, "kept.exe");

    [Fact]
    public void OnlyApprovedTargetsParticipateInFinalOutcome()
    {
        var tracker = new GpuAppForceCloseOutcomeTracker();

        tracker.Approve(Approved);

        Assert.True(tracker.HasApprovedTargets);
        Assert.True(tracker.WasApproved(Approved));
        Assert.False(tracker.WasApproved(KeptRunning));
    }

    [Fact]
    public void ApprovedTargetNameComparisonIgnoresCaseButPreservesProcessIdentity()
    {
        var tracker = new GpuAppForceCloseOutcomeTracker();
        tracker.Approve(Approved);

        Assert.True(tracker.WasApproved(Approved with { Name = "APPROVED.EXE" }));
        Assert.False(tracker.WasApproved(Approved with { ProcessId = Approved.ProcessId + 1 }));
        Assert.False(tracker.WasApproved(Approved with
        {
            StartTimeUtcTicks = Approved.StartTimeUtcTicks + 1,
        }));
    }

    [Fact]
    public void FailureIsReportedWhileApprovedTargetStillSurvives()
    {
        var tracker = new GpuAppForceCloseOutcomeTracker();
        tracker.Approve(Approved);
        tracker.RecordFailure(Approved, "- PID 101: identity changed");

        string failure = Assert.Single(tracker.ReconcileFailures([Approved]));

        Assert.Equal("- PID 101: identity changed", failure);
    }

    [Fact]
    public void FailureIsSuppressedWhenFinalScanShowsTargetGone()
    {
        var tracker = new GpuAppForceCloseOutcomeTracker();
        tracker.Approve(Approved);
        tracker.RecordFailure(Approved, "- PID 101: identity changed");

        Assert.Empty(tracker.ReconcileFailures([]));
    }

    [Fact]
    public void FailureReconciliationIgnoresSurvivorNameCase()
    {
        var tracker = new GpuAppForceCloseOutcomeTracker();
        tracker.RecordFailure(Approved, "- PID 101: identity changed");

        string failure = Assert.Single(tracker.ReconcileFailures(
            [Approved with { Name = "APPROVED.EXE" }]));

        Assert.Equal("- PID 101: identity changed", failure);
    }

    [Fact]
    public void TargetFiltersIgnoreNameCaseButPreserveProcessIdentity()
    {
        GpuAppProcessIdentity caseVariant = Approved with { Name = "APPROVED.EXE" };
        GpuAppProcessIdentity reusedPid = Approved with
        {
            StartTimeUtcTicks = Approved.StartTimeUtcTicks + 1,
        };
        GpuAppProcessIdentity[] current = [caseVariant, reusedPid, KeptRunning];

        GpuAppProcessIdentity match = Assert.Single(
            GpuAppProcessIdentityFilter.MatchTargets(current, [Approved], static app => app));
        GpuAppProcessIdentity[] others = GpuAppProcessIdentityFilter.ExceptTargets(
            current,
            [Approved],
            static app => app);

        Assert.Equal(caseVariant, match);
        Assert.Equal([reusedPid, KeptRunning], others);
    }

    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(true, 1, false)]
    [InlineData(false, 0, false)]
    public void GracefulCloseAbortsOnlyForRequestLevelFailure(
        bool requestFailed,
        int resultCount,
        bool expected) => Assert.Equal(
            expected,
            GpuAppCleanupPolicy.ShouldAbortGracefulClose(requestFailed, resultCount));

    [Fact]
    public async Task ForceVerificationStopsWhenApprovedSurvivorDisappears()
    {
        var responses = new Queue<bool>([true, false]);
        var delays = new List<TimeSpan>();
        var time = new ManualTimeProvider();

        bool result = await GpuAppForceCloseVerification.VerifyAsync(
            _ => Task.FromResult(responses.Dequeue()),
            shouldRetry: response => response,
            (duration, _) =>
            {
                delays.Add(duration);
                time.Advance(duration);
                return Task.CompletedTask;
            },
            time,
            settleBudget: TimeSpan.FromSeconds(5),
            retryInterval: TimeSpan.FromMilliseconds(750),
            CancellationToken.None);

        Assert.False(result);
        Assert.Empty(responses);
        Assert.Equal([TimeSpan.FromMilliseconds(750)], delays);
    }

    [Fact]
    public async Task ForceVerificationReturnsLastSurvivorAtBudget()
    {
        int queryCount = 0;
        var delays = new List<TimeSpan>();
        var time = new ManualTimeProvider();

        bool result = await GpuAppForceCloseVerification.VerifyAsync(
            _ =>
            {
                queryCount++;
                return Task.FromResult(true);
            },
            shouldRetry: response => response,
            (duration, _) =>
            {
                delays.Add(duration);
                time.Advance(duration);
                return Task.CompletedTask;
            },
            time,
            settleBudget: TimeSpan.FromSeconds(5),
            retryInterval: TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(result);
        Assert.Equal(4, queryCount);
        Assert.Equal(
            [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1)],
            delays);
    }

    [Theory]
    [InlineData(VerificationStatus.Failed)]
    [InlineData(VerificationStatus.PartiallySucceeded)]
    public async Task ForceVerificationRetriesIncompleteScanUntilCompleteWithoutSurvivor(
        VerificationStatus initialStatus)
    {
        var responses = new Queue<VerificationResponse>(
        [
            new(initialStatus, HasApprovedSurvivor: false),
            new(VerificationStatus.Succeeded, HasApprovedSurvivor: false),
        ]);
        var delays = new List<TimeSpan>();
        var time = new ManualTimeProvider();

        VerificationResponse result = await GpuAppForceCloseVerification.VerifyAsync(
            _ => Task.FromResult(responses.Dequeue()),
            shouldRetry: response => response.Status != VerificationStatus.Succeeded
                || response.HasApprovedSurvivor,
            (duration, _) =>
            {
                delays.Add(duration);
                time.Advance(duration);
                return Task.CompletedTask;
            },
            time,
            settleBudget: TimeSpan.FromSeconds(5),
            retryInterval: TimeSpan.FromMilliseconds(750),
            CancellationToken.None);

        Assert.Equal(VerificationStatus.Succeeded, result.Status);
        Assert.False(result.HasApprovedSurvivor);
        Assert.Empty(responses);
        Assert.Equal([TimeSpan.FromMilliseconds(750)], delays);
    }

    [Theory]
    [InlineData(VerificationStatus.Failed)]
    [InlineData(VerificationStatus.PartiallySucceeded)]
    public async Task ForceVerificationCountsQueryTimeAndBoundsIncompleteScans(
        VerificationStatus status)
    {
        int queryCount = 0;
        var delays = new List<TimeSpan>();
        var time = new ManualTimeProvider();

        VerificationResponse result = await GpuAppForceCloseVerification.VerifyAsync(
            _ =>
            {
                queryCount++;
                time.Advance(TimeSpan.FromMilliseconds(800));
                return Task.FromResult(new VerificationResponse(
                    status,
                    HasApprovedSurvivor: false));
            },
            shouldRetry: response => response.Status != VerificationStatus.Succeeded
                || response.HasApprovedSurvivor,
            (duration, _) =>
            {
                delays.Add(duration);
                time.Advance(duration);
                return Task.CompletedTask;
            },
            time,
            settleBudget: TimeSpan.FromSeconds(2),
            retryInterval: TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.Equal(status, result.Status);
        Assert.Equal(2, queryCount);
        Assert.Equal([TimeSpan.FromSeconds(1)], delays);
    }

    [Fact]
    public async Task ForceVerificationDoesNotDelayAfterQueryExhaustsBudget()
    {
        int queryCount = 0;
        var delays = new List<TimeSpan>();
        var time = new ManualTimeProvider();

        bool result = await GpuAppForceCloseVerification.VerifyAsync(
            _ =>
            {
                queryCount++;
                time.Advance(TimeSpan.FromSeconds(3));
                return Task.FromResult(true);
            },
            shouldRetry: response => response,
            (duration, _) =>
            {
                delays.Add(duration);
                time.Advance(duration);
                return Task.CompletedTask;
            },
            time,
            settleBudget: TimeSpan.FromSeconds(2),
            retryInterval: TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert.True(result);
        Assert.Equal(1, queryCount);
        Assert.Empty(delays);
    }

    [Fact]
    public async Task ForceVerificationPropagatesCancellationFromDelay()
    {
        using var cancellation = new CancellationTokenSource();

        Task<bool> verification = GpuAppForceCloseVerification.VerifyAsync(
            _ => Task.FromResult(true),
            shouldRetry: response => response,
            async (_, token) =>
            {
                cancellation.Cancel();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            TimeProvider.System,
            settleBudget: TimeSpan.FromSeconds(5),
            retryInterval: TimeSpan.FromMilliseconds(750),
            cancellation.Token);

        await Assert.ThrowsAsync<TaskCanceledException>(() => verification);
    }

    public enum VerificationStatus
    {
        Succeeded,
        PartiallySucceeded,
        Failed,
    }

    private readonly record struct VerificationResponse(
        VerificationStatus Status,
        bool HasApprovedSurvivor);

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => timestamp;

        internal void Advance(TimeSpan amount) => timestamp += amount.Ticks;
    }
}
