using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class CoolingPadControlTests
{
    [Fact]
    public void FanControlImplementation_IsCompleteWhileEvidenceAdmissionRemainsSeparate()
    {
        CoolingPadFanControlImplementationGates gates =
            CoolingPadFanControlImplementationGates.Current;

        Assert.True(gates.ExactPlanExecutorImplemented);
        Assert.True(gates.OwnershipStateMachineImplemented);
        Assert.True(gates.RuntimeCoordinatorImplemented);
        Assert.True(gates.LifecycleHandbackImplemented);
        Assert.True(gates.HyperBoostInterlockImplemented);
        Assert.True(gates.BoundedKnownNotAppliedRetryImplemented);
        Assert.True(gates.TransactionExhaustionRotationImplemented);
        Assert.True(gates.LeaseSchedulingImplemented);
        Assert.True(gates.IndeterminateHyperBoostInterlockImplemented);
        Assert.True(gates.ProductionBackendAvailable);
        Assert.True(gates.WriteAvailable);
    }

    [Fact]
    public void SmartCurve_UsesHigherCpuOrGpuDemand()
    {
        CoolingPadDemandResult result = CoolingPadSmartCurveEvaluator.Calculate(
            CoolingPadSmartCurve.Default,
            new FanTemperatureReadings(65, 75));

        Assert.False(result.FailSafe);
        Assert.Equal(6_150, result.Demand.BasisPoints);
    }

    [Theory]
    [InlineData(null, 60, false)]
    [InlineData(60, null, false)]
    [InlineData(95, 60, false)]
    [InlineData(60, 87, false)]
    public void SmartCurve_FailsSafeForMissingOrEmergencySensors(
        int? cpu,
        int? gpu,
        bool gpuInactive)
    {
        CoolingPadDemandResult result = CoolingPadSmartCurveEvaluator.Calculate(
            CoolingPadSmartCurve.Default,
            new FanTemperatureReadings(cpu, gpu, gpuInactive));

        Assert.True(result.FailSafe);
        Assert.Equal(CoolingDemandLevel.Maximum, result.Demand);
    }

    [Fact]
    public void SmartCurve_UsesCpuForInactiveGpu()
    {
        CoolingPadDemandResult result = CoolingPadSmartCurveEvaluator.Calculate(
            CoolingPadSmartCurve.Default,
            new FanTemperatureReadings(65, null, GpuInactive: true));

        Assert.False(result.FailSafe);
        Assert.Equal(5_450, result.Demand.BasisPoints);
    }

    [Fact]
    public void Leases_AggregateByMaximumAndExpireAtBoundary()
    {
        var time = new ManualTimeProvider();
        var registry = new CoolingDemandLeaseRegistry(time);
        var firstOwner = new CoolingDemandLeaseOwner("S-1-5-21-1", 1);
        var secondOwner = new CoolingDemandLeaseOwner("S-1-5-21-2", 2);

        registry.Set(firstOwner, Guid.NewGuid(), new CoolingDemandLevel(2_000), TimeSpan.FromSeconds(3));
        registry.Set(secondOwner, Guid.NewGuid(), new CoolingDemandLevel(8_000), TimeSpan.FromSeconds(2));

        Assert.Equal(8_000, registry.GetMaximumDemand()!.Value.BasisPoints);
        Assert.Equal(2, registry.Count);

        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(2_000, registry.GetMaximumDemand()!.Value.BasisPoints);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void LeaseRenewal_ReplacesDemandAndLifetime()
    {
        var time = new ManualTimeProvider();
        var registry = new CoolingDemandLeaseRegistry(time);
        var owner = new CoolingDemandLeaseOwner("S-1-5-21-1", 1);
        Guid leaseId = Guid.NewGuid();

        registry.Set(owner, leaseId, new CoolingDemandLevel(2_000), TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromMilliseconds(500));
        registry.Set(owner, leaseId, new CoolingDemandLevel(7_000), TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromMilliseconds(1_500));

        Assert.Equal(7_000, registry.GetMaximumDemand()!.Value.BasisPoints);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void LeaseExpiry_UsesMonotonicTimeWhenWallClockMovesBackward()
    {
        var time = new ManualTimeProvider();
        var registry = new CoolingDemandLeaseRegistry(time);
        var owner = new CoolingDemandLeaseOwner("S-1-5-21-1", 1);
        registry.Set(
            owner,
            Guid.NewGuid(),
            new CoolingDemandLevel(4_000),
            TimeSpan.FromSeconds(2));

        time.ShiftUtc(TimeSpan.FromHours(-1));
        time.AdvanceTimestamp(TimeSpan.FromSeconds(2));

        Assert.Null(registry.GetMaximumDemand());
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void LeaseRelease_IsScopedToOwner()
    {
        var registry = new CoolingDemandLeaseRegistry();
        var first = new CoolingDemandLeaseOwner("S-1-5-21-1", 1);
        var second = new CoolingDemandLeaseOwner("S-1-5-21-2", 2);
        Guid leaseId = Guid.NewGuid();
        registry.Set(first, leaseId, new CoolingDemandLevel(4_000), TimeSpan.FromSeconds(2));
        registry.Set(second, leaseId, new CoolingDemandLevel(8_000), TimeSpan.FromSeconds(2));

        Assert.True(registry.Release(first, leaseId));
        Assert.False(registry.Release(first, leaseId));
        Assert.Equal(8_000, registry.GetMaximumDemand()!.Value.BasisPoints);
    }

    [Fact]
    public void LeaseRegistry_EnforcesBoundedCapacity()
    {
        var registry = new CoolingDemandLeaseRegistry();
        var owner = new CoolingDemandLeaseOwner("S-1-5-21-1", 1);
        for (int index = 0; index < CoolingDemandLeaseRegistry.MaximumLeaseCount; index++)
        {
            registry.Set(
                owner,
                Guid.NewGuid(),
                new CoolingDemandLevel(index),
                TimeSpan.FromSeconds(2));
        }

        Assert.Throws<InvalidOperationException>(() => registry.Set(
            owner,
            Guid.NewGuid(),
            CoolingDemandLevel.Maximum,
            TimeSpan.FromSeconds(2)));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-08-09T12:00:00Z");
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => now;

        public override long GetTimestamp() => timestamp;

        public void Advance(TimeSpan value)
        {
            now += value;
            AdvanceTimestamp(value);
        }

        public void AdvanceTimestamp(TimeSpan value) => timestamp += value.Ticks;

        public void ShiftUtc(TimeSpan value) => now += value;
    }
}
