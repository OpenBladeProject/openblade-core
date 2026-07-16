using Microsoft.Extensions.Logging.Abstractions;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ServiceControlRuntimeTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.Runtime.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Observe_ReadsCapabilitiesAndStateOnceWithoutSetters()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Performance,
            FanMode.Maximum,
            BatteryPolicy.Create(75),
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting with { Brightness = 25 });

        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(1, context.Device.CapabilitiesReadCalls);
        Assert.Equal(1, context.Device.StateReadCalls);
        Assert.Equal(0, context.Device.TotalSetterCalls);
        Assert.Equal(context.Device.Capabilities, context.Runtime.GetStatus().Capabilities);
        Assert.Equal(context.Device.ConfirmedState, context.Runtime.GetStatus().ConfirmedState);
    }

    [Fact]
    public async Task FirstObservation_ProfileMismatchEstablishesBaselineWithoutWarning()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Performance,
            FanMode.Maximum,
            BatteryPolicy.Create(65),
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting with { Brightness = 15 });

        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task LaterExternalChanges_UpdateConfirmedStateAndLatchChangedSubsystems()
    {
        TestContext context = CreateContext(providerAvailable: true);
        LightingSettings lighting = ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            BatteryPolicy.Create(80),
            lighting);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Performance,
            FanMode.Maximum,
            BatteryPolicy.Create(75),
            lighting with { Brightness = 30 });
        await context.Runtime.ObserveAsync(CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(context.Device.ConfirmedState, status.ConfirmedState);
        Assert.Equal(
            [
                DeviceSubsystem.Performance,
                DeviceSubsystem.Fans,
                DeviceSubsystem.Battery,
                DeviceSubsystem.Lighting,
            ],
            status.ExternalChanges);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task CancellationAfterReads_DoesNotPublishOrAdvanceDriftBaseline()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            null,
            FanMode.Automatic,
            null,
            null);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ServiceStatusSnapshot baseline = context.Runtime.GetStatus();
        using var cancellation = new CancellationTokenSource();
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Fans = FanMode.Maximum,
        };
        context.Device.AfterReadState = cancellation.Cancel;

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => context.Runtime.ObserveAsync(cancellation.Token));

        Assert.Same(baseline, context.Runtime.GetStatus());
        context.Device.AfterReadState = null;
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Fans = FanMode.Automatic,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
    }

    [Fact]
    public async Task CancellationDuringProviderProbe_DoesNotPublishUnavailableState()
    {
        TestContext context = CreateContext(providerAvailable: false);
        ServiceStatusSnapshot before = context.Runtime.GetStatus();
        using var cancellation = new CancellationTokenSource();
        context.Provider.AfterTryGet = cancellation.Cancel;

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => context.Runtime.ObserveAsync(cancellation.Token));

        Assert.Same(before, context.Runtime.GetStatus());
        Assert.Empty(context.Hub.Published);
    }

    [Fact]
    public async Task FailedAndUnavailableObservation_PreserveConfirmedStateAndCapabilities()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            BatteryPolicy.Create(80),
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        DeviceCapabilities capabilities = context.Runtime.GetStatus().Capabilities!;
        ConfirmedDeviceState confirmed = context.Runtime.GetStatus().ConfirmedState!;

        context.Device.ReadStateFailure = new IOException("device vanished");
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.False(context.Runtime.GetStatus().DeviceVerified);
        Assert.True(context.Runtime.GetStatus().WritesBlocked);
        Assert.Same(capabilities, context.Runtime.GetStatus().Capabilities);
        Assert.Same(confirmed, context.Runtime.GetStatus().ConfirmedState);

        context.Device.ReadStateFailure = null;
        context.Provider.Available = false;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Same(capabilities, context.Runtime.GetStatus().Capabilities);
        Assert.Same(confirmed, context.Runtime.GetStatus().ConfirmedState);
    }

    [Fact]
    public async Task BlockedIntentionalReconciliation_PreservesLatchedExternalChanges()
    {
        TestContext context = CreateContext(providerAvailable: true);
        await LatchFanDriftAsync(context);
        context.Guard.SetScript(new HardwareAccessDecision(["RazerAppEngine"], null));

        ControlOperationResult result = await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal([DeviceSubsystem.Fans], context.Runtime.GetStatus().ExternalChanges);
    }

    [Fact]
    public async Task UnavailableIntentionalReconciliation_PreservesLatchedExternalChanges()
    {
        TestContext context = CreateContext(providerAvailable: true);
        await LatchFanDriftAsync(context);
        context.Provider.Available = false;

        ControlOperationResult result = await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal([DeviceSubsystem.Fans], context.Runtime.GetStatus().ExternalChanges);
    }

    [Fact]
    public async Task ThrowingIntentionalReconciliation_PreservesLatchedExternalChanges()
    {
        TestContext context = CreateContext(providerAvailable: true);
        await LatchFanDriftAsync(context);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        context.Device.ReadStateFailure = new IOException("device vanished");

        ControlOperationResult result = await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Equal([DeviceSubsystem.Fans], context.Runtime.GetStatus().ExternalChanges);
    }

    [Fact]
    public async Task SuccessfulObservationWithEmptyCapabilities_DoesNotBlockWrites()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.Capabilities = DeviceCapabilities.Unavailable();

        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.True(context.Runtime.GetStatus().DeviceVerified);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
    }

    [Fact]
    public async Task ControllerPresenceRefresh_IsAdvisoryBoundedAndDoesNotTouchHidOrTelemetry()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            BatteryPolicy.Create(80),
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ServiceStatusSnapshot before = context.Runtime.GetStatus();
        int telemetryCaptures = context.Telemetry.CaptureCount;
        int providerCalls = context.Provider.TryGetCount;
        int deviceCalls = context.Device.TotalCalls;
        context.Monitor.Current = new ControllerPresenceObservation(
            [
                "razerappengine",
                "RazerAppEngine",
                .. Enumerable.Range(0, 17).Select(index => $"ZController{index:D2}"),
            ],
            "enumeration warning");

        await context.Runtime.RefreshControllerPresenceAsync(CancellationToken.None);

        ServiceStatusSnapshot after = context.Runtime.GetStatus();
        Assert.Equal(16, after.PotentialControllers.Count);
        Assert.Equal(
            after.PotentialControllers.OrderBy(name => name, StringComparer.Ordinal),
            after.PotentialControllers);
        Assert.Equal(
            after.PotentialControllers.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            after.PotentialControllers.Count);
        Assert.Contains("RazerAppEngine", after.PotentialControllers);
        Assert.Equal("enumeration warning", after.ControllerDetectionWarning);
        Assert.Equal(before.WritesBlocked, after.WritesBlocked);
        Assert.Equal(before.DeviceVerified, after.DeviceVerified);
        Assert.Same(before.Capabilities, after.Capabilities);
        Assert.Same(before.ConfirmedState, after.ConfirmedState);
        Assert.Same(before.Telemetry, after.Telemetry);
        Assert.Same(before.DesiredState, after.DesiredState);
        Assert.Equal(before.Message, after.Message);
        Assert.Equal(before.ExternalChanges, after.ExternalChanges);
        Assert.Equal(telemetryCaptures, context.Telemetry.CaptureCount);
        Assert.Equal(providerCalls, context.Provider.TryGetCount);
        Assert.Equal(deviceCalls, context.Device.TotalCalls);
        Assert.Equal(1, context.Monitor.ObserveCount);
    }

    [Fact]
    public async Task DuplicatePowerNotification_ObservesWithoutSettersOrConsumingLaterTransition()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            BatteryPolicy.Create(80),
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(1, context.Device.CapabilitiesReadCalls);
        Assert.Equal(1, context.Device.StateReadCalls);
        Assert.Equal(0, context.Device.TotalSetterCalls);

        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(2, context.Provider.TryGetCount);
        Assert.Equal(1, context.Device.PerformanceSetCalls);
    }

    [Fact]
    public async Task TransitionalRefreshMethods_RouteToZeroWriteReplacements()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Performance,
            FanMode.Maximum,
            BatteryPolicy.Create(70),
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting with { Brightness = 10 });

        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(2, context.Device.TotalCalls);
        Assert.Equal(0, context.Device.TotalSetterCalls);

        int telemetryCaptures = context.Telemetry.CaptureCount;
        context.Monitor.Current = new ControllerPresenceObservation(["RazerAppEngine"], null);
        await context.Runtime.RefreshControllerPresenceAsync(CancellationToken.None);
        Assert.Equal(1, context.Monitor.ObserveCount);
        Assert.Equal(telemetryCaptures, context.Telemetry.CaptureCount);
        Assert.Equal(2, context.Device.TotalCalls);
    }

    [Fact]
    public async Task UnavailableProvider_BlocksEveryOperationWithoutDeviceCalls()
    {
        TestContext context = CreateContext(providerAvailable: false);
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults() with
        {
            BatteryPolicy = BatteryPolicy.Create(75),
        };

        ControlOperationResult[] results =
        [
            await context.Runtime.UpdateProfilesAsync(profiles, CancellationToken.None),
            await context.Runtime.SetPerformanceOverrideAsync(
                new ManualOverride<PerformancePreset>(PerformancePreset.Performance, OverrideLifetime.Persistent),
                CancellationToken.None),
            await context.Runtime.SetFanOverrideAsync(
                new ManualOverride<FanMode>(FanMode.Maximum, OverrideLifetime.Persistent),
                CancellationToken.None),
            await context.Runtime.SetLightingOverrideAsync(null, CancellationToken.None),
            await context.Runtime.SetBatteryPolicyAsync(BatteryPolicy.Create(70), CancellationToken.None),
            await context.Runtime.SetFullChargeOverrideAsync(true, CancellationToken.None),
        ];
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, results[0].Status);
        Assert.All(results[1..], result => Assert.Equal(ControlOperationStatus.Blocked, result.Status));
        Assert.Contains("stored", results[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, context.Device.TotalCalls);
        Assert.False(context.Runtime.GetStatus().DeviceVerified);
        Assert.True(context.Runtime.GetStatus().WritesBlocked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyGuardConflictOrDetectionFailure_DoesNotBlockWriteOrObservation(
        bool detectionFailure)
    {
        TestContext context = CreateWritableContext(
            conflicts: detectionFailure ? [] : ["RazerAppEngine"],
            detectionFailure: detectionFailure ? "enumeration failed" : null);

        ControlOperationResult result = await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(1, context.Provider.TryGetCount);
        Assert.Equal(4, context.Device.TotalCalls);

        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(2, context.Provider.TryGetCount);
        Assert.Equal(6, context.Device.TotalCalls);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
    }

    [Fact]
    public async Task LegacyGuardConflictAppearingDuringAdmission_IsIgnored()
    {
        TestContext context = CreateWritableContext();
        context.Guard.SetScript(
            new HardwareAccessDecision([], null),
            new HardwareAccessDecision(["RazerAppEngine"], null));

        ControlOperationResult result = await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(1, context.Provider.TryGetCount);
        Assert.Equal(4, context.Device.TotalCalls);
    }

    [Fact]
    public async Task Refresh_ObservesVerifiedDeviceDespiteStrictGuardConflict()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            BatteryPolicy.Create(80),
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting);
        context.Guard.SetScript(new HardwareAccessDecision(["RazerAppEngine"], null));

        await context.Runtime.ObserveAsync(CancellationToken.None);

        ServiceStatusSnapshot observed = context.Runtime.GetStatus();
        Assert.True(observed.DeviceVerified);
        Assert.False(observed.WritesBlocked);
        Assert.Equal(1, context.Provider.TryGetCount);
        Assert.Equal(2, context.Device.TotalCalls);
        Assert.Equal(0, context.Device.PerformanceSetCalls);
        Assert.Equal(0, context.Device.FanSetCalls);
        Assert.Equal(0, context.Device.BatterySetCalls);
        Assert.Equal(0, context.Device.LightingSetCalls);
    }

    [Fact]
    public async Task ConflictRefresh_WhenSafeAndUnchanged_DoesNotTouchTelemetryOrDevice()
    {
        TestContext context = CreateContext(providerAvailable: true);
        int telemetryCaptures = context.Telemetry.CaptureCount;
        long revision = context.Runtime.GetStatus().Revision;

        await context.Runtime.RefreshControllerPresenceAsync(CancellationToken.None);

        Assert.Equal(telemetryCaptures, context.Telemetry.CaptureCount);
        Assert.Equal(0, context.Provider.TryGetCount);
        Assert.Equal(0, context.Device.TotalCalls);
        Assert.Equal(revision, context.Runtime.GetStatus().Revision);
        Assert.Empty(context.Hub.Published);
    }

    [Fact]
    public async Task ConflictRefresh_PublishesConflictChangesWithoutTouchingTelemetryOrDevice()
    {
        TestContext context = CreateContext(providerAvailable: true);
        int telemetryCaptures = context.Telemetry.CaptureCount;

        context.Monitor.Current = new ControllerPresenceObservation(["RazerAppEngine"], null);
        await context.Runtime.RefreshControllerPresenceAsync(CancellationToken.None);
        ServiceStatusSnapshot conflict = context.Runtime.GetStatus();

        context.Monitor.Current = new ControllerPresenceObservation(
            ["RazerAppEngine"],
            "process enumeration failed");
        await context.Runtime.RefreshControllerPresenceAsync(CancellationToken.None);
        ServiceStatusSnapshot detectionFailure = context.Runtime.GetStatus();

        Assert.Equal(["RazerAppEngine"], conflict.PotentialControllers);
        Assert.Null(conflict.ControllerDetectionWarning);
        Assert.Empty(conflict.ExternalChanges);
        Assert.Equal("No verified PID 02E0 device is available.", conflict.Message);
        Assert.Equal(conflict.Revision + 1, detectionFailure.Revision);
        Assert.Equal(
            "process enumeration failed",
            detectionFailure.ControllerDetectionWarning);
        Assert.Equal(conflict.Message, detectionFailure.Message);
        Assert.Equal(telemetryCaptures, context.Telemetry.CaptureCount);
        Assert.Equal(0, context.Provider.TryGetCount);
        Assert.Equal(0, context.Device.TotalCalls);
    }

    [Fact]
    public async Task ConflictRefresh_WhenControllerClears_NeverReconciles()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            BatteryPolicy.Create(80),
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        context.Monitor.Current = new ControllerPresenceObservation(["RazerAppEngine"], null);
        await context.Runtime.RefreshControllerPresenceAsync(CancellationToken.None);
        int telemetryCapturesWhileBlocked = context.Telemetry.CaptureCount;
        int providerCallsWhileBlocked = context.Provider.TryGetCount;
        int deviceCallsWhileBlocked = context.Device.TotalCalls;

        context.Monitor.Current = new ControllerPresenceObservation([], null);
        await context.Runtime.RefreshControllerPresenceAsync(CancellationToken.None);
        await context.Runtime.RefreshControllerPresenceAsync(CancellationToken.None);

        Assert.True(context.Runtime.GetStatus().DeviceVerified);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        Assert.Equal(telemetryCapturesWhileBlocked, context.Telemetry.CaptureCount);
        Assert.Equal(providerCallsWhileBlocked, context.Provider.TryGetCount);
        Assert.Equal(deviceCallsWhileBlocked, context.Device.TotalCalls);
        Assert.Equal(0, context.Device.PerformanceSetCalls);
        Assert.Equal(0, context.Device.FanSetCalls);
        Assert.Equal(0, context.Device.BatterySetCalls);
        Assert.Equal(0, context.Device.LightingSetCalls);
    }

    [Fact]
    public async Task LifecycleRefresh_WithUnsafeGuard_UsesReadOnlyObservation()
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            conflicts: ["RazerAppEngine"]);
        var loop = new ServiceLifecycleRefreshLoop(
            context.Runtime,
            new SingleLifecycleEventSource(ServiceLifecycleTrigger.DeviceArrived),
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);

        await loop.RunAsync(CancellationToken.None);

        Assert.Equal(1, context.Provider.TryGetCount);
        Assert.Equal(2, context.Device.TotalCalls);
        Assert.True(context.Runtime.GetStatus().DeviceVerified);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task DetectionFailure_PreservesSortedExactAdvisoryNamesWithoutBlockingMessage()
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            conflicts: ["Razer Synapse Service", "Razer Central Service"],
            detectionFailure: "Windows service enumeration failed.");

        await context.Runtime.RefreshControllerPresenceAsync(CancellationToken.None);
        ServiceStatusSnapshot status = context.Runtime.GetStatus();

        Assert.Equal(
            ["Razer Central Service", "Razer Synapse Service"],
            status.PotentialControllers);
        Assert.Equal(
            "Windows service enumeration failed.",
            status.ControllerDetectionWarning);
        Assert.Empty(status.ExternalChanges);
        Assert.Equal("No verified PID 02E0 device is available.", status.Message);
        Assert.Equal(0, context.Provider.TryGetCount);
        Assert.Equal(0, context.Device.TotalCalls);
    }

    [Fact]
    public async Task UpdateProfiles_AtomicallyPersistsValidIntentWhileUnavailable()
    {
        TestContext context = CreateContext(providerAvailable: false);
        ProfileSet expected = ProfileDefaults.CreateSafeDefaults() with
        {
            BatteryPolicy = BatteryPolicy.Create(75),
        };

        ControlOperationResult result = await context.Runtime.UpdateProfilesAsync(
            expected,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, result.Status);
        Assert.Equal(expected, (await context.Store.LoadAsync(CancellationToken.None)).Profiles);
        Assert.Equal(expected, context.Runtime.GetStatus().Profiles);
        Assert.Equal(0, context.Provider.TryGetCount);
        Assert.Equal(0, context.Device.TotalCalls);
    }

    [Fact]
    public async Task ScopedActions_CallOnlyTheirNamedSetter()
    {
        TestContext performance = CreateWritableContext();
        await performance.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);
        Assert.Equal([1, 0, 0, 0], SetterCounts(performance.Device));

        TestContext fans = CreateWritableContext();
        await fans.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Maximum, OverrideLifetime.Persistent),
            CancellationToken.None);
        Assert.Equal([0, 1, 0, 0], SetterCounts(fans.Device));

        TestContext lighting = CreateWritableContext();
        LightingSettings target = ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting;
        await lighting.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(target, OverrideLifetime.Persistent),
            CancellationToken.None);
        Assert.Equal([0, 0, 0, 1], SetterCounts(lighting.Device));

        TestContext battery = CreateWritableContext();
        await battery.Runtime.SetBatteryPolicyAsync(
            BatteryPolicy.Create(75),
            CancellationToken.None);
        Assert.Equal([0, 0, 1, 0], SetterCounts(battery.Device));
    }

    [Fact]
    public async Task ClearingFanOverride_AppliesOnlyResolvedFanTarget()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Maximum, OverrideLifetime.Persistent),
            CancellationToken.None);
        int fanCalls = context.Device.FanSetCalls;

        ControlOperationResult result = await context.Runtime.SetFanOverrideAsync(
            null,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(fanCalls + 1, context.Device.FanSetCalls);
        Assert.Equal(0, context.Device.PerformanceSetCalls);
        Assert.Equal(0, context.Device.BatterySetCalls);
        Assert.Equal(0, context.Device.LightingSetCalls);
    }

    [Fact]
    public async Task ControllerPresenceWarning_RemainsVisibleAndDoesNotBlockScopedWrite()
    {
        TestContext context = CreateWritableContext(
            conflicts: ["RazerAppEngine"],
            detectionFailure: "enumeration warning");
        await context.Runtime.RefreshControllerPresenceAsync(CancellationToken.None);

        ControlOperationResult result = await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(1, context.Device.PerformanceSetCalls);
        Assert.Equal(["RazerAppEngine"], context.Runtime.GetStatus().PotentialControllers);
        Assert.Equal("enumeration warning", context.Runtime.GetStatus().ControllerDetectionWarning);
    }

    [Fact]
    public async Task ScopedMismatch_PreservesConfirmedSnapshotAndLatchesOnlyNamedDrift()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Fans = FanMode.Maximum,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ConfirmedDeviceState before = context.Runtime.GetStatus().ConfirmedState!;
        context.Device.SuppressConfirmationFor = DeviceSubsystem.Performance;

        ControlOperationResult result = await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Same(before, context.Runtime.GetStatus().ConfirmedState);
        Assert.Equal(
            [DeviceSubsystem.Performance, DeviceSubsystem.Fans],
            context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(1, context.Device.PerformanceSetCalls);
    }

    [Fact]
    public async Task ScopedReadbackFailure_PreservesConfirmedAndLatchesOnlyNamedDriftWithoutRetry()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ConfirmedDeviceState before = context.Runtime.GetStatus().ConfirmedState!;
        context.Device.ReadStateFailureOnCall = context.Device.StateReadCalls + 2;

        ControlOperationResult result = await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Same(before, context.Runtime.GetStatus().ConfirmedState);
        Assert.Equal([DeviceSubsystem.Performance], context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(1, context.Device.PerformanceSetCalls);
        Assert.Equal(3, context.Device.StateReadCalls);
        Assert.True(context.Runtime.GetStatus().WritesBlocked);
    }

    [Fact]
    public async Task ScopedMatchingAggregateReadback_LatchesUnrelatedDriftAndClearsNamedDrift()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Fans = FanMode.Maximum,
        };

        ControlOperationResult result = await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(context.Device.ConfirmedState, context.Runtime.GetStatus().ConfirmedState);
        Assert.Equal([DeviceSubsystem.Fans], context.Runtime.GetStatus().ExternalChanges);
    }

    [Fact]
    public async Task ScopedCancellationBeforeSetter_DoesNotMutateStatusOrDrift()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ServiceStatusSnapshot before = context.Runtime.GetStatus();
        using var cancellation = new CancellationTokenSource();
        int startingReads = context.Device.StateReadCalls;
        context.Device.AfterReadStateCall = call =>
        {
            if (call == startingReads + 1)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.Runtime.SetPerformanceOverrideAsync(
                new ManualOverride<PerformancePreset>(
                    PerformancePreset.Performance,
                    OverrideLifetime.Persistent),
                cancellation.Token));

        Assert.Same(before, context.Runtime.GetStatus());
        Assert.Equal(0, context.Device.PerformanceSetCalls);
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
    }

    [Fact]
    public async Task ScopedCancellationAfterSetter_PreservesConfirmedAndLatchesNamedDrift()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ConfirmedDeviceState before = context.Runtime.GetStatus().ConfirmedState!;
        using var cancellation = new CancellationTokenSource();
        context.Device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Performance)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.Runtime.SetPerformanceOverrideAsync(
                new ManualOverride<PerformancePreset>(
                    PerformancePreset.Performance,
                    OverrideLifetime.Persistent),
                cancellation.Token));

        Assert.Same(before, context.Runtime.GetStatus().ConfirmedState);
        Assert.Equal([DeviceSubsystem.Performance], context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(1, context.Device.PerformanceSetCalls);
    }

    [Fact]
    public async Task FullCharge_UsesOnlyBatteryAndDoesNotConsumeNextPowerTransition()
    {
        TestContext context = CreateWritableContext();

        ControlOperationResult fullCharge = await context.Runtime.SetFullChargeOverrideAsync(
            true,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, fullCharge.Status);
        Assert.Equal([0, 0, 1, 0], SetterCounts(context.Device));
        Assert.Null(context.Runtime.GetStatus().ConfirmedState!.Battery);

        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Performance,
        };
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.True(context.Device.PerformanceSetCalls > 0);
    }

    [Fact]
    public async Task RealPowerTransition_UsesOneFullApplyAndOneFinalAggregateRead()
    {
        TestContext context = CreateWritableContext();
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(1, context.Device.CapabilitiesReadCalls);
        Assert.Equal(2, context.Device.StateReadCalls);
        Assert.Equal(1, context.Provider.TryGetCount);
    }

    [Fact]
    public async Task RealPowerTransition_FinalExternalFanChangeRemainsLatchedAndIsNotUnchanged()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Performance,
            Lighting = ProfileDefaults.CreateSafeDefaults().Battery.Lighting,
        };
        context.Device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Performance)
            {
                context.Device.ConfirmedState = context.Device.ConfirmedState with
                {
                    Fans = FanMode.Maximum,
                };
            }
        };
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(FanMode.Maximum, context.Runtime.GetStatus().ConfirmedState!.Fans);
        Assert.Equal([DeviceSubsystem.Fans], context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(0, context.Device.FanSetCalls);
        DiagnosticCommandResult fanResult = Assert.Single(
            context.History.Snapshot().CommandResults,
            result => result.Subsystem == "Fans");
        Assert.Equal("Failed", fanResult.Status);
        Assert.Contains("final", fanResult.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RealPowerTransition_FinalBatteryProtectionOffRemainsLatchedWithoutBatteryWrite()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Performance,
            Lighting = ProfileDefaults.CreateSafeDefaults().Battery.Lighting,
        };
        context.Device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Performance)
            {
                context.Device.ConfirmedState = context.Device.ConfirmedState with
                {
                    Battery = null,
                };
            }
        };
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Null(context.Runtime.GetStatus().ConfirmedState!.Battery);
        Assert.Equal([DeviceSubsystem.Battery], context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(1, context.Device.PerformanceSetCalls);
        Assert.Equal(0, context.Device.BatterySetCalls);
        DiagnosticHistorySnapshot history = context.History.Snapshot();
        DiagnosticCommandResult performanceResult = Assert.Single(
            history.CommandResults,
            result => result.Subsystem == "Performance");
        Assert.Equal("Applied", performanceResult.Status);
        DiagnosticCommandResult batteryResult = Assert.Single(
            history.CommandResults,
            result => result.Subsystem == "Battery");
        Assert.Equal("Failed", batteryResult.Status);
        Assert.Contains("final", batteryResult.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScopedAction_RecordsOneSubsystemHistoryEntry()
    {
        TestContext context = CreateWritableContext();

        await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);

        DiagnosticCommandResult record = Assert.Single(
            context.History.Snapshot().CommandResults);
        Assert.Equal("Performance", record.Subsystem);
    }

    [Fact]
    public async Task UnsupportedScopedValue_DoesNotSetGlobalWritesBlocked()
    {
        TestContext context = CreateWritableContext();
        context.Device.Capabilities = context.Device.Capabilities with
        {
            PerformancePresets = new HashSet<PerformancePreset>
            {
                PerformancePreset.Silent,
                PerformancePreset.Balanced,
            },
        };

        ControlOperationResult result = await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        Assert.True(context.Runtime.GetStatus().DeviceVerified);
    }

    [Fact]
    public async Task UpdateProfiles_RejectsInvalidIntentWithoutRevisionOrPublication()
    {
        TestContext context = CreateContext(providerAvailable: false);
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet invalid = defaults with
        {
            PluggedIn = defaults.PluggedIn with { Fans = (FanMode)999 },
        };

        ControlOperationResult result = await context.Runtime.UpdateProfilesAsync(
            invalid,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Equal(0, context.Runtime.GetStatus().Revision);
        Assert.Empty(context.Hub.Published);
        Assert.Equal(defaults, context.Runtime.GetStatus().Profiles);
    }

    [Fact]
    public async Task Unplug_ClearsTemporaryOverrideAndFullChargeButKeepsPersistentOverride()
    {
        TestContext context = CreateContext(providerAvailable: false);
        await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);
        await context.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Maximum, OverrideLifetime.Persistent),
            CancellationToken.None);
        await context.Runtime.SetFullChargeOverrideAsync(true, CancellationToken.None);
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        ServiceStatusSnapshot status = context.Runtime.GetStatus();

        Assert.Null(status.PerformanceOverride);
        Assert.Equal(FanMode.Maximum, status.FanOverride!.Value);
        Assert.False(status.FullChargeOverride);
        Assert.False(status.DesiredState.Battery.TemporaryFullCharge);
    }

    [Fact]
    public async Task EnableFullChargeWhileOnBattery_DoesNotRemainLatentAfterAcTransition()
    {
        TestContext context = CreateContext(
            providerAvailable: false,
            initialPowerSource: PowerSource.Battery);

        ControlOperationResult result = await context.Runtime.SetFullChargeOverrideAsync(
            true,
            CancellationToken.None);
        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.False(status.FullChargeOverride);
        Assert.False(status.DesiredState.Battery.TemporaryFullCharge);
        Assert.Equal(80, status.DesiredState.Battery.ChargeLimitPercent);
    }

    [Fact]
    public async Task EnableFullCharge_RejectsFreshUnplugBeforeRequestIsHandled()
    {
        TestContext context = CreateContext(providerAvailable: false);
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery, batteryPercent: 79);

        ControlOperationResult result = await context.Runtime.SetFullChargeOverrideAsync(
            true,
            CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.False(status.FullChargeOverride);
        Assert.False(status.DesiredState.Battery.TemporaryFullCharge);
        Assert.Equal(PowerSource.Battery, status.Telemetry.PowerSource);
        Assert.Equal(79, status.Telemetry.BatteryPercent);
    }

    [Fact]
    public async Task CancelFullCharge_RestoresConfiguredChargeLimitIntent()
    {
        TestContext context = CreateContext(providerAvailable: false);
        await context.Runtime.SetBatteryPolicyAsync(
            BatteryPolicy.Create(65),
            CancellationToken.None);
        await context.Runtime.SetFullChargeOverrideAsync(true, CancellationToken.None);

        await context.Runtime.SetFullChargeOverrideAsync(false, CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.False(status.FullChargeOverride);
        Assert.False(status.DesiredState.Battery.TemporaryFullCharge);
        Assert.Equal(65, status.DesiredState.Battery.ChargeLimitPercent);
    }

    [Fact]
    public async Task CancelFullCharge_UsesFreshTelemetryWhileHardwareIsBlocked()
    {
        TestContext context = CreateContext(providerAvailable: false);
        await context.Runtime.SetFullChargeOverrideAsync(true, CancellationToken.None);
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery, batteryPercent: 78);

        ControlOperationResult result = await context.Runtime.SetFullChargeOverrideAsync(
            false,
            CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.False(status.FullChargeOverride);
        Assert.Equal(PowerSource.Battery, status.Telemetry.PowerSource);
        Assert.Equal(78, status.Telemetry.BatteryPercent);
    }

    [Fact]
    public async Task AcceptedStateChanges_IncrementRevisionAndPublishEachSnapshot()
    {
        TestContext context = CreateContext(providerAvailable: false);

        await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);
        await context.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Maximum, OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal([1L, 2L], context.Hub.Published.Select(status => status.Revision));
        Assert.Equal(2, context.Runtime.GetStatus().Revision);
    }

    [Fact]
    public async Task Refresh_PublishesOnlyWhenSnapshotContentChanges()
    {
        TestContext context = CreateContext(providerAvailable: false);

        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Empty(context.Hub.Published);

        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn, batteryPercent: 79);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Single(context.Hub.Published);
        Assert.Equal(1, context.Runtime.GetStatus().Revision);
    }

    [Fact]
    public async Task VerifiedDeviceSubsystemFailure_ReturnsFailedWithoutSafetyBlock()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Silent,
            FanMode.Automatic,
            BatteryPolicy.Create(80),
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting);
        context.Device.PerformanceFailure = new InvalidOperationException("write rejected");

        ControlOperationResult result = await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        Assert.Contains("write rejected", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, context.Device.PerformanceSetCalls);
        Assert.Equal(1, context.Device.StateReadCalls);
        Assert.Equal(0, context.Device.FanSetCalls);
        Assert.Equal(0, context.Device.BatterySetCalls);
        Assert.Equal(0, context.Device.LightingSetCalls);
    }

    [Fact]
    public async Task VerifiedReadOnlyDevice_PreservesConfirmedBatteryWithoutBlockingObservation()
    {
        TestContext context = CreateContext(providerAvailable: true);
        BatteryPolicy confirmedBattery = BatteryPolicy.Create(65);
        context.Device.Capabilities = DeviceCapabilities.Unavailable();
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            null,
            null,
            confirmedBattery,
            null);

        await context.Runtime.ObserveAsync(CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.True(status.DeviceVerified);
        Assert.Equal(confirmedBattery, status.ConfirmedState!.Battery);
        Assert.False(status.WritesBlocked);
        Assert.Equal("Verified device state observed.", status.Message);
        Assert.Equal(0, context.Device.PerformanceSetCalls);
        Assert.Equal(0, context.Device.FanSetCalls);
        Assert.Equal(0, context.Device.BatterySetCalls);
        Assert.Equal(0, context.Device.LightingSetCalls);
    }

    [Fact]
    public async Task MixedSubsystemFailureAndBlock_ReturnsFailed()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            LightingEffects = new HashSet<LightingEffect>(),
        };
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Silent,
            FanMode.Automatic,
            BatteryPolicy.Create(80),
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting);
        context.Device.PerformanceFailure = new InvalidOperationException("write rejected");

        ControlOperationResult result = await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        Assert.Contains("write rejected", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reconcile_RecordsEachCompletedSubsystemResultWithoutAdditionalDeviceCalls()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.Capabilities = DeviceCapabilities.Unavailable();
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            null,
            null,
            BatteryPolicy.Create(80),
            null);

        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        int callsAfterReconcile = context.Device.TotalCalls;
        DiagnosticHistorySnapshot snapshot = context.History.Snapshot();

        Assert.Equal(callsAfterReconcile, context.Device.TotalCalls);
        Assert.Equal(4, snapshot.CommandResults.Count);
        Assert.Equal(
            ["Performance", "Fans", "Battery", "Lighting"],
            snapshot.CommandResults
                .Where(result => result.Subsystem != "Service")
                .Select(result => result.Subsystem));
        Assert.All(snapshot.CommandResults, result => Assert.Equal("Blocked", result.Status));
    }

    [Fact]
    public async Task BlockedAndFailedReconcile_RecordsBoundedServiceSummaries()
    {
        TestContext blocked = CreateContext(providerAvailable: false);
        blocked.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await blocked.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        DiagnosticCommandResult blockedRecord = Assert.Single(
            blocked.History.Snapshot().CommandResults);
        Assert.Equal("Service", blockedRecord.Subsystem);
        Assert.Equal("Blocked", blockedRecord.Status);
        Assert.Equal(0, blocked.Device.TotalCalls);

        TestContext failed = CreateContext(providerAvailable: true);
        failed.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        failed.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Silent,
            FanMode.Automatic,
            BatteryPolicy.Create(80),
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting);
        failed.Device.PerformanceFailure = new IOException("device disconnected");

        _ = await failed.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);

        DiagnosticCommandResult failedRecord = Assert.Single(
            failed.History.Snapshot().CommandResults);
        Assert.Equal("Performance", failedRecord.Subsystem);
        Assert.Equal("Failed", failedRecord.Status);
    }

    private TestContext CreateWritableContext(
        IReadOnlyList<string>? conflicts = null,
        string? detectionFailure = null)
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            conflicts,
            detectionFailure);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Silent,
            FanMode.Automatic,
            BatteryPolicy.Create(80),
            new LightingSettings(
                LightingEffect.Static,
                new RgbColor(1, 2, 3),
                25));
        return context;
    }

    private static int[] SetterCounts(CountingBladeDevice device) =>
    [
        device.PerformanceSetCalls,
        device.FanSetCalls,
        device.BatterySetCalls,
        device.LightingSetCalls,
    ];

    private TestContext CreateContext(
        bool providerAvailable,
        IReadOnlyList<string>? conflicts = null,
        string? detectionFailure = null,
        PowerSource initialPowerSource = PowerSource.PluggedIn)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        var store = new ProfileStore(Path.Combine(directory, "profiles.json"));
        var telemetry = new FakeTelemetryProvider { Current = CreateTelemetry(initialPowerSource) };
        var hub = new FakeHub();
        var device = new CountingBladeDevice();
        var provider = new CountingProvider(providerAvailable, device);
        var guard = new FakeGuard(conflicts ?? [], detectionFailure);
        var monitor = new FakeControllerPresenceMonitor(
            new ControllerPresenceObservation(conflicts ?? [], detectionFailure));
        var history = new DiagnosticHistory();
        var runtime = new ServiceControlRuntime(
            store,
            profiles,
            telemetry,
            hub,
            provider,
            history,
            monitor);
        return new TestContext(
            runtime,
            store,
            telemetry,
            hub,
            guard,
            monitor,
            provider,
            device,
            history);
    }

    private static async Task LatchFanDriftAsync(TestContext context)
    {
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            null,
            FanMode.Automatic,
            null,
            null);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Fans = FanMode.Maximum,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal([DeviceSubsystem.Fans], context.Runtime.GetStatus().ExternalChanges);
    }

    private static TelemetrySnapshot CreateTelemetry(
        PowerSource source,
        int? batteryPercent = 80) => new(
            DateTimeOffset.Parse("2026-07-14T12:00:00Z"),
            source,
            batteryPercent,
            null,
            null,
            null);

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed record TestContext(
        ServiceControlRuntime Runtime,
        ProfileStore Store,
        FakeTelemetryProvider Telemetry,
        FakeHub Hub,
        FakeGuard Guard,
        FakeControllerPresenceMonitor Monitor,
        CountingProvider Provider,
        CountingBladeDevice Device,
        DiagnosticHistory History);

    private sealed class FakeControllerPresenceMonitor(ControllerPresenceObservation current)
        : IControllerPresenceMonitor
    {
        public ControllerPresenceObservation Current { get; set; } = current;

        public int ObserveCount { get; private set; }

        public ControllerPresenceObservation Observe()
        {
            ObserveCount++;
            return Current;
        }
    }

    private sealed class FakeGuard : IHardwareAccessGuard
    {
        private readonly Queue<HardwareAccessDecision> script = [];
        private HardwareAccessDecision current;

        public FakeGuard(IReadOnlyList<string> conflicts, string? detectionFailure)
        {
            current = new HardwareAccessDecision(conflicts, detectionFailure);
        }

        public void SetScript(params HardwareAccessDecision[] decisions)
        {
            script.Clear();
            foreach (HardwareAccessDecision decision in decisions)
            {
                script.Enqueue(decision);
            }
        }

        public HardwareAccessDecision Check()
        {
            if (script.TryDequeue(out HardwareAccessDecision? decision))
            {
                current = decision;
            }

            return current;
        }
    }

    private sealed class FakeTelemetryProvider : IPowerTelemetryProvider
    {
        public required TelemetrySnapshot Current { get; set; }

        public int CaptureCount { get; private set; }

        public TelemetrySnapshot Capture()
        {
            CaptureCount++;
            return Current;
        }
    }

    private sealed class SingleLifecycleEventSource(ServiceLifecycleTrigger trigger)
        : IServiceLifecycleEventSource
    {
        public async IAsyncEnumerable<ServiceLifecycleTrigger> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return trigger;
        }
    }

    private sealed class FakeHub : IStatusSubscriptionHub
    {
        public List<ServiceStatusSnapshot> Published { get; } = [];

        public void Publish(ServiceStatusSnapshot snapshot) => Published.Add(snapshot);

        public StatusSubscriptionLease Subscribe(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CountingProvider(bool available, IBladeDevice device)
        : IVerifiedBladeDeviceProvider
    {
        public bool Available { get; set; } = available;

        public int TryGetCount { get; private set; }

        public Action? AfterTryGet { get; set; }

        public bool TryGetVerifiedDevice(out IBladeDevice verifiedDevice)
        {
            TryGetCount++;
            verifiedDevice = device;
            AfterTryGet?.Invoke();
            return Available;
        }
    }

    private sealed class CountingBladeDevice : IBladeDevice
    {
        public int TotalCalls { get; private set; }

        public int PerformanceSetCalls { get; private set; }

        public int FanSetCalls { get; private set; }

        public int BatterySetCalls { get; private set; }

        public int LightingSetCalls { get; private set; }

        public int CapabilitiesReadCalls { get; private set; }

        public int StateReadCalls { get; private set; }

        public int TotalSetterCalls => PerformanceSetCalls
            + FanSetCalls
            + BatterySetCalls
            + LightingSetCalls;

        public DeviceCapabilities Capabilities { get; set; } =
            DeviceCapabilities.Unavailable();

        public ConfirmedDeviceState ConfirmedState { get; set; } =
            new(null, null, null, null);

        public Exception? PerformanceFailure { get; set; }

        public Exception? ReadStateFailure { get; set; }

        public int? ReadStateFailureOnCall { get; set; }

        public Action? AfterReadState { get; set; }

        public Action<int>? AfterReadStateCall { get; set; }

        public Action<DeviceSubsystem>? AfterSetter { get; set; }

        public DeviceSubsystem? SuppressConfirmationFor { get; set; }

        public Task<DeviceCapabilities> ReadCapabilitiesAsync(CancellationToken cancellationToken)
        {
            TotalCalls++;
            CapabilitiesReadCalls++;
            return Task.FromResult(Capabilities);
        }

        public Task<ConfirmedDeviceState> ReadStateAsync(CancellationToken cancellationToken)
        {
            TotalCalls++;
            StateReadCalls++;
            if (ReadStateFailure is not null
                || ReadStateFailureOnCall == StateReadCalls)
            {
                throw ReadStateFailure ?? new IOException("readback failed");
            }

            AfterReadState?.Invoke();
            AfterReadStateCall?.Invoke(StateReadCalls);
            return Task.FromResult(ConfirmedState);
        }

        public Task SetPerformanceAsync(PerformancePreset value, CancellationToken cancellationToken)
        {
            TotalCalls++;
            PerformanceSetCalls++;
            if (PerformanceFailure is not null)
            {
                throw PerformanceFailure;
            }

            if (SuppressConfirmationFor != DeviceSubsystem.Performance)
            {
                ConfirmedState = ConfirmedState with { Performance = value };
            }

            AfterSetter?.Invoke(DeviceSubsystem.Performance);
            return Task.CompletedTask;
        }

        public Task SetFanModeAsync(FanMode value, CancellationToken cancellationToken)
        {
            TotalCalls++;
            FanSetCalls++;
            if (SuppressConfirmationFor != DeviceSubsystem.Fans)
            {
                ConfirmedState = ConfirmedState with { Fans = value };
            }

            AfterSetter?.Invoke(DeviceSubsystem.Fans);
            return Task.CompletedTask;
        }

        public Task SetBatteryPolicyAsync(BatteryPolicy value, CancellationToken cancellationToken)
        {
            TotalCalls++;
            BatterySetCalls++;
            if (SuppressConfirmationFor != DeviceSubsystem.Battery)
            {
                ConfirmedState = ConfirmedState with
                {
                    Battery = value.TemporaryFullCharge ? null : value,
                };
            }

            AfterSetter?.Invoke(DeviceSubsystem.Battery);
            return Task.CompletedTask;
        }

        public Task SetLightingAsync(LightingSettings value, CancellationToken cancellationToken)
        {
            TotalCalls++;
            LightingSetCalls++;
            if (SuppressConfirmationFor != DeviceSubsystem.Lighting)
            {
                ConfirmedState = ConfirmedState with { Lighting = value };
            }

            AfterSetter?.Invoke(DeviceSubsystem.Lighting);
            return Task.CompletedTask;
        }
    }
}
