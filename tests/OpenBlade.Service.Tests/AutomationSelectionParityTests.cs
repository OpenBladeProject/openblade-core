using System.Reflection;
using OpenBlade.Contracts;
using OpenBlade.Service;
using OpenBlade.Testing;

namespace OpenBlade.Service.Tests;

public sealed class AutomationSelectionParityTests
{
    [Fact]
    public void ActualHostPerformanceHelperMatchesFrozenBaselineForAllPrioritiesAndRawValues()
    {
        var resolve = typeof(ProfileAutomationState).GetMethod("ResolvePerformance",
            BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<PerformancePreset, PerformanceOverride?, PerformanceOverride?,
                SettingValueSource, ResolvedSetting<PerformancePreset>>>();
        foreach (int scalar in new[] { 0, -1, 999 })
        {
            foreach (SettingValueSource source in new[] { SettingValueSource.PowerProfile, SettingValueSource.ApplicationProfile })
            {
                for (int flags = 0; flags < 4; flags++)
                {
                    PerformanceOverride? temporary = (flags & 1) != 0
                        ? new((PerformancePreset)scalar, OverrideLifetime.UntilPowerSourceChange) : null;
                    PerformanceOverride? persistent = (flags & 2) != 0
                        ? new((PerformancePreset)0, OverrideLifetime.Persistent) : null;
                    Assert.Equal(ManagedAutomationPolicyBaseline.ResolvePerformance(
                        (PerformancePreset)999, temporary, persistent, source),
                        resolve((PerformancePreset)999, temporary, persistent, source));
                }
            }
        }
    }

    [Fact]
    public void ActualHostLightingHelperPreservesSourceAndOriginalObjectForEveryPresenceCombination()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        foreach (PowerSource source in new[] { PowerSource.PluggedIn, PowerSource.Battery, PowerSource.UsbC, PowerSource.ExternalUnknown })
        {
            var state = new ProfileAutomationState(profiles, new BatteryAutomationPreferences(), source);
            var resolve = typeof(ProfileAutomationState).GetMethod("ResolveLighting",
                BindingFlags.NonPublic | BindingFlags.Instance)!
                .CreateDelegate<Func<PowerProfile, ConfirmedDeviceState, bool, ResolvedSetting<LightingSettings>>>(state);
            foreach (bool nullOverrideContents in new[] { false, true })
            {
                for (int flags = 0; flags < 32; flags++)
                {
                    LightingSettings basis = LightingSettingsFactory.Static(new(17, 0, 255), 0);
                    LightingSettings? profileLighting = (flags & 1) != 0 ? basis with { } : null;
                    LightingSettings? observed = (flags & 2) != 0 ? basis with { } : null;
                    var temporary = (flags & 4) != 0 ? new ManualOverride<LightingSettings>(
                        nullOverrideContents ? null! : basis with { }, OverrideLifetime.UntilPowerSourceChange) : null;
                    var persistent = (flags & 8) != 0 ? new ManualOverride<LightingSettings>(
                        nullOverrideContents ? null! : basis with { }, OverrideLifetime.Persistent) : null;
                    SetField(state, "temporaryLighting", temporary);
                    SetField(state, "persistentLighting", persistent);
                    PowerProfile profile = profiles.PluggedIn with { Lighting = profileLighting! };
                    var confirmed = new ConfirmedDeviceState(null, null, null, observed);
                    bool application = (flags & 16) != 0;
                    ResolvedSetting<LightingSettings> expected = ManagedAutomationPolicyBaseline.ResolveLighting(
                        profile, confirmed, application, source, temporary, persistent);
                    ResolvedSetting<LightingSettings> actual = resolve(profile, confirmed, application);
                    Assert.Equal(expected.Source, actual.Source);
                    Assert.Same(expected.Value, actual.Value);
                }
            }
        }
    }

    [Fact]
    public void CompleteResolutionRetainsUnprojectedObjectsAndSourceReversion()
    {
        var custom = CustomPerformanceSettings.Default with { };
        var curve = new ManualFanCurve([new(32, 2100), new(84, 5300)], [new(34, 2200), new(88, 5200)], false);
        var fan = new FanControlSettings(FanMode.ManualCurve, 3700, curve);
        ProfileSet defaults = ProfileDefaults.CreateSafeDefaults();
        ProfileSet profiles = defaults with
        {
            PluggedIn = defaults.PluggedIn with
            {
                Performance = PerformancePreset.Custom,
                CustomPerformance = custom,
                Fans = FanMode.ManualCurve,
                FanControl = fan,
            },
        };
        var state = new ProfileAutomationState(profiles, new BatteryAutomationPreferences(), PowerSource.PluggedIn);
        var confirmed = new ConfirmedDeviceState(null, null, null, null);
        DesiredDeviceState original = state.Resolve(confirmed);
        Assert.Same(custom, original.CustomPerformance);
        Assert.Same(fan, original.FanControl);
        Assert.Same(curve, original.FanControl!.ManualCurve);
        Assert.True(state.ChangePowerSource(PowerSource.ExternalUnknown));
        LightingSettings observed = LightingSettingsFactory.Static(new(1, 2, 3), 0);
        DesiredDeviceState unknown = state.Resolve(confirmed with { Lighting = observed });
        Assert.Same(observed, unknown.Lighting.Value);
        Assert.Equal(SettingValueSource.ConfirmedFirmwareState, unknown.Lighting.Source);
        Assert.True(state.ChangePowerSource(PowerSource.PluggedIn));
        DesiredDeviceState reverted = state.Resolve(confirmed);
        Assert.Same(custom, reverted.CustomPerformance);
        Assert.Same(fan, reverted.FanControl);
        Assert.Same(profiles.PluggedIn.Lighting, reverted.Lighting.Value);
    }

    private static void SetField<T>(ProfileAutomationState state, string name, T value) =>
        typeof(ProfileAutomationState).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(state, value);
}
