using OpenBlade.Capture;
using Fixture = OpenBlade.Capture.Tests.So6120DirectDutySessionTests.Fixture;

namespace OpenBlade.Capture.Tests;

public sealed class So6120GuardianIntegrationTests
{
    [Fact]
    public async Task TrackedGuardianDrainWaitsForPendingTransferBeforeFixtureDeletion()
    {
        using var fixture = So6120SilentTrialTests.StopFixture();
        using var release = new ManualResetEventSlim();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Port.AfterWrite = (_, _) =>
        {
            if (fixture.Port.Writes.Count != 1) { return; }
            pending.SetResult();
            if (!release.Wait(Fixture.GuardianWait)) { throw new TimeoutException(); }
        };
        var guardian = So6120TrialGuardian.Start(progress =>
        {
            var session = fixture.Create();
            var plan = session.PrepareBalancedStop(new(() => true, () => true, progress));
            return new(session, plan, new(plan.PlanId, plan.PlanSha256, Guid.NewGuid(), new('C', 64)));
        }, fixture.Clock);
        await using var ownership = fixture.Track(guardian);
        Task drain;
        try
        {
            await pending.Task.WaitAsync(Fixture.GuardianWait);
            drain = ownership.DisposeAsync().AsTask();
            Assert.False(drain.IsCompleted);
            Assert.False(fixture.Port.Disposed);
            Assert.True(Directory.Exists(fixture.Root));
        }
        finally { release.Set(); }
        await drain.WaitAsync(Fixture.GuardianWait);
        Assert.True(fixture.Port.Disposed);
        fixture.Dispose();
        Assert.False(Directory.Exists(fixture.Root));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(255)]
    public async Task StopBeforeWorkerAttachmentCannotBeLostOrRenewed(int command)
    {
        using var fixture = So6120SilentTrialTests.StopFixture();
        await using var signals = new So6120GuardianSignals(fixture.Clock);
        Assert.True(signals.Receive(1));
        var session = fixture.Create();
        var plan = session.PrepareBalancedStop(new(() => true, () => true));
        int calls = fixture.Port.Calls;
        Assert.False(signals.Receive((byte)command));
        Assert.True(signals.StopToken.IsCancellationRequested);
        Assert.False(signals.Receive(1));
        var guardian = So6120TrialGuardian.Start(_ => new(session, plan,
            new(plan.PlanId, plan.PlanSha256, Guid.NewGuid(), new('C', 64))), fixture.Clock, signals: signals);
        await using var ownership = fixture.Track(guardian);
        var receipt = await guardian.Completion.WaitAsync(Fixture.GuardianWait);
        Assert.Equal(command == 2 ? So6120GuardianStopReason.ExplicitAuto : So6120GuardianStopReason.CallerDisconnected,
            receipt.StopReason);
        Assert.True(receipt.Trial!.NoSettingWritePublished);
        Assert.Empty(fixture.Port.Writes);
        Assert.Equal(calls, fixture.Port.Calls);
        Assert.True(receipt.ReleaseCompleted);
        Assert.False(receipt.Resolved);
    }

    [Fact]
    public async Task LeaseDuringConsentCannotBeRenewedAfterItsDeadline()
    {
        var clock = new So6120DirectDutySessionTests.Clock();
        await using var signals = new So6120GuardianSignals(clock);
        Assert.True(signals.Receive(1));
        await signals.FirstHeartbeat;
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(signals.Receive(1));
        Assert.Equal(So6120GuardianStopReason.LeaseExpired, signals.StopReason);
        Assert.True(signals.StopToken.IsCancellationRequested);
        // The existing bounded approval wait observes the pre-execution token.
        Assert.False(await So6120SilentPwmCommand.ConfirmAsync(new StringReader("APPLY"), new StringWriter(),
            "APPLY", signals.StopToken));
    }

    [Fact]
    public async Task ProductStartupGraceAllowsLateFirstHeartbeatThenUsesNormalLease()
    {
        var clock = new So6120DirectDutySessionTests.Clock();
        await using var signals = new So6120GuardianSignals(clock, startupLease: TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(7));
        signals.CheckDeadline();
        Assert.Equal(So6120GuardianStopReason.None, signals.StopReason);
        Assert.True(signals.Receive(1));
        await signals.FirstHeartbeat;
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(signals.Receive(1));
        Assert.Equal(So6120GuardianStopReason.LeaseExpired, signals.StopReason);
    }

    [Fact]
    public async Task ProductStartupGraceStillExpiresWithoutHeartbeat()
    {
        var clock = new So6120DirectDutySessionTests.Clock();
        await using var signals = new So6120GuardianSignals(clock, startupLease: TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(signals.Receive(1));
        Assert.True(signals.StopToken.IsCancellationRequested);
        Assert.False(signals.FirstHeartbeat.IsCompleted);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task NativeReleaseAndAcquisitionDisposalAreIndependentOfConfirmedAuto(bool closeConfirmed, bool disposalThrows)
    {
        using var fixture = So6120SilentTrialTests.StopFixture();
        bool verifiedAfterDisposal = false;
        var lifetime = new Lifetime(() =>
        {
            Assert.True(fixture.Port.Disposed);
            if (disposalThrows) { throw new IOException("Synthetic acquisition release failure."); }
        });
        var guardian = So6120TrialGuardian.Start(progress =>
        {
            var session = fixture.Create();
            var plan = session.PrepareBalancedStop(new(() => true, () => true, progress));
            return new(session, plan, new(plan.PlanId, plan.PlanSha256, Guid.NewGuid(), new('C', 64)), lifetime, () =>
            {
                verifiedAfterDisposal = lifetime.Disposed && fixture.Port.Disposed;
                return closeConfirmed;
            });
        }, fixture.Clock);
        await using var ownership = fixture.Track(guardian);
        var receipt = await guardian.Completion.WaitAsync(Fixture.GuardianWait);
        Assert.True(receipt.AutoConfirmed);
        Assert.True(receipt.CleanupConfirmed);
        Assert.True(receipt.JournalCompleted);
        Assert.True(verifiedAfterDisposal);
        Assert.Equal(closeConfirmed && !disposalThrows, receipt.ReleaseCompleted);
        Assert.Equal(closeConfirmed && !disposalThrows, receipt.Resolved);
    }

    [Fact]
    public async Task AttachedAutoSignalUsesExistingConfirmationAndHandback()
    {
        using var fixture = So6120SilentTrialTests.StopFixture();
        await using var signals = new So6120GuardianSignals(fixture.Clock);
        Assert.True(signals.Receive(1));
        using var release = new ManualResetEventSlim();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Port.AfterWrite = (_, _) =>
        {
            if (fixture.Port.Writes.Count != 1) { return; }
            pending.SetResult();
            if (!release.Wait(Fixture.GuardianWait)) { throw new TimeoutException(); }
        };
        var guardian = So6120TrialGuardian.Start(progress =>
        {
            var session = fixture.Create();
            var plan = session.PrepareBalancedStop(new(() => true, () => true, progress));
            return new(session, plan, new(plan.PlanId, plan.PlanSha256, Guid.NewGuid(), new('C', 64)));
        }, fixture.Clock, signals: signals);
        await using var ownership = fixture.Track(guardian);
        try
        {
            await pending.Task.WaitAsync(Fixture.GuardianWait);
            Assert.False(signals.Receive(2));
            Assert.False(fixture.Port.Disposed);
            Assert.False(guardian.Completion.IsCompleted);
            Assert.Single(fixture.Port.Writes);
        }
        finally { release.Set(); }
        var receipt = await guardian.Completion.WaitAsync(Fixture.GuardianWait);
        Assert.True(receipt.Resolved);
        Assert.Equal(So6120GuardianStopReason.ExplicitAuto, receipt.StopReason);
        Assert.True(receipt.Trial!.CancelRequested);
        Assert.False(signals.Receive(1));
    }

    [Theory]
    [InlineData("validate-so6120-balanced-pwm-stop", "1", true)]
    [InlineData("validate-so6120-balanced-pwm-stop", "0", false)]
    [InlineData("validate-so6120-balanced-pwm-stop", "-1", false)]
    [InlineData("validate-so6120-balanced-pwm-stop", "1 --raw", false)]
    [InlineData("raw-write", "1", false)]
    public void OwnerEntryHasOnlyFixedTrialsAndDistinctInheritedHandles(string trial, string handle, bool valid)
    {
        string root = Path.Combine(Path.GetTempPath(), "ob-guardian-parse-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(valid, So6120GuardianHost.TryParseOwner(
            ["--trial", trial, "--run-root", root, "--control-handle", handle,
                "--caller-job-handle", "2", "--caller-process-handle", "3"], out _, out _, out _, out _, out _));
    }

    [Theory]
    [InlineData("1", "3")]
    [InlineData("2", "1")]
    [InlineData("2", "2")]
    [InlineData("0", "3")]
    [InlineData("2", "-1")]
    [InlineData("2 --raw", "3")]
    public void OwnerEntryRejectsMissingInvalidOrAliasedIsolationHandles(string job, string caller)
    {
        string root = Path.Combine(Path.GetTempPath(), "ob-guardian-parse-" + Guid.NewGuid().ToString("N"));
        Assert.False(So6120GuardianHost.TryParseOwner(
            ["--trial", So6120SilentPwmCommand.StopName, "--run-root", root, "--control-handle", "1",
                "--caller-job-handle", job, "--caller-process-handle", caller], out _, out _, out _, out _, out _));
        Assert.False(So6120GuardianHost.TryParseOwner(
            ["--trial", So6120SilentPwmCommand.StopName, "--run-root", root, "--control-handle", "1"],
            out _, out _, out _, out _, out _));
    }

    [Fact]
    public void UnconfirmedNativeReleaseRequiresHoldingEitherAcquisitionPath()
    {
        var unknown = new So6120NativeCloseEvidence();
        var failed = new So6120NativeCloseEvidence();
        failed.Record(new(false, 5));
        var closed = new So6120NativeCloseEvidence();
        closed.Record(new(true, 0));
        Assert.False(So6120SilentPwmCommand.NativeReleaseRequiresHold(null, null));
        Assert.False(So6120SilentPwmCommand.NativeReleaseRequiresHold(closed, null));
        Assert.False(So6120SilentPwmCommand.NativeReleaseRequiresHold(null, closed));
        Assert.True(So6120SilentPwmCommand.NativeReleaseRequiresHold(unknown, null));
        Assert.True(So6120SilentPwmCommand.NativeReleaseRequiresHold(failed, null));
        Assert.True(So6120SilentPwmCommand.NativeReleaseRequiresHold(null, failed));
    }

    [Theory]
    [InlineData("BASELINE run")]
    [InlineData("APPLY run digest")]
    public async Task CallerStopDuringApprovalIsCancellationRatherThanDecline(string approval)
    {
        await using var signals = new So6120GuardianSignals();
        Assert.True(signals.Receive(1));
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new ApprovalReader(started, release, approval);
        try
        {
            var confirmation = So6120SilentPwmCommand.ConfirmLiveApprovalAsync(reader, new StringWriter(), approval, signals.StopToken);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            signals.CallerDisconnected();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => confirmation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(So6120GuardianStopReason.CallerDisconnected, signals.StopReason);
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task MismatchedApprovalReceiptIsRecordedBeforeMissingTrialFailure()
    {
        using var fixture = So6120SilentTrialTests.StopFixture();
        var guardian = So6120TrialGuardian.Start(progress =>
        {
            var session = fixture.Create();
            var plan = session.PrepareBalancedStop(new(() => true, () => true, progress));
            return new(session, plan, new(Guid.NewGuid(), plan.PlanSha256, Guid.NewGuid(), new('C', 64)));
        }, fixture.Clock);
        await using var ownership = fixture.Track(guardian);
        var receipt = await guardian.Completion.WaitAsync(Fixture.GuardianWait);
        Assert.Null(receipt.Trial);
        Assert.True(receipt.ReleaseCompleted);
        Assert.False(receipt.Resolved);
        Assert.Empty(fixture.Port.Writes);
        var rows = new List<string>();
        Assert.Throws<InvalidOperationException>(() => So6120SilentPwmCommand.RecordGuardianOutcome(
            row => rows.Add(System.Text.Json.JsonSerializer.Serialize(row)), receipt));
        using var recorded = System.Text.Json.JsonDocument.Parse(Assert.Single(rows));
        Assert.Equal("guardianReceipt", recorded.RootElement.GetProperty("phase").GetString());
        Assert.Equal(receipt.Failure, recorded.RootElement.GetProperty("receipt").GetProperty("Failure").GetString());
        Assert.True(recorded.RootElement.GetProperty("receipt").GetProperty("ReleaseCompleted").GetBoolean());
    }

    private sealed class ApprovalReader(TaskCompletionSource started, ManualResetEventSlim release, string response) : TextReader
    {
        public override string ReadLine() { started.TrySetResult(); release.Wait(); return response; }
    }

    private sealed class Lifetime(Action dispose) : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() { Disposed = true; dispose(); }
    }
}
