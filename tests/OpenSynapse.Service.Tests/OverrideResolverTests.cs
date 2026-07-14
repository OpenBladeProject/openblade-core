using OpenSynapse.Contracts;
using OpenSynapse.Service;

namespace OpenSynapse.Service.Tests;

public sealed class OverrideResolverTests
{
    [Fact]
    public void Resolve_PersistentOverrideWins()
    {
        ResolvedSetting<PerformancePreset> resolved = OverrideResolver.Resolve(
            profileValue: PerformancePreset.Silent,
            confirmedValue: PerformancePreset.Balanced,
            temporaryOverride: new ManualOverride<PerformancePreset>(
                PerformancePreset.Balanced,
                OverrideLifetime.UntilPowerSourceChange),
            persistentOverride: new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance,
                OverrideLifetime.Persistent));

        Assert.Equal(PerformancePreset.Performance, resolved.Value);
        Assert.Equal(SettingValueSource.PersistentOverride, resolved.Source);
    }

    [Fact]
    public void Resolve_TemporaryOverrideWinsOverProfile()
    {
        ResolvedSetting<FanMode> resolved = OverrideResolver.Resolve(
            profileValue: FanMode.Automatic,
            confirmedValue: FanMode.Automatic,
            temporaryOverride: new ManualOverride<FanMode>(
                FanMode.Maximum,
                OverrideLifetime.UntilPowerSourceChange),
            persistentOverride: null);

        Assert.Equal(FanMode.Maximum, resolved.Value);
        Assert.Equal(SettingValueSource.TemporaryOverride, resolved.Source);
    }

    [Fact]
    public void Resolve_ProfileWinsOverConfirmedFirmwareState()
    {
        ResolvedSetting<PerformancePreset> resolved = OverrideResolver.Resolve(
            profileValue: PerformancePreset.Silent,
            confirmedValue: PerformancePreset.Balanced,
            temporaryOverride: null,
            persistentOverride: null);

        Assert.Equal(PerformancePreset.Silent, resolved.Value);
        Assert.Equal(SettingValueSource.PowerProfile, resolved.Source);
    }
}
