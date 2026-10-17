using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalBladeLightingTests
{
    [Fact]
    public async Task Blade18Animation_StreamsBeyondAdmissionBudgetWithoutQueueingMissedFrames()
    {
        var clock = new AnimationClock();
        var session = new Session();
        await using var port = AnimatedPort(session, clock);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(50), CancellationToken.None);
        await clock.WaitForTimerCountAsync(1);
        Assert.Equal(1, session.AnimationBatches);
        for (int i = 1; i <= 60; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            await clock.WaitForTimerCountAsync(i + 1);
        }
        Assert.Equal(61, session.AnimationBatches);
        Assert.Equal(367, session.AnimationTransactions.Count);
        Assert.All(session.AnimationTransactions, id => Assert.InRange(id, (byte)1, (byte)31));
        Assert.True(session.AnimationTransactions.Zip(session.AnimationTransactions.Skip(1))
            .All(pair => pair.Second == (pair.First == 31 ? 1 : pair.First + 1)));
        clock.Advance(TimeSpan.FromSeconds(10));
        await clock.WaitForTimerCountAsync(62);
        Assert.Equal(62, session.AnimationBatches);
        Assert.Single(session.Commands, c => c.ClassId == 3 && c.CommandId == 10);
        Assert.Null(await port.ReadStateAsync(CancellationToken.None));
        await port.RelinquishRendererAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(62, session.AnimationBatches);
        Assert.Equal(LightingRendererOwnershipState.None, port.OwnershipState);
    }

    [Fact]
    public async Task Blade18Animation_PartialSubmissionPausesAndNeverReplays()
    {
        var clock = new AnimationClock();
        var session = new Session { FailAnimationBatch = 2 };
        var policy = Enabled();
        await using var port = AnimatedPort(session, clock, policy);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(50), CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await WaitUntilAsync(() => port.OwnershipState == LightingRendererOwnershipState.Faulted);
        Assert.False(policy.WritesAllowed);
        Assert.Contains(policy.Snapshot().Observations, item =>
            item.Operation == "KeyboardAnimationFrame" && item.Outcome == "Indeterminate");
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(2, session.AnimationBatches);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetLightingAsync(
            LightingSettingsFactory.SpectrumCycling(50), CancellationToken.None));
        Assert.Equal(2, session.AnimationBatches);
    }

    [Fact]
    public async Task Blade18Animation_FreshAdmissionLossStopsBeforeNextFrame()
    {
        var clock = new AnimationClock();
        var session = new Session();
        bool present = true;
        await using var port = new ExperimentalBladePort(session, new(), Model, Enabled(),
            () => present, () => PowerSource.PluggedIn, lightingTimeProvider: clock);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(50), CancellationToken.None);
        present = false;
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await WaitUntilAsync(() => port.OwnershipState == LightingRendererOwnershipState.Faulted);
        Assert.Equal(1, session.AnimationBatches);
        present = true;
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(1, session.AnimationBatches);
    }

    [Fact]
    public async Task Blade18Animation_RecoveryTakesNewReadableBaseline()
    {
        var clock = new AnimationClock();
        var session = new Session { FailAnimationBatch = 2 };
        var policy = Enabled();
        await using var port = AnimatedPort(session, clock, policy);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(50), CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await WaitUntilAsync(() => port.OwnershipState == LightingRendererOwnershipState.Faulted);
        session.Effect = 4;
        session.Raw = 77;
        policy.CompleteRecovery();
        await port.SetLightingAsync(LightingSettingsFactory.Fire(50), CancellationToken.None);
        await port.RelinquishRendererAsync(CancellationToken.None);
        Assert.Equal(4, session.Effect);
        Assert.Equal(77, session.Raw);
    }

    [Fact]
    public async Task Blade18Animation_RejectedReplacementFaultsStoppedOwnerWithoutResumingTraffic()
    {
        var clock = new AnimationClock();
        var session = new Session();
        await using var port = AnimatedPort(session, clock);
        await port.SetLightingAsync(LightingSettingsFactory.Fire(50), CancellationToken.None);
        await clock.WaitForTimerCountAsync(1);
        int timerCount = clock.TimerCount;
        session.RejectNextAnimationBatch = true;

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetLightingAsync(
                LightingSettingsFactory.Wheel(LightingRotationDirection.Clockwise, 50),
                CancellationToken.None));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Equal(LightingRendererOwnershipState.Faulted, port.OwnershipState);
        Assert.Equal(timerCount, clock.TimerCount);
        int stoppedBatchCount = session.AnimationBatches;
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(stoppedBatchCount, session.AnimationBatches);
    }

    private static ExperimentalBladePort AnimatedPort(Session session, AnimationClock clock,
        ExperimentalFeaturesPolicy? policy = null) => new(session, new(), Model, policy ?? Enabled(),
            () => true, () => PowerSource.PluggedIn, lightingTimeProvider: clock);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(1, timeout.Token);
        }
    }

    private sealed partial class Session : IRazerSetFeatureCommandSession
    {
        internal int AnimationBatches;
        internal int FailAnimationBatch;
        internal bool RejectNextAnimationBatch;
        internal List<byte> AnimationTransactions { get; } = [];
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> SubmitAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId, TimeSpan timeout, CancellationToken token) =>
            SubmitBatchAsync([command], assignTransactionId, timeout, token);

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> SubmitBatchAsync(
            IReadOnlyList<RazerCommandSpec> commands, Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            AnimationBatches++;
            if (RejectNextAnimationBatch)
            {
                RejectNextAnimationBatch = false;
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(
                    default,
                    HidSessionFailureKind.ConflictBlocked)
                {
                    Diagnostic = new(
                        HidSessionFailurePhase.Guard,
                        HidSessionFailureKind.ConflictBlocked,
                        null,
                        null),
                });
            }
            foreach (RazerCommandSpec command in commands)
            {
                Assert.True(command.SubmissionAllowed?.Invoke());
                TransactionIdAssignment id = assignTransactionId();
                Assert.Null(id.Failure);
                AnimationTransactions.Add(id.TransactionId);
                Commands.Add(command);
                if (AnimationBatches == FailAnimationBatch)
                {
                    return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(
                        default,
                        HidSessionFailureKind.NativeIoFailure)
                    {
                        Diagnostic = new(
                            HidSessionFailurePhase.SetCompletion,
                            HidSessionFailureKind.NativeIoFailure,
                            null,
                            null),
                    });
                }
            }
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, null));
        }
    }

    private sealed class AnimationClock : TimeProvider
    {
        private long ticks;
        private readonly List<AnimationTimer> timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
        internal int TimerCount
        {
            get
            {
                lock (timers)
                {
                    return timers.Count;
                }
            }
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new AnimationTimer(callback, state, GetTimestamp() + dueTime.Ticks);
            lock (timers)
            {
                timers.Add(timer);
            }
            return timer;
        }
        internal void Advance(TimeSpan amount)
        {
            Interlocked.Add(ref ticks, amount.Ticks);
            AnimationTimer[] due;
            lock (timers)
            {
                due = timers.Where(t => t.Due <= GetTimestamp()).ToArray();
            }
            foreach (AnimationTimer timer in due)
            {
                timer.Fire();
            }
        }
        internal Task WaitForTimerCountAsync(int count) => WaitUntilAsync(() =>
        {
            lock (timers)
            {
                return timers.Count >= count;
            }
        });
        private sealed class AnimationTimer(TimerCallback callback, object? state, long due) : ITimer
        {
            private int completed;
            internal long Due { get; } = due;
            internal void Fire()
            {
                if (Interlocked.Exchange(ref completed, 1) == 0)
                {
                    callback(state);
                }
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Interlocked.Exchange(ref completed, 1);
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
