using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class HyperBoostCapabilityPolicyTests
{
    private const DeviceFeatureAccess RW = DeviceFeatureAccess.ReadWrite;
    private const DeviceFeatureAccess Observed = DeviceFeatureAccess.ObservedOnly;

    [Fact]
    public void BalancedPadlessModel_KeepsPadlessOnlyEntry()
    {
        // RZ09-0581 shape: its captured Performance transition is not writable.
        DeviceCapabilities capabilities = Create(padless: RW, silent: Observed, balanced: RW, performance: Observed);

        Assert.Null(HyperBoostCapabilityPolicy.PadPresentEntryBaseMode(capabilities));
        Assert.Equal(PerformancePreset.Balanced, HyperBoostCapabilityPolicy.PadlessEntryBaseMode(capabilities));
        Assert.Null(HyperBoostCapabilityPolicy.EntryBaseMode(capabilities, coolingPadPresent: true));
        Assert.False(HyperBoostCapabilityPolicy.RequiresCoolingPad(capabilities));
        Assert.True(HyperBoostCapabilityPolicy.IsWritable(capabilities));
    }

    [Fact]
    public void PerformancePadlessModel_AdmitsBothPadPathsFromPerformance()
    {
        // Admitted RZ09-0528 shape: pad-present and pad-less entries share the Performance transition.
        DeviceCapabilities capabilities = Create(padless: RW, silent: Observed, balanced: Observed, performance: RW);

        Assert.Equal(PerformancePreset.Performance, HyperBoostCapabilityPolicy.EntryBaseMode(capabilities, coolingPadPresent: true));
        Assert.Equal(PerformancePreset.Performance, HyperBoostCapabilityPolicy.EntryBaseMode(capabilities, coolingPadPresent: false));
        Assert.False(HyperBoostCapabilityPolicy.RequiresCoolingPad(capabilities));
        Assert.True(HyperBoostCapabilityPolicy.IsWritable(capabilities));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(DeviceFeatureAccess.ObservedOnly)]
    public void UnadmittedPadless_RequiresTheCoolingPad(DeviceFeatureAccess? padless)
    {
        DeviceCapabilities capabilities = Create(padless, silent: Observed, balanced: Observed, performance: RW);

        Assert.True(HyperBoostCapabilityPolicy.RequiresCoolingPad(capabilities));
        Assert.Equal(PerformancePreset.Performance, HyperBoostCapabilityPolicy.PadPresentEntryBaseMode(capabilities));
        Assert.Null(HyperBoostCapabilityPolicy.PadlessEntryBaseMode(capabilities));
    }

    [Fact]
    public void PadlessWithoutAWritableBase_IsNotWritable()
    {
        DeviceCapabilities capabilities = Create(padless: RW, silent: RW, balanced: Observed, performance: Observed);

        Assert.Null(HyperBoostCapabilityPolicy.PadlessEntryBaseMode(capabilities));
        Assert.Null(HyperBoostCapabilityPolicy.PadPresentEntryBaseMode(capabilities));
        Assert.False(HyperBoostCapabilityPolicy.IsWritable(capabilities));
    }

    [Fact]
    public void ObservedHyperBoost_AdmitsNoEntryPath()
    {
        DeviceCapabilities capabilities = Create(padless: Observed, silent: Observed, balanced: Observed, performance: Observed) with
        {
            HyperBoostAccess = Observed,
        };

        Assert.Null(HyperBoostCapabilityPolicy.PadPresentEntryBaseMode(capabilities));
        Assert.Null(HyperBoostCapabilityPolicy.PadlessEntryBaseMode(capabilities));
        Assert.False(HyperBoostCapabilityPolicy.IsWritable(capabilities));
    }

    private static DeviceCapabilities Create(
        DeviceFeatureAccess? padless,
        DeviceFeatureAccess silent,
        DeviceFeatureAccess balanced,
        DeviceFeatureAccess performance) => DeviceCapabilities.Unavailable() with
        {
            HyperBoostAccess = RW,
            HyperBoostWithoutCoolingPadAccess = padless,
            HyperBoostBaseModes = new HyperBoostBaseModeCapabilities(silent, balanced, performance),
        };
}
