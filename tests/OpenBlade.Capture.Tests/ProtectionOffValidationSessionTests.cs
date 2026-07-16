using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class ProtectionOffValidationSessionTests
{
    [Fact]
    public async Task RunAsync_RequiresLimit80TemporaryFalseAndExactEightStageSuccess()
    {
        var port = HappyPort();
        var approval = new Approval(true);
        var session = new ProtectionOffValidationSession(
            new Factory(new(port, null)), approval, TimeSpan.FromSeconds(1));

        ProtectionOffValidationResult result = await session.RunAsync(default);

        Assert.True(result.Success);
        Assert.Equal(BatteryValidationSetting.Limit80, approval.Prior);
        Assert.Equal(BatteryValidationSetting.ProtectionOff, approval.Candidate);
        Assert.Equal(
            ["query-protection", "query-temporary", "apply-off", "query-protection",
             "query-temporary", "restore-80", "query-protection", "query-temporary", "dispose"],
            port.Events);
    }

    [Theory]
    [InlineData(BatteryValidationSetting.Limit75, false)]
    [InlineData(BatteryValidationSetting.Limit80, true)]
    public async Task RunAsync_RejectsUnsafeBaselineWithoutAnySetter(
        BatteryValidationSetting baseline,
        bool temporaryActive)
    {
        var port = HappyPort();
        port.Protection.Clear();
        port.Protection.Enqueue(new(baseline, null));
        port.Temporary.Clear();
        port.Temporary.Enqueue(new(temporaryActive, null));
        var session = new ProtectionOffValidationSession(
            new Factory(new(port, null)), new Approval(true), TimeSpan.FromSeconds(1));

        ProtectionOffValidationResult result = await session.RunAsync(default);

        Assert.False(result.Success);
        Assert.DoesNotContain("apply-off", port.Events);
        Assert.DoesNotContain("restore-80", port.Events);
    }

    [Fact]
    public async Task RunAsync_CandidateSemanticFailureStillRestoresSavedLimit80AndVerifiesBothSemantics()
    {
        var port = HappyPort();
        port.Temporary.Clear();
        port.Temporary.Enqueue(new(false, null));
        port.Temporary.Enqueue(new(true, null));
        port.Temporary.Enqueue(new(false, null));
        var session = new ProtectionOffValidationSession(
            new Factory(new(port, null)), new Approval(true), TimeSpan.FromSeconds(1));

        ProtectionOffValidationResult result = await session.RunAsync(default);

        Assert.Equal(ProtectionOffValidationPrimaryFailure.CandidateTemporaryActive, result.PrimaryFailure);
        Assert.True(result.Restored);
        Assert.Equal(ProtectionOffValidationRestorationFailure.None, result.RestorationFailure);
        Assert.Equal(1, port.ApplyOffCount);
        Assert.Equal(1, port.RestoreCount);
        Assert.Equal("dispose", port.Events[^1]);
    }

    [Fact]
    public async Task RunAsync_CandidateTransportFailureStillAttemptsOnlyFixedRestoration()
    {
        var port = HappyPort();
        port.Protection.Clear();
        port.Protection.Enqueue(new(BatteryValidationSetting.Limit80, null));
        port.Protection.Enqueue(new(default, HidSessionFailureKind.NativeIoFailure));
        port.Protection.Enqueue(new(BatteryValidationSetting.Limit80, null));
        var session = new ProtectionOffValidationSession(
            new Factory(new(port, null)), new Approval(true), TimeSpan.FromSeconds(1));

        ProtectionOffValidationResult result = await session.RunAsync(default);

        Assert.Equal(ProtectionOffValidationPrimaryFailure.CandidateProtectionQueryFailed, result.PrimaryFailure);
        Assert.Equal(1, port.ApplyOffCount);
        Assert.Equal(1, port.RestoreCount);
        Assert.True(result.Restored);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_CandidateSetterFailureOrThrow_RestoresExactlyOnce(bool throws)
    {
        var port = SetterFailurePort();
        port.ApplySteps.Enqueue(throws
            ? _ => Task.FromException<HidSessionResult<HidUnit>>(
                new IOException("candidate setter failed"))
            : _ => Task.FromResult(new HidSessionResult<HidUnit>(
                default,
                HidSessionFailureKind.NativeIoFailure)));
        var session = new ProtectionOffValidationSession(
            new Factory(new(port, null)), new Approval(true), TimeSpan.FromSeconds(1));

        ProtectionOffValidationResult result = await session.RunAsync(default);

        Assert.Equal(ProtectionOffValidationPrimaryFailure.ApplyFailed, result.PrimaryFailure);
        Assert.False(result.Applied);
        Assert.True(result.Restored);
        Assert.Equal(1, port.ApplyOffCount);
        Assert.Equal(1, port.RestoreCount);
        Assert.Equal(
            ["query-protection", "query-temporary", "apply-off", "restore-80",
             "query-protection", "query-temporary", "dispose"],
            port.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_CandidateSetterCancellation_RestoresOnceThenRethrowsOriginal(
        bool throws)
    {
        using var caller = new CancellationTokenSource();
        var port = SetterFailurePort();
        var original = new OperationCanceledException("candidate canceled", caller.Token);
        port.ApplySteps.Enqueue(token =>
        {
            Assert.Equal(caller.Token, token);
            caller.Cancel();
            return throws
                ? Task.FromException<HidSessionResult<HidUnit>>(original)
                : Task.FromResult(new HidSessionResult<HidUnit>(
                    default,
                    HidSessionFailureKind.Canceled));
        });
        var session = new ProtectionOffValidationSession(
            new Factory(new(port, null)), new Approval(true), TimeSpan.FromSeconds(1));

        OperationCanceledException cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => session.RunAsync(caller.Token));

        if (throws)
        {
            Assert.Same(original, cancellation);
        }
        Assert.Equal(1, port.ApplyOffCount);
        Assert.Equal(1, port.RestoreCount);
        Assert.True(port.RestoreTokens.Single().CanBeCanceled);
        Assert.False(port.RestoreTokens.Single().IsCancellationRequested);
        Assert.Equal(
            ["query-protection", "query-temporary", "apply-off", "restore-80",
             "query-protection", "query-temporary", "dispose"],
            port.Events);
    }

    [Fact]
    public async Task RunAsync_CandidateTemporaryReadFailure_StillRestoresExactlyOnce()
    {
        var port = HappyPort();
        port.Temporary.Clear();
        port.Temporary.Enqueue(new(false, null));
        port.Temporary.Enqueue(new(default, HidSessionFailureKind.ProtocolFailure));
        port.Temporary.Enqueue(new(false, null));
        var session = new ProtectionOffValidationSession(
            new Factory(new(port, null)), new Approval(true), TimeSpan.FromSeconds(1));

        ProtectionOffValidationResult result = await session.RunAsync(default);

        Assert.Equal(
            ProtectionOffValidationPrimaryFailure.CandidateTemporaryQueryFailed,
            result.PrimaryFailure);
        Assert.True(result.Restored);
        Assert.Equal(1, port.ApplyOffCount);
        Assert.Equal(1, port.RestoreCount);
        Assert.Equal("dispose", port.Events[^1]);
    }

    [Fact]
    public async Task RunAsync_RestoreSetterFailure_IsAttemptedOnceWithoutReadback()
    {
        var port = HappyPort();
        port.RestoreSteps.Enqueue(_ => Task.FromResult(new HidSessionResult<HidUnit>(
            default,
            HidSessionFailureKind.NativeIoFailure)));
        var session = new ProtectionOffValidationSession(
            new Factory(new(port, null)), new Approval(true), TimeSpan.FromSeconds(1));

        ProtectionOffValidationResult result = await session.RunAsync(default);

        Assert.Equal(
            ProtectionOffValidationRestorationFailure.RestoreApplyFailed,
            result.RestorationFailure);
        Assert.Equal(1, port.RestoreCount);
        Assert.Equal(
            ["query-protection", "query-temporary", "apply-off", "query-protection",
             "query-temporary", "restore-80", "dispose"],
            port.Events);
    }

    [Fact]
    public async Task RunAsync_RestoreProtectionReadFailure_StillReadsTemporaryWithoutExtraRestore()
    {
        var port = HappyPort();
        port.Protection.Clear();
        port.Protection.Enqueue(new(BatteryValidationSetting.Limit80, null));
        port.Protection.Enqueue(new(BatteryValidationSetting.ProtectionOff, null));
        port.Protection.Enqueue(new(default, HidSessionFailureKind.ProtocolFailure));
        var session = new ProtectionOffValidationSession(
            new Factory(new(port, null)), new Approval(true), TimeSpan.FromSeconds(1));

        ProtectionOffValidationResult result = await session.RunAsync(default);

        Assert.Equal(
            ProtectionOffValidationRestorationFailure.RestoreProtectionQueryFailed,
            result.RestorationFailure);
        Assert.Equal(1, port.RestoreCount);
        Assert.Equal(3, port.TemporaryQueryCount);
        Assert.Equal("query-temporary", port.Events[^2]);
        Assert.Equal("dispose", port.Events[^1]);
    }

    [Fact]
    public async Task RunAsync_RestoreTemporaryReadFailure_IsClassifiedWithoutExtraRestore()
    {
        var port = HappyPort();
        port.Temporary.Clear();
        port.Temporary.Enqueue(new(false, null));
        port.Temporary.Enqueue(new(false, null));
        port.Temporary.Enqueue(new(default, HidSessionFailureKind.ProtocolFailure));
        var session = new ProtectionOffValidationSession(
            new Factory(new(port, null)), new Approval(true), TimeSpan.FromSeconds(1));

        ProtectionOffValidationResult result = await session.RunAsync(default);

        Assert.Equal(
            ProtectionOffValidationRestorationFailure.RestoreTemporaryQueryFailed,
            result.RestorationFailure);
        Assert.Equal(1, port.RestoreCount);
        Assert.Equal("query-temporary", port.Events[^2]);
        Assert.Equal("dispose", port.Events[^1]);
    }

    private static Port HappyPort()
    {
        var port = new Port();
        port.Protection.Enqueue(new(BatteryValidationSetting.Limit80, null));
        port.Protection.Enqueue(new(BatteryValidationSetting.ProtectionOff, null));
        port.Protection.Enqueue(new(BatteryValidationSetting.Limit80, null));
        port.Temporary.Enqueue(new(false, null));
        port.Temporary.Enqueue(new(false, null));
        port.Temporary.Enqueue(new(false, null));
        return port;
    }

    private static Port SetterFailurePort()
    {
        var port = new Port();
        port.Protection.Enqueue(new(BatteryValidationSetting.Limit80, null));
        port.Protection.Enqueue(new(BatteryValidationSetting.Limit80, null));
        port.Temporary.Enqueue(new(false, null));
        port.Temporary.Enqueue(new(false, null));
        return port;
    }

    private sealed class Factory(HidSessionResult<IProtectionOffValidationPort> result)
        : IProtectionOffValidationRunFactory
    {
        public Task<HidSessionResult<IProtectionOffValidationPort>> OpenAsync(CancellationToken token) => Task.FromResult(result);
    }

    private sealed class Approval(bool approved) : IInteractiveApproval
    {
        internal BatteryValidationSetting? Prior { get; private set; }
        internal BatteryValidationSetting? Candidate { get; private set; }
        public Task<bool> ConfirmAsync(BatteryValidationSetting prior, BatteryValidationSetting candidate, CancellationToken token)
        {
            Prior = prior;
            Candidate = candidate;
            return Task.FromResult(approved);
        }
    }

    private sealed class Port : IProtectionOffValidationPort
    {
        internal Queue<HidSessionResult<BatteryValidationSetting>> Protection { get; } = [];
        internal Queue<HidSessionResult<bool>> Temporary { get; } = [];
        internal Queue<Func<CancellationToken, Task<HidSessionResult<HidUnit>>>> ApplySteps { get; } = [];
        internal Queue<Func<CancellationToken, Task<HidSessionResult<HidUnit>>>> RestoreSteps { get; } = [];
        internal List<string> Events { get; } = [];
        internal List<CancellationToken> RestoreTokens { get; } = [];
        internal int ApplyOffCount { get; private set; }
        internal int RestoreCount { get; private set; }
        internal int TemporaryQueryCount { get; private set; }
        public Task<HidSessionResult<BatteryValidationSetting>> QueryProtectionAsync(CancellationToken token)
        {
            Events.Add("query-protection");
            return Task.FromResult(Protection.Dequeue());
        }
        public Task<HidSessionResult<bool>> QueryTemporaryFullChargeAsync(CancellationToken token)
        {
            Events.Add("query-temporary");
            TemporaryQueryCount++;
            return Task.FromResult(Temporary.Dequeue());
        }
        public Task<HidSessionResult<HidUnit>> ApplyProtectionOffAsync(CancellationToken token)
        {
            Events.Add("apply-off");
            ApplyOffCount++;
            if (ApplySteps.Count != 0)
            {
                return ApplySteps.Dequeue()(token);
            }
            return Task.FromResult(new HidSessionResult<HidUnit>(new HidUnit(), null));
        }
        public Task<HidSessionResult<HidUnit>> RestoreLimit80Async(CancellationToken token)
        {
            Events.Add("restore-80");
            RestoreCount++;
            RestoreTokens.Add(token);
            if (RestoreSteps.Count != 0)
            {
                return RestoreSteps.Dequeue()(token);
            }
            return Task.FromResult(new HidSessionResult<HidUnit>(new HidUnit(), null));
        }
        public ValueTask DisposeAsync() { Events.Add("dispose"); return ValueTask.CompletedTask; }
    }
}
