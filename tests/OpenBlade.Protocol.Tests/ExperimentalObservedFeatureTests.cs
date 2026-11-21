using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ExperimentalObservedFeatureTests
{
    private static readonly int[] AmdPowerLimitModels = [0x02B6, 0x02C5];
    private static readonly int[] IntelPowerLimitModels = [0x029F, 0x02A0, 0x02B7, 0x02B8, 0x02C7];
    private static readonly int[] FirmwareGamingGpuModels = [0x0246, 0x0268, 0x0255];

    [Fact]
    public void AmdCpuPowerLimit_IsObservedOnlyAt45To95WattsOnTheReviewedModels()
    {
        foreach (ExperimentalBladeModel model in ExperimentalBladeModels.All)
        {
            foreach (bool writesAllowed in new[] { true, false })
            {
                CpuPowerLimitCapabilities? limit = ExperimentalBladePort.CreateCapabilities(model, writesAllowed).CpuPowerLimit;
                if (AmdPowerLimitModels.Contains(model.ProductId))
                {
                    Assert.Equal(new CpuPowerLimitCapabilities(new DeviceNumericRange(45, 95, 1), DeviceFeatureAccess.ObservedOnly), limit);
                    Assert.True(limit!.IsWellFormed);
                }
                else
                {
                    Assert.Null(limit);
                }
            }
        }
    }

    [Fact]
    public void IntelCpuPowerLimits_AreObservedOnlyWithSynapseRangesAndDefaults()
    {
        foreach (ExperimentalBladeModel model in ExperimentalBladeModels.All)
        {
            foreach (bool writesAllowed in new[] { true, false })
            {
                IntelCpuPowerLimitCapabilities? limit =
                    ExperimentalBladePort.CreateCapabilities(model, writesAllowed).IntelCpuPowerLimit;
                if (!IntelPowerLimitModels.Contains(model.ProductId))
                {
                    Assert.Null(limit);
                    continue;
                }

                Assert.NotNull(limit);
                Assert.True(limit.IsWellFormed);
                Assert.Equal(DeviceFeatureAccess.ObservedOnly, limit.Access);
                Assert.True(limit.SupportsShortTermPowerEnableDisable);
                Assert.Equal(120_000, limit.DefaultLongTermPowerMilliwatts);
                Assert.Equal(160_000, limit.DefaultShortTermPowerMilliwatts);
                Assert.Equal(56, limit.DefaultTimeWindowSeconds);
                // 671 and 672 pin the defaults but not the UI ranges.
                if (model.ProductId is 0x029F or 0x02A0)
                {
                    Assert.False(limit.RangesKnown);
                    Assert.Null(limit.LongTermPowerMilliwatts);
                    Assert.Null(limit.ShortTermPowerMilliwatts);
                    Assert.Null(limit.TimeWindowSeconds);
                    continue;
                }

                Assert.True(limit.RangesKnown);
                Assert.Equal(new DeviceNumericRange(1_000, 190_000, 125), limit.LongTermPowerMilliwatts);
                Assert.Equal(new DeviceNumericRange(1_000, 200_000, 125), limit.ShortTermPowerMilliwatts);
                Assert.True(limit.SupportsShortTermPowerEnableDisable);
                Assert.Equal(120_000, limit.DefaultLongTermPowerMilliwatts);
                Assert.Equal(160_000, limit.DefaultShortTermPowerMilliwatts);
                Assert.Equal(56, limit.DefaultTimeWindowSeconds);
                Assert.Equal(
                    [1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 32, 40, 48, 56, 64, 80, 96, 112, 128],
                    limit.TimeWindowSeconds);
            }
        }
    }

    // Product 669 is series 9: its power-limit path drives the V22 curve
    // optimizer, so RZ09-0482 shows no AMD CPU power limit.
    [Fact]
    public void AmdCpuPowerLimit_IsNotShownOnRz090482()
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x029D);
        Assert.Null(ExperimentalBladePort.CreateCapabilities(model, true).CpuPowerLimit);
        Assert.DoesNotContain(ExperimentalBladeModels.FeatureEvidenceFor(model), item => item.Feature == "CpuPowerLimit");
    }

    [Theory]
    [InlineData(0x0246, "SynapseSourceObservedOnlyNativeVendorWrite")]
    [InlineData(0x0268, "SynapseSourceObservedOnlyNativeVendorWriteWattageModuleUnpinned")]
    [InlineData(0x0255, "SynapseSourceObservedOnlyNativeVendorWriteWattageModuleUnpinned")]
    public void FirmwareGamingGpuOverclock_MarksTheUnpinnedWattageModules(int productId, string basis)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == productId);
        ExperimentalFeatureEvidence evidence = Assert.Single(
            ExperimentalBladeModels.FeatureEvidenceFor(model), item => item.Feature == "FirmwareGamingGpuOverclock");
        Assert.Equal(basis, evidence.Basis);
        Assert.Equal(productId == 0x0246, evidence.Sources.Any(source => source.Contains("Wattage", StringComparison.Ordinal)));
    }

    [Fact]
    public void FirmwareGamingGpuOverclock_IsObservedOnlyOnTheWattageModels()
    {
        foreach (ExperimentalBladeModel model in ExperimentalBladeModels.All)
        {
            Assert.Equal(
                FirmwareGamingGpuModels.Contains(model.ProductId) ? DeviceFeatureAccess.ObservedOnly : null,
                ExperimentalBladePort.CreateCapabilities(model, true).FirmwareGamingGpuOverclockAccess);
        }
    }

    [Fact]
    public void LocalDimming_IsObservedOnlyOnTheHdrCoupledRz090530AndNotOfferedElsewhereBeforeAPanelRead()
    {
        foreach (ExperimentalBladeModel model in ExperimentalBladeModels.All)
        {
            LocalDimmingCapabilities? dimming = ExperimentalBladePort.CreateCapabilities(model, true).LocalDimming;
            Assert.Equal(
                model.ProductId == 0x02C5
                    ? new LocalDimmingCapabilities(DeviceFeatureAccess.ObservedOnly, SupportsClear: false)
                    : null,
                dimming);
            Assert.True(dimming?.IsWellFormed ?? true);
        }
    }

    [Theory]
    [InlineData(0x02B6, "CpuPowerLimit")]
    [InlineData(0x02C5, "CpuPowerLimit")]
    [InlineData(0x029F, "IntelCpuPowerLimits")]
    [InlineData(0x02A0, "IntelCpuPowerLimits")]
    [InlineData(0x02B7, "IntelCpuPowerLimits")]
    [InlineData(0x02B8, "IntelCpuPowerLimits")]
    [InlineData(0x02C7, "IntelCpuPowerLimits")]
    [InlineData(0x0246, "FirmwareGamingGpuOverclock")]
    [InlineData(0x0268, "FirmwareGamingGpuOverclock")]
    [InlineData(0x0255, "FirmwareGamingGpuOverclock")]
    [InlineData(0x02B7, "LocalDimmingClearOnMiniLedPanels")]
    [InlineData(0x02B8, "LocalDimmingClearOnMiniLedPanels")]
    [InlineData(0x02C5, "LocalDimmingFollowsWindowsHdr")]
    public void FeatureEvidence_RecordsPinnedSynapseSourcesForEachObservedFeature(int productId, string feature)
    {
        ExperimentalBladeModel model = ExperimentalBladeModels.All.Single(item => item.ProductId == productId);
        ExperimentalFeatureEvidence evidence = Assert.Single(
            ExperimentalBladeModels.FeatureEvidenceFor(model), item => item.Feature == feature);
        Assert.Contains("evidence/source-reviewed-blades/2026-11-20-observed-features-and-local-dimming.json", evidence.Sources);
        Assert.All(evidence.Sources.Where(source => source.StartsWith("https://", StringComparison.Ordinal)),
            source => Assert.Matches(@"^https://apps\.razer\.com/synapse/products/\d{3}/.+;SHA256=[0-9A-F]{64}$", source));
        Assert.Contains(evidence.Sources, source => source.Contains(
            $"/products/{productId}/", StringComparison.Ordinal));
    }

    [Fact]
    public void FeatureEvidence_ObservedFeaturesAppearOnlyOnTheirModels()
    {
        string[] observed =
        [
            "CpuPowerLimit", "IntelCpuPowerLimits", "FirmwareGamingGpuOverclock",
            "LocalDimmingClearOnMiniLedPanels", "LocalDimmingFollowsWindowsHdr",
        ];
        int[] withObserved = [.. AmdPowerLimitModels, .. IntelPowerLimitModels, .. FirmwareGamingGpuModels];
        foreach (ExperimentalBladeModel model in ExperimentalBladeModels.All.Where(item => !withObserved.Contains(item.ProductId)))
        {
            Assert.DoesNotContain(ExperimentalBladeModels.FeatureEvidenceFor(model), item => observed.Contains(item.Feature));
        }
    }
}
