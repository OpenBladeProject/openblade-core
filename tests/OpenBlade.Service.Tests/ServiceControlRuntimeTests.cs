using System.Security.Cryptography;
using System.Text.Json;
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
    public async Task DisplayBrightnessHid_HeldUsageIsSubmittedOnceThenReleased()
    {
        var controller = new RecordingDisplayBrightnessHidController();
        TestContext context = CreateContext(
            providerAvailable: true,
            displayBrightnessHidController: controller);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        ControlOperationResult down = await context.Runtime.SetDisplayBrightnessHidUsageAsync(
            DisplayBrightnessHidUsage.Decrement,
            CancellationToken.None);
        ControlOperationResult repeat = await context.Runtime.SetDisplayBrightnessHidUsageAsync(
            DisplayBrightnessHidUsage.Decrement,
            CancellationToken.None);
        ControlOperationResult up = await context.Runtime.SetDisplayBrightnessHidUsageAsync(
            DisplayBrightnessHidUsage.Released,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, down.Status);
        Assert.Equal(ControlOperationStatus.Applied, repeat.Status);
        Assert.Equal(ControlOperationStatus.Applied, up.Status);
        Assert.Equal(
            [
                DisplayBrightnessHidUsage.Released,
                DisplayBrightnessHidUsage.Decrement,
                DisplayBrightnessHidUsage.Released,
            ],
            controller.Usages);
    }

    [Fact]
    public async Task DisplayBrightnessHid_UnverifiedDeviceBlocksBeforeTransport()
    {
        var controller = new RecordingDisplayBrightnessHidController();
        TestContext context = CreateContext(
            providerAvailable: false,
            displayBrightnessHidController: controller);

        ControlOperationResult result = await context.Runtime.SetDisplayBrightnessHidUsageAsync(
            DisplayBrightnessHidUsage.Increment,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Empty(controller.Usages);
    }

    [Fact]
    public async Task DisplayBrightnessHid_IndeterminatePressStillRequiresRelease()
    {
        var controller = new RecordingDisplayBrightnessHidController(
            new ControlOperationResult(
                ControlOperationStatus.Applied,
                "Neutralized."),
            new ControlOperationResult(
                ControlOperationStatus.Failed,
                "Submission could not be confirmed."),
            new ControlOperationResult(
                ControlOperationStatus.Applied,
                "Released."));
        TestContext context = CreateContext(
            providerAvailable: true,
            displayBrightnessHidController: controller);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        ControlOperationResult down = await context.Runtime.SetDisplayBrightnessHidUsageAsync(
            DisplayBrightnessHidUsage.Increment,
            CancellationToken.None);
        ControlOperationResult up = await context.Runtime.SetDisplayBrightnessHidUsageAsync(
            DisplayBrightnessHidUsage.Released,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, down.Status);
        Assert.Equal(ControlOperationStatus.Applied, up.Status);
        Assert.Equal(
            [
                DisplayBrightnessHidUsage.Released,
                DisplayBrightnessHidUsage.Increment,
                DisplayBrightnessHidUsage.Released,
            ],
            controller.Usages);
    }

    [Fact]
    public async Task DisplayBrightnessHid_DriverModeMaintenanceNeutralizesStaleUsageOnce()
    {
        var controller = new RecordingDisplayBrightnessHidController();
        TestContext context = CreateContext(
            providerAvailable: true,
            displayBrightnessHidController: controller);

        await context.Runtime.MaintainDriverModeAsync(CancellationToken.None);
        await context.Runtime.MaintainDriverModeAsync(CancellationToken.None);

        Assert.Equal([DisplayBrightnessHidUsage.Released], controller.Usages);
    }

    [Fact]
    public async Task DisplayBrightnessHid_LowPowerCleanupReleasesHeldUsage()
    {
        var controller = new RecordingDisplayBrightnessHidController();
        TestContext context = CreateContext(
            providerAvailable: true,
            displayBrightnessHidController: controller);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.DriverModeActive = true;
        await context.Runtime.SetDisplayBrightnessHidUsageAsync(
            DisplayBrightnessHidUsage.Increment,
            CancellationToken.None);

        ControlOperationResult result = await context.Runtime.PrepareForLowPowerAsync(
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(
            [
                DisplayBrightnessHidUsage.Released,
                DisplayBrightnessHidUsage.Increment,
                DisplayBrightnessHidUsage.Released,
            ],
            controller.Usages);
    }

    [Fact]
    public async Task DisplayBrightnessHid_OppositeDirectionReleasesBeforeNewPress()
    {
        var controller = new RecordingDisplayBrightnessHidController();
        TestContext context = CreateContext(
            providerAvailable: true,
            displayBrightnessHidController: controller);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        await context.Runtime.SetDisplayBrightnessHidUsageAsync(
            DisplayBrightnessHidUsage.Increment,
            CancellationToken.None);
        await context.Runtime.SetDisplayBrightnessHidUsageAsync(
            DisplayBrightnessHidUsage.Decrement,
            CancellationToken.None);

        Assert.Equal(
            [
                DisplayBrightnessHidUsage.Released,
                DisplayBrightnessHidUsage.Increment,
                DisplayBrightnessHidUsage.Released,
                DisplayBrightnessHidUsage.Decrement,
            ],
            controller.Usages);
    }

    [Theory]
    [InlineData(DisplayBrightnessHidAvailability.Unknown)]
    [InlineData(DisplayBrightnessHidAvailability.Available)]
    [InlineData(DisplayBrightnessHidAvailability.Unavailable)]
    public async Task DisplayBrightnessHidAvailability_IsPublishedWithoutUsageWrite(
        DisplayBrightnessHidAvailability availability)
    {
        var controller = new RecordingDisplayBrightnessHidController
        {
            Availability = availability,
        };
        TestContext context = CreateContext(
            providerAvailable: true,
            displayBrightnessHidController: controller);

        Assert.Equal(
            availability,
            context.Runtime.GetStatus().DisplayBrightnessHidAvailability);
        Assert.Empty(controller.Usages);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(
            availability,
            context.Runtime.GetStatus().DisplayBrightnessHidAvailability);
    }

    [Fact]
    public async Task ActivateUserAsync_SwitchesProfilesAndSameUserIsNoOp()
    {
        ProfileSet firstProfiles = ProfileDefaults.CreateSafeDefaults();
        ProfileSet secondProfiles = firstProfiles with
        {
            PluggedIn = firstProfiles.PluggedIn with
            {
                Performance = PerformancePreset.Performance,
            },
        };
        var firstSid = new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1001");
        var secondSid = new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1002");
        var repository = new RecordingUserConfigurationRepository(new Dictionary<string, ProfileSet>
        {
            [firstSid.Value] = firstProfiles,
            [secondSid.Value] = secondProfiles,
        }, directory);
        TestContext context = CreateContext(
            providerAvailable: false,
            userConfigurationRepository: repository);

        await context.Runtime.ActivateUserAsync(firstSid, CancellationToken.None);
        long firstRevision = context.Runtime.GetStatus().Revision;
        await context.Runtime.ActivateUserAsync(firstSid, CancellationToken.None);

        Assert.Equal(1, repository.Loads[firstSid.Value]);
        Assert.Equal(firstRevision, context.Runtime.GetStatus().Revision);

        await context.Runtime.ActivateUserAsync(secondSid, CancellationToken.None);

        ServiceStatusSnapshot switched = context.Runtime.GetStatus();
        Assert.Equal(secondProfiles, switched.Profiles);
        Assert.Equal(
            PerformancePreset.Performance,
            switched.DesiredState.Performance.Value);
        Assert.Equal(1, repository.Loads[secondSid.Value]);
    }

    [Theory]
    [InlineData(true, KeyboardLightingControlMode.OpenBlade)]
    [InlineData(false, KeyboardLightingControlMode.WindowsDynamicLighting)]
    public async Task ActivateUserAsync_PublishesLoadedLightingPreferences(
        bool alwaysOverrideExternalLightingChanges,
        KeyboardLightingControlMode keyboardLightingControlMode)
    {
        var preferences = new BatteryAutomationPreferences(
            ProtectionEnabled: true,
            AlwaysOverrideExternalLightingChanges:
                alwaysOverrideExternalLightingChanges,
            KeyboardLightingControlMode: keyboardLightingControlMode);
        var repository = new FixedUserConfigurationRepository(
            new BatteryConfiguration(
                ProfileDefaults.CreateSafeDefaults(),
                preferences,
                Recovered: false),
            PersistentOverrideState.Empty,
            directory);
        TestContext context = CreateContext(
            providerAvailable: false,
            userConfigurationRepository: repository);
        var userSid = new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1001");

        await context.Runtime.ActivateUserAsync(userSid, CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(
            alwaysOverrideExternalLightingChanges,
            status.AlwaysOverrideExternalLightingChanges);
        Assert.Equal(keyboardLightingControlMode, status.KeyboardLightingControlMode);
    }

    [Fact]
    public async Task ActivateUserAsync_ResetsLightingPreferencesAcrossUsers()
    {
        var firstSid = new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1001");
        var secondSid = new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1002");
        var repository = new MappedUserConfigurationRepository(
            new Dictionary<string, BatteryConfiguration>
            {
                [firstSid.Value] = new BatteryConfiguration(
                    ProfileDefaults.CreateSafeDefaults(),
                    new BatteryAutomationPreferences(
                        ProtectionEnabled: true,
                        AlwaysOverrideExternalLightingChanges: true,
                        KeyboardLightingControlMode:
                            KeyboardLightingControlMode.OpenBlade),
                    Recovered: false),
                [secondSid.Value] = new BatteryConfiguration(
                    ProfileDefaults.CreateSafeDefaults(),
                    new BatteryAutomationPreferences(
                        ProtectionEnabled: true,
                        AlwaysOverrideExternalLightingChanges: false,
                        KeyboardLightingControlMode:
                            KeyboardLightingControlMode.WindowsDynamicLighting),
                    Recovered: false),
            },
            directory);
        TestContext context = CreateContext(
            providerAvailable: false,
            userConfigurationRepository: repository);

        await context.Runtime.ActivateUserAsync(firstSid, CancellationToken.None);
        Assert.True(context.Runtime.GetStatus().AlwaysOverrideExternalLightingChanges);
        Assert.Equal(
            KeyboardLightingControlMode.OpenBlade,
            context.Runtime.GetStatus().KeyboardLightingControlMode);

        await context.Runtime.ActivateUserAsync(secondSid, CancellationToken.None);

        Assert.False(context.Runtime.GetStatus().AlwaysOverrideExternalLightingChanges);
        Assert.Equal(
            KeyboardLightingControlMode.WindowsDynamicLighting,
            context.Runtime.GetStatus().KeyboardLightingControlMode);
    }

    [Fact]
    public async Task ActivateUserAsync_SynapseRelinquishesRendererWithoutInvalidation()
    {
        var repository = new FixedUserConfigurationRepository(
            new BatteryConfiguration(
                ProfileDefaults.CreateSafeDefaults(),
                new BatteryAutomationPreferences(ProtectionEnabled: true),
                Recovered: false),
            PersistentOverrideState.Empty,
            directory);
        TestContext context = await CreateOwnedStaticLightingContextAsync(
            userConfigurationRepository: repository);
        context.Monitor.Current = new ControllerPresenceObservation(
            ["RazerAppEngine"],
            null);
        var userSid = new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1001");

        await context.Runtime.ActivateUserAsync(userSid, CancellationToken.None);

        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Equal(1, context.Provider.RelinquishCount);
        Assert.Equal(0, context.Provider.InvalidationCount);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task ActivateUserAsync_DetectionWarningRelinquishesRendererWithoutInvalidation()
    {
        var repository = new FixedUserConfigurationRepository(
            new BatteryConfiguration(
                ProfileDefaults.CreateSafeDefaults(),
                new BatteryAutomationPreferences(ProtectionEnabled: true),
                Recovered: false),
            PersistentOverrideState.Empty,
            directory);
        TestContext context = await CreateOwnedStaticLightingContextAsync(
            userConfigurationRepository: repository);
        context.Monitor.Current = new ControllerPresenceObservation(
            [],
            "Controller detection was incomplete.");
        var userSid = new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1001");

        await context.Runtime.ActivateUserAsync(userSid, CancellationToken.None);

        Assert.Equal(1, context.Provider.RelinquishCount);
        Assert.Equal(0, context.Provider.InvalidationCount);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);
        Assert.Equal(
            "Controller detection was incomplete.",
            context.Runtime.GetStatus().ControllerDetectionWarning);
    }

    [Fact]
    public async Task ActivateUserAsync_NoControllerInvalidatesLightingPortBoundary()
    {
        var repository = new FixedUserConfigurationRepository(
            new BatteryConfiguration(
                ProfileDefaults.CreateSafeDefaults(),
                new BatteryAutomationPreferences(ProtectionEnabled: true),
                Recovered: false),
            PersistentOverrideState.Empty,
            directory);
        TestContext context = await CreateOwnedStaticLightingContextAsync(
            userConfigurationRepository: repository);
        var userSid = new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1001");

        await context.Runtime.ActivateUserAsync(userSid, CancellationToken.None);

        Assert.Equal(1, context.Provider.InvalidationCount);
        Assert.Equal(0, context.Provider.RelinquishCount);
    }

    [Fact]
    public async Task ActivateUserAsync_WindowsLightingInvalidatesBoundaryWithSynapse()
    {
        var repository = new FixedUserConfigurationRepository(
            new BatteryConfiguration(
                ProfileDefaults.CreateSafeDefaults(),
                new BatteryAutomationPreferences(
                    ProtectionEnabled: true,
                    KeyboardLightingControlMode:
                        KeyboardLightingControlMode.WindowsDynamicLighting),
                Recovered: false),
            PersistentOverrideState.Empty,
            directory);
        TestContext context = await CreateOwnedStaticLightingContextAsync(
            userConfigurationRepository: repository);
        context.Monitor.Current = new ControllerPresenceObservation(
            ["RazerAppEngine"],
            null);
        var userSid = new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1001");

        await context.Runtime.ActivateUserAsync(userSid, CancellationToken.None);

        Assert.Equal(1, context.Provider.InvalidationCount);
        Assert.Equal(0, context.Provider.RelinquishCount);
        Assert.Equal(
            KeyboardLightingControlMode.WindowsDynamicLighting,
            context.Runtime.GetStatus().KeyboardLightingControlMode);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task ActivateUserAsync_PersistedLightingIsNotFreshExplicitOwnership()
    {
        LightingSettings lighting = LightingSettingsFactory.Static(
            new RgbColor(24, 96, 240),
            brightness: 45);
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Static,
            },
        };
        var repository = new FixedUserConfigurationRepository(
            new BatteryConfiguration(
                ProfileDefaults.CreateSafeDefaults(),
                new BatteryAutomationPreferences(ProtectionEnabled: true),
                Recovered: false),
            new PersistentOverrideState(null, null, lighting),
            directory);
        TestContext context = CreateContext(
            providerAvailable: true,
            profileValidator: ProfileSetValidator.CreateForLightingCapabilities(capabilities),
            userConfigurationRepository: repository);
        context.Device.Capabilities = capabilities;
        context.Device.ConfirmedState = new ConfirmedDeviceState(null, null, null, null);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        var userSid = new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1001");

        await context.Runtime.ActivateUserAsync(userSid, CancellationToken.None);

        Assert.Equal(lighting, context.Runtime.GetStatus().LightingOverride?.Value);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
        context.Monitor.Current = new ControllerPresenceObservation(
            ["RazerAppEngine"],
            null);

        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);

        Assert.Equal(1, context.Provider.RelinquishCount);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task ActivateUserAsync_DoesNotApplyConfigurationAfterSessionChanges()
    {
        ProfileSet initialProfiles = ProfileDefaults.CreateSafeDefaults();
        ProfileSet staleProfiles = initialProfiles with
        {
            PluggedIn = initialProfiles.PluggedIn with
            {
                Performance = PerformancePreset.Performance,
            },
        };
        var staleSid = new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1001");
        var repository = new RecordingUserConfigurationRepository(new Dictionary<string, ProfileSet>
        {
            [staleSid.Value] = staleProfiles,
        }, directory);
        TestContext context = CreateContext(
            providerAvailable: false,
            userConfigurationRepository: repository);

        await context.Runtime.ActivateUserAsync(
            staleSid,
            static () => false,
            CancellationToken.None);

        Assert.Equal(initialProfiles, context.Runtime.GetStatus().Profiles);
        Assert.Equal(1, repository.Loads[staleSid.Value]);
    }

    [Fact]
    public async Task UnexpectedRestart_InitialUserActivationClassifiesRecoveryDrift()
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet profiles = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Performance = PerformancePreset.Silent,
            },
        };
        var userSid = new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1001");
        var repository = new RecordingUserConfigurationRepository(
            new Dictionary<string, ProfileSet>
            {
                [userSid.Value] = profiles,
            },
            directory);
        DeviceCapabilities capabilities = DeviceCapabilities.SafeTargetDefaults();
        TestContext context = CreateContext(
            providerAvailable: true,
            initialize: false,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(capabilities),
            userConfigurationRepository: repository);
        context.Device.Capabilities = capabilities;
        BatteryConfiguration configuration = await context.Store.LoadAsync(
            CancellationToken.None);
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Maximum,
            ConfirmedBattery(80),
            null);
        context.Runtime.Initialize(
            configuration,
            PowerSource.PluggedIn,
            PersistentOverrideState.Empty,
            recoveringUnexpectedExit: true);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.ReconcileUnexpectedExitAsync(CancellationToken.None);
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(0, context.Device.TotalSetterCalls);

        await context.Runtime.ActivateUserAsync(userSid, CancellationToken.None);

        Assert.Equal(0, context.Device.TotalSetterCalls);
        Assert.Equal(0, context.Provider.InvalidationCount);
        Assert.Equal(
            [DeviceSubsystem.Performance, DeviceSubsystem.Fans],
            context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(FanMode.Maximum, context.Runtime.GetStatus().ConfirmedState!.Fans);
        Assert.Equal(profiles, context.Runtime.GetStatus().Profiles);
        Assert.Equal(1, repository.Loads[userSid.Value]);
    }

    [Fact]
    public async Task UnexpectedRestart_DelayedInitialPowerClassificationPreservesRecoveryDrift()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.SafeTargetDefaults();
        TestContext context = CreateContext(
            providerAvailable: true,
            initialPowerSource: PowerSource.ExternalUnknown,
            initialize: false,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(
                capabilities));
        context.Device.Capabilities = capabilities;
        BatteryConfiguration configuration = await context.Store.LoadAsync(
            CancellationToken.None);
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Maximum,
            ConfirmedBattery(80),
            null);
        context.Runtime.Initialize(
            configuration,
            PowerSource.ExternalUnknown,
            PersistentOverrideState.Empty,
            recoveringUnexpectedExit: true);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.ReconcileUnexpectedExitAsync(CancellationToken.None);
        Assert.Equal([DeviceSubsystem.Fans], context.Runtime.GetStatus().ExternalChanges);

        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(0, context.Device.TotalSetterCalls);
        Assert.Equal([DeviceSubsystem.Fans], context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(FanMode.Maximum, context.Runtime.GetStatus().ConfirmedState!.Fans);
        Assert.Equal(PowerSource.PluggedIn, context.Runtime.GetStatus().ActivePowerSource);
    }

    [Theory]
    [InlineData(DeviceSubsystem.Performance)]
    [InlineData(DeviceSubsystem.Fans)]
    [InlineData(DeviceSubsystem.Battery)]
    [InlineData(DeviceSubsystem.Lighting)]
    public async Task ActivateUserAsync_ReappliesEverySubsystemAcrossUserRoundTrip(
        DeviceSubsystem externallyChangedSubsystem)
    {
        LightingSettings firstLighting = LightingSettingsFactory.Static(
            new RgbColor(255, 0, 0),
            brightness: 40);
        LightingSettings secondLighting = LightingSettingsFactory.Static(
            new RgbColor(0, 0, 255),
            brightness: 70);
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet firstProfiles = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Performance = PerformancePreset.Silent,
                Fans = FanMode.Automatic,
                Lighting = firstLighting,
            },
            Battery = defaults.Battery with
            {
                Lighting = firstLighting,
            },
            UsbC = defaults.UsbC with
            {
                Lighting = firstLighting,
            },
            BatteryPolicy = BatteryPolicy.Create(80),
        };
        ProfileSet secondProfiles = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Performance = PerformancePreset.Balanced,
                Fans = FanMode.Maximum,
                Lighting = secondLighting,
            },
            Battery = defaults.Battery with
            {
                Lighting = secondLighting,
            },
            UsbC = defaults.UsbC with
            {
                Lighting = secondLighting,
            },
            BatteryPolicy = BatteryPolicy.Create(75),
        };
        DeviceCapabilities capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Static,
            },
            ChargeLimits = new HashSet<int> { 70, 75, 80 },
            SupportsBatteryProtectionOff = true,
            SupportsTemporaryFullCharge = true,
        };
        var firstSid = new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1001");
        var secondSid = new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1002");
        var repository = new RecordingUserConfigurationRepository(new Dictionary<string, ProfileSet>
        {
            [firstSid.Value] = firstProfiles,
            [secondSid.Value] = secondProfiles,
        }, directory);
        TestContext context = CreateContext(
            providerAvailable: true,
            profileValidator: ProfileSetValidator.CreateForLightingCapabilities(capabilities),
            userConfigurationRepository: repository);
        context.Device.Capabilities = capabilities;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
            LightingSettingsFactory.Off());
        context.Device.LightingRendererOwnership = LightingRendererOwnershipState.None;

        await context.Runtime.ActivateUserAsync(firstSid, CancellationToken.None);
        await context.Runtime.ActivateUserAsync(secondSid, CancellationToken.None);

        Assert.Equal(PerformancePreset.Balanced, context.Device.ConfirmedState.Performance);
        Assert.Equal(FanMode.Maximum, context.Device.ConfirmedState.Fans);
        Assert.Equal(ConfirmedBattery(75), context.Device.ConfirmedState.Battery);
        Assert.Equal(secondLighting, context.Device.ConfirmedState.Lighting);
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);

        context.Device.ConfirmedState = externallyChangedSubsystem switch
        {
            DeviceSubsystem.Performance => context.Device.ConfirmedState with
            {
                Performance = PerformancePreset.Performance,
            },
            DeviceSubsystem.Fans => context.Device.ConfirmedState with
            {
                Fans = FanMode.Automatic,
            },
            DeviceSubsystem.Battery => context.Device.ConfirmedState with
            {
                Battery = ConfirmedBattery(70),
            },
            DeviceSubsystem.Lighting => context.Device.ConfirmedState with
            {
                Lighting = LightingSettingsFactory.Off(),
            },
            _ => throw new ArgumentOutOfRangeException(
                nameof(externallyChangedSubsystem)),
        };
        if (externallyChangedSubsystem == DeviceSubsystem.Lighting)
        {
            context.Device.LightingRendererOwnership = LightingRendererOwnershipState.None;
        }

        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(
            [externallyChangedSubsystem],
            context.Runtime.GetStatus().ExternalChanges);

        await context.Runtime.ActivateUserAsync(firstSid, CancellationToken.None);

        Assert.Equal(PerformancePreset.Silent, context.Device.ConfirmedState.Performance);
        Assert.Equal(FanMode.Automatic, context.Device.ConfirmedState.Fans);
        Assert.Equal(ConfirmedBattery(80), context.Device.ConfirmedState.Battery);
        Assert.Equal(firstLighting, context.Device.ConfirmedState.Lighting);
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
    }

    [Fact]
    public async Task MaintainDriverMode_PublishesConfirmedStateAndClearsItWhenUnavailable()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.DriverModeActive = true;

        await context.Runtime.MaintainDriverModeAsync(CancellationToken.None);

        Assert.True(context.Runtime.GetStatus().KeyboardDriverModeActive);
        Assert.Equal(1, context.Device.DriverModeEnsureCalls);
        Assert.Equal(1, context.Device.DriverModeReadCalls);

        context.Provider.Available = false;
        await context.Runtime.MaintainDriverModeAsync(CancellationToken.None);

        Assert.Null(context.Runtime.GetStatus().KeyboardDriverModeActive);
    }

    [Fact]
    public async Task MaintainDriverMode_RecoversStalePortOncePerFailureStreak()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.DriverModeActive = true;

        await context.Runtime.MaintainDriverModeAsync(CancellationToken.None);
        Assert.True(context.Runtime.GetStatus().KeyboardDriverModeActive);

        context.Device.DriverModeReadFailures.Enqueue(new IOException("stale port"));
        await context.Runtime.MaintainDriverModeAsync(CancellationToken.None);

        Assert.True(context.Runtime.GetStatus().KeyboardDriverModeActive);
        Assert.Equal(1, context.Provider.InvalidationCount);
        Assert.Equal(3, context.Device.DriverModeReadCalls);

        context.Device.DriverModeReadFailures.Enqueue(new IOException("stale port"));
        context.Device.DriverModeReadFailures.Enqueue(new IOException("fresh port failed"));
        await Assert.ThrowsAsync<IOException>(() =>
            context.Runtime.MaintainDriverModeAsync(CancellationToken.None));

        Assert.Null(context.Runtime.GetStatus().KeyboardDriverModeActive);
        Assert.Equal(2, context.Provider.InvalidationCount);

        context.Device.DriverModeReadFailures.Enqueue(new IOException("still failed"));
        await Assert.ThrowsAsync<IOException>(() =>
            context.Runtime.MaintainDriverModeAsync(CancellationToken.None));

        Assert.Null(context.Runtime.GetStatus().KeyboardDriverModeActive);
        Assert.Equal(2, context.Provider.InvalidationCount);

        await context.Runtime.MaintainDriverModeAsync(CancellationToken.None);
        Assert.True(context.Runtime.GetStatus().KeyboardDriverModeActive);

        context.Device.DriverModeReadFailures.Enqueue(new IOException("new failure streak"));
        await context.Runtime.MaintainDriverModeAsync(CancellationToken.None);
        Assert.Equal(3, context.Provider.InvalidationCount);
        Assert.True(context.Runtime.GetStatus().KeyboardDriverModeActive);
    }

    [Fact]
    public async Task PrepareForShutdown_ReleasesDriverModeAndPublishesNormalState()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.DriverModeActive = true;
        await context.Runtime.MaintainDriverModeAsync(CancellationToken.None);

        ControlOperationResult result = await context.Runtime
            .PrepareForShutdownAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.False(context.Runtime.GetStatus().KeyboardDriverModeActive);
        Assert.Equal(1, context.Device.DriverModeReleaseCalls);
        Assert.Equal(1, context.Device.DriverModeEnsureCalls);
    }

    [Fact]
    public async Task PrepareForShutdown_RelinquishFailureLeavesDriverModeUnchanged()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync(
            explicitAction: false);
        context.Device.DriverModeActive = true;
        await context.Runtime.MaintainDriverModeAsync(CancellationToken.None);
        context.Monitor.Current = new ControllerPresenceObservation(
            ["RazerAppEngine"],
            null);
        context.Provider.RelinquishFailure = new IOException("synthetic handoff failure");

        ControlOperationResult result = await context.Runtime
            .PrepareForShutdownAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.True(context.Device.DriverModeActive);
        Assert.Equal(0, context.Device.DriverModeReleaseCalls);
        Assert.Contains(
            "Driver mode was left unchanged",
            result.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "lighting control could not be relinquished",
            result.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareForShutdown_NoVerifiedDeviceIsACompletedNoOp()
    {
        TestContext context = CreateContext(providerAvailable: false);

        ControlOperationResult result = await context.Runtime
            .PrepareForShutdownAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, result.Status);
        Assert.Contains(
            "No verified laptop was available",
            result.Message,
            StringComparison.Ordinal);
        Assert.Equal(0, context.Device.TotalCalls);
    }

    [Theory]
    [InlineData(FanMode.Fixed)]
    [InlineData(FanMode.ManualCurve)]
    [InlineData(FanMode.Maximum)]
    public async Task PrepareForShutdown_RestoresAutomaticFansAndPreservesManualIntent(
        FanMode mode)
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ControlOperationResult applied = await context.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(mode, OverrideLifetime.Persistent),
            CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Applied, applied.Status);
        FanControlSettings? storedControl = context.Runtime.GetStatus().FanControlOverride;
        int fanWrites = context.Device.FanSetCalls;

        ControlOperationResult result = await context.Runtime
            .PrepareForShutdownAsync(CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(fanWrites + 1, context.Device.FanSetCalls);
        Assert.Equal(FanMode.Automatic, status.ConfirmedState!.Fans);
        Assert.Equal(mode, status.FanOverride!.Value);
        Assert.Equal(storedControl, status.FanControlOverride);
        Assert.Equal(mode, status.DesiredState.Fans.Value);
        Assert.Contains("Automatic fan control was confirmed", result.Message);
    }

    [Fact]
    public async Task PrepareForShutdown_RestoresManualFansOwnedByPowerTransition()
    {
        TestContext context = CreateWritableContext(
            initialPowerSource: PowerSource.Battery);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        profiles = profiles with
        {
            PluggedIn = profiles.PluggedIn with
            {
                Performance = PerformancePreset.Balanced,
                Fans = FanMode.Maximum,
                FanControl = FanControlSettings.FromLegacy(FanMode.Maximum),
            },
        };
        ControlOperationResult stored = await context.Runtime.UpdateProfilesAsync(
            profiles,
            CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Stored, stored.Status);
        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        Assert.Equal(FanMode.Maximum, context.Device.ConfirmedState.Fans);
        int fanWrites = context.Device.FanSetCalls;

        ControlOperationResult result = await context.Runtime
            .PrepareForShutdownAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(fanWrites + 1, context.Device.FanSetCalls);
        Assert.Equal(FanMode.Automatic, context.Device.ConfirmedState.Fans);
    }

    [Fact]
    public async Task PrepareForShutdown_FanReadbackFailureStillReleasesDriverMode()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(
                FanMode.Maximum,
                OverrideLifetime.Persistent),
            CancellationToken.None);
        context.Device.DriverModeActive = true;
        context.Device.SuppressConfirmationFor = DeviceSubsystem.Fans;

        ControlOperationResult result = await context.Runtime
            .PrepareForShutdownAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Equal(FanMode.Maximum, context.Device.ConfirmedState.Fans);
        Assert.Equal(1, context.Device.DriverModeReleaseCalls);
        Assert.False(context.Device.DriverModeActive);
        Assert.Contains("could not be handed back", result.Message);
    }

    [Fact]
    public async Task PrepareForShutdown_FanCancellationOccursAfterDriverModeRelease()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(
                FanMode.Maximum,
                OverrideLifetime.Persistent),
            CancellationToken.None);
        context.Device.DriverModeActive = true;
        using var cancellation = new CancellationTokenSource();
        context.Device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Fans)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Runtime.PrepareForShutdownAsync(cancellation.Token));

        Assert.Equal(1, context.Device.DriverModeReleaseCalls);
        Assert.False(context.Device.DriverModeActive);
        Assert.Contains(
            DeviceSubsystem.Fans,
            context.Runtime.GetStatus().ExternalChanges);
    }

    [Fact]
    public async Task PrepareForShutdown_UsesConfirmedManualStateAfterClearWasRefused()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(
                FanMode.Maximum,
                OverrideLifetime.Persistent),
            CancellationToken.None);
        context.Device.FanFailure = new CapabilityUnavailableException(
            "Synthetic fan refusal.");

        ControlOperationResult cleared = await context.Runtime.SetFanOverrideAsync(
            null,
            CancellationToken.None);

        ServiceStatusSnapshot refused = context.Runtime.GetStatus();
        Assert.Equal(ControlOperationStatus.Blocked, cleared.Status);
        Assert.Equal(FanMode.Automatic, refused.DesiredState.Fans.Value);
        Assert.Equal(FanMode.Maximum, refused.ConfirmedState!.Fans);
        Assert.DoesNotContain(DeviceSubsystem.Fans, refused.ExternalChanges);
        context.Device.FanFailure = null;
        int fanWrites = context.Device.FanSetCalls;

        ControlOperationResult result = await context.Runtime
            .PrepareForShutdownAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(fanWrites + 1, context.Device.FanSetCalls);
        Assert.Equal(FanMode.Automatic, context.Device.ConfirmedState.Fans);
        Assert.Contains("Automatic fan control was confirmed", result.Message);
    }

    [Fact]
    public async Task PrepareForShutdown_DoesNotClaimFirstObservedManualFans()
    {
        TestContext context = CreateWritableContext();
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Fans = FanMode.Maximum,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
        int fanWrites = context.Device.FanSetCalls;

        ControlOperationResult result = await context.Runtime
            .PrepareForShutdownAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(fanWrites, context.Device.FanSetCalls);
        Assert.Equal(FanMode.Maximum, context.Device.ConfirmedState.Fans);
        Assert.Contains("did not establish ownership", result.Message);
    }

    [Fact]
    public async Task PrepareForShutdown_DoesNotRestoreOwnedFansWhileSynapseIsActive()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(
                FanMode.Maximum,
                OverrideLifetime.Persistent),
            CancellationToken.None);
        context.Monitor.Current = new ControllerPresenceObservation(
            ["RazerAppEngine"],
            null);
        int fanWrites = context.Device.FanSetCalls;

        ControlOperationResult result = await context.Runtime
            .PrepareForShutdownAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, result.Status);
        Assert.Equal(fanWrites, context.Device.FanSetCalls);
        Assert.Equal(FanMode.Maximum, context.Device.ConfirmedState.Fans);
        Assert.Equal(0, context.Device.DriverModeReleaseCalls);
        Assert.Contains("Razer Synapse is active", result.Message);
    }

    [Fact]
    public async Task PrepareForShutdown_DoesNotReclaimExternallyChangedFans()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(
                FanMode.Maximum,
                OverrideLifetime.Persistent),
            CancellationToken.None);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Fans = FanMode.Automatic,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Contains(DeviceSubsystem.Fans, context.Runtime.GetStatus().ExternalChanges);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Fans = FanMode.Maximum,
        };
        int fanWrites = context.Device.FanSetCalls;

        ControlOperationResult result = await context.Runtime
            .PrepareForShutdownAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(fanWrites, context.Device.FanSetCalls);
        Assert.Equal(FanMode.Maximum, context.Device.ConfirmedState.Fans);
        Assert.Contains("no longer owns", result.Message);
    }

    [Fact]
    public async Task PrepareForShutdown_DoesNotRestoreFansWhenControllerDetectionFailed()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(
                FanMode.Maximum,
                OverrideLifetime.Persistent),
            CancellationToken.None);
        context.Monitor.Current = new ControllerPresenceObservation(
            [],
            "Process enumeration failed.");
        int fanWrites = context.Device.FanSetCalls;

        ControlOperationResult result = await context.Runtime
            .PrepareForShutdownAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, result.Status);
        Assert.Equal(fanWrites, context.Device.FanSetCalls);
        Assert.Equal(FanMode.Maximum, context.Device.ConfirmedState.Fans);
        Assert.Contains("could not be checked", result.Message);
    }

    [Fact]
    public async Task PrepareForLowPower_ReleasesDriverModeAndRecordsBoundary()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.DriverModeActive = true;

        ControlOperationResult result = await context.Runtime
            .PrepareForLowPowerAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.False(context.Device.DriverModeActive);
        Assert.Equal(1, context.Device.DriverModeReleaseCalls);
        Assert.Contains("low power", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            context.History.Snapshot().CommandResults,
            entry => entry.Detail?.Contains(
                "Attempting",
                StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task PrepareForLowPower_RecordsDistinctReleaseFailureWithoutThrowing()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.DriverModeActive = true;
        context.Device.DriverModeReleaseFailure = new IOException("synthetic release failure");

        ControlOperationResult result = await context.Runtime
            .PrepareForLowPowerAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Contains("synthetic release failure", result.Message);
        Assert.Contains(
            context.History.Snapshot().CommandResults,
            entry => entry.Status == "Failed"
                && entry.Detail?.Contains("synthetic release failure") == true);
    }

    [Fact]
    public async Task PrepareForLowPower_NoVerifiedDeviceRemainsFailed()
    {
        TestContext context = CreateContext(providerAvailable: false);

        ControlOperationResult result = await context.Runtime
            .PrepareForLowPowerAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Contains(
            "could not verify",
            result.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidateLightingRenderer_RecordsIndeterminateModeCleanup()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Provider.InvalidationDriverModeRestoreOutcome =
            DriverModeRestoreOutcome.Indeterminate;

        await context.Runtime.InvalidateLightingRendererAsync(CancellationToken.None);

        Assert.Contains(
            context.History.Snapshot().CommandResults,
            entry => entry.Status == "Failed"
                && entry.Detail?.Contains(
                    "could not confirm",
                    StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task PrepareForShutdown_DoesNotChangeDriverModeDuringControllerConflict()
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            conflicts: ["RazerAppEngine"]);
        context.Device.DriverModeActive = true;

        ControlOperationResult result = await context.Runtime
            .PrepareForShutdownAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, result.Status);
        Assert.True(context.Device.DriverModeActive);
        Assert.Equal(0, context.Device.DriverModeReleaseCalls);
    }

    [Fact]
    public async Task PrepareForShutdown_ReleasesDriverModeWhileChromaServiceIsRunning()
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            conflicts: ["Razer Chroma SDK Service"]);
        context.Device.DriverModeActive = true;

        ControlOperationResult result = await context.Runtime
            .PrepareForShutdownAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.False(context.Device.DriverModeActive);
        Assert.Equal(1, context.Device.DriverModeReleaseCalls);
    }

    [Fact]
    public async Task PrepareForShutdown_DoesNotWriteWhenControllerDetectionFailed()
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            detectionFailure: "Process enumeration failed.");
        context.Device.DriverModeActive = true;

        ControlOperationResult result = await context.Runtime
            .PrepareForShutdownAsync(CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, result.Status);
        Assert.True(context.Device.DriverModeActive);
        Assert.Equal(0, context.Device.DriverModeReleaseCalls);
    }

    [Fact]
    public async Task BeforeInitialize_StatusAndMutationAccessAreRejectedWithoutHardwareAccess()
    {
        TestContext context = CreateContext(providerAvailable: true, initialize: false);

        Assert.Throws<InvalidOperationException>(() => context.Runtime.GetStatus());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Runtime.UpdateProfilesAsync(
                ProfileDefaults.CreateSafeDefaults(),
                CancellationToken.None));
        Assert.Equal(0, context.Provider.TryGetCount);
        Assert.Equal(0, context.Device.TotalCalls);
    }

    [Fact]
    public void Initialize_IsAllowedExactlyOnce()
    {
        TestContext context = CreateContext(providerAvailable: true, initialize: false);
        BatteryConfiguration configuration = new(
            ProfileDefaults.CreateSafeDefaults(),
            new BatteryAutomationPreferences(ProtectionEnabled: true),
            Recovered: false);

        context.Runtime.Initialize(configuration, PowerSource.PluggedIn);

        Assert.Throws<InvalidOperationException>(() =>
            context.Runtime.Initialize(configuration, PowerSource.PluggedIn));
        Assert.Equal(0, context.Provider.TryGetCount);
        Assert.Equal(0, context.Device.TotalCalls);
    }

    [Fact]
    public void Initialize_PublishesCapturedFirmwareWithoutHardwareAccess()
    {
        var firmware = new FirmwareVersions("3.01", "1.04", "Unavailable", "98.3.5c.40.3d");
        TestContext context = CreateContext(
            providerAvailable: true,
            firmwareVersions: firmware);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(firmware, status.Firmware);
        Assert.Equal(
            OpenBladeProductVersion.Read(typeof(ServiceControlRuntime).Assembly),
            status.ServiceVersion);
        Assert.Equal(0, context.Provider.TryGetCount);
        Assert.Equal(0, context.Device.TotalCalls);
    }

    [Fact]
    public void Initialize_PublishesReadOnlyBatteryHealthWithoutBladeHardwareAccess()
    {
        var batteryHealth = new BatteryHealthStatus(95_200, 90_440);
        TestContext context = CreateContext(
            providerAvailable: true,
            batteryHealth: batteryHealth);

        Assert.Equal(batteryHealth, context.Runtime.GetStatus().BatteryHealth);
        Assert.Equal(1, context.Telemetry.BatteryHealthCaptureCount);
        Assert.Equal(0, context.Provider.TryGetCount);
        Assert.Equal(0, context.Device.TotalCalls);
    }

    [Fact]
    public async Task PowerSourceChange_RetriesBatteryHealthUntilAvailable()
    {
        TestContext context = CreateContext(providerAvailable: true);
        var recovered = new BatteryHealthStatus(95_200, 90_440);
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Null(context.Runtime.GetStatus().BatteryHealth);
        Assert.Equal(2, context.Telemetry.BatteryHealthCaptureCount);

        context.Telemetry.BatteryHealth = recovered;
        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(recovered, context.Runtime.GetStatus().BatteryHealth);
        Assert.Equal(3, context.Telemetry.BatteryHealthCaptureCount);

        context.Telemetry.BatteryHealth = new BatteryHealthStatus(95_200, 89_000);
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(recovered, context.Runtime.GetStatus().BatteryHealth);
        Assert.Equal(3, context.Telemetry.BatteryHealthCaptureCount);
    }

    [Fact]
    public async Task FirstVerifiedObservation_RetriesUnavailableBatteryHealthOnce()
    {
        TestContext context = CreateContext(providerAvailable: true);
        var recovered = new BatteryHealthStatus(95_200, 90_440);
        context.Telemetry.BatteryHealth = recovered;

        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(recovered, context.Runtime.GetStatus().BatteryHealth);
        Assert.Equal(2, context.Telemetry.BatteryHealthCaptureCount);
    }

    [Fact]
    public async Task FirstVerifiedObservation_ReadsMcuVersionOnceAndPreservesOtherFirmware()
    {
        var firmware = new FirmwareVersions(
            "3.01",
            "1.04",
            "Unavailable",
            "98.3.5c.40.3d");
        TestContext context = CreateContext(
            providerAvailable: true,
            firmwareVersions: firmware);
        context.Device.McuVersion = "1.5.0.0";

        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(
            firmware with { McuVersion = "1.5.0.0" },
            context.Runtime.GetStatus().Firmware);
        Assert.Equal(1, context.Device.McuVersionReadCalls);
    }

    [Fact]
    public async Task Initialize_DisabledProtectionSurvivesObservationAndProfileSaveWithoutSetters()
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            protectionEnabled: false);

        Assert.False(context.Runtime.GetStatus().DesiredState!.Battery.ProtectionEnabled);
        Assert.Equal(0, context.Device.TotalCalls);
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            null,
            null,
            ProtectionOff(),
            null);

        await context.Runtime.ObserveAsync(CancellationToken.None);
        ProfileSet updated = context.Runtime.GetStatus().Profiles with
        {
            BatteryPolicy = BatteryPolicy.Create(75),
        };
        ControlOperationResult result = await context.Runtime.UpdateProfilesAsync(
            updated,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, result.Status);
        Assert.False(context.Runtime.GetStatus().DesiredState!.Battery.ProtectionEnabled);
        BatteryConfiguration committed = await context.Store.LoadAsync(CancellationToken.None);
        Assert.False(committed.Profiles.PluggedIn.EffectiveBatteryProtectionEnabled);
        Assert.Null(committed.Preferences.ProtectionEnabled);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task DisabledProtectionRejectsTemporaryFullChargeWithoutSetters()
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            protectionEnabled: false);

        ControlOperationResult result = await context.Runtime.SetFullChargeOverrideAsync(
            enabled: true,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Contains("protection", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(context.Runtime.GetStatus().DesiredState!.Battery.TemporaryFullCharge);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task Observe_ReadsCapabilitiesAndStateOnceWithoutSetters()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Performance,
            FanMode.Maximum,
            ConfirmedBattery(75),
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting with { Brightness = 25 });

        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(1, context.Device.CapabilitiesReadCalls);
        Assert.Equal(1, context.Device.StateReadCalls);
        Assert.Equal(0, context.Device.TotalSetterCalls);
        Assert.Equal(context.Device.Capabilities, context.Runtime.GetStatus().Capabilities);
        Assert.Equal(context.Device.ConfirmedState, context.Runtime.GetStatus().ConfirmedState);
    }

    [Fact]
    public async Task Observe_PublishesObservedCapabilityOnlyChanges()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        long revision = context.Runtime.GetStatus().Revision;
        int publications = context.Hub.Published.Count;
        context.Device.Capabilities = context.Device.Capabilities with
        {
            SpecialKeyBindingsAccess = DeviceFeatureAccess.ObservedOnly,
            LampArrayLightingAccess = DeviceFeatureAccess.ObservedOnly,
            ObservedLightingEffects =
                new HashSet<LightingEffect> { LightingEffect.Spectrum },
        };

        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(revision + 1, context.Runtime.GetStatus().Revision);
        Assert.Equal(publications + 1, context.Hub.Published.Count);
        Assert.True(context.Runtime.GetStatus().Capabilities!.ObservedLightingEffects.SetEquals(
            [LightingEffect.Spectrum]));
        Assert.Equal(
            DeviceFeatureAccess.ObservedOnly,
            context.Runtime.GetStatus().Capabilities!.SpecialKeyBindingsAccess);
        Assert.Equal(
            DeviceFeatureAccess.ObservedOnly,
            context.Runtime.GetStatus().Capabilities!.LampArrayLightingAccess);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task Observe_PublishesPlaybackMuteIndicatorCapabilityChange()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            SupportsPlaybackMuteIndicator = false,
        };
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        long revision = context.Runtime.GetStatus().Revision;
        int publications = context.Hub.Published.Count;
        context.Device.Capabilities = context.Device.Capabilities with
        {
            SupportsPlaybackMuteIndicator = true,
        };

        await context.Runtime.ObserveAsync(CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(revision + 1, status.Revision);
        Assert.Equal(publications + 1, context.Hub.Published.Count);
        Assert.True(status.Capabilities!.SupportsPlaybackMuteIndicator);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task ReactivePulse_RequiresOwnedReactiveRendererWithoutEffectReadback()
    {
        ProfileSetValidator validator = ProfileSetValidator.CreateForLightingCapabilities(
            DeviceCapabilities.Unavailable() with
            {
                LightingEffects = new HashSet<LightingEffect>
                {
                    LightingEffect.Off,
                    LightingEffect.Static,
                    LightingEffect.Reactive,
                },
            });
        TestContext context = CreateContext(
            providerAvailable: true,
            profileValidator: validator);
        LightingSettings reactive = LightingSettingsFactory.Reactive(
            new RgbColor(0, 255, 0),
            brightness: 77,
            ReactiveLightingDuration.Medium);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Reactive,
            },
        };
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            null,
            null,
            null,
            null);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ControlOperationResult selected = await context.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                reactive,
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Applied, selected.Status);
        Assert.Equal(
            LightingRendererOwnershipState.Reactive,
            context.Runtime.GetStatus().LightingRendererOwnership);

        ControlOperationResult applied = await context.Runtime.PulseReactiveKeyAsync(
            new ReactiveKeyPulse(77),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, applied.Status);
        Assert.Equal(1, context.Device.ReactivePulseCalls);

        context.Device.LightingRendererOwnership =
            LightingRendererOwnershipState.Faulted;
        ControlOperationResult faulted = await context.Runtime.PulseReactiveKeyAsync(
            new ReactiveKeyPulse(77),
            CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Blocked, faulted.Status);
        Assert.Equal(1, context.Device.ReactivePulseCalls);
        Assert.Equal(
            LightingRendererOwnershipState.Reactive,
            context.Runtime.GetStatus().LightingRendererOwnership);

        await context.Runtime.SetLightingOverrideAsync(null, CancellationToken.None);
        Assert.Equal(
            LightingEffect.Off,
            context.Runtime.GetStatus().DesiredState.Lighting.Value.Effect);
        ControlOperationResult blocked = await context.Runtime.PulseReactiveKeyAsync(
            new ReactiveKeyPulse(77),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, blocked.Status);
        Assert.Equal(1, context.Device.ReactivePulseCalls);
    }

    [Fact]
    public async Task ReactivePulse_DoesNotWaitBehindRuntimeFirmwareOperationGate()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Reactive,
            },
        };
        TestContext context = CreateContext(
            providerAvailable: true,
            profileValidator: ProfileSetValidator.CreateForLightingCapabilities(capabilities));
        context.Device.Capabilities = capabilities;
        context.Device.ConfirmedState = new ConfirmedDeviceState(null, null, null, null);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                LightingSettingsFactory.Reactive(
                    new RgbColor(0, 255, 0),
                    77,
                    ReactiveLightingDuration.Medium),
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);

        var gate = Assert.IsType<SemaphoreSlim>(typeof(ServiceControlRuntime)
            .GetField(
                "operationGate",
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(context.Runtime));
        await gate.WaitAsync();
        try
        {
            ControlOperationResult result = await context.Runtime.PulseReactiveKeyAsync(
                    new ReactiveKeyPulse(77),
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromMilliseconds(250));
            Assert.Equal(ControlOperationStatus.Applied, result.Status);
        }
        finally
        {
            gate.Release();
        }

        Assert.Equal(1, context.Device.ReactivePulseCalls);

        context.Provider.Available = false;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal("Checking the laptop connection after a temporary device-access failure.",
            context.Runtime.GetStatus().Message);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        ControlOperationResult duringRecheck = await context.Runtime.PulseReactiveKeyAsync(
            new ReactiveKeyPulse(53),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, duringRecheck.Status);
        Assert.Equal(2, context.Device.ReactivePulseCalls);
    }

    [Fact]
    public async Task KeyboardBrightnessStep_UsesDedicatedDevicePathAndUpdatesOverride()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Static,
            },
        };
        TestContext context = CreateContext(
            providerAvailable: true,
            profileValidator: ProfileSetValidator.CreateForLightingCapabilities(
                capabilities));
        context.Device.Capabilities = capabilities;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        _ = await context.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                LightingSettingsFactory.Static(
                    LightingSettingsFactory.DefaultVisibleColor,
                    brightness: 50),
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);
        int fullLightingWrites = context.Device.LightingSetCalls;
        context.Device.NextKeyboardBrightness = 44;

        ControlOperationResult result = await context.Runtime.StepKeyboardBrightnessAsync(
            KeyboardBrightnessDirection.Decrease,
            OverrideLifetime.UntilPowerSourceChange,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(
            [KeyboardBrightnessDirection.Decrease],
            context.Device.KeyboardBrightnessSteps);
        Assert.Equal(fullLightingWrites, context.Device.LightingSetCalls);
        Assert.Equal(
            44,
            context.Runtime.GetStatus().DesiredState.Lighting.Value.Brightness);
        Assert.Equal(
            OverrideLifetime.UntilPowerSourceChange,
            context.Runtime.GetStatus().LightingOverride?.Lifetime);
    }

    [Fact]
    public async Task ReactivePulse_ControllerAdvisoryDoesNotBlockOwnedRenderer()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Reactive,
            },
        };
        TestContext context = CreateContext(
            providerAvailable: true,
            conflicts: ["RazerAppEngine"],
            detectionFailure: "enumeration warning",
            profileValidator: ProfileSetValidator.CreateForLightingCapabilities(capabilities));
        context.Device.Capabilities = capabilities;
        context.Device.ConfirmedState = new ConfirmedDeviceState(null, null, null, null);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                LightingSettingsFactory.Reactive(
                    new RgbColor(0, 255, 0),
                    77,
                    ReactiveLightingDuration.Medium),
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);

        ControlOperationResult result = await context.Runtime.PulseReactiveKeyAsync(
            new ReactiveKeyPulse(53),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(1, context.Device.ReactivePulseCalls);
        Assert.Equal(["RazerAppEngine"], context.Runtime.GetStatus().PotentialControllers);
        Assert.Equal(
            "enumeration warning",
            context.Runtime.GetStatus().ControllerDetectionWarning);
    }

    [Fact]
    public async Task AmbientFrame_DoesNotWaitBehindRuntimeFirmwareOperationGate()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.AmbientAwareness,
            },
        };
        TestContext context = CreateContext(
            providerAvailable: true,
            profileValidator: ProfileSetValidator.CreateForLightingCapabilities(capabilities));
        context.Device.Capabilities = capabilities;
        context.Device.ConfirmedState = new ConfirmedDeviceState(null, null, null, null);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                LightingSettingsFactory.AmbientAwareness(
                    AmbientAwarenessRegion.Full,
                    77),
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);

        var gate = Assert.IsType<SemaphoreSlim>(typeof(ServiceControlRuntime)
            .GetField(
                "operationGate",
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(context.Runtime));
        await gate.WaitAsync();
        try
        {
            ControlOperationResult result = await context.Runtime.PushAmbientLightingFrameAsync(
                    new AmbientLightingFrame(
                        new RgbColor[AmbientLightingFrame.SlotCount]),
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromMilliseconds(250));
            Assert.Equal(ControlOperationStatus.Applied, result.Status);
        }
        finally
        {
            gate.Release();
        }

        Assert.Equal(1, context.Device.AmbientFrameCalls);
    }

    [Fact]
    public async Task AmbientFrame_ExplicitRendererSurvivesSynapseRefresh()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.AmbientAwareness,
            },
        };
        TestContext context = CreateContext(
            providerAvailable: true,
            profileValidator: ProfileSetValidator.CreateForLightingCapabilities(capabilities));
        context.Device.Capabilities = capabilities;
        context.Device.ConfirmedState = new ConfirmedDeviceState(null, null, null, null);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                LightingSettingsFactory.AmbientAwareness(
                    AmbientAwarenessRegion.Full,
                    77),
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);
        context.Monitor.Current = new ControllerPresenceObservation(
            ["RazerAppEngine"],
            null);

        await context.Runtime.RefreshControllerPresenceAsync(CancellationToken.None);
        ControlOperationResult result = await context.Runtime.PushAmbientLightingFrameAsync(
            new AmbientLightingFrame(new RgbColor[AmbientLightingFrame.SlotCount]),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(1, context.Device.AmbientFrameCalls);
        Assert.Equal(0, context.Provider.RelinquishCount);
        Assert.Equal(
            LightingRendererOwnershipState.AmbientAwareness,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task PlaybackMuteIndicator_NoOwnedRendererUsesOnlyMutePath()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            SupportsPlaybackMuteIndicator = true,
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Static,
            },
        };
        TestContext context = CreateContext(
            providerAvailable: true,
            profileValidator: ProfileSetValidator.CreateForLightingCapabilities(
                capabilities));
        context.Device.Capabilities = capabilities;
        await context.Runtime.ObserveAsync(CancellationToken.None);

        ControlOperationResult muted =
            await context.Runtime.SetPlaybackMuteIndicatorAsync(
                true,
                CancellationToken.None);
        int lightingWritesBeforeUnmute = context.Device.LightingSetCalls;
        int controllerScansBeforeUnmute = context.Monitor.ObserveCount;
        ControlOperationResult unmuted =
            await context.Runtime.SetPlaybackMuteIndicatorAsync(
                false,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, muted.Status);
        Assert.Equal(ControlOperationStatus.Applied, unmuted.Status);
        Assert.Equal([true, false], context.Device.PlaybackMuteIndicatorStates);
        Assert.Equal(lightingWritesBeforeUnmute, context.Device.LightingSetCalls);
        Assert.Equal(controllerScansBeforeUnmute, context.Monitor.ObserveCount);
    }

    [Fact]
    public async Task PlaybackMuteIndicator_HealthyOwnedUnmuteDoesNotRewriteLighting()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            SupportsPlaybackMuteIndicator = true,
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Static,
            },
        };
        TestContext context = CreateContext(
            providerAvailable: true,
            profileValidator: ProfileSetValidator.CreateForLightingCapabilities(
                capabilities));
        context.Device.Capabilities = capabilities;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                LightingSettingsFactory.Static(
                    new RgbColor(0x12, 0x34, 0x56),
                    100),
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);
        await context.Runtime.SetPlaybackMuteIndicatorAsync(
            true,
            CancellationToken.None);
        int lightingWritesBeforeUnmute = context.Device.LightingSetCalls;
        int controllerScansBeforeUnmute = context.Monitor.ObserveCount;

        ControlOperationResult result =
            await context.Runtime.SetPlaybackMuteIndicatorAsync(
                false,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(lightingWritesBeforeUnmute, context.Device.LightingSetCalls);
        Assert.Equal(controllerScansBeforeUnmute, context.Monitor.ObserveCount);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Device.LightingRendererOwnership);
    }

    [Fact]
    public async Task PlaybackMuteIndicator_ReconcileFailureKeepsSuccessfulUnmuteApplied()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            SupportsPlaybackMuteIndicator = true,
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Static,
            },
        };
        TestContext context = CreateContext(
            providerAvailable: true,
            profileValidator: ProfileSetValidator.CreateForLightingCapabilities(
                capabilities));
        context.Device.Capabilities = capabilities;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                LightingSettingsFactory.Static(
                    new RgbColor(0x12, 0x34, 0x56),
                    100),
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);
        await context.Runtime.SetPlaybackMuteIndicatorAsync(
            true,
            CancellationToken.None);
        typeof(ServiceControlRuntime)
            .GetField(
                "controllerPresenceObserved",
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(context.Runtime, false);
        context.Monitor.Failure = new IOException("controller scan failed");

        ControlOperationResult result =
            await context.Runtime.SetPlaybackMuteIndicatorAsync(
                false,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal([true, false], context.Device.PlaybackMuteIndicatorStates);
        Assert.Contains(
            context.History.Snapshot().CommandResults,
            summary => summary.Status == "Failed"
                && summary.Detail?.Contains(
                    "keyboard lighting reconciliation",
                    StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task PlaybackMuteIndicator_UnmuteRestoresOwnedRendererFromPublishedNone()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            SupportsPlaybackMuteIndicator = true,
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Static,
            },
        };
        TestContext context = CreateContext(
            providerAvailable: true,
            profileValidator: ProfileSetValidator.CreateForLightingCapabilities(
                capabilities));
        context.Device.Capabilities = capabilities;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                LightingSettingsFactory.Static(
                    new RgbColor(0x12, 0x34, 0x56),
                    100),
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);
        await context.Runtime.SetPlaybackMuteIndicatorAsync(
            true,
            CancellationToken.None);
        int lightingWritesBeforeUnmute = context.Device.LightingSetCalls;
        context.Device.LightingRendererOwnership = LightingRendererOwnershipState.None;
        typeof(ServiceControlRuntime)
            .GetField(
                "status",
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(
                context.Runtime,
                context.Runtime.GetStatus() with
                {
                    LightingRendererOwnership = LightingRendererOwnershipState.None,
                });

        ControlOperationResult result =
            await context.Runtime.SetPlaybackMuteIndicatorAsync(
                false,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(lightingWritesBeforeUnmute + 1, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Device.LightingRendererOwnership);
    }

    [Fact]
    public async Task PlaybackMuteIndicator_UnmuteRetryRestoresLostOwnedRenderer()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            SupportsPlaybackMuteIndicator = true,
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Static,
            },
        };
        TestContext context = CreateContext(
            providerAvailable: true,
            profileValidator: ProfileSetValidator.CreateForLightingCapabilities(
                capabilities));
        context.Device.Capabilities = capabilities;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        LightingSettings lighting = LightingSettingsFactory.Static(
            new RgbColor(0x12, 0x34, 0x56),
            100);
        await context.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                lighting,
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);
        int lightingWritesBeforeUnmute = context.Device.LightingSetCalls;
        await context.Runtime.SetPlaybackMuteIndicatorAsync(
            true,
            CancellationToken.None);
        context.Device.PlaybackMuteFailures.Enqueue(new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            "The unmute frame was rejected."));

        ControlOperationResult failed =
            await context.Runtime.SetPlaybackMuteIndicatorAsync(
                false,
                CancellationToken.None);
        ControlOperationResult recovered =
            await context.Runtime.SetPlaybackMuteIndicatorAsync(
                false,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, failed.Status);
        Assert.Equal(ControlOperationStatus.Applied, recovered.Status);
        Assert.Equal([true, false, false], context.Device.PlaybackMuteIndicatorStates);
        Assert.Equal(lightingWritesBeforeUnmute + 1, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Device.LightingRendererOwnership);
    }

    [Fact]
    public async Task VerifiedReadFailure_PreservesOwnedReactiveInput()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Reactive,
            },
        };
        TestContext context = CreateContext(
            providerAvailable: true,
            profileValidator: ProfileSetValidator.CreateForLightingCapabilities(capabilities));
        context.Device.Capabilities = capabilities;
        context.Device.ConfirmedState = new ConfirmedDeviceState(null, null, null, null);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                LightingSettingsFactory.Reactive(
                    new RgbColor(0, 255, 0),
                    77,
                    ReactiveLightingDuration.Medium),
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);

        context.Device.ReadStateFailure = new IOException("temporary read failure");
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        ServiceStatusSnapshot degraded = context.Runtime.GetStatus();
        Assert.True(degraded.DeviceVerified);
        Assert.True(degraded.WritesBlocked);
        Assert.Equal(
            LightingRendererOwnershipState.Reactive,
            degraded.LightingRendererOwnership);

        ControlOperationResult pulse = await context.Runtime.PulseReactiveKeyAsync(
            new ReactiveKeyPulse(53),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, pulse.Status);
        Assert.Equal(1, context.Device.ReactivePulseCalls);
    }

    [Fact]
    public async Task KeyPulse_AcceptsOwnedRippleRenderer()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Ripple,
            },
        };
        TestContext context = CreateContext(
            providerAvailable: true,
            profileValidator: ProfileSetValidator.CreateForLightingCapabilities(capabilities));
        context.Device.Capabilities = capabilities;
        context.Device.ConfirmedState = new ConfirmedDeviceState(null, null, null, null);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                LightingSettingsFactory.Ripple(new RgbColor(0, 255, 0), 77),
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);

        ControlOperationResult result = await context.Runtime.PulseReactiveKeyAsync(
            new ReactiveKeyPulse(53),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(1, context.Device.ReactivePulseCalls);
        Assert.Equal(
            LightingRendererOwnershipState.Ripple,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Theory]
    [InlineData(LightingRendererOwnershipState.None)]
    [InlineData(LightingRendererOwnershipState.Faulted)]
    public async Task LightingApplyFailsWhenRendererDoesNotRemainOwned(
        LightingRendererOwnershipState observedOwnership)
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Breathing,
            },
        };
        TestContext context = CreateContext(
            providerAvailable: true,
            profileValidator: ProfileSetValidator.CreateForLightingCapabilities(
                capabilities));
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            LightingEffects = capabilities.LightingEffects,
        };
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Lighting = null,
        };
        context.Device.LightingOwnershipAfterSet = observedOwnership;
        await context.Runtime.ObserveAsync(CancellationToken.None);

        ControlOperationResult result = await context.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                LightingSettingsFactory.BreathingSingle(
                    new RgbColor(0, 170, 255),
                    100),
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Contains("did not remain active", result.Message);
        Assert.NotNull(context.Runtime.GetStatus().LightingOverride);
        Assert.Equal(observedOwnership, context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task SavedReactiveProfile_DoesNotClaimRendererOwnershipAtStartup()
    {
        ProfileSetValidator validator = ProfileSetValidator.CreateForLightingCapabilities(
            DeviceCapabilities.Unavailable() with
            {
                LightingEffects = new HashSet<LightingEffect>
                {
                    LightingEffect.Off,
                    LightingEffect.Reactive,
                },
            });
        TestContext context = CreateContext(
            providerAvailable: true,
            profileValidator: validator);
        LightingSettings reactive = LightingSettingsFactory.Reactive(
            new RgbColor(0, 255, 0),
            77,
            ReactiveLightingDuration.Medium);
        ProfileSet profiles = context.Runtime.GetStatus().Profiles with
        {
            PluggedIn = context.Runtime.GetStatus().Profiles.PluggedIn with
            {
                Lighting = reactive,
            },
        };

        await context.Runtime.UpdateProfilesAsync(profiles, CancellationToken.None);
        ControlOperationResult pulse = await context.Runtime.PulseReactiveKeyAsync(
            new ReactiveKeyPulse(77),
            CancellationToken.None);

        Assert.Equal(LightingEffect.Reactive,
            context.Runtime.GetStatus().DesiredState.Lighting.Value.Effect);
        Assert.Equal(LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);
        Assert.Equal(ControlOperationStatus.Blocked, pulse.Status);
        Assert.Equal(0, context.Device.ReactivePulseCalls);
    }

    [Fact]
    public async Task ObservationPublishesBackgroundRendererFaultAndInvalidationClearsIt()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.LightingRendererOwnership =
            LightingRendererOwnershipState.Faulted;

        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(
            LightingRendererOwnershipState.Faulted,
            context.Runtime.GetStatus().LightingRendererOwnership);

        await context.Runtime.InvalidateLightingRendererAsync(CancellationToken.None);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task Invalidation_UsesLiveFaultStateAndNeverRestoresStaleOwnership()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);

        // The render loop faults after its last status publication but before the
        // suspend/removal invalidation reaches the serialized lighting port.
        context.Device.LightingRendererOwnership =
            LightingRendererOwnershipState.Faulted;

        await context.Runtime.InvalidateLightingRendererAsync(CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);

        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task Invalidation_RestoresCachedOwnershipWhenWindowsAlreadyClosedRenderer()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        LightingSettings expected =
            context.Runtime.GetStatus().DesiredState.Lighting.Value;

        // Windows can retire the HID stream before the service receives the
        // suspend callback. The last published ownership is still the service's
        // valid lifecycle restore intent.
        context.Device.LightingRendererOwnership =
            LightingRendererOwnershipState.None;

        await context.Runtime.InvalidateLightingRendererAsync(CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);

        Assert.Equal(2, context.Device.LightingSetCalls);
        Assert.Equal(expected, context.Device.ConfirmedState.Lighting);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task ResumeWithUnchangedPowerSource_RestoresBeforeObservingFirmwareOff()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        LightingSettings expected =
            context.Runtime.GetStatus().DesiredState.Lighting.Value;
        context.Device.LightingRendererOwnership =
            LightingRendererOwnershipState.None;

        await context.Runtime.InvalidateLightingRendererAsync(CancellationToken.None);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);

        Assert.Equal(2, context.Device.LightingSetCalls);
        Assert.Equal(expected, context.Device.ConfirmedState.Lighting);
        Assert.DoesNotContain(
            DeviceSubsystem.Lighting,
            context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task LifecycleRestore_WithoutExplicitOwnership_PerformsNoLightingWrite()
    {
        TestContext context = CreateWritableContext();

        await context.Runtime.InvalidateLightingRendererAsync(CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);

        Assert.Equal(0, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task LightingPowerPolicy_OverlappingReasonsSwitchOffOnceAndRestoreLatestIntent()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        LightingSettings expected = context.Runtime.GetStatus().DesiredState.Lighting.Value;
        ProfileSet profiles = context.Runtime.GetStatus().Profiles with
        {
            PluggedIn = context.Runtime.GetStatus().Profiles.PluggedIn with
            {
                LightingPowerPolicy = new LightingPowerPolicy(
                    turnOffWhenDisplayTurnsOff: true,
                    idleTimeoutMinutes: 5),
            },
        };
        await context.Runtime.UpdateProfilesAsync(profiles, CancellationToken.None);

        ControlOperationResult displayOff = await context.Runtime
            .SetLightingSuppressionAsync(
                LightingSuppressionReason.DisplayOff,
                suppressed: true,
                CancellationToken.None);
        ControlOperationResult idle = await context.Runtime.SetLightingSuppressionAsync(
            LightingSuppressionReason.UserIdle,
            suppressed: true,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, displayOff.Status);
        Assert.Equal(ControlOperationStatus.Stored, idle.Status);
        Assert.Equal(LightingEffect.Off, context.Device.ConfirmedState.Lighting!.Effect);
        Assert.Equal(2, context.Device.LightingSetCalls);
        Assert.Equal(
            [LightingSuppressionReason.DisplayOff, LightingSuppressionReason.UserIdle],
            context.Runtime.GetStatus().LightingSuppressions);
        Assert.Equal(expected, context.Runtime.GetStatus().DesiredState.Lighting.Value);

        ControlOperationResult displayOn = await context.Runtime
            .SetLightingSuppressionAsync(
                LightingSuppressionReason.DisplayOff,
                suppressed: false,
                CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Stored, displayOn.Status);
        Assert.Equal(2, context.Device.LightingSetCalls);

        ControlOperationResult activeAgain = await context.Runtime
            .SetLightingSuppressionAsync(
                LightingSuppressionReason.UserIdle,
                suppressed: false,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, activeAgain.Status);
        Assert.Equal(expected, context.Device.ConfirmedState.Lighting);
        Assert.Equal(3, context.Device.LightingSetCalls);
        Assert.Empty(context.Runtime.GetStatus().LightingSuppressions!);
    }

    [Fact]
    public async Task LightingPowerPolicy_PowerChangeWhileDisplayOffPersistsDestinationIntent()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        LightingSettings pluggedInLighting =
            context.Runtime.GetStatus().DesiredState.Lighting.Value;
        LightingSettings batteryLighting = LightingSettingsFactory.Static(
            new RgbColor(0, 96, 255),
            brightness: 40);
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        await context.Runtime.UpdateProfilesAsync(
            profiles with
            {
                PluggedIn = profiles.PluggedIn with
                {
                    LightingPowerPolicy = new LightingPowerPolicy(
                        turnOffWhenDisplayTurnsOff: true,
                        idleTimeoutMinutes: null),
                },
                Battery = profiles.Battery with
                {
                    Lighting = batteryLighting,
                    LightingPowerPolicy = new LightingPowerPolicy(
                        turnOffWhenDisplayTurnsOff: true,
                        idleTimeoutMinutes: null),
                },
            },
            CancellationToken.None);

        await context.Runtime.SetLightingSuppressionAsync(
            LightingSuppressionReason.DisplayOff,
            suppressed: true,
            CancellationToken.None);
        BatteryConfiguration savedWhilePluggedIn =
            await context.Store.LoadAsync(CancellationToken.None);
        context.Telemetry.Current = CreateTelemetry(PowerSource.ExternalUnknown);
        context.Device.AdapterPowerState = AdapterPowerState.Unknown;
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        BatteryConfiguration savedWhileUnclassified =
            await context.Store.LoadAsync(CancellationToken.None);

        Assert.Equal(
            PowerSource.PluggedIn,
            context.Runtime.GetStatus().ActivePowerSource);
        Assert.Equal(
            [LightingSuppressionReason.DisplayOff],
            context.Runtime.GetStatus().LightingSuppressions);
        Assert.Equal(
            pluggedInLighting,
            savedWhileUnclassified.Preferences.LastKnownLighting);

        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        BatteryConfiguration saved = await context.Store.LoadAsync(CancellationToken.None);
        var restarted = new ProfileAutomationState(
            saved.Profiles,
            saved.Preferences,
            PowerSource.Battery);

        Assert.Equal(LightingEffect.Off, context.Device.ConfirmedState.Lighting!.Effect);
        Assert.Equal(
            pluggedInLighting,
            savedWhilePluggedIn.Preferences.LastKnownLighting);
        Assert.Equal(batteryLighting, saved.Preferences.LastKnownLighting);
        Assert.Null(restarted.LightingOverride);
        Assert.Equal(
            batteryLighting,
            restarted.Resolve(context.Device.ConfirmedState).Lighting.Value);
    }

    [Fact]
    public async Task LightingPowerPolicy_DisabledReasonIsBlockedWithoutHardwareWrite()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();

        ControlOperationResult result = await context.Runtime.SetLightingSuppressionAsync(
            LightingSuppressionReason.UserIdle,
            suppressed: true,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Empty(context.Runtime.GetStatus().LightingSuppressions!);
    }

    [Fact]
    public async Task LightingPowerPolicy_LowPowerGateStagesStateWithoutHardwareWrite()
    {
        var gate = new LowPowerGate();
        TestContext context = await CreateOwnedStaticLightingContextAsync(
            lowPowerGate: gate);
        ProfileSet profiles = context.Runtime.GetStatus().Profiles with
        {
            PluggedIn = context.Runtime.GetStatus().Profiles.PluggedIn with
            {
                LightingPowerPolicy = new LightingPowerPolicy(
                    turnOffWhenDisplayTurnsOff: true,
                    idleTimeoutMinutes: null),
            },
        };
        await context.Runtime.UpdateProfilesAsync(profiles, CancellationToken.None);
        int writesBeforeSuppression = context.Device.LightingSetCalls;
        gate.Enter();

        ControlOperationResult result = await context.Runtime
            .SetLightingSuppressionAsync(
                LightingSuppressionReason.DisplayOff,
                suppressed: true,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, result.Status);
        Assert.Equal(writesBeforeSuppression, context.Device.LightingSetCalls);
        Assert.Equal(
            [LightingSuppressionReason.DisplayOff],
            context.Runtime.GetStatus().LightingSuppressions);
        Assert.Contains("without a firmware write", result.Message, StringComparison.Ordinal);

        gate.Exit();
        ControlOperationResult resumed = await context.Runtime
            .SetLightingSuppressionAsync(
                LightingSuppressionReason.DisplayOff,
                suppressed: false,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, resumed.Status);
        Assert.Equal(writesBeforeSuppression + 1, context.Device.LightingSetCalls);
        Assert.Empty(context.Runtime.GetStatus().LightingSuppressions!);
    }

    [Fact]
    public async Task LightingPowerPolicy_DeferredUnsuppressionReconcilesAfterGateExit()
    {
        var gate = new LowPowerGate();
        TestContext context = await CreateOwnedStaticLightingContextAsync(
            lowPowerGate: gate);
        ProfileSet profiles = context.Runtime.GetStatus().Profiles with
        {
            PluggedIn = context.Runtime.GetStatus().Profiles.PluggedIn with
            {
                LightingPowerPolicy = new LightingPowerPolicy(
                    turnOffWhenDisplayTurnsOff: true,
                    idleTimeoutMinutes: null),
            },
        };
        await context.Runtime.UpdateProfilesAsync(profiles, CancellationToken.None);
        await context.Runtime.SetLightingSuppressionAsync(
            LightingSuppressionReason.DisplayOff,
            suppressed: true,
            CancellationToken.None);
        int writesWhileSuppressed = context.Device.LightingSetCalls;
        gate.Enter();

        ControlOperationResult deferred = await context.Runtime
            .SetLightingSuppressionAsync(
                LightingSuppressionReason.DisplayOff,
                suppressed: false,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, deferred.Status);
        Assert.Equal(writesWhileSuppressed, context.Device.LightingSetCalls);
        Assert.Empty(context.Runtime.GetStatus().LightingSuppressions!);

        gate.Exit();
        ControlOperationResult reconciled = await context.Runtime
            .SetLightingSuppressionAsync(
                LightingSuppressionReason.DisplayOff,
                suppressed: false,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, reconciled.Status);
        Assert.Equal(writesWhileSuppressed + 1, context.Device.LightingSetCalls);
        Assert.NotEqual(LightingEffect.Off, context.Device.ConfirmedState.Lighting!.Effect);
    }

    [Fact]
    public async Task LightingPowerPolicy_ControllerAdvisoryDoesNotBlockConfiguredSuppression()
    {
        TestContext context = CreateWritableContext(
            conflicts: ["Razer Elevation Service"]);
        ProfileSet profiles = context.Runtime.GetStatus().Profiles with
        {
            PluggedIn = context.Runtime.GetStatus().Profiles.PluggedIn with
            {
                LightingPowerPolicy = new LightingPowerPolicy(
                    turnOffWhenDisplayTurnsOff: true,
                    idleTimeoutMinutes: null),
            },
        };
        await context.Runtime.UpdateProfilesAsync(profiles, CancellationToken.None);
        int writesBeforeSuppression = context.Device.LightingSetCalls;

        ControlOperationResult result = await context.Runtime
            .SetLightingSuppressionAsync(
                LightingSuppressionReason.DisplayOff,
                suppressed: true,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(writesBeforeSuppression + 1, context.Device.LightingSetCalls);
        Assert.Equal(LightingEffect.Off, context.Device.ConfirmedState.Lighting!.Effect);
        Assert.Equal(
            ["Razer Elevation Service"],
            context.Runtime.GetStatus().PotentialControllers);
        Assert.Equal(
            [LightingSuppressionReason.DisplayOff],
            context.Runtime.GetStatus().LightingSuppressions);
    }

    [Fact]
    public async Task LightingPowerPolicy_SynapseDefersSuppressionUntilControllerClears()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync(
            explicitAction: false);
        ProfileSet profiles = context.Runtime.GetStatus().Profiles with
        {
            PluggedIn = context.Runtime.GetStatus().Profiles.PluggedIn with
            {
                LightingPowerPolicy = new LightingPowerPolicy(
                    turnOffWhenDisplayTurnsOff: true,
                    idleTimeoutMinutes: null),
            },
        };
        await context.Runtime.UpdateProfilesAsync(profiles, CancellationToken.None);
        context.Monitor.Current = new ControllerPresenceObservation(
            ["RazerAppEngine"],
            null);

        ControlOperationResult result = await context.Runtime
            .SetLightingSuppressionAsync(
                LightingSuppressionReason.DisplayOff,
                suppressed: true,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, result.Status);
        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Equal(1, context.Provider.RelinquishCount);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);
        Assert.Equal(
            [LightingSuppressionReason.DisplayOff],
            context.Runtime.GetStatus().LightingSuppressions);
        Assert.Contains(
            "without changing externally controlled lighting",
            result.Message,
            StringComparison.Ordinal);

        context.Monitor.Current = new ControllerPresenceObservation([], null);
        ControlOperationResult restored = await context.Runtime
            .SetLightingSuppressionAsync(
                LightingSuppressionReason.DisplayOff,
                suppressed: false,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, restored.Status);
        Assert.Equal(2, context.Device.LightingSetCalls);
        Assert.Empty(context.Runtime.GetStatus().LightingSuppressions!);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task ProfileEdit_SynapsePreventsAutomaticSuppressionRestore()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        ProfileSet profiles = context.Runtime.GetStatus().Profiles with
        {
            PluggedIn = context.Runtime.GetStatus().Profiles.PluggedIn with
            {
                LightingPowerPolicy = new LightingPowerPolicy(
                    turnOffWhenDisplayTurnsOff: true,
                    idleTimeoutMinutes: null),
            },
        };
        await context.Runtime.UpdateProfilesAsync(profiles, CancellationToken.None);
        await context.Runtime.SetLightingSuppressionAsync(
            LightingSuppressionReason.DisplayOff,
            suppressed: true,
            CancellationToken.None);
        int writesWhileSuppressed = context.Device.LightingSetCalls;
        context.Monitor.Current = new ControllerPresenceObservation(
            ["RazerAppEngine"],
            null);

        await context.Runtime.UpdateProfilesAsync(
            profiles with
            {
                PluggedIn = profiles.PluggedIn with
                {
                    LightingPowerPolicy = LightingPowerPolicy.Disabled,
                },
            },
            CancellationToken.None);

        Assert.Equal(writesWhileSuppressed, context.Device.LightingSetCalls);
        Assert.Empty(context.Runtime.GetStatus().LightingSuppressions!);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);

        context.Monitor.Current = new ControllerPresenceObservation([], null);
        ControlOperationResult repeated = await context.Runtime
            .SetLightingSuppressionAsync(
                LightingSuppressionReason.DisplayOff,
                suppressed: false,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, repeated.Status);
        Assert.Equal(writesWhileSuppressed, context.Device.LightingSetCalls);
    }

    [Fact]
    public async Task LightingPowerPolicy_ExternalChangeStagesSuppressionWithoutWrite()
    {
        TestContext context = CreateWritableContext();
        ProfileSet profiles = context.Runtime.GetStatus().Profiles with
        {
            PluggedIn = context.Runtime.GetStatus().Profiles.PluggedIn with
            {
                LightingPowerPolicy = new LightingPowerPolicy(
                    turnOffWhenDisplayTurnsOff: true,
                    idleTimeoutMinutes: null),
            },
        };
        await context.Runtime.UpdateProfilesAsync(profiles, CancellationToken.None);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Lighting = LightingSettingsFactory.Static(
                new RgbColor(1, 2, 3),
                brightness: 25),
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        int writesBeforeSuppression = context.Device.LightingSetCalls;

        ControlOperationResult result = await context.Runtime
            .SetLightingSuppressionAsync(
                LightingSuppressionReason.DisplayOff,
                suppressed: true,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, result.Status);
        Assert.Equal(writesBeforeSuppression, context.Device.LightingSetCalls);
        Assert.Equal([DeviceSubsystem.Lighting], context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(
            [LightingSuppressionReason.DisplayOff],
            context.Runtime.GetStatus().LightingSuppressions);
    }

    [Theory]
    [InlineData("RazerAppEngine", null)]
    [InlineData("razer synapse service", null)]
    [InlineData("RazerAppEngine", "Controller detection was incomplete.")]
    public async Task PeriodicControllerRefresh_RelinquishesAutomaticLightingForSynapse(
        string? controller,
        string? detectionWarning)
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync(
            explicitAction: false);
        context.Monitor.Current = new ControllerPresenceObservation(
            controller is null ? [] : [controller],
            detectionWarning);

        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);
        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);

        ServiceStatusSnapshot refreshed = context.Runtime.GetStatus();
        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Device.LightingRendererOwnership);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            refreshed.LightingRendererOwnership);
        Assert.Null(refreshed.ConfirmedState!.Lighting);
        Assert.Equal(1, context.Provider.RelinquishCount);
        Assert.Equal(
            controller is null ? [] : [controller],
            refreshed.PotentialControllers);
        Assert.Equal(detectionWarning, refreshed.ControllerDetectionWarning);
        Assert.Contains(
            controller is not null ? "Razer Synapse is active" : "detection is incomplete",
            refreshed.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PeriodicControllerRefresh_DetectionWarningPreservesOwnedLighting()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync(
            explicitAction: false);
        context.Monitor.Current = new ControllerPresenceObservation(
            [],
            "Controller detection was incomplete.");

        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);

        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Equal(0, context.Provider.RelinquishCount);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
        Assert.Equal(
            "Controller detection was incomplete.",
            context.Runtime.GetStatus().ControllerDetectionWarning);

        context.Monitor.Current = new ControllerPresenceObservation([], null);
        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);

        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
        Assert.Null(context.Runtime.GetStatus().ControllerDetectionWarning);
    }

    [Fact]
    public async Task PeriodicControllerRefresh_IgnoresNonCompetingRazerHelpers()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync(
            explicitAction: false);
        context.Monitor.Current = new ControllerPresenceObservation(
            ["Razer Central Service", "Razer Elevation Service"],
            null);

        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);

        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Equal(0, context.Provider.RelinquishCount);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task VisibleObservation_SynapseRelinquishesWithoutFullInvalidation()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync(
            explicitAction: false);
        context.Monitor.Current = new ControllerPresenceObservation(
            ["RazerAppEngine"],
            null);

        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Equal(1, context.Provider.RelinquishCount);
        Assert.Equal(0, context.Provider.InvalidationCount);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task VisibleObservation_RelinquishFailurePreservesObservationHealth()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync(
            explicitAction: false);
        ServiceStatusSnapshot before = context.Runtime.GetStatus();
        context.Monitor.Current = new ControllerPresenceObservation(
            ["RazerAppEngine"],
            null);
        context.Provider.RelinquishFailure = new IOException("synthetic handoff failure");

        await context.Runtime.ObserveAsync(CancellationToken.None);

        ServiceStatusSnapshot after = context.Runtime.GetStatus();
        Assert.True(after.DeviceVerified);
        Assert.False(after.WritesBlocked);
        Assert.Equal(before.KeyboardDriverModeActive, after.KeyboardDriverModeActive);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            after.LightingRendererOwnership);
        Assert.Contains(
            "Keyboard lighting control could not be relinquished",
            after.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Verified device access failed",
            after.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PowerTransitionMessageOutranksLightingRelinquishDiagnostic()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync(
            explicitAction: false);
        await context.Runtime.ReconcileStartupProfileAsync(CancellationToken.None);
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        context.Monitor.Current = new ControllerPresenceObservation(
            ["RazerAppEngine"],
            null);
        context.Provider.RelinquishFailure =
            new IOException("synthetic handoff failure");
        context.Device.PerformanceFailure =
            new InvalidOperationException("write rejected");

        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Contains(
            "Performance write is indeterminate",
            context.Runtime.GetStatus().Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "could not be relinquished",
            context.Runtime.GetStatus().Message,
            StringComparison.Ordinal);
        Assert.Contains(
            context.History.Snapshot().CommandResults,
            result => result.Detail?.Contains(
                "Keyboard lighting control could not be relinquished",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task ControllerAdvisory_ExplicitLightingActionRetainsRenderer()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync(
            explicitAction: false);
        context.Monitor.Current = new ControllerPresenceObservation(
            ["RazerAppEngine"],
            null);
        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);

        LightingSettings requested = LightingSettingsFactory.Static(
            new RgbColor(255, 32, 64),
            brightness: 45);
        ControlOperationResult result = await context.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                requested,
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(2, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);

        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);
        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);

        Assert.Equal(2, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
        Assert.Equal(1, context.Provider.RelinquishCount);
    }

    [Fact]
    public async Task ExternalLightingOverridePolicy_PersistsAndReappliesLightingWithAdvisory()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync(
            explicitAction: false);
        context.Monitor.Current = new ControllerPresenceObservation(
            ["RazerAppEngine"],
            null);
        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);
        Assert.Equal(1, context.Provider.RelinquishCount);

        ControlOperationResult enabled =
            await context.Runtime.SetExternalLightingOverridePolicyAsync(
                true,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, enabled.Status);
        Assert.True(context.Runtime.GetStatus().AlwaysOverrideExternalLightingChanges);
        Assert.Equal(2, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);

        context.Device.LightingRendererOwnership =
            LightingRendererOwnershipState.None;
        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);

        Assert.Equal(3, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
        BatteryConfiguration saved = await context.Store.LoadAsync(CancellationToken.None);
        Assert.True(saved.Preferences.AlwaysOverrideExternalLightingChanges);
    }

    [Fact]
    public async Task FirmwareCompatibilityOverride_PersistsAndRefreshesDeviceAdmission()
    {
        TestContext context = CreateContext(providerAvailable: false);
        context.Provider.AvailableWhenUntestedFirmwareAllowed = true;

        ControlOperationResult enabled =
            await context.Runtime.SetFirmwareCompatibilityOverrideAsync(
                true,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, enabled.Status);
        Assert.True(context.Provider.AllowUntestedFirmware);
        Assert.True(context.Runtime.GetStatus().AllowUntestedFirmware);
        Assert.True(context.Runtime.GetStatus().DeviceVerified);
        BatteryConfiguration saved = await context.Store.LoadAsync(CancellationToken.None);
        Assert.True(saved.Preferences.AllowUntestedFirmware);

        ControlOperationResult disabled =
            await context.Runtime.SetFirmwareCompatibilityOverrideAsync(
                false,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, disabled.Status);
        Assert.False(context.Provider.AllowUntestedFirmware);
        Assert.False(context.Runtime.GetStatus().AllowUntestedFirmware);
        Assert.False(context.Runtime.GetStatus().DeviceVerified);
        saved = await context.Store.LoadAsync(CancellationToken.None);
        Assert.False(saved.Preferences.AllowUntestedFirmware);
    }

    [Fact]
    public async Task DisablingFirmwareCompatibilityOverride_RevokesOwnedRendererAfterUnknownProbe()
    {
        TestContext context = CreateContext(providerAvailable: false);
        context.Provider.AvailableWhenUntestedFirmwareAllowed = true;
        await context.Runtime.SetFirmwareCompatibilityOverrideAsync(
            true,
            CancellationToken.None);
        context.Device.LightingRendererOwnership =
            LightingRendererOwnershipState.Reactive;
        context.Provider.ReportUnknownFirmwareCompatibility = true;

        await context.Runtime.SetFirmwareCompatibilityOverrideAsync(
            false,
            CancellationToken.None);

        Assert.Equal(1, context.Provider.InvalidationCount);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Device.LightingRendererOwnership);
        Assert.False(context.Provider.TryGetOwnedKeyTriggeredLightingDevice(out _));
    }

    [Fact]
    public async Task WindowsDynamicLightingMode_PersistsAndBlocksKeyboardWrites()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            LampArrayLightingAccess = DeviceFeatureAccess.ObservedOnly,
        };
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
            LightingSettingsFactory.Off());
        await context.Runtime.ObserveAsync(CancellationToken.None);
        int writesBeforeDelegation = context.Device.LightingSetCalls;

        ControlOperationResult delegated =
            await context.Runtime.SetKeyboardLightingControlModeAsync(
                KeyboardLightingControlMode.WindowsDynamicLighting,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, delegated.Status);
        Assert.Equal(
            KeyboardLightingControlMode.WindowsDynamicLighting,
            context.Runtime.GetStatus().KeyboardLightingControlMode);
        Assert.Equal(writesBeforeDelegation, context.Device.LightingSetCalls);

        ControlOperationResult effect = await context.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                LightingSettingsFactory.Off(),
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);
        ControlOperationResult brightness =
            await context.Runtime.StepKeyboardBrightnessAsync(
                KeyboardBrightnessDirection.Increase,
                OverrideLifetime.UntilPowerSourceChange,
                CancellationToken.None);
        ControlOperationResult suppression =
            await context.Runtime.SetLightingSuppressionAsync(
                LightingSuppressionReason.DisplayOff,
                suppressed: true,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, effect.Status);
        Assert.Equal(ControlOperationStatus.Blocked, brightness.Status);
        Assert.Equal(ControlOperationStatus.Blocked, suppression.Status);
        Assert.Equal(writesBeforeDelegation, context.Device.LightingSetCalls);
        Assert.All(
            new[] { effect, brightness, suppression },
            result => Assert.Contains("Windows Dynamic Lighting", result.Message));

        BatteryConfiguration saved = await context.Store.LoadAsync(CancellationToken.None);
        Assert.Equal(
            KeyboardLightingControlMode.WindowsDynamicLighting,
            saved.Preferences.KeyboardLightingControlMode);
    }

    [Fact]
    public async Task WindowsDynamicLightingMode_InvalidatesOwnedRendererBeforeDelegating()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        context.Device.Capabilities = context.Device.Capabilities with
        {
            LampArrayLightingAccess = DeviceFeatureAccess.ObservedOnly,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);

        ControlOperationResult delegated =
            await context.Runtime.SetKeyboardLightingControlModeAsync(
                KeyboardLightingControlMode.WindowsDynamicLighting,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, delegated.Status);
        Assert.Equal(1, context.Provider.InvalidationCount);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Device.LightingRendererOwnership);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);
        Assert.Equal(
            KeyboardLightingControlMode.WindowsDynamicLighting,
            context.Runtime.GetStatus().KeyboardLightingControlMode);
    }

    [Fact]
    public async Task WindowsDynamicLightingMode_DoesNotDelegateWhenRendererInvalidationFails()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        context.Device.Capabilities = context.Device.Capabilities with
        {
            LampArrayLightingAccess = DeviceFeatureAccess.ObservedOnly,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Provider.InvalidationFailure =
            new InvalidOperationException("Synthetic invalidation failure.");

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.Runtime.SetKeyboardLightingControlModeAsync(
                KeyboardLightingControlMode.WindowsDynamicLighting,
                CancellationToken.None));

        Assert.Contains("Synthetic invalidation failure", failure.Message);
        Assert.Equal(
            KeyboardLightingControlMode.OpenBlade,
            context.Runtime.GetStatus().KeyboardLightingControlMode);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
        BatteryConfiguration saved = await context.Store.LoadAsync(CancellationToken.None);
        Assert.Equal(
            KeyboardLightingControlMode.OpenBlade,
            saved.Preferences.KeyboardLightingControlMode);
    }

    [Fact]
    public async Task WindowsDynamicLightingMode_RestoresRendererWhenPersistenceFails()
    {
        bool failPersistence = false;
        TestContext context = await CreateOwnedStaticLightingContextAsync(
            (_, _) => failPersistence
                ? Task.FromException(new IOException("Synthetic persistence failure."))
                : Task.CompletedTask);
        context.Device.Capabilities = context.Device.Capabilities with
        {
            LampArrayLightingAccess = DeviceFeatureAccess.ObservedOnly,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        failPersistence = true;

        IOException failure = await Assert.ThrowsAsync<IOException>(
            () => context.Runtime.SetKeyboardLightingControlModeAsync(
                KeyboardLightingControlMode.WindowsDynamicLighting,
                CancellationToken.None));

        Assert.Contains("Synthetic persistence failure", failure.Message);
        Assert.Equal(1, context.Provider.InvalidationCount);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Device.LightingRendererOwnership);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
        Assert.Equal(
            KeyboardLightingControlMode.OpenBlade,
            context.Runtime.GetStatus().KeyboardLightingControlMode);
    }

    [Fact]
    public async Task WindowsDynamicLightingMode_PersistenceFailurePausesRollbackUntilControllerCheckRecovers()
    {
        bool failPersistence = false;
        TestContext context = await CreateOwnedStaticLightingContextAsync(
            (_, _) => failPersistence
                ? Task.FromException(new IOException("Synthetic persistence failure."))
                : Task.CompletedTask);
        context.Device.Capabilities = context.Device.Capabilities with
        {
            LampArrayLightingAccess = DeviceFeatureAccess.ObservedOnly,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Monitor.Current = new ControllerPresenceObservation(
            [],
            "Controller detection was incomplete.");
        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);
        failPersistence = true;

        IOException failure = await Assert.ThrowsAsync<IOException>(
            () => context.Runtime.SetKeyboardLightingControlModeAsync(
                KeyboardLightingControlMode.WindowsDynamicLighting,
                CancellationToken.None));

        Assert.Contains("Synthetic persistence failure", failure.Message);
        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);
        Assert.Equal(
            KeyboardLightingControlMode.OpenBlade,
            context.Runtime.GetStatus().KeyboardLightingControlMode);

        context.Monitor.Current = new ControllerPresenceObservation([], null);
        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);

        Assert.Equal(2, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task WindowsDynamicLightingMode_ReportsFailedPersistenceAndRollback()
    {
        bool failPersistence = false;
        TestContext? context = null;
        context = await CreateOwnedStaticLightingContextAsync(
            (_, _) =>
            {
                if (!failPersistence)
                {
                    return Task.CompletedTask;
                }

                context!.Provider.Available = false;
                return Task.FromException(
                    new IOException("Synthetic persistence failure."));
            });
        context.Device.Capabilities = context.Device.Capabilities with
        {
            LampArrayLightingAccess = DeviceFeatureAccess.ObservedOnly,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        failPersistence = true;

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
            () => context.Runtime.SetKeyboardLightingControlModeAsync(
                KeyboardLightingControlMode.WindowsDynamicLighting,
                CancellationToken.None));

        Assert.Collection(
            failure.InnerExceptions,
            exception => Assert.Contains(
                "Synthetic persistence failure",
                exception.Message),
            exception => Assert.Contains(
                "previous lighting renderer could not be restored",
                exception.Message,
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            KeyboardLightingControlMode.OpenBlade,
            context.Runtime.GetStatus().KeyboardLightingControlMode);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task Restart_RestoresPersistedLastKnownLighting()
    {
        TestContext original = await CreateOwnedStaticLightingContextAsync();
        LightingSettings expected = original.Runtime.GetStatus().DesiredState.Lighting.Value;
        BatteryConfiguration saved = await original.Store.LoadAsync(CancellationToken.None);
        Assert.Equal(expected, saved.Preferences.LastKnownLighting);

        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Static,
            },
        };
        TestContext restarted = CreateContext(
            providerAvailable: true,
            profileValidator: ProfileSetValidator.CreateForLightingCapabilities(capabilities));
        restarted.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            LightingEffects = capabilities.LightingEffects,
        };
        restarted.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
            null);

        await restarted.Runtime.ObserveAsync(CancellationToken.None);
        await restarted.Runtime.RestoreLightingRendererAsync(CancellationToken.None);

        Assert.Equal(1, restarted.Device.LightingSetCalls);
        Assert.Equal(expected, restarted.Device.ConfirmedState.Lighting);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            restarted.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task UnexpectedRestart_ObservesBeforeRecoveringOwnedProducers()
    {
        var temperatures = new FixedFanTemperatureSource(default);
        DeviceCapabilities recoveryCapabilities =
            DeviceCapabilities.SafeTargetDefaults() with
            {
                FanModes = new HashSet<FanMode>
                {
                    FanMode.Automatic,
                    FanMode.ManualCurve,
                    FanMode.Maximum,
                },
                LightingEffects = new HashSet<LightingEffect>
                {
                    LightingEffect.Off,
                    LightingEffect.Static,
                },
                FanSpeedRange = new FanSpeedRange(2000, 5000, 100),
            };
        TestContext context = CreateContext(
            providerAvailable: true,
            initialize: false,
            fanTemperatureSource: temperatures,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(
                recoveryCapabilities));
        LightingSettings lighting = LightingSettingsFactory.Static(
            new RgbColor(32, 255, 64),
            brightness: 55);
        var recoveryCurve = new ManualFanCurve(
            ManualFanCurve.Default.Cpu
                .Select(point => new FanCurvePoint(
                    point.TemperatureCelsius,
                    Math.Min(point.SpeedRpm, 5000)))
                .ToArray(),
            ManualFanCurve.Default.Gpu
                .Select(point => new FanCurvePoint(
                    point.TemperatureCelsius,
                    Math.Min(point.SpeedRpm, 5000)))
                .ToArray(),
            ManualFanCurve.Default.UseHigherTemperatureForBoth);
        var fanControl = new FanControlSettings(
            FanMode.ManualCurve,
            2800,
            recoveryCurve);
        BatteryConfiguration configuration = await context.Store.LoadAsync(
            CancellationToken.None);
        configuration = configuration with
        {
            Profiles = configuration.Profiles with
            {
                PluggedIn = configuration.Profiles.PluggedIn with
                {
                    Performance = PerformancePreset.Balanced,
                    Fans = FanMode.ManualCurve,
                    FanControl = fanControl,
                    Lighting = lighting,
                },
            },
            Preferences = configuration.Preferences with
            {
                LastKnownLighting = lighting,
            },
        };
        var overrides = new PersistentOverrideState(
            PerformancePreset.Balanced,
            FanMode.ManualCurve,
            lighting,
            FanControl: fanControl);
        context.Device.Capabilities = recoveryCapabilities;
        // Firmware reports one generic manual state after the old service
        // process disappears; it cannot report the host-side curve subtype.
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Maximum,
            ConfirmedBattery(80),
            null);
        context.Device.FanTargetsAvailable = true;
        context.Runtime.Initialize(
            configuration,
            PowerSource.PluggedIn,
            overrides,
            recoveringUnexpectedExit: true);

        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(0, context.Device.TotalSetterCalls);
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(overrides.FanControl, context.Runtime.GetStatus().FanControlOverride);
        Assert.Equal(overrides.Lighting, context.Runtime.GetStatus().LightingOverride!.Value);

        await context.Runtime.ReconcileUnexpectedExitAsync(CancellationToken.None);
        Assert.Equal(0, context.Device.TotalSetterCalls);

        await context.Runtime.PrepareManualFanCurveRecoveryAsync(
            CancellationToken.None);
        Assert.Equal(
            5000,
            Assert.Single(context.Device.FanTargets).Fan1Rpm);
        await context.Runtime.MaintainFanCurveAsync(CancellationToken.None);
        Assert.Contains(
            "temperature is unavailable",
            context.Runtime.GetStatus().Message,
            StringComparison.OrdinalIgnoreCase);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);

        Assert.Equal(1, temperatures.ReadCount);
        Assert.All(
            context.Device.FanTargets,
            target => Assert.Equal(
                new FanTargetSpeeds(
                    5000,
                    5000,
                    FailSafe: true),
                target));
        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Equal(lighting, context.Device.ConfirmedState.Lighting);
    }

    [Theory]
    [InlineData(PowerSource.PluggedIn)]
    [InlineData(PowerSource.Battery)]
    public async Task UnexpectedRestart_WithoutActiveUserAppliesOneThermalSafetyFloor(
        PowerSource powerSource)
    {
        var temperatures = new FixedFanTemperatureSource(
            new FanTemperatureReadings(65, 60));
        DeviceCapabilities capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            FanModes = new HashSet<FanMode>
            {
                FanMode.Automatic,
                FanMode.Fixed,
                FanMode.ManualCurve,
                FanMode.Maximum,
            },
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Spectrum,
            },
            FanSpeedRange = new FanSpeedRange(2000, 5400, 100),
        };
        LightingSettings lighting = LightingSettingsFactory.SpectrumCycling(brightness: 35);
        var fanControl = new FanControlSettings(
            FanMode.ManualCurve,
            3700,
            ManualFanCurve.Default);
        var repository = new FixedUserConfigurationRepository(
            new BatteryConfiguration(
                ProfileDefaults.CreateSafeDefaults(),
                new BatteryAutomationPreferences(
                    ProtectionEnabled: true,
                    LastKnownLighting: lighting),
                Recovered: false),
            new PersistentOverrideState(
                Performance: null,
                Fans: FanMode.ManualCurve,
                Lighting: lighting,
                FanControl: fanControl),
            directory);
        TestContext context = CreateContext(
            providerAvailable: true,
            initialize: false,
            fanTemperatureSource: temperatures,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(capabilities),
            userConfigurationRepository: repository,
            initialPowerSource: powerSource);
        BatteryConfiguration machineConfiguration = await context.Store.LoadAsync(
            CancellationToken.None);
        context.Device.Capabilities = capabilities;
        // Firmware reports one generic manual state after the old process dies;
        // it does not prove that the retained target is the admitted maximum.
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Maximum,
            ConfirmedBattery(80),
            LightingSettingsFactory.Off());
        context.Device.FanTargetsAvailable = true;
        context.Runtime.Initialize(
            machineConfiguration,
            powerSource,
            PersistentOverrideState.Empty,
            recoveringUnexpectedExit: true);

        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.MaintainDriverModeAsync(CancellationToken.None);
        await context.Runtime.ReconcileUnexpectedExitAsync(CancellationToken.None);
        await context.Runtime.PrepareManualFanCurveRecoveryAsync(CancellationToken.None);
        await context.Runtime.MaintainFanCurveAsync(CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);

        Assert.Equal(0, repository.LoadCount);
        Assert.Equal(
            new FanTargetSpeeds(5400, 5400, FailSafe: true),
            Assert.Single(context.Device.FanTargets));
        Assert.Equal(0, temperatures.ReadCount);
        Assert.Equal(0, context.Device.LightingSetCalls);
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
        Assert.Null(context.Runtime.GetStatus().FanControlOverride);
        Assert.Equal(
            FanMode.Automatic,
            context.Runtime.GetStatus().DesiredState.Fans.Value);

        await context.Runtime.PrepareManualFanCurveRecoveryAsync(CancellationToken.None);
        await context.Runtime.MaintainFanCurveAsync(CancellationToken.None);

        Assert.Single(context.Device.FanTargets);
        Assert.Equal(0, temperatures.ReadCount);

        ControlOperationResult shutdown = await context.Runtime.PrepareForShutdownAsync(
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, shutdown.Status);
        Assert.Equal([FanMode.Automatic], context.Device.FanWrites);
    }

    [Fact]
    public async Task UnexpectedRestart_UsbCHandbackWaitsForUserBeforeReflooringOnAc()
    {
        var temperatures = new FixedFanTemperatureSource(default);
        DeviceCapabilities capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            FanModes = new HashSet<FanMode>
            {
                FanMode.Automatic,
                FanMode.ManualCurve,
                FanMode.Maximum,
            },
            FanSpeedRange = new FanSpeedRange(2000, 5400, 100),
        };
        var fanControl = new FanControlSettings(
            FanMode.ManualCurve,
            3700,
            ManualFanCurve.Default);
        var repository = new FixedUserConfigurationRepository(
            new BatteryConfiguration(
                ProfileDefaults.CreateSafeDefaults(),
                new BatteryAutomationPreferences(ProtectionEnabled: true),
                Recovered: false),
            new PersistentOverrideState(
                Performance: null,
                Fans: FanMode.ManualCurve,
                Lighting: null,
                FanControl: fanControl),
            directory);
        TestContext context = CreateContext(
            providerAvailable: true,
            initialize: false,
            fanTemperatureSource: temperatures,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(capabilities),
            userConfigurationRepository: repository,
            initialPowerSource: PowerSource.PluggedIn);
        BatteryConfiguration machineConfiguration = await context.Store.LoadAsync(
            CancellationToken.None);
        context.Device.Capabilities = capabilities;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Maximum,
            ConfirmedBattery(80),
            LightingSettingsFactory.Off());
        context.Device.FanTargetsAvailable = true;
        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        context.Runtime.Initialize(
            machineConfiguration,
            PowerSource.PluggedIn,
            PersistentOverrideState.Empty,
            recoveringUnexpectedExit: true);

        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.ReconcileUnexpectedExitAsync(CancellationToken.None);
        await context.Runtime.PrepareManualFanCurveRecoveryAsync(CancellationToken.None);

        Assert.Single(context.Device.FanTargets);
        Assert.Empty(context.Device.FanWrites);
        Assert.Equal(0, repository.LoadCount);
        Assert.Equal(0, temperatures.ReadCount);

        context.Telemetry.Current = CreateTelemetry(PowerSource.UsbC);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Single(context.Device.FanTargets);
        Assert.Equal([FanMode.Automatic], context.Device.FanWrites);
        Assert.Equal(FanMode.Automatic, context.Device.ConfirmedState.Fans);

        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Single(context.Device.FanTargets);
        Assert.Equal([FanMode.Automatic], context.Device.FanWrites);
        Assert.Equal(0, repository.LoadCount);
        Assert.Equal(0, temperatures.ReadCount);

        var userSid = new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1001");
        await context.Runtime.ActivateUserAsync(userSid, CancellationToken.None);

        Assert.Equal(2, context.Device.FanTargets.Count);
        Assert.All(
            context.Device.FanTargets,
            target => Assert.Equal(
                new FanTargetSpeeds(5400, 5400, FailSafe: true),
                target));
        Assert.Equal(1, repository.LoadCount);
    }

    [Fact]
    public async Task UnexpectedRestart_InitialUsbCManualStateRemainsUnowned()
    {
        FixedUserConfigurationRepository repository =
            CreateManualCurveRecoveryRepository();
        TestContext context = await CreateDeferredRecoveryContextAsync(
            repository,
            initialPowerSource: PowerSource.UsbC);

        Assert.Equal(PowerSource.UsbC, context.Runtime.GetStatus().ActivePowerSource);
        Assert.Equal(FanMode.Maximum, context.Runtime.GetStatus().ConfirmedState!.Fans);
        Assert.Equal(FanMode.Automatic, context.Runtime.GetStatus().DesiredState.Fans.Value);
        Assert.Equal(0, context.Device.FanSetCalls);
        Assert.Empty(context.Device.FanTargets);
        Assert.Empty(context.Device.FanWrites);
        Assert.Equal(0, repository.LoadCount);

        var userSid = new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1001");
        await context.Runtime.ActivateUserAsync(userSid, CancellationToken.None);
        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);

        Assert.Equal(0, context.Device.FanSetCalls);
        Assert.Empty(context.Device.FanTargets);
        Assert.Empty(context.Device.FanWrites);
        Assert.Equal(1, repository.LoadCount);
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
    }

    [Fact]
    public async Task UnexpectedRestart_IndeterminateUsbCHandbackIsNotRetried()
    {
        var repository = new FixedUserConfigurationRepository(
            new BatteryConfiguration(
                ProfileDefaults.CreateSafeDefaults(),
                new BatteryAutomationPreferences(ProtectionEnabled: true),
                Recovered: false),
            new PersistentOverrideState(
                Performance: null,
                Fans: FanMode.ManualCurve,
                Lighting: null,
                FanControl: new FanControlSettings(
                    FanMode.ManualCurve,
                    3700,
                    ManualFanCurve.Default)),
            directory);
        TestContext context = await CreateDeferredRecoveryContextAsync(repository);
        Assert.Single(context.Device.FanTargets);

        using var cancellation = new CancellationTokenSource();
        context.Device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Fans)
            {
                cancellation.Cancel();
            }
        };
        context.Telemetry.Current = CreateTelemetry(PowerSource.UsbC);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Runtime.HandlePowerSourceChangedAsync(cancellation.Token));

        Assert.Equal(2, context.Device.FanSetCalls);
        Assert.Equal([FanMode.Automatic], context.Device.FanWrites);
        context.Device.AfterSetter = null;

        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);
        _ = await context.Runtime.PrepareForShutdownAsync(CancellationToken.None);

        Assert.Equal(2, context.Device.FanSetCalls);
        Assert.Single(context.Device.FanTargets);
        Assert.Equal([FanMode.Automatic], context.Device.FanWrites);
    }

    [Fact]
    public async Task UnexpectedRestart_FreshAutomaticStateBlocksStaleUsbCHandback()
    {
        var repository = CreateManualCurveRecoveryRepository();
        TestContext context = await CreateDeferredRecoveryContextAsync(repository);
        int scopedPreRead = context.Device.StateReadCalls + 1;
        context.Device.AfterReadStateCall = call =>
        {
            if (call == scopedPreRead)
            {
                context.Device.ConfirmedState = context.Device.ConfirmedState with
                {
                    Fans = FanMode.Automatic,
                };
            }
        };
        context.Telemetry.Current = CreateTelemetry(PowerSource.UsbC);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Single(context.Device.FanTargets);
        Assert.Empty(context.Device.FanWrites);
        Assert.Equal(
            [DeviceSubsystem.Fans],
            context.Runtime.GetStatus().ExternalChanges);
    }

    [Fact]
    public async Task UnexpectedRestart_IndeterminateThermalFloorBlocksLaterCurveWrites()
    {
        var repository = CreateManualCurveRecoveryRepository();
        TestContext context = await CreateDeferredRecoveryContextAsync(
            repository,
            prepareFanRecovery: false);
        using var cancellation = new CancellationTokenSource();
        context.Device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Fans)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Runtime.PrepareManualFanCurveRecoveryAsync(cancellation.Token));

        Assert.Single(context.Device.FanTargets);
        context.Device.AfterSetter = null;
        var userSid = new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1001");
        await context.Runtime.ActivateUserAsync(userSid, CancellationToken.None);
        await context.Runtime.MaintainFanCurveAsync(CancellationToken.None);

        Assert.Single(context.Device.FanTargets);
        Assert.Equal(
            [DeviceSubsystem.Fans],
            context.Runtime.GetStatus().ExternalChanges);
    }

    [Fact]
    public async Task UnexpectedRestart_FreshAutomaticStateBlocksStaleThermalFloor()
    {
        var repository = CreateManualCurveRecoveryRepository();
        TestContext context = await CreateDeferredRecoveryContextAsync(
            repository,
            prepareFanRecovery: false);
        int scopedPreRead = context.Device.StateReadCalls + 1;
        context.Device.AfterReadStateCall = call =>
        {
            if (call == scopedPreRead)
            {
                context.Device.ConfirmedState = context.Device.ConfirmedState with
                {
                    Fans = FanMode.Automatic,
                };
            }
        };

        await context.Runtime.PrepareManualFanCurveRecoveryAsync(CancellationToken.None);

        Assert.Empty(context.Device.FanTargets);
        Assert.Empty(context.Device.FanWrites);
        var userSid = new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1001");
        await context.Runtime.ActivateUserAsync(userSid, CancellationToken.None);

        Assert.Empty(context.Device.FanTargets);
        Assert.Equal(
            [DeviceSubsystem.Fans],
            context.Runtime.GetStatus().ExternalChanges);
    }

    [Fact]
    public async Task UnexpectedRestart_CompetingControllerBlocksThermalFloor()
    {
        var repository = CreateManualCurveRecoveryRepository();
        TestContext context = await CreateDeferredRecoveryContextAsync(
            repository,
            prepareFanRecovery: false);
        context.Monitor.Current = new ControllerPresenceObservation(
            ["RazerAppEngine"],
            null);

        await context.Runtime.PrepareManualFanCurveRecoveryAsync(CancellationToken.None);

        Assert.Equal(0, context.Device.FanSetCalls);
        Assert.Empty(context.Device.FanTargets);
        Assert.Equal(
            ["RazerAppEngine"],
            context.Runtime.GetStatus().PotentialControllers);
    }

    [Theory]
    [InlineData(FanMode.Automatic)]
    [InlineData(FanMode.Fixed)]
    [InlineData(FanMode.Maximum)]
    public async Task UnexpectedRestart_SafetyFloorConvergesAuthorizedFanIntent(
        FanMode fanMode)
    {
        FanControlSettings fanControl = fanMode == FanMode.Fixed
            ? new FanControlSettings(
                FanMode.Fixed,
                3200,
                ManualFanCurve.Default)
            : FanControlSettings.FromLegacy(fanMode);
        var repository = new FixedUserConfigurationRepository(
            new BatteryConfiguration(
                ProfileDefaults.CreateSafeDefaults(),
                new BatteryAutomationPreferences(ProtectionEnabled: true),
                Recovered: false),
            new PersistentOverrideState(
                Performance: null,
                Fans: fanMode,
                Lighting: null,
                FanControl: fanControl),
            directory);
        TestContext context = await CreateDeferredRecoveryContextAsync(repository);
        var userSid = new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1001");

        await context.Runtime.ActivateUserAsync(userSid, CancellationToken.None);

        Assert.Single(context.Device.FanTargets);
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(fanControl, context.Runtime.GetStatus().FanControlOverride);
        if (fanMode == FanMode.Automatic)
        {
            Assert.Equal([FanMode.Automatic], context.Device.FanWrites);
        }
        else if (fanMode == FanMode.Fixed)
        {
            Assert.Equal([FanMode.Fixed], context.Device.FanWrites);
        }
        else
        {
            Assert.Empty(context.Device.FanWrites);
        }
    }

    [Fact]
    public async Task UnexpectedRestart_AutomaticHandoffRetainsManualEntryGate()
    {
        var fixedControl = new FanControlSettings(
            FanMode.Fixed,
            3200,
            ManualFanCurve.Default);
        var repository = new FixedUserConfigurationRepository(
            new BatteryConfiguration(
                ProfileDefaults.CreateSafeDefaults(),
                new BatteryAutomationPreferences(ProtectionEnabled: true),
                Recovered: false),
            new PersistentOverrideState(
                Performance: null,
                Fans: FanMode.Fixed,
                Lighting: null,
                FanControl: fixedControl),
            directory);
        TestContext context = await CreateDeferredRecoveryContextAsync(repository);
        context.Telemetry.Current = CreateTelemetry(PowerSource.UsbC);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Performance,
        };
        var userSid = new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1001");

        await context.Runtime.ActivateUserAsync(userSid, CancellationToken.None);

        Assert.Single(context.Device.FanTargets);
        Assert.Equal([FanMode.Automatic], context.Device.FanWrites);
        Assert.Contains(
            DeviceSubsystem.Fans,
            context.Runtime.GetStatus().ExternalChanges);
    }

    [Theory]
    [InlineData(PowerSource.PluggedIn)]
    [InlineData(PowerSource.UsbC)]
    public async Task UnexpectedRestart_DefersOwnedRecoveryUntilUserAndPowerAreCompatible(
        PowerSource initialPowerSource)
    {
        var temperatures = new FixedFanTemperatureSource(default);
        DeviceCapabilities capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            FanModes = new HashSet<FanMode>
            {
                FanMode.Automatic,
                FanMode.ManualCurve,
                FanMode.Maximum,
            },
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Spectrum,
            },
            FanSpeedRange = new FanSpeedRange(2000, 5400, 100),
        };
        LightingSettings lighting = LightingSettingsFactory.SpectrumCycling(brightness: 35);
        var fanControl = new FanControlSettings(
            FanMode.ManualCurve,
            3700,
            ManualFanCurve.Default);
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        var userConfiguration = new BatteryConfiguration(
            profiles,
            new BatteryAutomationPreferences(
                ProtectionEnabled: true,
                LastKnownLighting: lighting),
            Recovered: false);
        var userOverrides = new PersistentOverrideState(
            Performance: null,
            Fans: FanMode.ManualCurve,
            Lighting: lighting,
            FanControl: fanControl);
        var repository = new FixedUserConfigurationRepository(
            userConfiguration,
            userOverrides,
            directory);
        TestContext context = CreateContext(
            providerAvailable: true,
            initialize: false,
            fanTemperatureSource: temperatures,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(capabilities),
            userConfigurationRepository: repository,
            initialPowerSource: PowerSource.PluggedIn);
        BatteryConfiguration machineConfiguration = await context.Store.LoadAsync(
            CancellationToken.None);
        context.Device.Capabilities = capabilities;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Maximum,
            ConfirmedBattery(80),
            LightingSettingsFactory.Off());
        context.Device.LightingRendererOwnership = LightingRendererOwnershipState.None;
        context.Device.ScopedLightingReadback = context.Device.ConfirmedState with
        {
            Lighting = null,
        };
        context.Device.SuppressConfirmationFor = DeviceSubsystem.Lighting;
        context.Device.FanTargetsAvailable = true;
        context.Runtime.Initialize(
            machineConfiguration,
            PowerSource.PluggedIn,
            PersistentOverrideState.Empty,
            recoveringUnexpectedExit: true);

        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.MaintainDriverModeAsync(CancellationToken.None);
        await context.Runtime.ReconcileUnexpectedExitAsync(CancellationToken.None);
        await context.Runtime.PrepareManualFanCurveRecoveryAsync(CancellationToken.None);
        await context.Runtime.MaintainFanCurveAsync(CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);

        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
        Assert.Null(context.Runtime.GetStatus().FanControlOverride);
        Assert.Single(context.Device.FanTargets);
        Assert.Equal(0, context.Device.LightingSetCalls);

        var userSid = new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1001");
        if (initialPowerSource == PowerSource.UsbC)
        {
            // The cached status still says full AC. Recovery must classify the
            // current adapter again while it holds the operation gate.
            context.Telemetry.Current = CreateTelemetry(PowerSource.UsbC);
        }

        await context.Runtime.ActivateUserAsync(userSid, CancellationToken.None);
        if (initialPowerSource == PowerSource.UsbC)
        {
            Assert.Single(context.Device.FanTargets);
            Assert.Equal([FanMode.Automatic], context.Device.FanWrites);
            Assert.Equal(0, context.Device.LightingSetCalls);
            Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
            context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
            await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
            Assert.Single(context.Device.FanTargets);
            Assert.Equal(0, context.Device.LightingSetCalls);
            Assert.Equal(
                PowerSource.PluggedIn,
                context.Runtime.GetStatus().ActivePowerSource);
            Assert.Equal(
                FanMode.ManualCurve,
                context.Runtime.GetStatus().DesiredState.Fans.Value);
            context.Telemetry.Current = CreateTelemetry(PowerSource.UsbC);
            await context.Runtime.RefreshPowerSourceClassificationAsync(
                CancellationToken.None);
            Assert.Single(context.Device.FanTargets);
            Assert.Equal([FanMode.Automatic], context.Device.FanWrites);
            Assert.Equal(0, context.Device.LightingSetCalls);
            Assert.Equal(
                PowerSource.UsbC,
                context.Runtime.GetStatus().ActivePowerSource);
            Assert.NotEqual(
                FanMode.ManualCurve,
                context.Runtime.GetStatus().DesiredState.Fans.Value);
            context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
            await context.Runtime.RefreshPowerSourceClassificationAsync(
                CancellationToken.None);
            Assert.Single(context.Device.FanTargets);
            Assert.Equal(0, context.Device.LightingSetCalls);
            Assert.Equal(
                PowerSource.PluggedIn,
                context.Runtime.GetStatus().ActivePowerSource);
            await context.Runtime.ActivateUserAsync(userSid, CancellationToken.None);
        }

        int safetyFloorCount = initialPowerSource == PowerSource.UsbC ? 2 : 1;
        Assert.Equal(safetyFloorCount, context.Device.FanTargets.Count);
        Assert.All(
            context.Device.FanTargets,
            target => Assert.Equal(5400, target.Fan1Rpm));
        await context.Runtime.MaintainFanCurveAsync(CancellationToken.None);

        ServiceStatusSnapshot recovered = context.Runtime.GetStatus();
        Assert.Empty(recovered.ExternalChanges);
        Assert.Equal(userOverrides.FanControl, recovered.FanControlOverride);
        Assert.Equal(userOverrides.Lighting, recovered.LightingOverride!.Value);
        Assert.Equal(1, repository.LoadCount);
        Assert.Equal(1, temperatures.ReadCount);
        Assert.Equal(safetyFloorCount + 1, context.Device.FanTargets.Count);
        Assert.All(
            context.Device.FanTargets,
            target => Assert.Equal(
                new FanTargetSpeeds(5400, 5400, FailSafe: true),
                target));
        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.Spectrum,
            recovered.LightingRendererOwnership);
        Assert.Contains(
            context.History.Snapshot().CommandResults,
            result => result.Action == "Reconcile"
                && result.Detail?.Contains(
                    "No external control changes were observed",
                    StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task UnexpectedRestart_SessionLossDuringObservationDoesNotRecoverStaleUser()
    {
        LightingSettings lighting = LightingSettingsFactory.SpectrumCycling(brightness: 35);
        var fanControl = new FanControlSettings(
            FanMode.ManualCurve,
            3700,
            ManualFanCurve.Default);
        var repository = new FixedUserConfigurationRepository(
            new BatteryConfiguration(
                ProfileDefaults.CreateSafeDefaults(),
                new BatteryAutomationPreferences(
                    ProtectionEnabled: true,
                    LastKnownLighting: lighting),
                Recovered: false),
            new PersistentOverrideState(
                Performance: null,
                Fans: FanMode.ManualCurve,
                Lighting: lighting,
                FanControl: fanControl),
            directory);
        TestContext context = await CreateDeferredRecoveryContextAsync(repository);
        var userSid = new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1001");
        bool controlsSession = true;
        int fanPreReadCall = context.Device.StateReadCalls + 1;
        context.Device.AfterReadStateCall = call =>
        {
            if (call == fanPreReadCall)
            {
                controlsSession = false;
            }
        };

        await context.Runtime.ActivateUserAsync(
            userSid,
            () => controlsSession,
            CancellationToken.None);

        Assert.Single(context.Device.FanTargets);
        Assert.Equal(0, context.Device.LightingSetCalls);
        await context.Runtime.MaintainFanCurveAsync(CancellationToken.None);
        Assert.Single(context.Device.FanTargets);

        controlsSession = true;
        context.Device.AfterReadStateCall = null;
        await context.Runtime.ActivateUserAsync(
            userSid,
            () => controlsSession,
            CancellationToken.None);

        Assert.Single(context.Device.FanTargets);
        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Equal(2, repository.LoadCount);
    }

    [Fact]
    public async Task UnexpectedRestart_ThermalFloorPreSubmissionCancellationRearmsAttempt()
    {
        LightingSettings lighting = LightingSettingsFactory.SpectrumCycling(brightness: 35);
        var repository = new FixedUserConfigurationRepository(
            new BatteryConfiguration(
                ProfileDefaults.CreateSafeDefaults(),
                new BatteryAutomationPreferences(
                    ProtectionEnabled: true,
                    LastKnownLighting: lighting),
                Recovered: false),
            new PersistentOverrideState(
                Performance: null,
                Fans: FanMode.ManualCurve,
                Lighting: lighting,
                FanControl: new FanControlSettings(
                    FanMode.ManualCurve,
                    3700,
                    ManualFanCurve.Default)),
            directory);
        TestContext context = await CreateDeferredRecoveryContextAsync(
            repository,
            prepareFanRecovery: false);
        using var cancellation = new CancellationTokenSource();
        int fanPreReadCall = context.Device.StateReadCalls + 1;
        context.Device.AfterReadStateCall = call =>
        {
            if (call == fanPreReadCall)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Runtime.PrepareManualFanCurveRecoveryAsync(cancellation.Token));

        Assert.Empty(context.Device.FanTargets);
        Assert.Equal(0, context.Device.LightingSetCalls);
        await context.Runtime.MaintainFanCurveAsync(CancellationToken.None);
        Assert.Empty(context.Device.FanTargets);

        context.Device.AfterReadStateCall = null;
        await context.Runtime.PrepareManualFanCurveRecoveryAsync(CancellationToken.None);
        await context.Runtime.PrepareManualFanCurveRecoveryAsync(CancellationToken.None);

        Assert.Single(context.Device.FanTargets);

        var userSid = new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1001");
        await context.Runtime.ActivateUserAsync(userSid, CancellationToken.None);

        Assert.Single(context.Device.FanTargets);
        Assert.Equal(1, context.Device.LightingSetCalls);
    }

    [Fact]
    public async Task UnexpectedRestart_PreLightingCancellationKeepsRestorePending()
    {
        LightingSettings lighting = LightingSettingsFactory.SpectrumCycling(brightness: 35);
        var repository = new FixedUserConfigurationRepository(
            new BatteryConfiguration(
                ProfileDefaults.CreateSafeDefaults(),
                new BatteryAutomationPreferences(
                    ProtectionEnabled: true,
                    LastKnownLighting: lighting),
                Recovered: false),
            new PersistentOverrideState(
                Performance: null,
                Fans: FanMode.ManualCurve,
                Lighting: lighting,
                FanControl: new FanControlSettings(
                    FanMode.ManualCurve,
                    3700,
                    ManualFanCurve.Default)),
            directory);
        TestContext context = await CreateDeferredRecoveryContextAsync(repository);
        var userSid = new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1001");
        using var cancellation = new CancellationTokenSource();
        context.Device.BeforeReadLightingState = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Runtime.ActivateUserAsync(
                userSid,
                static () => true,
                cancellation.Token));

        Assert.Single(context.Device.FanTargets);
        Assert.Equal(0, context.Device.LightingSetCalls);
        await context.Runtime.MaintainFanCurveAsync(CancellationToken.None);
        Assert.Single(context.Device.FanTargets);

        context.Device.BeforeReadLightingState = null;
        await context.Runtime.ActivateUserAsync(userSid, CancellationToken.None);

        Assert.Single(context.Device.FanTargets);
        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedRestart_LightingChangeAfterClassificationHonorsPolicy(
        bool alwaysOverride)
    {
        LightingSettings lighting = LightingSettingsFactory.SpectrumCycling(brightness: 35);
        var repository = new FixedUserConfigurationRepository(
            new BatteryConfiguration(
                ProfileDefaults.CreateSafeDefaults(),
                new BatteryAutomationPreferences(
                    ProtectionEnabled: true,
                    AlwaysOverrideExternalLightingChanges: alwaysOverride,
                    LastKnownLighting: lighting),
                Recovered: false),
            new PersistentOverrideState(
                Performance: null,
                Fans: FanMode.ManualCurve,
                Lighting: lighting,
                FanControl: new FanControlSettings(
                    FanMode.ManualCurve,
                    3700,
                    ManualFanCurve.Default)),
            directory);
        TestContext context = await CreateDeferredRecoveryContextAsync(repository);
        LightingSettings external = LightingSettingsFactory.Static(
            new RgbColor(255, 0, 0),
            brightness: 40);
        bool externalChangeInjected = false;
        context.Device.BeforeReadLightingState = () =>
        {
            if (!externalChangeInjected)
            {
                externalChangeInjected = true;
                context.Device.ScopedLightingReadback =
                    context.Device.ConfirmedState with { Lighting = external };
                context.Device.LightingRendererOwnership =
                    LightingRendererOwnershipState.Static;
            }
        };
        var userSid = new System.Security.Principal.SecurityIdentifier(
            "S-1-5-21-1-2-3-1001");

        await context.Runtime.ActivateUserAsync(userSid, CancellationToken.None);

        ServiceStatusSnapshot recovered = context.Runtime.GetStatus();
        Assert.Single(context.Device.FanTargets);
        if (alwaysOverride)
        {
            Assert.Equal(1, context.Device.LightingSetCalls);
            Assert.Empty(recovered.ExternalChanges);
            Assert.Equal(
                LightingRendererOwnershipState.Spectrum,
                recovered.LightingRendererOwnership);
        }
        else
        {
            Assert.Equal(0, context.Device.LightingSetCalls);
            Assert.Equal([DeviceSubsystem.Lighting], recovered.ExternalChanges);
            Assert.Equal(external, recovered.ConfirmedState!.Lighting);
            Assert.Contains(
                context.History.Snapshot().CommandResults,
                result => result.Detail?.Contains(
                    "changed after crash-recovery classification",
                    StringComparison.OrdinalIgnoreCase) == true);
        }
    }

    [Fact]
    public async Task UnexpectedRestart_LatchesExternalChangesWithoutRecoveryWrites()
    {
        DeviceCapabilities recoveryCapabilities =
            DeviceCapabilities.SafeTargetDefaults() with
            {
                FanModes = new HashSet<FanMode>
                {
                    FanMode.Automatic,
                    FanMode.ManualCurve,
                    FanMode.Maximum,
                },
                LightingEffects = new HashSet<LightingEffect>
                {
                    LightingEffect.Off,
                    LightingEffect.Static,
                },
            };
        TestContext context = CreateContext(
            providerAvailable: true,
            initialize: false,
            fanTemperatureSource: new FixedFanTemperatureSource(default),
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(
                recoveryCapabilities));
        LightingSettings lighting = LightingSettingsFactory.Static(
            new RgbColor(32, 255, 64),
            brightness: 55);
        var fanControl = new FanControlSettings(
            FanMode.ManualCurve,
            2800,
            ManualFanCurve.Default);
        BatteryConfiguration configuration = await context.Store.LoadAsync(
            CancellationToken.None);
        configuration = configuration with
        {
            Profiles = configuration.Profiles with
            {
                PluggedIn = configuration.Profiles.PluggedIn with
                {
                    Performance = PerformancePreset.Balanced,
                    Fans = FanMode.ManualCurve,
                    FanControl = fanControl,
                    Lighting = lighting,
                },
            },
            Preferences = configuration.Preferences with
            {
                LastKnownLighting = lighting,
            },
        };
        var overrides = new PersistentOverrideState(
            PerformancePreset.Balanced,
            FanMode.ManualCurve,
            lighting,
            FanControl: fanControl);
        context.Device.Capabilities = recoveryCapabilities;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
            LightingSettingsFactory.Off());
        context.Device.FanTargetsAvailable = true;
        context.Runtime.Initialize(
            configuration,
            PowerSource.PluggedIn,
            overrides,
            recoveringUnexpectedExit: true);

        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.ReconcileUnexpectedExitAsync(CancellationToken.None);
        await context.Runtime.PrepareManualFanCurveRecoveryAsync(
            CancellationToken.None);
        await context.Runtime.MaintainFanCurveAsync(CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);

        Assert.Equal(0, context.Device.TotalSetterCalls);
        Assert.Equal(
            [DeviceSubsystem.Fans, DeviceSubsystem.Lighting],
            context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(overrides.FanControl, context.Runtime.GetStatus().FanControlOverride);
        Assert.Equal(overrides.Lighting, context.Runtime.GetStatus().LightingOverride!.Value);
        Assert.Contains(
            context.History.Snapshot().CommandResults,
            result => result.Action == "Reconcile"
                && result.Detail?.Contains(
                    "external control changes remain latched",
                    StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public async Task UnexpectedRestart_DeviceArrivingAfterRecoveryAttemptDoesNotStartCurve()
    {
        DeviceCapabilities recoveryCapabilities =
            DeviceCapabilities.SafeTargetDefaults() with
            {
                FanModes = new HashSet<FanMode>
                {
                    FanMode.Automatic,
                    FanMode.ManualCurve,
                    FanMode.Maximum,
                },
            };
        TestContext context = CreateContext(
            providerAvailable: false,
            initialize: false,
            fanTemperatureSource: new FixedFanTemperatureSource(
                new FanTemperatureReadings(65, 60)),
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(
                recoveryCapabilities));
        var fanControl = new FanControlSettings(
            FanMode.ManualCurve,
            2800,
            ManualFanCurve.Default);
        BatteryConfiguration configuration = await context.Store.LoadAsync(
            CancellationToken.None);
        configuration = configuration with
        {
            Profiles = configuration.Profiles with
            {
                PluggedIn = configuration.Profiles.PluggedIn with
                {
                    Fans = FanMode.ManualCurve,
                    FanControl = fanControl,
                },
            },
        };
        context.Device.Capabilities = recoveryCapabilities;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Maximum,
            ConfirmedBattery(80),
            null);
        context.Device.FanTargetsAvailable = true;
        context.Runtime.Initialize(
            configuration,
            PowerSource.PluggedIn,
            new PersistentOverrideState(
                null,
                FanMode.ManualCurve,
                null,
                FanControl: fanControl),
            recoveringUnexpectedExit: true);

        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.ReconcileUnexpectedExitAsync(CancellationToken.None);
        await context.Runtime.PrepareManualFanCurveRecoveryAsync(
            CancellationToken.None);

        Assert.Equal([DeviceSubsystem.Fans], context.Runtime.GetStatus().ExternalChanges);
        Assert.Contains(
            "ownership",
            context.Runtime.GetStatus().Message,
            StringComparison.OrdinalIgnoreCase);

        context.Provider.Available = true;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.MaintainFanCurveAsync(CancellationToken.None);

        Assert.Empty(context.Device.FanTargets);
        Assert.Equal(0, context.Device.TotalSetterCalls);
        Assert.Equal([DeviceSubsystem.Fans], context.Runtime.GetStatus().ExternalChanges);
    }

    [Fact]
    public async Task LifecycleRestore_ControllerAdvisoryPreservesExternalLightingOwnership()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        context.Monitor.Current = new ControllerPresenceObservation(
            ["RazerAppEngine"],
            null);
        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);

        LightingSettings requested = LightingSettingsFactory.Static(
            new RgbColor(255, 32, 64),
            brightness: 45);
        ControlOperationResult explicitResult =
            await context.Runtime.SetLightingOverrideAsync(
                new ManualOverride<LightingSettings>(
                    requested,
                    OverrideLifetime.UntilPowerSourceChange),
                CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Applied, explicitResult.Status);
        Assert.Equal(2, context.Device.LightingSetCalls);

        await context.Runtime.InvalidateLightingRendererAsync(CancellationToken.None);
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);

        Assert.Equal(2, context.Device.LightingSetCalls);
        Assert.Null(context.Device.ConfirmedState.Lighting);
        Assert.Equal(PowerSource.Battery, context.Runtime.GetStatus().ActivePowerSource);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task SynapseAdvisory_DropsPendingLifecycleRestore()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        await context.Runtime.InvalidateLightingRendererAsync(CancellationToken.None);
        context.Monitor.Current = new ControllerPresenceObservation(
            ["RazerAppEngine"],
            null);

        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);

        context.Monitor.Current = new ControllerPresenceObservation([], null);
        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);

        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);
        Assert.Empty(context.Runtime.GetStatus().PotentialControllers);
        Assert.Null(context.Runtime.GetStatus().ControllerDetectionWarning);
    }

    [Fact]
    public async Task DetectionWarning_PreservesPendingLifecycleRestoreUntilCleanCheck()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        await context.Runtime.InvalidateLightingRendererAsync(CancellationToken.None);
        context.Monitor.Current = new ControllerPresenceObservation(
            [],
            "Controller detection was incomplete.");

        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);

        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);

        context.Monitor.Current = new ControllerPresenceObservation([], null);
        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);

        Assert.Equal(2, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
        Assert.Empty(context.Runtime.GetStatus().PotentialControllers);
        Assert.Null(context.Runtime.GetStatus().ControllerDetectionWarning);
    }

    [Fact]
    public async Task Resume_DetectionWarningObservesAndKeepsLifecycleRestorePending()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        int stateReadsBeforeLifecycle = context.Device.StateReadCalls;
        var loop = new ServiceLifecycleRefreshLoop(
            context.Runtime,
            new InterleavedResumeLifecycleEventSource(() =>
            {
                context.Device.ConfirmedState = context.Device.ConfirmedState with
                {
                    Lighting = LightingSettingsFactory.Off(),
                };
                context.Monitor.Current = new ControllerPresenceObservation(
                    [],
                    "Controller detection was incomplete.");
            }),
            new LowPowerGate(),
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);

        await loop.RunAsync(CancellationToken.None);

        Assert.True(context.Device.StateReadCalls > stateReadsBeforeLifecycle);
        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.DoesNotContain(
            DeviceSubsystem.Lighting,
            context.Runtime.GetStatus().ExternalChanges);
        Assert.Contains(
            context.History.Snapshot().CommandResults,
            entry => entry.Action == "ResumeValidation");

        context.Monitor.Current = new ControllerPresenceObservation([], null);
        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);

        Assert.Equal(2, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task LifecycleRestore_OverridePolicyReclaimsLightingWithSynapseAdvisory()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        await context.Runtime.SetExternalLightingOverridePolicyAsync(
            true,
            CancellationToken.None);
        context.Monitor.Current = new ControllerPresenceObservation(
            ["RazerAppEngine"],
            null);
        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);
        int writesBeforeInvalidation = context.Device.LightingSetCalls;

        await context.Runtime.InvalidateLightingRendererAsync(CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);

        Assert.Equal(
            writesBeforeInvalidation + 1,
            context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task OwnedRenderer_SuspendResume_RestoresExactlyOnce()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        var loop = new ServiceLifecycleRefreshLoop(
            context.Runtime,
            new SequenceLifecycleEventSource(
                ServiceLifecycleTrigger.Suspending,
                ServiceLifecycleTrigger.Resumed),
            new LowPowerGate(),
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);

        await loop.RunAsync(CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);

        Assert.Equal(2, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task OwnedRenderer_ResumeBeforeDeviceArrival_RestoresOnArrival()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();

        await context.Runtime.InvalidateLightingRendererAsync(CancellationToken.None);
        context.Provider.Available = false;
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);

        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Contains(
            "waiting for the laptop to reconnect",
            context.Runtime.GetStatus().Message,
            StringComparison.OrdinalIgnoreCase);

        context.Provider.Available = true;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);

        Assert.Equal(2, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
        Assert.Contains(
            "was restored",
            context.Runtime.GetStatus().Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OwnedRenderer_PowerChangeDuringSuspend_AppliesDestinationProfileOnce()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        LightingSettings expected = LightingSettingsFactory.Static(
            new RgbColor(0, 96, 255),
            brightness: 40);
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        await context.Runtime.UpdateProfilesAsync(
            profiles with
            {
                Battery = profiles.Battery with { Lighting = expected },
            },
            CancellationToken.None);
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        var loop = new ServiceLifecycleRefreshLoop(
            context.Runtime,
            new SequenceLifecycleEventSource(
                ServiceLifecycleTrigger.Suspending,
                ServiceLifecycleTrigger.Resumed),
            new LowPowerGate(),
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);

        await loop.RunAsync(CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);

        Assert.Equal(2, context.Device.LightingSetCalls);
        Assert.Equal(expected, context.Device.ConfirmedState.Lighting);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task OwnedRenderer_RemovalArrival_RestoresExactlyOnce()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        var loop = new ServiceLifecycleRefreshLoop(
            context.Runtime,
            new SequenceLifecycleEventSource(
                ServiceLifecycleTrigger.DeviceRemoved,
                ServiceLifecycleTrigger.DeviceArrived),
            new LowPowerGate(),
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);

        await loop.RunAsync(CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);

        Assert.Equal(2, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task FailedLifecycleRestore_IsSurfacedAndNeverRetriedAutomatically()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        context.Device.LightingFailure = new IOException("renderer reopen failed");
        var loop = new ServiceLifecycleRefreshLoop(
            context.Runtime,
            new SequenceLifecycleEventSource(
                ServiceLifecycleTrigger.Suspending,
                ServiceLifecycleTrigger.Resumed),
            new LowPowerGate(),
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);

        await loop.RunAsync(CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);

        Assert.Equal(2, context.Device.LightingSetCalls);
        Assert.Contains(
            "will not retry",
            context.Runtime.GetStatus().Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task PeriodicRefreshRecoversOneTransientLightingRendererFault()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        context.Device.LightingRendererOwnership =
            LightingRendererOwnershipState.Faulted;

        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);

        Assert.Equal(2, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
        Assert.Contains(
            "recovered after a transient renderer error",
            context.Runtime.GetStatus().Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PeriodicRefreshRecoversUnexpectedlyMissingLightingRenderer()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        context.Device.LightingRendererOwnership =
            LightingRendererOwnershipState.None;

        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);

        Assert.Equal(2, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
        Assert.Contains(
            "recovered after a transient renderer error",
            context.Runtime.GetStatus().Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(LightingRendererOwnershipState.None)]
    [InlineData(LightingRendererOwnershipState.Faulted)]
    public async Task PeriodicRefresh_SynapseDiscardsRendererRecoveryCandidate(
        LightingRendererOwnershipState ownership)
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync(
            explicitAction: false);
        context.Device.LightingRendererOwnership = ownership;
        context.Monitor.Current = new ControllerPresenceObservation(
            ["RazerAppEngine"],
            null);

        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);
        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);

        context.Monitor.Current = new ControllerPresenceObservation([], null);
        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);

        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);
        Assert.Equal(1, context.Provider.RelinquishCount);
        Assert.Empty(context.Runtime.GetStatus().PotentialControllers);
        Assert.Null(context.Runtime.GetStatus().ControllerDetectionWarning);
    }

    [Theory]
    [InlineData(LightingRendererOwnershipState.None)]
    [InlineData(LightingRendererOwnershipState.Faulted)]
    public async Task PeriodicRefresh_DetectionWarningDefersRendererRecovery(
        LightingRendererOwnershipState ownership)
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync(
            explicitAction: false);
        context.Device.LightingRendererOwnership = ownership;
        context.Monitor.Current = new ControllerPresenceObservation(
            [],
            "Controller detection was incomplete.");

        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);
        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);

        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Equal(0, context.Provider.RelinquishCount);
        Assert.Single(
            context.History.Snapshot().CommandResults,
            result => result.Detail?.Contains(
                "paused automatic keyboard lighting recovery",
                StringComparison.Ordinal) == true);

        context.Monitor.Current = new ControllerPresenceObservation([], null);
        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);

        Assert.Equal(2, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
        Assert.Empty(context.Runtime.GetStatus().PotentialControllers);
        Assert.Null(context.Runtime.GetStatus().ControllerDetectionWarning);

        context.Device.LightingRendererOwnership =
            LightingRendererOwnershipState.Faulted;
        context.Monitor.Current = new ControllerPresenceObservation(
            [],
            "Controller detection was incomplete.");
        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);

        Assert.Equal(
            2,
            context.History.Snapshot().CommandResults.Count(
                result => result.Detail?.Contains(
                    "paused automatic keyboard lighting recovery",
                    StringComparison.Ordinal) == true));
    }

    [Fact]
    public async Task VisibleObservationFaultIsRecoveredImmediatelyByServiceObservation()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        context.Device.LightingRendererOwnership =
            LightingRendererOwnershipState.Faulted;

        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(2, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task VisibleObservationRecoversUnexpectedlyMissingLightingRenderer()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        context.Device.LightingRendererOwnership =
            LightingRendererOwnershipState.None;

        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(2, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task VisibleObservationFaultWithExternalLightingChangeIsNotRecovered()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        context.Device.LightingRendererOwnership =
            LightingRendererOwnershipState.Faulted;
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Lighting = new LightingSettings(
                LightingEffect.Static,
                new RgbColor(1, 2, 3),
                25),
        };

        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);
        Assert.Contains(
            DeviceSubsystem.Lighting,
            context.Runtime.GetStatus().ExternalChanges);
        Assert.Contains(
            "confirmed external lighting change",
            context.Runtime.GetStatus().Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task VisibleObservation_ExternalChangeRelinquishesOwnedRenderer()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Lighting = LightingSettingsFactory.Static(
                new RgbColor(1, 2, 3),
                brightness: 25),
        };

        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Equal(1, context.Provider.RelinquishCount);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);
        Assert.Contains(
            DeviceSubsystem.Lighting,
            context.Runtime.GetStatus().ExternalChanges);
        Assert.Contains(
            "confirmed external lighting change",
            context.Runtime.GetStatus().Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PersistentLightingRendererFaultIsReportedWithoutRetryLoop()
    {
        TestContext context = await CreateOwnedStaticLightingContextAsync();
        context.Device.LightingRendererOwnership =
            LightingRendererOwnershipState.Faulted;
        context.Device.LightingFailure = new IOException("matrix stream unavailable");

        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);
        await context.Runtime.RefreshPowerSourceClassificationAsync(
            CancellationToken.None);

        Assert.Equal(2, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.Faulted,
            context.Runtime.GetStatus().LightingRendererOwnership);
        Assert.Contains(
            "could not be restarted automatically",
            context.Runtime.GetStatus().Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(PowerSource.Battery)]
    [InlineData(PowerSource.UsbC)]
    public async Task PowerTransition_WithoutLightingOwnership_ClaimsDestinationProfileWhenControllerDetectionIsClean(
        PowerSource destination)
    {
        TestContext context = await CreateLightingTransitionContextAsync(
            LightingRendererOwnershipState.None);
        LightingSettings expected = destination == PowerSource.Battery
            ? context.Runtime.GetStatus().Profiles.Battery.Lighting
            : context.Runtime.GetStatus().Profiles.UsbC.Lighting;
        context.Telemetry.Current = CreateTelemetry(destination);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Equal(expected, context.Device.ConfirmedState.Lighting);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);

        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(2, context.Device.LightingSetCalls);
        Assert.Equal(
            context.Runtime.GetStatus().Profiles.PluggedIn.Lighting,
            context.Device.ConfirmedState.Lighting);
    }

    [Fact]
    public async Task PowerTransitionReportsFailureWhenLightingRendererDoesNotRemainOwned()
    {
        TestContext context = await CreateLightingTransitionContextAsync(
            LightingRendererOwnershipState.None);
        context.Device.LightingOwnershipAfterSet = LightingRendererOwnershipState.None;
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);
        Assert.Contains(
            "did not remain active",
            context.Runtime.GetStatus().Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            DeviceSubsystem.Lighting,
            context.Runtime.GetStatus().ExternalChanges);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PowerTransition_WithoutLightingOwnership_PreservesExternalLighting(
        bool controllerRunning,
        bool detectionWarning)
    {
        TestContext context = await CreateLightingTransitionContextAsync(
            LightingRendererOwnershipState.None);
        LightingSettings prior = context.Device.ConfirmedState.Lighting!;
        context.Monitor.Current = new ControllerPresenceObservation(
            controllerRunning ? ["RazerAppEngine"] : [],
            detectionWarning ? "Controller detection was incomplete." : null);
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(0, context.Device.LightingSetCalls);
        Assert.Equal(prior, context.Device.ConfirmedState.Lighting);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task PowerTransition_AllOffProfiles_ApplySavedOffToUnknownLighting()
    {
        TestContext context = CreateWritableContext();
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Lighting = null,
        };
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Equal(
            context.Runtime.GetStatus().Profiles.Battery.Lighting,
            context.Device.ConfirmedState.Lighting);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task PowerTransition_WithOwnedLighting_WritesDestinationProfileLighting()
    {
        TestContext context = await CreateLightingTransitionContextAsync(
            LightingRendererOwnershipState.Static);
        LightingSettings expected = context.Runtime.GetStatus().Profiles.Battery.Lighting;
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Equal(expected, context.Device.ConfirmedState.Lighting);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Theory]
    [InlineData(PowerSource.PluggedIn, PowerSource.Battery)]
    [InlineData(PowerSource.Battery, PowerSource.PluggedIn)]
    public async Task PowerTransition_TransientOwnedLightingOff_ConfirmsBeforeClassifying(
        PowerSource source,
        PowerSource destination)
    {
        TestContext context = await CreateLightingTransitionContextAsync(
            LightingRendererOwnershipState.Static,
            source);
        PowerProfile destinationProfile = destination == PowerSource.PluggedIn
            ? context.Runtime.GetStatus().Profiles.PluggedIn
            : context.Runtime.GetStatus().Profiles.Battery;
        context.Device.PowerTransitionStateReads.Enqueue(new(
            context.Device.ConfirmedState with { Lighting = null },
            LightingOffConfirmationUnavailable: false));
        context.Device.PowerTransitionReadCalls.Clear();
        context.Telemetry.Current = CreateTelemetry(destination);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(2, context.Device.PowerTransitionStateReadCalls);
        Assert.Equal("TransitionState", context.Device.PowerTransitionReadCalls[0]);
        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Equal(destinationProfile.Lighting, context.Device.ConfirmedState.Lighting);
        Assert.DoesNotContain(
            DeviceSubsystem.Lighting,
            context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Theory]
    [InlineData(PowerSource.PluggedIn, PowerSource.Battery)]
    [InlineData(PowerSource.Battery, PowerSource.PluggedIn)]
    public async Task PowerTransition_StableOwnedLightingOff_RemainsExternal(
        PowerSource source,
        PowerSource destination)
    {
        TestContext context = await CreateLightingTransitionContextAsync(
            LightingRendererOwnershipState.Static,
            source);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Lighting = LightingSettingsFactory.Off(),
        };
        context.Device.PowerTransitionStateReads.Enqueue(new(
            context.Device.ConfirmedState,
            LightingOffConfirmationUnavailable: false));
        context.Telemetry.Current = CreateTelemetry(destination);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(2, context.Device.PowerTransitionStateReadCalls);
        Assert.Equal(0, context.Device.LightingSetCalls);
        Assert.Equal(LightingEffect.Off, context.Device.ConfirmedState.Lighting!.Effect);
        Assert.Contains(
            DeviceSubsystem.Lighting,
            context.Runtime.GetStatus().ExternalChanges);
        Assert.Contains(
            "paused",
            context.Runtime.GetStatus().Message,
            StringComparison.OrdinalIgnoreCase);

        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(
            LightingRendererOwnershipState.None,
            context.Runtime.GetStatus().LightingRendererOwnership);
    }

    [Fact]
    public async Task PowerTransition_ConfirmedThirdLightingValue_RemainsExternal()
    {
        TestContext context = await CreateLightingTransitionContextAsync(
            LightingRendererOwnershipState.Static,
            transitionRetryDelay: static (_, _) => Task.CompletedTask);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Lighting = LightingSettingsFactory.Static(
                new RgbColor(4, 5, 6),
                brightness: 30),
        };
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(0, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingSettingsFactory.Static(new RgbColor(4, 5, 6), brightness: 30),
            context.Device.ConfirmedState.Lighting);
        Assert.Contains(
            DeviceSubsystem.Lighting,
            context.Runtime.GetStatus().ExternalChanges);
    }

    [Theory]
    [InlineData(PowerSource.PluggedIn, PowerSource.Battery)]
    [InlineData(PowerSource.Battery, PowerSource.PluggedIn)]
    public async Task PowerTransition_OffConfirmationUnavailable_SkipsOnlyLighting(
        PowerSource source,
        PowerSource destination)
    {
        TestContext context = await CreateLightingTransitionContextAsync(
            LightingRendererOwnershipState.Static,
            source);
        int performanceWrites = context.Device.PerformanceSetCalls;
        int lightingWrites = context.Device.LightingSetCalls;
        context.Device.PowerTransitionStateReads.Enqueue(new(
            context.Device.ConfirmedState with { Lighting = null },
            LightingOffConfirmationUnavailable: true));
        context.Telemetry.Current = CreateTelemetry(destination);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(performanceWrites + 1, context.Device.PerformanceSetCalls);
        Assert.Equal(lightingWrites, context.Device.LightingSetCalls);
        Assert.DoesNotContain(
            DeviceSubsystem.Lighting,
            context.Runtime.GetStatus().ExternalChanges);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        Assert.Contains(
            "lighting profile application was skipped",
            context.Runtime.GetStatus().Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(PowerSource.PluggedIn, PowerSource.Battery)]
    [InlineData(PowerSource.Battery, PowerSource.PluggedIn)]
    public async Task PowerTransition_FinalLightingReadUnavailableDoesNotBlockLaterTransition(
        PowerSource source,
        PowerSource destination)
    {
        TestContext context = await CreateLightingTransitionContextAsync(
            LightingRendererOwnershipState.Static,
            source);
        PowerProfile destinationProfile = destination == PowerSource.PluggedIn
            ? context.Runtime.GetStatus().Profiles.PluggedIn
            : context.Runtime.GetStatus().Profiles.Battery;
        ConfirmedDeviceState initial = context.Device.ConfirmedState;
        int lightingWrites = context.Device.LightingSetCalls;
        context.Device.PowerTransitionStateReads.Enqueue(new(
            initial,
            LightingOffConfirmationUnavailable: false));
        context.Device.PowerTransitionStateReads.Enqueue(new(
            initial with
            {
                Performance = destinationProfile.Performance,
                Fans = destinationProfile.Fans,
                Lighting = null,
            },
            LightingOffConfirmationUnavailable: true));
        context.Telemetry.Current = CreateTelemetry(destination);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(lightingWrites + 1, context.Device.LightingSetCalls);
        Assert.Empty(context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        Assert.DoesNotContain(
            DeviceSubsystem.Lighting,
            context.Runtime.GetStatus().ExternalChanges);

        context.Telemetry.Current = CreateTelemetry(source);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(lightingWrites + 2, context.Device.LightingSetCalls);
        Assert.Empty(context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
    }

    [Fact]
    public async Task FirstObservation_ProfileMismatchEstablishesBaselineWithoutSetters()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Performance,
            FanMode.Maximum,
            ConfirmedBattery(65),
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting with { Brightness = 15 });

        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task StartupReconciliation_ReappliesSavedProfileWithoutExternalWarning()
    {
        TestContext context = CreateWritableContext();
        PowerProfile saved = context.Runtime.GetStatus().Profiles.PluggedIn;
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Performance,
        };

        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(0, context.Device.TotalSetterCalls);

        await context.Runtime.ReconcileStartupProfileAsync(CancellationToken.None);

        ServiceStatusSnapshot reconciled = context.Runtime.GetStatus();
        Assert.Empty(reconciled.ExternalChanges);
        Assert.Equal(saved.Performance, reconciled.ConfirmedState!.Performance);
        Assert.Equal(1, context.Device.PerformanceSetCalls);

        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Performance,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(
            [DeviceSubsystem.Performance],
            context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(1, context.Device.PerformanceSetCalls);
    }

    [Fact]
    public async Task StartupBaseline_BatteryMismatchIsPreservedBeforeReconciliation()
    {
        TestContext context = CreateWritableContext();
        PowerProfile pluggedIn = context.Runtime.GetStatus().Profiles.PluggedIn;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            pluggedIn.Performance,
            pluggedIn.Fans,
            ConfirmedBattery(75),
            pluggedIn.Lighting);

        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(0, context.Device.BatterySetCalls);
        Assert.Equal(75, status.ConfirmedState!.Battery!.ChargeLimitPercent);
        Assert.Equal([DeviceSubsystem.Battery], status.ExternalChanges);
        Assert.Contains("paused", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LaterExternalChanges_UpdateConfirmedStateAndLatchChangedSubsystems()
    {
        TestContext context = CreateContext(providerAvailable: true);
        LightingSettings lighting = ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
            lighting);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Performance,
            FanMode.Maximum,
            ConfirmedBattery(75),
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
    public async Task ReapplyChangedSettings_RestoresOnlyAffectedResolvedOverride()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Balanced,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        int[] before = SetterCounts(context.Device);

        ControlOperationResult result = await context.Runtime.ReapplyChangedSettingsAsync(
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(PerformancePreset.Performance, context.Device.ConfirmedState.Performance);
        Assert.Equal(before[0] + 1, context.Device.PerformanceSetCalls);
        Assert.Equal(before[1], context.Device.FanSetCalls);
        Assert.Equal(before[2], context.Device.BatterySetCalls);
        Assert.Equal(before[3], context.Device.LightingSetCalls);
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(
            PerformancePreset.Performance,
            context.Runtime.GetStatus().PerformanceOverride?.Value);
        Assert.Contains("Power mode", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReapplyChangedSettings_PartialFailureLeavesOnlyUnconfirmedWarning()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Performance,
            Fans = FanMode.Maximum,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.PerformanceFailure = new InvalidOperationException("write rejected");

        ControlOperationResult result = await context.Runtime.ReapplyChangedSettingsAsync(
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Equal([DeviceSubsystem.Performance], context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(FanMode.Automatic, context.Device.ConfirmedState.Fans);
        Assert.Equal(1, context.Device.PerformanceSetCalls);
        Assert.Equal(1, context.Device.FanSetCalls);
        Assert.Equal(0, context.Device.BatterySetCalls);
        Assert.Equal(0, context.Device.LightingSetCalls);
        Assert.Contains("Fans", result.Message, StringComparison.Ordinal);
        Assert.Contains("Power mode", result.Message, StringComparison.Ordinal);
        Assert.Contains("write rejected", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReapplyChangedSettings_ManualFansSafelyBlockThermalRecovery()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(
                FanMode.Maximum,
                OverrideLifetime.Persistent),
            CancellationToken.None);
        int[] before = SetterCounts(context.Device);
        LightingSettings externalLighting = context.Device.ConfirmedState.Lighting! with
        {
            Brightness = 30,
        };
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Performance,
            Battery = ConfirmedBattery(75),
            Lighting = externalLighting,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);

        ControlOperationResult result = await context.Runtime.ReapplyChangedSettingsAsync(
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(
            [DeviceSubsystem.Performance],
            context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(before[0], context.Device.PerformanceSetCalls);
        Assert.Equal(before[1], context.Device.FanSetCalls);
        Assert.Equal(before[2] + 1, context.Device.BatterySetCalls);
        Assert.Equal(before[3] + 1, context.Device.LightingSetCalls);
        Assert.Contains(
            "Power mode cannot be reapplied while manual fan control is active",
            result.Message,
            StringComparison.Ordinal);
        Assert.Contains("Switch fans to Automatic", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReapplyChangedSettings_ExternalUnknownPreservesAvailability()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Lighting = context.Device.ConfirmedState.Lighting! with
            {
                Brightness = 30,
            },
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ServiceStatusSnapshot before = context.Runtime.GetStatus();
        Assert.True(before.DeviceVerified);
        Assert.False(before.WritesBlocked);
        context.Telemetry.Current = CreateTelemetry(PowerSource.ExternalUnknown);
        context.Device.AdapterPowerState = AdapterPowerState.Unknown;
        int setters = context.Device.TotalSetterCalls;

        ControlOperationResult result = await context.Runtime.ReapplyChangedSettingsAsync(
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        ServiceStatusSnapshot after = context.Runtime.GetStatus();
        Assert.True(after.DeviceVerified);
        Assert.False(after.WritesBlocked);
        Assert.Equal(before.ActivePowerSource, after.ActivePowerSource);
        Assert.Equal(setters, context.Device.TotalSetterCalls);
        Assert.Equal(
            [DeviceSubsystem.Lighting],
            after.ExternalChanges);
    }

    [Fact]
    public async Task ReapplyChangedSettings_ManualFanBlockRetainsBothThermalReasons()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(
                FanMode.Maximum,
                OverrideLifetime.Persistent),
            CancellationToken.None);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Fans = FanMode.Automatic,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Performance,
            Fans = FanMode.Maximum,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        int[] before = SetterCounts(context.Device);

        ControlOperationResult result = await context.Runtime.ReapplyChangedSettingsAsync(
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(
            [DeviceSubsystem.Performance, DeviceSubsystem.Fans],
            context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(before, SetterCounts(context.Device));
        Assert.Contains(
            "Power mode cannot be reapplied while manual fan control is active",
            result.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "Fans were not reapplied because OpenBlade kept manual fan control unchanged",
            result.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReapplyChangedSettings_WithNoWarningPerformsNoHardwareAccess()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        int calls = context.Device.TotalCalls;

        ControlOperationResult result = await context.Runtime.ReapplyChangedSettingsAsync(
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, result.Status);
        Assert.Equal(calls, context.Device.TotalCalls);
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
    }

    [Fact]
    public async Task Observation_BatteryLimitToProtectionOff_UpdatesConfirmedAndWarnsWithoutSetters()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            null,
            null,
            ConfirmedBattery(80),
            null);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Battery = ProtectionOff(),
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(ProtectionOff(), context.Runtime.GetStatus().ConfirmedState!.Battery);
        Assert.Equal([DeviceSubsystem.Battery], context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task Observation_ProtectionOffToBatteryLimit_UpdatesConfirmedAndWarnsWithoutSetters()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            null,
            null,
            ProtectionOff(),
            null);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Battery = ConfirmedBattery(80),
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(ConfirmedBattery(80), context.Runtime.GetStatus().ConfirmedState!.Battery);
        Assert.Equal([DeviceSubsystem.Battery], context.Runtime.GetStatus().ExternalChanges);
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
            ConfirmedBattery(80),
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        DeviceCapabilities capabilities = context.Runtime.GetStatus().Capabilities!;
        ConfirmedDeviceState confirmed = context.Runtime.GetStatus().ConfirmedState!;

        context.Device.ReadStateFailure = new IOException("device vanished");
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.True(context.Runtime.GetStatus().DeviceVerified);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        Assert.Contains(
            "temporary device-access failure",
            context.Runtime.GetStatus().Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Same(capabilities, context.Runtime.GetStatus().Capabilities);
        Assert.Same(confirmed, context.Runtime.GetStatus().ConfirmedState);

        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.True(context.Runtime.GetStatus().DeviceVerified);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);

        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.True(context.Runtime.GetStatus().DeviceVerified);
        Assert.True(context.Runtime.GetStatus().WritesBlocked);

        context.Device.ReadStateFailure = null;
        context.Provider.Available = false;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.True(context.Runtime.GetStatus().DeviceVerified);
        Assert.True(context.Runtime.GetStatus().WritesBlocked);
        Assert.Contains("Checking the laptop connection", context.Runtime.GetStatus().Message);
        Assert.Same(capabilities, context.Runtime.GetStatus().Capabilities);
        Assert.Same(confirmed, context.Runtime.GetStatus().ConfirmedState);

        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.True(context.Runtime.GetStatus().DeviceVerified);
        Assert.True(context.Runtime.GetStatus().WritesBlocked);
        Assert.Same(capabilities, context.Runtime.GetStatus().Capabilities);
        Assert.Same(confirmed, context.Runtime.GetStatus().ConfirmedState);

        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.False(context.Runtime.GetStatus().DeviceVerified);
        Assert.True(context.Runtime.GetStatus().WritesBlocked);
        Assert.Same(capabilities, context.Runtime.GetStatus().Capabilities);
        Assert.Same(confirmed, context.Runtime.GetStatus().ConfirmedState);
    }

    [Fact]
    public async Task ScopedAdmissionMiss_PreservesPreviouslyVerifiedGlobalAvailability()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
            LightingSettingsFactory.Off());
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ServiceStatusSnapshot verified = context.Runtime.GetStatus();
        Assert.True(verified.DeviceVerified);
        Assert.False(verified.WritesBlocked);

        context.Provider.Available = false;
        ControlOperationResult result = await context.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                LightingSettingsFactory.Off(),
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.True(context.Runtime.GetStatus().DeviceVerified);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        Assert.Same(verified.Capabilities, context.Runtime.GetStatus().Capabilities);
        Assert.Same(verified.ConfirmedState, context.Runtime.GetStatus().ConfirmedState);
        Assert.Contains(
            "this control operation",
            context.Runtime.GetStatus().Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScopedReadFailure_PreservesPreviouslyVerifiedGlobalAvailability()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Static,
            },
        };
        TestContext context = CreateContext(
            providerAvailable: true,
            profileValidator: ProfileSetValidator.CreateForLightingCapabilities(capabilities));
        context.Device.Capabilities = capabilities;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
            LightingSettingsFactory.Off());
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ServiceStatusSnapshot verified = context.Runtime.GetStatus();
        context.Device.ReadStateFailureOnCall = context.Device.StateReadCalls + 1;
        context.Device.ReadStateFailureOnCallException = new IOException("temporary read failure");

        ControlOperationResult result = await context.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                LightingSettingsFactory.Static(new RgbColor(0, 255, 0), 77),
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.True(context.Runtime.GetStatus().DeviceVerified);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        Assert.Same(verified.ConfirmedState, context.Runtime.GetStatus().ConfirmedState);
        Assert.Contains("temporary read failure", context.Runtime.GetStatus().Message);
    }

    [Fact]
    public async Task StartupAnimationControl_PublishesConfirmedReadback()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            OneShotLaptopControls = new HashSet<OneShotLaptopControl>
            {
                OneShotLaptopControl.StartupAnimation,
            },
        };
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting);
        context.Device.StartupAnimationEnabled = true;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        var request = new ApplyOneShotLaptopControl(
            OneShotLaptopControl.StartupAnimation,
            Enabled: false);

        ControlOperationResult result = await context.Runtime
            .ApplyOneShotLaptopControlAsync(request, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(request, context.Device.LastOneShotLaptopControl);
        Assert.Equal(1, context.Device.OneShotSetCalls);
        Assert.False(context.Runtime.GetStatus().StartupAnimationEnabled);
        Assert.Equal("Startup animation is off.", result.Message);
    }

    [Fact]
    public async Task FunctionKeyControl_PublishesConfirmedReadback()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            OneShotLaptopControls = new HashSet<OneShotLaptopControl>
            {
                OneShotLaptopControl.FunctionKeyPrimary,
            },
        };
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting);
        context.Device.FunctionKeyPrimary = FunctionKeyPrimaryMode.FunctionKeys;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        var request = new ApplyOneShotLaptopControl(
            OneShotLaptopControl.FunctionKeyPrimary,
            FunctionKeyMode: FunctionKeyPrimaryMode.MultimediaKeys);

        ControlOperationResult result = await context.Runtime
            .ApplyOneShotLaptopControlAsync(request, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(request, context.Device.LastOneShotLaptopControl);
        Assert.Equal(1, context.Device.OneShotSetCalls);
        Assert.Equal(
            FunctionKeyPrimaryMode.MultimediaKeys,
            context.Runtime.GetStatus().FunctionKeyPrimary);
        Assert.Equal("Function key behavior is Media keys.", result.Message);
    }

    [Fact]
    public async Task GamingModeControl_PublishesConfirmedReadback()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            OneShotLaptopControls = new HashSet<OneShotLaptopControl>
            {
                OneShotLaptopControl.GamingMode,
            },
        };
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting);
        context.Device.GamingModeEnabled = false;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        var request = new ApplyOneShotLaptopControl(
            OneShotLaptopControl.GamingMode,
            Enabled: true);

        ControlOperationResult result = await context.Runtime
            .ApplyOneShotLaptopControlAsync(request, CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(request, context.Device.LastOneShotLaptopControl);
        Assert.Equal(1, context.Device.OneShotSetCalls);
        Assert.True(context.Runtime.GetStatus().GamingModeEnabled);
        Assert.Equal("Gaming Mode is on.", result.Message);
    }

    [Fact]
    public async Task PowerTransition_AppliesDestinationLaptopBehaviorProfile()
    {
        TestContext context = CreateContext(providerAvailable: true);
        DeviceCapabilities capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            OneShotLaptopControls = new HashSet<OneShotLaptopControl>
            {
                OneShotLaptopControl.StartupAnimation,
                OneShotLaptopControl.FunctionKeyPrimary,
                OneShotLaptopControl.GamingMode,
            },
        };
        context.Device.Capabilities = capabilities;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting);
        context.Device.StartupAnimationEnabled = false;
        context.Device.FunctionKeyPrimary = FunctionKeyPrimaryMode.MultimediaKeys;
        context.Device.GamingModeEnabled = true;
        await context.Runtime.ObserveAsync(CancellationToken.None);

        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        profiles = profiles with
        {
            Battery = profiles.Battery with
            {
                LaptopBehavior = new LaptopBehaviorProfileSettings(
                    StartupAnimationEnabled: true,
                    FunctionKeyPrimary: FunctionKeyPrimaryMode.FunctionKeys,
                    GamingModeEnabled: false),
            },
        };
        ControlOperationResult stored = await context.Runtime.UpdateProfilesAsync(
            profiles,
            CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Stored, stored.Status);

        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(3, context.Device.OneShotSetCalls);
        Assert.True(context.Device.StartupAnimationEnabled);
        Assert.Equal(
            FunctionKeyPrimaryMode.FunctionKeys,
            context.Device.FunctionKeyPrimary);
        Assert.False(context.Device.GamingModeEnabled);
        Assert.True(context.Runtime.GetStatus().StartupAnimationEnabled);
        Assert.Equal(
            FunctionKeyPrimaryMode.FunctionKeys,
            context.Runtime.GetStatus().FunctionKeyPrimary);
        Assert.False(context.Runtime.GetStatus().GamingModeEnabled);
    }

    [Fact]
    public async Task PowerTransition_UnconfiguredLaptopBehaviorDoesNotWrite()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            OneShotLaptopControls = new HashSet<OneShotLaptopControl>
            {
                OneShotLaptopControl.StartupAnimation,
                OneShotLaptopControl.FunctionKeyPrimary,
                OneShotLaptopControl.GamingMode,
            },
        };
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(0, context.Device.OneShotSetCalls);
    }

    [Fact]
    public async Task PayloadValidationObservationFailure_RetriesOneReadAndRecovers()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting);
        context.Device.ReadStateFailureOnCall = 1;
        context.Device.ReadStateFailureOnCallException = new RazerProtocolException(
            "Battery state query failed protocol validation (PayloadValidator).",
            HidProtocolValidationFailure.PayloadValidator);

        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(2, context.Device.StateReadCalls);
        Assert.Equal(0, context.Device.TotalSetterCalls);
        Assert.True(context.Runtime.GetStatus().DeviceVerified);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        Assert.Equal("Verified device state observed.", context.Runtime.GetStatus().Message);
    }

    [Fact]
    public async Task RepeatedPayloadValidationObservationFailure_BlocksWritesButPreservesVerification()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.ReadStateFailure = new RazerProtocolException(
            "Battery state query failed protocol validation (PayloadValidator).",
            HidProtocolValidationFailure.PayloadValidator);

        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Equal(2, context.Device.StateReadCalls);
        Assert.Equal(0, context.Device.TotalSetterCalls);
        Assert.True(context.Runtime.GetStatus().DeviceVerified);
        Assert.True(context.Runtime.GetStatus().WritesBlocked);
        Assert.Contains("PayloadValidator", context.Runtime.GetStatus().Message);
    }

    [Fact]
    public async Task ProtocolObservationFailure_PublishesAndRecordsSafeValidationCategory()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.ReadStateFailure = new RazerProtocolException(
            "Battery state query failed protocol validation (Checksum).",
            HidProtocolValidationFailure.Checksum);

        await context.Runtime.ObserveAsync(CancellationToken.None);

        const string expected =
            "Verified device access failed: Battery state query failed protocol validation (Checksum).";
        Assert.Equal(expected, context.Runtime.GetStatus().Message);
        Assert.Equal(1, context.Device.StateReadCalls);
        Assert.True(context.Runtime.GetStatus().DeviceVerified);
        Assert.True(context.Runtime.GetStatus().WritesBlocked);
        DiagnosticCommandResult record = Assert.Single(
            context.History.Snapshot().CommandResults);
        Assert.Equal("Service", record.Subsystem);
        Assert.Equal("Reconcile", record.Action);
        Assert.Equal("Failed", record.Status);
        Assert.Equal(expected, record.Detail);
    }

    [Fact]
    public async Task UnsupportedIntentionalReconciliation_PreservesLatchedExternalChanges()
    {
        TestContext context = CreateContext(providerAvailable: true);
        await LatchFanDriftAsync(context);

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
        Assert.Contains("override intent was stored", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("device vanished", result.Message, StringComparison.OrdinalIgnoreCase);
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
    public async Task ControllerPresenceRefresh_IsBoundedAndDoesNotTouchDevice()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
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
        Assert.Equal(before.ActivePowerSource, after.ActivePowerSource);
        Assert.Same(before.LiveReadings, after.LiveReadings);
        Assert.Same(before.DesiredState, after.DesiredState);
        Assert.Contains("Razer Synapse is active", after.Message, StringComparison.Ordinal);
        Assert.Equal(before.ExternalChanges, after.ExternalChanges);
        Assert.Equal(telemetryCaptures, context.Telemetry.CaptureCount);
        Assert.Equal(providerCalls, context.Provider.TryGetCount);
        Assert.Equal(deviceCalls, context.Device.TotalCalls);
        Assert.Equal(0, context.Provider.RelinquishCount);
        Assert.Equal(2, context.Monitor.ObserveCount);
    }

    [Fact]
    public async Task DuplicatePowerNotification_ObservesWithoutSettersOrConsumingLaterTransition()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
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
    public async Task GenuinePowerTransition_PreservesStartupProtectionMismatch()
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            protectionEnabled: false);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            ChargeLimits = new HashSet<int> { 80 },
            SupportsBatteryProtectionOff = true,
        };
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting);
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Empty(context.Device.BatterySetterCalls);
        Assert.Equal(0, context.Device.BatterySetCalls);
        Assert.Equal(ConfirmedBattery(80), context.Runtime.GetStatus().ConfirmedState!.Battery);
        Assert.Equal(
            [DeviceSubsystem.Battery],
            context.Runtime.GetStatus().ExternalChanges);
    }

    [Theory]
    [InlineData(
        PowerSource.PluggedIn,
        PowerSource.Battery,
        PerformancePreset.Balanced,
        PerformancePreset.BatteryBalanced)]
    [InlineData(
        PowerSource.Battery,
        PowerSource.PluggedIn,
        PerformancePreset.BatteryBalanced,
        PerformancePreset.Balanced)]
    public async Task PowerTransition_PreReadAtPriorDesired_WritesNewDesiredWithoutWarning(
        PowerSource initialSource,
        PowerSource newSource,
        PerformancePreset priorDesired,
        PerformancePreset newDesired)
    {
        TestContext context = await CreateBatteryBalancedTransitionContextAsync(
            initialSource,
            priorDesired);
        int setterCalls = context.Device.PerformanceSetCalls;
        context.Telemetry.Current = CreateTelemetry(newSource);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(setterCalls + 1, context.Device.PerformanceSetCalls);
        Assert.Equal(newDesired, context.Runtime.GetStatus().ConfirmedState!.Performance);
        Assert.DoesNotContain(
            DeviceSubsystem.Performance,
            context.Runtime.GetStatus().ExternalChanges);
    }

    [Theory]
    [InlineData(
        PowerSource.PluggedIn,
        PowerSource.Battery,
        PerformancePreset.BatteryBalanced)]
    [InlineData(
        PowerSource.Battery,
        PowerSource.PluggedIn,
        PerformancePreset.Balanced)]
    public async Task PowerTransition_PreReadAtNewDesired_PerformsNoWriteOrWarning(
        PowerSource initialSource,
        PowerSource newSource,
        PerformancePreset newDesired)
    {
        PerformancePreset priorDesired = initialSource == PowerSource.PluggedIn
            ? PerformancePreset.Balanced
            : PerformancePreset.BatteryBalanced;
        TestContext context = await CreateBatteryBalancedTransitionContextAsync(
            initialSource,
            priorDesired);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = newDesired,
        };
        int setterCalls = context.Device.PerformanceSetCalls;
        context.Telemetry.Current = CreateTelemetry(newSource);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(setterCalls, context.Device.PerformanceSetCalls);
        Assert.Equal(newDesired, context.Runtime.GetStatus().ConfirmedState!.Performance);
        Assert.DoesNotContain(
            DeviceSubsystem.Performance,
            context.Runtime.GetStatus().ExternalChanges);
    }

    [Theory]
    [InlineData(PowerSource.PluggedIn, PowerSource.Battery)]
    [InlineData(PowerSource.Battery, PowerSource.PluggedIn)]
    public async Task PowerTransition_PreReadMatchingNeitherEndpoint_PreservesAndWarns(
        PowerSource initialSource,
        PowerSource newSource)
    {
        PerformancePreset priorDesired = initialSource == PowerSource.PluggedIn
            ? PerformancePreset.Balanced
            : PerformancePreset.BatteryBalanced;
        TestContext context = await CreateBatteryBalancedTransitionContextAsync(
            initialSource,
            priorDesired);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.BatterySaver,
        };
        int setterCalls = context.Device.PerformanceSetCalls;
        context.Telemetry.Current = CreateTelemetry(newSource);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(setterCalls, context.Device.PerformanceSetCalls);
        Assert.Equal(PerformancePreset.BatterySaver, status.ConfirmedState!.Performance);
        Assert.Contains(DeviceSubsystem.Performance, status.ExternalChanges);
        Assert.Contains("paused", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DeviceSubsystem.Performance)]
    [InlineData(DeviceSubsystem.Fans)]
    [InlineData(DeviceSubsystem.Battery)]
    [InlineData(DeviceSubsystem.Lighting)]
    public async Task GenuinePowerTransition_PreservesLatchedExternalChangeWithoutSetter(
        DeviceSubsystem subsystem)
    {
        TestContext context = CreateWritableContext();
        PowerProfile pluggedIn = context.Runtime.GetStatus().Profiles.PluggedIn;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            pluggedIn.Performance,
            pluggedIn.Fans,
            ConfirmedBattery(80),
            pluggedIn.Lighting);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        ConfirmedDeviceState externallyChanged = subsystem switch
        {
            DeviceSubsystem.Performance => context.Device.ConfirmedState with
            {
                Performance = PerformancePreset.Performance,
            },
            DeviceSubsystem.Fans => context.Device.ConfirmedState with
            {
                Fans = FanMode.Maximum,
            },
            DeviceSubsystem.Battery => context.Device.ConfirmedState with
            {
                Battery = ConfirmedBattery(75),
            },
            DeviceSubsystem.Lighting => context.Device.ConfirmedState with
            {
                Lighting = new LightingSettings(
                    LightingEffect.Static,
                    new RgbColor(1, 2, 3),
                    25),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(subsystem)),
        };
        context.Device.ConfirmedState = externallyChanged;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal([subsystem], context.Runtime.GetStatus().ExternalChanges);
        int capabilityReads = context.Device.CapabilitiesReadCalls;
        int stateReads = context.Device.StateReadCalls;

        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(capabilityReads + 1, context.Device.CapabilitiesReadCalls);
        Assert.Equal(
            stateReads + (subsystem is DeviceSubsystem.Performance or DeviceSubsystem.Fans ? 1 : 2),
            context.Device.StateReadCalls);
        Assert.Equal(0, SetterCount(context.Device, subsystem));
        if (subsystem is DeviceSubsystem.Performance or DeviceSubsystem.Fans)
        {
            Assert.Equal(0, context.Device.PerformanceSetCalls);
            Assert.Equal(0, context.Device.FanSetCalls);
        }
        Assert.Equal([subsystem], status.ExternalChanges);
        Assert.Contains("paused", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DeviceSubsystem.Performance)]
    [InlineData(DeviceSubsystem.Fans)]
    [InlineData(DeviceSubsystem.Battery)]
    [InlineData(DeviceSubsystem.Lighting)]
    public async Task GenuinePowerTransition_PreservesExternalChangeNotPreviouslyObserved(
        DeviceSubsystem subsystem)
    {
        TestContext context = CreateWritableContext();
        PowerProfile pluggedIn = context.Runtime.GetStatus().Profiles.PluggedIn;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            pluggedIn.Performance,
            pluggedIn.Fans,
            ConfirmedBattery(80),
            pluggedIn.Lighting);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        ConfirmedDeviceState externallyChanged = subsystem switch
        {
            DeviceSubsystem.Performance => context.Device.ConfirmedState with
            {
                Performance = PerformancePreset.Performance,
            },
            DeviceSubsystem.Fans => context.Device.ConfirmedState with
            {
                Fans = FanMode.Maximum,
            },
            DeviceSubsystem.Battery => context.Device.ConfirmedState with
            {
                Battery = ConfirmedBattery(75),
            },
            DeviceSubsystem.Lighting => context.Device.ConfirmedState with
            {
                Lighting = new LightingSettings(
                    LightingEffect.Static,
                    new RgbColor(4, 5, 6),
                    30),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(subsystem)),
        };
        context.Device.ConfirmedState = externallyChanged;

        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(0, SetterCount(context.Device, subsystem));
        Assert.Contains(subsystem, status.ExternalChanges);
        Assert.Contains("paused", status.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(status.ConfirmedState);
        switch (subsystem)
        {
            case DeviceSubsystem.Performance:
                Assert.Equal(externallyChanged.Performance, status.ConfirmedState.Performance);
                break;
            case DeviceSubsystem.Fans:
                Assert.Equal(externallyChanged.Fans, status.ConfirmedState.Fans);
                break;
            case DeviceSubsystem.Battery:
                Assert.Equal(externallyChanged.Battery, status.ConfirmedState.Battery);
                break;
            case DeviceSubsystem.Lighting:
                Assert.Equal(externallyChanged.Lighting, status.ConfirmedState.Lighting);
                break;
        }
    }

    [Fact]
    public async Task ExplicitScopedCommand_ReclaimsLatchedExternalChangeAfterReadback()
    {
        TestContext context = CreateWritableContext();
        await LatchFanDriftAsync(context);

        ControlOperationResult result = await context.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Automatic, OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(1, context.Device.FanSetCalls);
        Assert.Equal(FanMode.Automatic, context.Runtime.GetStatus().ConfirmedState!.Fans);
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
    }

    [Fact]
    public async Task TransitionalRefreshMethods_RouteToZeroWriteReplacements()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Performance,
            FanMode.Maximum,
            ConfirmedBattery(70),
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting with { Brightness = 10 });

        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(5, context.Device.TotalCalls);
        Assert.Equal(0, context.Device.TotalSetterCalls);

        int telemetryCaptures = context.Telemetry.CaptureCount;
        context.Monitor.Current = new ControllerPresenceObservation(["RazerAppEngine"], null);
        await context.Runtime.RefreshControllerPresenceAsync(CancellationToken.None);
        Assert.Equal(2, context.Monitor.ObserveCount);
        Assert.Equal(telemetryCaptures, context.Telemetry.CaptureCount);
        Assert.Equal(5, context.Device.TotalCalls);
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
                new ManualOverride<FanMode>(FanMode.Automatic, OverrideLifetime.Persistent),
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

    [Fact]
    public async Task UpdateProfiles_UsesInjectedProductionLightingValidationPolicy()
    {
        ProfileSetValidator validator = ProfileSetValidator.CreateForLightingCapabilities(
            DeviceCapabilities.Unavailable() with
            {
                LightingEffects = new HashSet<LightingEffect> { LightingEffect.Static },
            });
        TestContext context = CreateContext(
            providerAvailable: false,
            profileValidator: validator);
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        LightingSettings lighting = LightingSettingsFactory.Static(
            new RgbColor(10, 20, 30),
            brightness: 60);
        profiles = profiles with
        {
            PluggedIn = profiles.PluggedIn with { Lighting = lighting },
            Battery = profiles.Battery with { Lighting = lighting },
            UsbC = profiles.UsbC with { Lighting = lighting },
        };

        ControlOperationResult result = await context.Runtime.UpdateProfilesAsync(
            profiles,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, result.Status);
        Assert.Equal(profiles, context.Runtime.GetStatus().Profiles);
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
        Assert.Contains(
            "Razer Synapse is active",
            conflict.Message,
            StringComparison.Ordinal);
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
            ConfirmedBattery(80),
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
    public async Task LifecycleRefresh_WithControllerAdvisory_UsesReadOnlyObservation()
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            conflicts: ["RazerAppEngine"]);
        var loop = new ServiceLifecycleRefreshLoop(
            context.Runtime,
            new SingleLifecycleEventSource(ServiceLifecycleTrigger.DeviceArrived),
            new LowPowerGate(),
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);

        await loop.RunAsync(CancellationToken.None);

        Assert.True(context.Runtime.GetStatus().DeviceVerified);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        Assert.Equal(0, context.Device.TotalSetterCalls);
        Assert.Contains(
            "RazerAppEngine",
            context.Runtime.GetStatus().PotentialControllers);
    }

    [Fact]
    public async Task Resume_AfterMissedPowerChangeAppliesNewPowerProfile()
    {
        TestContext context = CreateWritableContext();
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        var loop = new ServiceLifecycleRefreshLoop(
            context.Runtime,
            new SingleLifecycleEventSource(ServiceLifecycleTrigger.Resumed),
            new LowPowerGate(),
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);

        await loop.RunAsync(CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(PowerSource.Battery, status.ActivePowerSource);
        Assert.Equal(PerformancePreset.BatterySaver, status.DesiredState.Performance.Value);
        Assert.Equal(PerformancePreset.BatterySaver, status.ConfirmedState!.Performance);
        Assert.Equal(
            status.Profiles.Battery.Lighting,
            status.ConfirmedState.Lighting);
        Assert.Equal(1, context.Device.PerformanceSetCalls);
        Assert.Equal(0, context.Device.FanSetCalls);
        Assert.Equal(0, context.Device.BatterySetCalls);
        Assert.Equal(1, context.Device.LightingSetCalls);
    }

    [Fact]
    public async Task Resume_WithoutPowerChangeRestoresLightingWithoutOtherWrites()
    {
        TestContext context = CreateWritableContext();
        var loop = new ServiceLifecycleRefreshLoop(
            context.Runtime,
            new SingleLifecycleEventSource(ServiceLifecycleTrigger.Resumed),
            new LowPowerGate(),
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);

        await loop.RunAsync(CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(PowerSource.PluggedIn, status.ActivePowerSource);
        Assert.Equal(PerformancePreset.Balanced, status.ConfirmedState!.Performance);
        Assert.Equal(2, context.Device.CapabilitiesReadCalls);
        Assert.Equal(3, context.Device.StateReadCalls);
        Assert.Equal(0, context.Device.PerformanceSetCalls);
        Assert.Equal(0, context.Device.FanSetCalls);
        Assert.Equal(0, context.Device.BatterySetCalls);
        Assert.Equal(1, context.Device.LightingSetCalls);
    }

    [Fact]
    public async Task ResumeOnAc_InvalidatesGpuClockConfirmationBeforeStateRead()
    {
        TestContext context = CreateWritableContext();
        var calls = new List<string>();
        context.Provider.AfterGpuClockOffsetInvalidation = () => calls.Add("InvalidateGpu");
        context.Device.AfterReadState = () => calls.Add("State");
        var loop = new ServiceLifecycleRefreshLoop(
            context.Runtime,
            new SingleLifecycleEventSource(ServiceLifecycleTrigger.Resumed),
            new LowPowerGate(),
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);

        await loop.RunAsync(CancellationToken.None);

        Assert.Equal(1, context.Provider.GpuClockOffsetInvalidationCount);
        Assert.Equal("InvalidateGpu", calls[0]);
        Assert.Contains("State", calls);
    }

    [Fact]
    public async Task Resume_InvalidatesGpuClockConfirmationWhenDeviceIsUnavailable()
    {
        TestContext context = CreateContext(providerAvailable: false);
        var loop = new ServiceLifecycleRefreshLoop(
            context.Runtime,
            new SingleLifecycleEventSource(ServiceLifecycleTrigger.Resumed),
            new LowPowerGate(),
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);

        await loop.RunAsync(CancellationToken.None);

        Assert.Equal(1, context.Provider.GpuClockOffsetInvalidationCount);
    }

    [Fact]
    public async Task ResumeValidation_LifecyclePathDisclosesLightingRestoreWrite()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        int settersBeforeResume = context.Device.TotalSetterCalls;
        var loop = new ServiceLifecycleRefreshLoop(
            context.Runtime,
            new SequenceLifecycleEventSource(
                ServiceLifecycleTrigger.Suspending,
                ServiceLifecycleTrigger.Resumed),
            new LowPowerGate(),
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);

        await loop.RunAsync(CancellationToken.None);

        DiagnosticCommandResult validation = Assert.Single(
            context.History.Snapshot().CommandResults,
            entry => entry.Action == "ResumeValidation");
        Assert.Equal(ControlOperationStatus.Applied.ToString(), validation.Status);
        Assert.Contains(
            "retained control state for Power mode, Fans, Battery",
            validation.Detail,
            StringComparison.Ordinal);
        Assert.Contains(
            "Lighting was not compared",
            validation.Detail,
            StringComparison.Ordinal);
        Assert.Contains(
            "restoration applied a lighting control write",
            validation.Detail,
            StringComparison.Ordinal);
        Assert.Equal(settersBeforeResume + 1, context.Device.TotalSetterCalls);
        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
    }

    [Fact]
    public async Task ResumeValidation_DoesNotClaimPowerRetainedAfterDefaultResetInvalidates()
    {
        TestContext context = CreateWritableContext();
        context.Provider.GpuClockOffsetConfirmationRequiresRevalidation = true;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        var loop = new ServiceLifecycleRefreshLoop(
            context.Runtime,
            new SequenceLifecycleEventSource(
                ServiceLifecycleTrigger.Suspending,
                ServiceLifecycleTrigger.Resumed),
            new LowPowerGate(),
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);

        await loop.RunAsync(CancellationToken.None);

        DiagnosticCommandResult validation = Assert.Single(
            context.History.Snapshot().CommandResults,
            entry => entry.Action == "ResumeValidation");
        Assert.DoesNotContain(
            "retained control state for Power mode",
            validation.Detail,
            StringComparison.Ordinal);
        Assert.Contains(
            "Unavailable for comparison: Power mode.",
            validation.Detail,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledGpuReset_DoesNotBecomeExternalDuringPowerTransition()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            GpuOverclock = new GpuOverclockCapabilities(
                new DeviceNumericRange(-1000, 400, null),
                new DeviceNumericRange(-1000, 3000, null),
                DeviceFeatureAccess.ReadWrite),
        };
        TestContext context = CreateContext(
            providerAvailable: true,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(
                capabilities));
        context.Device.Capabilities = capabilities;
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        profiles = profiles with
        {
            PluggedIn = profiles.PluggedIn with
            {
                GpuClockTuning = GpuClockTuningSettings.Disabled,
            },
        };
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            profiles.PluggedIn.Performance,
            profiles.PluggedIn.Fans,
            ConfirmedBattery(80),
            profiles.PluggedIn.Lighting);
        context.Device.GpuClockOffsetManagementState =
            GpuClockOffsetManagementState.ResetConfirmed;
        await context.Runtime.UpdateProfilesAsync(profiles, CancellationToken.None);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(
            GpuClockTuningSettings.Disabled,
            context.Runtime.GetStatus().Profiles.PluggedIn.GpuClockTuning);
        Assert.Null(context.Runtime.GetStatus().Profiles.Battery.GpuClockTuning);
        int performanceWrites = context.Device.PerformanceSetCalls;

        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.DoesNotContain(
            DeviceSubsystem.Performance,
            context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(performanceWrites + 1, context.Device.PerformanceSetCalls);
    }

    [Fact]
    public async Task ResumeValidation_LifecyclePathClassifiesChangedStateWithoutRecoveryWrites()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        int settersBeforeResume = context.Device.TotalSetterCalls;
        var loop = new ServiceLifecycleRefreshLoop(
            context.Runtime,
            new InterleavedResumeLifecycleEventSource(() =>
            {
                context.Device.ConfirmedState = context.Device.ConfirmedState with
                {
                    Performance = PerformancePreset.Performance,
                    Fans = FanMode.Maximum,
                    Battery = ConfirmedBattery(75),
                    Lighting = LightingSettingsFactory.Off(),
                };
            }),
            new LowPowerGate(),
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);

        await loop.RunAsync(CancellationToken.None);

        DiagnosticCommandResult validation = Assert.Single(
            context.History.Snapshot().CommandResults,
            entry => entry.Action == "ResumeValidation");
        Assert.Equal(ControlOperationStatus.Blocked.ToString(), validation.Status);
        Assert.Contains("Power mode", validation.Detail, StringComparison.Ordinal);
        Assert.Contains("Fans", validation.Detail, StringComparison.Ordinal);
        Assert.Contains("Battery", validation.Detail, StringComparison.Ordinal);
        Assert.Contains(
            "no validation-driven recovery writes",
            validation.Detail,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Lighting was not compared",
            validation.Detail,
            StringComparison.Ordinal);
        Assert.Contains(
            "restoration applied a lighting control write",
            validation.Detail,
            StringComparison.Ordinal);
        Assert.Equal(settersBeforeResume + 1, context.Device.TotalSetterCalls);
        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Equal(
            [
                DeviceSubsystem.Performance,
                DeviceSubsystem.Fans,
                DeviceSubsystem.Battery,
            ],
            context.Runtime.GetStatus().ExternalChanges);
    }

    [Fact]
    public async Task ResumeValidation_LifecyclePathWaitsForDeviceToReturn()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        int settersBeforeResume = context.Device.TotalSetterCalls;
        var loop = new ServiceLifecycleRefreshLoop(
            context.Runtime,
            new InterleavedResumeLifecycleEventSource(
                () => context.Provider.Available = false),
            new LowPowerGate(),
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);

        await loop.RunAsync(CancellationToken.None);

        Assert.DoesNotContain(
            context.History.Snapshot().CommandResults,
            entry => entry.Action == "ResumeValidation");
        Assert.Equal(settersBeforeResume, context.Device.TotalSetterCalls);

        context.Provider.Available = true;
        await context.Runtime.ObserveAsync(
            includeLiveReadings: false,
            CancellationToken.None);

        DiagnosticCommandResult validation = Assert.Single(
            context.History.Snapshot().CommandResults,
            entry => entry.Action == "ResumeValidation");
        Assert.Equal(ControlOperationStatus.Stored.ToString(), validation.Status);
        Assert.Contains("retained", validation.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Lighting was unavailable in at least one sample",
            validation.Detail,
            StringComparison.Ordinal);
        Assert.Equal(settersBeforeResume, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task ResumeValidation_LifecyclePowerChangeUsesDestinationProfile()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        var loop = new ServiceLifecycleRefreshLoop(
            context.Runtime,
            new InterleavedResumeLifecycleEventSource(
                () => context.Telemetry.Current = CreateTelemetry(PowerSource.Battery)),
            new LowPowerGate(),
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);

        await loop.RunAsync(CancellationToken.None);

        DiagnosticCommandResult validation = Assert.Single(
            context.History.Snapshot().CommandResults,
            entry => entry.Action == "ResumeValidation");
        Assert.Equal(ControlOperationStatus.Stored.ToString(), validation.Status);
        Assert.Contains("PluggedIn", validation.Detail, StringComparison.Ordinal);
        Assert.Contains("Battery", validation.Detail, StringComparison.Ordinal);
        Assert.Contains(
            "destination-profile transition",
            validation.Detail,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PowerSource.Battery, context.Runtime.GetStatus().ActivePowerSource);
        Assert.Equal(
            PerformancePreset.BatterySaver,
            context.Runtime.GetStatus().ConfirmedState!.Performance);
    }

    [Fact]
    public async Task ResumeValidation_UnresumedBaselineDoesNotLabelLaterPowerChange()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.PrepareForLowPowerAsync(CancellationToken.None);
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.DoesNotContain(
            context.History.Snapshot().CommandResults,
            entry => entry.Action == "ResumeValidation");
        Assert.Equal(PowerSource.Battery, context.Runtime.GetStatus().ActivePowerSource);
    }

    [Fact]
    public async Task ResumeValidation_LifecyclePathDoesNotLabelLaterPowerChangeAtDeviceArrival()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        var loop = new ServiceLifecycleRefreshLoop(
            context.Runtime,
            new ResumeThenDeviceArrivalLifecycleEventSource(
                afterSuspending: () => context.Provider.Available = false,
                beforeDeviceArrival: () =>
                {
                    context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
                    context.Provider.Available = true;
                }),
            new LowPowerGate(),
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);

        await loop.RunAsync(CancellationToken.None);

        Assert.DoesNotContain(
            context.History.Snapshot().CommandResults,
            entry => entry.Action == "ResumeValidation");
        Assert.Equal(PowerSource.Battery, context.Runtime.GetStatus().ActivePowerSource);
    }

    [Fact]
    public async Task ResumeValidation_LifecyclePathExpiresBeforeLateDeviceArrival()
    {
        var time = new ManualTimeProvider();
        TestContext context = CreateWritableContext(timeProvider: time);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        var loop = new ServiceLifecycleRefreshLoop(
            context.Runtime,
            new ResumeThenDeviceArrivalLifecycleEventSource(
                afterSuspending: () => context.Provider.Available = false,
                beforeDeviceArrival: () =>
                {
                    time.Advance(
                        ServiceControlRuntime.ResumeValidationObservationWindow
                        + TimeSpan.FromMilliseconds(1));
                    context.Provider.Available = true;
                }),
            new LowPowerGate(),
            NullLogger<ServiceLifecycleRefreshLoop>.Instance);

        await loop.RunAsync(CancellationToken.None);

        Assert.DoesNotContain(
            context.History.Snapshot().CommandResults,
            entry => entry.Action == "ResumeValidation");
        Assert.Equal(PowerSource.PluggedIn, context.Runtime.GetStatus().ActivePowerSource);
    }

    [Fact]
    public void ResumeValidation_ComparatorAllowlistCoversConfirmedState()
    {
        string[] expected = typeof(ConfirmedDeviceState)
            .GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] actual = ServiceControlRuntime.ResumeComparedStatePropertyNames
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
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
        Assert.Contains(
            "Razer Synapse is active",
            status.Message,
            StringComparison.Ordinal);
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
        lighting.Device.ConfirmedState = lighting.Device.ConfirmedState with
        {
            Lighting = new LightingSettings(
                LightingEffect.Static,
                new RgbColor(1, 2, 3),
                25),
        };
        LightingSettings target =
            ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting;
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
    public async Task PerformanceShortcuts_CycleAdmittedPluggedInPresetsAsTemporaryOverrides()
    {
        TestContext context = CreateWritableContext();

        ControlOperationResult dedicated = await context.Runtime
            .CyclePerformanceShortcutAsync(
                PerformanceShortcutSource.DedicatedButton,
                CancellationToken.None);
        ControlOperationResult functionP = await context.Runtime
            .CyclePerformanceShortcutAsync(
                PerformanceShortcutSource.FunctionP,
                CancellationToken.None);
        ControlOperationResult custom = await context.Runtime
            .CyclePerformanceShortcutAsync(
                PerformanceShortcutSource.DedicatedButton,
                CancellationToken.None);
        ControlOperationResult wrapped = await context.Runtime
            .CyclePerformanceShortcutAsync(
                PerformanceShortcutSource.FunctionP,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, dedicated.Status);
        Assert.Equal("The performance button selected Silent.", dedicated.Message);
        Assert.Equal(ControlOperationStatus.Applied, functionP.Status);
        Assert.Equal("Fn+P selected Performance.", functionP.Message);
        Assert.Equal(ControlOperationStatus.Applied, custom.Status);
        Assert.Equal("The performance button selected Custom.", custom.Message);
        Assert.Equal(ControlOperationStatus.Applied, wrapped.Status);
        Assert.Equal("Fn+P selected Balanced.", wrapped.Message);
        Assert.Equal(4, context.Device.PerformanceSetCalls);
        Assert.Equal(
            new PerformanceOverride(
                PerformancePreset.Balanced,
                OverrideLifetime.UntilPowerSourceChange),
            context.Runtime.GetStatus().PerformanceOverride);
    }

    [Fact]
    public async Task PerformanceShortcutPreview_OnlyMatchingLatestIntentCanClearIt()
    {
        TestContext context = CreateWritableContext();
        Guid sessionId = Guid.NewGuid();

        await context.Runtime.SetPerformanceShortcutPreviewAsync(
            sessionId,
            generation: 1,
            PerformancePreset.Silent,
            CancellationToken.None);
        await context.Runtime.SetPerformanceShortcutPreviewAsync(
            sessionId,
            generation: 2,
            PerformancePreset.Performance,
            CancellationToken.None);
        await context.Runtime.SetPerformanceShortcutPreviewAsync(
            sessionId,
            generation: 1,
            preset: null,
            CancellationToken.None);

        Assert.Equal(
            new PerformanceShortcutPreview(
                sessionId,
                2,
                PerformancePreset.Performance),
            context.Runtime.GetStatus().PerformanceShortcutPreview);

        context.Monitor.Current = new ControllerPresenceObservation(
            ["RazerAppEngine"],
            null);
        await context.Runtime.RefreshControllerPresenceAsync(CancellationToken.None);
        Assert.Equal(
            new PerformanceShortcutPreview(
                sessionId,
                2,
                PerformancePreset.Performance),
            context.Runtime.GetStatus().PerformanceShortcutPreview);

        await context.Runtime.SetPerformanceShortcutPreviewAsync(
            sessionId,
            generation: 2,
            preset: null,
            CancellationToken.None);

        Assert.Null(context.Runtime.GetStatus().PerformanceShortcutPreview);
        Assert.Equal(0, context.Device.PerformanceSetCalls);
    }

    [Fact]
    public async Task PerformanceShortcut_OnBatteryUsesBalancedAndBatterySaverCycle()
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            initialPowerSource: PowerSource.Battery);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        PowerProfile battery = context.Runtime.GetStatus().Profiles.Battery;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.BatterySaver,
            battery.Fans,
            ConfirmedBattery(80),
            battery.Lighting);

        ControlOperationResult result = await context.Runtime
            .CyclePerformanceShortcutAsync(
                PerformanceShortcutSource.FunctionP,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(PerformancePreset.BatteryBalanced, context.Device.ConfirmedState.Performance);
        Assert.Equal(
            PerformancePreset.BatteryBalanced,
            context.Runtime.GetStatus().PerformanceOverride!.Value);
    }

    [Fact]
    public async Task PerformanceShortcut_ControllerAdvisoryDoesNotBlockIntent()
    {
        TestContext context = CreateWritableContext(conflicts: ["RazerAppEngine"]);

        ControlOperationResult result = await context.Runtime
            .CyclePerformanceShortcutAsync(
                PerformanceShortcutSource.DedicatedButton,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(
            new PerformanceOverride(
                PerformancePreset.Silent,
                OverrideLifetime.UntilPowerSourceChange),
            context.Runtime.GetStatus().PerformanceOverride);
        Assert.Equal(1, context.Device.PerformanceSetCalls);
        Assert.Contains(
            "RazerAppEngine",
            context.Runtime.GetStatus().PotentialControllers);
    }

    [Fact]
    public async Task PerformanceShortcut_ExplicitlyReclaimsExternalPerformanceChange()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Performance,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(
            [DeviceSubsystem.Performance],
            context.Runtime.GetStatus().ExternalChanges);

        ControlOperationResult result = await context.Runtime
            .CyclePerformanceShortcutAsync(
                PerformanceShortcutSource.DedicatedButton,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(
            "The performance button selected Custom.",
            result.Message);
        Assert.Equal(
            PerformancePreset.Custom,
            context.Runtime.GetStatus().ConfirmedState!.Performance);
        Assert.Equal(
            PerformancePreset.Custom,
            context.Runtime.GetStatus().PerformanceOverride!.Value);
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
    }

    [Fact]
    public async Task PerformanceShortcut_DoesNotClaimUnrelatedExternalFanChange()
    {
        TestContext context = CreateWritableContext();
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Fans = FanMode.Maximum,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Fans = FanMode.Automatic,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(
            [DeviceSubsystem.Fans],
            context.Runtime.GetStatus().ExternalChanges);

        ControlOperationResult result = await context.Runtime
            .CyclePerformanceShortcutAsync(
                PerformanceShortcutSource.DedicatedButton,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(
            [DeviceSubsystem.Fans],
            context.Runtime.GetStatus().ExternalChanges);
    }

    [Fact]
    public async Task PerformanceShortcut_IsNoOpOnUsbCWhereOnlyBalancedIsAdmitted()
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            initialPowerSource: PowerSource.UsbC);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        PowerProfile usbC = context.Runtime.GetStatus().Profiles.UsbC;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            usbC.Performance,
            usbC.Fans,
            ConfirmedBattery(80),
            usbC.Lighting);

        ControlOperationResult result = await context.Runtime
            .CyclePerformanceShortcutAsync(
                PerformanceShortcutSource.DedicatedButton,
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Null(context.Runtime.GetStatus().PerformanceOverride);
        Assert.Equal(0, context.Device.PerformanceSetCalls);
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
        Assert.Equal(2, context.Monitor.ObserveCount);
        Assert.Equal(["RazerAppEngine"], context.Runtime.GetStatus().PotentialControllers);
        Assert.Equal("enumeration warning", context.Runtime.GetStatus().ControllerDetectionWarning);
    }

    [Fact]
    public async Task ScopedPerformanceMismatch_PublishesFinalSnapshotAndLatchesOnlyNamedDrift()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ConfirmedDeviceState before = context.Runtime.GetStatus().ConfirmedState!;
        context.Device.SuppressConfirmationFor = DeviceSubsystem.Performance;
        context.Device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Performance)
            {
                context.Device.ConfirmedState = context.Device.ConfirmedState with
                {
                    Performance = PerformancePreset.Silent,
                };
            }
        };
        int readsBeforeAction = context.Device.StateReadCalls;

        ControlOperationResult result = await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.NotSame(before, context.Runtime.GetStatus().ConfirmedState);
        Assert.Equal(context.Device.ConfirmedState, context.Runtime.GetStatus().ConfirmedState);
        Assert.Equal(
            [DeviceSubsystem.Performance],
            context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(1, context.Device.PerformanceSetCalls);
        Assert.Equal(readsBeforeAction + 2, context.Device.StateReadCalls);
    }

    [Fact]
    public async Task ScopedBatteryMismatch_PublishesProtectionOffAndLatchesBatteryDriftWithoutRetry()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.SuppressConfirmationFor = DeviceSubsystem.Battery;
        context.Device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Battery)
            {
                context.Device.ConfirmedState = context.Device.ConfirmedState with
                {
                    Battery = ProtectionOff(),
                };
            }
        };
        int readsBeforeAction = context.Device.StateReadCalls;

        ControlOperationResult result = await context.Runtime.SetBatteryPolicyAsync(
            BatteryPolicy.Create(75),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Equal(ProtectionOff(), context.Runtime.GetStatus().ConfirmedState!.Battery);
        Assert.Equal([DeviceSubsystem.Battery], context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(1, context.Device.BatterySetCalls);
        Assert.Equal(readsBeforeAction + 2, context.Device.StateReadCalls);
        BatteryConfiguration committed = await context.Store.LoadAsync(CancellationToken.None);
        Assert.Equal(BatteryPolicy.Create(80), committed.Profiles.BatteryPolicy);
        Assert.Null(committed.Preferences.ProtectionEnabled);
    }

    [Fact]
    public void StatusEquivalence_DetectsBatteryOverrideChanges()
    {
        ServiceStatusSnapshot baseline = CreateContext(providerAvailable: false)
            .Runtime
            .GetStatus();

        Assert.False(ServiceControlRuntime.Equivalent(
            baseline,
            baseline with { BatteryPolicyOverride = BatteryPolicy.Create(75) }));
        Assert.False(ServiceControlRuntime.Equivalent(
            baseline,
            baseline with { BatteryProtectionOverride = false }));
        Assert.False(ServiceControlRuntime.Equivalent(
            baseline,
            baseline with
            {
                DisplayBrightnessHidAvailability =
                    DisplayBrightnessHidAvailability.Available,
            }));
    }

    [Fact]
    public async Task DisableProtection_UnchangedCreatesTemporaryOverrideWithoutSetter()
    {
        TestContext context = CreateWritableContext();
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Battery = ProtectionOff(),
        };

        ControlOperationResult result = await context.Runtime.SetBatteryProtectionAsync(
            enabled: false,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(0, context.Device.BatterySetCalls);
        BatteryConfiguration committed = await context.Store.LoadAsync(CancellationToken.None);
        Assert.False(context.Runtime.GetStatus().BatteryProtectionOverride);
        Assert.True(committed.Profiles.PluggedIn.EffectiveBatteryProtectionEnabled);
        Assert.Null(committed.Preferences.ProtectionEnabled);
        Assert.Equal(BatteryPolicy.Create(80), committed.Profiles.BatteryPolicy);
    }

    [Fact]
    public async Task DisableProtection_AppliedCreatesTemporaryOverrideAfterMatchingReadback()
    {
        TestContext context = CreateWritableContext();

        ControlOperationResult result = await context.Runtime.SetBatteryProtectionAsync(
            enabled: false,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(["SetProtectionOff"], context.Device.BatterySetterCalls);
        BatteryConfiguration committed = await context.Store.LoadAsync(CancellationToken.None);
        Assert.False(context.Runtime.GetStatus().BatteryProtectionOverride);
        Assert.True(committed.Profiles.PluggedIn.EffectiveBatteryProtectionEnabled);
        Assert.Null(committed.Preferences.ProtectionEnabled);
        Assert.Equal(BatteryPolicy.Create(80), committed.Profiles.BatteryPolicy);
    }

    [Fact]
    public async Task UnchangedChargeLimit_CreatesGlobalOverrideAfterConfirmedMatch()
    {
        TestContext context = CreateWritableContext();
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Battery = ConfirmedBattery(75),
        };

        ControlOperationResult result = await context.Runtime.SetBatteryPolicyAsync(
            BatteryPolicy.Create(75),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Empty(context.Device.BatterySetterCalls);
        BatteryConfiguration committed = await context.Store.LoadAsync(CancellationToken.None);
        Assert.Equal(BatteryPolicy.Create(80), committed.Profiles.BatteryPolicy);
        Assert.Equal(BatteryPolicy.Create(75), context.Runtime.GetStatus().BatteryPolicyOverride);
        Assert.Null(committed.Preferences.ProtectionEnabled);
    }

    [Fact]
    public async Task CanceledBatteryConfirmation_CommitsNoIntent()
    {
        TestContext context = CreateWritableContext();
        using var cancellation = new CancellationTokenSource();
        context.Device.AfterSetter = _ => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.Runtime.SetBatteryPolicyAsync(
                BatteryPolicy.Create(75),
                cancellation.Token));

        BatteryConfiguration committed = await context.Store.LoadAsync(CancellationToken.None);
        Assert.Equal(BatteryPolicy.Create(80), committed.Profiles.BatteryPolicy);
        Assert.Null(committed.Preferences.ProtectionEnabled);
        Assert.Equal(BatteryPolicy.Create(80), context.Runtime.GetStatus().Profiles.BatteryPolicy);
        Assert.Equal([DeviceSubsystem.Battery], context.Runtime.GetStatus().ExternalChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemporaryBatteryOverride_DoesNotPersistAfterMatchingReadback(
        bool unauthorized)
    {
        Exception failure = unauthorized
            ? new UnauthorizedAccessException("configuration is read-only")
            : new IOException("configuration write failed");
        TestContext context = CreateWritableContext(
            transactionBoundary: (boundary, _) =>
            {
                if (boundary is BatteryConfigurationCommitBoundary.BeforeProfileReplacement)
                {
                    throw failure;
                }

                return Task.CompletedTask;
            });

        ControlOperationResult result = await context.Runtime.SetBatteryPolicyAsync(
            BatteryPolicy.Create(75),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(["SetChargeLimit"], context.Device.BatterySetterCalls);
        BatteryConfiguration committed = await context.Store.LoadAsync(CancellationToken.None);
        Assert.Equal(BatteryPolicy.Create(80), committed.Profiles.BatteryPolicy);
        Assert.Equal(BatteryPolicy.Create(80), context.Runtime.GetStatus().Profiles.BatteryPolicy);
        Assert.Equal(BatteryPolicy.Create(75),
            context.Runtime.GetStatus().DesiredState.Battery.ConfiguredPolicy);
        Assert.Equal(BatteryPolicy.Create(75), context.Runtime.GetStatus().BatteryPolicyOverride);
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
    }

    [Fact]
    public async Task TemporaryBatteryOverride_DoesNotReachConfigurationCancellationBoundary()
    {
        using var cancellation = new CancellationTokenSource();
        TestContext context = CreateWritableContext(
            transactionBoundary: (boundary, boundaryToken) =>
            {
                if (boundary is BatteryConfigurationCommitBoundary.BeforeProfileReplacement)
                {
                    cancellation.Cancel();
                    boundaryToken.ThrowIfCancellationRequested();
                }

                return Task.CompletedTask;
            });

        ControlOperationResult result = await context.Runtime.SetBatteryPolicyAsync(
            BatteryPolicy.Create(75),
            cancellation.Token);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(["SetChargeLimit"], context.Device.BatterySetterCalls);
        BatteryConfiguration committed = await context.Store.LoadAsync(CancellationToken.None);
        Assert.Equal(BatteryPolicy.Create(80), committed.Profiles.BatteryPolicy);
        Assert.Equal(BatteryPolicy.Create(80), context.Runtime.GetStatus().Profiles.BatteryPolicy);
        Assert.Equal(BatteryPolicy.Create(75),
            context.Runtime.GetStatus().DesiredState.Battery.ConfiguredPolicy);
        Assert.Equal(BatteryPolicy.Create(75), context.Runtime.GetStatus().BatteryPolicyOverride);
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
    }

    [Fact]
    public async Task QueuedBatteryIntent_StagesAfterPriorIntentCommits()
    {
        TestContext context = CreateWritableContext();
        var enteredSetter = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSetter = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        context.Device.ChargeLimitSetterBoundary = async () =>
        {
            enteredSetter.SetResult();
            await releaseSetter.Task;
        };

        Task<ControlOperationResult> limit = context.Runtime.SetBatteryPolicyAsync(
            BatteryPolicy.Create(75),
            CancellationToken.None);
        await enteredSetter.Task;
        Task<ControlOperationResult> protection = context.Runtime.SetBatteryProtectionAsync(
            enabled: false,
            CancellationToken.None);
        releaseSetter.SetResult();

        ControlOperationResult[] results = await Task.WhenAll(limit, protection);

        Assert.All(results, result => Assert.Equal(ControlOperationStatus.Applied, result.Status));
        Assert.Equal(
            ["SetChargeLimit", "SetProtectionOff"],
            context.Device.BatterySetterCalls);
        BatteryConfiguration committed = await context.Store.LoadAsync(CancellationToken.None);
        Assert.Equal(BatteryPolicy.Create(80), committed.Profiles.BatteryPolicy);
        Assert.Null(committed.Preferences.ProtectionEnabled);
        Assert.Equal(BatteryPolicy.Create(75), context.Runtime.GetStatus().BatteryPolicyOverride);
        Assert.False(context.Runtime.GetStatus().BatteryProtectionOverride);
    }

    [Fact]
    public async Task BlockedProtectionIntent_CommitsNeitherProfilesNorPreference()
    {
        TestContext context = CreateWritableContext();
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Battery = new ConfirmedBatteryState(true, 75, false),
        };

        ControlOperationResult result = await context.Runtime.SetBatteryProtectionAsync(
            enabled: false,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Empty(context.Device.BatterySetterCalls);
        BatteryConfiguration committed = await context.Store.LoadAsync(CancellationToken.None);
        Assert.Null(committed.Preferences.ProtectionEnabled);
        Assert.Equal(BatteryPolicy.Create(80), committed.Profiles.BatteryPolicy);
    }

    [Fact]
    public async Task UnavailableBatteryIntent_CommitsNothingAndDoesNotClaimStoredIntent()
    {
        TestContext context = CreateContext(providerAvailable: false);

        ControlOperationResult result = await context.Runtime.SetBatteryPolicyAsync(
            BatteryPolicy.Create(75),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.DoesNotContain("stored", result.Message, StringComparison.OrdinalIgnoreCase);
        BatteryConfiguration committed = await context.Store.LoadAsync(CancellationToken.None);
        Assert.Equal(BatteryPolicy.Create(80), committed.Profiles.BatteryPolicy);
        Assert.Null(committed.Preferences.ProtectionEnabled);
    }

    [Fact]
    public async Task ActiveTemporaryIntent_BlocksLimitAndProtectionDespiteExternalDrift()
    {
        TestContext context = CreateWritableContext();
        ControlOperationResult temporary = await context.Runtime.SetFullChargeOverrideAsync(
            enabled: true,
            CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Applied, temporary.Status);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Battery = ConfirmedBattery(80),
        };

        ControlOperationResult limit = await context.Runtime.SetBatteryPolicyAsync(
            BatteryPolicy.Create(75),
            CancellationToken.None);
        ControlOperationResult protection = await context.Runtime.SetBatteryProtectionAsync(
            enabled: true,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, limit.Status);
        Assert.Equal(ControlOperationStatus.Blocked, protection.Status);
        Assert.Equal(["SetTemporaryFullCharge"], context.Device.BatterySetterCalls);
        Assert.True(context.Runtime.GetStatus().DesiredState.Battery.TemporaryFullCharge);
        BatteryConfiguration committed = await context.Store.LoadAsync(CancellationToken.None);
        Assert.Equal(BatteryPolicy.Create(80), committed.Profiles.BatteryPolicy);
        Assert.Null(committed.Preferences.ProtectionEnabled);
    }

    [Fact]
    public async Task ScopedReadbackFailure_PreservesConfirmedAndWaitsForReadOnlyConfirmation()
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
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(
            [DeviceSubsystem.Performance],
            context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.Equal(1, context.Device.PerformanceSetCalls);
        Assert.Equal(3, context.Device.StateReadCalls);
        Assert.True(context.Runtime.GetStatus().WritesBlocked);

        int readsBeforeGuard = context.Device.StateReadCalls;
        ControlOperationResult blocked = await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Balanced,
                OverrideLifetime.Persistent),
            CancellationToken.None);
        ControlOperationResult reapply = await context.Runtime.ReapplyChangedSettingsAsync(
            CancellationToken.None);
        ControlOperationResult shortcut = await context.Runtime.CyclePerformanceShortcutAsync(
            PerformanceShortcutSource.FunctionP,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, blocked.Status);
        Assert.Equal(ControlOperationStatus.Blocked, reapply.Status);
        Assert.Equal(ControlOperationStatus.Blocked, shortcut.Status);
        Assert.Equal(readsBeforeGuard, context.Device.StateReadCalls);
        Assert.Equal(1, context.Device.PerformanceSetCalls);

        context.Device.ReadStateFailureOnCall = null;
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Empty(context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        Assert.Equal(1, context.Device.PerformanceSetCalls);
    }

    [Fact]
    public async Task ScopedReadbackFailure_DisabledGpuResetClearsAfterReadOnlyConfirmation()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            GpuOverclock = new GpuOverclockCapabilities(
                new DeviceNumericRange(-1000, 400, null),
                new DeviceNumericRange(-1000, 3000, null),
                DeviceFeatureAccess.ReadWrite),
        };
        TestContext context = CreateContext(
            providerAvailable: true,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(capabilities));
        context.Device.Capabilities = capabilities;
        PowerProfile active = context.Runtime.GetStatus().Profiles.PluggedIn;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            active.Performance,
            active.Fans,
            ConfirmedBattery(80),
            active.Lighting)
        {
            GpuClockOffsets = new GpuClockOffsetCapabilities(
                "Test NVIDIA GPU",
                new GpuClockOffsetRange(-1000, 400, 100),
                new GpuClockOffsetRange(-1000, 3000, 200)),
        };
        context.Device.GpuClockOffsetManagementState =
            GpuClockOffsetManagementState.Managed;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.ReadStateFailureOnCall = context.Device.StateReadCalls + 2;

        ControlOperationResult result = await context.Runtime
            .SetPerformanceConfigurationOverrideAsync(
                new PerformanceOverride(
                    active.Performance,
                    OverrideLifetime.Persistent)
                {
                    GpuClockTuning = GpuClockTuningSettings.Disabled,
                },
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Equal(
            GpuClockOffsetManagementState.ResetConfirmed,
            context.Device.GpuClockOffsetManagementState);
        Assert.Equal(
            [DeviceSubsystem.Performance],
            context.Runtime.GetStatus().UnconfirmedWrites);

        context.Device.ReadStateFailureOnCall = null;
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Empty(context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScopedReadbackFailure_UnsupportedGpuTuningUsesEffectiveIntent(
        bool enabled)
    {
        TestContext context = CreateWritableContext();
        context.Device.GpuClockOffsetManagementState =
            GpuClockOffsetManagementState.Unknown;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.ReadStateFailureOnCall = context.Device.StateReadCalls + 2;
        var tuning = new GpuClockTuningSettings(
            enabled,
            new GpuClockOffsetSettings(100, 200));

        ControlOperationResult result = await context.Runtime
            .SetPerformanceConfigurationOverrideAsync(
                new PerformanceOverride(
                    PerformancePreset.Performance,
                    OverrideLifetime.Persistent)
                {
                    GpuClockTuning = tuning,
                },
                CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Equal(
            GpuClockOffsetManagementState.Relinquished,
            context.Device.GpuClockOffsetManagementState);
        Assert.Equal(
            [DeviceSubsystem.Performance],
            context.Runtime.GetStatus().UnconfirmedWrites);
        int setters = context.Device.TotalSetterCalls;

        context.Device.ReadStateFailureOnCall = null;
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Empty(context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        Assert.Equal(setters, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task ScopedReadbackFailure_LaterMismatchBecomesExternalWithoutRetry()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.ReadStateFailureOnCall = context.Device.StateReadCalls + 2;

        await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);

        context.Device.ReadStateFailureOnCall = null;
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = PerformancePreset.Balanced,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Empty(context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.Equal(
            [DeviceSubsystem.Performance],
            context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(1, context.Device.PerformanceSetCalls);
    }

    [Fact]
    public async Task PendingConfirmation_IsSafeDuringConcurrentPreviewPublishing()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Guid sessionId = Guid.NewGuid();
        Task previews = Task.Run(async () =>
        {
            for (int generation = 1; generation <= 500; generation++)
            {
                await context.Runtime.SetPerformanceShortcutPreviewAsync(
                    sessionId,
                    generation,
                    generation % 2 == 0
                        ? PerformancePreset.Balanced
                        : PerformancePreset.Performance,
                    CancellationToken.None);
            }
        });

        for (int iteration = 0; iteration < 20; iteration++)
        {
            PerformancePreset target = iteration % 2 == 0
                ? PerformancePreset.Performance
                : PerformancePreset.Balanced;
            context.Device.ReadStateFailureOnCall = context.Device.StateReadCalls + 2;
            ControlOperationResult result = await context.Runtime
                .SetPerformanceOverrideAsync(
                    new ManualOverride<PerformancePreset>(
                        target,
                        OverrideLifetime.UntilPowerSourceChange),
                    CancellationToken.None);
            Assert.Equal(ControlOperationStatus.Failed, result.Status);
            Assert.Contains(
                DeviceSubsystem.Performance,
                context.Runtime.GetStatus().UnconfirmedWrites);

            context.Device.ReadStateFailureOnCall = null;
            await context.Runtime.ObserveAsync(CancellationToken.None);
            Assert.Empty(context.Runtime.GetStatus().UnconfirmedWrites);
        }

        await previews;

        Assert.False(context.Runtime.GetStatus().WritesBlocked);
    }

    [Fact]
    public async Task ScopedMatchingAggregateReadback_LatchesUnrelatedDriftAndClearsNamedDrift()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Lighting = new LightingSettings(
                LightingEffect.Static,
                new RgbColor(1, 2, 3),
                25),
        };

        ControlOperationResult result = await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(context.Device.ConfirmedState, context.Runtime.GetStatus().ConfirmedState);
        Assert.Equal([DeviceSubsystem.Lighting], context.Runtime.GetStatus().ExternalChanges);
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
    public async Task FullCharge_AppliesTypedSetterAndCommitsAfterReadback()
    {
        TestContext context = CreateWritableContext();

        ControlOperationResult fullCharge = await context.Runtime.SetFullChargeOverrideAsync(
            true,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, fullCharge.Status);
        Assert.Equal([0, 0, 1, 0], SetterCounts(context.Device));
        Assert.True(context.Runtime.GetStatus().FullChargeOverride);
        Assert.True(context.Runtime.GetStatus().ConfirmedState!.Battery!.TemporaryFullCharge);
    }

    [Fact]
    public async Task RestartObservation_ReportsFirmwareFullChargeAndUnplugRestoresLimit()
    {
        TestContext context = CreateWritableContext();
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Battery = new ConfirmedBatteryState(true, 80, true),
        };

        await context.Runtime.ObserveAsync(CancellationToken.None);

        ServiceStatusSnapshot observed = context.Runtime.GetStatus();
        Assert.True(observed.FullChargeOverride);
        Assert.False(observed.DesiredState.Battery.TemporaryFullCharge);

        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        ServiceStatusSnapshot restored = context.Runtime.GetStatus();
        Assert.False(restored.FullChargeOverride);
        Assert.False(restored.ConfirmedState!.Battery!.TemporaryFullCharge);
        Assert.Equal(80, restored.ConfirmedState.Battery.ChargeLimitPercent);
        Assert.Equal(["SetTemporaryFullCharge"], context.Device.BatterySetterCalls);
    }

    [Fact]
    public async Task PowerTransition_CancelsFullChargeBeforeApplyingProtectionOffProfile()
    {
        TestContext context = CreateWritableContext();
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        profiles = profiles with
        {
            Battery = profiles.Battery with { BatteryProtectionEnabled = false },
        };
        await context.Runtime.UpdateProfilesAsync(profiles, CancellationToken.None);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Battery = new ConfirmedBatteryState(true, 80, true),
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);

        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(
            ["SetTemporaryFullCharge", "SetProtectionOff"],
            context.Device.BatterySetterCalls);
        Assert.Equal(ProtectionOff(), context.Runtime.GetStatus().ConfirmedState!.Battery);
        Assert.False(context.Runtime.GetStatus().DesiredState.Battery.ProtectionEnabled);
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
    }

    [Fact]
    public async Task FailedFullCharge_PreservesPriorIntent()
    {
        TestContext context = CreateWritableContext();
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Battery = new ConfirmedBatteryState(true, 80, true),
        };
        context.Device.ReadStateFailure = new IOException("read failed");

        ControlOperationResult result = await context.Runtime.SetFullChargeOverrideAsync(
            false,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.False(context.Runtime.GetStatus().FullChargeOverride);
        Assert.Equal(0, context.Device.BatterySetCalls);
    }

    [Fact]
    public async Task MismatchedFullCharge_ReportsConfirmedFirmwareState()
    {
        TestContext context = CreateWritableContext();
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Battery = new ConfirmedBatteryState(true, 80, true),
        };
        context.Device.SuppressConfirmationFor = DeviceSubsystem.Battery;

        ControlOperationResult result = await context.Runtime.SetFullChargeOverrideAsync(
            false,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.True(context.Runtime.GetStatus().FullChargeOverride);
        Assert.Equal(1, context.Device.BatterySetCalls);
    }

    [Fact]
    public async Task IndeterminateFullCharge_PreservesPriorIntent()
    {
        TestContext context = CreateWritableContext();
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Battery = new ConfirmedBatteryState(true, 80, true),
        };
        context.Device.ReadStateFailureOnCall = 2;

        ControlOperationResult result = await context.Runtime.SetFullChargeOverrideAsync(
            false,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.False(context.Runtime.GetStatus().FullChargeOverride);
        Assert.Equal(1, context.Device.BatterySetCalls);
    }

    [Fact]
    public async Task CanceledFullCharge_PreservesPriorIntent()
    {
        TestContext context = CreateWritableContext();
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Battery = new ConfirmedBatteryState(true, 80, true),
        };
        using var cancellation = new CancellationTokenSource();
        context.Device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Battery)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.Runtime.SetFullChargeOverrideAsync(false, cancellation.Token));

        Assert.False(context.Runtime.GetStatus().FullChargeOverride);
        Assert.Equal(1, context.Device.BatterySetCalls);
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
    public async Task PowerTransition_PreReadFailureRetriesBeforeAnyWriteAndRecovers()
    {
        var delays = new List<TimeSpan>();
        CountingBladeDevice? deviceAtDelay = null;
        TestContext context = CreateWritableContext(
            transitionRetryDelay: (delay, _) =>
            {
                Assert.Equal(0, deviceAtDelay!.TotalSetterCalls);
                delays.Add(delay);
                return Task.CompletedTask;
            });
        deviceAtDelay = context.Device;
        context.Device.ReadStateFailureOnCall = context.Device.StateReadCalls + 1;
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal([TimeSpan.FromMilliseconds(750)], delays);
        Assert.Equal(2, context.Device.CapabilitiesReadCalls);
        Assert.Equal(3, context.Device.StateReadCalls);
        Assert.Equal(1, context.Provider.TryGetCount);
        Assert.Equal(1, context.Device.PerformanceSetCalls);
        Assert.All(SetterCounts(context.Device), count => Assert.InRange(count, 0, 1));
        Assert.Equal(4, context.History.Snapshot().CommandResults.Count);
        Assert.True(context.Runtime.GetStatus().DeviceVerified);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        Assert.Equal(PowerSource.Battery, context.Runtime.GetStatus().ActivePowerSource);
    }

    [Fact]
    public async Task PowerTransition_RepeatedPreReadFailureStopsAfterOneRetryWithoutWrites()
    {
        var delays = new List<TimeSpan>();
        TestContext context = CreateWritableContext(
            transitionRetryDelay: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });
        context.Device.ReadStateFailure = new IOException("persistent pre-read failure");
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Single(delays);
        Assert.Equal(2, context.Device.CapabilitiesReadCalls);
        Assert.Equal(2, context.Device.StateReadCalls);
        Assert.Equal(0, context.Device.TotalSetterCalls);
        Assert.Equal(1, context.Provider.TryGetCount);
        Assert.Equal(4, context.History.Snapshot().CommandResults.Count);
        Assert.False(context.Runtime.GetStatus().DeviceVerified);
        Assert.True(context.Runtime.GetStatus().WritesBlocked);
    }

    [Fact]
    public async Task PowerTransition_FinalReadFailureAfterWriteDoesNotRetry()
    {
        var delays = new List<TimeSpan>();
        TestContext context = CreateWritableContext(
            transitionRetryDelay: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });
        context.Device.ReadStateFailureOnCall = context.Device.StateReadCalls + 2;
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Empty(delays);
        Assert.Equal(1, context.Device.CapabilitiesReadCalls);
        Assert.Equal(2, context.Device.StateReadCalls);
        Assert.Equal(1, context.Provider.TryGetCount);
        Assert.True(context.Device.TotalSetterCalls > 0);
        Assert.All(SetterCounts(context.Device), count => Assert.InRange(count, 0, 1));
        Assert.NotEmpty(context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.Empty(
            context.Runtime.GetStatus().UnconfirmedWrites.Intersect(
                context.Runtime.GetStatus().ExternalChanges));
        Assert.False(context.Runtime.GetStatus().DeviceVerified);
        Assert.True(context.Runtime.GetStatus().WritesBlocked);

        int setters = context.Device.TotalSetterCalls;
        context.Device.ReadStateFailureOnCall = null;
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Empty(context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.Equal(setters, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task PowerTransition_ManualFansClearHiddenPerformanceConfirmation()
    {
        TestContext context = CreateWritableContext(
            initialPowerSource: PowerSource.Battery);
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        profiles = profiles with
        {
            PluggedIn = profiles.PluggedIn with
            {
                Performance = PerformancePreset.Balanced,
                Fans = FanMode.Maximum,
                FanControl = FanControlSettings.FromLegacy(FanMode.Maximum),
            },
        };
        ControlOperationResult stored = await context.Runtime.UpdateProfilesAsync(
            profiles,
            CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Stored, stored.Status);
        context.Device.ReadStateFailureOnCall = context.Device.StateReadCalls + 2;
        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Contains(
            DeviceSubsystem.Performance,
            context.Runtime.GetStatus().UnconfirmedWrites);
        context.Device.ReadStateFailureOnCall = null;
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = null,
            CustomPerformance = null,
            Fans = FanMode.Maximum,
        };
        int setters = context.Device.TotalSetterCalls;

        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.DoesNotContain(
            DeviceSubsystem.Performance,
            context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.DoesNotContain(
            DeviceSubsystem.Performance,
            context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(setters, context.Device.TotalSetterCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PowerTransition_UnsupportedGpuTuningUsesEffectiveIntent(
        bool enabled)
    {
        DeviceCapabilities profileCapabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            GpuOverclock = new GpuOverclockCapabilities(
                new DeviceNumericRange(-1000, 400, null),
                new DeviceNumericRange(-1000, 3000, null),
                DeviceFeatureAccess.ReadWrite),
        };
        TestContext context = CreateContext(
            providerAvailable: true,
            initialPowerSource: PowerSource.Battery,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(
                profileCapabilities));
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        context.Device.GpuClockOffsetManagementState =
            GpuClockOffsetManagementState.Unknown;
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        profiles = profiles with
        {
            PluggedIn = profiles.PluggedIn with
            {
                GpuClockTuning = new GpuClockTuningSettings(
                    enabled,
                    new GpuClockOffsetSettings(100, 200)),
            },
        };
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            profiles.Battery.Performance,
            profiles.Battery.Fans,
            ConfirmedBattery(80),
            profiles.Battery.Lighting);
        ControlOperationResult stored = await context.Runtime.UpdateProfilesAsync(
            profiles,
            CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Stored, stored.Status);
        context.Device.ReadStateFailureOnCall = context.Device.StateReadCalls + 2;
        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(
            GpuClockOffsetManagementState.Relinquished,
            context.Device.GpuClockOffsetManagementState);
        Assert.Contains(
            DeviceSubsystem.Performance,
            context.Runtime.GetStatus().UnconfirmedWrites);
        int setters = context.Device.TotalSetterCalls;

        context.Device.ReadStateFailureOnCall = null;
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Empty(context.Runtime.GetStatus().UnconfirmedWrites);
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        Assert.Equal(setters, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task PowerTransition_CancellationDuringAccessRetryDoesNotWriteOrPublish()
    {
        using var cancellation = new CancellationTokenSource();
        TestContext context = CreateWritableContext(
            transitionRetryDelay: (_, token) =>
            {
                cancellation.Cancel();
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        context.Device.ReadStateFailureOnCall = context.Device.StateReadCalls + 1;
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.Runtime.HandlePowerSourceChangedAsync(cancellation.Token));

        Assert.Equal(1, context.Device.CapabilitiesReadCalls);
        Assert.Equal(1, context.Device.StateReadCalls);
        Assert.Equal(0, context.Device.TotalSetterCalls);
        Assert.Empty(context.History.Snapshot().CommandResults);
        Assert.Empty(context.Hub.Published);
    }

    [Fact]
    public async Task RealPowerTransition_FinalExternalFanChangeRemainsLatchedAndIsNotUnchanged()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
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
        context.Device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Performance)
            {
                context.Device.ConfirmedState = context.Device.ConfirmedState with
                {
                    Battery = ProtectionOff(),
                };
            }
        };
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);

        Assert.Equal(ProtectionOff(), context.Runtime.GetStatus().ConfirmedState!.Battery);
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
            new ManualOverride<FanMode>(FanMode.Automatic, OverrideLifetime.Persistent),
            CancellationToken.None);
        await context.Runtime.SetFullChargeOverrideAsync(true, CancellationToken.None);
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);

        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        ServiceStatusSnapshot status = context.Runtime.GetStatus();

        Assert.Null(status.PerformanceOverride);
        Assert.Equal(FanMode.Automatic, status.FanOverride!.Value);
        Assert.False(status.FullChargeOverride);
        Assert.False(status.DesiredState.Battery.TemporaryFullCharge);
    }

    [Fact]
    public async Task PersistentOverride_SurvivesServiceRuntimeRecreation()
    {
        string overridePath = Path.Combine(directory, "restart-overrides.json");
        TestContext first = CreateContext(
            providerAvailable: false,
            persistentOverridePath: overridePath,
            loadPersistentOverrides: true);

        ControlOperationResult result = await first.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        TestContext restarted = CreateContext(
            providerAvailable: false,
            persistentOverridePath: overridePath,
            loadPersistentOverrides: true);
        Assert.Equal(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            restarted.Runtime.GetStatus().PerformanceOverride);
    }

    [Fact]
    public async Task PersistentFixedFanSpeed_SurvivesServiceRuntimeRecreation()
    {
        string overridePath = Path.Combine(directory, "restart-fan-overrides.json");
        TestContext first = CreateContext(
            providerAvailable: false,
            persistentOverridePath: overridePath,
            loadPersistentOverrides: true);
        var settings = new FanControlSettings(
            FanMode.Fixed,
            2800,
            ManualFanCurve.Default);

        ControlOperationResult result = await first.Runtime.SetFanControlOverrideAsync(
            new ManualOverride<FanMode>(
                FanMode.Fixed,
                OverrideLifetime.Persistent),
            settings,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        TestContext restarted = CreateContext(
            providerAvailable: false,
            persistentOverridePath: overridePath,
            loadPersistentOverrides: true);
        ServiceStatusSnapshot status = restarted.Runtime.GetStatus();
        Assert.Equal(
            new ManualOverride<FanMode>(
                FanMode.Fixed,
                OverrideLifetime.Persistent),
            status.FanOverride);
        Assert.Equal(settings, status.FanControlOverride);
    }

    [Fact]
    public async Task AcManualFans_PreserveAndRestoreIndependentPerformanceIntent()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);
        context.Device.AfterSetter = subsystem =>
        {
            if (subsystem != DeviceSubsystem.Fans)
            {
                return;
            }

            context.Device.ConfirmedState = context.Device.ConfirmedState.Fans
                == FanMode.Automatic
                    ? context.Device.ConfirmedState with
                    {
                        Performance = PerformancePreset.Balanced,
                    }
                    : context.Device.ConfirmedState with { Performance = null };
        };
        int performanceWritesBeforeManual = context.Device.PerformanceSetCalls;
        int fanWritesBeforeManual = context.Device.FanSetCalls;

        ControlOperationResult manual = await context.Runtime.SetFanControlOverrideAsync(
            new ManualOverride<FanMode>(
                FanMode.Fixed,
                OverrideLifetime.UntilPowerSourceChange),
            new FanControlSettings(FanMode.Fixed, 3200, ManualFanCurve.Default),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, manual.Status);
        Assert.Equal(performanceWritesBeforeManual + 1, context.Device.PerformanceSetCalls);
        Assert.Equal(fanWritesBeforeManual + 1, context.Device.FanSetCalls);
        Assert.Equal(
            [PerformancePreset.Balanced],
            context.Device.PerformanceWrites.Skip(performanceWritesBeforeManual));
        ServiceStatusSnapshot manualStatus = context.Runtime.GetStatus();
        Assert.Equal(PerformancePreset.Performance, manualStatus.DesiredState.Performance.Value);
        Assert.Equal(FanMode.Fixed, manualStatus.DesiredState.Fans.Value);
        Assert.Null(manualStatus.ConfirmedState!.Performance);

        int writesBeforeDeferredIntent = context.Device.TotalSetterCalls;
        ControlOperationResult deferred = await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Silent,
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, deferred.Status);
        Assert.Equal(writesBeforeDeferredIntent, context.Device.TotalSetterCalls);
        Assert.Equal(
            PerformancePreset.Silent,
            context.Runtime.GetStatus().DesiredState.Performance.Value);

        int performanceWritesBeforeAutomatic = context.Device.PerformanceSetCalls;
        int fanWritesBeforeAutomatic = context.Device.FanSetCalls;
        ControlOperationResult automatic = await context.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(
                FanMode.Automatic,
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, automatic.Status);
        Assert.Equal(performanceWritesBeforeAutomatic + 1, context.Device.PerformanceSetCalls);
        Assert.Equal(fanWritesBeforeAutomatic + 1, context.Device.FanSetCalls);
        Assert.Equal(PerformancePreset.Silent, context.Device.PerformanceWrites[^1]);
        Assert.Equal(FanMode.Automatic, context.Device.FanWrites[^1]);
        ServiceStatusSnapshot restored = context.Runtime.GetStatus();
        Assert.Equal(PerformancePreset.Silent, restored.ConfirmedState!.Performance);
        Assert.Equal(FanMode.Automatic, restored.ConfirmedState.Fans);
        Assert.DoesNotContain(DeviceSubsystem.Performance, restored.ExternalChanges);
        Assert.DoesNotContain(DeviceSubsystem.Fans, restored.ExternalChanges);
    }

    [Theory]
    [InlineData(PowerSource.PluggedIn, PerformancePreset.Performance)]
    [InlineData(PowerSource.Battery, PerformancePreset.BatteryBalanced)]
    public async Task UnchangedAutomaticFanIntent_PreservesExternalPerformanceDrift(
        PowerSource powerSource,
        PerformancePreset externalPerformance)
    {
        TestContext context = CreateWritableContext(initialPowerSource: powerSource);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = externalPerformance,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(
            [DeviceSubsystem.Performance],
            context.Runtime.GetStatus().ExternalChanges);
        int writesBeforeCommand = context.Device.TotalSetterCalls;

        ControlOperationResult result = await context.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(
                FanMode.Automatic,
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(writesBeforeCommand, context.Device.TotalSetterCalls);
        Assert.Equal(
            [DeviceSubsystem.Performance],
            context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(
            externalPerformance,
            context.Runtime.GetStatus().ConfirmedState!.Performance);
    }

    [Fact]
    public async Task AcManualFanEntry_ReclaimsExternallyManualFirmwareState()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Performance = null,
            Fans = FanMode.Maximum,
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.AfterSetter = subsystem =>
        {
            if (subsystem != DeviceSubsystem.Fans)
            {
                return;
            }

            context.Device.ConfirmedState = context.Device.ConfirmedState.Fans
                == FanMode.Automatic
                    ? context.Device.ConfirmedState with
                    {
                        Performance = PerformancePreset.Balanced,
                    }
                    : context.Device.ConfirmedState with { Performance = null };
        };
        int performanceWritesBeforeCommand = context.Device.PerformanceSetCalls;
        int fanWritesBeforeCommand = context.Device.FanSetCalls;

        ControlOperationResult result = await context.Runtime.SetFanControlOverrideAsync(
            new ManualOverride<FanMode>(
                FanMode.Fixed,
                OverrideLifetime.UntilPowerSourceChange),
            new FanControlSettings(FanMode.Fixed, 3200, ManualFanCurve.Default),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        Assert.Equal(performanceWritesBeforeCommand, context.Device.PerformanceSetCalls);
        Assert.Equal(fanWritesBeforeCommand + 2, context.Device.FanSetCalls);
        Assert.Equal(
            [FanMode.Automatic, FanMode.Fixed],
            context.Device.FanWrites.Skip(fanWritesBeforeCommand));
        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(FanMode.Fixed, status.ConfirmedState!.Fans);
        Assert.Null(status.ConfirmedState.Performance);
        Assert.Empty(status.ExternalChanges);
    }

    [Fact]
    public async Task PerformanceShortcut_WithAcManualFansStoresIntentWithoutHardwareWrite()
    {
        TestContext context = CreateWritableContext();
        context.Device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Fans)
            {
                context.Device.ConfirmedState = context.Device.ConfirmedState with
                {
                    Performance = null,
                };
            }
        };
        await context.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(
                FanMode.Maximum,
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);
        int writesBeforeShortcut = context.Device.TotalSetterCalls;

        ControlOperationResult result = await context.Runtime.CyclePerformanceShortcutAsync(
            PerformanceShortcutSource.FunctionP,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Stored, result.Status);
        Assert.Equal(
            "Fn+P selected Silent. It will be applied when fans return to Automatic.",
            result.Message);
        Assert.Equal(writesBeforeShortcut, context.Device.TotalSetterCalls);
        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(
            PerformancePreset.Silent,
            status.PerformanceOverride!.Value);
        Assert.Equal(FanMode.Maximum, status.ConfirmedState!.Fans);
        Assert.Null(status.ConfirmedState.Performance);
    }

    [Fact]
    public async Task SwitchingPersistentOverrideToTemporary_RemovesDurableValue()
    {
        string overridePath = Path.Combine(directory, "replacement-overrides.json");
        TestContext first = CreateContext(
            providerAvailable: false,
            persistentOverridePath: overridePath,
            loadPersistentOverrides: true);
        await first.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);

        await first.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Silent,
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);

        Assert.Equal(
            OverrideLifetime.UntilPowerSourceChange,
            first.Runtime.GetStatus().PerformanceOverride!.Lifetime);
        TestContext restarted = CreateContext(
            providerAvailable: false,
            persistentOverridePath: overridePath,
            loadPersistentOverrides: true);
        Assert.Null(restarted.Runtime.GetStatus().PerformanceOverride);
    }

    [Fact]
    public async Task ClearingPersistentOverride_RemovesDurableValueWhileDeviceUnavailable()
    {
        string overridePath = Path.Combine(directory, "cleared-overrides.json");
        TestContext first = CreateContext(
            providerAvailable: false,
            persistentOverridePath: overridePath,
            loadPersistentOverrides: true);
        await first.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Maximum, OverrideLifetime.Persistent),
            CancellationToken.None);

        ControlOperationResult result = await first.Runtime.SetFanOverrideAsync(
            null,
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        TestContext restarted = CreateContext(
            providerAvailable: false,
            persistentOverridePath: overridePath,
            loadPersistentOverrides: true);
        Assert.Null(restarted.Runtime.GetStatus().FanOverride);
    }

    [Fact]
    public async Task PersistentOverrideWriteFailure_LeavesIntentUnchangedAndSkipsDevice()
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            overrideWriteBoundary: _ =>
                Task.FromException(new IOException("disk unavailable")));

        ControlOperationResult result = await context.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Maximum, OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Null(context.Runtime.GetStatus().FanOverride);
        Assert.Equal(0, context.Provider.TryGetCount);
        Assert.Equal(0, context.Device.TotalCalls);
    }

    [Fact]
    public async Task IndependentAcThermalOverrides_PersistBeforeUnavailableHardwareResult()
    {
        string overridePath = Path.Combine(directory, "thermal-safety-overrides.json");
        TestContext context = CreateContext(
            providerAvailable: false,
            persistentOverridePath: overridePath,
            loadPersistentOverrides: true);
        await context.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(FanMode.Maximum, OverrideLifetime.Persistent),
            CancellationToken.None);

        ControlOperationResult result = await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(
            PerformancePreset.Performance,
            context.Runtime.GetStatus().PerformanceOverride!.Value);
        Assert.Equal(FanMode.Maximum, context.Runtime.GetStatus().FanOverride!.Value);
        TestContext restarted = CreateContext(
            providerAvailable: false,
            persistentOverridePath: overridePath,
            loadPersistentOverrides: true);
        Assert.Equal(
            PerformancePreset.Performance,
            restarted.Runtime.GetStatus().PerformanceOverride!.Value);
        Assert.Equal(FanMode.Maximum, restarted.Runtime.GetStatus().FanOverride!.Value);
    }

    [Theory]
    [InlineData(PowerSource.PluggedIn, PerformancePreset.BatterySaver)]
    [InlineData(PowerSource.PluggedIn, PerformancePreset.BatteryBalanced)]
    [InlineData(PowerSource.Battery, PerformancePreset.Silent)]
    [InlineData(PowerSource.Battery, PerformancePreset.Balanced)]
    [InlineData(PowerSource.Battery, PerformancePreset.Performance)]
    public async Task ManualPerformanceOverride_IncompatibleWithPowerSource_IsRejectedBeforeDeviceAccess(
        PowerSource source,
        PerformancePreset performance)
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            initialPowerSource: source);

        ControlOperationResult result = await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Null(context.Runtime.GetStatus().PerformanceOverride);
        Assert.Equal(0, context.Provider.TryGetCount);
        Assert.Equal(0, context.Device.TotalCalls);
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
        Assert.Equal(80, status.DesiredState.Battery.ConfiguredPolicy.ChargeLimitPercent);
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
        Assert.Equal(PowerSource.Battery, status.ActivePowerSource);
    }

    [Fact]
    public async Task BlockedChargeLimit_DoesNotChangeConfiguredLimitIntent()
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
        Assert.Equal(80, status.DesiredState.Battery.ConfiguredPolicy.ChargeLimitPercent);
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
        Assert.Equal(PowerSource.Battery, status.ActivePowerSource);
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
            new ManualOverride<FanMode>(FanMode.Automatic, OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal([1L, 2L], context.Hub.Published.Select(status => status.Revision));
        Assert.Equal(2, context.Runtime.GetStatus().Revision);
    }

    [Fact]
    public async Task Refresh_StoresTelemetryOnlyChangesWithoutBroadcastingThem()
    {
        TestContext context = CreateContext(providerAvailable: false);

        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Empty(context.Hub.Published);

        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn) with
        {
            BatteryPowerFlowMilliwatts = -19_500,
        };
        await context.Runtime.ObserveAsync(
            includeLiveReadings: true,
            CancellationToken.None);

        Assert.Empty(context.Hub.Published);
        Assert.Equal(-19_500, context.Runtime.GetStatus().LiveReadings?.BatteryPowerFlowMilliwatts);
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
            ConfirmedBattery(80),
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
        ConfirmedBatteryState confirmedBattery = ConfirmedBattery(65);
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
    public async Task FailedCapabilityRefresh_PreservesLastConfirmedControlSurface()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            ChargeLimits = new HashSet<int> { 50, 60, 70, 80 },
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        DeviceCapabilities confirmed = Assert.IsType<DeviceCapabilities>(
            context.Runtime.GetStatus().Capabilities);

        context.Device.Capabilities = DeviceCapabilities.Unavailable();
        await context.Runtime.ObserveAsync(CancellationToken.None);

        Assert.Same(confirmed, context.Runtime.GetStatus().Capabilities);
        Assert.True(context.Runtime.GetStatus().DeviceVerified);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
    }

    [Fact]
    public async Task Observe_WithLiveReadingsRefreshesBothFansAndFailureDegradesWithoutLosingDevice()
    {
        var temperatures = new FixedFanTemperatureSource(
            new FanTemperatureReadings(47, 52));
        TestContext context = CreateContext(
            providerAvailable: true,
            fanTemperatureSource: temperatures);
        context.Device.FanRpm = 2700;

        await context.Runtime.ObserveAsync(includeLiveReadings: true, CancellationToken.None);

        Assert.Equal(2700, context.Runtime.GetStatus().LiveReadings!.Fan1Rpm);
        Assert.Equal(2700, context.Runtime.GetStatus().LiveReadings!.Fan2Rpm);
        Assert.Equal(47, context.Runtime.GetStatus().LiveReadings!.CpuTemperatureCelsius);
        Assert.Equal(52, context.Runtime.GetStatus().LiveReadings!.GpuTemperatureCelsius);
        Assert.True(context.Runtime.GetStatus().DeviceVerified);
        Assert.Equal(1, context.Device.FanRpmReadCalls);
        Assert.Equal(1, temperatures.ReadCount);
        int capabilityReads = context.Device.CapabilitiesReadCalls;
        int stateReads = context.Device.StateReadCalls;

        context.Device.FanRpmFailure = new IOException("fan telemetry unavailable");
        await context.Runtime.ObserveAsync(includeLiveReadings: true, CancellationToken.None);

        Assert.Null(context.Runtime.GetStatus().LiveReadings!.Fan1Rpm);
        Assert.Null(context.Runtime.GetStatus().LiveReadings!.Fan2Rpm);
        Assert.Equal(47, context.Runtime.GetStatus().LiveReadings!.CpuTemperatureCelsius);
        Assert.Equal(52, context.Runtime.GetStatus().LiveReadings!.GpuTemperatureCelsius);
        Assert.True(context.Runtime.GetStatus().DeviceVerified);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        Assert.Equal("Verified device state observed.", context.Runtime.GetStatus().Message);
        Assert.Equal(2, context.Device.FanRpmReadCalls);
        Assert.Equal(2, temperatures.ReadCount);
        Assert.Equal(capabilityReads, context.Device.CapabilitiesReadCalls);
        Assert.Equal(stateReads, context.Device.StateReadCalls);
    }

    [Fact]
    public async Task LiveReadingsFastPath_DeviceMissForcesNextPollThroughFullObservation()
    {
        var brightnessController = new RecordingDisplayBrightnessHidController
        {
            Availability = DisplayBrightnessHidAvailability.Available,
        };
        TestContext context = CreateContext(
            providerAvailable: true,
            displayBrightnessHidController: brightnessController);
        await context.Runtime.ObserveAsync(
            includeLiveReadings: true,
            CancellationToken.None);
        int capabilityReads = context.Device.CapabilitiesReadCalls;
        Assert.Equal(
            DisplayBrightnessHidAvailability.Available,
            context.Runtime.GetStatus().DisplayBrightnessHidAvailability);

        context.Provider.Available = false;
        brightnessController.Availability =
            DisplayBrightnessHidAvailability.Unavailable;
        await context.Runtime.ObserveAsync(
            includeLiveReadings: true,
            CancellationToken.None);

        Assert.True(context.Runtime.GetStatus().DeviceVerified);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);
        Assert.Contains(
            "temporary device-access failure",
            context.Runtime.GetStatus().Message,
            StringComparison.OrdinalIgnoreCase);

        await context.Runtime.ObserveAsync(
            includeLiveReadings: true,
            CancellationToken.None);

        Assert.True(context.Runtime.GetStatus().DeviceVerified);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);

        await context.Runtime.ObserveAsync(
            includeLiveReadings: true,
            CancellationToken.None);

        Assert.False(context.Runtime.GetStatus().DeviceVerified);
        Assert.True(context.Runtime.GetStatus().WritesBlocked);
        Assert.Equal(
            DisplayBrightnessHidAvailability.Unavailable,
            context.Runtime.GetStatus().DisplayBrightnessHidAvailability);
        Assert.Equal(capabilityReads, context.Device.CapabilitiesReadCalls);
    }

    [Fact]
    public async Task Observe_DisabledSkipsLiveProvidersAndClearsPriorReadings()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.FanReadings = new FanRpmReadings(2_300, 2_700);
        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn) with
        {
            BatteryPowerFlowMilliwatts = 31_400,
        };

        await context.Runtime.ObserveAsync(
            includeLiveReadings: false,
            CancellationToken.None);

        Assert.Equal(0, context.Device.FanRpmReadCalls);
        Assert.Equal(0, context.Telemetry.BatteryPowerFlowCaptureCount);
        Assert.Null(context.Runtime.GetStatus().LiveReadings);

        await context.Runtime.ObserveAsync(
            includeLiveReadings: true,
            CancellationToken.None);

        Assert.Equal(1, context.Device.FanRpmReadCalls);
        Assert.Equal(1, context.Telemetry.BatteryPowerFlowCaptureCount);
        Assert.Equal(
            new LiveReadings(31_400, 2_300, 2_700),
            context.Runtime.GetStatus().LiveReadings);

        await context.Runtime.ObserveAsync(
            includeLiveReadings: false,
            CancellationToken.None);

        Assert.Equal(1, context.Device.FanRpmReadCalls);
        Assert.Equal(1, context.Telemetry.BatteryPowerFlowCaptureCount);
        Assert.Null(context.Runtime.GetStatus().LiveReadings);
    }

    [Fact]
    public async Task Observe_BatteryOnlySkipsHidAndTemperatureSensors()
    {
        var temperatures = new FixedFanTemperatureSource(
            new FanTemperatureReadings(47, 52));
        TestContext context = CreateContext(
            providerAvailable: true,
            fanTemperatureSource: temperatures);
        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn) with
        {
            BatteryPowerFlowMilliwatts = 18_400,
        };

        await context.Runtime.ObserveAsync(
            LiveReadingSensors.BatteryPower,
            CancellationToken.None);

        Assert.Equal(new LiveReadings(18_400, null, null), context.Runtime.GetStatus().LiveReadings);
        Assert.Equal(0, context.Device.FanRpmReadCalls);
        Assert.Equal(0, temperatures.ReadCount);
    }

    [Fact]
    public async Task Observe_DelegatesProfileIndependentGpuActivityGateToTemperatureSource()
    {
        var temperatures = new FixedFanTemperatureSource(
            new FanTemperatureReadings(47, 52));
        TestContext context = CreateContext(
            providerAvailable: true,
            initialPowerSource: PowerSource.Battery,
            fanTemperatureSource: temperatures);

        await context.Runtime.ObserveAsync(
            LiveReadingSensors.CpuTemperature | LiveReadingSensors.GpuTemperature,
            CancellationToken.None);

        Assert.Equal([(true, true)], temperatures.Requests);
        Assert.Equal(47, context.Runtime.GetStatus().LiveReadings!.CpuTemperatureCelsius);
        Assert.Equal(52, context.Runtime.GetStatus().LiveReadings!.GpuTemperatureCelsius);
    }

    [Fact]
    public async Task LiveReadings_PersistAcrossScopedUpdateAndClearOnPowerOrDisabledObservation()
    {
        TestContext context = CreateContext(providerAvailable: true);
        context.Device.FanRpm = 2700;
        await context.Runtime.ObserveAsync(includeLiveReadings: true, CancellationToken.None);
        context.Provider.Available = false;

        await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent),
            CancellationToken.None);
        Assert.Equal(2700, context.Runtime.GetStatus().LiveReadings!.Fan1Rpm);

        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        Assert.Null(context.Runtime.GetStatus().LiveReadings);

        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Null(context.Runtime.GetStatus().LiveReadings);
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
            ConfirmedBattery(80),
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
            ConfirmedBattery(80),
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
        Assert.Equal(
            ["Blocked", "Blocked", "Unchanged", "Blocked"],
            snapshot.CommandResults.Select(result => result.Status));
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
            ConfirmedBattery(80),
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

    [Fact]
    public async Task Observation_ClassifiesUsbCPdOnlyAfterTwoAgreeingAdapterReads()
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            initialPowerSource: PowerSource.ExternalUnknown);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        context.Device.AdapterPowerState = AdapterPowerState.UsbCPower100W;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
            context.Runtime.GetStatus().Profiles.UsbC.Lighting);

        await context.Runtime.ObserveAsync(CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(PowerSource.UsbC, status.ActivePowerSource);
        Assert.Equal(2, context.Device.AdapterPowerReadCalls);
        Assert.Equal(PerformancePreset.Balanced, status.DesiredState.Performance.Value);
        Assert.Equal(FanMode.Automatic, status.DesiredState.Fans.Value);
        Assert.False(status.WritesBlocked);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task LiveTelemetry_ReusesKnownExternalClassificationBetweenPeriodicRefreshes()
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            initialPowerSource: PowerSource.ExternalUnknown);
        context.Telemetry.Current = CreateTelemetry(PowerSource.ExternalUnknown);
        context.Device.AdapterPowerState = AdapterPowerState.UsbCPower100W;
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
            context.Runtime.GetStatus().Profiles.UsbC.Lighting);

        await context.Runtime.ObserveAsync(
            includeLiveReadings: false,
            CancellationToken.None);
        int classifiedReads = context.Device.AdapterPowerReadCalls;
        Assert.Equal(2, classifiedReads);

        await context.Runtime.ObserveAsync(
            includeLiveReadings: true,
            CancellationToken.None);
        await context.Runtime.ObserveAsync(
            includeLiveReadings: true,
            CancellationToken.None);

        Assert.Equal(classifiedReads, context.Device.AdapterPowerReadCalls);
        Assert.Equal(PowerSource.UsbC, context.Runtime.GetStatus().ActivePowerSource);

        await context.Runtime.RefreshPowerSourceClassificationAsync(CancellationToken.None);
        Assert.Equal(classifiedReads + 2, context.Device.AdapterPowerReadCalls);
    }

    [Fact]
    public async Task LiveTelemetryFastPath_ReusesRecentControllerObservationButMutationDoesNot()
    {
        TestContext context = CreateWritableContext();

        await context.Runtime.ObserveAsync(
            includeLiveReadings: true,
            CancellationToken.None);
        Assert.Equal(1, context.Monitor.ObserveCount);

        await context.Runtime.ObserveAsync(
            includeLiveReadings: true,
            CancellationToken.None);
        Assert.Equal(1, context.Monitor.ObserveCount);

        _ = await context.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                LightingSettingsFactory.SpectrumCycling(50),
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);

        Assert.Equal(2, context.Monitor.ObserveCount);
    }

    [Theory]
    [InlineData(AdapterPowerState.FullPower280W)]
    [InlineData(AdapterPowerState.FullPower330W)]
    public async Task PeriodicObservation_ReclassifiesUsbCToBarrelWithoutPowerEvent(
        AdapterPowerState fullPowerState)
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            initialPowerSource: PowerSource.ExternalUnknown);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        ProfileSet profiles = context.Runtime.GetStatus().Profiles with
        {
            PluggedIn = context.Runtime.GetStatus().Profiles.PluggedIn with
            {
                Performance = PerformancePreset.Silent,
            },
        };
        _ = await context.Runtime.UpdateProfilesAsync(profiles, CancellationToken.None);
        context.Device.AdapterPowerState = AdapterPowerState.UsbCPower100W;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
            profiles.UsbC.Lighting);
        await context.Runtime.RefreshPowerSourceClassificationAsync(CancellationToken.None);
        Assert.Equal(PowerSource.UsbC, context.Runtime.GetStatus().ActivePowerSource);

        context.Device.AdapterPowerState = fullPowerState;
        await context.Runtime.RefreshPowerSourceClassificationAsync(CancellationToken.None);

        Assert.Equal(PowerSource.PluggedIn, context.Runtime.GetStatus().ActivePowerSource);
        Assert.Equal(PerformancePreset.Silent, context.Device.ConfirmedState.Performance);
        Assert.Equal(1, context.Device.PerformanceSetCalls);
    }

    [Fact]
    public async Task SourceOnlyRefresh_PreservesLiveReadingsAndLastConfirmedSourceAcrossTransientUnknown()
    {
        TestContext context = CreateWritableContext();
        context.Device.FanReadings = new FanRpmReadings(2400, 2300);
        await context.Runtime.ObserveAsync(
            includeLiveReadings: true,
            CancellationToken.None);
        LiveReadings expected = Assert.IsType<LiveReadings>(
            context.Runtime.GetStatus().LiveReadings);
        int stateReads = context.Device.StateReadCalls;
        int fanReads = context.Device.FanRpmReadCalls;

        await context.Runtime.RefreshPowerSourceClassificationAsync(CancellationToken.None);

        Assert.Equal(expected, context.Runtime.GetStatus().LiveReadings);
        Assert.Equal(stateReads, context.Device.StateReadCalls);
        Assert.Equal(fanReads, context.Device.FanRpmReadCalls);

        context.Telemetry.Current = CreateTelemetry(PowerSource.ExternalUnknown);
        context.Device.AdapterPowerState = AdapterPowerState.Unknown;
        await context.Runtime.RefreshPowerSourceClassificationAsync(CancellationToken.None);

        ServiceStatusSnapshot unknown = context.Runtime.GetStatus();
        Assert.Equal(PowerSource.PluggedIn, unknown.ActivePowerSource);
        Assert.True(unknown.WritesBlocked);
        Assert.Contains("last confirmed power source", unknown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(expected, unknown.LiveReadings);
        Assert.Equal(stateReads, context.Device.StateReadCalls);
        Assert.Equal(fanReads, context.Device.FanRpmReadCalls);

        context.Device.AdapterPowerState = AdapterPowerState.FullPower280W;
        await context.Runtime.RefreshPowerSourceClassificationAsync(CancellationToken.None);

        ServiceStatusSnapshot recovered = context.Runtime.GetStatus();
        Assert.Equal(PowerSource.PluggedIn, recovered.ActivePowerSource);
        Assert.False(recovered.WritesBlocked);
        Assert.Equal(expected, recovered.LiveReadings);
        Assert.Equal(stateReads, context.Device.StateReadCalls);
        Assert.Equal(fanReads, context.Device.FanRpmReadCalls);
    }

    [Fact]
    public async Task SteadyUsbCPd_TransientUnknownSamplesDoNotReplaceConfirmedDashboardSource()
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            initialPowerSource: PowerSource.ExternalUnknown);
        context.Telemetry.Current = CreateTelemetry(PowerSource.ExternalUnknown);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        context.Device.AdapterPowerState = AdapterPowerState.UsbCPower100W;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
            context.Runtime.GetStatus().Profiles.UsbC.Lighting);

        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Equal(PowerSource.UsbC, context.Runtime.GetStatus().ActivePowerSource);
        Assert.False(context.Runtime.GetStatus().WritesBlocked);

        context.Device.AdapterPowerState = AdapterPowerState.Unknown;
        await context.Runtime.ObserveAsync(CancellationToken.None);

        ServiceStatusSnapshot transient = context.Runtime.GetStatus();
        Assert.Equal(PowerSource.UsbC, transient.ActivePowerSource);
        Assert.True(transient.WritesBlocked);
        Assert.Contains("last confirmed power source", transient.Message, StringComparison.OrdinalIgnoreCase);

        long transientRevision = transient.Revision;
        await context.Runtime.RefreshPowerSourceClassificationAsync(CancellationToken.None);

        Assert.Equal(PowerSource.UsbC, context.Runtime.GetStatus().ActivePowerSource);
        Assert.Equal(transientRevision, context.Runtime.GetStatus().Revision);

        context.Device.AdapterPowerState = AdapterPowerState.UsbCPower100W;
        await context.Runtime.RefreshPowerSourceClassificationAsync(CancellationToken.None);

        ServiceStatusSnapshot recovered = context.Runtime.GetStatus();
        Assert.Equal(PowerSource.UsbC, recovered.ActivePowerSource);
        Assert.False(recovered.WritesBlocked);
        Assert.Equal("Power adapter classification recovered.", recovered.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SteadyUsbCPd_SingleUnknownReadIsRetriedWithoutShowingCheckingPowerSource(
        bool unknownFirst)
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            initialPowerSource: PowerSource.ExternalUnknown);
        context.Telemetry.Current = CreateTelemetry(PowerSource.ExternalUnknown);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        context.Device.AdapterPowerState = AdapterPowerState.UsbCPower100W;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
            context.Runtime.GetStatus().Profiles.UsbC.Lighting);

        await context.Runtime.ObserveAsync(CancellationToken.None);
        ServiceStatusSnapshot confirmed = context.Runtime.GetStatus();
        Assert.Equal(PowerSource.UsbC, confirmed.ActivePowerSource);
        Assert.False(confirmed.WritesBlocked);

        context.Device.AdapterPowerStates.Enqueue(
            unknownFirst
                ? AdapterPowerState.Unknown
                : AdapterPowerState.UsbCPower100W);
        context.Device.AdapterPowerStates.Enqueue(
            unknownFirst
                ? AdapterPowerState.UsbCPower100W
                : AdapterPowerState.Unknown);
        context.Device.AdapterPowerStates.Enqueue(AdapterPowerState.UsbCPower100W);

        await context.Runtime.RefreshPowerSourceClassificationAsync(CancellationToken.None);

        ServiceStatusSnapshot refreshed = context.Runtime.GetStatus();
        Assert.Equal(PowerSource.UsbC, refreshed.ActivePowerSource);
        Assert.False(refreshed.WritesBlocked);
        Assert.DoesNotContain(
            "temporarily unavailable",
            refreshed.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(5, context.Device.AdapterPowerReadCalls);
    }

    [Fact]
    public async Task UnstableAdapterClassification_BlocksWritesAndPreservesTemporaryOverride()
    {
        TestContext context = CreateWritableContext();
        _ = await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);
        int settersBefore = context.Device.TotalSetterCalls;
        context.Telemetry.Current = CreateTelemetry(PowerSource.ExternalUnknown);
        context.Device.AdapterPowerStates.Enqueue(AdapterPowerState.FullPower280W);
        context.Device.AdapterPowerStates.Enqueue(AdapterPowerState.UsbCPower100W);

        await context.Runtime.ObserveAsync(CancellationToken.None);

        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(PowerSource.PluggedIn, status.ActivePowerSource);
        Assert.True(status.WritesBlocked);
        Assert.Contains("last confirmed power source", status.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(context.Runtime.GetStatus().PerformanceOverride);
        Assert.Equal(settersBefore, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task ExternalUnknown_RejectsOverrideBeforePersistenceOrHardwareWrite()
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            initialPowerSource: PowerSource.ExternalUnknown);
        context.Device.AdapterPowerState = AdapterPowerState.Unknown;

        ControlOperationResult result = await context.Runtime.SetPerformanceOverrideAsync(
            new ManualOverride<PerformancePreset>(
                PerformancePreset.Balanced,
                OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Null(context.Runtime.GetStatus().PerformanceOverride);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task TransientUnknownDuringMutation_PreservesVerifiedLaptopAndLastPowerSource()
    {
        TestContext context = CreateWritableContext();
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ServiceStatusSnapshot verified = context.Runtime.GetStatus();
        Assert.True(verified.DeviceVerified);
        Assert.False(verified.WritesBlocked);
        Assert.Equal(PowerSource.PluggedIn, verified.ActivePowerSource);

        context.Telemetry.Current = CreateTelemetry(PowerSource.ExternalUnknown);
        context.Device.AdapterPowerState = AdapterPowerState.Unknown;
        int writesBefore = context.Device.TotalSetterCalls;

        ControlOperationResult result = await context.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                LightingSettingsFactory.SpectrumCycling(50),
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        ServiceStatusSnapshot after = context.Runtime.GetStatus();
        Assert.True(after.DeviceVerified);
        Assert.False(after.WritesBlocked);
        Assert.Equal(PowerSource.PluggedIn, after.ActivePowerSource);
        Assert.Equal(writesBefore, context.Device.TotalSetterCalls);
        Assert.Same(verified.ConfirmedState, after.ConfirmedState);
    }

    [Fact]
    public async Task UsbC_RejectsMaximumFanOverrideWithoutWriting()
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            initialPowerSource: PowerSource.ExternalUnknown);
        context.Device.AdapterPowerState = AdapterPowerState.UsbCPower100W;
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults();
        PowerProfile usbC = context.Runtime.GetStatus().Profiles.UsbC;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            usbC.Performance,
            usbC.Fans,
            ConfirmedBattery(80),
            usbC.Lighting);

        ControlOperationResult result = await context.Runtime.SetFanOverrideAsync(
            new ManualOverride<FanMode>(
                FanMode.Maximum,
                OverrideLifetime.Persistent),
            CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, result.Status);
        Assert.Null(context.Runtime.GetStatus().FanOverride);
        Assert.Equal(0, context.Device.TotalSetterCalls);
    }

    private TestContext CreateWritableContext(
        IReadOnlyList<string>? conflicts = null,
        string? detectionFailure = null,
        Func<BatteryConfigurationCommitBoundary, CancellationToken, Task>?
            transactionBoundary = null,
        Func<TimeSpan, CancellationToken, Task>? transitionRetryDelay = null,
        PowerSource initialPowerSource = PowerSource.PluggedIn,
        TimeProvider? timeProvider = null)
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            conflicts,
            detectionFailure,
            initialPowerSource,
            transactionBoundary: transactionBoundary,
            transitionRetryDelay: transitionRetryDelay,
            timeProvider: timeProvider);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            ChargeLimits = new HashSet<int> { 50, 55, 60, 65, 70, 75, 80 },
            SupportsBatteryProtectionOff = true,
            SupportsTemporaryFullCharge = true,
        };
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        PowerProfile active = initialPowerSource switch
        {
            PowerSource.Battery => defaults.Battery,
            PowerSource.UsbC => defaults.UsbC,
            _ => defaults.PluggedIn,
        };
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            active.Performance,
            active.Fans,
            ConfirmedBattery(80),
            active.Lighting);
        return context;
    }

    private async Task<TestContext> CreateOwnedStaticLightingContextAsync(
        Func<BatteryConfigurationCommitBoundary, CancellationToken, Task>?
            transactionBoundary = null,
        LowPowerGate? lowPowerGate = null,
        IUserConfigurationRepository? userConfigurationRepository = null,
        bool explicitAction = true)
    {
        LightingSettings lighting = LightingSettingsFactory.Static(
            new RgbColor(0, 128, 255),
            brightness: 60);
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Static,
            },
        };
        TestContext context = CreateContext(
            providerAvailable: true,
            profileValidator: ProfileSetValidator.CreateForLightingCapabilities(capabilities),
            transactionBoundary: transactionBoundary,
            lowPowerGate: lowPowerGate,
            userConfigurationRepository: userConfigurationRepository);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            LightingEffects = capabilities.LightingEffects,
        };
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            ConfirmedBattery(80),
            null);
        await context.Runtime.ObserveAsync(CancellationToken.None);

        ControlOperationResult applied = await context.Runtime.SetLightingOverrideAsync(
            new ManualOverride<LightingSettings>(
                lighting,
                OverrideLifetime.UntilPowerSourceChange),
            CancellationToken.None);
        if (!explicitAction)
        {
            typeof(ServiceControlRuntime)
                .GetField(
                    "explicitLightingOwnershipActive",
                    System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(context.Runtime, false);
        }

        Assert.Equal(ControlOperationStatus.Applied, applied.Status);
        Assert.Equal(1, context.Device.LightingSetCalls);
        Assert.Equal(
            LightingRendererOwnershipState.Static,
            context.Runtime.GetStatus().LightingRendererOwnership);
        return context;
    }

    private async Task<TestContext> CreateLightingTransitionContextAsync(
        LightingRendererOwnershipState ownership,
        PowerSource initialPowerSource = PowerSource.PluggedIn,
        Func<TimeSpan, CancellationToken, Task>? transitionRetryDelay = null)
    {
        LightingSettings pluggedInLighting = LightingSettingsFactory.Static(
            new RgbColor(255, 64, 0),
            brightness: 60);
        LightingSettings batteryLighting = LightingSettingsFactory.Static(
            new RgbColor(0, 96, 255),
            brightness: 40);
        LightingSettings usbCLighting = LightingSettingsFactory.Static(
            new RgbColor(128, 0, 255),
            brightness: 30);
        DeviceCapabilities lightingCapabilities = DeviceCapabilities.Unavailable() with
        {
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Static,
            },
        };
        TestContext context = CreateContext(
            providerAvailable: true,
            initialPowerSource: initialPowerSource,
            transitionRetryDelay: transitionRetryDelay,
            profileValidator: ProfileSetValidator.CreateForLightingCapabilities(
                lightingCapabilities));
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            LightingEffects = lightingCapabilities.LightingEffects,
        };
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        profiles = profiles with
        {
            PluggedIn = profiles.PluggedIn with { Lighting = pluggedInLighting },
            Battery = profiles.Battery with { Lighting = batteryLighting },
            UsbC = profiles.UsbC with { Lighting = usbCLighting },
        };
        await context.Runtime.UpdateProfilesAsync(profiles, CancellationToken.None);
        PowerProfile active = initialPowerSource == PowerSource.Battery
            ? profiles.Battery
            : profiles.PluggedIn;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            active.Performance,
            active.Fans,
            ConfirmedBattery(80),
            active.Lighting);
        context.Device.LightingRendererOwnership = ownership;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        if (ownership == LightingRendererOwnershipState.Static)
        {
            ControlOperationResult owned = await context.Runtime.SetLightingOverrideAsync(
                new ManualOverride<LightingSettings>(
                    active.Lighting,
                    OverrideLifetime.UntilPowerSourceChange),
                CancellationToken.None);
            Assert.Equal(ControlOperationStatus.Applied, owned.Status);
        }
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
        Assert.Equal(ownership, context.Runtime.GetStatus().LightingRendererOwnership);
        return context;
    }

    private async Task<TestContext> CreateBatteryBalancedTransitionContextAsync(
        PowerSource initialSource,
        PerformancePreset initialPerformance)
    {
        TestContext context = CreateContext(
            providerAvailable: true,
            initialPowerSource: initialSource);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            ChargeLimits = new HashSet<int> { 80 },
            SupportsBatteryProtectionOff = true,
            SupportsTemporaryFullCharge = true,
        };
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        profiles = profiles with
        {
            Battery = profiles.Battery with
            {
                Performance = PerformancePreset.BatteryBalanced,
            },
        };
        await context.Runtime.UpdateProfilesAsync(profiles, CancellationToken.None);
        PowerProfile active = initialSource == PowerSource.PluggedIn
            ? profiles.PluggedIn
            : profiles.Battery;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            initialPerformance,
            active.Fans,
            ConfirmedBattery(80),
            active.Lighting);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.Empty(context.Runtime.GetStatus().ExternalChanges);
        return context;
    }

    private static int[] SetterCounts(CountingBladeDevice device) =>
    [
        device.PerformanceSetCalls,
        device.FanSetCalls,
        device.BatterySetCalls,
        device.LightingSetCalls,
    ];

    private static int SetterCount(
        CountingBladeDevice device,
        DeviceSubsystem subsystem) => subsystem switch
        {
            DeviceSubsystem.Performance => device.PerformanceSetCalls,
            DeviceSubsystem.Fans => device.FanSetCalls,
            DeviceSubsystem.Battery => device.BatterySetCalls,
            DeviceSubsystem.Lighting => device.LightingSetCalls,
            _ => throw new ArgumentOutOfRangeException(nameof(subsystem)),
        };

    private static ConfirmedBatteryState ConfirmedBattery(int limit) =>
        new(true, limit, false);

    private static ConfirmedBatteryState ProtectionOff() => new(false, null, false);

    private FixedUserConfigurationRepository CreateManualCurveRecoveryRepository()
    {
        LightingSettings lighting = LightingSettingsFactory.SpectrumCycling(
            brightness: 35);
        return new FixedUserConfigurationRepository(
            new BatteryConfiguration(
                ProfileDefaults.CreateSafeDefaults(),
                new BatteryAutomationPreferences(
                    ProtectionEnabled: true,
                    LastKnownLighting: lighting),
                Recovered: false),
            new PersistentOverrideState(
                Performance: null,
                Fans: FanMode.ManualCurve,
                Lighting: lighting,
                FanControl: new FanControlSettings(
                    FanMode.ManualCurve,
                    3700,
                    ManualFanCurve.Default)),
            directory);
    }

    private async Task<TestContext> CreateDeferredRecoveryContextAsync(
        IUserConfigurationRepository repository,
        bool prepareFanRecovery = true,
        PowerSource initialPowerSource = PowerSource.PluggedIn)
    {
        DeviceCapabilities capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            FanModes = new HashSet<FanMode>
            {
                FanMode.Automatic,
                FanMode.Fixed,
                FanMode.ManualCurve,
                FanMode.Maximum,
            },
            LightingEffects = new HashSet<LightingEffect>
            {
                LightingEffect.Off,
                LightingEffect.Static,
                LightingEffect.Spectrum,
            },
            FanSpeedRange = new FanSpeedRange(2000, 5400, 100),
        };
        TestContext context = CreateContext(
            providerAvailable: true,
            initialize: false,
            profileValidator: ProfileSetValidator.CreateForDeviceCapabilities(capabilities),
            userConfigurationRepository: repository,
            initialPowerSource: initialPowerSource);
        BatteryConfiguration machineConfiguration = await context.Store.LoadAsync(
            CancellationToken.None);
        context.Device.Capabilities = capabilities;
        context.Device.ConfirmedState = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Maximum,
            ConfirmedBattery(80),
            LightingSettingsFactory.Off());
        context.Device.LightingRendererOwnership = LightingRendererOwnershipState.None;
        context.Device.ScopedLightingReadback = context.Device.ConfirmedState with
        {
            Lighting = null,
        };
        context.Device.SuppressConfirmationFor = DeviceSubsystem.Lighting;
        context.Device.FanTargetsAvailable = true;
        context.Runtime.Initialize(
            machineConfiguration,
            initialPowerSource,
            PersistentOverrideState.Empty,
            recoveringUnexpectedExit: true);
        await context.Runtime.ObserveAsync(CancellationToken.None);
        await context.Runtime.MaintainDriverModeAsync(CancellationToken.None);
        await context.Runtime.ReconcileUnexpectedExitAsync(CancellationToken.None);
        if (prepareFanRecovery)
        {
            await context.Runtime.PrepareManualFanCurveRecoveryAsync(
                CancellationToken.None);
        }
        await context.Runtime.MaintainFanCurveAsync(CancellationToken.None);
        await context.Runtime.RestoreLightingRendererAsync(CancellationToken.None);
        return context;
    }

    private TestContext CreateContext(
        bool providerAvailable,
        IReadOnlyList<string>? conflicts = null,
        string? detectionFailure = null,
        PowerSource initialPowerSource = PowerSource.PluggedIn,
        bool initialize = true,
        bool protectionEnabled = true,
        Func<BatteryConfigurationCommitBoundary, CancellationToken, Task>?
            transactionBoundary = null,
        string? persistentOverridePath = null,
        Func<CancellationToken, Task>? overrideWriteBoundary = null,
        bool loadPersistentOverrides = false,
        Func<TimeSpan, CancellationToken, Task>? transitionRetryDelay = null,
        FirmwareVersions? firmwareVersions = null,
        ProfileSetValidator? profileValidator = null,
        IFanTemperatureSource? fanTemperatureSource = null,
        BatteryHealthStatus? batteryHealth = null,
        LowPowerGate? lowPowerGate = null,
        TimeProvider? timeProvider = null,
        IUserConfigurationRepository? userConfigurationRepository = null,
        IDisplayBrightnessHidController? displayBrightnessHidController = null)
    {
        string profilePath = Path.Combine(directory, "profiles.json");
        string preferencesPath = Path.Combine(directory, "battery-preferences.json");
        string markerPath = Path.Combine(directory, "battery-configuration.commit.json");
        var store = new BatteryConfigurationStore(
            profilePath,
            preferencesPath,
            markerPath,
            validator: profileValidator,
            postCommitBoundary: null,
            transactionBoundary: transactionBoundary);
        BatteryConfiguration configuration = store.LoadAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        if (!protectionEnabled)
        {
            ProfileSet profiles = configuration.Profiles;
            configuration = configuration with
            {
                Profiles = profiles with
                {
                    PluggedIn = profiles.PluggedIn with
                    {
                        BatteryProtectionEnabled = false,
                    },
                    Battery = profiles.Battery with
                    {
                        BatteryProtectionEnabled = false,
                    },
                    UsbC = profiles.UsbC with
                    {
                        BatteryProtectionEnabled = false,
                    },
                },
                Preferences = new BatteryAutomationPreferences(),
            };
            SeedConfiguration(
                profilePath,
                preferencesPath,
                markerPath,
                configuration);
            configuration = store.LoadAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        var telemetry = new FakeTelemetryProvider
        {
            Current = CreateTelemetry(initialPowerSource),
            BatteryHealth = batteryHealth,
        };
        var hub = new FakeHub();
        var device = new CountingBladeDevice();
        var provider = new CountingProvider(providerAvailable, device);
        var monitor = new FakeControllerPresenceMonitor(
            new ControllerPresenceObservation(conflicts ?? [], detectionFailure));
        var history = new DiagnosticHistory(timeProvider: timeProvider);
        var overrideStore = new PersistentOverrideStore(
            persistentOverridePath ?? Path.Combine(
                directory,
                $"manual-overrides-{Guid.NewGuid():N}.json"),
            overrideWriteBoundary);
        var firmwareSource = new FixedFirmwareVersionSource(
            firmwareVersions ?? FirmwareVersions.Unavailable());
        var runtime = new ServiceControlRuntime(
            store,
            overrideStore,
            telemetry,
            hub,
            provider,
            history,
            lowPowerGate ?? new LowPowerGate(),
            monitor,
            firmwareSource,
            transitionRetryDelay ?? (static (delay, token) => Task.Delay(delay, token)),
            profileValidator ?? ProfileSetValidator.CreateUnverifiedSafeDefaults(),
            fanTemperatureSource: fanTemperatureSource
                ?? new FixedFanTemperatureSource(default),
            timeProvider: timeProvider,
            userConfigurationRepository: userConfigurationRepository,
            displayBrightnessHidController: displayBrightnessHidController);
        if (initialize)
        {
            PersistentOverrideState overrides = loadPersistentOverrides
                ? overrideStore.LoadAsync(CancellationToken.None).GetAwaiter().GetResult().Overrides
                : PersistentOverrideState.Empty;
            runtime.Initialize(configuration, initialPowerSource, overrides);
        }
        return new TestContext(
            runtime,
            store,
            telemetry,
            hub,
            monitor,
            provider,
            device,
            history);
    }

    private static void SeedConfiguration(
        string profilePath,
        string preferencesPath,
        string markerPath,
        BatteryConfiguration configuration)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
        byte[] profiles = JsonSerializer.SerializeToUtf8Bytes(
            new ProfileDocument(ProfileStore.CurrentSchemaVersion, configuration.Profiles),
            options);
        byte[] preferences = JsonSerializer.SerializeToUtf8Bytes(
            new BatteryPreferencesDocument(ProfileStore.CurrentSchemaVersion, configuration.Preferences),
            options);
        byte[] marker = JsonSerializer.SerializeToUtf8Bytes(
            new BatteryConfigurationCommit(
                ProfileStore.CurrentSchemaVersion,
                Convert.ToHexString(SHA256.HashData(profiles)),
                Convert.ToHexString(SHA256.HashData(preferences))),
            options);
        File.WriteAllBytes(profilePath, profiles);
        File.WriteAllBytes(preferencesPath, preferences);
        File.WriteAllBytes(markerPath, marker);
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

    private static TestPowerState CreateTelemetry(
        PowerSource source,
        int? batteryPercent = 80) => new(
            source,
            batteryPercent,
            BatteryPowerFlowMilliwatts: -18_000);

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class RecordingUserConfigurationRepository(
        IReadOnlyDictionary<string, ProfileSet> profiles,
        string root) : IUserConfigurationRepository
    {
        public Dictionary<string, int> Loads { get; } =
            profiles.Keys.ToDictionary(key => key, _ => 0, StringComparer.OrdinalIgnoreCase);

        public Task<UserConfigurationContext> LoadAsync(
            System.Security.Principal.SecurityIdentifier userSid,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProfileSet selected = profiles[userSid.Value];
            Loads[userSid.Value]++;
            string userRoot = Path.Combine(root, userSid.Value);
            var store = new BatteryConfigurationStore(
                Path.Combine(userRoot, "profiles.json"),
                Path.Combine(userRoot, "battery-preferences.json"),
                Path.Combine(userRoot, "battery-configuration.commit.json"));
            var overrides = new PersistentOverrideStore(
                Path.Combine(userRoot, "manual-overrides.json"));
            return Task.FromResult(new UserConfigurationContext(
                store,
                overrides,
                new BatteryConfiguration(
                    selected,
                    new BatteryAutomationPreferences(ProtectionEnabled: true),
                    Recovered: false),
                PersistentOverrideState.Empty));
        }
    }

    private sealed class FixedUserConfigurationRepository(
        BatteryConfiguration configuration,
        PersistentOverrideState overrides,
        string root) : IUserConfigurationRepository
    {
        public int LoadCount { get; private set; }

        public Task<UserConfigurationContext> LoadAsync(
            System.Security.Principal.SecurityIdentifier userSid,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            string userRoot = Path.Combine(root, userSid.Value);
            return Task.FromResult(new UserConfigurationContext(
                new BatteryConfigurationStore(
                    Path.Combine(userRoot, "profiles.json"),
                    Path.Combine(userRoot, "battery-preferences.json"),
                    Path.Combine(userRoot, "battery-configuration.commit.json")),
                new PersistentOverrideStore(
                    Path.Combine(userRoot, "manual-overrides.json")),
                configuration,
                overrides));
        }
    }

    private sealed class MappedUserConfigurationRepository(
        IReadOnlyDictionary<string, BatteryConfiguration> configurations,
        string root) : IUserConfigurationRepository
    {
        public Task<UserConfigurationContext> LoadAsync(
            System.Security.Principal.SecurityIdentifier userSid,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string userRoot = Path.Combine(root, userSid.Value);
            return Task.FromResult(new UserConfigurationContext(
                new BatteryConfigurationStore(
                    Path.Combine(userRoot, "profiles.json"),
                    Path.Combine(userRoot, "battery-preferences.json"),
                    Path.Combine(userRoot, "battery-configuration.commit.json")),
                new PersistentOverrideStore(
                    Path.Combine(userRoot, "manual-overrides.json")),
                configurations[userSid.Value],
                PersistentOverrideState.Empty));
        }
    }

    private sealed record TestContext(
        ServiceControlRuntime Runtime,
        BatteryConfigurationStore Store,
        FakeTelemetryProvider Telemetry,
        FakeHub Hub,
        FakeControllerPresenceMonitor Monitor,
        CountingProvider Provider,
        CountingBladeDevice Device,
        DiagnosticHistory History);

    private sealed class RecordingDisplayBrightnessHidController(
        params ControlOperationResult[] results)
        : IDisplayBrightnessHidController
    {
        private readonly Queue<ControlOperationResult> results = new(results);

        public List<DisplayBrightnessHidUsage> Usages { get; } = [];

        public DisplayBrightnessHidAvailability Availability { get; set; } =
            DisplayBrightnessHidAvailability.Available;

        public DisplayBrightnessHidAvailability GetAvailability() => Availability;

        public Task<ControlOperationResult> SetUsageAsync(
            DisplayBrightnessHidUsage usage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Usages.Add(usage);
            return Task.FromResult(results.Count > 0
                ? results.Dequeue()
                : new ControlOperationResult(
                    ControlOperationStatus.Applied,
                    "Applied."));
        }
    }

    private sealed class FixedFirmwareVersionSource(FirmwareVersions versions)
        : IFirmwareVersionSource
    {
        public FirmwareVersions Capture() => versions;
    }

    private sealed class FixedFanTemperatureSource(FanTemperatureReadings readings)
        : IFanTemperatureSource
    {
        public int ReadCount { get; private set; }

        public List<(bool IncludeCpu, bool IncludeGpu)> Requests { get; } = [];

        public ValueTask<FanTemperatureReadings> ReadAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return ValueTask.FromResult(readings);
        }

        public ValueTask<FanTemperatureReadings> ReadAsync(
            bool includeCpu,
            bool includeGpu,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            Requests.Add((includeCpu, includeGpu));
            return ValueTask.FromResult(new FanTemperatureReadings(
                includeCpu ? readings.CpuCelsius : null,
                includeGpu ? readings.GpuCelsius : null));
        }
    }

    private sealed class FakeControllerPresenceMonitor(ControllerPresenceObservation current)
        : IControllerPresenceMonitor
    {
        public ControllerPresenceObservation Current { get; set; } = current;

        public Exception? Failure { get; set; }

        public int ObserveCount { get; private set; }

        public ControllerPresenceObservation Observe()
        {
            ObserveCount++;
            if (Failure is not null)
            {
                throw Failure;
            }
            return Current;
        }
    }

    private sealed record TestPowerState(
        PowerSource PowerSource,
        int? BatteryPercent,
        int? BatteryPowerFlowMilliwatts);

    private sealed class FakeTelemetryProvider : IPowerStateProvider
    {
        public required TestPowerState Current { get; set; }

        public int CaptureCount { get; private set; }
        public int BatteryPowerFlowCaptureCount { get; private set; }
        public int BatteryHealthCaptureCount { get; private set; }
        public BatteryHealthStatus? BatteryHealth { get; set; }

        public PowerSource CapturePowerSource()
        {
            CaptureCount++;
            return Current.PowerSource;
        }

        public int? CaptureBatteryPowerFlowMilliwatts()
        {
            BatteryPowerFlowCaptureCount++;
            return Current.BatteryPowerFlowMilliwatts;
        }

        public BatteryHealthStatus? CaptureBatteryHealth()
        {
            BatteryHealthCaptureCount++;
            return BatteryHealth;
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

    private sealed class SequenceLifecycleEventSource(
        params ServiceLifecycleTrigger[] triggers) : IServiceLifecycleEventSource
    {
        public async IAsyncEnumerable<ServiceLifecycleTrigger> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            foreach (ServiceLifecycleTrigger trigger in triggers)
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                yield return trigger;
            }
        }
    }

    private sealed class InterleavedResumeLifecycleEventSource(Action betweenTriggers)
        : IServiceLifecycleEventSource
    {
        public async IAsyncEnumerable<ServiceLifecycleTrigger> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return ServiceLifecycleTrigger.Suspending;

            betweenTriggers();
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return ServiceLifecycleTrigger.Resumed;
        }
    }

    private sealed class ResumeThenDeviceArrivalLifecycleEventSource(
        Action afterSuspending,
        Action beforeDeviceArrival) : IServiceLifecycleEventSource
    {
        public async IAsyncEnumerable<ServiceLifecycleTrigger> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return ServiceLifecycleTrigger.Suspending;

            afterSuspending();
            yield return ServiceLifecycleTrigger.Resumed;

            beforeDeviceArrival();
            yield return ServiceLifecycleTrigger.DeviceArrived;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset utcNow = DateTimeOffset.UnixEpoch;
        private long timestamp;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public override long GetTimestamp() => timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan amount)
        {
            utcNow += amount;
            timestamp += amount.Ticks;
        }
    }

    private sealed class FakeHub : IStatusSubscriptionHub
    {
        private readonly object publishGate = new();

        public List<ServiceStatusSnapshot> Published { get; } = [];

        public void Publish(ServiceStatusSnapshot snapshot)
        {
            lock (publishGate)
            {
                Published.Add(snapshot);
            }
        }

        public StatusSubscriptionLease Subscribe(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CountingProvider(bool available, IBladeDevice device)
        : IVerifiedBladeDeviceProvider, IGpuClockOffsetConfirmationInvalidator
    {
        public bool Available { get; set; } = available;

        public bool AvailableWhenUntestedFirmwareAllowed { get; set; }

        public bool AllowUntestedFirmware { get; private set; }

        public FirmwareCompatibilityStatus FirmwareCompatibility =>
            ReportUnknownFirmwareCompatibility
                ? FirmwareCompatibilityStatus.Unknown
                : AvailableWhenUntestedFirmwareAllowed
                ? AllowUntestedFirmware
                    ? FirmwareCompatibilityStatus.UntestedAllowed
                    : FirmwareCompatibilityStatus.UntestedBlocked
                : Available
                    ? FirmwareCompatibilityStatus.Tested
                    : FirmwareCompatibilityStatus.Unknown;

        public bool RequiresUntestedFirmwareOverride =>
            AvailableWhenUntestedFirmwareAllowed;

        public bool ReportUnknownFirmwareCompatibility { get; set; }

        public int TryGetCount { get; private set; }

        public Action? AfterTryGet { get; set; }

        public int InvalidationCount { get; private set; }

        public int RelinquishCount { get; private set; }

        public int GpuClockOffsetInvalidationCount { get; private set; }

        public Action? AfterGpuClockOffsetInvalidation { get; set; }

        public Exception? InvalidationFailure { get; set; }

        public Exception? RelinquishFailure { get; set; }

        public DriverModeRestoreOutcome InvalidationDriverModeRestoreOutcome { get; set; }

        public bool TryGetVerifiedDevice(out IBladeDevice verifiedDevice)
        {
            TryGetCount++;
            verifiedDevice = device;
            AfterTryGet?.Invoke();
            return Available
                || (AvailableWhenUntestedFirmwareAllowed && AllowUntestedFirmware);
        }

        public void SetAllowUntestedFirmware(bool enabled) =>
            AllowUntestedFirmware = enabled;

        public bool TryGetOwnedKeyTriggeredLightingDevice(out IBladeDevice verifiedDevice)
        {
            verifiedDevice = device;
            return device is CountingBladeDevice
            {
                LightingRendererOwnership:
                    LightingRendererOwnershipState.Reactive or
                    LightingRendererOwnershipState.Ripple,
            };
        }

        public bool TryGetOwnedAmbientLightingDevice(out IBladeDevice verifiedDevice)
        {
            verifiedDevice = device;
            return device is CountingBladeDevice
            {
                LightingRendererOwnership:
                    LightingRendererOwnershipState.AmbientAwareness,
            };
        }

        public Task<LightingRendererInvalidationResult> InvalidateLightingRendererAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvalidationCount++;
            if (InvalidationFailure is { } failure)
            {
                InvalidationFailure = null;
                return Task.FromException<LightingRendererInvalidationResult>(failure);
            }

            LightingRendererOwnershipState observed =
                LightingRendererOwnershipState.None;
            if (device is CountingBladeDevice counting)
            {
                observed = counting.LightingRendererOwnership;
                counting.LightingRendererOwnership =
                    LightingRendererOwnershipState.None;
                counting.ConfirmedState = counting.ConfirmedState with { Lighting = null };
            }

            return Task.FromResult(new LightingRendererInvalidationResult(
                observed,
                InvalidationDriverModeRestoreOutcome));
        }

        public Task<LightingRendererOwnershipState> RelinquishLightingRendererAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RelinquishCount++;
            if (RelinquishFailure is { } failure)
            {
                RelinquishFailure = null;
                return Task.FromException<LightingRendererOwnershipState>(failure);
            }
            if (device is not CountingBladeDevice counting)
            {
                return Task.FromResult(LightingRendererOwnershipState.None);
            }

            LightingRendererOwnershipState observed =
                counting.LightingRendererOwnership;
            counting.LightingRendererOwnership = LightingRendererOwnershipState.None;
            counting.ConfirmedState = counting.ConfirmedState with { Lighting = null };
            return Task.FromResult(observed);
        }

        public bool GpuClockOffsetConfirmationRequiresRevalidation { get; set; }

        public Task<bool> InvalidateGpuClockOffsetConfirmationAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GpuClockOffsetInvalidationCount++;
            AfterGpuClockOffsetInvalidation?.Invoke();
            return Task.FromResult(GpuClockOffsetConfirmationRequiresRevalidation);
        }
    }

    private sealed class CountingBladeDevice :
        IBladeDevice,
        IGpuClockOffsetManagementDevice
    {
        public GpuClockOffsetManagementState GpuClockOffsetManagementState
        {
            get;
            set;
        } = GpuClockOffsetManagementState.Relinquished;
        public int TotalCalls { get; private set; }

        public int PerformanceSetCalls { get; private set; }

        public List<PerformancePreset> PerformanceWrites { get; } = [];

        public int FanSetCalls { get; private set; }

        public List<FanMode> FanWrites { get; } = [];

        public bool FanTargetsAvailable { get; set; }

        public List<FanTargetSpeeds> FanTargets { get; } = [];

        public int BatterySetCalls { get; private set; }

        public List<string> BatterySetterCalls { get; } = [];

        public int LightingSetCalls { get; private set; }

        public List<KeyboardBrightnessDirection> KeyboardBrightnessSteps { get; } = [];

        public byte NextKeyboardBrightness { get; set; } = 50;

        public int ReactivePulseCalls { get; private set; }

        public int AmbientFrameCalls { get; private set; }

        public List<bool> PlaybackMuteIndicatorStates { get; } = [];

        public Queue<Exception> PlaybackMuteFailures { get; } = new();

        public LightingRendererOwnershipState LightingRendererOwnership { get; set; }
        public LightingRendererOwnershipState? LightingOwnershipAfterSet { get; set; }

        public int CapabilitiesReadCalls { get; private set; }

        public int StateReadCalls { get; private set; }

        public int FanRpmReadCalls { get; private set; }

        public int AdapterPowerReadCalls { get; private set; }

        public int McuVersionReadCalls { get; private set; }

        public AdapterPowerState AdapterPowerState { get; set; } =
            AdapterPowerState.FullPower280W;

        public Queue<AdapterPowerState> AdapterPowerStates { get; } = new();

        public Exception? AdapterPowerFailure { get; set; }

        public string? McuVersion { get; set; }

        public int TotalSetterCalls => PerformanceSetCalls
            + FanSetCalls
            + BatterySetCalls
            + LightingSetCalls;

        public DeviceCapabilities Capabilities { get; set; } =
            DeviceCapabilities.Unavailable();

        public ConfirmedDeviceState ConfirmedState { get; set; } =
            new(null, null, null, null);

        public int? FanRpm { get; set; }
        public FanRpmReadings? FanReadings { get; set; }

        public Exception? FanRpmFailure { get; set; }

        public Exception? PerformanceFailure { get; set; }

        public Exception? FanFailure { get; set; }

        public Exception? LightingFailure { get; set; }

        public Exception? OneShotFailure { get; set; }

        public bool? StartupAnimationEnabled { get; set; }

        public FunctionKeyPrimaryMode? FunctionKeyPrimary { get; set; }

        public bool? GamingModeEnabled { get; set; }

        public bool? DriverModeActive { get; set; }

        public Queue<Exception> DriverModeReadFailures { get; } = new();

        public int DriverModeEnsureCalls { get; private set; }

        public int DriverModeReadCalls { get; private set; }

        public int DriverModeReleaseCalls { get; private set; }

        public Exception? DriverModeReleaseFailure { get; set; }

        public int OneShotSetCalls { get; private set; }

        public ApplyOneShotLaptopControl? LastOneShotLaptopControl { get; private set; }

        public Exception? ReadStateFailure { get; set; }

        public int? ReadStateFailureOnCall { get; set; }

        public Exception? ReadStateFailureOnCallException { get; set; }

        public Action? AfterReadState { get; set; }

        public Action<int>? AfterReadStateCall { get; set; }

        public Queue<PowerTransitionDeviceStateReadResult> PowerTransitionStateReads
        {
            get;
        } = new();

        public int PowerTransitionStateReadCalls { get; private set; }

        public List<string> PowerTransitionReadCalls { get; } = [];

        public ConfirmedDeviceState? ScopedLightingReadback { get; set; }

        public Action? BeforeReadLightingState { get; set; }

        public Action<DeviceSubsystem>? AfterSetter { get; set; }

        public Func<Task>? ChargeLimitSetterBoundary { get; set; }

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
            PowerTransitionReadCalls.Add("State");
            if (ReadStateFailure is not null
                || ReadStateFailureOnCall == StateReadCalls)
            {
                throw ReadStateFailure
                    ?? ReadStateFailureOnCallException
                    ?? new IOException("readback failed");
            }

            AfterReadState?.Invoke();
            AfterReadStateCall?.Invoke(StateReadCalls);
            return Task.FromResult(ConfirmedState);
        }

        public Task<ConfirmedDeviceState> ReadLightingStateAsync(
            CancellationToken cancellationToken)
        {
            if (ScopedLightingReadback is null)
            {
                return ReadStateAsync(cancellationToken);
            }

            BeforeReadLightingState?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            TotalCalls++;
            return Task.FromResult(ScopedLightingReadback);
        }

        public async Task<PowerTransitionDeviceStateReadResult>
            ReadStateForPowerTransitionAsync(CancellationToken cancellationToken)
        {
            PowerTransitionStateReadCalls++;
            if (PowerTransitionStateReads.TryDequeue(
                out PowerTransitionDeviceStateReadResult scripted))
            {
                cancellationToken.ThrowIfCancellationRequested();
                TotalCalls++;
                StateReadCalls++;
                PowerTransitionReadCalls.Add("TransitionState");
                return scripted;
            }
            ConfirmedDeviceState state = await ReadStateAsync(cancellationToken);
            return new(
                state,
                LightingOffConfirmationUnavailable: false);
        }

        public Task<FanRpmReadings?> ReadFanRpmAsync(CancellationToken cancellationToken)
        {
            TotalCalls++;
            FanRpmReadCalls++;
            return FanRpmFailure is null
                ? Task.FromResult(
                    FanReadings ?? (FanRpm.HasValue
                        ? new FanRpmReadings(FanRpm.Value, FanRpm.Value)
                        : null))
                : Task.FromException<FanRpmReadings?>(FanRpmFailure);
        }

        public Task<AdapterPowerState> ReadAdapterPowerStateAsync(
            CancellationToken cancellationToken)
        {
            TotalCalls++;
            AdapterPowerReadCalls++;
            if (AdapterPowerFailure is not null)
            {
                return Task.FromException<AdapterPowerState>(AdapterPowerFailure);
            }

            return Task.FromResult(
                AdapterPowerStates.Count > 0
                    ? AdapterPowerStates.Dequeue()
                    : AdapterPowerState);
        }

        public Task<string?> ReadMcuVersionAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (McuVersion is null)
            {
                return Task.FromResult<string?>(null);
            }

            TotalCalls++;
            McuVersionReadCalls++;
            return Task.FromResult<string?>(McuVersion);
        }

        public Task EnsureDriverModeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DriverModeEnsureCalls++;
            return Task.CompletedTask;
        }

        public Task<bool?> ReadDriverModeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DriverModeReadCalls++;
            if (DriverModeReadFailures.TryDequeue(out Exception? failure))
            {
                return Task.FromException<bool?>(failure);
            }
            return Task.FromResult(DriverModeActive);
        }

        public Task<bool?> ReleaseDriverModeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DriverModeReleaseCalls++;
            if (DriverModeReleaseFailure is not null)
            {
                return Task.FromException<bool?>(DriverModeReleaseFailure);
            }
            DriverModeActive = false;
            return Task.FromResult<bool?>(false);
        }

        public Task SetPerformanceAsync(PerformancePreset value, CancellationToken cancellationToken)
        {
            TotalCalls++;
            PerformanceSetCalls++;
            PerformanceWrites.Add(value);
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

        public Task SetPerformanceAsync(
            PerformancePreset value,
            CustomPerformanceSettings? customPerformance,
            CancellationToken cancellationToken)
        {
            TotalCalls++;
            PerformanceSetCalls++;
            PerformanceWrites.Add(value);
            if (PerformanceFailure is not null)
            {
                throw PerformanceFailure;
            }

            if (SuppressConfirmationFor != DeviceSubsystem.Performance)
            {
                ConfirmedState = ConfirmedState with
                {
                    Performance = value,
                    CustomPerformance = value == PerformancePreset.Custom
                        ? customPerformance
                        : null,
                };
            }

            AfterSetter?.Invoke(DeviceSubsystem.Performance);
            return Task.CompletedTask;
        }

        public async Task SetPerformanceAsync(
            PerformancePreset value,
            CustomPerformanceSettings? customPerformance,
            GpuClockOffsetSettings? gpuClockOffsets,
            CancellationToken cancellationToken)
        {
            await SetPerformanceAsync(
                value,
                customPerformance,
                cancellationToken);
            if (gpuClockOffsets is not null)
            {
                await SetGpuClockOffsetsAsync(gpuClockOffsets, cancellationToken);
            }
        }

        public Task SetGpuClockOffsetsAsync(
            GpuClockOffsetSettings value,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Capabilities.GpuOverclock is not { Access: DeviceFeatureAccess.ReadWrite })
            {
                return Task.FromException(
                    new CapabilityUnavailableException(
                        "GPU clock tuning is unavailable in this test device."));
            }

            GpuClockOffsetManagementState = GpuClockOffsetManagementState.Managed;
            ConfirmedState = ConfirmedState with
            {
                GpuClockOffsets = new GpuClockOffsetCapabilities(
                    "Test NVIDIA GPU",
                    new GpuClockOffsetRange(-1000, 400, value.CoreMegahertz),
                    new GpuClockOffsetRange(-1000, 3000, value.MemoryMegahertz)),
            };
            return Task.CompletedTask;
        }

        public Task SetPerformanceAsync(
            PerformancePreset value,
            CustomPerformanceSettings? customPerformance,
            GpuClockOffsetSettings? gpuClockOffsets,
            bool retainGpuClockOffsetManagement,
            CancellationToken cancellationToken) =>
            SetManagedPerformanceAsync(
                value,
                customPerformance,
                gpuClockOffsets,
                retainGpuClockOffsetManagement,
                cancellationToken);

        public Task SetGpuClockOffsetsAsync(
            GpuClockOffsetSettings value,
            bool retainManagement,
            CancellationToken cancellationToken)
        {
            if (!retainManagement && value == GpuClockOffsetSettings.Default)
            {
                GpuClockOffsetManagementState =
                    GpuClockOffsetManagementState.ResetConfirmed;
                ConfirmedState = ConfirmedState with { GpuClockOffsets = null };
                return Task.CompletedTask;
            }
            return SetGpuClockOffsetsAsync(value, cancellationToken);
        }

        public Task RelinquishGpuClockOffsetManagementAsync(
            CancellationToken cancellationToken)
        {
            GpuClockOffsetManagementState =
                GpuClockOffsetManagementState.Relinquished;
            ConfirmedState = ConfirmedState with { GpuClockOffsets = null };
            return Task.CompletedTask;
        }

        public Task<bool> InvalidateGpuClockOffsetConfirmationAsync(
            CancellationToken cancellationToken) => Task.FromResult(false);

        private async Task SetManagedPerformanceAsync(
            PerformancePreset value,
            CustomPerformanceSettings? customPerformance,
            GpuClockOffsetSettings? gpuClockOffsets,
            bool retainManagement,
            CancellationToken cancellationToken)
        {
            await SetPerformanceAsync(value, customPerformance, cancellationToken);
            if (gpuClockOffsets is null
                || !retainManagement
                    && gpuClockOffsets == GpuClockOffsetSettings.Default
                    && GpuClockOffsetManagementState ==
                        GpuClockOffsetManagementState.ResetConfirmed)
            {
                return;
            }
            await SetGpuClockOffsetsAsync(
                gpuClockOffsets,
                retainManagement,
                cancellationToken);
        }

        public Task SetFanModeAsync(FanMode value, CancellationToken cancellationToken)
        {
            TotalCalls++;
            FanSetCalls++;
            FanWrites.Add(value);
            if (FanFailure is not null)
            {
                throw FanFailure;
            }

            if (SuppressConfirmationFor != DeviceSubsystem.Fans)
            {
                ConfirmedState = ConfirmedState with { Fans = value };
            }

            AfterSetter?.Invoke(DeviceSubsystem.Fans);
            return Task.CompletedTask;
        }

        public Task SetFanControlAsync(
            FanControlSettings value,
            CancellationToken cancellationToken) =>
            value.Mode == FanMode.ManualCurve && FanTargetsAvailable
                ? SetFanTargetsAsync(
                    Capabilities.FanSpeedRange?.MaximumRpm
                        ?? FanControlSettings.MaximumSpeedRpm,
                    Capabilities.FanSpeedRange?.MaximumRpm
                        ?? FanControlSettings.MaximumSpeedRpm,
                    cancellationToken)
                : SetFanModeAsync(value.Mode, cancellationToken);

        public Task SetFanTargetsAsync(
            int fan1Rpm,
            int fan2Rpm,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!FanTargetsAvailable)
            {
                return Task.FromException(
                    new CapabilityUnavailableException(
                        "Manual fan targets are unavailable in this test device."));
            }

            TotalCalls++;
            FanSetCalls++;
            int maximumRpm = Capabilities.FanSpeedRange?.MaximumRpm
                ?? FanControlSettings.MaximumSpeedRpm;
            FanTargets.Add(new FanTargetSpeeds(
                fan1Rpm,
                fan2Rpm,
                fan1Rpm == maximumRpm
                    && fan2Rpm == maximumRpm));
            ConfirmedState = ConfirmedState with { Fans = FanMode.Maximum };
            AfterSetter?.Invoke(DeviceSubsystem.Fans);
            return Task.CompletedTask;
        }

        public async Task SetChargeLimitAsync(BatteryPolicy value, CancellationToken cancellationToken)
        {
            TotalCalls++;
            BatterySetCalls++;
            BatterySetterCalls.Add("SetChargeLimit");
            if (ChargeLimitSetterBoundary is not null)
            {
                await ChargeLimitSetterBoundary();
            }

            if (SuppressConfirmationFor != DeviceSubsystem.Battery)
            {
                ConfirmedState = ConfirmedState with
                {
                    Battery = ConfirmedBattery(value.ChargeLimitPercent),
                };
            }

            AfterSetter?.Invoke(DeviceSubsystem.Battery);
        }

        public Task SetBatteryProtectionOffAsync(CancellationToken cancellationToken)
        {
            TotalCalls++;
            BatterySetCalls++;
            BatterySetterCalls.Add("SetProtectionOff");
            if (SuppressConfirmationFor != DeviceSubsystem.Battery)
            {
                ConfirmedState = ConfirmedState with { Battery = ProtectionOff() };
            }

            AfterSetter?.Invoke(DeviceSubsystem.Battery);
            return Task.CompletedTask;
        }

        public Task SetTemporaryFullChargeAsync(bool enabled, CancellationToken cancellationToken)
        {
            TotalCalls++;
            BatterySetCalls++;
            BatterySetterCalls.Add("SetTemporaryFullCharge");
            if (SuppressConfirmationFor != DeviceSubsystem.Battery
                && ConfirmedState.Battery is { } battery)
            {
                ConfirmedState = ConfirmedState with
                {
                    Battery = new ConfirmedBatteryState(
                        battery.ProtectionEnabled,
                        battery.ChargeLimitPercent,
                        enabled),
                };
            }

            AfterSetter?.Invoke(DeviceSubsystem.Battery);
            return Task.CompletedTask;
        }

        public Task SetLightingAsync(LightingSettings value, CancellationToken cancellationToken)
        {
            TotalCalls++;
            LightingSetCalls++;
            if (LightingFailure is not null)
            {
                throw LightingFailure;
            }

            if (SuppressConfirmationFor != DeviceSubsystem.Lighting)
            {
                ConfirmedState = ConfirmedState with { Lighting = value };
            }

            LightingRendererOwnership = LightingOwnershipAfterSet ?? value.Effect switch
            {
                LightingEffect.Off => LightingRendererOwnershipState.None,
                LightingEffect.Static => LightingRendererOwnershipState.Static,
                LightingEffect.Spectrum => LightingRendererOwnershipState.Spectrum,
                LightingEffect.Breathing => LightingRendererOwnershipState.Breathing,
                LightingEffect.Reactive => LightingRendererOwnershipState.Reactive,
                LightingEffect.Ripple => LightingRendererOwnershipState.Ripple,
                LightingEffect.AmbientAwareness =>
                    LightingRendererOwnershipState.AmbientAwareness,
                _ => throw new ArgumentOutOfRangeException(),
            };

            AfterSetter?.Invoke(DeviceSubsystem.Lighting);
            return Task.CompletedTask;
        }

        public Task<byte> StepKeyboardBrightnessAsync(
            KeyboardBrightnessDirection direction,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            KeyboardBrightnessSteps.Add(direction);
            if (LightingFailure is not null)
            {
                return Task.FromException<byte>(LightingFailure);
            }

            return Task.FromResult(NextKeyboardBrightness);
        }

        public Task ApplyOneShotLaptopControlAsync(
            ApplyOneShotLaptopControl value,
            CancellationToken cancellationToken)
        {
            TotalCalls++;
            OneShotSetCalls++;
            LastOneShotLaptopControl = value;
            if (value is
                {
                    Control: OneShotLaptopControl.StartupAnimation,
                    Enabled: { } enabled,
                })
            {
                StartupAnimationEnabled = enabled;
            }
            else if (value is
            {
                Control: OneShotLaptopControl.FunctionKeyPrimary,
                FunctionKeyMode: { } functionKeyMode,
            })
            {
                FunctionKeyPrimary = functionKeyMode;
            }
            else if (value is
            {
                Control: OneShotLaptopControl.GamingMode,
                Enabled: { } gamingModeEnabled,
            })
            {
                GamingModeEnabled = gamingModeEnabled;
            }
            return OneShotFailure is null
                ? Task.CompletedTask
                : Task.FromException(OneShotFailure);
        }

        public Task<bool?> ReadStartupAnimationAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TotalCalls++;
            return Task.FromResult(StartupAnimationEnabled);
        }

        public Task<FunctionKeyPrimaryMode?> ReadFunctionKeyPrimaryAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TotalCalls++;
            return Task.FromResult(FunctionKeyPrimary);
        }

        public Task<bool?> ReadGamingModeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TotalCalls++;
            return Task.FromResult(GamingModeEnabled);
        }

        public Task<LightingRendererOwnershipState>
            ReadLightingRendererOwnershipAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(LightingRendererOwnership);
        }

        public Task PulseReactiveKeyAsync(
            ReactiveKeyPulse pulse,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TotalCalls++;
            ReactivePulseCalls++;
            return LightingRendererOwnership is
                LightingRendererOwnershipState.Reactive or
                LightingRendererOwnershipState.Ripple
                    ? Task.CompletedTask
                    : Task.FromException(new IOException("Renderer is not active."));
        }

        public Task PushAmbientLightingFrameAsync(
            AmbientLightingFrame frame,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TotalCalls++;
            AmbientFrameCalls++;
            return LightingRendererOwnership ==
                LightingRendererOwnershipState.AmbientAwareness
                    ? Task.CompletedTask
                    : Task.FromException(new IOException("Renderer is not active."));
        }

        public Task SetPlaybackMuteIndicatorAsync(
            bool muted,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TotalCalls++;
            PlaybackMuteIndicatorStates.Add(muted);
            if (PlaybackMuteFailures.TryDequeue(out Exception? failure))
            {
                LightingRendererOwnership = LightingRendererOwnershipState.None;
                return Task.FromException(failure);
            }
            return Task.CompletedTask;
        }
    }
}
