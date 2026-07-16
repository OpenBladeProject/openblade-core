using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ConfirmedStateDriftTrackerTests
{
    private static readonly LightingSettings DefaultLighting =
        ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting;

    [Fact]
    public void FirstNonNullValues_EstablishBaselinesWithoutExternalChanges()
    {
        var tracker = new ConfirmedStateDriftTracker();

        tracker.Observe(State(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            BatteryPolicy.Create(80),
            DefaultLighting));

        Assert.Empty(tracker.ExternalChanges);
    }

    [Fact]
    public void LaterComparableChanges_LatchOnlyChangedSubsystemsInSortedOrder()
    {
        var tracker = new ConfirmedStateDriftTracker();
        tracker.Observe(State(
            PerformancePreset.Balanced,
            FanMode.Automatic,
            BatteryPolicy.Create(80),
            DefaultLighting));

        tracker.Observe(State(
            PerformancePreset.Performance,
            FanMode.Maximum,
            BatteryPolicy.Create(75),
            DefaultLighting with { Brightness = 20 }));

        Assert.Equal(
            [
                DeviceSubsystem.Performance,
                DeviceSubsystem.Fans,
                DeviceSubsystem.Battery,
                DeviceSubsystem.Lighting,
            ],
            tracker.ExternalChanges);
    }

    [Fact]
    public void EqualRepeat_KeepsExistingLatch()
    {
        var tracker = new ConfirmedStateDriftTracker();
        tracker.Observe(State(fans: FanMode.Automatic));
        tracker.Observe(State(fans: FanMode.Maximum));

        tracker.Observe(State(fans: FanMode.Maximum));

        Assert.Equal([DeviceSubsystem.Fans], tracker.ExternalChanges);
    }

    [Fact]
    public void MatchingConfirmation_ClearsOnlyRequestedSubsystem()
    {
        var tracker = new ConfirmedStateDriftTracker();
        tracker.Observe(State(fans: FanMode.Automatic, battery: BatteryPolicy.Create(80)));
        tracker.Observe(State(fans: FanMode.Maximum, battery: BatteryPolicy.Create(75)));

        tracker.ConfirmIntentional(DeviceSubsystem.Fans, matched: true);

        Assert.Equal([DeviceSubsystem.Battery], tracker.ExternalChanges);
    }

    [Fact]
    public void FailedConfirmation_LatchesSubsystemWithoutBaseline()
    {
        var tracker = new ConfirmedStateDriftTracker();

        tracker.ConfirmIntentional(DeviceSubsystem.Lighting, matched: false);

        Assert.Equal([DeviceSubsystem.Lighting], tracker.ExternalChanges);
    }

    [Fact]
    public void NullThenValue_EstablishesBaselineWithoutDrift()
    {
        var tracker = new ConfirmedStateDriftTracker();
        tracker.Observe(State());

        tracker.Observe(State(performance: PerformancePreset.Balanced));

        Assert.Empty(tracker.ExternalChanges);
    }

    [Fact]
    public void ValueThenNull_PreservesLastComparableBaseline()
    {
        var tracker = new ConfirmedStateDriftTracker();
        tracker.Observe(State(performance: PerformancePreset.Balanced));
        tracker.Observe(State());

        tracker.Observe(State(performance: PerformancePreset.Performance));

        Assert.Equal([DeviceSubsystem.Performance], tracker.ExternalChanges);
    }

    [Fact]
    public void ExternalChanges_ReturnsDefensiveImmutableSnapshots()
    {
        var tracker = new ConfirmedStateDriftTracker();
        tracker.ConfirmIntentional(DeviceSubsystem.Fans, matched: false);
        IReadOnlyList<DeviceSubsystem> first = tracker.ExternalChanges;

        tracker.ConfirmIntentional(DeviceSubsystem.Battery, matched: false);

        Assert.Equal([DeviceSubsystem.Fans], first);
        Assert.IsNotType<List<DeviceSubsystem>>(first);
        Assert.Equal(
            [DeviceSubsystem.Fans, DeviceSubsystem.Battery],
            tracker.ExternalChanges);
    }

    private static ConfirmedDeviceState State(
        PerformancePreset? performance = null,
        FanMode? fans = null,
        BatteryPolicy? battery = null,
        LightingSettings? lighting = null) =>
        new(performance, fans, battery, lighting);
}
