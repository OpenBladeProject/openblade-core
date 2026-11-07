using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class So6120GuardianIntegrationTests
{
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
        var receipt = await guardian.Completion.WaitAsync(TimeSpan.FromSeconds(10));
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
        var receipt = await guardian.Completion.WaitAsync(TimeSpan.FromSeconds(15));
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
            if (!release.Wait(TimeSpan.FromSeconds(15))) { throw new TimeoutException(); }
        };
        var guardian = So6120TrialGuardian.Start(progress =>
        {
            var session = fixture.Create();
            var plan = session.PrepareBalancedStop(new(() => true, () => true, progress));
            return new(session, plan, new(plan.PlanId, plan.PlanSha256, Guid.NewGuid(), new('C', 64)));
        }, fixture.Clock, signals: signals);
        try
        {
            await pending.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(signals.Receive(2));
            Assert.False(fixture.Port.Disposed);
            Assert.False(guardian.Completion.IsCompleted);
            Assert.Single(fixture.Port.Writes);
        }
        finally { release.Set(); }
        var receipt = await guardian.Completion.WaitAsync(TimeSpan.FromSeconds(15));
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
    public void OwnerEntryHasOnlyFixedTrialsAndAnInheritedControlHandle(string trial, string handle, bool valid)
    {
        string root = Path.Combine(Path.GetTempPath(), "ob-guardian-parse-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(valid, So6120GuardianHost.TryParseOwner(
            ["--trial", trial, "--run-root", root, "--control-handle", handle], out _, out _, out _));
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
        var receipt = await guardian.Completion.WaitAsync(TimeSpan.FromSeconds(10));
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
