using System.Security.Cryptography;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class ExperimentalEvidenceCatalogTests
{
    [Fact]
    public void ExactModelSnapshots_RetainPerFeaturePinnedSources()
    {
        foreach (ExperimentalBladeModel model in ExperimentalBladeModels.All)
        {
            var snapshot = new ExperimentalFeaturesPolicy(model).Snapshot();
            Assert.Equal(model.ModelNumber, snapshot.ModelNumber);
            Assert.NotEmpty(snapshot.FeatureEvidence);
            Assert.All(snapshot.FeatureEvidence, feature =>
            {
                Assert.NotEmpty(feature.Sources);
                if (feature.Feature == "OrdinaryThermalControlsOnUnclassifiedExternalPower")
                {
                    Assert.True(OrdinaryThermalPowerAdmission.SupportsProduct(model.ProductId));
                    Assert.Equal("SynapseSourceBackedWithoutPhysicalValidation", feature.Basis);
                    Assert.Equal(OrdinaryThermalPowerAdmission.EvidenceSource, Assert.Single(feature.Sources));
                }
                else if (feature.Feature == "FanStopAndGuardianAuto")
                {
                    Assert.Equal("FirmwareSourceBackedWithoutPhysicalValidation", feature.Basis);
                    FanStopModelContract[] contracts = FanStopModelContract.All.Where(contract =>
                        contract.Model == model.ModelNumber).ToArray();
                    Assert.NotEmpty(contracts);
                    Assert.Equal(contracts.Length + 1, feature.Sources.Count);
                    Assert.Equal("https://github.com/OSSBlade/openblade-core/blob/main/docs/fan-stop-model-coverage.md",
                        feature.Sources[0]);
                    foreach (FanStopModelContract contract in contracts)
                    {
                        Assert.False(contract.PhysicallyValidated);
                        Assert.Equal(model.ProductId, contract.ProductId);
                        Assert.Equal(model.ProductName, contract.Host.ProductName);
                        string source = $"{contract.FirmwareIdentity};SHA256={contract.FirmwareSha256}";
                        Assert.Contains(source, feature.Sources);
                        Assert.Matches(@";SHA256=[0-9A-F]{64}$", source);
                    }
                }
                else if (feature.Feature == "MappedReactiveRippleAndSequence")
                {
                    Assert.Equal((ushort)0x02C5, model.ProductId);
                    Assert.Equal("KeyboardMcuCommonRegionalBindingsWithoutRgbReadbackOrPhysicalValidation", feature.Basis);
                    Assert.Equal(2, feature.Sources.Count);
                    Assert.Equal("https://dl.razerzone.com/drivers/Razer%20Blade%2014%E2%80%9D%20%282025%29%20%7C%20RZ09-0530/PI4110-MCUv01.04.00.exe;SHA256=AED7039BE5CC4EC3F166A2728B0C1B90839BAE72C8A04150056654F64F20581F",
                        feature.Sources[0]);
                    using Stream map = typeof(Blade14KeyboardLightingLayout).Assembly.GetManifestResourceStream(
                        "OpenBlade.Contracts.Fixtures.Blade14KeyboardLightingLayout.json")!;
                    Assert.Equal($"Blade14KeyboardLightingLayout.json;SHA256={Convert.ToHexString(SHA256.HashData(map))}",
                        feature.Sources[1]);
                }
                else if (feature.Feature is "ActiveKeyboardLightingAndBrightness"
                    or "KeyboardBreathingRandomSingleDualColors" or "KeyboardWaveBothDirections" or "KeyboardStaticColor")
                {
                    Assert.Contains(model.ProductId, new ushort[] { 0x0245, 0x028A, 0x028B, 0x028C, 0x02B7, 0x02B8, 0x02C7 });
                    string basis = feature.Feature switch
                    {
                        "ActiveKeyboardLightingAndBrightness" => "ExactMcuAndSynapseSourceBackedWithoutPhysicalValidation",
                        "KeyboardWaveBothDirections" => "ExactMcuAndSelectedModelLabelsWithoutPhysicalValidation",
                        "KeyboardStaticColor" when model.ProductId is 0x028A or 0x02B7 => "NotEstablishedHiddenPaletteSelector",
                        _ => "ExactMcuSourceBackedWithoutPhysicalValidation",
                    };
                    Assert.Equal(basis, feature.Basis);
                    Assert.StartsWith(model.ProductId == 0x0245 ? "https://rzr.to/KkxlH;SHA256=" : "https://dl.razerzone.com/drivers/", feature.Sources[0]);
                    Assert.All(feature.Sources, source => Assert.Matches(@";SHA256=[0-9A-F]{64}$", source));
                    Assert.All(feature.Sources.Skip(1), source => Assert.StartsWith(
                        $"https://apps.razer.com/synapse/products/{model.ProductId}/", source));
                    Assert.Equal("Pending", snapshot.PhysicalValidation);
                }
                else if (model.ThermalPolicy is not null)
                {
                    Assert.Equal("Supported", snapshot.SupportStatus);
                    Assert.Equal("Pending", snapshot.PhysicalValidation);
                    Assert.All(feature.Sources, source =>
                    {
                        Assert.StartsWith($"https://apps.razer.com/synapse/products/{model.ProductId}/", source);
                        Assert.Matches(@";SHA256=[0-9A-F]{64}$", source);
                    });
                }
                else if (feature.Feature == "StaticSourceHostFanCurves")
                {
                    Assert.Contains(model.ProductId, new ushort[] { 0x029D, 0x029F });
                    Assert.EndsWith("WithoutPhysicalCurveValidation", feature.Basis);
                    Assert.Contains(ExperimentalBladeModels.MaintainedRazerCtlSource, feature.Sources);
                    Assert.All(feature.Sources.Skip(1), source =>
                    {
                        Assert.StartsWith($"https://apps.razer.com/synapse/products/{model.ProductId}/mw/", source);
                        Assert.Matches(@";SHA256=[0-9A-F]{64}$", source);
                    });
                }
                else if (feature.Feature == "AdapterPowerClassification")
                {
                    Assert.Equal("SynapseSourceBacked", feature.Basis);
                    Assert.Equal(model.AdapterPowerEvidenceSource, Assert.Single(feature.Sources));
                    Assert.StartsWith("https://apps.razer.com/synapse/products/", model.AdapterPowerEvidenceSource);
                }
                else if (model.ProductId is 0x0255 or 0x02C5
                    && feature.Basis.StartsWith("SynapseSource", StringComparison.Ordinal))
                {
                    Assert.Equal("Supported", snapshot.SupportStatus);
                    Assert.Equal("Pending", snapshot.PhysicalValidation);
                    Assert.True(feature.Feature.StartsWith("ManualFanRange", StringComparison.Ordinal)
                        || feature.Feature is "FixedAndCurveFanWrites" or "CreatorPerformanceRaw2" or "MaximumPowerPerformanceRaw2");
                    Assert.All(feature.Sources, source =>
                    {
                        Assert.StartsWith($"https://apps.razer.com/synapse/products/{model.ProductId}/", source);
                        Assert.Matches(@";SHA256=[0-9A-F]{64}$", source);
                    });
                }
                else if (feature.Feature is "HyperBoost" or "PadlessHyperBoost")
                {
                    bool blade14 = model.ProductId == 0x029D;
                    Assert.True(blade14 || model.ProductId == 0x029F);
                    string basis = feature.Feature == "HyperBoost" ? "SynapseSourceBacked" : "SourceBackedExtrapolation";
                    Assert.Equal(blade14 ? basis + "NotPubliclyListed" : basis, feature.Basis);
                    Assert.All(feature.Sources, source => Assert.StartsWith(
                        $"https://apps.razer.com/synapse/products/{(blade14 ? 669 : 671)}/mw/", source));
                }
                else if (feature.Feature == "FireAndWheel")
                {
                    Assert.Equal((ushort)0x02A0, model.ProductId);
                    Assert.Equal("ObservedFrameTransportWithExperimentalPlaybackWithoutRgbReadback", feature.Basis);
                    Assert.Equal(3, feature.Sources.Count);
                    Assert.Contains(ExperimentalBladeModels.Blade18SynapseSource, feature.Sources);
                    Assert.Contains(ExperimentalBladeModels.Blade18ObserverControlsSource, feature.Sources);
                    Assert.Contains($"{Blade18LightingAnimation.ResourceName};SHA256={Blade18LightingAnimation.ApprovedSha256}", feature.Sources);
                }
                else if (feature.Basis.StartsWith("SharedSoftware", StringComparison.Ordinal))
                {
                    Assert.Equal((ushort)0x02A0, model.ProductId);
                    Assert.Contains("WithoutRgbReadback", feature.Basis, StringComparison.Ordinal);
                    Assert.Contains(ExperimentalBladeModels.Blade18SynapseSource, feature.Sources);
                    Assert.Contains(ExperimentalBladeModels.Blade18ObserverControlsSource, feature.Sources);
                }
                else if (feature.Basis.StartsWith("SynapseSource", StringComparison.Ordinal))
                {
                    Assert.Equal("RZ09-0484", model.ModelNumber);
                    Assert.Contains(ExperimentalBladeModels.Blade18SynapseSource, feature.Sources);
                }
                else
                {
                    Assert.All(feature.Sources, source => Assert.StartsWith("https://github.com/", source));
                }
            });
            Assert.Equal(snapshot.FeatureEvidence.Count, snapshot.FeatureEvidence.Select(item => item.Feature).Distinct().Count());
        }
    }

    [Fact]
    public void Blade18UniformStatic_DoesNotClaimRgbReadbackOrOwnerValidation()
    {
        var model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02A0);
        var evidence = Assert.Single(new ExperimentalFeaturesPolicy(model).Snapshot().FeatureEvidence,
            item => item.Feature == "UniformStaticRgb");
        Assert.Equal("SourceBackedExtrapolationWithoutRgbReadback", evidence.Basis);
    }

    [Fact]
    public void Blade18FunctionLayer_PinsRecoveredDecoderSourcesWithoutClaimingPhysicalValidation()
    {
        var model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x02A0);
        var evidence = Assert.Single(new ExperimentalFeaturesPolicy(model).Snapshot().FeatureEvidence,
            item => item.Feature == "FunctionLayerAndFnP");

        Assert.Equal("SynapseSourceBackedDefaultShortcutsWithoutPhysicalInputValidation", evidence.Basis);
        Assert.Equal(
        [
            ExperimentalBladeModels.Blade18SynapseSource,
            ExperimentalBladeModels.Blade18MappingSdkSource,
            ExperimentalBladeModels.Blade18MappingEngineSource,
            ExperimentalBladeModels.Blade18SynapseHardwareEventParserSource,
            ExperimentalBladeModels.Blade18ObserverControlsSource,
            "SysUtilsNative.dll;SHA256=01223BFABF0355F42705836974E343A1F16F837D692999020D218B2615B8E318",
            "https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/touchpad-enable-or-disable-toggle-button",
        ], evidence.Sources);
    }

    [Fact]
    public void Blade15Evidence_DoesNotAddUnsupportedBatteryOrLogoClaims()
    {
        var model = ExperimentalBladeModels.All.Single(item => item.ProductId == 0x0255);
        var evidence = new ExperimentalFeaturesPolicy(model).Snapshot().FeatureEvidence;
        Assert.DoesNotContain(evidence, item => item.Feature.Contains("Charge", StringComparison.Ordinal)
            || item.Feature.Contains("Logo", StringComparison.Ordinal));
    }
}
