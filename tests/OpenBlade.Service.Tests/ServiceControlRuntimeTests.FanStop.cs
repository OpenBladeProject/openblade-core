using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Fact]
    public async Task FanStop_IdleEntryLossDuringAdmissionPreventsMappedObservation()
    {
        TestContext? context = null;
        var backend = new FanStopControllerTests.Backend { DuringAdmission = () => context!.LowPowerGate.Enter() };
        context = CreateContext(true, fanStopBackend: backend);
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = true };
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.MaintainFanCurveAsync(default);
        Assert.Equal(["admission"], backend.Calls);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
    }

    [Fact]
    public async Task FanStop_ClearStartupThenUnownedDirectStopFencesOrdinaryWrites()
    {
        var backend = new FanStopControllerTests.Backend { Observed = FanStopObservedState.Stopped };
        TestContext context = CreateContext(true, fanStopBackend: backend);
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = true };
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.MaintainFanCurveAsync(default);
        Assert.Equal(FanStopState.RecoveryRequired, context.Runtime.GetStatus().FanStopStatus.State);
        await context.Runtime.SetFanControlOverrideAsync(new(FanMode.Automatic, OverrideLifetime.Persistent),
            FanControlSettings.Default, default);
        await context.Runtime.SetFanStopModeAsync(FanStopMode.Automatic, () => true, default);
        Assert.Equal(0, context.Device.TotalSetterCalls);
        Assert.Equal(["admission", "read"], backend.Calls);
    }

    [Fact]
    public async Task FanStop_HistoryIncludesAutoAfterEverySameCallCleanup()
    {
        TestContext? context = null;
        var backend = new FanStopControllerTests.Backend { AfterSubmission = () => context!.LowPowerGate.Enter() };
        context = CreateContext(true, fanStopBackend: backend);
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = true };
        await context.Runtime.ObserveAsync(default);
        for (int iteration = 0; iteration < 2; iteration++)
        {
            context.LowPowerGate.Exit();
            await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default);
        }
        string[] history = context.History.Snapshot().CommandResults
            .Select(result => result.Detail).OfType<string>()
            .Where(detail => detail.StartsWith("Fan Stop/Auto", StringComparison.Ordinal)).ToArray();
        Assert.Equal(4, history.Length);
        Assert.Contains("Stopped", history[0]);
        Assert.Contains("Automatic", history[1]);
        Assert.Contains("Stopped", history[2]);
        Assert.Contains("Automatic", history[3]);
    }

    [Fact]
    public async Task FanStop_UnresolvedStartupBlocksOrdinaryStartupReconciliation()
    {
        var backend = new FanStopControllerTests.Backend { StartupDisposition = FanStopStartupDisposition.Unresolved };
        TestContext context = CreateContext(true, fanStopBackend: backend);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with { HasFanStopTrialEvidence = true };
        Assert.Equal(FanStopState.RecoveryRequired, context.Runtime.GetStatus().FanStopStatus.State);
        await context.Runtime.ReconcileUnexpectedExitAsync(default);
        await context.Runtime.PrepareManualFanCurveRecoveryAsync(default);
        await context.Runtime.ReconcileStartupProfileAsync(default);
        Assert.Equal(0, context.Device.TotalSetterCalls);
        Assert.Empty(backend.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FanStop_IdleMaintenanceNeverQueuesBehindAnotherOperation(bool available)
    {
        var backend = available ? new FanStopControllerTests.Backend() : null;
        TestContext context = CreateContext(true, fanStopBackend: backend);
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = true };
        await context.Runtime.ObserveAsync(default);
        Task? maintenance = null;
        bool completedWhileGateHeld = false;
        context.Provider.AfterTryGet = () =>
        {
            if (maintenance is null)
            {
                maintenance = context.Runtime.MaintainFanCurveAsync(default);
                completedWhileGateHeld = maintenance.IsCompleted;
            }
        };
        await context.Runtime.ObserveAsync(default);
        Assert.True(completedWhileGateHeld);
        Assert.NotNull(maintenance);
        await maintenance;
        if (backend is not null)
        {
            Assert.Empty(backend.Calls);
        }
    }

    [Fact]
    public async Task FanStop_ShutdownClosesEntryBeforeQueuedRequestsCanStopFans()
    {
        var backend = new FanStopControllerTests.Backend();
        TestContext context = CreateContext(true, fanStopBackend: backend);
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = true };
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.PrepareForShutdownAsync(default);
        Assert.Equal(ControlOperationStatus.Blocked,
            (await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default)).Status);
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task FanStop_UserActivationCannotReopenStopAfterShutdownBegins()
    {
        var firstSid = new System.Security.Principal.SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var secondSid = new System.Security.Principal.SecurityIdentifier("S-1-5-21-1-2-3-1002");
        var repository = new RecordingUserConfigurationRepository(new Dictionary<string, ProfileSet>
        {
            [firstSid.Value] = ProfileDefaults.CreateSafeDefaults(),
            [secondSid.Value] = ProfileDefaults.CreateSafeDefaults(),
        }, directory);
        var backend = new FanStopControllerTests.Backend();
        TestContext context = CreateContext(true, fanStopBackend: backend, userConfigurationRepository: repository);
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = true };
        await context.Runtime.ActivateUserAsync(firstSid, default);
        await context.Runtime.PrepareForShutdownAsync(default);
        await context.Runtime.ActivateUserAsync(secondSid, default);
        Assert.Equal(ControlOperationStatus.Blocked,
            (await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default)).Status);
        Assert.Empty(backend.Calls);
    }

    [Fact]
    public async Task FanStop_LowPowerDuringResolvedSubmissionConfirmsAndExitsWithoutFence()
    {
        TestContext? context = null;
        var backend = new FanStopControllerTests.Backend { AfterSubmission = () => context!.LowPowerGate.Enter() };
        context = CreateContext(true, fanStopBackend: backend);
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = true };
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default);
        Assert.Equal(FanStopState.Automatic, context.Runtime.GetStatus().FanStopStatus.State);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        Assert.Equal(["Stop", "Automatic"], backend.Calls.Where(value => value is "Stop" or "Automatic"));
    }

    [Fact]
    public async Task FanStop_HyperBoostRequestHandsBackOwnedStopBeforeThermalEntry()
    {
        var backend = new FanStopControllerTests.Backend();
        TestContext context = CreateContext(true, fanStopBackend: backend,
            hyperBoostOwnershipJournalStore: new RecordingHyperBoostOwnershipJournalStore());
        ConfigureBothPadPathHyperBoost(context);
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = true };
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default);
        ControlOperationResult result = await context.Runtime.SetHyperBoostAsync(true, default);
        Assert.True(result.Status == ControlOperationStatus.Applied, result.Message);
        Assert.Equal(["Stop", "Automatic"], backend.Calls.Where(value => value is "Stop" or "Automatic"));
        Assert.Equal(1, context.Device.HyperBoostSetCalls);
    }

    [Fact]
    public async Task FanStop_CanceledConfirmedStopRetiresPriorManualOwnership()
    {
        using var cancellation = new CancellationTokenSource();
        var backend = new FanStopControllerTests.Backend { AfterSubmission = cancellation.Cancel };
        TestContext context = CreateContext(true, fanStopBackend: backend);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            HasFanStopTrialEvidence = true,
            SupportsDirectManualFanTransitions = true,
        };
        context.Device.ConfirmedState = new(PerformancePreset.Balanced, FanMode.Automatic,
            ConfirmedBattery(80), context.Runtime.GetStatus().Profiles.PluggedIn.Lighting)
        {
            HyperBoostActive = false,
        };
        await context.Runtime.ObserveAsync(default);
        var manual = new FanControlSettings(FanMode.Fixed, 4100, ManualFanCurve.Default);
        Assert.Equal(ControlOperationStatus.Applied,
            (await context.Runtime.SetFanControlOverrideAsync(new(FanMode.Fixed, OverrideLifetime.Persistent), manual, default)).Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, cancellation.Token));
        Assert.NotEqual(ExactFanControlOwnershipDisposition.OwnedExactIntent, context.Runtime.FanControlOwnershipDisposition);
        Assert.Equal(FanStopState.Automatic, context.Runtime.GetStatus().FanStopStatus.State);
    }

    [Fact]
    public async Task FanStop_KeyboardCancellationDoesNotConsumeAutoShutdownBudget()
    {
        using var keyboardCancellation = new CancellationTokenSource();
        var backend = new FanStopControllerTests.Backend();
        TestContext context = CreateContext(true, fanStopBackend: backend);
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = true };
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default);
        context.Device.DriverModeReleaseOverride = token =>
        {
            keyboardCancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult<bool?>(false);
        };
        await context.Runtime.PrepareForShutdownAsync(keyboardCancellation.Token);
        Assert.True(context.Device.DriverModeReleaseCalls > 0);
        Assert.Equal(FanStopState.Automatic, context.Runtime.GetStatus().FanStopStatus.State);
    }

    [Fact]
    public async Task FanStop_HistoryRecordsTransitionsAndCauseWithoutPeriodicSpam()
    {
        var backend = new FanStopControllerTests.Backend();
        TestContext context = CreateContext(true, fanStopBackend: backend);
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = true };
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default);
        await context.Runtime.MaintainFanCurveAsync(default);
        await context.Runtime.MaintainFanCurveAsync(default);
        Assert.Single(context.History.Snapshot().CommandResults,
            result => result.Detail?.Contains("Fan Stop/Auto Stopped", StringComparison.Ordinal) == true);
        backend.Failure = "uncertain";
        await context.Runtime.SetFanStopModeAsync(FanStopMode.Automatic, () => true, default);
        Assert.Single(context.History.Snapshot().CommandResults,
            result => result.Detail?.Contains("Fan Stop/Auto RecoveryRequired", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task FanStop_UnresolvedInitialReadFencesOrdinaryWritesBeforeAnyStopSubmission()
    {
        var backend = new FanStopControllerTests.Backend
        {
            DuringStateRead = () => throw new IOException("Simulated unresolved observation command."),
        };
        TestContext context = CreateContext(true, fanStopBackend: backend);
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = true };
        await context.Runtime.ObserveAsync(default);
        int setters = context.Device.TotalSetterCalls;
        await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default);
        Assert.True(context.Runtime.GetStatus().WritesBlocked);
        Assert.Equal(FanStopState.RecoveryRequired, context.Runtime.GetStatus().FanStopStatus.State);
        await context.Runtime.SetFanControlOverrideAsync(new(FanMode.Automatic, OverrideLifetime.Persistent),
            FanControlSettings.Default, default);
        await context.Runtime.MaintainFanCurveAsync(default);
        await context.Runtime.PrepareForShutdownAsync(default);
        Assert.Equal(setters, context.Device.TotalSetterCalls);
        Assert.Equal(["admission", "read"], backend.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FanStop_PublicationFailureStillReleasesTheServiceOperationGate(bool maintenance)
    {
        using var canceledSubmission = new CancellationTokenSource();
        var backend = new FanStopControllerTests.Backend
        {
            DuringSubmission = () => canceledSubmission.Cancel(),
        };
        TestContext context = CreateContext(true, fanStopBackend: backend);
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = true };
        await context.Runtime.ObserveAsync(default);
        context.Hub.BeforePublish = () => throw new IOException("Simulated subscription failure.");
        await Assert.ThrowsAsync<IOException>(async () =>
        {
            if (maintenance)
            {
                await context.Runtime.MaintainFanCurveAsync(default);
            }
            else
            {
                await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, canceledSubmission.Token);
            }
        });
        context.Hub.BeforePublish = null;
        using var nextRequest = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        ControlOperationResult result = await context.Runtime.SetFanStopModeAsync(
            FanStopMode.Automatic, () => true, nextRequest.Token);
        Assert.Contains(result.Status, new[] { ControlOperationStatus.Blocked, ControlOperationStatus.Stored });
    }

    [Fact]
    public async Task FanStop_RecoveryFencePreventsBrightnessReleaseAndLifecycleNeutralization()
    {
        var backend = new FanStopControllerTests.Backend { Failure = "uncertain" };
        var brightness = new RecordingDisplayBrightnessHidController();
        TestContext context = CreateContext(true, fanStopBackend: backend, displayBrightnessHidController: brightness);
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = true };
        await context.Runtime.ObserveAsync(default);
        Assert.Equal(ControlOperationStatus.Applied,
            (await context.Runtime.SetDisplayBrightnessHidUsageAsync(DisplayBrightnessHidUsage.Increment, default)).Status);
        int submitted = brightness.Usages.Count;
        await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default);
        Assert.Equal(ControlOperationStatus.Blocked,
            (await context.Runtime.SetDisplayBrightnessHidUsageAsync(DisplayBrightnessHidUsage.Released, default)).Status);
        Assert.Equal(ControlOperationStatus.Blocked,
            (await context.Runtime.SetDisplayBrightnessHidUsageAsync(DisplayBrightnessHidUsage.Decrement, default)).Status);
        await context.Runtime.MaintainDriverModeAsync(default);
        await context.Runtime.PrepareForShutdownAsync(default);
        context.LowPowerGate.Enter();
        await context.Runtime.PrepareForLowPowerAsync(context.LowPowerGate.Generation, () => true, default);
        Assert.Equal(submitted, brightness.Usages.Count);
        Assert.Equal(FanStopState.RecoveryRequired, context.Runtime.GetStatus().FanStopStatus.State);
    }

    [Fact]
    public async Task FanStop_DefaultBackendNeverWritesAndReportsDistinctUnavailableReason()
    {
        TestContext context = CreateContext(true);
        ServiceStatusSnapshot initial = context.Runtime.GetStatus();
        Assert.Same(initial, context.Runtime.GetStatus());
        Assert.Empty(context.Hub.Published);
        await context.Runtime.ObserveAsync(default);
        Assert.Equal(FanStopUnavailableReason.UnsupportedDevice,
            context.Runtime.GetStatus().FanStopStatus.UnavailableReason);
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = true };
        await context.Runtime.ObserveAsync(default);
        Assert.Same(context.Hub.Published[^1], context.Runtime.GetStatus());
        ProfileSet saved = context.Runtime.GetStatus().Profiles;
        int writes = context.Device.FanSetCalls;
        ControlOperationResult result = await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(FanStopUnavailableReason.RecoveryNotValidated,
            context.Runtime.GetStatus().FanStopStatus.UnavailableReason);
        Assert.Equal(writes, context.Device.FanSetCalls);
        Assert.Equal(saved, context.Runtime.GetStatus().Profiles);
    }

    [Fact]
    public async Task FanStop_PeriodicMaintenanceAdvertisesAdmissionAndReleasesOnThermalExit()
    {
        var backend = new FanStopControllerTests.Backend();
        TestContext context = CreateContext(true, fanStopBackend: backend);
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = true };
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.MaintainFanCurveAsync(default);
        Assert.True(context.Runtime.GetStatus().FanStopStatus.CanStop);
        ProfileSet saved = context.Runtime.GetStatus().Profiles;
        Assert.Equal(ControlOperationStatus.Applied,
            (await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default)).Status);
        backend.Admission = backend.Admission with { TemperatureSafe = false };
        await context.Runtime.MaintainFanCurveAsync(default);
        Assert.Equal(FanStopState.Automatic, context.Runtime.GetStatus().FanStopStatus.State);
        Assert.Equal(saved, context.Runtime.GetStatus().Profiles);
        Assert.Equal(["Stop", "Automatic"], backend.Calls.Where(value => value is "Stop" or "Automatic"));
    }

    [Fact]
    public async Task FanStop_ShutdownUsesOwnedAutoAndDoesNotReplayUncertainSubmission()
    {
        var backend = new FanStopControllerTests.Backend { Failure = "uncertain" };
        TestContext context = CreateContext(true, fanStopBackend: backend);
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = true };
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default);
        Assert.True(context.Runtime.GetStatus().WritesBlocked);
        int maintainCalls = context.Device.DriverModeEnsureCalls;
        int releaseCalls = context.Device.DriverModeReleaseCalls;
        await context.Runtime.MaintainDriverModeAsync(default);
        await context.Runtime.PrepareForShutdownAsync(default);
        Assert.Equal(maintainCalls, context.Device.DriverModeEnsureCalls);
        Assert.Equal(releaseCalls, context.Device.DriverModeReleaseCalls);
        Assert.DoesNotContain("Automatic", backend.Calls);
        Assert.Equal(FanStopState.RecoveryRequired, context.Runtime.GetStatus().FanStopStatus.State);
    }

    [Fact]
    public async Task FanStop_ConfirmedShutdownRestoresGenericAuto()
    {
        var backend = new FanStopControllerTests.Backend();
        TestContext context = CreateContext(true, fanStopBackend: backend);
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = true };
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default);
        backend.Admission = backend.Admission with { ProfileInScope = false, TemperatureSafe = false };
        await context.Runtime.PrepareForShutdownAsync(default);
        Assert.Equal(FanStopState.Automatic, context.Runtime.GetStatus().FanStopStatus.State);
        Assert.Equal(["Stop", "Automatic"], backend.Calls.Where(value => value is "Stop" or "Automatic"));
    }

    [Fact]
    public async Task FanStop_AutoKeepsSavedManualIntentWithoutReplayingItUntilNewFanIntent()
    {
        var backend = new FanStopControllerTests.Backend();
        TestContext context = CreateContext(true, fanStopBackend: backend);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            HasFanStopTrialEvidence = true,
            SupportsDirectManualFanTransitions = true,
        };
        context.Device.ConfirmedState = context.Device.ConfirmedState with { HyperBoostActive = false };
        await context.Runtime.ObserveAsync(default);
        var manual = new FanControlSettings(FanMode.Fixed, 4100, ManualFanCurve.Default);
        await context.Runtime.SetFanControlOverrideAsync(new(FanMode.Fixed, OverrideLifetime.Persistent), manual, default);
        await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default);
        await context.Runtime.SetFanStopModeAsync(FanStopMode.Automatic, () => true, default);
        int writes = context.Device.FanSetCalls;
        await context.Runtime.MaintainFanCurveAsync(default);
        // RunThermalConvergenceAsync is a lifetime worker, not an observation
        // tick. Check its admission without starting an uncancelled worker.
        Assert.False(context.Runtime.ShouldRunThermalConvergenceObservation());
        Assert.Equal(writes, context.Device.FanSetCalls);
        Assert.Equal(manual, context.Runtime.GetStatus().FanControlOverride);
        Assert.Equal(FanStopState.Automatic, context.Runtime.GetStatus().FanStopStatus.State);
        await context.Runtime.SetFanControlOverrideAsync(new(FanMode.Automatic, OverrideLifetime.Persistent),
            FanControlSettings.Default, default);
        Assert.Equal(FanStopState.Inactive, context.Runtime.GetStatus().FanStopStatus.State);
    }
}
