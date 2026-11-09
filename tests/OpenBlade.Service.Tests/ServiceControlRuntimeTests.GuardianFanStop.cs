using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task GuardianHeartbeatUsesOnlyCachedContinuationIncludingAfterReleaseAndLifecycleLoss(
        bool returnedToAuto, bool lowPower)
    {
        var owner = new GuardianFanStopBackendTests.Client();
        TestContext context = CreateContext(true,
            fanStopBackend: new GuardianFanStopBackend(owner));
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with { HasFanStopTrialEvidence = true };
        context.Device.ConfirmedState = context.Device.ConfirmedState with { Performance = PerformancePreset.Balanced };
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default);
        if (returnedToAuto)
        { await context.Runtime.SetFanStopModeAsync(FanStopMode.Automatic, () => true, default); }
        if (lowPower) { context.LowPowerGate.Enter(); }
        if (!returnedToAuto && !lowPower)
        {
            FanStopAdmission continuation = context.Runtime.ReadFanStopContinuationAdmission();
            Assert.True(continuation.CanStop, System.Text.Json.JsonSerializer.Serialize(continuation));
        }
        int stateReads = context.Device.StateReadCalls;
        int thermalReads = context.Device.PerformanceFanStateReadCalls;
        int adapterReads = context.Device.AdapterPowerReadCalls;
        int entryCalls = 0;
        await using var client = new WindowsFanStopGuardianClient(directory,
            _ => { entryCalls++; throw new InvalidOperationException("The background heartbeat invoked gated entry admission."); },
            context.Runtime.ReadFanStopContinuationAdmission);
        using var stopped = new CancellationTokenSource();
        using var stream = new CancelAfterHeartbeatStream(stopped);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PumpAsync(Guid.NewGuid(), stream, stopped.Token));
        stream.Position = 0;
        FanStopGuardianCommand command = await FanStopGuardianTransport.ReadAsync<FanStopGuardianCommand>(stream, default);
        Assert.Equal(returnedToAuto || lowPower ? FanStopGuardianCommandKind.Automatic
            : FanStopGuardianCommandKind.Heartbeat, command.Kind);
        Assert.Null(command.ObservedPowerSource);
        Assert.Equal(0, entryCalls);
        Assert.Equal(stateReads, context.Device.StateReadCalls);
        Assert.Equal(thermalReads, context.Device.PerformanceFanStateReadCalls);
        Assert.Equal(adapterReads, context.Device.AdapterPowerReadCalls);
    }

    [Fact]
    public async Task GuardianContinuationForNewRunDoesNotReusePriorPublishedAutoAsAnEntryRefusal()
    {
        var client = new GuardianFanStopBackendTests.Client();
        TestContext context = CreateContext(true,
            fanStopBackend: new GuardianFanStopBackend(client));
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with { HasFanStopTrialEvidence = true };
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default);
        await context.Runtime.SetFanStopModeAsync(FanStopMode.Automatic, () => true, default);
        Assert.False(context.Runtime.ReadFanStopContinuationAdmission().CanStop);
        client.Start = (_, _) =>
        {
            Assert.True(context.Runtime.ReadFanStopContinuationAdmission().CanStop);
            return Task.FromResult(FanStopSubmission.Submitted);
        };
        await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default);
        Assert.Equal(2, client.StartCalls);
    }

    private sealed class CancelAfterHeartbeatStream(CancellationTokenSource stopped) : MemoryStream
    {
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            stopped.Cancel();
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public async Task GuardianCapabilityProjectionUsesBackendAndExactEvidenceWhileFreshAdmissionControlsCanStop(
        bool available, bool trialEvidence, bool sourceEvidence, bool temperatureSafe)
    {
        bool exactEvidence = trialEvidence || sourceEvidence;
        var client = new GuardianFanStopBackendTests.Client
        { Admission = GuardianFanStopBackendTests.Client.SafeAdmission with { TemperatureSafe = temperatureSafe } };
        IFanStopBackend backend = available ? new GuardianFanStopBackend(client) : UnavailableFanStopBackend.Instance;
        TestContext context = CreateContext(true, fanStopBackend: backend);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            HasFanStopTrialEvidence = trialEvidence,
            HasFanStopSourceEvidence = sourceEvidence,
            // A stale/raw flag must neither enable an unsupported model nor hide
            // a backend admitted by this runtime's explicit composition.
            SupportsFanStop = !available || !exactEvidence,
        };
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.MaintainFanCurveAsync(default);
        ServiceStatusSnapshot projected = context.Runtime.GetStatus();
        Assert.Equal(available && exactEvidence, projected.Capabilities!.SupportsFanStop);
        Assert.Equal(trialEvidence, projected.Capabilities.HasFanStopTrialEvidence);
        Assert.Equal(sourceEvidence, projected.Capabilities.HasFanStopSourceEvidence);
        Assert.Equal(available && exactEvidence && temperatureSafe, projected.FanStopStatus.CanStop);
        Assert.Equal(!available || !exactEvidence, context.Device.Capabilities.SupportsFanStop);
        Assert.Same(projected, context.Runtime.GetStatus());
        Assert.Same(projected.Capabilities, context.Runtime.GetStatus().Capabilities);
    }

    [Fact]
    public async Task GuardianNotificationPublishesAutonomousAutoWithoutFanCurveMaintenanceOrAnotherWrite()
    {
        var client = new GuardianFanStopBackendTests.Client();
        TestContext context = CreateContext(true, initialPowerSource: PowerSource.UsbC,
            fanStopBackend: new GuardianFanStopBackend(client));
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = true };
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default);
        client.Complete();
        client.Notify();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (context.Runtime.GetStatus().FanStopStatus.State != FanStopState.Automatic)
        { await Task.Delay(10, deadline.Token); }
        Assert.Equal(0, client.AutoCalls);
        Assert.Equal(1, context.Provider.GuardianLease.Disposals);
        Assert.Contains(context.History.Snapshot().CommandResults,
            item => item.Detail?.Contains("Fan Stop/Auto Automatic", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task GuardianShutdownAttemptsKeyboardBeforeAutoAndRetriesOnlyAfterRelease()
    {
        var client = new GuardianFanStopBackendTests.Client();
        TestContext context = CreateContext(true,
            fanStopBackend: new GuardianFanStopBackend(client));
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = true };
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default);
        context.Device.DriverModeReleaseOverride = token =>
        {
            Assert.Equal(1, client.AutoCalls);
            Assert.Equal(1, context.Provider.GuardianLease.Disposals);
            return Task.FromResult<bool?>(false);
        };
        await context.Runtime.PrepareForShutdownAsync(default);
        Assert.Equal(1, client.AutoCalls);
        Assert.Equal(1, context.Device.DriverModeReleaseCalls);
        Assert.Equal(FanStopState.Automatic, context.Runtime.GetStatus().FanStopStatus.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuardianShutdownRestoresCoolingPadAfterOwnedAutoAndRetainsPadFailure(bool failPadRestore)
    {
        var client = new GuardianFanStopBackendTests.Client();
        var pad = new LifecyclePad();
        var baseline = await pad.ReadSnapshotAsync(default);
        var user = new System.Security.Principal.SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var repository = new RecordingUserConfigurationRepository(
            new Dictionary<string, ProfileSet> { [user.Value] = ProfileDefaults.CreateSafeDefaults() }, directory);
        TestContext context = CreateContext(true,
            userConfigurationRepository: repository,
            fanStopBackend: new GuardianFanStopBackend(client),
            coolingPadDeviceProvider: new LifecyclePadProvider(pad));
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = true };
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.ActivateUserAsync(user, () => true, default);
        ControlOperationResult applied = await context.Runtime.SetCoolingPadSettingsAsync(
            new CoolingPadSettings { FanMode = CoolingPadFanMode.Fixed, FixedRpm = 1500 }, user, () => true, default);
        Assert.Equal(ControlOperationStatus.Applied, applied.Status);
        Assert.True(context.Runtime.GetStatus().CoolingPad.Active);
        ControlOperationResult stopped = await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default);
        Assert.Equal(ControlOperationStatus.Applied, stopped.Status);
        Assert.Equal(FanStopState.Stopped, context.Runtime.GetStatus().FanStopStatus.State);
        pad.Restore = token =>
        {
            // The independent accessory must wait for confirmed laptop Auto and
            // native ownership release, then restore even on the guardian path.
            Assert.Equal(1, client.AutoCalls);
            Assert.Equal(1, context.Provider.GuardianLease.Disposals);
            Assert.Equal(FanStopState.Automatic, context.Runtime.GetStatus().FanStopStatus.State);
            if (failPadRestore)
            {
                throw new CoolingPadOperationException("Synthetic pad restoration failure.", true);
            }
            return Task.CompletedTask;
        };

        ControlOperationResult result = await context.Runtime.PrepareForShutdownAsync(default);

        Assert.Equal(1, client.AutoCalls);
        Assert.Equal(1, context.Provider.GuardianLease.Disposals);
        Assert.Equal(FanStopState.Automatic, context.Runtime.GetStatus().FanStopStatus.State);
        Assert.Equal(1, pad.RestoreCalls);
        Assert.False(context.Runtime.GetStatus().CoolingPad.Active);
        Assert.Equal(failPadRestore, context.Runtime.GetStatus().CoolingPad.RecoveryRequired);
        Assert.Equal(failPadRestore, context.Runtime.GetStatus().CoolingPad.WritesPaused);
        Assert.Equal(failPadRestore ? ControlOperationStatus.Failed : ControlOperationStatus.Applied, result.Status);
        if (!failPadRestore)
        {
            Assert.Equal(baseline, await pad.ReadSnapshotAsync(default));
        }
    }

    [Fact]
    public async Task GuardianOwnedStopBlocksOrdinaryObservationAndDriverMaintenance()
    {
        var client = new GuardianFanStopBackendTests.Client();
        TestContext context = CreateContext(true,
            fanStopBackend: new GuardianFanStopBackend(client));
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = true };
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default);
        int reads = context.Device.StateReadCalls;
        int writes = context.Device.TotalSetterCalls;
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.MaintainDriverModeAsync(default);
        Assert.Equal(reads, context.Device.StateReadCalls);
        Assert.Equal(writes, context.Device.TotalSetterCalls);
        Assert.False(context.Provider.RecoveryWriteAdmission!());
    }

    [Theory]
    [InlineData(PowerSource.PluggedIn, PowerSource.UsbC, false)]
    [InlineData(PowerSource.UsbC, PowerSource.Battery, false)]
    [InlineData(PowerSource.UsbC, PowerSource.PluggedIn, true)]
    public async Task GuardianPowerDecreaseRetainsStopWhileIncreaseHandsBackBeforeDestination(
        PowerSource initial, PowerSource destination, bool automatic)
    {
        var client = new GuardianFanStopBackendTests.Client();
        TestContext context = CreateContext(true, initialPowerSource: initial,
            fanStopBackend: new GuardianFanStopBackend(client));
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = true };
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default);
        int writes = context.Device.TotalSetterCalls;
        context.Telemetry.Current = context.Telemetry.Current with { PowerSource = destination };
        await context.Runtime.HandlePowerSourceChangedAsync(default);
        Assert.Equal(automatic ? 1 : 0, client.AutoCalls);
        if (!automatic)
        {
            Assert.Equal(FanStopState.Stopped, context.Runtime.GetStatus().FanStopStatus.State);
            Assert.Equal(writes, context.Device.TotalSetterCalls);
            Assert.Equal(0, context.Provider.GuardianLease.Disposals);
        }
    }

    [Fact]
    public async Task GuardianDelegationBlocksSeparateBrightnessHidController()
    {
        var client = new GuardianFanStopBackendTests.Client();
        var brightness = new RecordingDisplayBrightnessHidController();
        TestContext context = CreateContext(true, displayBrightnessHidController: brightness,
            fanStopBackend: new GuardianFanStopBackend(client));
        context.Device.Capabilities = context.Device.Capabilities with { HasFanStopTrialEvidence = true };
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default);
        foreach (DisplayBrightnessHidUsage usage in Enum.GetValues<DisplayBrightnessHidUsage>())
        {
            ControlOperationResult result = await context.Runtime.SetDisplayBrightnessHidUsageAsync(usage, default);
            Assert.Equal(ControlOperationStatus.Blocked, result.Status);
            Assert.Equal(ServiceControlRuntime.GuardianHardwareBlockedMessage, result.Message);
        }
        Assert.Empty(brightness.Usages);
    }

    [Fact]
    public async Task GuardianExplicitFanModeChangeHandsBackAndReleasesBeforeDestinationWrite()
    {
        var client = new GuardianFanStopBackendTests.Client();
        TestContext context = CreateContext(true,
            fanStopBackend: new GuardianFanStopBackend(client));
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with { HasFanStopTrialEvidence = true };
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default);
        ControlOperationResult result = await context.Runtime.SetFanControlOverrideAsync(
            new(FanMode.Maximum, OverrideLifetime.Persistent), FanControlSettings.FromLegacy(FanMode.Maximum), default);
        Assert.True(result.Status == ControlOperationStatus.Applied, result.Message);
        Assert.Equal(1, client.AutoCalls);
        Assert.Equal(1, context.Provider.GuardianLease.Disposals);
        Assert.True(context.Device.TotalSetterCalls > 0);
    }

    [Theory]
    [InlineData(PerformancePreset.Silent, PerformancePreset.Balanced)]
    [InlineData(PerformancePreset.Balanced, PerformancePreset.Silent)]
    public async Task GuardianExplicitPerformanceChangeEndsStopEvenWhenBackendAdmitsBothProfiles(
        PerformancePreset origin, PerformancePreset destination)
    {
        // This fake admits both profiles so the reason for Auto is the explicit
        // mode change, not a loss of the backend's Stop-entry capability.
        var client = new GuardianFanStopBackendTests.Client();
        TestContext context = CreateContext(true,
            fanStopBackend: new GuardianFanStopBackend(client));
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with { HasFanStopTrialEvidence = true };
        context.Device.ConfirmedState = context.Device.ConfirmedState with { Performance = origin };
        await context.Runtime.ObserveAsync(default);
        await context.Runtime.SetFanStopModeAsync(FanStopMode.Stop, () => true, default);
        ControlOperationResult result = await context.Runtime.SetPerformanceOverrideAsync(
            new(destination, OverrideLifetime.Persistent), default);
        Assert.True(result.Status == ControlOperationStatus.Applied, result.Message);
        Assert.Equal(1, client.AutoCalls);
        Assert.Equal(1, context.Provider.GuardianLease.Disposals);
        Assert.Equal(destination, context.Device.ConfirmedState.Performance);
        Assert.Equal(1, client.StartCalls);
    }
}
