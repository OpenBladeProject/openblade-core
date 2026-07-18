using OpenBlade.Contracts;
using OpenBlade.Display;
using OpenBlade.Session;

namespace OpenBlade.Session.Tests;

public sealed class DisplayProfileApplierTests
{
    [Fact]
    public async Task ApplyProfile_SequencesQueryRefreshHdrColorAndConfirmsReadback()
    {
        var context = ConfirmedContext();
        var controller = new FakeDisplayController(
            SessionDisplayIpcCodecTests.Snapshot("fixed-60", true, "native"));
        using var applier = new DisplayProfileApplier(controller, context);

        SessionDisplayExecutionResult result = await applier.ApplyProfileAsync(
            PowerSource.PluggedIn,
            new DisplayProfileSettings("dynamic-48-240", false, "sRGB_CTR.icm"));

        Assert.True(result.Succeeded);
        Assert.Equal(
            ["query", "refresh:dynamic-48-240", "hdr:False", "color:sRGB_CTR.icm"],
            controller.Calls);
        Assert.Equal("dynamic-48-240", result.State.CurrentRefreshKey);
        Assert.False(result.State.HdrEnabled);
        Assert.Equal("sRGB_CTR.icm", result.State.ActiveColorProfileId);
    }

    [Fact]
    public async Task Mutation_SourceMismatchIsBlockedBeforeAnySetter()
    {
        var context = ConfirmedContext(PowerSource.Battery);
        var controller = new FakeDisplayController(
            SessionDisplayIpcCodecTests.Snapshot("fixed-60", false, "native"));
        using var applier = new DisplayProfileApplier(controller, context);

        SessionDisplayExecutionResult result = await applier.SetHdrAsync(
            PowerSource.PluggedIn,
            true);

        Assert.Equal(SessionDisplayOperationStatus.Blocked, result.Status);
        Assert.Equal(["query"], controller.Calls);
    }

    [Fact]
    public async Task UnconfiguredProfile_IsAQueryOnlyNoOpEvenWithoutConfirmedSource()
    {
        var context = new DisplayPowerContextState();
        var controller = new FakeDisplayController(
            SessionDisplayIpcCodecTests.Snapshot("fixed-60", false, "native"));
        using var applier = new DisplayProfileApplier(controller, context);

        SessionDisplayExecutionResult result = await applier.ApplyProfileAsync(
            PowerSource.PluggedIn,
            DisplayProfileSettings.Unconfigured());

        Assert.True(result.Succeeded);
        Assert.Equal(["query"], controller.Calls);
    }

    [Fact]
    public async Task ApplyProfile_HdrTargetSkipsDormantColorProfile()
    {
        var context = ConfirmedContext();
        var controller = new FakeDisplayController(
            SessionDisplayIpcCodecTests.Snapshot("fixed-240", false, "native"));
        using var applier = new DisplayProfileApplier(controller, context);

        SessionDisplayExecutionResult result = await applier.ApplyProfileAsync(
            PowerSource.PluggedIn,
            new DisplayProfileSettings("fixed-240", true, "sRGB_CTR.icm"));

        Assert.True(result.Succeeded);
        Assert.Equal(["query", "hdr:True"], controller.Calls);
        Assert.True(result.State.HdrEnabled);
        Assert.Equal("native", result.State.ActiveColorProfileId);
    }

    [Fact]
    public async Task ApplyProfile_StopsBetweenStepsWhenGenerationBecomesStale()
    {
        var context = ConfirmedContext();
        var controller = new FakeDisplayController(
            SessionDisplayIpcCodecTests.Snapshot("fixed-60", true, "native"));
        controller.AfterRefresh = () => context.Update(PowerSource.Battery, 2);
        using var applier = new DisplayProfileApplier(controller, context);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => applier.ApplyProfileForTransitionAsync(
                PowerSource.PluggedIn,
                1,
                new DisplayProfileSettings("fixed-240", false, "sRGB_CTR.icm"),
                CancellationToken.None));

        Assert.Equal(["query", "refresh:fixed-240"], controller.Calls);
    }

    [Fact]
    public async Task ApplyProfile_FailedStepStopsTransactionAndReturnsItsReadback()
    {
        var context = ConfirmedContext();
        var controller = new FakeDisplayController(
            SessionDisplayIpcCodecTests.Snapshot("fixed-60", true, "native"))
        {
            FailRefresh = true,
        };
        using var applier = new DisplayProfileApplier(controller, context);

        SessionDisplayExecutionResult result = await applier.ApplyProfileAsync(
            PowerSource.PluggedIn,
            new DisplayProfileSettings("fixed-240", false, "sRGB_CTR.icm"));

        Assert.Equal(SessionDisplayOperationStatus.Failed, result.Status);
        Assert.Equal(["query", "refresh:fixed-240"], controller.Calls);
        Assert.Equal("fixed-60", result.State.CurrentRefreshKey);
    }

    [Fact]
    public async Task OperationsShareOneGate()
    {
        var context = ConfirmedContext();
        var controller = new FakeDisplayController(
            SessionDisplayIpcCodecTests.Snapshot("fixed-60", false, "native"))
        {
            BlockRefresh = true,
        };
        using var applier = new DisplayProfileApplier(controller, context);

        Task<SessionDisplayExecutionResult> refresh = applier.SetRefreshRateAsync(
            PowerSource.PluggedIn,
            "fixed-240");
        await controller.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<SessionDisplayExecutionResult> hdr = applier.SetHdrAsync(
            PowerSource.PluggedIn,
            true);
        await Task.Delay(50);

        Assert.DoesNotContain("hdr:True", controller.Calls);
        controller.ReleaseRefresh.TrySetResult();
        await Task.WhenAll(refresh, hdr);
        Assert.Equal(["refresh:fixed-240", "hdr:True"], controller.Calls);
        Assert.Equal(1, controller.MaximumConcurrentCalls);
    }

    private static DisplayPowerContextState ConfirmedContext(
        PowerSource source = PowerSource.PluggedIn)
    {
        var context = new DisplayPowerContextState();
        context.Update(source, 1);
        return context;
    }

    private sealed class FakeDisplayController(DisplaySettingsSnapshot initial)
        : IDisplaySettingsController
    {
        private DisplaySettingsSnapshot state = initial;
        private int activeCalls;

        public List<string> Calls { get; } = [];
        public Action? AfterRefresh { get; set; }
        public bool FailRefresh { get; init; }
        public bool BlockRefresh { get; init; }
        public TaskCompletionSource RefreshStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRefresh { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public int MaximumConcurrentCalls { get; private set; }

        public async Task<DisplaySettingsSnapshot> QueryAsync(
            CancellationToken cancellationToken = default)
        {
            Enter();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Calls.Add("query");
                await Task.Yield();
                return state;
            }
            finally
            {
                Exit();
            }
        }

        public async Task<DisplayOperationResult> SetRefreshAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            Enter();
            try
            {
                Calls.Add($"refresh:{key}");
                if (BlockRefresh)
                {
                    RefreshStarted.TrySetResult();
                    await ReleaseRefresh.Task.WaitAsync(cancellationToken);
                }
                if (FailRefresh)
                {
                    return DisplayOperationResult.Failure("refresh failed", state);
                }
                state = state with { CurrentRefreshKey = key };
                AfterRefresh?.Invoke();
                return DisplayOperationResult.Success(state);
            }
            finally
            {
                Exit();
            }
        }

        public Task<DisplayOperationResult> SetHdrAsync(
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            Enter();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Calls.Add($"hdr:{enabled}");
                state = state with
                {
                    HdrEnabled = enabled,
                    ColorProfilesEnabled = !enabled,
                };
                return Task.FromResult(DisplayOperationResult.Success(state));
            }
            finally
            {
                Exit();
            }
        }

        public Task<DisplayOperationResult> SetColorProfileAsync(
            string id,
            CancellationToken cancellationToken = default)
        {
            Enter();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Calls.Add($"color:{id}");
                state = state with { ActiveColorProfileId = id };
                return Task.FromResult(DisplayOperationResult.Success(state));
            }
            finally
            {
                Exit();
            }
        }

        private void Enter()
        {
            int active = Interlocked.Increment(ref activeCalls);
            MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, active);
        }

        private void Exit() => Interlocked.Decrement(ref activeCalls);
    }
}
