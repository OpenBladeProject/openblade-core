using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class BatteryValidationSessionTests
{
    [Fact]
    public void Constructor_AcceptsConfigurableSettlingDelayAndDelayFunction()
    {
        TestContext context = new();
        Func<TimeSpan, CancellationToken, Task> delay = (_, _) => Task.CompletedTask;

        var session = new BatteryValidationSession(
            context.Factory,
            context.Approval,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(250),
            delay);

        Assert.NotNull(session);
    }

    [Fact]
    public async Task Run_UndefinedCandidateAbortsBeforeFactoryOpen()
    {
        TestContext context = new();

        BatteryValidationResult result = await context.RunAsync((BatteryValidationSetting)999);

        Assert.Equal(BatteryValidationPrimaryFailure.UndefinedCandidate, result.PrimaryFailure);
        Assert.Empty(context.Events);
        Assert.False(result.ApplyAttempted);
    }

    [Theory]
    [InlineData(HidSessionFailureKind.IdentityBlocked, BatteryValidationPrimaryFailure.WrongTarget)]
    [InlineData(HidSessionFailureKind.ConflictBlocked, BatteryValidationPrimaryFailure.Conflict)]
    [InlineData(HidSessionFailureKind.ConflictDetectionFailed, BatteryValidationPrimaryFailure.ConflictDetectionFailed)]
    [InlineData(HidSessionFailureKind.Poisoned, BatteryValidationPrimaryFailure.OpenFailed)]
    [InlineData(HidSessionFailureKind.Exhausted, BatteryValidationPrimaryFailure.OpenFailed)]
    [InlineData(HidSessionFailureKind.Disposed, BatteryValidationPrimaryFailure.OpenFailed)]
    [InlineData(HidSessionFailureKind.Timeout, BatteryValidationPrimaryFailure.OpenFailed)]
    [InlineData(HidSessionFailureKind.NativeIoFailure, BatteryValidationPrimaryFailure.OpenFailed)]
    [InlineData(HidSessionFailureKind.ProtocolFailure, BatteryValidationPrimaryFailure.OpenFailed)]
    [InlineData(HidSessionFailureKind.Canceled, BatteryValidationPrimaryFailure.OpenFailed)]
    [InlineData(HidSessionFailureKind.QueuedCanceled, BatteryValidationPrimaryFailure.OpenFailed)]
    public async Task Run_MapsFactoryFailureExactly(
        HidSessionFailureKind failure,
        BatteryValidationPrimaryFailure expected)
    {
        TestContext context = new();
        context.Factory.Result = Failure<IBatteryValidationPort>(failure);

        BatteryValidationResult result = await context.RunAsync();

        Assert.Equal(expected, result.PrimaryFailure);
        Assert.Equal(["open"], context.Events);
        Assert.False(context.Port.Disposed);
    }

    [Fact]
    public async Task Run_NullSuccessFromFactoryMapsToOpenFailed()
    {
        TestContext context = new();
        context.Factory.Result = new(null, null);

        BatteryValidationResult result = await context.RunAsync();

        Assert.Equal(BatteryValidationPrimaryFailure.OpenFailed, result.PrimaryFailure);
    }

    [Fact]
    public async Task Run_MixedFactoryFailureAndPortDisposesBeforeExactMapping()
    {
        TestContext context = new();
        context.Factory.Result = new(context.Port, HidSessionFailureKind.IdentityBlocked);

        BatteryValidationResult result = await context.RunAsync();

        Assert.Equal(BatteryValidationPrimaryFailure.WrongTarget, result.PrimaryFailure);
        Assert.Equal(["open", "dispose"], context.Events);
        Assert.Equal(1, context.Port.DisposeCount);
    }

    [Fact]
    public async Task Run_UnexpectedFactoryExceptionMapsToStableOpenFailed()
    {
        TestContext context = new();
        context.Factory.Exception = new IOException("unstable factory detail");

        BatteryValidationResult result = await context.RunAsync();

        Assert.Equal(BatteryValidationPrimaryFailure.OpenFailed, result.PrimaryFailure);
        Assert.DoesNotContain("unstable factory detail", result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HidSessionFailureKind.NativeIoFailure)]
    [InlineData(HidSessionFailureKind.Canceled)]
    [InlineData(HidSessionFailureKind.QueuedCanceled)]
    public async Task Run_BaselineStableFailureMapsToBaselineQueryFailedAndDisposes(
        HidSessionFailureKind failure)
    {
        TestContext context = new();
        context.Port.QuerySteps.Clear();
        context.Port.QuerySteps.Enqueue(_ => Task.FromResult(Failure<BatteryValidationSetting>(failure)));

        BatteryValidationResult result = await context.RunAsync();

        Assert.Equal(BatteryValidationPrimaryFailure.BaselineQueryFailed, result.PrimaryFailure);
        Assert.Equal(["open", "query", "dispose"], context.Events);
        Assert.True(context.Port.Disposed);
    }

    [Fact]
    public async Task Run_BaselineExceptionMapsToStableFailureAndDisposes()
    {
        TestContext context = new();
        context.Port.QuerySteps.Clear();
        context.Port.QuerySteps.Enqueue(_ => Task.FromException<HidSessionResult<BatteryValidationSetting>>(
            new IOException("unstable query detail")));

        BatteryValidationResult result = await context.RunAsync();

        Assert.Equal(BatteryValidationPrimaryFailure.BaselineQueryFailed, result.PrimaryFailure);
        Assert.True(context.Port.Disposed);
        Assert.DoesNotContain("unstable query detail", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_BaselineFailurePreservesSafeDiagnostic()
    {
        TestContext context = new();
        var diagnostic = new HidSessionFailureDiagnostic(
            HidSessionFailurePhase.GetCompletion,
            HidSessionFailureKind.NativeIoFailure,
            NativeWin32Error: 31,
            TransferredByteCount: 12);
        context.Port.QuerySteps.Clear();
        context.Port.QuerySteps.Enqueue(_ => Task.FromResult(
            Failure<BatteryValidationSetting>(HidSessionFailureKind.NativeIoFailure) with
            {
                Diagnostic = diagnostic,
            }));

        BatteryValidationResult result = await context.RunAsync();

        Assert.Equal(BatteryValidationPrimaryFailure.BaselineQueryFailed, result.PrimaryFailure);
        Assert.Equal(diagnostic, result.BaselineQueryDiagnostic);
        Assert.False(result.ApplyAttempted);
        Assert.Equal(["open", "query", "dispose"], context.Events);
    }

    [Fact]
    public async Task Run_UndefinedBaselineAbortsAndDisposes()
    {
        TestContext context = new();
        context.Port.QuerySteps.Clear();
        context.Port.QuerySteps.Enqueue(_ => Task.FromResult(Success((BatteryValidationSetting)999)));

        BatteryValidationResult result = await context.RunAsync();

        Assert.Equal(BatteryValidationPrimaryFailure.UndefinedPrior, result.PrimaryFailure);
        Assert.Equal(["open", "query", "dispose"], context.Events);
    }

    [Fact]
    public async Task Run_SameCandidateAbortsAndDisposes()
    {
        TestContext context = new();

        BatteryValidationResult result = await context.RunAsync(BatteryValidationSetting.Limit80);

        Assert.Equal(BatteryValidationPrimaryFailure.SameCandidate, result.PrimaryFailure);
        Assert.Equal(["open", "query", "dispose"], context.Events);
    }

    [Fact]
    public async Task Run_ApprovalDeclineUsesNoApplyAndDisposes()
    {
        TestContext context = new() { ApprovalResult = false };

        BatteryValidationResult result = await context.RunAsync();

        Assert.Equal(BatteryValidationPrimaryFailure.ApprovalDeclined, result.PrimaryFailure);
        Assert.Equal(["open", "query", "approve", "dispose"], context.Events);
        Assert.Empty(context.Port.ApplyTokens);
        Assert.False(result.ApplyAttempted);
    }

    [Theory]
    [InlineData("open", true)]
    [InlineData("baseline", true)]
    [InlineData("approval", true)]
    [InlineData("approval", false)]
    public async Task Run_CallerCancellationAfterSuccessfulPreMutationAwaitRethrowsWithoutApply(
        string stage,
        bool approvalResult)
    {
        using CancellationTokenSource caller = new();
        TestContext context = new() { ApprovalResult = approvalResult };
        if (stage == "open")
        {
            context.Factory.Open = _ =>
            {
                caller.Cancel();
                return Task.FromResult(Success<IBatteryValidationPort>(context.Port));
            };
        }
        else if (stage == "baseline")
        {
            context.Port.QuerySteps.Clear();
            context.Port.QuerySteps.Enqueue(_ =>
            {
                caller.Cancel();
                return Task.FromResult(Success(BatteryValidationSetting.Limit80));
            });
        }
        else
        {
            context.Approval.OnConfirm = () => caller.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.RunAsync(cancellationToken: caller.Token));

        Assert.Empty(context.Port.ApplyTokens);
        Assert.True(context.Port.Disposed);
        Assert.Equal("dispose", context.Events[^1]);
    }

    [Fact]
    public async Task Run_ApprovalReceivesExactBaselineCandidateAndCallerToken()
    {
        using CancellationTokenSource caller = new();
        TestContext context = new();

        await context.RunAsync(cancellationToken: caller.Token);

        Assert.Equal(BatteryValidationSetting.Limit80, context.Approval.Prior);
        Assert.Equal(BatteryValidationSetting.Limit75, context.Approval.Candidate);
        Assert.Equal(caller.Token, context.Approval.Token);
    }

    [Fact]
    public async Task Run_HappyPathUsesExactOrderIndependentCleanupTokenAndDisposes()
    {
        TestContext context = new();
        using CancellationTokenSource caller = new();

        BatteryValidationResult result = await context.RunAsync(cancellationToken: caller.Token);

        Assert.Equal(
            ["open", "query", "approve", "apply:Limit75", "query", "apply:Limit80", "query", "dispose"],
            context.Events);
        Assert.True(result.ApplyAttempted);
        Assert.True(result.Applied);
        Assert.True(result.ReadBack);
        Assert.True(result.Restored);
        Assert.True(result.Success);
        Assert.Equal(caller.Token, context.Port.ApplyTokens[0]);
        Assert.NotEqual(caller.Token, context.Port.ApplyTokens[1]);
        Assert.False(context.Port.ApplyTokens[1].IsCancellationRequested);
        Assert.NotEqual(context.Port.ApplyTokens[1], context.Port.QueryTokens[2]);
        Assert.True(context.Port.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_CandidateApplyFailureOrExceptionStillRestores(bool throws)
    {
        TestContext context = new();
        context.SetQueries(
            BatteryValidationSetting.Limit80,
            BatteryValidationSetting.Limit80);
        context.Port.ApplySteps.Clear();
        context.Port.ApplySteps.Enqueue(throws
            ? _ => Task.FromException<HidSessionResult<HidUnit>>(new IOException("unstable apply detail"))
            : _ => Task.FromResult(Failure<HidUnit>(HidSessionFailureKind.ProtocolFailure)));
        context.Port.ApplySteps.Enqueue(_ => Task.FromResult(Success(new HidUnit())));

        BatteryValidationResult result = await context.RunAsync();

        Assert.True(result.ApplyAttempted);
        Assert.False(result.Applied);
        Assert.False(result.ReadBack);
        Assert.True(result.Restored);
        Assert.Equal(BatteryValidationPrimaryFailure.ApplyFailed, result.PrimaryFailure);
        Assert.Equal(BatteryValidationRestorationFailure.None, result.RestorationFailure);
        Assert.Equal(["open", "query", "approve", "apply:Limit75", "apply:Limit80", "query", "dispose"], context.Events);
        Assert.DoesNotContain("unstable apply detail", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_CandidateApplyFailurePreservesSafeDiagnostic()
    {
        TestContext context = new();
        var diagnostic = new HidSessionFailureDiagnostic(
            HidSessionFailurePhase.SetCompletion,
            HidSessionFailureKind.NativeIoFailure,
            NativeWin32Error: 31,
            TransferredByteCount: 0);
        context.SetQueries(
            BatteryValidationSetting.Limit80,
            BatteryValidationSetting.Limit80);
        context.Port.ApplySteps.Clear();
        context.Port.ApplySteps.Enqueue(_ => Task.FromResult(
            Failure<HidUnit>(HidSessionFailureKind.NativeIoFailure) with
            {
                Diagnostic = diagnostic,
            }));
        context.Port.ApplySteps.Enqueue(_ => Task.FromResult(Success(new HidUnit())));

        BatteryValidationResult result = await context.RunAsync();

        Assert.Equal(BatteryValidationPrimaryFailure.ApplyFailed, result.PrimaryFailure);
        Assert.Equal(diagnostic, result.CandidateApplyDiagnostic);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_CandidateQueryFailureOrExceptionStillRestores(bool throws)
    {
        TestContext context = new();
        context.Port.QuerySteps.Clear();
        context.Port.QuerySteps.Enqueue(_ => Task.FromResult(Success(BatteryValidationSetting.Limit80)));
        context.Port.QuerySteps.Enqueue(throws
            ? _ => Task.FromException<HidSessionResult<BatteryValidationSetting>>(new IOException("unstable readback detail"))
            : _ => Task.FromResult(Failure<BatteryValidationSetting>(HidSessionFailureKind.ProtocolFailure)));
        context.Port.QuerySteps.Enqueue(_ => Task.FromResult(Success(BatteryValidationSetting.Limit80)));

        BatteryValidationResult result = await context.RunAsync();

        Assert.True(result.Applied);
        Assert.False(result.ReadBack);
        Assert.True(result.Restored);
        Assert.Equal(BatteryValidationPrimaryFailure.CandidateQueryFailed, result.PrimaryFailure);
        Assert.Equal(BatteryValidationRestorationFailure.None, result.RestorationFailure);
        Assert.True(context.Port.Disposed);
    }

    [Fact]
    public async Task Run_CandidateMismatchStillRestores()
    {
        TestContext context = new();
        context.SetQueries(
            BatteryValidationSetting.Limit80,
            BatteryValidationSetting.Limit70,
            BatteryValidationSetting.Limit80);

        BatteryValidationResult result = await context.RunAsync();

        Assert.True(result.Applied);
        Assert.False(result.ReadBack);
        Assert.True(result.Restored);
        Assert.Equal(BatteryValidationPrimaryFailure.CandidateMismatch, result.PrimaryFailure);
        Assert.False(result.Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_RestoreApplyFailureOrExceptionSkipsRestoreQuery(bool throws)
    {
        TestContext context = new();
        context.Port.ApplySteps.Clear();
        context.Port.ApplySteps.Enqueue(_ => Task.FromResult(Success(new HidUnit())));
        context.Port.ApplySteps.Enqueue(throws
            ? _ => Task.FromException<HidSessionResult<HidUnit>>(new IOException("unstable restore detail"))
            : _ => Task.FromResult(Failure<HidUnit>(HidSessionFailureKind.NativeIoFailure)));

        BatteryValidationResult result = await context.RunAsync();

        Assert.True(result.Applied);
        Assert.True(result.ReadBack);
        Assert.False(result.Restored);
        Assert.Equal(BatteryValidationPrimaryFailure.None, result.PrimaryFailure);
        Assert.Equal(BatteryValidationRestorationFailure.RestoreApplyFailed, result.RestorationFailure);
        Assert.Equal(2, context.Port.QueryTokens.Count);
        Assert.True(context.Port.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_RestoreQueryFailureOrExceptionIsIndependent(bool throws)
    {
        TestContext context = new();
        context.Port.QuerySteps.Clear();
        context.Port.QuerySteps.Enqueue(_ => Task.FromResult(Success(BatteryValidationSetting.Limit80)));
        context.Port.QuerySteps.Enqueue(_ => Task.FromResult(Success(BatteryValidationSetting.Limit75)));
        context.Port.QuerySteps.Enqueue(throws
            ? _ => Task.FromException<HidSessionResult<BatteryValidationSetting>>(new IOException("unstable restore query"))
            : _ => Task.FromResult(Failure<BatteryValidationSetting>(HidSessionFailureKind.ProtocolFailure)));

        BatteryValidationResult result = await context.RunAsync();

        Assert.Equal(BatteryValidationPrimaryFailure.None, result.PrimaryFailure);
        Assert.Equal(BatteryValidationRestorationFailure.RestoreQueryFailed, result.RestorationFailure);
        Assert.False(result.Restored);
        Assert.True(context.Port.Disposed);
    }

    [Fact]
    public async Task Run_WaitsDefaultIntervalBeforeEveryPostBaselineHardwareCommand()
    {
        TestContext context = new(recordDelays: true);
        using CancellationTokenSource caller = new();

        BatteryValidationResult result = await context.RunAsync(cancellationToken: caller.Token);

        Assert.True(result.Success);
        Assert.Equal(
            [
                "open",
                "query",
                "approve",
                "settle:1000",
                "apply:Limit75",
                "settle:1000",
                "query",
                "settle:1000",
                "apply:Limit80",
                "settle:1000",
                "query",
                "dispose",
            ],
            context.Events);
        Assert.Equal(4, context.DelayCalls.Count);
        Assert.All(context.DelayCalls, call => Assert.Equal(TimeSpan.FromSeconds(1), call.Delay));
        Assert.Equal(caller.Token, context.DelayCalls[0].Token);
        Assert.Equal(caller.Token, context.DelayCalls[1].Token);
        Assert.False(context.DelayCalls[2].Token.CanBeCanceled);
        Assert.False(context.DelayCalls[3].Token.CanBeCanceled);
        Assert.True(context.Port.ApplyTokens[1].CanBeCanceled);
        Assert.True(context.Port.QueryTokens[2].CanBeCanceled);
        Assert.NotEqual(context.Port.ApplyTokens[1], context.Port.QueryTokens[2]);
    }

    [Fact]
    public async Task Run_UsesConfiguredSettlingInterval()
    {
        TestContext context = new(
            settlingDelay: TimeSpan.FromMilliseconds(250),
            recordDelays: true);

        BatteryValidationResult result = await context.RunAsync();

        Assert.True(result.Success);
        Assert.Equal(4, context.DelayCalls.Count);
        Assert.All(
            context.DelayCalls,
            call => Assert.Equal(TimeSpan.FromMilliseconds(250), call.Delay));
    }

    [Fact]
    public async Task Run_CallerCancellationDuringCandidateApplySettleSkipsMutationAndRestore()
    {
        using CancellationTokenSource caller = new();
        TestContext context = new(recordDelays: true);
        context.DelaySteps.Enqueue(token =>
        {
            Assert.Equal(caller.Token, token);
            caller.Cancel();
            return Task.FromCanceled(token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.RunAsync(cancellationToken: caller.Token));

        Assert.Empty(context.Port.ApplyTokens);
        Assert.Equal(
            ["open", "query", "approve", "settle:1000", "dispose"],
            context.Events);
    }

    [Fact]
    public async Task Run_CallerCancellationDuringCandidateQuerySettleRestoresWithCleanupTokens()
    {
        using CancellationTokenSource caller = new();
        TestContext context = new(recordDelays: true);
        context.DelaySteps.Enqueue(_ => Task.CompletedTask);
        context.DelaySteps.Enqueue(token =>
        {
            Assert.Equal(caller.Token, token);
            caller.Cancel();
            return Task.FromCanceled(token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.RunAsync(cancellationToken: caller.Token));

        Assert.Equal(2, context.Port.ApplyTokens.Count);
        Assert.Equal(2, context.Port.QueryTokens.Count);
        Assert.Equal(4, context.DelayCalls.Count);
        Assert.Equal(caller.Token, context.DelayCalls[0].Token);
        Assert.Equal(caller.Token, context.DelayCalls[1].Token);
        Assert.False(context.DelayCalls[2].Token.CanBeCanceled);
        Assert.False(context.DelayCalls[3].Token.CanBeCanceled);
        Assert.True(context.Port.ApplyTokens[1].CanBeCanceled);
        Assert.True(context.Port.QueryTokens[1].CanBeCanceled);
        Assert.NotEqual(context.Port.ApplyTokens[1], context.Port.QueryTokens[1]);
        Assert.Equal(
            [
                "open",
                "query",
                "approve",
                "settle:1000",
                "apply:Limit75",
                "settle:1000",
                "settle:1000",
                "apply:Limit80",
                "settle:1000",
                "query",
                "dispose",
            ],
            context.Events);
    }

    [Fact]
    public async Task Run_RestoreSettlingCannotConsumeHardwareCleanupTimeout()
    {
        TestContext context = new(
            cleanupTimeout: TimeSpan.FromMilliseconds(20),
            recordDelays: true);
        context.DelaySteps.Enqueue(_ => Task.CompletedTask);
        context.DelaySteps.Enqueue(_ => Task.CompletedTask);
        context.DelaySteps.Enqueue(_ => Task.Delay(TimeSpan.FromMilliseconds(80)));
        context.DelaySteps.Enqueue(_ => Task.Delay(TimeSpan.FromMilliseconds(80)));

        BatteryValidationResult result = await context.RunAsync();

        Assert.True(result.Success);
        Assert.Equal(2, context.Port.ApplyTokens.Count);
        Assert.Equal(3, context.Port.QueryTokens.Count);
        Assert.False(context.DelayCalls[2].Token.CanBeCanceled);
        Assert.False(context.DelayCalls[3].Token.CanBeCanceled);
        Assert.True(context.Port.ApplyTokens[1].CanBeCanceled);
        Assert.True(context.Port.QueryTokens[2].CanBeCanceled);
        Assert.NotEqual(context.Port.ApplyTokens[1], context.Port.QueryTokens[2]);
    }

    [Fact]
    public async Task Run_CandidateQueryFailurePreservesSafeDiagnostic()
    {
        TestContext context = new();
        var diagnostic = new HidSessionFailureDiagnostic(
            HidSessionFailurePhase.GetCompletion,
            HidSessionFailureKind.Timeout,
            NativeWin32Error: 995,
            TransferredByteCount: 0)
        {
            ProtocolValidationFailure = HidProtocolValidationFailure.PayloadValidator,
        };
        context.Port.QuerySteps.Clear();
        context.Port.QuerySteps.Enqueue(_ => Task.FromResult(Success(BatteryValidationSetting.Limit80)));
        context.Port.QuerySteps.Enqueue(_ => Task.FromResult(
            Failure<BatteryValidationSetting>(HidSessionFailureKind.Timeout) with
            {
                Diagnostic = diagnostic,
            }));
        context.Port.QuerySteps.Enqueue(_ => Task.FromResult(Success(BatteryValidationSetting.Limit80)));

        BatteryValidationResult result = await context.RunAsync();

        Assert.Equal(BatteryValidationPrimaryFailure.CandidateQueryFailed, result.PrimaryFailure);
        Assert.Equal(diagnostic, result.CandidateQueryDiagnostic);
    }

    [Fact]
    public async Task Run_RestoreQueryFailurePreservesSafeDiagnostic()
    {
        TestContext context = new();
        var diagnostic = new HidSessionFailureDiagnostic(
            HidSessionFailurePhase.GetCompletion,
            HidSessionFailureKind.Timeout,
            NativeWin32Error: 995,
            TransferredByteCount: 0);
        context.Port.QuerySteps.Clear();
        context.Port.QuerySteps.Enqueue(_ => Task.FromResult(Success(BatteryValidationSetting.Limit80)));
        context.Port.QuerySteps.Enqueue(_ => Task.FromResult(Success(BatteryValidationSetting.Limit75)));
        context.Port.QuerySteps.Enqueue(_ => Task.FromResult(
            Failure<BatteryValidationSetting>(HidSessionFailureKind.Timeout) with
            {
                Diagnostic = diagnostic,
            }));

        BatteryValidationResult result = await context.RunAsync();

        Assert.Equal(BatteryValidationRestorationFailure.RestoreQueryFailed, result.RestorationFailure);
        Assert.Equal(diagnostic, result.RestorationDiagnostic);
    }

    [Fact]
    public async Task Run_RestoreVerificationGetsFreshBoundedCleanupToken()
    {
        TestContext context = new();

        BatteryValidationResult result = await context.RunAsync();

        Assert.True(result.Restored);
        Assert.NotEqual(context.Port.ApplyTokens[1], context.Port.QueryTokens[2]);
        Assert.False(context.Port.ApplyTokens[1].IsCancellationRequested);
        Assert.False(context.Port.QueryTokens[2].IsCancellationRequested);
    }

    [Fact]
    public async Task Run_RestoreMismatchIsIndependent()
    {
        TestContext context = new();
        context.SetQueries(
            BatteryValidationSetting.Limit80,
            BatteryValidationSetting.Limit75,
            BatteryValidationSetting.Limit70);

        BatteryValidationResult result = await context.RunAsync();

        Assert.Equal(BatteryValidationPrimaryFailure.None, result.PrimaryFailure);
        Assert.Equal(BatteryValidationRestorationFailure.RestoreMismatch, result.RestorationFailure);
        Assert.False(result.Restored);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_CallerCancellationBeforeMutationRethrowsWithoutRestore(bool stableResult)
    {
        using CancellationTokenSource caller = new();
        TestContext context = new();
        context.Port.QuerySteps.Clear();
        context.Port.QuerySteps.Enqueue(_ =>
        {
            caller.Cancel();
            return stableResult
                ? Task.FromResult(Failure<BatteryValidationSetting>(HidSessionFailureKind.Canceled))
                : Task.FromException<HidSessionResult<BatteryValidationSetting>>(
                    new OperationCanceledException(caller.Token));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.RunAsync(cancellationToken: caller.Token));

        Assert.Empty(context.Port.ApplyTokens);
        Assert.True(context.Port.Disposed);
        Assert.Equal(["open", "query", "dispose"], context.Events);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Run_CallerCancellationDuringCandidateOperationRestoresThenRethrows(
        bool duringQuery,
        bool stableResult)
    {
        using CancellationTokenSource caller = new();
        TestContext context = new();
        if (duringQuery)
        {
            context.Port.QuerySteps.Clear();
            context.Port.QuerySteps.Enqueue(_ => Task.FromResult(Success(BatteryValidationSetting.Limit80)));
            context.Port.QuerySteps.Enqueue(_ => CancelQuery(caller, stableResult));
            context.Port.QuerySteps.Enqueue(_ => Task.FromResult(Success(BatteryValidationSetting.Limit80)));
        }
        else
        {
            context.Port.ApplySteps.Clear();
            context.Port.ApplySteps.Enqueue(_ => CancelApply(caller, stableResult));
            context.Port.ApplySteps.Enqueue(_ => Task.FromResult(Success(new HidUnit())));
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.RunAsync(cancellationToken: caller.Token));

        Assert.Equal(2, context.Port.ApplyTokens.Count);
        Assert.True(context.Port.QueryTokens.Count >= 2);
        Assert.False(context.Port.ApplyTokens[1].IsCancellationRequested);
        Assert.True(context.Port.Disposed);
        Assert.Equal("dispose", context.Events[^1]);
    }

    [Fact]
    public async Task Run_CancellationTriggeredDuringCleanupCompletesCleanupDisposesThenRethrows()
    {
        using CancellationTokenSource caller = new();
        TestContext context = new();
        context.Port.ApplySteps.Clear();
        context.Port.ApplySteps.Enqueue(_ => Task.FromResult(Success(new HidUnit())));
        context.Port.ApplySteps.Enqueue(cleanupToken =>
        {
            Assert.False(cleanupToken.IsCancellationRequested);
            caller.Cancel();
            return Task.FromResult(Success(new HidUnit()));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.RunAsync(cancellationToken: caller.Token));

        Assert.Equal(["open", "query", "approve", "apply:Limit75", "query", "apply:Limit80", "query", "dispose"], context.Events);
        Assert.False(context.Port.QueryTokens[2].IsCancellationRequested);
        Assert.True(context.Port.Disposed);
    }

    [Fact]
    public async Task Run_FactoryCallerCancellationIsExceptional()
    {
        using CancellationTokenSource caller = new();
        TestContext context = new();
        context.Factory.Open = token =>
        {
            caller.Cancel();
            return Task.FromResult(Failure<IBatteryValidationPort>(HidSessionFailureKind.QueuedCanceled));
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.RunAsync(cancellationToken: caller.Token));

        Assert.False(context.Port.Disposed);
    }

    [Fact]
    public async Task Run_PreMutationCancellationWinsOverThrowingDisposal()
    {
        using CancellationTokenSource caller = new();
        TestContext context = new();
        context.Port.DisposeException = new IOException("dispose must not mask cancellation");
        context.Factory.Open = _ =>
        {
            caller.Cancel();
            return Task.FromResult(Success<IBatteryValidationPort>(context.Port));
        };

        OperationCanceledException thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.RunAsync(cancellationToken: caller.Token));

        Assert.Equal(caller.Token, thrown.CancellationToken);
        Assert.Equal(1, context.Port.DisposeCount);
        Assert.Empty(context.Port.ApplyTokens);
        Assert.Equal(["open", "dispose"], context.Events);
    }

    [Fact]
    public async Task Run_PostMutationCancellationWinsOverThrowingDisposalAndPreservesOriginal()
    {
        using CancellationTokenSource caller = new();
        TestContext context = new();
        OperationCanceledException original = new("original caller cancellation", caller.Token);
        context.Port.DisposeException = new IOException("dispose must not mask cancellation");
        context.Port.ApplySteps.Clear();
        context.Port.ApplySteps.Enqueue(_ =>
        {
            caller.Cancel();
            return Task.FromException<HidSessionResult<HidUnit>>(original);
        });
        context.Port.ApplySteps.Enqueue(_ => Task.FromResult(Success(new HidUnit())));
        context.SetQueries(BatteryValidationSetting.Limit80, BatteryValidationSetting.Limit80);

        OperationCanceledException thrown = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.RunAsync(cancellationToken: caller.Token));

        Assert.Same(original, thrown);
        Assert.Equal(2, context.Port.ApplyTokens.Count);
        Assert.False(context.Port.ApplyTokens[1].IsCancellationRequested);
        Assert.Equal(1, context.Port.DisposeCount);
        Assert.Equal("dispose", context.Events[^1]);
    }

    [Fact]
    public async Task Run_ThrowingDisposalPropagatesOnNormalTerminalPath()
    {
        TestContext context = new() { ApprovalResult = false };
        IOException disposal = new("observable disposal failure");
        context.Port.DisposeException = disposal;

        IOException thrown = await Assert.ThrowsAsync<IOException>(() => context.RunAsync());

        Assert.Same(disposal, thrown);
        Assert.Equal(1, context.Port.DisposeCount);
        Assert.Empty(context.Port.ApplyTokens);
        Assert.Equal(["open", "query", "approve", "dispose"], context.Events);
    }

    private static async Task<HidSessionResult<HidUnit>> CancelApply(
        CancellationTokenSource caller,
        bool stableResult)
    {
        caller.Cancel();
        if (!stableResult)
        {
            await Task.Yield();
            throw new OperationCanceledException(caller.Token);
        }

        return Failure<HidUnit>(HidSessionFailureKind.Canceled);
    }

    private static async Task<HidSessionResult<BatteryValidationSetting>> CancelQuery(
        CancellationTokenSource caller,
        bool stableResult)
    {
        caller.Cancel();
        if (!stableResult)
        {
            await Task.Yield();
            throw new OperationCanceledException(caller.Token);
        }

        return Failure<BatteryValidationSetting>(HidSessionFailureKind.QueuedCanceled);
    }

    private static HidSessionResult<T> Success<T>(T value) => new(value, null);

    private static HidSessionResult<T> Failure<T>(HidSessionFailureKind failure) =>
        new(default, failure);

    private sealed class TestContext
    {
        public TestContext(
            TimeSpan? cleanupTimeout = null,
            TimeSpan? settlingDelay = null,
            bool recordDelays = false)
        {
            Port = new FakePort(Events);
            Factory = new FakeFactory(Events) { Result = Success<IBatteryValidationPort>(Port) };
            Func<TimeSpan, CancellationToken, Task> delayAsync = (delay, token) =>
            {
                DelayCalls.Add((delay, token));
                if (recordDelays)
                {
                    Events.Add($"settle:{delay.TotalMilliseconds:0}");
                }

                return DelaySteps.Count == 0
                    ? Task.CompletedTask
                    : DelaySteps.Dequeue()(token);
            };
            Session = new BatteryValidationSession(
                Factory,
                Approval = new FakeApproval(Events, () => ApprovalResult),
                cleanupTimeout ?? TimeSpan.FromSeconds(1),
                settlingDelay,
                delayAsync);

            SetQueries(
                BatteryValidationSetting.Limit80,
                BatteryValidationSetting.Limit75,
                BatteryValidationSetting.Limit80);
            Port.ApplySteps.Enqueue(_ => Task.FromResult(Success(new HidUnit())));
            Port.ApplySteps.Enqueue(_ => Task.FromResult(Success(new HidUnit())));
        }

        public List<string> Events { get; } = [];
        public FakeFactory Factory { get; }
        public FakePort Port { get; }
        public FakeApproval Approval { get; }
        public BatteryValidationSession Session { get; }
        public bool ApprovalResult { get; set; } = true;
        public List<(TimeSpan Delay, CancellationToken Token)> DelayCalls { get; } = [];
        public Queue<Func<CancellationToken, Task>> DelaySteps { get; } = [];

        public Task<BatteryValidationResult> RunAsync(
            BatteryValidationSetting candidate = BatteryValidationSetting.Limit75,
            CancellationToken cancellationToken = default) =>
            Session.RunAsync(candidate, cancellationToken);

        public void SetQueries(params BatteryValidationSetting[] settings)
        {
            Port.QuerySteps.Clear();
            foreach (BatteryValidationSetting setting in settings)
            {
                Port.QuerySteps.Enqueue(_ => Task.FromResult(Success(setting)));
            }
        }
    }

    private sealed class FakeFactory(List<string> events) : IBatteryValidationRunFactory
    {
        public HidSessionResult<IBatteryValidationPort> Result { get; set; }
        public Exception? Exception { get; set; }
        public Func<CancellationToken, Task<HidSessionResult<IBatteryValidationPort>>>? Open { get; set; }

        public Task<HidSessionResult<IBatteryValidationPort>> OpenAsync(CancellationToken cancellationToken)
        {
            events.Add("open");
            if (Open is not null)
            {
                return Open(cancellationToken);
            }

            return Exception is null
                ? Task.FromResult(Result)
                : Task.FromException<HidSessionResult<IBatteryValidationPort>>(Exception);
        }
    }

    private sealed class FakePort(List<string> events) : IBatteryValidationPort
    {
        public Queue<Func<CancellationToken, Task<HidSessionResult<BatteryValidationSetting>>>> QuerySteps { get; } = [];
        public Queue<Func<CancellationToken, Task<HidSessionResult<HidUnit>>>> ApplySteps { get; } = [];
        public List<CancellationToken> QueryTokens { get; } = [];
        public List<CancellationToken> ApplyTokens { get; } = [];
        public bool Disposed { get; private set; }
        public int DisposeCount { get; private set; }
        public Exception? DisposeException { get; set; }

        public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(CancellationToken cancellationToken)
        {
            events.Add("query");
            QueryTokens.Add(cancellationToken);
            return QuerySteps.Dequeue()(cancellationToken);
        }

        public Task<HidSessionResult<HidUnit>> ApplyAsync(
            BatteryValidationSetting setting,
            CancellationToken cancellationToken)
        {
            events.Add($"apply:{setting}");
            ApplyTokens.Add(cancellationToken);
            return ApplySteps.Dequeue()(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            events.Add("dispose");
            DisposeCount++;
            Disposed = true;
            return DisposeException is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(DisposeException);
        }
    }

    private sealed class FakeApproval(List<string> events, Func<bool> result) : IInteractiveApproval
    {
        public Action? OnConfirm { get; set; }
        public BatteryValidationSetting? Prior { get; private set; }
        public BatteryValidationSetting? Candidate { get; private set; }
        public CancellationToken Token { get; private set; }

        public Task<bool> ConfirmAsync(
            BatteryValidationSetting prior,
            BatteryValidationSetting candidate,
            CancellationToken cancellationToken)
        {
            events.Add("approve");
            Prior = prior;
            Candidate = candidate;
            Token = cancellationToken;
            OnConfirm?.Invoke();
            return Task.FromResult(result());
        }
    }
}
