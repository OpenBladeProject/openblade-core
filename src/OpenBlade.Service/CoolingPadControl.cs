using OpenBlade.Contracts;

namespace OpenBlade.Service;

internal readonly record struct CoolingPadFanControlImplementationGates(
    bool ExactPlanExecutorImplemented,
    bool OwnershipStateMachineImplemented,
    bool RuntimeCoordinatorImplemented,
    bool LifecycleHandbackImplemented,
    bool HyperBoostInterlockImplemented,
    bool BoundedKnownNotAppliedRetryImplemented,
    bool TransactionExhaustionRotationImplemented,
    bool LeaseSchedulingImplemented,
    bool IndeterminateHyperBoostInterlockImplemented,
    bool ProductionBackendAvailable)
{
    internal bool WriteAvailable =>
        ExactPlanExecutorImplemented
        && OwnershipStateMachineImplemented
        && RuntimeCoordinatorImplemented
        && LifecycleHandbackImplemented
        && HyperBoostInterlockImplemented
        && BoundedKnownNotAppliedRetryImplemented
        && TransactionExhaustionRotationImplemented
        && LeaseSchedulingImplemented
        && IndeterminateHyperBoostInterlockImplemented
        && ProductionBackendAvailable;

    internal static CoolingPadFanControlImplementationGates Current => new(
        ExactPlanExecutorImplemented: true,
        OwnershipStateMachineImplemented: true,
        RuntimeCoordinatorImplemented: true,
        LifecycleHandbackImplemented: true,
        HyperBoostInterlockImplemented: true,
        BoundedKnownNotAppliedRetryImplemented: true,
        TransactionExhaustionRotationImplemented: true,
        LeaseSchedulingImplemented: true,
        IndeterminateHyperBoostInterlockImplemented: true,
        ProductionBackendAvailable: true);
}

public readonly record struct CoolingPadDemandResult(
    CoolingDemandLevel Demand,
    bool FailSafe);

public static class CoolingPadSmartCurveEvaluator
{
    public static CoolingPadDemandResult Calculate(
        CoolingPadSmartCurve curve,
        FanTemperatureReadings temperatures)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (temperatures.CpuCelsius is not int cpu)
        {
            return new CoolingPadDemandResult(CoolingDemandLevel.Maximum, FailSafe: true);
        }

        int gpu;
        if (temperatures.GpuCelsius is int gpuTemperature)
        {
            gpu = gpuTemperature;
        }
        else if (temperatures.GpuInactive)
        {
            gpu = cpu;
        }
        else
        {
            return new CoolingPadDemandResult(CoolingDemandLevel.Maximum, FailSafe: true);
        }

        if (cpu >= 95 || gpu >= 87)
        {
            return new CoolingPadDemandResult(CoolingDemandLevel.Maximum, FailSafe: true);
        }

        if (curve.UseHigherTemperatureForBoth)
        {
            int hotter = Math.Max(cpu, gpu);
            return new CoolingPadDemandResult(
                CoolingDemandLevel.Max(
                    curve.InterpolateCpu(hotter),
                    curve.InterpolateGpu(hotter)),
                FailSafe: false);
        }

        return new CoolingPadDemandResult(
            CoolingDemandLevel.Max(
                curve.InterpolateCpu(cpu),
                curve.InterpolateGpu(gpu)),
            FailSafe: false);
    }
}

public readonly record struct CoolingDemandLeaseOwner(string UserSid, int SessionId)
{
    public bool IsWellFormed =>
        !string.IsNullOrWhiteSpace(UserSid) && SessionId >= 0;
}

public sealed class CoolingDemandLeaseRegistry
{
    public const int MaximumLeaseCount = 32;
    public static readonly TimeSpan MinimumLifetime = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromSeconds(30);

    private readonly TimeProvider timeProvider;
    private readonly object gate = new();
    private readonly Dictionary<LeaseKey, LeaseEntry> leases = [];

    public CoolingDemandLeaseRegistry(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public int Count
    {
        get
        {
            lock (gate)
            {
                RemoveExpiredCore(timeProvider.GetTimestamp());
                return leases.Count;
            }
        }
    }

    public void Set(
        CoolingDemandLeaseOwner owner,
        Guid leaseId,
        CoolingDemandLevel demand,
        TimeSpan lifetime)
    {
        if (!owner.IsWellFormed)
        {
            throw new ArgumentException("The cooling-demand lease owner is invalid.", nameof(owner));
        }
        if (leaseId == Guid.Empty)
        {
            throw new ArgumentException("The cooling-demand lease ID is missing.", nameof(leaseId));
        }
        if (lifetime < MinimumLifetime || lifetime > MaximumLifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        lock (gate)
        {
            long now = timeProvider.GetTimestamp();
            RemoveExpiredCore(now);
            var key = new LeaseKey(owner, leaseId);
            if (!leases.ContainsKey(key) && leases.Count >= MaximumLeaseCount)
            {
                throw new InvalidOperationException("The cooling-demand lease limit was reached.");
            }

            leases[key] = new LeaseEntry(demand, now, lifetime);
        }
    }

    public bool Release(CoolingDemandLeaseOwner owner, Guid leaseId)
    {
        if (!owner.IsWellFormed || leaseId == Guid.Empty)
        {
            return false;
        }

        lock (gate)
        {
            RemoveExpiredCore(timeProvider.GetTimestamp());
            return leases.Remove(new LeaseKey(owner, leaseId));
        }
    }

    public int ReleaseOwner(CoolingDemandLeaseOwner owner)
    {
        lock (gate)
        {
            RemoveExpiredCore(timeProvider.GetTimestamp());
            LeaseKey[] matches = [.. leases.Keys.Where(key => key.Owner == owner)];
            foreach (LeaseKey match in matches)
            {
                leases.Remove(match);
            }
            return matches.Length;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            leases.Clear();
        }
    }

    public CoolingDemandLevel? GetMaximumDemand()
    {
        lock (gate)
        {
            RemoveExpiredCore(timeProvider.GetTimestamp());
            if (leases.Count == 0)
            {
                return null;
            }

            return leases.Values
                .Select(entry => entry.Demand)
                .Aggregate(CoolingDemandLevel.Max);
        }
    }

    private void RemoveExpiredCore(long now)
    {
        LeaseKey[] expired =
        [
            .. leases
                .Where(pair => timeProvider.GetElapsedTime(
                        pair.Value.CreatedAt,
                        now) >= pair.Value.Lifetime)
                .Select(pair => pair.Key),
        ];
        foreach (LeaseKey key in expired)
        {
            leases.Remove(key);
        }
    }

    private readonly record struct LeaseKey(
        CoolingDemandLeaseOwner Owner,
        Guid LeaseId);

    private readonly record struct LeaseEntry(
        CoolingDemandLevel Demand,
        long CreatedAt,
        TimeSpan Lifetime);
}
