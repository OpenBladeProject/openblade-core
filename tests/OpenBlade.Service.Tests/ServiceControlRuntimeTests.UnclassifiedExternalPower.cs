using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    private async Task<TestContext> CreateUnclassifiedExternalContextAsync(bool ordinaryThermal = true,
        ApplicationProfileDefinition? application = null,
        IUserConfigurationRepository? userConfigurationRepository = null)
    {
        TestContext context = CreateContext(true, initialPowerSource: PowerSource.ExternalUnknown, initialize: false,
            userConfigurationRepository: userConfigurationRepository,
            profileValidator: userConfigurationRepository is null ? null
                : ProfileSetValidator.CreateForLightingCapabilities(DeviceCapabilities.SafeTargetDefaults() with
                {
                    LightingEffects = new HashSet<LightingEffect> { LightingEffect.Off, LightingEffect.Static },
                }));
        BatteryConfiguration configuration = await context.Store.LoadAsync(CancellationToken.None);
        if (application is not null)
        {
            await context.Store.SaveProfilesAsync(configuration.Profiles with { ApplicationProfiles = [application] }, CancellationToken.None);
            configuration = await context.Store.LoadAsync(CancellationToken.None);
        }
        context.Runtime.Initialize(configuration, PowerSource.ExternalUnknown, PersistentOverrideState.Empty);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            PerformancePresets = new HashSet<PerformancePreset>
                { PerformancePreset.Balanced, PerformancePreset.Silent, PerformancePreset.Custom },
            FanModes = new HashSet<FanMode> { FanMode.Automatic, FanMode.Fixed, FanMode.ManualCurve },
            LightingEffects = new HashSet<LightingEffect> { LightingEffect.Off, LightingEffect.Static },
            ChargeLimits = new HashSet<int> { 80 },
            SupportsBatteryProtectionOff = true,
            SupportsTemporaryFullCharge = true,
            SupportsOrdinaryThermalControlOnUnclassifiedExternalPower = ordinaryThermal,
            SupportsDirectManualFanTransitions = true,
            ReportsPerformanceWithManualFans = true,
        };
        context.Device.AdapterPowerState = AdapterPowerState.Unknown;
        context.Device.ConfirmedState = new(PerformancePreset.Balanced, FanMode.Automatic,
            new ConfirmedBatteryState(false, null, false), LightingSettingsFactory.Off());
        await context.Runtime.ObserveAsync(CancellationToken.None);
        Assert.True(context.Runtime.GetStatus().ExternalPowerPresent);
        Assert.Equal(PowerSource.ExternalUnknown, context.Runtime.GetStatus().ActivePowerSource);
        return context;
    }

    [Fact]
    public async Task UnclassifiedExternal_ExplicitLightingUsesOriginalTemporaryProvenanceWithoutProfileMutation()
    {
        TestContext context = await CreateUnclassifiedExternalContextAsync(ordinaryThermal: false);
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        LightingSettings requested = LightingSettingsFactory.Static(new RgbColor(12, 34, 56), 60);

        ControlOperationResult result = await context.Runtime.SetLightingOverrideAsync(
            new(requested, OverrideLifetime.UntilPowerSourceChange), CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Applied, result.Status);
        ServiceStatusSnapshot status = context.Runtime.GetStatus();
        Assert.Equal(requested, context.Device.ConfirmedState.Lighting);
        Assert.Equal(requested, status.DesiredState.Lighting.Value);
        Assert.Equal(SettingValueSource.TemporaryOverride, status.DesiredState.Lighting.Source);
        Assert.Equal(PowerSource.ExternalUnknown, status.ActivePowerSource);
        Assert.True(status.ExternalPowerPresent);
        Assert.Equal(profiles, status.Profiles);
        Assert.Equal(0, context.Device.PerformanceSetCalls);
        Assert.Equal(0, context.Device.FanSetCalls);
    }

    [Theory]
    [InlineData(false, ControlOperationStatus.Blocked)]
    [InlineData(true, ControlOperationStatus.Applied)]
    public async Task UnclassifiedExternal_OrdinaryPerformanceRequiresReviewedCapability(
        bool admitted, ControlOperationStatus expected)
    {
        TestContext context = await CreateUnclassifiedExternalContextAsync(admitted);
        ControlOperationResult result = await context.Runtime.SetPerformanceOverrideAsync(
            new(PerformancePreset.Silent, OverrideLifetime.UntilPowerSourceChange), CancellationToken.None);
        Assert.Equal(expected, result.Status);
        Assert.Equal(admitted ? 1 : 0, context.Device.PerformanceSetCalls);
        Assert.Equal(PowerSource.ExternalUnknown, context.Runtime.GetStatus().ActivePowerSource);
        if (!admitted)
        {
            Assert.Null(context.Runtime.GetStatus().PerformanceOverride);
        }
    }

    [Fact]
    public async Task UnclassifiedExternal_ActiveHyperBoostRejectsOrdinaryRequestBeforeIntentOrSetter()
    {
        TestContext context = await CreateUnclassifiedExternalContextAsync();
        context.Device.ConfirmedState = context.Device.ConfirmedState with { HyperBoostActive = true };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        int writes = context.Device.TotalSetterCalls;
        ControlOperationResult result = await context.Runtime.SetPerformanceOverrideAsync(
            new(PerformancePreset.Silent, OverrideLifetime.Persistent), CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(writes, context.Device.TotalSetterCalls);
        Assert.Null(context.Runtime.GetStatus().PerformanceOverride);
        Assert.True(context.Device.ConfirmedState.HyperBoostActive);
    }

    [Fact]
    public async Task UnclassifiedExternal_ClearLightingDoesNotApplyAnUnclassifiedProfileSlot()
    {
        TestContext context = await CreateUnclassifiedExternalContextAsync();
        LightingSettings requested = LightingSettingsFactory.Static(new RgbColor(32, 64, 128), 70);
        Assert.Equal(ControlOperationStatus.Applied, (await context.Runtime.SetLightingOverrideAsync(
            new(requested, OverrideLifetime.Persistent), CancellationToken.None)).Status);
        int setters = context.Device.TotalSetterCalls;
        ProfileSet profiles = context.Runtime.GetStatus().Profiles;
        ControlOperationResult result = await context.Runtime.SetLightingOverrideAsync(null, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Stored, result.Status);
        Assert.Equal(setters, context.Device.TotalSetterCalls);
        Assert.Null(context.Runtime.GetStatus().LightingOverride);
        Assert.Equal(profiles, context.Runtime.GetStatus().Profiles);
    }

    [Fact]
    public async Task UnclassifiedExternal_ClassificationRefinementPreservesExplicitLightingUntilActualBatteryBoundary()
    {
        LightingSettings conflicting = LightingSettingsFactory.Off();
        var application = new ApplicationProfileDefinition(Guid.NewGuid(), "Active app", @"C:\Games\game.exe",
            ApplicationActivationMode.Foreground, new ApplicationProfileOverrideSet
            {
                PluggedIn = new ApplicationPowerProfileOverride
                {
                    Performance = PerformancePreset.Silent,
                    Lighting = conflicting,
                },
            });
        TestContext context = await CreateUnclassifiedExternalContextAsync(application: application);
        Assert.Equal(ControlOperationStatus.Stored, (await context.Runtime.ReportApplicationContextAsync(
            new(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), Guid.NewGuid(), 1, application.ExecutablePath),
            () => true, CancellationToken.None)).Status);
        Assert.Equal(ControlOperationStatus.Applied, (await context.Runtime.SetPerformanceOverrideAsync(
            new(PerformancePreset.Balanced, OverrideLifetime.UntilPowerSourceChange), CancellationToken.None)).Status);
        LightingSettings requested = LightingSettingsFactory.Static(new RgbColor(90, 40, 20), 70);
        Assert.Equal(ControlOperationStatus.Applied, (await context.Runtime.SetLightingOverrideAsync(
            new(requested, OverrideLifetime.UntilPowerSourceChange), CancellationToken.None)).Status);
        var appliedLighting = new List<LightingSettings>();
        context.Device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Lighting)
            {
                appliedLighting.Add(context.Device.ConfirmedState.Lighting!);
            }
        };
        context.Device.PerformanceWrites.Clear();
        context.Device.AdapterPowerState = AdapterPowerState.FullPower280W;
        await context.Runtime.RefreshPowerSourceClassificationAsync(CancellationToken.None);
        Assert.Equal(PowerSource.PluggedIn, context.Runtime.GetStatus().ActivePowerSource);
        Assert.Equal(requested, context.Runtime.GetStatus().LightingOverride?.Value);
        Assert.Equal(SettingValueSource.TemporaryOverride, context.Runtime.GetStatus().DesiredState.Lighting.Source);
        Assert.Equal(application.Id, context.Runtime.GetStatus().ActiveApplicationProfileId);
        Assert.Equal(PerformancePreset.Balanced, context.Runtime.GetStatus().DesiredState.Performance.Value);
        Assert.Equal(requested, context.Device.ConfirmedState.Lighting);
        Assert.DoesNotContain(conflicting, appliedLighting);
        Assert.DoesNotContain(PerformancePreset.Silent, context.Device.PerformanceWrites);
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        Assert.False(context.Runtime.GetStatus().ExternalPowerPresent);
        Assert.Null(context.Runtime.GetStatus().LightingOverride);
    }

    [Fact]
    public async Task UnclassifiedExternal_ChargeLimitDoesNotManufactureProtectionEnabled()
    {
        TestContext context = await CreateUnclassifiedExternalContextAsync();
        ControlOperationResult result = await context.Runtime.SetBatteryPolicyAsync(
            BatteryPolicy.Create(80), CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(0, context.Device.BatterySetCalls);
        Assert.False(context.Device.ConfirmedState.Battery!.ProtectionEnabled);
        Assert.Equal(PowerSource.ExternalUnknown, context.Runtime.GetStatus().ActivePowerSource);
        ControlOperationResult fullCharge = await context.Runtime.SetFullChargeOverrideAsync(true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Failed, fullCharge.Status);
        Assert.False(context.Device.ConfirmedState.Battery.TemporaryFullCharge);
    }

    [Fact]
    public async Task UnclassifiedExternal_UnknownBatteryOverrideReadbackDoesNotGrantDirectBatteryAuthority()
    {
        TestContext context = await CreateUnclassifiedExternalContextAsync();
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Battery = new ConfirmedBatteryState(true, 80, null),
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        int setters = context.Device.TotalSetterCalls;
        ControlOperationResult result = await context.Runtime.SetBatteryProtectionAsync(false, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(setters, context.Device.TotalSetterCalls);
    }

    [Fact]
    public async Task UnclassifiedExternal_KnownFullToLimitedThroughUnknownExpiresTemporaryOverride()
    {
        TestContext context = await CreateUnclassifiedExternalContextAsync();
        context.Device.AdapterPowerState = AdapterPowerState.FullPower280W;
        await context.Runtime.RefreshPowerSourceClassificationAsync(CancellationToken.None);
        context.Device.AdapterPowerState = AdapterPowerState.Unknown;
        await context.Runtime.RefreshPowerSourceClassificationAsync(CancellationToken.None);
        LightingSettings requested = LightingSettingsFactory.Static(new RgbColor(90, 20, 80), 70);
        Assert.Equal(ControlOperationStatus.Applied, (await context.Runtime.SetLightingOverrideAsync(
            new(requested, OverrideLifetime.UntilPowerSourceChange), CancellationToken.None)).Status);
        context.Device.AdapterPowerState = AdapterPowerState.LimitedExternal;
        await context.Runtime.RefreshPowerSourceClassificationAsync(CancellationToken.None);
        Assert.Equal(PowerSource.UsbC, context.Runtime.GetStatus().ActivePowerSource);
        Assert.Null(context.Runtime.GetStatus().LightingOverride);
    }

    [Fact]
    public async Task UnclassifiedExternal_ExplicitOwnedCurveMaintainsWithExpectedPresetAndTruthfulSource()
    {
        TestContext context = await CreateUnclassifiedExternalContextAsync();
        context.Device.FanTargetsAvailable = true;
        var settings = new FanControlSettings(FanMode.ManualCurve, 3700, ManualFanCurve.Default);
        Assert.Equal(ControlOperationStatus.Applied, (await context.Runtime.SetFanControlOverrideAsync(
            new(FanMode.ManualCurve, OverrideLifetime.UntilPowerSourceChange), settings,
            CancellationToken.None)).Status);
        int before = context.Device.TypedCurveTargetCalls;
        await context.Runtime.MaintainFanCurveAsync(CancellationToken.None);
        Assert.True(context.Device.TypedCurveTargetCalls > before);
        Assert.Equal(PerformancePreset.Balanced, context.Device.LastCurveExpectedPerformance);
        Assert.Equal(PowerSource.ExternalUnknown, context.Runtime.GetStatus().ActivePowerSource);
        Assert.Equal(FanMode.ManualCurve, context.Runtime.GetStatus().FanOverride?.Value);
    }


    [Fact]
    public async Task UnclassifiedExternal_PriorUsersMarkerCannotPreserveAnotherOwnersDormantLighting()
    {
        var firstSid = new System.Security.Principal.SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var secondSid = new System.Security.Principal.SecurityIdentifier("S-1-5-21-1-2-3-1002");
        LightingSettings dormant = LightingSettingsFactory.Static(new RgbColor(0, 240, 0), 40);
        var first = new BatteryConfiguration(ProfileDefaults.CreateSafeDefaults(),
            new BatteryAutomationPreferences(), Recovered: false);
        var second = first with { Preferences = new BatteryAutomationPreferences { LastKnownLighting = dormant } };
        var repository = new MappedUserConfigurationRepository(new Dictionary<string, BatteryConfiguration>
        {
            [firstSid.Value] = first,
            [secondSid.Value] = second,
        }, directory);
        TestContext context = await CreateUnclassifiedExternalContextAsync(userConfigurationRepository: repository);
        await context.Runtime.ActivateUserAsync(firstSid, CancellationToken.None);
        LightingSettings explicitFirst = LightingSettingsFactory.Static(new RgbColor(200, 0, 0), 60);
        Assert.Equal(ControlOperationStatus.Applied, (await context.Runtime.SetLightingOverrideAsync(
            new(explicitFirst, OverrideLifetime.UntilPowerSourceChange), CancellationToken.None)).Status);
        await context.Runtime.ActivateUserAsync(secondSid, CancellationToken.None);
        Assert.Equal(dormant, context.Runtime.GetStatus().LightingOverride?.Value);
        var submitted = new List<LightingSettings>();
        context.Device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Lighting)
            {
                submitted.Add(context.Device.ConfirmedState.Lighting!);
            }
        };
        context.Device.AdapterPowerState = AdapterPowerState.FullPower280W;
        await context.Runtime.RefreshPowerSourceClassificationAsync(CancellationToken.None);
        Assert.Null(context.Runtime.GetStatus().LightingOverride);
        Assert.DoesNotContain(dormant, submitted);
        Assert.Equal(second.Profiles, context.Runtime.GetStatus().Profiles);
    }

}
