using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed class ThermalConvergenceClassifierTests
{
    [Fact]
    public void ExactAutomaticStateIsMatched()
    {
        ThermalConvergenceAssessment result = ThermalConvergenceClassifier.Classify(
            Desired(PerformancePreset.Balanced, FanMode.Automatic),
            Confirmed(PerformancePreset.Balanced, FanMode.Automatic));

        Assert.Equal(ThermalConvergenceObservationKind.Matched, result.Kind);
        Assert.Equal(ThermalConvergenceContradiction.None, result.Contradiction);
    }

    [Theory]
    [InlineData(PerformancePreset.Silent)]
    [InlineData(PerformancePreset.Performance)]
    public void VaryingPerformanceValuesNormalizeToOneClass(PerformancePreset observed)
    {
        ThermalConvergenceAssessment result = ThermalConvergenceClassifier.Classify(
            Desired(PerformancePreset.Balanced, FanMode.Automatic),
            Confirmed(observed, FanMode.Automatic));

        Assert.Equal(ThermalConvergenceObservationKind.Candidate, result.Kind);
        Assert.Equal(
            ThermalConvergenceContradiction.Performance,
            result.Contradiction);
    }

    [Fact]
    public void DesiredManualAndObservedAutomaticCreatesTopologyCandidate()
    {
        ThermalConvergenceAssessment result = ThermalConvergenceClassifier.Classify(
            Desired(PerformancePreset.Balanced, FanMode.Fixed),
            Confirmed(PerformancePreset.Balanced, FanMode.Automatic));

        Assert.Equal(ThermalConvergenceObservationKind.Candidate, result.Kind);
        Assert.Equal(
            ThermalConvergenceContradiction.FanTopology,
            result.Contradiction);
    }

    [Fact]
    public void DesiredAutomaticAndObservedManualCreatesTopologyCandidate()
    {
        ThermalConvergenceAssessment result = ThermalConvergenceClassifier.Classify(
            Desired(PerformancePreset.Balanced, FanMode.Automatic),
            Confirmed(null, FanMode.Fixed));

        Assert.Equal(ThermalConvergenceObservationKind.Candidate, result.Kind);
        Assert.Equal(
            ThermalConvergenceContradiction.FanTopology,
            result.Contradiction);
    }

    [Theory]
    [InlineData(FanMode.Fixed)]
    [InlineData(FanMode.ManualCurve)]
    [InlineData(FanMode.Maximum)]
    public void GenericManualReadbackNeverProvesExactManualIntent(FanMode desiredMode)
    {
        ThermalConvergenceAssessment result = ThermalConvergenceClassifier.Classify(
            Desired(PerformancePreset.Balanced, desiredMode),
            Confirmed(null, FanMode.Fixed));

        Assert.Equal(ThermalConvergenceObservationKind.ManualTopologyOnly, result.Kind);
        Assert.Equal(ThermalConvergenceContradiction.None, result.Contradiction);
    }

    [Fact]
    public void CurrentEpochExactManualOwnershipMakesGenericTopologyMatch()
    {
        ThermalConvergenceAssessment result = ThermalConvergenceClassifier.Classify(
            Desired(PerformancePreset.Balanced, FanMode.ManualCurve),
            Confirmed(null, FanMode.Fixed),
            exactManualIntentOwned: true);

        Assert.Equal(ThermalConvergenceObservationKind.Matched, result.Kind);
        Assert.Equal(ThermalConvergenceContradiction.None, result.Contradiction);
    }

    [Fact]
    public void UnavailableAutomaticReadbackAwaitsObservation()
    {
        ThermalConvergenceAssessment result = ThermalConvergenceClassifier.Classify(
            Desired(PerformancePreset.Balanced, FanMode.Automatic),
            Confirmed(null, null));

        Assert.Equal(ThermalConvergenceObservationKind.AwaitingObservation, result.Kind);
    }

    [Fact]
    public void ActiveHyperBoostIsExternalAndNeverACandidate()
    {
        ThermalConvergenceAssessment result = ThermalConvergenceClassifier.Classify(
            Desired(PerformancePreset.Balanced, FanMode.Automatic),
            Confirmed(PerformancePreset.Balanced, FanMode.Automatic) with
            {
                HyperBoostActive = true,
            });

        Assert.Equal(ThermalConvergenceObservationKind.ExternalHyperBoost, result.Kind);
        Assert.Equal(ThermalConvergenceContradiction.None, result.Contradiction);
    }

    [Fact]
    public void CombinedObservableDriftDeclaresBothComponents()
    {
        ThermalConvergenceAssessment result = ThermalConvergenceClassifier.Classify(
            Desired(PerformancePreset.Balanced, FanMode.Automatic),
            Confirmed(PerformancePreset.Silent, FanMode.Fixed));

        Assert.Equal(
            ThermalConvergenceContradiction.Performance
                | ThermalConvergenceContradiction.FanTopology,
            result.Contradiction);
    }

    [Theory]
    [InlineData(PerformancePreset.Balanced)]
    [InlineData(PerformancePreset.Silent)]
    [InlineData(PerformancePreset.Performance)]
    public void EnabledGpuClockDriftIsIndependentOfPerformancePreset(
        PerformancePreset performance)
    {
        DesiredDeviceState desired = Desired(performance, FanMode.Automatic) with
        {
            GpuClockTuning = new GpuClockTuningSettings(
                Enabled: true,
                new GpuClockOffsetSettings(100, 200)),
        };
        ConfirmedDeviceState confirmed = Confirmed(performance, FanMode.Automatic) with
        {
            GpuClockOffsets = new GpuClockOffsetCapabilities(
                "Test GPU",
                new GpuClockOffsetRange(-300, 300, CurrentMegahertz: 0),
                new GpuClockOffsetRange(-500, 500, CurrentMegahertz: 0)),
        };

        ThermalConvergenceAssessment result = ThermalConvergenceClassifier.Classify(
            desired,
            confirmed);

        Assert.Equal(ThermalConvergenceObservationKind.Candidate, result.Kind);
        Assert.Equal(ThermalConvergenceContradiction.Performance, result.Contradiction);
    }

    [Fact]
    public void EnabledGpuClockTuningAwaitsMissingReadback()
    {
        DesiredDeviceState desired = Desired(
            PerformancePreset.Balanced,
            FanMode.Automatic) with
        {
            GpuClockTuning = new GpuClockTuningSettings(
                Enabled: true,
                new GpuClockOffsetSettings(100, 200)),
        };

        ThermalConvergenceAssessment result = ThermalConvergenceClassifier.Classify(
            desired,
            Confirmed(PerformancePreset.Balanced, FanMode.Automatic));

        Assert.Equal(ThermalConvergenceObservationKind.AwaitingObservation, result.Kind);
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(100, 200, true)]
    public void DisabledGpuClockTuningRequiresConfirmedDefaultOffsets(
        int coreOffset,
        int memoryOffset,
        bool expectCandidate)
    {
        DesiredDeviceState desired = Desired(
            PerformancePreset.Balanced,
            FanMode.Automatic) with
        {
            GpuClockTuning = GpuClockTuningSettings.Disabled,
        };
        ConfirmedDeviceState confirmed = Confirmed(
            PerformancePreset.Balanced,
            FanMode.Automatic) with
        {
            GpuClockOffsets = new GpuClockOffsetCapabilities(
                "Test GPU",
                new GpuClockOffsetRange(
                    -300,
                    300,
                    CurrentMegahertz: coreOffset),
                new GpuClockOffsetRange(
                    -500,
                    500,
                    CurrentMegahertz: memoryOffset)),
        };

        ThermalConvergenceAssessment result = ThermalConvergenceClassifier.Classify(
            desired,
            confirmed);

        Assert.Equal(
            expectCandidate
                ? ThermalConvergenceObservationKind.Candidate
                : ThermalConvergenceObservationKind.Matched,
            result.Kind);
    }

    private static DesiredDeviceState Desired(
        PerformancePreset performance,
        FanMode fans) => new(
            new ResolvedSetting<PerformancePreset>(
                performance,
                SettingValueSource.PowerProfile),
            new ResolvedSetting<FanMode>(fans, SettingValueSource.PowerProfile),
            new ResolvedSetting<LightingSettings>(
                LightingSettingsFactory.Off(),
                SettingValueSource.PowerProfile),
            new DesiredBatteryState(BatteryPolicy.Create(80), true, false));

    private static ConfirmedDeviceState Confirmed(
        PerformancePreset? performance,
        FanMode? fans) => new(performance, fans, null, null);
}
