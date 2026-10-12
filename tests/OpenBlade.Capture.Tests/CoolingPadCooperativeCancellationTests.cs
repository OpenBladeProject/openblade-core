using OpenBlade.Capture;
using OpenBlade.Protocol;
using System.Text.Json;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadCooperativeCancellationTests
{
    private const PadProcessTrial Trial = PadProcessTrial.CooperativeCancellation;

    [Fact]
    public void ResultLabelsLimitEvidenceToDeveloperArmedIdleWait()
    {
        var result = new PadProcessResult(true, null, "0000", true, [], [1, 2], false);
        using var cooperative = JsonDocument.Parse(CoolingPadFanProcessContinuityCommand.SerializeResult(result, Trial));
        var root = cooperative.RootElement;
        Assert.Equal(CoolingPadFanProcessContinuityCommand.CooperativeName, root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("cooperativeCancellationTested").GetBoolean());
        Assert.Equal("developer-armed-idle-wait", root.GetProperty("cancellationWorkKind").GetString());
        foreach (string claim in new[] { "productionWorkLoopCancellationTested", "inFlightIoCancellationTested",
            "processCrashTested", "productionAdmitted", "ownershipConfirmed", "handbackConfirmed" })
        { Assert.False(root.GetProperty(claim).GetBoolean()); }
        using var normal = JsonDocument.Parse(CoolingPadFanProcessContinuityCommand.SerializeResult(result, PadProcessTrial.NormalExit));
        Assert.Equal(CoolingPadFanProcessContinuityCommand.Name, normal.RootElement.GetProperty("command").GetString());
        Assert.False(normal.RootElement.GetProperty("cooperativeCancellationTested").GetBoolean());
        Assert.Equal(JsonValueKind.Null, normal.RootElement.GetProperty("cancellationWorkKind").ValueKind);
    }

    [Fact]
    public async Task WorkWaitReallyCancelsOnceWithoutCancelingControlToken()
    {
        var pending = new PendingDisposalRegistry();
        using var control = new CancellationTokenSource();
        var work = new CoolingPadCooperativeCancellation(pending);
        Assert.True(work.Armed);
        await work.ObserveAsync(control.Token);
        Assert.True(work.WorkToken.IsCancellationRequested);
        Assert.False(control.IsCancellationRequested);
        Assert.False(work.Armed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => work.ObserveAsync(control.Token));
        work.Dispose();
        await pending.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task SupervisorCancellationCannotBeMistakenForPlannedWorkCancellation()
    {
        var pending = new PendingDisposalRegistry();
        using var control = new CancellationTokenSource();
        var work = new CoolingPadCooperativeCancellation(pending);
        control.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.ObserveAsync(control.Token));
        Assert.False(work.WorkToken.IsCancellationRequested);
        work.Dispose();
        await pending.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task BlockedCancellationCallbackTimesOutAndLateCompletionCannotBecomeSuccess()
    {
        var pending = new PendingDisposalRegistry();
        using var release = new ManualResetEventSlim();
        var work = new CoolingPadCooperativeCancellation(pending);
        using var registration = work.WorkToken.Register(() => release.Wait());
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => work.ObserveAsync(default, TimeSpan.FromMilliseconds(50)));
            work.Dispose();
            Assert.True(pending.Count > 0);
        }
        finally { release.Set(); work.Dispose(); }
        await pending.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void ContinuationRequiresObservedCancellationDisposalReceiptExitFourAndFreshPhysicalYes()
    {
        var state = new PadProcessState(Trial);
        state.Begin(1);
        Assert.Throws<InvalidDataException>(state.ConfirmPostCancellationPhysical);
        foreach (var step in PadProcessSequence.ForRole(1, Trial))
        {
            if (step.Name == "cancelWork")
            {
                Assert.Throws<InvalidDataException>(() => state.Receipt(PadProcessSequence.Receipt(1, Trial)));
                Assert.Throws<InvalidDataException>(() => state.Exited(4));
            }
            Complete(state, 1, step);
        }
        Assert.Throws<InvalidDataException>(() => state.Receipt("COMPLETE:1:FullyDisposed"));
        state.Receipt(PadProcessSequence.Receipt(1, Trial));
        Assert.Throws<InvalidDataException>(() => state.Exited(0));
        Assert.Throws<InvalidDataException>(() => state.Exited(2));
        Assert.Throws<InvalidDataException>(() => state.Begin(2));
        state.Exited(4);
        Assert.Throws<InvalidDataException>(() => state.Begin(2));
        state.ConfirmPostCancellationPhysical();
        Assert.Throws<InvalidDataException>(state.ConfirmPostCancellationPhysical);
        state.Begin(2);
        Assert.Throws<InvalidDataException>(() => state.Begin(2));
        foreach (var step in PadProcessSequence.ForRole(2, Trial)) { Complete(state, 2, step); }
        state.Receipt(PadProcessSequence.Receipt(2, Trial));
        Assert.Throws<InvalidDataException>(() => state.Exited(4));
        state.Exited(0);
        Assert.True(state.Success);
        Assert.Equal("0000", state.OriginalBaseline);
    }

    [Fact]
    public void CancellationNotObservedOrOutOfOrderNeverAllowsCloseReceipt()
    {
        var state = new PadProcessState(Trial);
        state.Begin(1);
        Assert.Throws<InvalidDataException>(() => state.Request("READY:1:cancelWork"));
        foreach (var step in PadProcessSequence.ForRole(1, Trial).TakeWhile(s => s.Name != "cancelWork"))
        { Complete(state, 1, step); }
        var cancellation = state.Request("READY:1:cancelWork");
        state.Permit(cancellation);
        Assert.Throws<InvalidDataException>(() => state.Complete("DONE:1:cancelWork:CancellationNotObserved"));
        Assert.Throws<InvalidDataException>(() => state.Request("READY:1:close"));
        Assert.Throws<InvalidDataException>(() => state.Receipt(PadProcessSequence.Receipt(1, Trial)));
        Assert.Throws<InvalidDataException>(() => state.Begin(2));
    }

    [Fact]
    public void NormalExitTrialStillRejectsCancellationReceiptAndExitFour()
    {
        var state = new PadProcessState();
        state.Begin(1);
        foreach (var step in PadProcessSequence.ForRole(1)) { Complete(state, 1, step); }
        Assert.Throws<InvalidDataException>(() => state.Receipt(PadProcessSequence.Receipt(1, Trial)));
        state.Receipt("COMPLETE:1:FullyDisposed");
        Assert.Throws<InvalidDataException>(() => state.Exited(4));
        state.Exited(0);
        state.Begin(2);
    }

    [Fact]
    public async Task TrialCliIdentitiesAreNotInterchangeableAndChildRejectsDirectInvocation()
    {
        string[] args = ["--confirm-target", CoolingPadFanProcessContinuityCommand.ExactTarget,
            "--confirm-recovery", CoolingPadFanProcessContinuityCommand.ExactRecovery];
        Assert.False(CoolingPadFanProcessContinuityCommand.Admitted(args, Trial));
        Assert.Equal(1, await CoolingPadFanProcessContinuityCommand.RunAsync(args,
            new StringReader("YES"), new StringWriter(), new StringWriter(), default, Trial));
        args[1] = CoolingPadFanProcessContinuityCommand.CooperativeTarget;
        Assert.True(CoolingPadFanProcessContinuityCommand.Admitted(args, Trial));
        Assert.False(CoolingPadFanProcessContinuityCommand.Admitted(args));
        Assert.False(CoolingPadFanProcessContinuityCommand.Admitted([.. args, "--resume", "saved.json"], Trial));
        Assert.Equal(2, await CoolingPadFanProcessContinuityCommand.ChildAsync(false, [], default, Trial));
    }

    [Fact]
    public void CancellationAddsNoHidCommandAndLeavesCapturedTransactionPartitionsUnchanged()
    {
        foreach (int role in new[] { 1, 2 })
        {
            Assert.Equal(PadProcessSequence.ForRole(role).Where(s => s.Transaction != 0),
                PadProcessSequence.ForRole(role, Trial).Where(s => s.Transaction != 0));
        }
        var first = PadProcessSequence.ForRole(1, Trial);
        Assert.Equal("fixedPhysical", first[^3].Name);
        Assert.Equal("cancelWork", first[^2].Name);
        Assert.Equal("close", first[^1].Name);
        Assert.False(first[^2].Mutation || first[^1].Mutation);
    }

    private static void Complete(PadProcessState state, int role, PadProcessStep step)
    {
        Assert.Equal(step, state.Request($"READY:{role}:{step.Name}"));
        state.Permit(step);
        state.Complete($"DONE:{role}:{step.Name}:{step.Result}");
    }
}
