using System.Text.Json.Serialization;

namespace OpenBlade.Contracts;

public readonly record struct CoolingPadCurvePoint(
    int TemperatureCelsius,
    CoolingPadConfiguredRpmDemand Demand);

/// <summary>Immutable caller-provided policy; not a vendor preset or write admission.</summary>
public sealed class CoolingPadDemandCurve : IEquatable<CoolingPadDemandCurve>
{
    [JsonConstructor]
    public CoolingPadDemandCurve(IReadOnlyList<CoolingPadCurvePoint> points)
        : this((IEnumerable<CoolingPadCurvePoint>)points) { }

    public CoolingPadDemandCurve(IEnumerable<CoolingPadCurvePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        CoolingPadCurvePoint[] copy = points.Take(11).ToArray();
        if (copy.Length is < 2 or > 10)
        {
            throw new ArgumentException("A curve requires two through ten points.", nameof(points));
        }
        for (int i = 0; i < copy.Length; i++)
        {
            CoolingPadCurvePoint point = copy[i];
            if (point.TemperatureCelsius is < 0 or > 115
                || !point.Demand.IsWellFormed
                || i > 0 && (point.TemperatureCelsius <= copy[i - 1].TemperatureCelsius
                    || point.Demand.Rpm < copy[i - 1].Demand.Rpm))
            {
                throw new ArgumentException("Invalid temperature or demand curve.", nameof(points));
            }
        }
        Points = Array.AsReadOnly(copy);
    }

    public IReadOnlyList<CoolingPadCurvePoint> Points { get; }
    public bool Equals(CoolingPadDemandCurve? other) =>
        other is not null && Points.SequenceEqual(other.Points);
    public override bool Equals(object? obj) => Equals(obj as CoolingPadDemandCurve);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (CoolingPadCurvePoint point in Points) { hash.Add(point); }
        return hash.ToHashCode();
    }
}

public enum CoolingPadControlMode
{
    Stop,
    FixedDemand,
    HostCurve,
}

/// <summary>Transient requested intent. Stop submits no firmware handback.</summary>
public sealed record CoolingPadControlSettings
{
    public CoolingPadControlSettings(
        CoolingPadControlMode mode = CoolingPadControlMode.Stop,
        CoolingPadConfiguredRpmDemand? fixedDemand = null,
        CoolingPadDemandCurve? cpuCurve = null,
        CoolingPadDemandCurve? gpuCurve = null)
    {
        bool valid = mode switch
        {
            CoolingPadControlMode.Stop => fixedDemand is null && cpuCurve is null && gpuCurve is null,
            CoolingPadControlMode.FixedDemand => fixedDemand is { IsWellFormed: true }
                && cpuCurve is null && gpuCurve is null,
            CoolingPadControlMode.HostCurve => fixedDemand is null && cpuCurve is not null && gpuCurve is not null,
            _ => false,
        };
        if (!valid) { throw new ArgumentException("Cooling-pad settings do not match the requested mode.", nameof(mode)); }
        Mode = mode;
        FixedDemand = fixedDemand;
        CpuCurve = cpuCurve;
        GpuCurve = gpuCurve;
    }

    public CoolingPadControlMode Mode { get; }
    public CoolingPadConfiguredRpmDemand? FixedDemand { get; }
    public CoolingPadDemandCurve? CpuCurve { get; }
    public CoolingPadDemandCurve? GpuCurve { get; }
    public static CoolingPadControlSettings Stopped { get; } = new();
    public static CoolingPadControlSettings Default => Stopped;
}

public enum CoolingPadControlState
{
    Stopped,
    Running,
    Paused,
    Unavailable,
}

public enum CoolingPadControlPauseReason
{
    None,
    Unsupported,
    AdmissionClosed,
    DeviceUnavailable,
    ControllerConflict,
    HyperBoostActive,
    TelemetryUnavailable,
    StaleTelemetry,
    SubmissionUnknown,
    SessionChanged,
    PowerTransition,
    ExplicitResumeRequired,
}

/// <summary>Requested, planned and historical acknowledged demand remain distinct from measured speed.</summary>
public sealed record CoolingPadControlStatus(
    CoolingPadControlSettings Requested,
    CoolingPadControlState State,
    CoolingPadControlPauseReason PauseReason,
    CoolingPadConfiguredRpmDemand? PlannedDemand = null,
    CoolingPadConfiguredRpmDemand? LastAcknowledgedDemand = null)
{
    public DeviceFeatureAccess Access { get; init; } = DeviceFeatureAccess.ObservedOnly;
    public CoolingPadConnectionState Connection { get; init; } = CoolingPadConnectionState.Unavailable;
    public bool ResumeRequired { get; init; }
    public bool SubmissionStateUnknown { get; init; }
    public bool MeasuredRpmReadbackSupported => false;

    internal bool IsWellFormed => Requested is not null
        && Enum.IsDefined(State) && Enum.IsDefined(PauseReason)
        && Enum.IsDefined(Access) && Enum.IsDefined(Connection)
        && (PlannedDemand is null || PlannedDemand.Value.IsWellFormed)
        && (LastAcknowledgedDemand is null || LastAcknowledgedDemand.Value.IsWellFormed);
}
