using OpenBlade.Capture;
using Fixture = OpenBlade.Capture.Tests.So6120DirectDutySessionTests.Fixture;

namespace OpenBlade.Capture.Tests;

public sealed class So6120TrialGuardianTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static So6120GuardianTrial Prepare(Fixture fixture, Action<So6120SilentTrialProgress> progress,
        Action<So6120JournalWriteStage>? storageFault = null, ISo6120MailboxBytePort? port = null)
    {
        var session = port is null ? fixture.Create(storageFault) : So6120DirectDutySession.CreateInjected(
            fixture.Root, fixture.Binding, port, fixture.Source, fixture.Clock, storageFault: storageFault);
        try
        {
            var plan = session.PrepareBalancedStop(new(() => true, () => true, progress));
            return new(session, plan, new(plan.PlanId, plan.PlanSha256, Guid.NewGuid(), new('C', 64)));
        }
        catch { session.Dispose(); throw; }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task CallerLossDuringEachMutationWaitsForConfirmationThenRestoresTheKnownPrefix(int writeNumber)
    {
        using var fixture = So6120SilentTrialTests.StopFixture();
        using var release = new ManualResetEventSlim();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var update = fixture.Port.AfterWrite;
        fixture.Port.AfterWrite = (register, value) =>
        {
            update!(register, value);
            if (fixture.Port.Writes.Count != writeNumber) { return; }
            pending.SetResult();
            if (!release.Wait(Wait)) { throw new TimeoutException("Fake transfer was not released."); }
        };
        var guardian = So6120TrialGuardian.Start(progress => Prepare(fixture, progress), fixture.Clock);
        try
        {
            await pending.Task.WaitAsync(Wait);
            guardian.CallerDisconnected();
            guardian.RequestAuto();
            Assert.False(guardian.Heartbeat());
            Assert.Equal(So6120GuardianStopReason.CallerDisconnected, guardian.Snapshot.StopReason);
            Assert.True(guardian.Snapshot.OwnerRunning);
            Assert.False(guardian.Completion.IsCompleted);
            Assert.False(fixture.Port.Disposed);
            Assert.Equal(writeNumber, fixture.Port.Writes.Count);
        }
        finally { release.Set(); }

        var receipt = await guardian.Completion.WaitAsync(Wait);
        Assert.True(receipt.Resolved, receipt.Failure);
        Assert.True(receipt.Trial!.CancelRequested);
        Assert.False(receipt.Trial.Success);
        Assert.True(fixture.Port.Disposed);
        Assert.Equal(0, fixture.Port.Mapped[0x1A]);
        Assert.Equal(0x11, fixture.Port.Mapped[0xF0]);
        Assert.Equal(10, fixture.Port.Mapped[0xB8]);
        Assert.Equal(20, fixture.Port.Mapped[0xB9]);
        Assert.True(So6120DirectDutyJournal.Inspect(fixture.Root).Complete);
        Assert.False(So6120DirectDutyJournal.Inspect(fixture.Root).CanResume);
        Assert.Equal(writeNumber >= 4 ? 1 : 0, fixture.Port.Writes.Count(write => write == (0x1A, 0)));
        Assert.Equal("Complete", guardian.Snapshot.Phase);
    }

    [Fact]
    public async Task PendingAmbiguousTransferFencesEvenAfterCallerLoss()
    {
        using var fixture = So6120SilentTrialTests.StopFixture();
        using var release = new ManualResetEventSlim();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Port.AmbiguousFirstWrite = true;
        fixture.Port.AfterWrite = (_, _) =>
        {
            pending.SetResult();
            if (!release.Wait(Wait)) { throw new TimeoutException(); }
        };
        var guardian = So6120TrialGuardian.Start(progress => Prepare(fixture, progress), fixture.Clock);
        try
        {
            await pending.Task.WaitAsync(Wait);
            guardian.CallerDisconnected();
            Assert.False(fixture.Port.Disposed);
            Assert.False(guardian.Completion.IsCompleted);
        }
        finally { release.Set(); }
        var receipt = await guardian.Completion.WaitAsync(Wait);
        Assert.False(receipt.Resolved);
        Assert.False(receipt.AutoConfirmed);
        Assert.False(receipt.JournalCompleted);
        Assert.True(receipt.ReleaseCompleted);
        Assert.True(receipt.Trial!.CommandsFenced);
        Assert.Single(fixture.Port.Writes);
        Assert.Equal(0xA1, fixture.Port.Publications[^1].Command);
        Assert.Equal("Unresolved", guardian.Snapshot.Phase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task StorageFailureCannotBecomeCleanupOrSuccessfulReceipt(int stageToFail)
    {
        using var fixture = So6120SilentTrialTests.StopFixture();
        bool armed = false;
        fixture.Port.AfterWrite = (_, _) => armed = true;
        var guardian = So6120TrialGuardian.Start(progress => Prepare(fixture, progress, stage =>
        {
            if (armed && (int)stage == stageToFail) { throw new IOException("Synthetic journal failure."); }
        }), fixture.Clock);
        var receipt = await guardian.Completion.WaitAsync(Wait);
        Assert.False(receipt.Resolved);
        Assert.False(receipt.JournalCompleted);
        Assert.True(receipt.Trial!.CommandsFenced);
        Assert.False(receipt.Trial.DurableFenceConfirmed);
        Assert.Single(fixture.Port.Writes);
        Assert.Equal(0xA1, fixture.Port.Publications[^1].Command);
    }

    [Fact]
    public async Task LostAdmissionCannotBeBypassedToRestoreAuto()
    {
        using var fixture = So6120SilentTrialTests.StopFixture();
        fixture.Port.AfterWrite = (_, _) => fixture.Source.Deny = true;
        var guardian = So6120TrialGuardian.Start(progress => Prepare(fixture, progress), fixture.Clock);
        var receipt = await guardian.Completion.WaitAsync(Wait);
        Assert.False(receipt.Resolved);
        Assert.True(receipt.Trial!.CommandsFenced);
        Assert.Single(fixture.Port.Writes);
        Assert.Equal(0xA1, fixture.Port.Publications[^1].Command);
    }

    [Fact]
    public async Task CloseFailureDoesNotEraseConfirmedAutoOrInventRelease()
    {
        using var fixture = So6120SilentTrialTests.StopFixture();
        var guardian = So6120TrialGuardian.Start(progress => Prepare(fixture, progress,
            port: new CloseFailingPort(fixture.Port)), fixture.Clock);
        var receipt = await guardian.Completion.WaitAsync(Wait);
        Assert.True(receipt.AutoConfirmed);
        Assert.True(receipt.CleanupConfirmed);
        Assert.True(receipt.JournalCompleted);
        Assert.True(So6120DirectDutyJournal.Inspect(fixture.Root).Complete);
        Assert.False(receipt.ReleaseCompleted);
        Assert.False(receipt.Resolved);
        Assert.Contains("Synthetic close failure", receipt.Failure);
        Assert.Equal("Unresolved", guardian.Snapshot.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeartbeatsCannotResurrectAnExpiredLeaseOrExtendTheOperation(bool operationExpires)
    {
        using var fixture = So6120SilentTrialTests.StopFixture();
        using var release = new ManualResetEventSlim();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Port.AfterWrite = (_, _) =>
        {
            if (fixture.Port.Writes.Count != 1) { return; }
            pending.SetResult();
            if (!release.Wait(Wait)) { throw new TimeoutException(); }
        };
        var options = new So6120GuardianOptions(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(800));
        var guardian = So6120TrialGuardian.Start(progress => Prepare(fixture, progress), fixture.Clock, options);
        try
        {
            await pending.Task.WaitAsync(Wait);
            if (operationExpires)
            {
                for (int i = 0; i < 3; i++)
                {
                    fixture.Clock.Advance(TimeSpan.FromMilliseconds(200));
                    Assert.True(guardian.Heartbeat());
                    guardian.CheckDeadline(); // A queued old timer must use the renewed deadline.
                    Assert.Equal(So6120GuardianStopReason.None, guardian.Snapshot.StopReason);
                }
                fixture.Clock.Advance(TimeSpan.FromMilliseconds(200));
            }
            else { fixture.Clock.Advance(TimeSpan.FromMilliseconds(300)); }
            Assert.False(guardian.Heartbeat()); // Must check expiry even if the watchdog has not run.
            Assert.Equal(operationExpires ? So6120GuardianStopReason.OperationLimit : So6120GuardianStopReason.LeaseExpired,
                guardian.Snapshot.StopReason);
            Assert.False(fixture.Port.Disposed);
        }
        finally { release.Set(); }
        Assert.True((await guardian.Completion.WaitAsync(Wait)).Resolved);
    }

    [Fact]
    public async Task OperationCutoffDuringAutoDoesNotCancelSuccessfulTrial()
    {
        using var fixture = So6120SilentTrialTests.StopFixture();
        using var release = new ManualResetEventSlim();
        var pendingAuto = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var update = fixture.Port.AfterWrite;
        fixture.Port.AfterWrite = (register, value) =>
        {
            update!(register, value);
            if (register != 0x1A || value != 0) { return; }
            pendingAuto.SetResult();
            if (!release.Wait(Wait)) { throw new TimeoutException("Fake Auto transfer was not released."); }
        };
        var operationLimit = TimeSpan.FromSeconds(1);
        var guardian = So6120TrialGuardian.Start(progress => Prepare(fixture, progress), fixture.Clock,
            new(TimeSpan.FromSeconds(5), operationLimit));
        try
        {
            await pendingAuto.Task.WaitAsync(Wait);
            Assert.Equal("Restoring", guardian.Snapshot.Phase);
            fixture.Clock.Advance(operationLimit);
            guardian.CheckDeadline();
            Assert.Equal(So6120GuardianStopReason.None, guardian.Snapshot.StopReason);
            Assert.False(fixture.Port.Disposed);
        }
        finally { release.Set(); }

        var receipt = await guardian.Completion.WaitAsync(Wait);
        Assert.True(receipt.Resolved, receipt.Failure);
        Assert.Equal(So6120GuardianStopReason.None, receipt.StopReason);
        Assert.True(receipt.Trial!.Success);
        Assert.False(receipt.Trial.CancelRequested);
        Assert.Equal(1, fixture.Port.Writes.Count(write => write == (0x1A, 0)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachedSignalsOwnCallerLeaseWithoutReplacingOperationCutoff(bool operationExpires)
    {
        using var fixture = So6120SilentTrialTests.StopFixture();
        using var release = new ManualResetEventSlim();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Port.AfterWrite = (_, _) =>
        {
            if (fixture.Port.Writes.Count != 1) { return; }
            pending.SetResult();
            if (!release.Wait(Wait)) { throw new TimeoutException("Fake entry transfer was not released."); }
        };
        await using var signals = new So6120GuardianSignals(fixture.Clock, TimeSpan.FromMilliseconds(900));
        Assert.True(signals.Receive(1));
        var options = new So6120GuardianOptions(TimeSpan.FromMilliseconds(300),
            TimeSpan.FromMilliseconds(operationExpires ? 800 : 5000));
        var guardian = So6120TrialGuardian.Start(progress => Prepare(fixture, progress), fixture.Clock, options, signals);
        var expected = operationExpires ? So6120GuardianStopReason.OperationLimit : So6120GuardianStopReason.LeaseExpired;
        try
        {
            await pending.Task.WaitAsync(Wait);
            fixture.Clock.Advance(options.CallerLease);
            guardian.CheckDeadline();
            Assert.Equal(So6120GuardianStopReason.None, guardian.Snapshot.StopReason);
            Assert.False(guardian.Heartbeat());
            Assert.True(signals.Receive(1));

            fixture.Clock.Advance(TimeSpan.FromMilliseconds(operationExpires ? 500 : 900));
            guardian.CheckDeadline();
            signals.CheckDeadline();
            Assert.Equal(operationExpires ? So6120GuardianStopReason.None : expected, signals.StopReason);
            Assert.Equal(expected, guardian.Snapshot.StopReason);
        }
        finally { release.Set(); }

        var receipt = await guardian.Completion.WaitAsync(Wait);
        Assert.True(receipt.Resolved, receipt.Failure);
        Assert.Equal(expected, receipt.StopReason);
        Assert.True(receipt.Trial!.CancelRequested);
    }

    [Fact]
    public async Task LiveCallerWithoutHeartbeatsExpiresWhileItsTransferRemainsPending()
    {
        using var fixture = So6120SilentTrialTests.StopFixture();
        using var release = new ManualResetEventSlim();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Port.AfterWrite = (_, _) =>
        {
            if (fixture.Port.Writes.Count != 1) { return; }
            pending.SetResult();
            if (!release.Wait(Wait)) { throw new TimeoutException(); }
        };
        var guardian = So6120TrialGuardian.Start(progress => Prepare(fixture, progress),
            options: new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)));
        try
        {
            await pending.Task.WaitAsync(Wait);
            using var timeout = new CancellationTokenSource(Wait);
            So6120GuardianStatus expired;
            do { expired = await guardian.Statuses.ReadAsync(timeout.Token); }
            while (expired.StopReason == So6120GuardianStopReason.None);
            Assert.Equal(So6120GuardianStopReason.LeaseExpired, expired.StopReason);
            guardian.RequestAuto();
            Assert.Equal(So6120GuardianStopReason.LeaseExpired, guardian.Snapshot.StopReason);
            Assert.False(guardian.Completion.IsCompleted);
            Assert.False(fixture.Port.Disposed);
        }
        finally { release.Set(); }
        Assert.True((await guardian.Completion.WaitAsync(Wait)).Resolved);
    }

    [Fact]
    public async Task ExplicitAutoStopsEntryWithoutNeedingCallerDisconnection()
    {
        using var fixture = So6120SilentTrialTests.StopFixture();
        using var release = new ManualResetEventSlim();
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var guardian = So6120TrialGuardian.Start(progress =>
        {
            var owned = Prepare(fixture, progress);
            prepared.SetResult();
            if (!release.Wait(Wait)) { owned.Session.Dispose(); throw new TimeoutException(); }
            return owned;
        }, fixture.Clock);
        try
        {
            await prepared.Task.WaitAsync(Wait);
            guardian.RequestAuto();
            guardian.CallerDisconnected();
            Assert.Equal(So6120GuardianStopReason.ExplicitAuto, guardian.Snapshot.StopReason);
            Assert.False(guardian.Heartbeat());
        }
        finally { release.Set(); }
        var receipt = await guardian.Completion.WaitAsync(Wait);
        Assert.Empty(fixture.Port.Writes);
        Assert.True(receipt.Trial!.NoSettingWritePublished);
        Assert.True(receipt.ReleaseCompleted);
        // No cleanup sequence or completion frame was needed; do not invent
        // an Auto/recovery receipt from a baseline or dispose alone.
        Assert.False(receipt.AutoConfirmed);
        Assert.False(receipt.Resolved);
        Assert.False(So6120DirectDutyJournal.Inspect(fixture.Root).Complete);
    }

    [Fact]
    public async Task NoStatusConsumerDoesNotBlockAutoAndOnlyLatestStatusIsRetained()
    {
        using var fixture = So6120SilentTrialTests.StopFixture();
        var guardian = So6120TrialGuardian.Start(progress => Prepare(fixture, progress), fixture.Clock);
        Assert.True((await guardian.Completion.WaitAsync(Wait)).Resolved);
        Assert.True(guardian.Statuses.TryRead(out var status));
        Assert.Equal("Complete", status!.Phase);
        Assert.False(status.OwnerRunning);
        Assert.False(guardian.Statuses.TryRead(out _));
        Assert.False(guardian.Heartbeat());
        var terminal = guardian.Snapshot;
        guardian.RequestAuto();
        guardian.CallerDisconnected();
        Assert.Equal(terminal, guardian.Snapshot);
    }

    [Fact]
    public async Task PreparationFailureDoesNotProduceAReleaseOrAutoReceipt()
    {
        var guardian = So6120TrialGuardian.Start(_ => throw new IOException("Synthetic preparation failure."));
        var receipt = await guardian.Completion.WaitAsync(Wait);
        Assert.Null(receipt.Trial);
        Assert.False(receipt.Resolved);
        Assert.False(receipt.ReleaseCompleted);
        Assert.Contains("Synthetic preparation failure", receipt.Failure);
    }

    private sealed class CloseFailingPort(ISo6120MailboxBytePort port) : ISo6120MailboxBytePort, IDisposable
    {
        public So6120PortResult SelectIndex(byte index) => port.SelectIndex(index);
        public So6120PortResult ReadData() => port.ReadData();
        public So6120PortResult WriteData(byte value) => port.WriteData(value);
        public void Dispose() => throw new IOException("Synthetic close failure.");
    }
}
