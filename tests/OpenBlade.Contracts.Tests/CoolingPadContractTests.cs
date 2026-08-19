using OpenBlade.Contracts;

namespace OpenBlade.Contracts.Tests;

public sealed class CoolingPadContractTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(10_001)]
    public void CoolingDemandLevel_RejectsOutOfRangeValues(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CoolingDemandLevel(value));
    }

    [Fact]
    public void CoolingDemandLevel_ProvidesNormalizedAndMaximumOperations()
    {
        CoolingDemandLevel fortyTwoPercent = CoolingDemandLevel.FromPercent(42);

        Assert.Equal(4_200, fortyTwoPercent.BasisPoints);
        Assert.Equal(0.42, fortyTwoPercent.Fraction, precision: 6);
        Assert.Equal(
            new CoolingDemandLevel(8_000),
            CoolingDemandLevel.Max(fortyTwoPercent, new CoolingDemandLevel(8_000)));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(2200)]
    [InlineData(3200)]
    public void CoolingPadFanTarget_AcceptsCapturedRangeAndStep(int rpm)
    {
        Assert.Equal(rpm, new CoolingPadFanTarget(rpm).Rpm);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(499)]
    [InlineData(525)]
    [InlineData(3250)]
    public void CoolingPadFanTarget_RejectsUnsupportedValues(int rpm)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CoolingPadFanTarget(rpm));
    }

    [Theory]
    [InlineData(0, 500)]
    [InlineData(1_563, 500)]
    [InlineData(6_875, 2_200)]
    [InlineData(10_000, 3_200)]
    public void CoolingPadFanTarget_QuantizesNormalizedDemand(
        int basisPoints,
        int expectedRpm)
    {
        CoolingPadFanTarget target = CoolingPadFanTarget.FromDemand(
            new CoolingDemandLevel(basisPoints));

        Assert.Equal(expectedRpm, target.Rpm);
        Assert.Equal(
            target,
            CoolingPadFanTarget.FromDemand(target.ToDemand()));
    }

    [Fact]
    public void CoolingPadSmartCurve_InterpolatesAndClampsDeterministically()
    {
        var curve = new CoolingPadSmartCurve(
            [
                new(40, new CoolingDemandLevel(2_000)),
                new(80, new CoolingDemandLevel(8_000)),
            ],
            [
                new(30, new CoolingDemandLevel(3_000)),
                new(90, new CoolingDemandLevel(9_000)),
            ],
            useHigherTemperatureForBoth: false);

        Assert.Equal(new CoolingDemandLevel(2_000), curve.InterpolateCpu(20));
        Assert.Equal(new CoolingDemandLevel(5_000), curve.InterpolateCpu(60));
        Assert.Equal(new CoolingDemandLevel(8_000), curve.InterpolateCpu(100));
        Assert.Equal(new CoolingDemandLevel(6_000), curve.InterpolateGpu(60));
    }

    [Fact]
    public void CoolingPadSmartCurve_RejectsDecreasingDemand()
    {
        Assert.Throws<ArgumentException>(() => new CoolingPadSmartCurve(
            [
                new(40, new CoolingDemandLevel(5_000)),
                new(80, new CoolingDemandLevel(4_000)),
            ],
            CoolingPadSmartCurve.Default.Gpu,
            useHigherTemperatureForBoth: false));
    }

    [Fact]
    public void CoolingPadSmartCurve_DefensivelyCopiesInputPoints()
    {
        CoolingPadCurvePoint[] cpu =
        [
            new(40, new CoolingDemandLevel(2_000)),
            new(80, new CoolingDemandLevel(8_000)),
        ];
        var curve = new CoolingPadSmartCurve(
            cpu,
            CoolingPadSmartCurve.Default.Gpu,
            useHigherTemperatureForBoth: false);

        cpu[0] = new CoolingPadCurvePoint(40, new CoolingDemandLevel(9_000));

        Assert.Equal(new CoolingDemandLevel(2_000), curve.Cpu[0].Demand);
        Assert.IsNotType<CoolingPadCurvePoint[]>(curve.Cpu);
    }

    [Fact]
    public void CoolingPadStatus_NeverTreatsObservedStateAsOwnedOrApplied()
    {
        var observed = new CoolingPadCapabilities(
            DeviceFeatureAccess.ReadOnly,
            DeviceFeatureAccess.ObservedOnly,
            DeviceFeatureAccess.ObservedOnly,
            DeviceFeatureAccess.ObservedOnly,
            SupportsIntentPersistence: true,
            CoolingDemandApiAccess.IntentOnly);
        var status = new CoolingPadStatus(
            CoolingPadConnectionState.PresentExact,
            observed,
            CoolingPadFanIntent.Default,
            new CoolingDemandLevel(5_000),
            AppliedDemand: null,
            ConfiguredFanTarget: new CoolingPadFanTarget(2200),
            ConfiguredFanTargetIsFresh: true,
            Ownership: CoolingPadControlOwnershipState.Unowned,
            CoolingDemandApiEnabled: true,
            ActiveDemandLeases: 1,
            LightingSyncEnabled: false,
            LightingSyncState: CoolingPadLightingSyncState.Unavailable,
            Message: "Observed only.")
        {
            PlannedFanTarget = new CoolingPadFanTarget(1_600),
        };

        Assert.True(status.IsWellFormed);
        Assert.Null(status.ConfirmedActiveFanControlMode);
        Assert.False((status with
        {
            ConfirmedActiveFanControlMode =
                CoolingPadActiveFanControlMode.Automatic,
        }).IsWellFormed);
        Assert.True((status with
        {
            Capabilities = observed with
            {
                ActiveFanModeObservationAccess = DeviceFeatureAccess.ReadOnly,
            },
            ConfirmedActiveFanControlMode =
                CoolingPadActiveFanControlMode.Automatic,
        }).IsWellFormed);
        Assert.False((status with
        {
            Ownership = CoolingPadControlOwnershipState.Owned,
        }).IsWellFormed);
        Assert.False((status with
        {
            AppliedDemand = CoolingDemandLevel.Maximum,
        }).IsWellFormed);
        Assert.False((status with
        {
            PlannedFanTarget = new CoolingPadFanTarget(2_200),
        }).IsWellFormed);
        Assert.False((status with
        {
            ConfiguredFanTarget = new CoolingPadFanTarget(2200),
            Capabilities = observed with
            {
                FanObservationAccess = DeviceFeatureAccess.ObservedOnly,
            },
        }).IsWellFormed);
        Assert.False((status with
        {
            CoolingDemandApiEnabled = false,
        }).IsWellFormed);
        Assert.False((status with
        {
            LightingSyncEnabled = true,
            LightingSyncState = CoolingPadLightingSyncState.Mirroring,
        }).IsWellFormed);
        Assert.True((status with
        {
            Capabilities = observed with
            {
                LightingSyncAccess = DeviceFeatureAccess.ReadWrite,
            },
            LightingSyncEnabled = true,
            LightingSyncState = CoolingPadLightingSyncState.WaitingForFrame,
        }).IsWellFormed);
        Assert.True((status with
        {
            Capabilities = observed with
            {
                LightingSyncAccess = DeviceFeatureAccess.ReadWrite,
            },
            LightingSyncEnabled = false,
            LightingSyncState = CoolingPadLightingSyncState.Faulted,
            Message = "Handback is unconfirmed.",
        }).IsWellFormed);
        Assert.True((status with
        {
            Capabilities = observed with
            {
                LightingSyncAccess = DeviceFeatureAccess.ReadWrite,
            },
            LightingSyncEnabled = false,
            LightingSyncState = CoolingPadLightingSyncState.Faulted,
            LightingRecoveryRequired = true,
            Message = "Manual preset recovery is required.",
        }).IsWellFormed);
        Assert.False((status with
        {
            LightingSyncEnabled = false,
            LightingSyncState = CoolingPadLightingSyncState.Disabled,
            LightingRecoveryRequired = true,
        }).IsWellFormed);

        CoolingPadStatus staleAfterFailure = status with
        {
            Connection = CoolingPadConnectionState.ObservationFailed,
            ConfiguredFanTargetIsFresh = false,
        };
        Assert.True(staleAfterFailure.IsWellFormed);
        Assert.False((staleAfterFailure with
        {
            Connection = CoolingPadConnectionState.Absent,
        }).IsWellFormed);

        CoolingPadCapabilities readWrite = observed with
        {
            FanControlAccess = DeviceFeatureAccess.ReadWrite,
            ActiveFanModeObservationAccess = DeviceFeatureAccess.ReadOnly,
            CoolingDemandApiAccess = CoolingDemandApiAccess.AppliedControl,
        };
        CoolingPadStatus owned = status with
        {
            Capabilities = readWrite,
            Ownership = CoolingPadControlOwnershipState.Owned,
            AppliedDemand = new CoolingDemandLevel(5_000),
            ConfirmedActiveFanControlMode =
                CoolingPadActiveFanControlMode.Fixed,
        };
        Assert.True(owned.IsWellFormed);
        Assert.False((owned with
        {
            Connection = CoolingPadConnectionState.Absent,
            ConfiguredFanTarget = null,
            ConfiguredFanTargetIsFresh = false,
        }).IsWellFormed);
        Assert.False((owned with
        {
            Ownership = CoolingPadControlOwnershipState.Unowned,
        }).IsWellFormed);
    }

    [Fact]
    public void CoolingDemandApiAccess_DistinguishesPlanningFromAppliedControl()
    {
        var intentOnly = new CoolingPadCapabilities(
            DeviceFeatureAccess.ReadOnly,
            DeviceFeatureAccess.ObservedOnly,
            DeviceFeatureAccess.ObservedOnly,
            DeviceFeatureAccess.ObservedOnly,
            SupportsIntentPersistence: true,
            CoolingDemandApiAccess.IntentOnly);
        CoolingPadCapabilities invalidApplied = intentOnly with
        {
            CoolingDemandApiAccess = CoolingDemandApiAccess.AppliedControl,
        };
        CoolingPadCapabilities applied = invalidApplied with
        {
            FanControlAccess = DeviceFeatureAccess.ReadWrite,
        };

        Assert.True(intentOnly.IsWellFormed);
        Assert.False(invalidApplied.IsWellFormed);
        Assert.True(applied.IsWellFormed);
    }
}
