using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class CpuTuningTests
{
    [Fact]
    public void DisabledOptimizerPreservesRememberedNegativeValueButEffectiveValueIsZero()
    {
        var settings = new CpuCurveOptimizerSettings(false, -17);
        Assert.True(settings.IsWellFormed);
        Assert.Equal(-17, settings.Value);
        Assert.Equal(0, settings.EffectiveValue);
        Assert.Equal(-17, (settings with { Enabled = true }).EffectiveValue);
    }

    [Theory]
    [InlineData(-31)]
    [InlineData(0)]
    [InlineData(1)]
    public void DisabledOptimizerStillRequiresValidRememberedValue(int value) =>
        Assert.False(new CpuCurveOptimizerSettings(false, value).IsWellFormed);

    [Fact]
    public void MissingIntentLeavesBothFeaturesUnmanaged()
    {
        var settings = new CpuTuningSettings(null, null);
        Assert.True(settings.IsWellFormed);
        Assert.False(settings.IsConfigured);
    }

    [Theory]
    [InlineData(44, false)]
    [InlineData(45, true)]
    [InlineData(95, true)]
    [InlineData(96, false)]
    public void IntentUsesObservedModelRange(int watts, bool expected) =>
        Assert.Equal(expected, new CpuTuningSettings(watts, null).IsWellFormed);

    [Fact]
    public void BaselineRetainsPositiveAndMixedValuesOutsideEditorRange()
    {
        CpuCoreCurveOptimizerValue[] cores = [new(0, 40), new(1, -100), new(2, 0)];
        var live = new CpuLiveState(new(63.5, 65000, 95000), cores);
        Assert.True(live.IsWellFormed);
        Assert.Null(live.UniformCurveOptimizerValue);
        Assert.Equal(cores, live.CoreValues);
        Assert.True(new CpuCurveOptimizerState(CpuCurveOptimizerMode.PerCore, null, cores).IsWellFormed);
    }

    [Fact]
    public void UniformDisabledReadbackIsZero()
    {
        var live = new CpuLiveState(new(65, null, null), [new(0, 0), new(1, 0)]);
        Assert.Equal(0, live.UniformCurveOptimizerValue);
    }

    [Fact]
    public void EmptyDuplicateAndOversizedCoreBaselinesAreRejected()
    {
        Assert.False(new CpuLiveState(new(65, null, null), []).IsWellFormed);
        Assert.False(new CpuLiveState(new(65, null, null), [new(0, -1), new(0, -2)]).IsWellFormed);
        Assert.False(new CpuLiveState(new(65, null, null), [new(0, -1), new(64, -2)]).IsWellFormed);
        Assert.False(new CpuLiveState(new(65, null, null),
            Enumerable.Range(0, 65).Select(core => new CpuCoreCurveOptimizerValue(core, 0)).ToArray()).IsWellFormed);
    }

    [Fact]
    public void PhysicalCoreIdentifiersArePreservedWhenTopologyHasGaps()
    {
        var live = new CpuLiveState(new(65, null, null), [new(0, -1), new(2, -2)]);
        Assert.True(live.IsWellFormed);
        Assert.Equal(2, live.CoreValues[1].CoreIndex);
        Assert.Null(live.UniformCurveOptimizerValue);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    [InlineData(10001)]
    public void InvalidLivePowerReadingCannotBecomeConfirmed(double watts) =>
        Assert.False(new CpuLiveState(new(watts, null, null), [new(0, 0)]).IsWellFormed);

    [Fact]
    public void SavedStateDoesNotCreateLiveConfirmationOrClearPendingBoot()
    {
        var backend = new CpuTuningBackendStatus(DeviceFeatureAccess.ObservedOnly,
            CpuTuningBackendAvailability.Available);
        var status = new CpuTuningStatus(
            new(65000, 2, 2, new(CpuCurveOptimizerMode.AllCore, 0, [])),
            null, backend, backend, CpuTuningPendingBoot.Requested, true);
        Assert.True(status.IsWellFormed);
        Assert.Null(status.LiveState);
        Assert.Equal(CpuTuningPendingBoot.Requested, status.PendingBoot);
        Assert.True(status.RestorePending);
        Assert.False(backend.CanWrite);
    }

    [Fact]
    public void DisabledSavedPptZeroIsNotMeasuredZeroWatts()
    {
        var saved = new CpuSavedConfiguration(0, 2, 0xFF,
            new(CpuCurveOptimizerMode.AllCore, 0, []));
        Assert.True(saved.IsWellFormed);
        Assert.False(saved.CustomPowerLimitEnabled);
        Assert.Null((saved with { Policy = 99 }).CustomPowerLimitEnabled);
    }

    [Fact]
    public void MeasuredPowerAloneCannotRestoreBothSmUFields()
    {
        Assert.False(new CpuLivePowerLimits(65, null, null).HasRestoreBaseline);
        Assert.False(new CpuLivePowerLimits(65, 65000, null).HasRestoreBaseline);
        var baseline = new CpuLivePowerLimits(65, 65123, 95001);
        Assert.True(baseline.HasRestoreBaseline);
        Assert.NotEqual(baseline.Message32Milliwatts, baseline.Message33Milliwatts);
        Assert.False(new CpuLivePowerLimits(null, null, null).IsWellFormed);
    }

    [Fact]
    public void OptionalBackendAvailabilityDoesNotGrantAdmission()
    {
        Assert.False(new CpuTuningBackendStatus(DeviceFeatureAccess.ReadOnly,
            CpuTuningBackendAvailability.Available).CanWrite);
        Assert.False(new CpuTuningBackendStatus(DeviceFeatureAccess.ReadWrite,
            CpuTuningBackendAvailability.MissingDependency).CanWrite);
        Assert.True(new CpuTuningBackendStatus(DeviceFeatureAccess.ReadWrite,
            CpuTuningBackendAvailability.Available).CanWrite);
        Assert.False(new CpuTuningBackendStatus((DeviceFeatureAccess)99,
            CpuTuningBackendAvailability.Available).IsWellFormed);
    }

    [Fact]
    public void SavedModeRequiresOneUnambiguousRepresentation()
    {
        Assert.False(new CpuCurveOptimizerState(CpuCurveOptimizerMode.AllCore,
            0, [new(0, 0)]).IsWellFormed);
        Assert.False(new CpuCurveOptimizerState(CpuCurveOptimizerMode.PerCore,
            0, [new(0, 0)]).IsWellFormed);
        Assert.True(new CpuCurveOptimizerState(CpuCurveOptimizerMode.AllCore,
            -65535, []).IsWellFormed);
        Assert.False(new CpuCurveOptimizerState(CpuCurveOptimizerMode.AllCore,
            -65536, []).IsWellFormed);
    }
}
