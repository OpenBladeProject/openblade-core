using OpenSynapse.Capture;
using OpenSynapse.Protocol;

namespace OpenSynapse.Capture.Tests;

public sealed class BatteryValidationSessionTests
{
    private static readonly HidInterfaceInfo ExactInterface = new(
        "approved", 0x1532, 0x02E0, 0x0001, 0x0002, 91);

    [Fact]
    public async Task Run_WrongTargetAbortsBeforeConflictCheck()
    {
        TestContext context = new();

        BatteryValidationResult result = await context.Session.RunAsync(
            "wrong-model",
            ExactInterface,
            BatteryValidationSetting.Limit75,
            CancellationToken.None);

        Assert.Equal(BatteryValidationPrimaryFailure.WrongTarget, result.PrimaryFailure);
        Assert.Empty(context.Events);
        Assert.False(result.ApplyAttempted);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task Run_InitialConflictAbortsBeforeQuery()
    {
        TestContext context = new();
        context.ConflictChecks.Clear();
        context.ConflictChecks.Enqueue(new ValidationConflictCheck(["RazerAppEngine"], null));

        BatteryValidationResult result = await context.RunAsync();

        Assert.Equal(BatteryValidationPrimaryFailure.Conflict, result.PrimaryFailure);
        Assert.Equal(["conflict"], context.Events);
        Assert.False(result.ApplyAttempted);
    }

    [Fact]
    public async Task Run_InitialConflictDetectionFailureAbortsBeforeQuery()
    {
        TestContext context = new();
        context.ConflictChecks.Clear();
        context.ConflictChecks.Enqueue(new ValidationConflictCheck(
            ["RazerAppEngine"],
            "Process enumeration failed."));

        BatteryValidationResult result = await context.RunAsync();

        Assert.Equal(
            BatteryValidationPrimaryFailure.ConflictDetectionFailed,
            result.PrimaryFailure);
        Assert.Equal(["conflict"], context.Events);
        Assert.False(result.ApplyAttempted);
    }

    [Fact]
    public async Task Run_UnknownPriorAbortsBeforeConfirmationOrApply()
    {
        TestContext context = new();
        context.QueryResults.Clear();
        context.QueryResults.Enqueue(BatteryQueryResult.Unknown);

        BatteryValidationResult result = await context.RunAsync();

        Assert.Equal(BatteryValidationPrimaryFailure.UnknownPrior, result.PrimaryFailure);
        Assert.Equal(["conflict", "query"], context.Events);
        Assert.False(result.ApplyAttempted);
    }

    [Fact]
    public async Task Run_UndefinedPriorAbortsBeforeConfirmationOrApply()
    {
        TestContext context = new();
        context.QueryResults.Clear();
        context.QueryResults.Enqueue(new BatteryQueryResult((BatteryValidationSetting)999));

        BatteryValidationResult result = await context.RunAsync();

        Assert.Equal(BatteryValidationPrimaryFailure.UndefinedPrior, result.PrimaryFailure);
        Assert.Equal(["conflict", "query"], context.Events);
        Assert.False(result.ApplyAttempted);
    }

    [Fact]
    public async Task Run_UndefinedCandidateAbortsBeforeConfirmationOrApply()
    {
        TestContext context = new();

        BatteryValidationResult result = await context.Session.RunAsync(
            TargetBladeDevice.Model,
            ExactInterface,
            (BatteryValidationSetting)999,
            CancellationToken.None);

        Assert.Equal(BatteryValidationPrimaryFailure.UndefinedCandidate, result.PrimaryFailure);
        Assert.Equal(["conflict"], context.Events);
        Assert.False(result.ApplyAttempted);
    }

    [Fact]
    public async Task Run_SameCandidateAbortsBeforeConfirmationOrApply()
    {
        TestContext context = new();

        BatteryValidationResult result = await context.Session.RunAsync(
            TargetBladeDevice.Model,
            ExactInterface,
            BatteryValidationSetting.Limit80,
            CancellationToken.None);

        Assert.Equal(BatteryValidationPrimaryFailure.SameCandidate, result.PrimaryFailure);
        Assert.Equal(["conflict", "query"], context.Events);
        Assert.False(result.ApplyAttempted);
    }

    [Fact]
    public async Task Run_DeclinedApprovalAbortsBeforeApply()
    {
        TestContext context = new() { ApprovalResult = false };

        BatteryValidationResult result = await context.RunAsync();

        Assert.Equal(BatteryValidationPrimaryFailure.ApprovalDeclined, result.PrimaryFailure);
        Assert.Equal(["conflict", "query", "approve"], context.Events);
        Assert.False(result.ApplyAttempted);
    }

    [Fact]
    public async Task Run_ConflictOnSecondCheckAbortsImmediatelyBeforeApply()
    {
        TestContext context = new();
        context.ConflictChecks.Clear();
        context.ConflictChecks.Enqueue(new ValidationConflictCheck([], null));
        context.ConflictChecks.Enqueue(new ValidationConflictCheck(["RazerAppEngine"], null));

        BatteryValidationResult result = await context.RunAsync();

        Assert.Equal(BatteryValidationPrimaryFailure.Conflict, result.PrimaryFailure);
        Assert.Equal(["conflict", "query", "approve", "conflict"], context.Events);
        Assert.False(result.ApplyAttempted);
    }

    [Fact]
    public async Task Run_DetectionFailureOnSecondCheckAbortsImmediatelyBeforeApply()
    {
        TestContext context = new();
        context.ConflictChecks.Clear();
        context.ConflictChecks.Enqueue(new ValidationConflictCheck([], null));
        context.ConflictChecks.Enqueue(new ValidationConflictCheck(
            ["RazerAppEngine"],
            "Windows service enumeration failed."));

        BatteryValidationResult result = await context.RunAsync();

        Assert.Equal(
            BatteryValidationPrimaryFailure.ConflictDetectionFailed,
            result.PrimaryFailure);
        Assert.Equal(["conflict", "query", "approve", "conflict"], context.Events);
        Assert.False(result.ApplyAttempted);
    }

    [Fact]
    public async Task Run_SuccessUsesExactOrderIndependentQueriesAndCleanupToken()
    {
        TestContext context = new();
        using CancellationTokenSource caller = new();

        BatteryValidationResult result = await context.Session.RunAsync(
            TargetBladeDevice.Model,
            ExactInterface,
            BatteryValidationSetting.Limit75,
            caller.Token);

        Assert.Equal(
            [
                "conflict",
                "query",
                "approve",
                "conflict",
                "apply:Limit75",
                "query",
                "apply:Limit80",
                "query",
            ],
            context.Events);
        Assert.True(result.ApplyAttempted);
        Assert.True(result.Applied);
        Assert.True(result.ReadBack);
        Assert.True(result.Restored);
        Assert.True(result.Success);
        Assert.Equal(BatteryValidationPrimaryFailure.None, result.PrimaryFailure);
        Assert.Equal(BatteryValidationRestorationFailure.None, result.RestorationFailure);
        Assert.Equal(caller.Token, context.ApplyTokens[0]);
        Assert.NotEqual(caller.Token, context.ApplyTokens[1]);
        Assert.False(context.ApplyTokens[1].IsCancellationRequested);
        Assert.Equal(context.ApplyTokens[1], context.QueryTokens[2]);
    }

    [Fact]
    public async Task Run_CandidateMismatchStillRestoresAndFailsGates()
    {
        TestContext context = new();
        context.SetQueryResults(
            BatteryValidationSetting.Limit80,
            BatteryValidationSetting.Limit70,
            BatteryValidationSetting.Limit80);

        BatteryValidationResult result = await context.RunAsync();

        Assert.True(result.Applied);
        Assert.False(result.ReadBack);
        Assert.True(result.Restored);
        Assert.False(result.Success);
        Assert.Equal(BatteryValidationPrimaryFailure.CandidateMismatch, result.PrimaryFailure);
        Assert.Contains("apply:Limit80", context.Events);
    }

    [Fact]
    public async Task Run_UnknownCandidateReadbackStillRestoresAndFailsGates()
    {
        TestContext context = new();
        context.QueryResults.Clear();
        context.QueryResults.Enqueue(new BatteryQueryResult(BatteryValidationSetting.Limit80));
        context.QueryResults.Enqueue(BatteryQueryResult.Unknown);
        context.QueryResults.Enqueue(new BatteryQueryResult(BatteryValidationSetting.Limit80));

        BatteryValidationResult result = await context.RunAsync();

        Assert.True(result.Applied);
        Assert.False(result.ReadBack);
        Assert.True(result.Restored);
        Assert.False(result.Success);
        Assert.Equal(BatteryValidationPrimaryFailure.CandidateMismatch, result.PrimaryFailure);
    }

    [Fact]
    public async Task Run_ApplyExceptionStillRestoresAndPreservesBothOutcomes()
    {
        TestContext context = new();
        context.ApplyFailures[0] = new IOException("unstable detail");
        context.SetQueryResults(
            BatteryValidationSetting.Limit80,
            BatteryValidationSetting.Limit70);

        BatteryValidationResult result = await context.RunAsync();

        Assert.True(result.ApplyAttempted);
        Assert.False(result.Applied);
        Assert.False(result.ReadBack);
        Assert.False(result.Restored);
        Assert.False(result.Success);
        Assert.Equal(BatteryValidationPrimaryFailure.ApplyFailed, result.PrimaryFailure);
        Assert.Equal(
            BatteryValidationRestorationFailure.RestoreMismatch,
            result.RestorationFailure);
        Assert.Equal(2, context.ApplyTokens.Count);
    }

    [Fact]
    public async Task Run_CandidateQueryExceptionUsesStableFailureAndRestores()
    {
        TestContext context = new();
        context.QueryFailures[1] = new IOException("unstable detail");
        context.SetQueryResults(
            BatteryValidationSetting.Limit80,
            BatteryValidationSetting.Limit80);

        BatteryValidationResult result = await context.RunAsync();

        Assert.Equal(BatteryValidationPrimaryFailure.CandidateQueryFailed, result.PrimaryFailure);
        Assert.True(result.Applied);
        Assert.False(result.ReadBack);
        Assert.True(result.Restored);
        Assert.DoesNotContain("unstable detail", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_RestoreMismatchIsReportedSeparately()
    {
        TestContext context = new();
        context.SetQueryResults(
            BatteryValidationSetting.Limit80,
            BatteryValidationSetting.Limit75,
            BatteryValidationSetting.Limit70);

        BatteryValidationResult result = await context.RunAsync();

        Assert.True(result.Applied);
        Assert.True(result.ReadBack);
        Assert.False(result.Restored);
        Assert.False(result.Success);
        Assert.Equal(BatteryValidationPrimaryFailure.None, result.PrimaryFailure);
        Assert.Equal(
            BatteryValidationRestorationFailure.RestoreMismatch,
            result.RestorationFailure);
    }

    [Fact]
    public async Task Run_UnknownRestoreReadbackIsRestoreMismatch()
    {
        TestContext context = new();
        context.QueryResults.Clear();
        context.QueryResults.Enqueue(new BatteryQueryResult(BatteryValidationSetting.Limit80));
        context.QueryResults.Enqueue(new BatteryQueryResult(BatteryValidationSetting.Limit75));
        context.QueryResults.Enqueue(BatteryQueryResult.Unknown);

        BatteryValidationResult result = await context.RunAsync();

        Assert.True(result.Applied);
        Assert.True(result.ReadBack);
        Assert.False(result.Restored);
        Assert.False(result.Success);
        Assert.Equal(
            BatteryValidationRestorationFailure.RestoreMismatch,
            result.RestorationFailure);
    }

    [Fact]
    public async Task Run_RestoreApplyFailureIsReportedWithoutFinalQuery()
    {
        TestContext context = new();
        context.ApplyFailures[1] = new OperationCanceledException();

        BatteryValidationResult result = await context.RunAsync();

        Assert.True(result.Applied);
        Assert.True(result.ReadBack);
        Assert.False(result.Restored);
        Assert.Equal(
            BatteryValidationRestorationFailure.RestoreApplyFailed,
            result.RestorationFailure);
        Assert.Equal(2, context.QueryTokens.Count);
    }

    [Fact]
    public async Task Run_RestoreFinalQueryCancellationIsReported()
    {
        TestContext context = new();
        context.QueryFailures[2] = new OperationCanceledException();

        BatteryValidationResult result = await context.RunAsync();

        Assert.False(result.Restored);
        Assert.Equal(
            BatteryValidationRestorationFailure.RestoreQueryFailed,
            result.RestorationFailure);
        Assert.Equal(2, context.ApplyTokens.Count);
        Assert.Equal(3, context.QueryTokens.Count);
    }

    [Fact]
    public async Task Run_CallerCancellationBeforeMutationPropagatesWithoutRestore()
    {
        TestContext context = new();
        using CancellationTokenSource caller = new();
        context.QueryFailures[0] = new OperationCanceledException(caller.Token);
        context.OnQuery = (index, _) =>
        {
            if (index == 0)
            {
                caller.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Session.RunAsync(
                TargetBladeDevice.Model,
                ExactInterface,
                BatteryValidationSetting.Limit75,
                caller.Token));

        Assert.Equal(["conflict", "query"], context.Events);
        Assert.Empty(context.ApplyTokens);
    }

    [Fact]
    public async Task Run_CallerCancellationDuringCandidateSetRestoresThenRethrows()
    {
        TestContext context = new();
        using CancellationTokenSource caller = new();
        context.SetQueryResults(
            BatteryValidationSetting.Limit80,
            BatteryValidationSetting.Limit80);
        context.ApplyFailures[0] = new OperationCanceledException(caller.Token);
        context.OnApply = (index, _) =>
        {
            if (index == 0)
            {
                caller.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Session.RunAsync(
                TargetBladeDevice.Model,
                ExactInterface,
                BatteryValidationSetting.Limit75,
                caller.Token));

        Assert.Equal(
            ["apply:Limit75", "apply:Limit80"],
            context.Events.Where(item => item.StartsWith("apply:", StringComparison.Ordinal)));
        Assert.NotEqual(caller.Token, context.ApplyTokens[1]);
        Assert.False(context.ApplyTokens[1].IsCancellationRequested);
    }

    [Fact]
    public async Task Run_CallerCancellationDuringCandidateQueryRestoresThenRethrows()
    {
        TestContext context = new();
        using CancellationTokenSource caller = new();
        context.SetQueryResults(
            BatteryValidationSetting.Limit80,
            BatteryValidationSetting.Limit80);
        context.QueryFailures[1] = new OperationCanceledException(caller.Token);
        context.OnQuery = (index, _) =>
        {
            if (index == 1)
            {
                caller.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Session.RunAsync(
                TargetBladeDevice.Model,
                ExactInterface,
                BatteryValidationSetting.Limit75,
                caller.Token));

        Assert.Equal("apply:Limit80", context.Events[^2]);
        Assert.Equal("query", context.Events[^1]);
        Assert.False(context.ApplyTokens[1].IsCancellationRequested);
    }

    [Fact]
    public async Task Run_CallerCancellationTriggeredDuringRestoreApplyFinishesCleanupThenRethrows()
    {
        TestContext context = new();
        using CancellationTokenSource caller = new();
        context.OnApply = (index, _) =>
        {
            if (index == 1)
            {
                caller.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Session.RunAsync(
                TargetBladeDevice.Model,
                ExactInterface,
                BatteryValidationSetting.Limit75,
                caller.Token));

        Assert.Equal("apply:Limit80", context.Events[^2]);
        Assert.Equal("query", context.Events[^1]);
        Assert.NotEqual(caller.Token, context.ApplyTokens[1]);
        Assert.False(context.ApplyTokens[1].IsCancellationRequested);
        Assert.Equal(context.ApplyTokens[1], context.QueryTokens[2]);
    }

    [Fact]
    public async Task Run_CallerCancellationTriggeredDuringFinalRestoreQueryFinishesThenRethrows()
    {
        TestContext context = new();
        using CancellationTokenSource caller = new();
        context.OnQuery = (index, _) =>
        {
            if (index == 2)
            {
                caller.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Session.RunAsync(
                TargetBladeDevice.Model,
                ExactInterface,
                BatteryValidationSetting.Limit75,
                caller.Token));

        Assert.Equal("query", context.Events[^1]);
        Assert.NotEqual(caller.Token, context.QueryTokens[2]);
        Assert.False(context.QueryTokens[2].IsCancellationRequested);
    }

    [Theory]
    [InlineData(false, BatteryValidationPrimaryFailure.None, BatteryValidationRestorationFailure.None)]
    [InlineData(true, BatteryValidationPrimaryFailure.ApplyFailed, BatteryValidationRestorationFailure.None)]
    [InlineData(true, BatteryValidationPrimaryFailure.None, BatteryValidationRestorationFailure.RestoreMismatch)]
    public void Success_RejectsContradictoryResult(
        bool applyAttempted,
        BatteryValidationPrimaryFailure primaryFailure,
        BatteryValidationRestorationFailure restorationFailure)
    {
        BatteryValidationResult result = new(
            ApplyAttempted: applyAttempted,
            Applied: true,
            ReadBack: true,
            Restored: true,
            PrimaryFailure: primaryFailure,
            RestorationFailure: restorationFailure);

        Assert.False(result.Success);
    }

    private sealed class TestContext
    {
        public TestContext()
        {
            QueryResults.Enqueue(new BatteryQueryResult(BatteryValidationSetting.Limit80));
            QueryResults.Enqueue(new BatteryQueryResult(BatteryValidationSetting.Limit75));
            QueryResults.Enqueue(new BatteryQueryResult(BatteryValidationSetting.Limit80));
            ConflictChecks.Enqueue(new ValidationConflictCheck([], null));
            ConflictChecks.Enqueue(new ValidationConflictCheck([], null));
            Session = new BatteryValidationSession(
                new FakePort(this),
                new FakeConflictGuard(this),
                new FakeApproval(this),
                TimeSpan.FromSeconds(1));
        }

        public List<string> Events { get; } = [];

        public Queue<BatteryQueryResult> QueryResults { get; } = [];

        public Queue<ValidationConflictCheck> ConflictChecks { get; } = [];

        public List<CancellationToken> ApplyTokens { get; } = [];

        public List<CancellationToken> QueryTokens { get; } = [];

        public Dictionary<int, Exception> ApplyFailures { get; } = [];

        public Dictionary<int, Exception> QueryFailures { get; } = [];

        public Action<int, CancellationToken>? OnApply { get; set; }

        public Action<int, CancellationToken>? OnQuery { get; set; }

        public BatteryValidationSession Session { get; }

        public bool ApprovalResult { get; init; } = true;

        public Task<BatteryValidationResult> RunAsync() => Session.RunAsync(
            TargetBladeDevice.Model,
            ExactInterface,
            BatteryValidationSetting.Limit75,
            CancellationToken.None);

        public void SetQueryResults(params BatteryValidationSetting[] settings)
        {
            QueryResults.Clear();
            foreach (BatteryValidationSetting setting in settings)
            {
                Assert.True(Enum.IsDefined(setting));
                QueryResults.Enqueue(new BatteryQueryResult(setting));
            }
        }

        private sealed class FakePort(TestContext context) : IBatteryValidationPort
        {
            public Task<BatteryQueryResult> QueryAsync(CancellationToken cancellationToken)
            {
                int index = context.QueryTokens.Count;
                context.Events.Add("query");
                context.QueryTokens.Add(cancellationToken);
                context.OnQuery?.Invoke(index, cancellationToken);
                if (context.QueryFailures.TryGetValue(index, out Exception? failure))
                {
                    return Task.FromException<BatteryQueryResult>(failure);
                }

                return Task.FromResult(context.QueryResults.Dequeue());
            }

            public Task ApplyAsync(
                BatteryValidationSetting setting,
                CancellationToken cancellationToken)
            {
                int index = context.ApplyTokens.Count;
                Assert.True(Enum.IsDefined(setting));
                context.Events.Add($"apply:{setting}");
                context.ApplyTokens.Add(cancellationToken);
                context.OnApply?.Invoke(index, cancellationToken);
                if (context.ApplyFailures.TryGetValue(index, out Exception? failure))
                {
                    return Task.FromException(failure);
                }

                return Task.CompletedTask;
            }
        }

        private sealed class FakeConflictGuard(TestContext context) : IValidationConflictGuard
        {
            public ValidationConflictCheck Check()
            {
                context.Events.Add("conflict");
                return context.ConflictChecks.Dequeue();
            }
        }

        private sealed class FakeApproval(TestContext context) : IInteractiveApproval
        {
            public Task<bool> ConfirmAsync(
                BatteryValidationSetting prior,
                BatteryValidationSetting candidate,
                CancellationToken cancellationToken)
            {
                Assert.True(Enum.IsDefined(prior));
                Assert.True(Enum.IsDefined(candidate));
                context.Events.Add("approve");
                return Task.FromResult(context.ApprovalResult);
            }
        }
    }
}
