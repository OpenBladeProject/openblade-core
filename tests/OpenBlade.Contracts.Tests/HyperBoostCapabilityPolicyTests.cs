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

    [Fact]
    public void DirectEntry_UsesEveryWritableBaseOnAnAdmittedPath()
    {
        // Admitted RZ09-0528 shape: Silent, Balanced and Performance enter and restore directly.
        DeviceCapabilities rz0528 = Create(padless: RW, silent: RW, balanced: RW, performance: RW);
        foreach (PerformancePreset mode in new[] { PerformancePreset.Silent, PerformancePreset.Balanced, PerformancePreset.Performance })
        {
            Assert.True(HyperBoostCapabilityPolicy.IsDirectEntryBase(rz0528, coolingPadPresent: true, mode));
            Assert.True(HyperBoostCapabilityPolicy.IsDirectEntryBase(rz0528, coolingPadPresent: false, mode));
        }
        // Custom is direct only where it is separately admitted.
        Assert.False(HyperBoostCapabilityPolicy.IsDirectEntryBase(rz0528, coolingPadPresent: false, PerformancePreset.Custom));
        DeviceCapabilities rz0528Custom = rz0528 with { HyperBoostBaseModes = rz0528.HyperBoostBaseModes! with { Custom = RW } };
        Assert.True(HyperBoostCapabilityPolicy.IsDirectEntryBase(rz0528Custom, coolingPadPresent: false, PerformancePreset.Custom));
        Assert.True(HyperBoostCapabilityPolicy.IsDirectEntryBase(rz0528Custom, coolingPadPresent: true, PerformancePreset.Custom));

        // RZ09-0581 shape: pad-less Balanced only; no pad-present path.
        DeviceCapabilities rz0581 = Create(padless: RW, silent: Observed, balanced: RW, performance: Observed);
        Assert.True(HyperBoostCapabilityPolicy.IsDirectEntryBase(rz0581, coolingPadPresent: false, PerformancePreset.Balanced));
        Assert.False(HyperBoostCapabilityPolicy.IsDirectEntryBase(rz0581, coolingPadPresent: false, PerformancePreset.Silent));
        Assert.False(HyperBoostCapabilityPolicy.IsDirectEntryBase(rz0581, coolingPadPresent: true, PerformancePreset.Balanced));
    }

    [Fact]
    public void DeclaredBalancedCoolingPadEntry_RestoresOnlyWritableOrdinaryBases()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            HyperBoostAccess = RW,
            HyperBoostBaseModes = new HyperBoostBaseModeCapabilities(RW, RW, Observed)
            {
                Custom = RW,
                CoolingPadEntryBaseMode = PerformancePreset.Balanced,
            },
        };

        Assert.Equal(
            PerformancePreset.Balanced,
            HyperBoostCapabilityPolicy.PadPresentEntryBaseMode(capabilities));
        Assert.True(HyperBoostCapabilityPolicy.IsDirectEntryBase(
            capabilities, coolingPadPresent: true, PerformancePreset.Balanced));
        Assert.True(HyperBoostCapabilityPolicy.IsDirectEntryBase(
            capabilities, coolingPadPresent: true, PerformancePreset.Silent));
        Assert.True(HyperBoostCapabilityPolicy.IsDirectEntryBase(
            capabilities, coolingPadPresent: true, PerformancePreset.Custom));
        Assert.False(HyperBoostCapabilityPolicy.IsDirectEntryBase(
            capabilities, coolingPadPresent: true, PerformancePreset.Performance));
        Assert.Null(HyperBoostCapabilityPolicy.PadlessEntryBaseMode(capabilities));
    }

    [Fact]
    public void DeclaredTurboCoolingPadEntry_AdmitsTurboAndPadlessOrdinaryBases()
    {
        // RZ09-0510 shape: product 695 lists Turbo (raw 1) and has no Performance mode.
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable() with
        {
            HyperBoostAccess = RW,
            HyperBoostWithoutCoolingPadAccess = RW,
            HyperBoostBaseModes = new HyperBoostBaseModeCapabilities(RW, RW, Observed)
            {
                Custom = RW,
                Turbo = RW,
                CoolingPadEntryBaseMode = PerformancePreset.Turbo,
            },
        };

        Assert.Equal(PerformancePreset.Turbo, HyperBoostCapabilityPolicy.PadPresentEntryBaseMode(capabilities));
        Assert.Equal(PerformancePreset.Balanced, HyperBoostCapabilityPolicy.PadlessEntryBaseMode(capabilities));
        foreach (bool pad in new[] { true, false })
        {
            foreach (PerformancePreset mode in new[] { PerformancePreset.Silent, PerformancePreset.Balanced,
                PerformancePreset.Turbo, PerformancePreset.Custom })
            {
                Assert.True(HyperBoostCapabilityPolicy.IsDirectEntryBase(capabilities, pad, mode));
            }
            Assert.False(HyperBoostCapabilityPolicy.IsDirectEntryBase(capabilities, pad, PerformancePreset.Performance));
        }
        DeviceCapabilities withoutTurbo = capabilities with
        {
            HyperBoostBaseModes = capabilities.HyperBoostBaseModes! with { Turbo = null, CoolingPadEntryBaseMode = null },
        };
        Assert.False(HyperBoostCapabilityPolicy.IsDirectEntryBase(withoutTurbo, coolingPadPresent: false, PerformancePreset.Turbo));
    }

    [Theory]
    [InlineData(PerformancePreset.Turbo, PerformancePreset.Turbo, PowerSource.PluggedIn, true)]
    [InlineData(PerformancePreset.Balanced, PerformancePreset.Balanced, PowerSource.Battery, true)]
    [InlineData(PerformancePreset.Balanced, PerformancePreset.BatteryBalanced, PowerSource.Battery, true)]
    [InlineData(PerformancePreset.Balanced, PerformancePreset.BatteryBalanced, PowerSource.PluggedIn, false)]
    [InlineData(PerformancePreset.Balanced, PerformancePreset.BatteryBalanced, PowerSource.UsbC, false)]
    // The mode-7 battery handoff is Balanced whatever base is owned.
    [InlineData(PerformancePreset.Silent, PerformancePreset.BatteryBalanced, PowerSource.Battery, true)]
    [InlineData(PerformancePreset.Turbo, PerformancePreset.BatteryBalanced, PowerSource.Battery, true)]
    [InlineData(PerformancePreset.Turbo, PerformancePreset.Balanced, PowerSource.Battery, true)]
    [InlineData(PerformancePreset.Custom, PerformancePreset.Balanced, PowerSource.Battery, true)]
    [InlineData(PerformancePreset.Performance, PerformancePreset.Balanced, PowerSource.Battery, true)]
    // A backend whose reviewed exit restores the owned base on battery still confirms it.
    [InlineData(PerformancePreset.Performance, PerformancePreset.Performance, PowerSource.Battery, true)]
    [InlineData(PerformancePreset.Turbo, PerformancePreset.Silent, PowerSource.Battery, false)]
    [InlineData(PerformancePreset.Turbo, PerformancePreset.BatterySaver, PowerSource.Battery, false)]
    [InlineData(PerformancePreset.Turbo, PerformancePreset.Balanced, PowerSource.PluggedIn, false)]
    [InlineData(PerformancePreset.Silent, PerformancePreset.Balanced, PowerSource.ExternalUnknown, false)]
    [InlineData(PerformancePreset.Turbo, PerformancePreset.Performance, PowerSource.PluggedIn, false)]
    [InlineData(PerformancePreset.Turbo, null, PowerSource.PluggedIn, false)]
    [InlineData(PerformancePreset.Turbo, null, PowerSource.Battery, false)]
    public void RestoredBase_AcceptsTheOwnedBaseOrTheBatteryBalancedHandoff(
        PerformancePreset requested, PerformancePreset? confirmed, PowerSource source, bool restored)
    {
        Assert.Equal(restored, HyperBoostCapabilityPolicy.IsRestoredBase(requested, confirmed, source));
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

    [Fact]
    public void EntryRestriction_DeclinesEveryEntryPathButKeepsExit()
    {
        DeviceCapabilities admitted = Create(padless: RW, silent: RW, balanced: RW, performance: RW);
        DeviceCapabilities restricted = admitted with
        {
            HyperBoostEntryRestriction = HyperBoostEntryRestriction.VendorSystemSku,
        };

        Assert.True(HyperBoostCapabilityPolicy.IsWritable(admitted));
        Assert.False(HyperBoostCapabilityPolicy.IsEntryRestricted(admitted));
        Assert.True(HyperBoostCapabilityPolicy.IsEntryRestricted(restricted));
        Assert.Null(HyperBoostCapabilityPolicy.PadPresentEntryBaseMode(restricted));
        Assert.Null(HyperBoostCapabilityPolicy.PadlessEntryBaseMode(restricted));
        Assert.False(HyperBoostCapabilityPolicy.IsWritable(restricted));
        foreach (PerformancePreset mode in new[] { PerformancePreset.Silent, PerformancePreset.Balanced, PerformancePreset.Performance })
        {
            Assert.False(HyperBoostCapabilityPolicy.IsDirectEntryBase(restricted, coolingPadPresent: true, mode));
            Assert.False(HyperBoostCapabilityPolicy.IsDirectEntryBase(restricted, coolingPadPresent: false, mode));
            // Restore access per base is unchanged.
            Assert.Equal(RW, restricted.HyperBoostBaseModes!.AccessFor(mode));
        }
        Assert.True(HyperBoostCapabilityPolicy.CanExit(restricted));
        Assert.False(HyperBoostCapabilityPolicy.CanExit(restricted with { HyperBoostAccess = Observed }));
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
