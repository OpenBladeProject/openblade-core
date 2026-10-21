using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ApplicationProfileAutomationTests
{
    [Fact]
    public void ApplicationIntentUsesCurrentPowerSourceAndManualOverridesRemainHigherPriority()
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        var definition = new ApplicationProfileDefinition(
            Guid.NewGuid(),
            "Game",
            @"C:\Games\game.exe",
            ApplicationActivationMode.Foreground,
            new ApplicationProfileOverrideSet
            {
                PluggedIn = new ApplicationPowerProfileOverride
                {
                    Performance = PerformancePreset.Performance,
                },
                Battery = new ApplicationPowerProfileOverride
                {
                    Performance = PerformancePreset.Balanced,
                },
            });
        var state = new ProfileAutomationState(
            defaults with { ApplicationProfiles = [definition] },
            new BatteryAutomationPreferences(),
            PowerSource.PluggedIn);

        Assert.True(state.SetApplicationContext(@"c:\games\GAME.exe"));
        DesiredDeviceState pluggedIn = Resolve(state);
        Assert.Equal(PerformancePreset.Performance, pluggedIn.Performance.Value);
        Assert.Equal(SettingValueSource.ApplicationProfile, pluggedIn.Performance.Source);

        state.SetPerformanceOverride(new ManualOverride<PerformancePreset>(
            PerformancePreset.Balanced,
            OverrideLifetime.Persistent));
        Assert.Equal(SettingValueSource.PersistentOverride, Resolve(state).Performance.Source);

        state.SetPerformanceOverride(null);
        state.ChangePowerSource(PowerSource.Battery);
        Assert.Equal(PerformancePreset.Balanced, Resolve(state).Performance.Value);
        Assert.Equal(SettingValueSource.ApplicationProfile, Resolve(state).Performance.Source);

        Assert.True(state.SetApplicationContext(null));
        Assert.Equal(defaults.Battery.Performance, Resolve(state).Performance.Value);
        Assert.Equal(SettingValueSource.PowerProfile, Resolve(state).Performance.Source);
    }

    [Fact]
    public void EffectiveLaptopBehaviorIncludesApplicationGamingOverride()
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        var definition = new ApplicationProfileDefinition(
            Guid.NewGuid(),
            "Game",
            @"C:\Games\game.exe",
            ApplicationActivationMode.Foreground,
            new ApplicationProfileOverrideSet
            {
                PluggedIn = new ApplicationPowerProfileOverride
                {
                    GamingModeEnabled = true,
                    GamingModeKeyPolicy = new GamingModeKeyPolicy(true, true),
                },
            });
        var state = new ProfileAutomationState(
            defaults with { ApplicationProfiles = [definition] },
            new BatteryAutomationPreferences(),
            PowerSource.PluggedIn);

        state.SetApplicationContext(definition.ExecutablePath);

        Assert.True(state.EffectiveLaptopBehavior.GamingModeEnabled);
        Assert.Equal(new GamingModeKeyPolicy(true, true),
            state.EffectiveLaptopBehavior.GamingModeKeyPolicy);
    }

    [Fact]
    public void RemovingActiveApplicationProfileImmediatelyRestoresCurrentPowerSourceBase()
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        var definition = new ApplicationProfileDefinition(
            Guid.NewGuid(),
            "Game",
            @"C:\Games\game.exe",
            ApplicationActivationMode.Foreground,
            new ApplicationProfileOverrideSet
            {
                Battery = new ApplicationPowerProfileOverride
                {
                    Performance = PerformancePreset.Performance,
                    GamingModeEnabled = true,
                },
            });
        var state = new ProfileAutomationState(
            defaults with { ApplicationProfiles = [definition] },
            new BatteryAutomationPreferences(),
            PowerSource.Battery);
        state.SetApplicationContext(definition.ExecutablePath);

        state.UpdateProfiles(defaults);

        Assert.Null(state.ActiveApplicationProfileId);
        Assert.Equal(defaults.Battery.Performance, Resolve(state).Performance.Value);
        Assert.Equal(SettingValueSource.PowerProfile, Resolve(state).Performance.Source);
        Assert.NotEqual(true, state.EffectiveLaptopBehavior.GamingModeEnabled);
    }

    [Fact]
    public void ChangingActiveApplicationExecutableImmediatelyRestoresCurrentPowerSourceBase()
    {
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        var definition = new ApplicationProfileDefinition(
            Guid.NewGuid(),
            "Game",
            @"C:\Games\game.exe",
            ApplicationActivationMode.Foreground,
            new ApplicationProfileOverrideSet
            {
                PluggedIn = new ApplicationPowerProfileOverride
                {
                    Performance = PerformancePreset.Performance,
                },
            });
        var state = new ProfileAutomationState(
            defaults with { ApplicationProfiles = [definition] },
            new BatteryAutomationPreferences(),
            PowerSource.PluggedIn);
        state.SetApplicationContext(definition.ExecutablePath);

        state.UpdateProfiles(defaults with
        {
            ApplicationProfiles =
            [
                definition with { ExecutablePath = @"C:\Games\replacement.exe" },
            ],
        });

        Assert.Null(state.ActiveApplicationProfileId);
        Assert.Equal(defaults.PluggedIn.Performance, Resolve(state).Performance.Value);
        Assert.Equal(SettingValueSource.PowerProfile, Resolve(state).Performance.Source);
    }

    private static DesiredDeviceState Resolve(ProfileAutomationState state) => state.Resolve(
        new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            null,
            LightingSettingsFactory.Static(new RgbColor(0, 0, 0), 50)));
}
