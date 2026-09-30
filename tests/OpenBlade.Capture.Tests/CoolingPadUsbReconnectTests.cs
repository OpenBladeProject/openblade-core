using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadUsbReconnectTests
{
    private const PadProcessTrial Trial = PadProcessTrial.UsbOnlyReconnect;

    [Fact]
    public async Task ReconnectPromptFollowsFiveSecondsOfSampledAbsenceAndNeedsFreshPhysicalYes()
    {
        var fixture = new PadUsbReconnectFake("none");
        List<string> prompts = [];
        var result = await fixture.RunAsync((stage, _) =>
        {
            if (stage == "reconnectUsbSamePort") { Assert.True(fixture.GetTimestamp() >= TimeSpan.TicksPerSecond * 5); }
            prompts.Add(stage);
            return Task.FromResult(true);
        }, default);
        Assert.True(result.Success);
        Assert.Equal(5000, result.ObservedAbsenceMilliseconds);
        Assert.Equal(new[] { "disconnectUsbOnly", "reconnectUsbSamePort", "postReconnectPhysical" }, prompts);
        Assert.Equal(250, result.NominalPollingIntervalMilliseconds);
        Assert.False(result.CanExcludeReconnectBetweenSamples || result.PassiveObservationEstablishesExactIdentity);
        Assert.True(result.PowerAndSamePadSamePortAreOperatorProvenanceOnly);
    }

    [Theory]
    [InlineData("usb-premature-reconnect", "UsbReconnectedBeforeFiveSeconds", 1)]
    [InlineData("usb-observation-failure", "UsbPresenceObservationFailed", 1)]
    [InlineData("usb-no-absence", "UsbPresenceTimeout", 1)]
    [InlineData("usb-no-reconnect", "UsbPresenceTimeout", 2)]
    public async Task PresenceFailureStopsWithoutLaterPhysicalOrReconnectPermission(string fault, string reason, int promptCount)
    {
        int prompts = 0;
        var result = await new PadUsbReconnectFake(fault).RunAsync((_, _) =>
        { prompts++; return Task.FromResult(true); }, default);
        Assert.False(result.Success);
        Assert.Contains(reason, result.Failure);
        Assert.Equal(promptCount, prompts);
        Assert.False(result.PhysicalMediumConfirmed);
    }

    [Theory]
    [InlineData("disconnectUsbOnly")]
    [InlineData("reconnectUsbSamePort")]
    [InlineData("postReconnectPhysical")]
    public async Task EachManualRefusalStopsTheBoundary(string rejected)
    {
        List<string> prompts = [];
        var result = await new PadUsbReconnectFake("none").RunAsync((stage, _) =>
        { prompts.Add(stage); return Task.FromResult(stage != rejected); }, default);
        Assert.False(result.Success);
        Assert.Equal(rejected, prompts[^1]);
        Assert.False(result.PhysicalMediumConfirmed);
    }

    [Fact]
    public async Task BlockedPassiveEnumerationIsBoundedAndLateResultCannotResume()
    {
        var pending = new PendingDisposalRegistry();
        using var release = new ManualResetEventSlim();
        int prompts = 0;
        var source = new Presence(() => { release.Wait(); return CoolingPadPhysicalPresenceObservation.Present; });
        var boundary = new CoolingPadUsbReconnectBoundary(source,
            observationTimeout: TimeSpan.FromMilliseconds(50), retained: pending);
        try
        {
            var result = await boundary.RunAsync((_, _) => { prompts++; return Task.FromResult(true); }, default)
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(result.Success);
            Assert.Contains("TimeoutException", result.Failure);
            Assert.Equal(0, prompts);
        }
        finally { release.Set(); }
        await pending.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, prompts);
    }

    [Fact]
    public async Task StaleCompletedObservationDoesNotCountAsFreshPresence()
    {
        var clock = new JumpClock();
        var source = new Presence(() => { clock.Advance(TimeSpan.FromSeconds(4)); return CoolingPadPhysicalPresenceObservation.Present; });
        int prompts = 0;
        var result = await new CoolingPadUsbReconnectBoundary(source, clock).RunAsync((_, _) =>
        { prompts++; return Task.FromResult(true); }, default);
        Assert.Contains("StaleUsbPresenceObservation", result.Failure);
        Assert.Equal(0, prompts);
    }

    [Fact]
    public async Task CancellationDuringNoncooperativePauseCannotResumeOnLateCompletion()
    {
        var pending = new PendingDisposalRegistry();
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int samples = 0;
        List<string> prompts = [];
        var source = new Presence(() => Interlocked.Increment(ref samples) == 1
            ? CoolingPadPhysicalPresenceObservation.Present : CoolingPadPhysicalPresenceObservation.Absent);
        var run = new CoolingPadUsbReconnectBoundary(source, delay: (_, _) =>
        { entered.SetResult(); return late.Task; }, retained: pending).RunAsync((stage, _) =>
        { prompts.Add(stage); return Task.FromResult(true); }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Canceled", result.Failure);
        late.SetResult();
        await pending.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, samples);
        Assert.Equal(new[] { "disconnectUsbOnly" }, prompts);
    }

    [Fact]
    public void NewChildNeedsExitZeroAndCompleteUsbBoundaryAndRawZeroCannotPermitAuto()
    {
        var state = new PadProcessState(Trial);
        state.Begin(1);
        Assert.Throws<InvalidDataException>(() => state.ConfirmUsbReconnect(CompleteBoundary()));
        foreach (var step in PadProcessSequence.ForRole(1, Trial)) { Complete(state, 1, step); }
        state.Receipt("COMPLETE:1:FullyDisposed");
        Assert.Throws<InvalidDataException>(() => state.Exited(4));
        state.Exited(0);
        Assert.Throws<InvalidDataException>(() => state.Begin(2));
        Assert.Throws<InvalidDataException>(() => state.ConfirmUsbReconnect(CompleteBoundary() with { ObservedAbsenceMilliseconds = 4999 }));
        Assert.Throws<InvalidDataException>(() => state.ConfirmUsbReconnect(CompleteBoundary() with { PhysicalMediumConfirmed = false }));
        state.ConfirmUsbReconnect(CompleteBoundary());
        Assert.Throws<InvalidDataException>(() => state.ConfirmUsbReconnect(CompleteBoundary()));
        state.Begin(2);
        Complete(state, 2, PadProcessSequence.ForRole(2)[0]);
        var query = state.Request("READY:2:intermediate");
        state.Permit(query);
        Assert.Throws<InvalidDataException>(() => state.Complete("DONE:2:intermediate:0000"));
        Assert.Equal("0000", state.OriginalBaseline);
        Assert.Equal("0000", state.RawIntermediate);
        Assert.Contains("READ:2:intermediate:0000", state.Evidence);
        Assert.Throws<InvalidDataException>(() => state.Request("READY:2:auto"));
        Assert.DoesNotContain("PERMIT:2:auto", state.Evidence);
    }

    [Fact]
    public async Task UsbTrialCliCannotReuseOtherTrialAdmissionOrResumeAFile()
    {
        string[] args = ["--confirm-target", CoolingPadFanProcessContinuityCommand.UsbTarget,
            "--confirm-recovery", CoolingPadFanProcessContinuityCommand.ExactRecovery];
        Assert.True(CoolingPadFanProcessContinuityCommand.Admitted(args, Trial));
        Assert.False(CoolingPadFanProcessContinuityCommand.Admitted(args));
        Assert.False(CoolingPadFanProcessContinuityCommand.Admitted(args, PadProcessTrial.CooperativeCancellation));
        Assert.False(CoolingPadFanProcessContinuityCommand.Admitted([.. args, "--resume", "saved.json"], Trial));
        args[1] = CoolingPadFanProcessContinuityCommand.ExactTarget;
        Assert.Equal(1, await CoolingPadFanProcessContinuityCommand.RunAsync(args,
            new StringReader("YES"), new StringWriter(), new StringWriter(), default, Trial));
        Assert.Equal(2, await CoolingPadFanProcessContinuityCommand.ChildAsync(false, [], default, Trial));
    }

    [Fact]
    public void JsonLabelsExposeSampledAndOperatorEvidenceWithoutBroaderClaims()
    {
        var result = new PadProcessResult(true, null, "0000", true, [], [1, 2], false) { UsbReconnect = CompleteBoundary() };
        using var json = JsonDocument.Parse(CoolingPadFanProcessContinuityCommand.SerializeResult(result, Trial));
        var root = json.RootElement;
        Assert.Equal(CoolingPadFanProcessContinuityCommand.UsbName, root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("fixedUsbOnlyReconnectTested").GetBoolean());
        Assert.True(root.GetProperty("powerAndSamePadSamePortAreOperatorProvenanceOnly").GetBoolean());
        foreach (string field in new[] { "activeHandleDisconnectTested", "powerLossTested", "processCrashTested",
            "cooperativeCancellationTested", "productionAdmitted", "ownershipConfirmed", "handbackConfirmed" })
        { Assert.False(root.GetProperty(field).GetBoolean()); }
    }

    private static PadUsbReconnectObservation CompleteBoundary() => new()
    {
        InitiallyPresent = true,
        DisconnectAcknowledged = true,
        ObservedAbsenceMilliseconds = 5000,
        ReconnectAcknowledged = true,
        Reconnected = true,
        PhysicalMediumConfirmed = true,
    };
    private static void Complete(PadProcessState state, int role, PadProcessStep step)
    {
        Assert.Equal(step, state.Request($"READY:{role}:{step.Name}"));
        state.Permit(step);
        state.Complete($"DONE:{role}:{step.Name}:{step.Result}");
    }
    private sealed class Presence(Func<CoolingPadPhysicalPresenceObservation> observe) : ICoolingPadPhysicalPresenceSource
    { public CoolingPadPhysicalPresenceObservation Observe() => observe(); }
    private sealed class JumpClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        internal void Advance(TimeSpan interval) => Interlocked.Add(ref timestamp, interval.Ticks);
    }
}
