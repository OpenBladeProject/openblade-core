using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class TemporaryFullChargeValidationSessionTests
{
    [Fact]
    public async Task RunAsync_RequiresExactBaselineAndCompletesEightStageValidation()
    {
        var port = HappyPort();
        var approval = new Approval(true);
        var session = CreateSession(port, approval);

        TemporaryFullChargeValidationResult result = await session.RunAsync(default);

        Assert.True(result.Success);
        Assert.Equal(BatteryValidationSetting.Limit80, approval.BaselineProtection);
        Assert.False(approval.BaselineTemporaryFullCharge);
        Assert.Equal(
            ["query-protection", "query-temporary", "enable", "query-protection",
             "query-temporary", "cancel", "query-protection", "query-temporary", "dispose"],
            port.Events);
    }

    [Theory]
    [InlineData(BatteryValidationSetting.Limit75, false,
        TemporaryFullChargeValidationPrimaryFailure.BaselineNotLimit80)]
    [InlineData(BatteryValidationSetting.Limit80, true,
        TemporaryFullChargeValidationPrimaryFailure.BaselineTemporaryActive)]
    public async Task RunAsync_UnsafeBaselinePerformsNoSetter(
        BatteryValidationSetting baseline,
        bool temporaryActive,
        TemporaryFullChargeValidationPrimaryFailure expected)
    {
        var port = HappyPort();
        port.Protection.Clear();
        port.Protection.Enqueue(new(baseline, null));
        port.Temporary.Clear();
        port.Temporary.Enqueue(new(temporaryActive, null));

        TemporaryFullChargeValidationResult result = await CreateSession(port).RunAsync(default);

        Assert.Equal(expected, result.PrimaryFailure);
        Assert.Equal(0, port.EnableCount);
        Assert.Equal(0, port.CancelCount);
    }

    [Fact]
    public async Task RunAsync_ApprovalDeclinedPerformsNoSetter()
    {
        var port = HappyPort();

        TemporaryFullChargeValidationResult result = await CreateSession(
            port,
            new Approval(false)).RunAsync(default);

        Assert.Equal(
            TemporaryFullChargeValidationPrimaryFailure.ApprovalDeclined,
            result.PrimaryFailure);
        Assert.Equal(0, port.EnableCount);
        Assert.Equal(0, port.CancelCount);
    }

    [Theory]
    [InlineData(true,
        TemporaryFullChargeValidationPrimaryFailure.BaselineProtectionQueryFailed)]
    [InlineData(false,
        TemporaryFullChargeValidationPrimaryFailure.BaselineTemporaryQueryFailed)]
    public async Task RunAsync_BaselineQueryFailurePerformsNoSetter(
        bool protectionFails,
        TemporaryFullChargeValidationPrimaryFailure expected)
    {
        var port = HappyPort();
        if (protectionFails)
        {
            port.Protection.Clear();
            port.Protection.Enqueue(new(default, HidSessionFailureKind.NativeIoFailure));
        }
        else
        {
            port.Temporary.Clear();
            port.Temporary.Enqueue(new(default, HidSessionFailureKind.ProtocolFailure));
        }

        TemporaryFullChargeValidationResult result = await CreateSession(port).RunAsync(default);

        Assert.Equal(expected, result.PrimaryFailure);
        Assert.Equal(0, port.EnableCount);
        Assert.Equal(0, port.CancelCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_EnableFailureOrThrow_StillCancelsExactlyOnce(bool throws)
    {
        var port = EnableFailurePort();
        port.EnableSteps.Enqueue(throws
            ? _ => Task.FromException<HidSessionResult<HidUnit>>(
                new IOException("enable failed"))
            : _ => Task.FromResult(new HidSessionResult<HidUnit>(
                default,
                HidSessionFailureKind.NativeIoFailure)));

        TemporaryFullChargeValidationResult result = await CreateSession(port).RunAsync(default);

        Assert.Equal(TemporaryFullChargeValidationPrimaryFailure.EnableFailed, result.PrimaryFailure);
        Assert.True(result.Restored);
        Assert.Equal(1, port.EnableCount);
        Assert.Equal(1, port.CancelCount);
        Assert.Equal(
            ["query-protection", "query-temporary", "enable", "cancel",
             "query-protection", "query-temporary", "dispose"],
            port.Events);
    }

    [Fact]
    public async Task RunAsync_CallerCancellationDuringEnable_CancelsBeforeRethrowing()
    {
        using var caller = new CancellationTokenSource();
        var port = EnableFailurePort();
        var original = new OperationCanceledException("enable canceled", caller.Token);
        port.EnableSteps.Enqueue(token =>
        {
            Assert.Equal(caller.Token, token);
            caller.Cancel();
            return Task.FromException<HidSessionResult<HidUnit>>(original);
        });

        OperationCanceledException cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateSession(port).RunAsync(caller.Token));

        Assert.Same(original, cancellation);
        Assert.Equal(1, port.CancelCount);
        Assert.True(port.CancelTokens.Single().CanBeCanceled);
        Assert.False(port.CancelTokens.Single().IsCancellationRequested);
        Assert.Equal("dispose", port.Events[^1]);
    }

    [Fact]
    public async Task RunAsync_CallerCancellationDuringCandidateQuery_RestoresBeforeRethrowing()
    {
        using var caller = new CancellationTokenSource();
        var port = HappyPort();
        port.Protection.Clear();
        port.Protection.Enqueue(new(BatteryValidationSetting.Limit80, null));
        port.Protection.Enqueue(new(default, HidSessionFailureKind.Canceled));
        port.Protection.Enqueue(new(BatteryValidationSetting.Limit80, null));
        port.Temporary.Clear();
        port.Temporary.Enqueue(new(false, null));
        port.Temporary.Enqueue(new(false, null));
        port.OnProtectionQuery = (count, _) =>
        {
            if (count == 2)
            {
                caller.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateSession(port).RunAsync(caller.Token));

        Assert.Equal(1, port.CancelCount);
        Assert.False(port.CancelTokens.Single().IsCancellationRequested);
        Assert.Equal(
            ["query-protection", "query-temporary", "enable", "query-protection",
             "query-temporary", "cancel", "query-protection", "query-temporary", "dispose"],
            port.Events);
    }

    [Theory]
    [InlineData(BatteryValidationSetting.Limit75, true,
        TemporaryFullChargeValidationPrimaryFailure.CandidateProtectionMismatch)]
    [InlineData(BatteryValidationSetting.Limit80, false,
        TemporaryFullChargeValidationPrimaryFailure.CandidateTemporaryInactive)]
    public async Task RunAsync_CandidateMismatch_StillCancelsAndRestores(
        BatteryValidationSetting candidateProtection,
        bool candidateTemporary,
        TemporaryFullChargeValidationPrimaryFailure expected)
    {
        var port = HappyPort();
        port.Protection.Clear();
        port.Protection.Enqueue(new(BatteryValidationSetting.Limit80, null));
        port.Protection.Enqueue(new(candidateProtection, null));
        port.Protection.Enqueue(new(BatteryValidationSetting.Limit80, null));
        port.Temporary.Clear();
        port.Temporary.Enqueue(new(false, null));
        port.Temporary.Enqueue(new(candidateTemporary, null));
        port.Temporary.Enqueue(new(false, null));

        TemporaryFullChargeValidationResult result = await CreateSession(port).RunAsync(default);

        Assert.Equal(expected, result.PrimaryFailure);
        Assert.True(result.Restored);
        Assert.Equal(1, port.CancelCount);
    }

    [Fact]
    public async Task RunAsync_CancelFailure_IsAttemptedOnceWithoutRestorationReads()
    {
        var port = HappyPort();
        port.CancelSteps.Enqueue(_ => Task.FromResult(new HidSessionResult<HidUnit>(
            default,
            HidSessionFailureKind.NativeIoFailure)));

        TemporaryFullChargeValidationResult result = await CreateSession(port).RunAsync(default);

        Assert.Equal(
            TemporaryFullChargeValidationRestorationFailure.CancelFailed,
            result.RestorationFailure);
        Assert.Equal(1, port.CancelCount);
        Assert.Equal(
            ["query-protection", "query-temporary", "enable", "query-protection",
             "query-temporary", "cancel", "dispose"],
            port.Events);
    }

    [Fact]
    public async Task RunAsync_RestorationProtectionFailure_StillReadsTemporaryWithoutExtraCancel()
    {
        var port = HappyPort();
        port.Protection.Clear();
        port.Protection.Enqueue(new(BatteryValidationSetting.Limit80, null));
        port.Protection.Enqueue(new(BatteryValidationSetting.Limit80, null));
        port.Protection.Enqueue(new(default, HidSessionFailureKind.ProtocolFailure));

        TemporaryFullChargeValidationResult result = await CreateSession(port).RunAsync(default);

        Assert.Equal(
            TemporaryFullChargeValidationRestorationFailure.RestorationProtectionQueryFailed,
            result.RestorationFailure);
        Assert.Equal(1, port.CancelCount);
        Assert.Equal(3, port.TemporaryQueryCount);
        Assert.Equal("query-temporary", port.Events[^2]);
        Assert.Equal("dispose", port.Events[^1]);
    }

    [Fact]
    public async Task RunAsync_RestorationTemporaryActive_IsNotRestored()
    {
        var port = HappyPort();
        port.Temporary.Clear();
        port.Temporary.Enqueue(new(false, null));
        port.Temporary.Enqueue(new(true, null));
        port.Temporary.Enqueue(new(true, null));

        TemporaryFullChargeValidationResult result = await CreateSession(port).RunAsync(default);

        Assert.Equal(
            TemporaryFullChargeValidationRestorationFailure.RestorationTemporaryActive,
            result.RestorationFailure);
        Assert.False(result.Restored);
        Assert.Equal(1, port.CancelCount);
    }

    private static TemporaryFullChargeValidationSession CreateSession(
        Port port,
        ITemporaryFullChargeApproval? approval = null) => new(
        new Factory(new(port, null)),
        approval ?? new Approval(true),
        TimeSpan.FromSeconds(1));

    private static Port HappyPort()
    {
        var port = new Port();
        port.Protection.Enqueue(new(BatteryValidationSetting.Limit80, null));
        port.Protection.Enqueue(new(BatteryValidationSetting.Limit80, null));
        port.Protection.Enqueue(new(BatteryValidationSetting.Limit80, null));
        port.Temporary.Enqueue(new(false, null));
        port.Temporary.Enqueue(new(true, null));
        port.Temporary.Enqueue(new(false, null));
        return port;
    }

    private static Port EnableFailurePort()
    {
        var port = new Port();
        port.Protection.Enqueue(new(BatteryValidationSetting.Limit80, null));
        port.Protection.Enqueue(new(BatteryValidationSetting.Limit80, null));
        port.Temporary.Enqueue(new(false, null));
        port.Temporary.Enqueue(new(false, null));
        return port;
    }

    private sealed class Factory(HidSessionResult<ITemporaryFullChargeValidationPort> result)
        : ITemporaryFullChargeValidationRunFactory
    {
        public Task<HidSessionResult<ITemporaryFullChargeValidationPort>> OpenAsync(
            CancellationToken token) => Task.FromResult(result);
    }

    private sealed class Approval(bool approved) : ITemporaryFullChargeApproval
    {
        internal BatteryValidationSetting? BaselineProtection { get; private set; }
        internal bool? BaselineTemporaryFullCharge { get; private set; }

        public Task<bool> ConfirmAsync(
            BatteryValidationSetting baselineProtection,
            bool baselineTemporaryFullCharge,
            CancellationToken token)
        {
            BaselineProtection = baselineProtection;
            BaselineTemporaryFullCharge = baselineTemporaryFullCharge;
            return Task.FromResult(approved);
        }
    }

    private sealed class Port : ITemporaryFullChargeValidationPort
    {
        internal Queue<HidSessionResult<BatteryValidationSetting>> Protection { get; } = [];
        internal Queue<HidSessionResult<bool>> Temporary { get; } = [];
        internal Queue<Func<CancellationToken, Task<HidSessionResult<HidUnit>>>> EnableSteps { get; } = [];
        internal Queue<Func<CancellationToken, Task<HidSessionResult<HidUnit>>>> CancelSteps { get; } = [];
        internal List<string> Events { get; } = [];
        internal List<CancellationToken> CancelTokens { get; } = [];
        internal int EnableCount { get; private set; }
        internal int CancelCount { get; private set; }
        internal int TemporaryQueryCount { get; private set; }
        internal int ProtectionQueryCount { get; private set; }
        internal Action<int, CancellationToken>? OnProtectionQuery { get; set; }

        public Task<HidSessionResult<BatteryValidationSetting>> QueryProtectionAsync(
            CancellationToken token)
        {
            Events.Add("query-protection");
            ProtectionQueryCount++;
            OnProtectionQuery?.Invoke(ProtectionQueryCount, token);
            return Task.FromResult(Protection.Dequeue());
        }

        public Task<HidSessionResult<bool>> QueryTemporaryFullChargeAsync(
            CancellationToken token)
        {
            Events.Add("query-temporary");
            TemporaryQueryCount++;
            if (token.IsCancellationRequested)
            {
                return Task.FromResult(new HidSessionResult<bool>(
                    default,
                    HidSessionFailureKind.Canceled));
            }

            return Task.FromResult(Temporary.Dequeue());
        }

        public Task<HidSessionResult<HidUnit>> ApplyTemporaryFullChargeAsync(
            CancellationToken token)
        {
            Events.Add("enable");
            EnableCount++;
            return EnableSteps.Count == 0
                ? Task.FromResult(new HidSessionResult<HidUnit>(new HidUnit(), null))
                : EnableSteps.Dequeue()(token);
        }

        public Task<HidSessionResult<HidUnit>> CancelTemporaryFullChargeAsync(
            CancellationToken token)
        {
            Events.Add("cancel");
            CancelCount++;
            CancelTokens.Add(token);
            return CancelSteps.Count == 0
                ? Task.FromResult(new HidSessionResult<HidUnit>(new HidUnit(), null))
                : CancelSteps.Dequeue()(token);
        }

        public ValueTask DisposeAsync()
        {
            Events.Add("dispose");
            return ValueTask.CompletedTask;
        }
    }
}
