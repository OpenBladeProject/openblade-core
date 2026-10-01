using System.Text.Json;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadCounterValidationTests
{
    [Fact]
    public async Task CompleteTrial_ActuallyRepeatsDemandAndTwoSuccessors_ThenExplicitSequentialRestoration()
    {
        var fake = new Session();
        var factory = Factory(fake);
        var stages = new List<string>();
        var runner = Runner(factory);
        var result = await runner.RunAsync((stage, _) => { stages.Add(stage); return Task.FromResult(true); }, default);
        Assert.True(result.Completed);
        Assert.True(result.RepeatedDemandIdAndTwoSuccessorsAcknowledged);
        Assert.Equal(43, result.CompletedLogicalExchangeCount);
        Assert.Equal(34, result.Decisions.Count);
        Assert.Equal(new[] { 2200, 1000, 2200 }, result.Decisions.Take(3).Select(x => x.LastAcknowledgedDemand!.Value.Rpm));
        Assert.Equal(new byte[] { 0x1D, 0x1E, 0, 1, 2 }, result.Exchanges.Skip(28).Take(5).Select(x => x.WireId));
        Assert.Equal(new byte[] { 6, 7, 8 }, result.Exchanges.Skip(36).Take(3).Select(x => x.WireId));
        Assert.Equal("0D/10:000600", fake.Commands[4]);
        Assert.Equal("0D/01:01052C", fake.Commands[5]);
        Assert.Equal(new[] { "0D/10:01012C", "0D/10:000600", "00/04:0000", "00/84:0000" }, fake.Commands.TakeLast(4));
        Assert.Equal(new[] { "firstDemandPhysicalConfirmation", "counterIntervalPhysicalConfirmation",
            "mediumPhysicalConfirmation", "quietPhysicalConfirmation", "finalPhysicalConfirmation" }, stages);
        Assert.False(result.ManualAttentionRequired);
        Assert.True(fake.Disposed);
        Assert.Null((await factory.OpenAsync(default)).Session); // No second epoch/reopen.
    }

    [Theory]
    [InlineData("late")]
    [InlineData("fault")]
    [InlineData("incomplete")]
    [InlineData("transient")]
    public async Task CleanupRequiresVerifiedFullyDisposedActor_NotJustManagedDrain(string behavior)
    {
        var fake = new Session { DisposalBehavior = behavior };
        var factory = Factory(fake);
        var runner = CoolingPadCounterValidationRunner.ForTesting(factory, cleanupTimeout: TimeSpan.FromMilliseconds(50));
        Task<CoolingPadCounterValidationResult> operation = runner.RunAsync((_, _) => Task.FromResult(true), default);
        await fake.DisposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (behavior == "transient") { fake.ReleaseDisposal.TrySetResult(); }
        var result = await operation.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.Equal(behavior == "transient", result.Completed);
        Assert.Equal(behavior == "transient", result.ResourceCleanupCompleted);
        Assert.Equal(behavior == "transient" ? 43 : (int?)null, result.CompletedLogicalExchangeCount);
        if (behavior != "transient") { Assert.True(result.ManualAttentionRequired); }
        if (behavior == "late")
        {
            Assert.False(factory.DeveloperCounterResourcesReleased);
            fake.ReleaseDisposal.TrySetResult();
            await fake.DisposalCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(result.Completed); // A late release never rewrites the returned evidence.
        }
    }

    [Theory]
    [InlineData(31)]
    [InlineData(37)]
    [InlineData(38)]
    [InlineData(39)]
    public async Task BoundaryOrRepeatedDemandFailure_IsTerminal_NoRestorationOrReopen(int failedExchange)
    {
        var fake = new Session { FailAt = failedExchange };
        var result = await Runner(Factory(fake)).RunAsync((_, _) => Task.FromResult(true), default);
        Assert.False(result.Completed);
        Assert.False(result.RepeatedDemandIdAndTwoSuccessorsAcknowledged);
        Assert.True(result.SubmissionStateUnknown);
        Assert.True(result.ManualAttentionRequired);
        Assert.Null(result.CompletedLogicalExchangeCount);
        Assert.Equal(failedExchange, fake.Commands.Count);
        Assert.Null(result.Restoration);
    }

    [Theory]
    [InlineData("firstDemandPhysicalConfirmation", 6)]
    [InlineData("counterIntervalPhysicalConfirmation", 39)]
    [InlineData("mediumPhysicalConfirmation", 40)]
    [InlineData("quietPhysicalConfirmation", 41)]
    [InlineData("finalPhysicalConfirmation", 43)]
    public async Task RejectedPhysicalGate_NeverContinues_AndRetainsEvidence(string rejected, int count)
    {
        var fake = new Session();
        var result = await Runner(Factory(fake)).RunAsync((stage, _) => Task.FromResult(stage != rejected), default);
        Assert.False(result.Completed);
        Assert.True(result.ManualAttentionRequired);
        Assert.Equal(count, fake.Commands.Count);
    }

    [Fact]
    public async Task CancellationDuringInterval_DoesNotRestoreOrContinue()
    {
        var fake = new Session();
        using var canceled = new CancellationTokenSource();
        var runner = CoolingPadCounterValidationRunner.ForTesting(Factory(fake),
            _ => { canceled.Cancel(); return Task.CompletedTask; });
        var result = await runner.RunAsync((_, _) => Task.FromResult(true), canceled.Token);
        Assert.False(result.Completed);
        Assert.Equal(6, fake.Commands.Count);
        Assert.Null(result.Restoration);
        Assert.True(fake.Disposed);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Command_RequiresExactAcknowledgmentAndFreshPhysicalGateBeforeRunner(bool targetValid, bool initialYes)
    {
        bool invoked = false;
        using var output = new StringWriter();
        int code = await CoolingPadCounterValidationCommand.RunAsync(Arguments(targetValid),
            (_, _) => { invoked = true; throw new InvalidOperationException(); },
            (_, _) => Task.FromResult(initialYes), output, default);
        Assert.Equal(2, code);
        Assert.False(invoked);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("submissionStateUnknown").GetBoolean());
    }

    [Fact]
    public async Task Command_InvokedRunnerExceptionReportsUnknownWithoutInventingResult()
    {
        using var output = new StringWriter();
        await CoolingPadCounterValidationCommand.RunAsync(Arguments(true), (_, _) => throw new InvalidOperationException(),
            (_, _) => Task.FromResult(true), output, default);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.True(json.RootElement.GetProperty("submissionStateUnknown").GetBoolean());
        Assert.False(json.RootElement.GetProperty("productionAdmission").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("result").ValueKind);
    }

    private static string[] Arguments(bool valid) => ["--confirm-target", valid ? CoolingPadCounterValidationCommand.Target : "wrong",
        "--confirm-recovery", "ManualPowerCycleOnly"];
    private static CoolingPadSessionFactory Factory(Session fake) => new(() => new Actors(fake), TimeProvider.System, developerCounter: true);
    private static CoolingPadCounterValidationRunner Runner(CoolingPadSessionFactory factory) =>
        CoolingPadCounterValidationRunner.ForTesting(factory);

    private sealed class Actors(Session fake) : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context) =>
            ValueTask.FromResult(new HidSessionResult<ValidationActor>(new(fake, new BatteryCommandClient(fake, TimeSpan.FromSeconds(3))), null));
    }
    private sealed class Session : IRazerCommandSession
    {
        public List<string> Commands { get; } = [];
        public int FailAt { get; init; }
        public string? DisposalBehavior { get; init; }
        public TaskCompletionSource DisposalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseDisposal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposalCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        private byte raw;
        public RazerCommandSessionDisposition Disposition => Disposed ? RazerCommandSessionDisposition.FullyDisposed : RazerCommandSessionDisposition.Healthy;
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assign, TimeSpan timeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var assignment = assign();
            Assert.True(assignment.CoolingPadDeveloperCounter);
            Assert.InRange(assignment.TransactionId, (byte)0, (byte)30);
            if (command.SubmissionAllowed?.Invoke() == false)
            { return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.ConflictBlocked)); }
            Commands.Add($"{command.ClassId:X2}/{command.CommandId:X2}:{Convert.ToHexString(command.Arguments.Span)}");
            if (Commands.Count == FailAt)
            { return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.ProtocolFailure)); }
            if (command.ClassId == 0 && command.CommandId == 4) { raw = command.Arguments.Span[0]; }
            ReadOnlyMemory<byte> payload = command.CommandId == 0x84 ? new byte[] { raw, 0 } : command.Arguments;
            Assert.True(command.PayloadValidator?.Invoke(payload));
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(payload, null));
        }
        public async ValueTask DisposeAsync()
        {
            DisposalStarted.TrySetResult();
            if (DisposalBehavior == "fault") { throw new InvalidOperationException("DisposalFailed"); }
            if (DisposalBehavior == "incomplete") { return; }
            if (DisposalBehavior is "late" or "transient") { await ReleaseDisposal.Task; }
            Disposed = true;
            DisposalCompleted.TrySetResult();
        }
    }
}
