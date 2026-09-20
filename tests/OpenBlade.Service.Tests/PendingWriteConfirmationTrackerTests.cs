using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed class PendingWriteConfirmationTrackerTests
{
    [Theory]
    [InlineData(DeviceSubsystem.Performance)]
    [InlineData(DeviceSubsystem.Fans)]
    public void ThermalEntry_BlocksBothThermalSubsystemsWithoutExpandingStatus(DeviceSubsystem subsystem)
    {
        var tracker = new PendingWriteConfirmationTracker();
        tracker.Set(subsystem, Pending());

        Assert.Equal([subsystem], tracker.SnapshotSubsystems());
        Assert.True(tracker.Blocks(DeviceSubsystem.Performance));
        Assert.True(tracker.Blocks(DeviceSubsystem.Fans));
        Assert.False(tracker.Blocks(DeviceSubsystem.Battery));
        Assert.False(tracker.Blocks(DeviceSubsystem.Lighting));
        Assert.Equal(
            new[] { DeviceSubsystem.Performance, DeviceSubsystem.Fans }.Order(),
            tracker.GetWriteExclusions().Order());

        tracker.Remove(subsystem);

        Assert.False(tracker.HasPending);
        Assert.False(tracker.Blocks(DeviceSubsystem.Performance));
        Assert.False(tracker.Blocks(DeviceSubsystem.Fans));
    }

    [Fact]
    public void ExternalThermalExclusion_CouplesWithoutCreatingPendingEntries()
    {
        var tracker = new PendingWriteConfirmationTracker();
        tracker.Set(DeviceSubsystem.Battery, Pending());

        Assert.Equal(
            new[] { DeviceSubsystem.Performance, DeviceSubsystem.Fans, DeviceSubsystem.Battery }.Order(),
            tracker.GetWriteExclusions([DeviceSubsystem.Fans]).Order());
        Assert.Equal([DeviceSubsystem.Battery], tracker.SnapshotSubsystems());
        Assert.False(tracker.Blocks(DeviceSubsystem.Performance));
    }

    [Fact]
    public void Clear_RetainsDetachedEntriesForRuntimeDriftAttribution()
    {
        var tracker = new PendingWriteConfirmationTracker();
        PendingWriteConfirmation original = Pending();
        tracker.Set(DeviceSubsystem.Battery, original);
        KeyValuePair<DeviceSubsystem, PendingWriteConfirmation>[] snapshot = tracker.Snapshot();

        KeyValuePair<DeviceSubsystem, PendingWriteConfirmation>[] abandoned = tracker.Clear();
        tracker.Set(DeviceSubsystem.Lighting, Pending());

        Assert.Equal(DeviceSubsystem.Battery, Assert.Single(abandoned).Key);
        Assert.Same(original, Assert.Single(snapshot).Value);
        Assert.Same(original, Assert.Single(abandoned).Value);
        Assert.Equal([DeviceSubsystem.Lighting], tracker.SnapshotSubsystems());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BatteryConfirmation_RequiresOriginalPowerAndIntentOwner(bool fullChargeOnly)
    {
        ProfileAutomationState owner = Owner();
        PendingWriteConfirmation pending = Pending() with
        {
            BatteryIntentCommit = new PendingBatteryIntentCommit(owner, fullChargeOnly),
        };

        Assert.Equal(PendingConfirmationResult.Match, pending.Match(
            DeviceSubsystem.Battery, Confirmed(), PowerSource.PluggedIn, owner, null));
        Assert.Equal(PendingConfirmationResult.Mismatch, pending.Match(
            DeviceSubsystem.Battery, Confirmed(), PowerSource.Battery, owner, null));
        Assert.Equal(PendingConfirmationResult.Mismatch, pending.Match(
            DeviceSubsystem.Battery, Confirmed(), PowerSource.PluggedIn, Owner(), null));
        Assert.Equal(PendingConfirmationResult.Unavailable, pending.Match(
            DeviceSubsystem.Battery, Confirmed() with { Battery = null }, PowerSource.PluggedIn, owner, null));
        Assert.Equal(PendingConfirmationResult.Mismatch, pending.Match(
            DeviceSubsystem.Battery, Confirmed() with { Battery = null }, PowerSource.Battery, owner, null));
    }

    private static ProfileAutomationState Owner() => new(
        ProfileDefaults.CreateSafeDefaults(), new BatteryAutomationPreferences(), PowerSource.PluggedIn);

    private static PendingWriteConfirmation Pending() => new(
        PowerSource.PluggedIn,
        new DesiredDeviceState(
            new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
            new ResolvedSetting<FanMode>(FanMode.Automatic, SettingValueSource.PowerProfile),
            new ResolvedSetting<LightingSettings>(LightingSettingsFactory.Off(), SettingValueSource.PowerProfile),
            new DesiredBatteryState(BatteryPolicy.Create(80), true, false)),
        Confirmed());

    private static ConfirmedDeviceState Confirmed() => new(
        PerformancePreset.Balanced, FanMode.Automatic,
        new ConfirmedBatteryState(true, 80, false), LightingSettingsFactory.Off());
}
