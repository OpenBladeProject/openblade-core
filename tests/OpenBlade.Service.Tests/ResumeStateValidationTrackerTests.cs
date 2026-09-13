using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed class ResumeStateValidationTrackerTests
{
    [Fact]
    public void ObserveBeforeCompletion_UsesLatestSampleAndConsumesOnce()
    {
        var tracker = new ResumeStateValidationTracker(TimeProvider.System);
        ConfirmedDeviceState baseline = Confirmed();
        tracker.Capture(PowerSource.PluggedIn, baseline);
        tracker.Begin(gpuClockOffsetConfirmationInvalidated: false);

        Assert.Null(tracker.Observe(PowerSource.PluggedIn, baseline));
        Assert.Null(tracker.Observe(
            PowerSource.PluggedIn,
            baseline with { Fans = FanMode.Maximum }));

        ResumeStateValidationResult result = Assert.IsType<ResumeStateValidationResult>(
            tracker.Complete());
        Assert.Equal(ControlOperationStatus.Blocked, result.Status);
        Assert.Equal(
            "Resume validation observed changed control state for Fans. The changes remain externally classified; no validation-driven recovery writes were attempted.",
            result.Message);
        Assert.False(tracker.ResumeStarted);
        Assert.Null(tracker.Complete());
        Assert.Null(tracker.Observe(PowerSource.PluggedIn, baseline));
    }

    [Fact]
    public void DelayedObservation_CompletesAfterLifecycleWithoutReplayingResult()
    {
        var tracker = new ResumeStateValidationTracker(TimeProvider.System);
        ConfirmedDeviceState baseline = Confirmed();
        tracker.Capture(PowerSource.PluggedIn, baseline);
        tracker.Begin(gpuClockOffsetConfirmationInvalidated: false);
        Assert.Null(tracker.Complete());

        ResumeStateValidationResult result = Assert.IsType<ResumeStateValidationResult>(
            tracker.Observe(PowerSource.PluggedIn, baseline));

        Assert.Equal(ControlOperationStatus.Stored, result.Status);
        Assert.Equal(
            "Resume validation confirmed retained control state for Power mode, Fans, Battery, Lighting. No validation-driven recovery writes were needed.",
            result.Message);
        Assert.Null(tracker.Observe(PowerSource.PluggedIn, baseline));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cancel_DropsObservedAndDelayedBaselines(bool completeBeforeCancel)
    {
        var tracker = new ResumeStateValidationTracker(TimeProvider.System);
        ConfirmedDeviceState baseline = Confirmed();
        tracker.Capture(PowerSource.PluggedIn, baseline);
        tracker.Begin(gpuClockOffsetConfirmationInvalidated: false);
        if (completeBeforeCancel)
        {
            Assert.Null(tracker.Complete());
        }
        else
        {
            Assert.Null(tracker.Observe(PowerSource.PluggedIn, baseline));
        }

        tracker.Cancel();

        Assert.False(tracker.ResumeStarted);
        Assert.Null(tracker.Complete());
        Assert.Null(tracker.Observe(PowerSource.PluggedIn, baseline));
        Assert.Null(tracker.RecordPowerSourceTransition(PowerSource.Battery));
    }

    [Fact]
    public void Capture_ReplacesEarlierLifecycleAndClearsUnavailableBaseline()
    {
        var tracker = new ResumeStateValidationTracker(TimeProvider.System);
        ConfirmedDeviceState baseline = Confirmed();
        tracker.Capture(PowerSource.PluggedIn, baseline);
        tracker.Begin(gpuClockOffsetConfirmationInvalidated: false);
        Assert.Null(tracker.Observe(PowerSource.PluggedIn, baseline));

        tracker.Capture(PowerSource.Battery, baseline with { Fans = FanMode.Maximum });
        Assert.False(tracker.ResumeStarted);
        Assert.Null(tracker.Complete());
        tracker.Begin(gpuClockOffsetConfirmationInvalidated: false);
        tracker.Capture(PowerSource.Battery, confirmed: null);

        Assert.False(tracker.ResumeStarted);
        Assert.Null(tracker.Complete());
        Assert.Null(tracker.RecordPowerSourceTransition(PowerSource.PluggedIn));
    }

    [Fact]
    public void DiscardUnstarted_PreservesOnlyStartedBaseline()
    {
        var tracker = new ResumeStateValidationTracker(TimeProvider.System);
        tracker.Capture(PowerSource.PluggedIn, Confirmed());
        tracker.DiscardUnstarted();
        tracker.Begin(gpuClockOffsetConfirmationInvalidated: false);
        Assert.False(tracker.ResumeStarted);

        tracker.Capture(PowerSource.PluggedIn, Confirmed());
        tracker.Begin(gpuClockOffsetConfirmationInvalidated: false);
        tracker.DiscardUnstarted();
        Assert.True(tracker.ResumeStarted);
    }

    [Fact]
    public void Deadline_StartsAtLifecycleCompletionAndExpiresAtExactBoundary()
    {
        var time = new ManualTimeProvider();
        var tracker = new ResumeStateValidationTracker(time);
        tracker.Capture(PowerSource.PluggedIn, Confirmed());
        tracker.Begin(gpuClockOffsetConfirmationInvalidated: false);
        time.Advance(ResumeStateValidationTracker.ObservationWindow);
        tracker.DiscardExpired();
        Assert.True(tracker.ResumeStarted);
        Assert.Null(tracker.Complete());

        time.Advance(ResumeStateValidationTracker.ObservationWindow - TimeSpan.FromTicks(1));
        tracker.DiscardExpired();
        Assert.True(tracker.ResumeStarted);
        time.Advance(TimeSpan.FromTicks(1));
        tracker.DiscardExpired();

        Assert.False(tracker.ResumeStarted);
        Assert.Null(tracker.Observe(PowerSource.PluggedIn, Confirmed()));
    }

    [Fact]
    public void Observe_DoesNotIntroduceExpirationInsideRuntimeOperation()
    {
        var time = new ManualTimeProvider();
        var tracker = new ResumeStateValidationTracker(time);
        tracker.Capture(PowerSource.PluggedIn, Confirmed());
        tracker.Begin(gpuClockOffsetConfirmationInvalidated: false);
        Assert.Null(tracker.Complete());
        tracker.DiscardExpired();
        time.Advance(ResumeStateValidationTracker.ObservationWindow);

        Assert.IsType<ResumeStateValidationResult>(
            tracker.Observe(PowerSource.PluggedIn, Confirmed()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PowerSourceChange_ConsumesBaselineAndLabelsOnlyActiveLifecycle(
        bool lifecycleCompleted)
    {
        var tracker = new ResumeStateValidationTracker(TimeProvider.System);
        tracker.Capture(PowerSource.PluggedIn, Confirmed());
        tracker.Begin(gpuClockOffsetConfirmationInvalidated: false);
        if (lifecycleCompleted)
        {
            Assert.Null(tracker.Complete());
        }

        ResumeStateValidationResult? result = tracker.Observe(PowerSource.Battery, Confirmed());

        if (lifecycleCompleted)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.NotNull(result);
            Assert.Equal(ControlOperationStatus.Stored, result.Status);
            Assert.Equal(
                "Resume changed the power source from PluggedIn to Battery; the existing destination-profile transition was used instead of comparing the two power profiles.",
                result.Message);
        }
        Assert.False(tracker.ResumeStarted);
        Assert.Null(tracker.Complete());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LightingRestoreAfterObservation_ExcludesLightingAndDisclosesWrite(
        bool writeApplied)
    {
        var tracker = new ResumeStateValidationTracker(TimeProvider.System);
        ConfirmedDeviceState baseline = Confirmed();
        tracker.Capture(PowerSource.PluggedIn, baseline);
        tracker.Begin(gpuClockOffsetConfirmationInvalidated: false);
        Assert.Null(tracker.Observe(
            PowerSource.PluggedIn,
            baseline with { Lighting = LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 25) }));
        tracker.RecordLightingRestore(writeApplied);

        ResumeStateValidationResult result = Assert.IsType<ResumeStateValidationResult>(
            tracker.Complete());

        Assert.Equal(writeApplied ? ControlOperationStatus.Applied : ControlOperationStatus.Stored, result.Status);
        Assert.Equal(
            "Resume validation confirmed retained control state for Power mode, Fans, Battery. No validation-driven recovery writes were needed. "
            + (writeApplied
                ? "Lighting was not compared because OpenBlade restored its owned lighting during the resume lifecycle; that restoration applied a lighting control write."
                : "Lighting was not compared because OpenBlade restored its owned lighting during the resume lifecycle; no new lighting control write was required."),
            result.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GpuInvalidation_ExcludesOtherwiseDetectablePowerDrift(bool invalidated)
    {
        var tracker = new ResumeStateValidationTracker(TimeProvider.System);
        ConfirmedDeviceState baseline = Confirmed() with
        {
            GpuClockOffsets = new GpuClockOffsetCapabilities(
                "Test NVIDIA GPU",
                new GpuClockOffsetRange(-1000, 400, 100),
                new GpuClockOffsetRange(-1000, 3000, 200)),
        };
        ConfirmedDeviceState observed = baseline with
        {
            GpuClockOffsets = new GpuClockOffsetCapabilities(
                "Test NVIDIA GPU",
                new GpuClockOffsetRange(-1000, 400, 0),
                new GpuClockOffsetRange(-1000, 3000, 0)),
        };
        tracker.Capture(PowerSource.PluggedIn, baseline);
        tracker.Begin(gpuClockOffsetConfirmationInvalidated: invalidated);
        Assert.Null(tracker.Observe(PowerSource.PluggedIn, observed));

        ResumeStateValidationResult result = Assert.IsType<ResumeStateValidationResult>(
            tracker.Complete());

        Assert.Equal(
            invalidated ? ControlOperationStatus.Stored : ControlOperationStatus.Blocked,
            result.Status);
        Assert.Equal(
            invalidated
                ? "Resume validation confirmed retained control state for Fans, Battery, Lighting. No validation-driven recovery writes were needed. Unavailable for comparison: Power mode."
                : "Resume validation observed changed control state for Power mode. The changes remain externally classified; no validation-driven recovery writes were attempted.",
            result.Message);
    }

    [Fact]
    public void UnavailableBaseline_ReportsNoComparableSubsystems()
    {
        var tracker = new ResumeStateValidationTracker(TimeProvider.System);
        tracker.Capture(PowerSource.PluggedIn, new ConfirmedDeviceState(null, null, null, null));
        tracker.Begin(gpuClockOffsetConfirmationInvalidated: false);
        Assert.Null(tracker.Observe(PowerSource.PluggedIn, Confirmed()));

        ResumeStateValidationResult result = Assert.IsType<ResumeStateValidationResult>(
            tracker.Complete());

        Assert.Equal(ControlOperationStatus.Stored, result.Status);
        Assert.Equal(
            "Resume validation could not compare control state because no subsystem had confirmed readings in both samples. No validation-driven recovery writes were attempted. Lighting was unavailable in at least one sample and was not compared. Unavailable for comparison: Power mode, Fans, Battery, Lighting.",
            result.Message);
    }

    [Fact]
    public void ComparatorAllowlist_CoversConfirmedState()
    {
        string[] expected = typeof(ConfirmedDeviceState)
            .GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] actual = ResumeStateValidationTracker.ComparedStatePropertyNames
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    private static ConfirmedDeviceState Confirmed() => new(
        PerformancePreset.Balanced,
        FanMode.Automatic,
        new ConfirmedBatteryState(true, 80, false),
        LightingSettingsFactory.Off());

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp;

        public override long GetTimestamp() => timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan amount) => timestamp += amount.Ticks;
    }
}
