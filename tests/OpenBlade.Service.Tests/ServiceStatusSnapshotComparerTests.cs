using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ServiceStatusSnapshotComparerTests
{
    [Fact]
    public void Equivalent_ReconstructedCollectionsCompareByContent()
    {
        ServiceStatusSnapshot baseline = CreateStatus();
        DeviceCapabilities capabilities = baseline.Capabilities!;
        ServiceStatusSnapshot reconstructed = baseline with
        {
            PotentialControllers = baseline.PotentialControllers.ToList(),
            ExternalChanges = baseline.ExternalChanges.ToList(),
            UnconfirmedWrites = baseline.UnconfirmedWrites.ToList(),
            LightingSuppressions = baseline.LightingSuppressions!.ToList(),
            ThermalConvergence = baseline.ThermalConvergence! with
            {
                AffectedComponents = baseline.ThermalConvergence.AffectedComponents.ToList(),
            },
            Capabilities = capabilities with
            {
                PerformancePresets = capabilities.PerformancePresets.Reverse().ToHashSet(),
                FanModes = capabilities.FanModes.Reverse().ToHashSet(),
                LightingEffects = capabilities.LightingEffects.Reverse().ToHashSet(),
                ObservedLightingEffects = capabilities.ObservedLightingEffects.Reverse().ToHashSet(),
                SupportedLightingRefreshRates = capabilities.SupportedLightingRefreshRates.Reverse().ToHashSet(),
                LogoLightingModes = capabilities.LogoLightingModes.Reverse().ToHashSet(),
                OneShotLaptopControls = capabilities.OneShotLaptopControls.Reverse().ToHashSet(),
                ChargeLimits = capabilities.ChargeLimits.Reverse().ToHashSet(),
                CustomPerformance = new CustomPerformanceCapabilities(
                    capabilities.CustomPerformance!.AllowedCombinations.Reverse().ToHashSet()),
                ObservedPowerSourceBehaviors = capabilities.ObservedPowerSourceBehaviors
                    .Select(behavior => behavior with { }).ToList(),
            },
        };

        Assert.NotEqual(baseline, reconstructed);
        Assert.True(ServiceStatusSnapshotComparer.Equivalent(baseline, reconstructed));
        Assert.True(ServiceStatusSnapshotComparer.Equivalent(reconstructed, baseline));
    }

    [Theory]
    [InlineData("controllers")]
    [InlineData("external changes")]
    [InlineData("unconfirmed writes")]
    [InlineData("lighting suppressions")]
    [InlineData("thermal components")]
    [InlineData("observed power behavior")]
    public void Equivalent_OrderedCollectionsRetainOrderSensitivity(string collection)
    {
        ServiceStatusSnapshot baseline = CreateStatus();
        ServiceStatusSnapshot reordered = collection switch
        {
            "controllers" => baseline with
            {
                PotentialControllers = baseline.PotentialControllers.Reverse().ToArray(),
            },
            "external changes" => baseline with
            {
                ExternalChanges = baseline.ExternalChanges.Reverse().ToArray(),
            },
            "unconfirmed writes" => baseline with
            {
                UnconfirmedWrites = baseline.UnconfirmedWrites.Reverse().ToArray(),
            },
            "lighting suppressions" => baseline with
            {
                LightingSuppressions = baseline.LightingSuppressions!.Reverse().ToArray(),
            },
            "thermal components" => baseline with
            {
                ThermalConvergence = baseline.ThermalConvergence! with
                {
                    AffectedComponents = baseline.ThermalConvergence.AffectedComponents.Reverse().ToArray(),
                },
            },
            "observed power behavior" => baseline with
            {
                Capabilities = baseline.Capabilities! with
                {
                    ObservedPowerSourceBehaviors = baseline.Capabilities.ObservedPowerSourceBehaviors
                        .Reverse().ToArray(),
                },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(collection)),
        };

        Assert.False(ServiceStatusSnapshotComparer.Equivalent(baseline, reordered));
        Assert.False(ServiceStatusSnapshotComparer.Equivalent(reordered, baseline));
    }

    [Fact]
    public void Equivalent_DetectsCapabilitySetAndNestedThermalChanges()
    {
        ServiceStatusSnapshot baseline = CreateStatus();
        ServiceStatusSnapshot[] changed =
        [
            baseline with { Capabilities = baseline.Capabilities! with { PerformancePresets = new HashSet<PerformancePreset>() } },
            baseline with { Capabilities = baseline.Capabilities! with { ChargeLimits = new HashSet<int> { 75 } } },
            baseline with
            {
                Capabilities = baseline.Capabilities! with
                {
                    CustomPerformance = new CustomPerformanceCapabilities(new HashSet<CustomPerformanceCombination>()),
                },
            },
            baseline with { ThermalConvergence = baseline.ThermalConvergence! with { ExplicitReapplyRequired = true } },
        ];

        Assert.All(changed, candidate =>
            Assert.False(ServiceStatusSnapshotComparer.Equivalent(baseline, candidate)));
    }

    [Fact]
    public void Equivalent_NullOptionalStateRemainsDistinctFromPopulatedState()
    {
        ServiceStatusSnapshot baseline = CreateStatus();
        ServiceStatusSnapshot[] absent =
        [
            baseline with { Capabilities = null },
            baseline with { Capabilities = baseline.Capabilities! with { CustomPerformance = null } },
            baseline with { ThermalConvergence = null },
        ];

        Assert.All(absent, candidate =>
        {
            Assert.True(ServiceStatusSnapshotComparer.Equivalent(candidate, candidate with { }));
            Assert.False(ServiceStatusSnapshotComparer.Equivalent(baseline, candidate));
            Assert.False(ServiceStatusSnapshotComparer.Equivalent(candidate, baseline));
        });
    }

    [Fact]
    public void Equivalent_NullAndEmptyLightingSuppressionsAreEquivalent()
    {
        ServiceStatusSnapshot baseline = CreateStatus() with { LightingSuppressions = null };
        Assert.True(ServiceStatusSnapshotComparer.Equivalent(
            baseline, baseline with { LightingSuppressions = [] }));
        Assert.False(ServiceStatusSnapshotComparer.Equivalent(
            baseline, baseline with { LightingSuppressions = [LightingSuppressionReason.DisplayOff] }));
    }

    [Fact]
    public void Equivalent_DetectsHyperBoostBlockerClassificationChanges()
    {
        ServiceStatusSnapshot baseline = CreateStatus();
        ServiceStatusSnapshot classified = baseline with
        {
            HyperBoostBlockingControllers = ["ControllerA"],
        };

        Assert.False(ServiceStatusSnapshotComparer.Equivalent(baseline, classified));
        Assert.True(ServiceStatusSnapshotComparer.Equivalent(classified,
            classified with { HyperBoostBlockingControllers = ["ControllerA"] }));
        Assert.False(ServiceStatusSnapshotComparer.Equivalent(classified,
            classified with { HyperBoostBlockingControllers = [] }));
    }

    [Fact]
    public void Equivalent_IgnoresRevisionButDetectsTelemetryAndObservationState()
    {
        ServiceStatusSnapshot baseline = CreateStatus();
        Assert.True(ServiceStatusSnapshotComparer.Equivalent(baseline, baseline with { Revision = 100 }));
        Assert.False(ServiceStatusSnapshotComparer.Equivalent(
            baseline, baseline with { LiveReadings = new LiveReadings(-10_000, null, null) }));
        Assert.False(ServiceStatusSnapshotComparer.Equivalent(
            baseline, baseline with { ObservationReason = DeviceObservationReason.CheckingConnection }));
        Assert.False(ServiceStatusSnapshotComparer.Equivalent(
            baseline,
            baseline with
            {
                PadlessHyperBoostEligibility =
                    PadlessHyperBoostEligibilityState.CoolingPadAbsent,
            }));
    }

    [Fact]
    public void Equivalent_UsesOrdinalStringComparisons()
    {
        ServiceStatusSnapshot baseline = CreateStatus();
        Assert.False(ServiceStatusSnapshotComparer.Equivalent(
            baseline, baseline with { PotentialControllers = ["controllera", "ControllerB"] }));
        Assert.False(ServiceStatusSnapshotComparer.Equivalent(
            baseline, baseline with { Message = baseline.Message.ToUpperInvariant() }));
        Assert.False(ServiceStatusSnapshotComparer.Equivalent(
            baseline, baseline with { ServiceVersion = "VERSION" }));
        Assert.False(ServiceStatusSnapshotComparer.Equivalent(
            baseline,
            baseline with { ExperimentalSupportSource = "COMMUNITY-VALIDATION" }));
    }

    [Fact]
    public void Equivalent_DetectsExperimentalConsentAndDeviceState()
    {
        ServiceStatusSnapshot baseline = CreateStatus();

        Assert.False(ServiceStatusSnapshotComparer.Equivalent(
            baseline,
            baseline with { AllowExperimentalFeatures = true }));
        Assert.False(ServiceStatusSnapshotComparer.Equivalent(
            baseline,
            baseline with { IsExperimentalDevice = true }));
        Assert.False(ServiceStatusSnapshotComparer.Equivalent(
            baseline,
            baseline with { ExperimentalSupportSource = "community-validation" }));
    }

    private static ServiceStatusSnapshot CreateStatus()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        DeviceCapabilities capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            CustomPerformance = new CustomPerformanceCapabilities(
                new HashSet<CustomPerformanceCombination>
                {
                    new(CustomPerformanceLevel.Low, CustomPerformanceLevel.Low),
                    new(CustomPerformanceLevel.Medium, CustomPerformanceLevel.Low),
                }),
            ObservedPowerSourceBehaviors =
            [
                new(PowerSource.PluggedIn, PerformancePreset.Balanced, FanMode.Automatic, true, true, true, true),
                new(PowerSource.Battery, PerformancePreset.BatteryBalanced, FanMode.Automatic, false, true, false, false),
            ],
        };
        return new ServiceStatusSnapshot(
            Revision: 1,
            DeviceVerified: true,
            WritesBlocked: false,
            PotentialControllers: ["ControllerA", "ControllerB"],
            ControllerDetectionWarning: null,
            ExternalChanges: [DeviceSubsystem.Performance, DeviceSubsystem.Fans],
            Message: "Observed state",
            Capabilities: capabilities,
            ConfirmedState: null,
            Profiles: profiles,
            DesiredState: new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(FanMode.Automatic, SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
                new DesiredBatteryState(profiles.BatteryPolicy, true, false)),
            ActivePowerSource: PowerSource.PluggedIn,
            LiveReadings: null,
            PerformanceOverride: null,
            FanOverride: null,
            LightingOverride: null,
            FullChargeOverride: false,
            ServiceVersion: "version",
            LightingSuppressions: [LightingSuppressionReason.DisplayOff, LightingSuppressionReason.UserIdle])
        {
            UnconfirmedWrites = [DeviceSubsystem.Performance, DeviceSubsystem.Fans],
            ThermalConvergence = new ThermalConvergenceStatus(
                ThermalControlPolicy.Strict,
                default,
                default,
                [ThermalComponent.Performance, ThermalComponent.Fans],
                ThermalConvergenceReason.StrictPolicy,
                ExplicitReapplyRequired: false),
        };
    }
}
