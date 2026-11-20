using OpenBlade.Contracts;
using OpenBlade.Protocol;
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
            Battery(80),
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
            Battery(80),
            DefaultLighting));

        tracker.Observe(State(
            PerformancePreset.Performance,
            FanMode.Maximum,
            Battery(75),
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
        tracker.Observe(State(fans: FanMode.Automatic, battery: Battery(80)));
        tracker.Observe(State(fans: FanMode.Maximum, battery: Battery(75)));

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
    public void SuccessfulSetterWithoutSemanticReadback_ClearsWarningWithoutBaseline()
    {
        var tracker = new ConfirmedStateDriftTracker();
        tracker.ConfirmIntentional(DeviceSubsystem.Lighting, matched: false);

        tracker.AcknowledgeIntentionalWithoutReadback(DeviceSubsystem.Lighting);
        tracker.Observe(State(lighting: LightingSettingsFactory.Static(
            new RgbColor(1, 2, 3),
            50)));

        Assert.Empty(tracker.ExternalChanges);
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
    public void BatteryLimitThenProtectionOff_LatchesBatteryChange()
    {
        var tracker = new ConfirmedStateDriftTracker();
        tracker.Observe(State(battery: Battery(80)));

        tracker.Observe(State(battery: ProtectionOff()));

        Assert.Equal([DeviceSubsystem.Battery], tracker.ExternalChanges);

        tracker.ConfirmIntentional(DeviceSubsystem.Battery, matched: true);

        Assert.Empty(tracker.ExternalChanges);
    }

    [Fact]
    public void InitialProtectionOffThenBatteryLimit_LatchesBatteryChange()
    {
        var tracker = new ConfirmedStateDriftTracker();
        tracker.Observe(State(battery: ProtectionOff()));

        tracker.Observe(State(battery: Battery(80)));

        Assert.Equal([DeviceSubsystem.Battery], tracker.ExternalChanges);
    }

    [Fact]
    public void ExternalChanges_ReturnsDefensiveImmutableSnapshots()
    {
        var tracker = new ConfirmedStateDriftTracker();
        tracker.ConfirmIntentional(DeviceSubsystem.Fans, matched: false);
        IReadOnlyList<DeviceSubsystem> first = tracker.ExternalChanges;
        IReadOnlyList<DeviceSubsystem> repeated = tracker.ExternalChanges;

        tracker.ConfirmIntentional(DeviceSubsystem.Battery, matched: false);
        IReadOnlyList<DeviceSubsystem> updated = tracker.ExternalChanges;

        Assert.Same(first, repeated);
        Assert.NotSame(first, updated);
        Assert.Equal([DeviceSubsystem.Fans], first);
        Assert.IsNotType<List<DeviceSubsystem>>(first);
        Assert.Equal(
            [DeviceSubsystem.Fans, DeviceSubsystem.Battery],
            updated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingBatteryRead_PreservesBaselineAndRealDrift(bool changed)
    {
        var tracker = new ConfirmedStateDriftTracker();
        tracker.Observe(State(battery: Battery(80)));

        tracker.Observe(State());
        Assert.Empty(tracker.ExternalChanges);
        tracker.Observe(State(battery: Battery(changed ? 75 : 80)));

        Assert.Equal(changed ? [DeviceSubsystem.Battery] : [], tracker.ExternalChanges);
    }

    [Fact]
    public void FirstBatteryAfterUnavailable_EstablishesBaseline()
    {
        var tracker = new ConfirmedStateDriftTracker();
        tracker.Observe(State());
        tracker.Observe(State(battery: Battery(80)));

        Assert.Empty(tracker.ExternalChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingTemporaryChargeRead_PreservesBaselineAndRealDrift(bool changed)
    {
        var tracker = new ConfirmedStateDriftTracker();
        tracker.Observe(State(battery: Battery(80)));

        tracker.Observe(State(battery: new ConfirmedBatteryState(true, 80, null)));
        Assert.Empty(tracker.ExternalChanges);
        tracker.Observe(State(battery: new ConfirmedBatteryState(true, 80, changed)));

        Assert.Equal(changed ? [DeviceSubsystem.Battery] : [], tracker.ExternalChanges);
    }

    [Fact]
    public void PartialBatteryRead_StillDetectsConfirmedLimitChange()
    {
        var tracker = new ConfirmedStateDriftTracker();
        tracker.Observe(State(battery: Battery(80)));
        tracker.Observe(State(battery: new ConfirmedBatteryState(true, 75, null)));

        Assert.Equal([DeviceSubsystem.Battery], tracker.ExternalChanges);
        tracker.Observe(State());
        tracker.Observe(State(battery: new ConfirmedBatteryState(true, 75, false)));
        Assert.Equal([DeviceSubsystem.Battery], tracker.ExternalChanges);
    }

    [Fact]
    public void FirstTemporaryChargeRead_EstablishesIndependentBaseline()
    {
        var tracker = new ConfirmedStateDriftTracker();
        tracker.Observe(State(battery: new ConfirmedBatteryState(true, 80, null)));
        tracker.Observe(State(battery: Battery(80)));

        Assert.Empty(tracker.ExternalChanges);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void MissingCustomPerformanceRead_PreservesBaselineAndRealDrift(
        bool missingPreset,
        bool changed)
    {
        var tracker = new ConfirmedStateDriftTracker();
        ConfirmedDeviceState baseline = State(performance: PerformancePreset.Custom) with
        {
            CustomPerformance = CustomPerformanceSettings.Default,
        };
        tracker.Observe(baseline);
        tracker.Observe(State(performance: missingPreset ? null : PerformancePreset.Custom));
        Assert.Empty(tracker.ExternalChanges);

        tracker.Observe(baseline with
        {
            CustomPerformance = changed
                ? new CustomPerformanceSettings(CustomPerformanceLevel.Low, CustomPerformanceLevel.Low)
                : baseline.CustomPerformance,
        });

        Assert.Equal(changed ? [DeviceSubsystem.Performance] : [], tracker.ExternalChanges);
    }

    [Fact]
    public void FirstCustomLevelsAfterUnavailable_EstablishBaseline()
    {
        var tracker = new ConfirmedStateDriftTracker();
        tracker.Observe(State(performance: PerformancePreset.Custom));
        tracker.Observe(State(performance: PerformancePreset.Custom) with
        {
            CustomPerformance = CustomPerformanceSettings.Default,
        });

        Assert.Empty(tracker.ExternalChanges);
    }

    [Fact]
    public void ConfirmedActiveToInactive_RetiresIntentInsteadOfLatching()
    {
        int retirements = 0;
        var tracker = new ConfirmedStateDriftTracker(() =>
        {
            retirements++;
            return true;
        });
        tracker.Observe(State(battery: new ConfirmedBatteryState(true, 80, true)));
        tracker.Observe(State(battery: new ConfirmedBatteryState(true, 80, null)));
        tracker.Observe(State(battery: new ConfirmedBatteryState(true, 80, false)));

        Assert.Equal(1, retirements);
        Assert.Empty(tracker.ExternalChanges);
    }

    [Fact]
    public void InactiveWithoutConfirmedActiveBaseline_NeverRetiresIntent()
    {
        int retirements = 0;
        var tracker = new ConfirmedStateDriftTracker(() =>
        {
            retirements++;
            return true;
        });
        tracker.Observe(State(battery: new ConfirmedBatteryState(true, 80, false)));
        tracker.Observe(State(battery: new ConfirmedBatteryState(true, 80, null)));
        tracker.Observe(State(battery: new ConfirmedBatteryState(true, 80, false)));

        Assert.Equal(0, retirements);
        Assert.Empty(tracker.ExternalChanges);
    }

    [Fact]
    public void LocalDimmingKeptWhileFullChargeEnds_RetiresIntentInsteadOfLatching()
    {
        // 0x0C is full charge on with local dimming on; 0x08 keeps local
        // dimming and ends full charge, which is a confirmed true-to-false.
        int retirements = 0;
        var tracker = new ConfirmedStateDriftTracker(() =>
        {
            retirements++;
            return true;
        });

        tracker.Observe(State(battery: PowerModeControlBattery(0x0C)));
        tracker.Observe(State(battery: PowerModeControlBattery(0x08)));

        Assert.Equal(1, retirements);
        Assert.Empty(tracker.ExternalChanges);
    }

    private static ConfirmedBatteryState PowerModeControlBattery(byte powerModeControl)
    {
        Assert.True(TemporaryFullChargeProtocol.TryDecodeQuery(
            [powerModeControl],
            out bool active));
        return new ConfirmedBatteryState(true, 80, active);
    }

    [Fact]
    public void ConfirmedActiveToInactive_WithoutIntentStillLatches()
    {
        var tracker = new ConfirmedStateDriftTracker(() => false);
        tracker.Observe(State(battery: new ConfirmedBatteryState(true, 80, true)));
        tracker.Observe(State(battery: new ConfirmedBatteryState(true, 80, false)));

        Assert.Equal([DeviceSubsystem.Battery], tracker.ExternalChanges);
    }

    private static ConfirmedDeviceState State(
        PerformancePreset? performance = null,
        FanMode? fans = null,
        ConfirmedBatteryState? battery = null,
        LightingSettings? lighting = null) =>
        new(performance, fans, battery, lighting);

    // A deferred choice applies once, only while the resolved intent still
    // holds it, no performance change was observed since it was made and the
    // fresh preset is still the observed one. Any observed change, an unknown
    // fresh preset or a differing fresh preset or Custom level keeps the mode;
    // an intent that replaced the choice no longer applies it; a fresh state
    // that already holds the choice, or a newer choice that is not deferred,
    // settles it silently. OpenBlade's own desired-versus-confirmed
    // comparisons never settle or invalidate it.
    [Fact]
    public void DeferredPerformanceChoice_AppliesOnceOnlyWithoutAnObservedChange()
    {
        const PerformancePreset Silent = PerformancePreset.Silent;
        var tracker = new ConfirmedStateDriftTracker();
        ConfirmedDeviceState balanced = State(PerformancePreset.Balanced, FanMode.Fixed, Battery(80), DefaultLighting);
        tracker.Observe(balanced);
        Assert.Equal(DeferredPerformanceDecision.None, tracker.DecideDeferredPerformance(balanced, Silent, null));

        tracker.RecordDeferredPerformanceChoice(Silent, null);
        Assert.Equal(DeferredPerformanceDecision.Apply, tracker.DecideDeferredPerformance(balanced, Silent, null));
        Assert.Equal(DeferredPerformanceDecision.None, tracker.DecideDeferredPerformance(balanced, Silent, null));
        tracker.RestoreUnsubmittedDeferredPerformance();
        Assert.Equal(DeferredPerformanceDecision.Apply, tracker.DecideDeferredPerformance(balanced, Silent, null));

        tracker.RecordDeferredPerformanceChoice(Silent, null);
        Assert.Equal(DeferredPerformanceDecision.Kept,
            tracker.DecideDeferredPerformance(balanced with { Performance = null }, Silent, null));
        Assert.Equal(DeferredPerformanceDecision.None, tracker.DecideDeferredPerformance(balanced, Silent, null));

        tracker.RecordDeferredPerformanceChoice(Silent, null);
        Assert.Equal(DeferredPerformanceDecision.Kept,
            tracker.DecideDeferredPerformance(balanced with { Performance = PerformancePreset.Performance }, Silent, null));

        // A fresh state that already holds the choice needs no write.
        tracker.RecordDeferredPerformanceChoice(Silent, null);
        Assert.Equal(DeferredPerformanceDecision.None,
            tracker.DecideDeferredPerformance(balanced with { Performance = Silent }, Silent, null));

        // The intent replaced the choice: it no longer applies, even unchanged.
        tracker.RecordDeferredPerformanceChoice(Silent, null);
        Assert.Equal(DeferredPerformanceDecision.NoLongerApplies,
            tracker.DecideDeferredPerformance(balanced, PerformancePreset.Balanced, null));
        Assert.Equal(DeferredPerformanceDecision.None, tracker.DecideDeferredPerformance(balanced, Silent, null));

        // OpenBlade's own comparisons neither settle nor invalidate it.
        tracker.RecordDeferredPerformanceChoice(Silent, null);
        tracker.ConfirmIntentional(DeviceSubsystem.Performance, matched: true);
        tracker.ConfirmIntentional(DeviceSubsystem.Performance, matched: false);
        Assert.True(tracker.HasDeferredPerformanceChoice);
        Assert.Equal(DeferredPerformanceDecision.Apply, tracker.DecideDeferredPerformance(balanced, Silent, null));

        // Away and back: an observed change drops the choice even though the
        // fresh preset matches again.
        tracker.RecordDeferredPerformanceChoice(Silent, null);
        tracker.Observe(balanced with { Performance = PerformancePreset.Performance });
        tracker.Observe(balanced);
        Assert.Equal(DeferredPerformanceDecision.Kept, tracker.DecideDeferredPerformance(balanced, Silent, null));

        // An external change before the choice does not drop it.
        Assert.Contains(DeviceSubsystem.Performance, tracker.ExternalChanges);
        tracker.RecordDeferredPerformanceChoice(Silent, null);
        Assert.Equal(DeferredPerformanceDecision.Apply, tracker.DecideDeferredPerformance(balanced, Silent, null));

        var low = new CustomPerformanceSettings(CustomPerformanceLevel.Low, CustomPerformanceLevel.Low);
        var high = new CustomPerformanceSettings(CustomPerformanceLevel.High, CustomPerformanceLevel.Low);
        var medium = new CustomPerformanceSettings(CustomPerformanceLevel.Medium, CustomPerformanceLevel.Low);
        ConfirmedDeviceState custom = balanced with
        {
            Performance = PerformancePreset.Custom,
            CustomPerformance = low,
        };
        tracker.Observe(custom);
        tracker.RecordDeferredPerformanceChoice(PerformancePreset.Custom, high);
        Assert.Equal(DeferredPerformanceDecision.Apply,
            tracker.DecideDeferredPerformance(custom, PerformancePreset.Custom, high));
        tracker.RecordDeferredPerformanceChoice(PerformancePreset.Custom, high);
        Assert.Equal(DeferredPerformanceDecision.NoLongerApplies,
            tracker.DecideDeferredPerformance(custom, PerformancePreset.Custom, medium));
        tracker.RecordDeferredPerformanceChoice(PerformancePreset.Custom, high);
        Assert.Equal(DeferredPerformanceDecision.None,
            tracker.DecideDeferredPerformance(custom with { CustomPerformance = high }, PerformancePreset.Custom, high));
        tracker.RecordDeferredPerformanceChoice(PerformancePreset.Custom, high);
        Assert.Equal(DeferredPerformanceDecision.Kept,
            tracker.DecideDeferredPerformance(custom with { CustomPerformance = medium }, PerformancePreset.Custom, high));
        tracker.RecordDeferredPerformanceChoice(PerformancePreset.Custom, high);
        tracker.Observe(custom with { CustomPerformance = medium });
        Assert.Equal(DeferredPerformanceDecision.Kept,
            tracker.DecideDeferredPerformance(custom with { CustomPerformance = medium }, PerformancePreset.Custom, high));

        tracker.RecordDeferredPerformanceChoice(Silent, null);
        tracker.ClearDeferredPerformanceChoice();
        Assert.False(tracker.HasDeferredPerformanceChoice);
        Assert.Equal(DeferredPerformanceDecision.None, tracker.DecideDeferredPerformance(balanced, Silent, null));
    }

    private static ConfirmedBatteryState Battery(int limit) => new(true, limit, false);

    private static ConfirmedBatteryState ProtectionOff() => new(false, null, false);
}
