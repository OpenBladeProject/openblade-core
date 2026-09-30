using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadFanProcessContinuityTests
{
    [Fact]
    public async Task ObserverCancellationIsBoundedEvenWhenCallbackBlocksAndLateYesArrives()
    {
        var pending = new PendingDisposalRegistry();
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> observation = CoolingPadFanProcessContinuityCommand.ConfirmAsync(async (_, ct) =>
        {
            using var registration = ct.Register(() => release.Wait());
            ready.SetResult();
            return await late.Task;
        }, "fixedPhysical", cancellation.Token, TimeSpan.FromSeconds(5), pending: pending);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observation.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally { release.Set(); late.TrySetResult(true); }
        await pending.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(observation.IsCanceled);
    }

    [Fact]
    public async Task ObserverElapsedDeadlineRejectsYesEvenIfTimerHasNotFired()
    {
        var pending = new PendingDisposalRegistry();
        var clock = new JumpClock();
        await Assert.ThrowsAsync<TimeoutException>(() => CoolingPadFanProcessContinuityCommand.ConfirmAsync((_, _) =>
        {
            clock.Advance();
            return Task.FromResult(true);
        }, "fixedPhysical", default, TimeSpan.FromMilliseconds(100), clock, pending));
        await pending.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task LaunchTimeoutRetainsAndObservesLateFailureWithoutResuming()
    {
        var pending = new PendingDisposalRegistry();
        var launch = new TaskCompletionSource<System.Diagnostics.Process>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Assert.ThrowsAnyAsync<IOException>(() => CoolingPadFanProcessContinuityCommand.AwaitChildLaunchAsync(
            launch.Task, TimeSpan.FromMilliseconds(50), default, pending));
        launch.SetException(new IOException("Late start failure."));
        await pending.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class JumpClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        internal void Advance() => Interlocked.Add(ref timestamp, TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void BuildIdentityRejectsOldLoadedModuleAgainstNewDiskAssembly()
    {
        var assembly = typeof(PadProcessBuild).Assembly;
        using var disk = File.OpenRead(assembly.Location);
        PadProcessBuild.RequireLoadedModule(disk, assembly.ManifestModule.ModuleVersionId);
        disk.Position = 0;
        Assert.Throws<IOException>(() => PadProcessBuild.RequireLoadedModule(disk, Guid.NewGuid()));
        disk.Position = 0;
        Assert.Throws<IOException>(() => PadProcessBuild.RequireLoadedModule(disk, Guid.Empty));
    }

    [Fact]
    public void SecondRoleRequiresFullSequenceReceiptAndRealZeroExit()
    {
        var state = new PadProcessState();
        Assert.Throws<InvalidDataException>(() => state.Begin(2));
        state.Begin(1);
        Assert.Throws<InvalidDataException>(() => state.Begin(1));
        Assert.Throws<InvalidDataException>(() => state.Begin(2));
        Assert.Throws<InvalidDataException>(() => state.Receipt("COMPLETE:1:FullyDisposed"));
        CompleteRole(state, 1);
        Assert.Throws<InvalidDataException>(() => state.Exited(0));
        Assert.Throws<InvalidDataException>(() => state.Begin(2));
        state.Receipt("COMPLETE:1:FullyDisposed");
        Assert.Throws<InvalidDataException>(() => state.Begin(2));
        Assert.Throws<InvalidDataException>(() => state.Exited(2));
        Assert.Throws<InvalidDataException>(() => state.Exited(-1));
        state.Exited(0);
        Assert.Throws<InvalidDataException>(() => state.Exited(0));
        state.Begin(2);
        Assert.Throws<InvalidDataException>(() => state.Begin(2));
        CompleteRole(state, 2);
        Assert.False(state.Success);
        state.Receipt("COMPLETE:2:FullyDisposed");
        state.Exited(0);
        Assert.True(state.Success);
        Assert.Equal("0000", state.OriginalBaseline);
        Assert.Contains("DONE:2:intermediate:0300", state.Evidence);
        Assert.Contains("DONE:2:restore:0000", state.Evidence);
        Assert.Throws<InvalidDataException>(() => state.Begin(2));
    }

    [Theory]
    [InlineData("READY:2:open")]
    [InlineData("READY:1:fixed")]
    [InlineData("READY:1:open:0000")]
    [InlineData("COMPLETE:1:FullyDisposed")]
    [InlineData("")]
    public void InvalidFirstFrameCannotPermitAnyMutation(string frame)
    {
        var state = new PadProcessState();
        state.Begin(1);
        Assert.Throws<InvalidDataException>(() => state.Request(frame));
        Assert.Null(state.OriginalBaseline);
        Assert.False(state.MutationPermitted);
        Assert.Empty(state.Evidence);
    }

    [Fact]
    public void BaselineAndIntermediateCannotBecomeDifferentRestoreAuthority()
    {
        var state = new PadProcessState();
        state.Begin(1);
        Step(state, 1, PadProcessSequence.ForRole(1)[0]);
        var baseline = state.Request("READY:1:baseline");
        state.Permit(baseline);
        Assert.Throws<InvalidDataException>(() => state.Complete("DONE:1:baseline:0300"));
        Assert.Null(state.OriginalBaseline);
        Assert.False(state.MutationPermitted);
        Assert.Throws<InvalidDataException>(() => state.Request("READY:1:acquire"));
    }

    [Fact]
    public void EveryResultNeedsOneLivePermitAndCannotBeReplayed()
    {
        var state = new PadProcessState();
        state.Begin(1);
        var step = state.Request("READY:1:open");
        Assert.Throws<InvalidDataException>(() => state.Complete("DONE:1:open:opened"));
        state.Permit(step);
        Assert.Throws<InvalidDataException>(() => state.Permit(step));
        state.Complete("DONE:1:open:opened");
        Assert.Throws<InvalidDataException>(() => state.Complete("DONE:1:open:opened"));
        Assert.Throws<InvalidDataException>(() => state.Request("READY:1:open"));
    }

    [Theory]
    [InlineData("ManualPowerCycleAndVendorRecovery")]
    [InlineData("manualpowercycleonly")]
    [InlineData("")]
    public async Task ExactCliRejectsAlternativesBeforeSupervisorOrHardware(string recovery)
    {
        string[] args = ["--confirm-target", CoolingPadFanProcessContinuityCommand.ExactTarget,
            "--confirm-recovery", recovery];
        Assert.Equal(1, await CoolingPadFanProcessContinuityCommand.RunAsync(args,
            new StringReader("YES"), new StringWriter(), new StringWriter(), default));
        args[3] = CoolingPadFanProcessContinuityCommand.ExactRecovery;
        Assert.True(CoolingPadFanProcessContinuityCommand.Admitted(args));
        Assert.False(CoolingPadFanProcessContinuityCommand.Admitted([.. args, "--payload", "000600"]));
        args[1] = args[1].Replace("0200", "0201", StringComparison.Ordinal);
        Assert.False(CoolingPadFanProcessContinuityCommand.Admitted(args));
    }

    [Fact]
    public async Task ChildWithoutAuthenticatedContextRejectsBeforeNativeFactory()
    {
        Assert.Equal(2, await CoolingPadFanProcessContinuityCommand.ChildAsync(false, [], default));
        Assert.Equal(2, await CoolingPadFanProcessContinuityCommand.ChildAsync(false,
            ["saved-journal.json", "0000", "2"], default));
        Assert.Equal(2, await CoolingPadFanProcessContinuityCommand.ChildAsync(false,
            ["OpenBlade.PadProcess." + new string('A', 64), new string('A', 64), "3"], default));
    }

    [Fact]
    public void FixedRolesPartitionTransactionsAndContainNoImplicitCleanupWrites()
    {
        var first = PadProcessSequence.ForRole(1);
        var second = PadProcessSequence.ForRole(2);
        Assert.Equal(new[] { 1, 2, 3, 4 }, first.Where(s => s.Transaction > 0).Select(s => s.Transaction));
        Assert.Equal(new[] { 5, 6, 7, 8 }, second.Where(s => s.Transaction > 0).Select(s => s.Transaction));
        Assert.Equal(new[] { "0300", "01012C" }, first.Where(s => s.Mutation).Select(s => s.Arguments));
        Assert.Equal(new[] { "000600", "0000" }, second.Where(s => s.Mutation).Select(s => s.Arguments));
        Assert.Equal("close", first[^1].Name);
        Assert.Equal("close", second[^1].Name);
        Assert.False(first[^1].Mutation || second[^1].Mutation);
    }

    private static void CompleteRole(PadProcessState state, int role)
    {
        foreach (var step in PadProcessSequence.ForRole(role)) { Step(state, role, step); }
    }
    private static void Step(PadProcessState state, int role, PadProcessStep step)
    {
        Assert.Equal(step, state.Request($"READY:{role}:{step.Name}"));
        state.Permit(step);
        state.Complete($"DONE:{role}:{step.Name}:{step.Result}");
    }
}
