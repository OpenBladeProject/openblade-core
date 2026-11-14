using OpenBlade.Contracts;
using OpenBlade.Display;
using OpenBlade.Session;

namespace OpenBlade.Session.Tests;

public sealed class DisplayProfileApplierTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstClassifiedApplicationCannotOverwriteSuccessfulUnclassifiedManualHdr(bool recovered)
    {
        var context = new DisplayPowerContextState();
        var controller = new FakeDisplayController(SessionDisplayIpcCodecTests.Snapshot("fixed-60", false, "native"));
        using var applier = new DisplayProfileApplier(controller, context);
        DisplayAutomationRecoveryState? checkpoint = null;
        await using var coordinator = new DisplayAutomationCoordinator(context, applier,
            recordRecoveryState: value => { checkpoint = value; return true; });
        Guid applicationId = Guid.NewGuid();
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults() with
        {
            ApplicationProfiles =
            [
                new(applicationId, "Opposite HDR", ApplicationProfileDefinition.NormalizeExecutablePath(@"C:/Games/display.exe"),
                    ApplicationActivationMode.Foreground, new ApplicationProfileOverrideSet
                    {
                        PluggedIn = new ApplicationPowerProfileOverride { Display = new(null, false, null) },
                    }),
            ],
        };
        ServiceStatusSnapshot unknown = UnclassifiedStatus() with
        {
            ExternalPowerPresent = true,
            Profiles = profiles,
            ActiveApplicationProfileId = applicationId,
        };
        coordinator.Observe(unknown);
        Assert.True(coordinator.BeginManualOperation(new(null, true, null)));
        SessionDisplayExecutionResult manual = await applier.SetHdrAsync(PowerSource.ExternalUnknown, true);
        Assert.True(manual.Succeeded);
        coordinator.CompleteManualOperation(manual.Status, manual.State);
        Assert.True(checkpoint!.ManualHold);
        Assert.False(checkpoint.AutomaticWritesPaused);
        ServiceStatusSnapshot classified = unknown with { ActivePowerSource = PowerSource.PluggedIn, Revision = 2 };
        if (recovered)
        {
            var recoveredContext = new DisplayPowerContextState();
            using var recoveredApplier = new DisplayProfileApplier(controller, recoveredContext);
            await using var restarted = new DisplayAutomationCoordinator(recoveredContext, recoveredApplier,
                recoveryState: checkpoint);
            Assert.Equal(DisplayAutomationObservation.BaselineEstablished, restarted.Observe(classified));
            await restarted.WhenIdleAsync();
        }
        else
        {
            Assert.Equal(DisplayAutomationObservation.BaselineEstablished, coordinator.Observe(classified));
            await coordinator.WhenIdleAsync();
        }
        Assert.True((await controller.QueryAsync()).HdrEnabled);
        Assert.Equal(["hdr:True"], controller.Calls.Where(call => call.StartsWith("hdr:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FirstClassifiedApplicationStillAppliesWhenThereIsNoManualHold()
    {
        var context = new DisplayPowerContextState();
        var controller = new FakeDisplayController(SessionDisplayIpcCodecTests.Snapshot("fixed-60", true, "native"));
        using var applier = new DisplayProfileApplier(controller, context);
        await using var coordinator = new DisplayAutomationCoordinator(context, applier);
        Guid applicationId = Guid.NewGuid();
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults() with
        {
            ApplicationProfiles =
            [
                new(applicationId, "Startup HDR", ApplicationProfileDefinition.NormalizeExecutablePath(@"C:/Games/startup-display.exe"),
                    ApplicationActivationMode.Foreground, new ApplicationProfileOverrideSet
                    {
                        PluggedIn = new ApplicationPowerProfileOverride { Display = new(null, false, null) },
                    }),
            ],
        };
        coordinator.Observe(UnclassifiedStatus() with { ExternalPowerPresent = true });
        Assert.Equal(DisplayAutomationObservation.TransitionScheduled, coordinator.Observe(UnclassifiedStatus() with
        {
            ActivePowerSource = PowerSource.PluggedIn,
            Profiles = profiles,
            ActiveApplicationProfileId = applicationId,
        }));
        await coordinator.WhenIdleAsync();
        Assert.False((await controller.QueryAsync()).HdrEnabled);
        Assert.Equal(["hdr:False"], controller.Calls.Where(call => call.StartsWith("hdr:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task UnclassifiedExternalManualSetterRequiresPositiveContextAndKeepsProfilesDeferred()
    {
        var context = new DisplayPowerContextState();
        var controller = new FakeDisplayController(SessionDisplayIpcCodecTests.Snapshot("fixed-60", false, "native"));
        using var applier = new DisplayProfileApplier(controller, context);
        await using var coordinator = new DisplayAutomationCoordinator(context, applier);
        ServiceStatusSnapshot status = UnclassifiedStatus();
        coordinator.Observe(status);
        Assert.Equal(SessionDisplayOperationStatus.Blocked,
            (await applier.SetHdrAsync(PowerSource.ExternalUnknown, true)).Status);
        Assert.DoesNotContain(controller.Calls, call => call.StartsWith("hdr:", StringComparison.Ordinal));
        coordinator.Observe(status with { ExternalPowerPresent = true });
        Assert.False(context.Capture().IsConfirmed);
        Assert.True(coordinator.BeginManualOperation(new(null, true, null)));
        SessionDisplayExecutionResult applied = await applier.SetHdrAsync(PowerSource.ExternalUnknown, true);
        coordinator.CompleteManualOperation(applied.Status, applied.State);
        Assert.True(applied.Succeeded);
        Assert.Equal(PowerSource.ExternalUnknown, context.Capture().PowerSource);
        Assert.Equal(SessionDisplayOperationStatus.Blocked,
            (await applier.ApplyProfileAsync(PowerSource.ExternalUnknown, new(null, false, null))).Status);
        coordinator.MarkUnavailable();
        Assert.Equal(SessionDisplayOperationStatus.Blocked,
            (await applier.SetHdrAsync(PowerSource.ExternalUnknown, false)).Status);
    }

    [Fact]
    public async Task UnclassifiedPresenceLossAfterSubmissionCancelsAndNeverReplays()
    {
        var context = new DisplayPowerContextState();
        var controller = new FakeDisplayController(SessionDisplayIpcCodecTests.Snapshot("fixed-60", false, "native"));
        using var applier = new DisplayProfileApplier(controller, context);
        await using var coordinator = new DisplayAutomationCoordinator(context, applier);
        coordinator.Observe(UnclassifiedStatus() with { ExternalPowerPresent = true });
        controller.AfterRefresh = () => coordinator.MarkUnavailable();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            applier.SetRefreshRateAsync(PowerSource.ExternalUnknown, "fixed-240"));
        Assert.Single(controller.Calls, call => call.StartsWith("refresh:", StringComparison.Ordinal));
    }

    private static ServiceStatusSnapshot UnclassifiedStatus()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        return new(1, true, false, [], null, [], "Unclassified external power", null, null, profiles,
            new(new(PerformancePreset.Balanced, SettingValueSource.ConfirmedFirmwareState),
                new(FanMode.Automatic, SettingValueSource.ConfirmedFirmwareState),
                new(profiles.PluggedIn.Lighting, SettingValueSource.ConfirmedFirmwareState),
                new(profiles.BatteryPolicy, true, false)),
            PowerSource.ExternalUnknown, null, null, null, null, false);
    }

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

        Assert.Equal(SessionDisplayOperationStatus.Indeterminate, result.Status);
        Assert.Equal(["query", "refresh:fixed-240"], controller.Calls);
        Assert.Equal("fixed-60", result.State.CurrentRefreshKey);
    }

    [Fact]
    public async Task BrightnessSelectsNearestSupportedLevelAndConfirmsReadback()
    {
        var context = ConfirmedContext();
        var controller = new FakeDisplayController(
            SessionDisplayIpcCodecTests.Snapshot("fixed-60", false, "native") with
            {
                BrightnessPercent = 0,
                SupportedBrightnessLevels = [0, 40, 60, 100],
            });
        using var applier = new DisplayProfileApplier(controller, context);

        SessionDisplayExecutionResult result = await applier.SetBrightnessAsync(
            PowerSource.PluggedIn,
            50);

        Assert.True(result.Succeeded);
        Assert.Equal<byte?>(40, result.State.BrightnessPercent);
        Assert.Equal(["query", "brightness:40"], controller.Calls);
    }

    [Theory]
    [InlineData(false, SessionDisplayOperationStatus.Failed)]
    [InlineData(true, SessionDisplayOperationStatus.Indeterminate)]
    public async Task MissingBrightnessPreservesEarlierSubmission(
        bool changeRefresh, SessionDisplayOperationStatus expectedStatus)
    {
        var context = ConfirmedContext();
        var controller = new FakeDisplayController(
            SessionDisplayIpcCodecTests.Snapshot("fixed-60", false, "native"));
        using var applier = new DisplayProfileApplier(controller, context);

        SessionDisplayExecutionResult result = await applier.ApplyProfileAsync(
            PowerSource.PluggedIn,
            new DisplayProfileSettings(changeRefresh ? "fixed-120" : null, null, null)
            {
                BrightnessPercent = 50,
            });

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(changeRefresh ? "fixed-120" : "fixed-60", result.State.CurrentRefreshKey);
        string[] expectedCalls = changeRefresh ? ["query", "refresh:fixed-120"] : ["query"];
        Assert.Equal(expectedCalls, controller.Calls);
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

        public Task<DisplayOperationResult> SetBrightnessAsync(
            byte percent,
            CancellationToken cancellationToken = default)
        {
            Enter();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Calls.Add($"brightness:{percent}");
                state = state with { BrightnessPercent = percent };
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
