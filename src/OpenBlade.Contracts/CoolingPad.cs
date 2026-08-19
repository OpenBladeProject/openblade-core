using System.Text.Json.Serialization;

namespace OpenBlade.Contracts;

public enum CoolingPadConnectionState
{
    Unavailable,
    Absent,
    PresentExact,
    Ambiguous,
    ObservationFailed,
}

public enum CoolingPadFanMode
{
    Automatic,
    Fixed,
    SmartCurve,
}

public enum CoolingPadActiveFanControlMode
{
    Automatic,
    Fixed,
    HostControlled,
}

public enum CoolingPadControlOwnershipState
{
    Unowned,
    Owned,
    Indeterminate,
}

public enum CoolingDemandApiAccess
{
    Unavailable,
    IntentOnly,
    AppliedControl,
}

public enum CoolingPadLightingSyncState
{
    Unavailable,
    Disabled,
    WaitingForPad,
    Mirroring,
    Suppressed,
    Delegated,
    Faulted,
    WaitingForFrame,
}

public readonly record struct CoolingDemandLevel
{
    public const int MinimumBasisPoints = 0;
    public const int MaximumBasisPoints = 10_000;

    [JsonConstructor]
    public CoolingDemandLevel(int basisPoints)
    {
        if (basisPoints is < MinimumBasisPoints or > MaximumBasisPoints)
        {
            throw new ArgumentOutOfRangeException(
                nameof(basisPoints),
                basisPoints,
                $"Cooling demand must be from {MinimumBasisPoints} to {MaximumBasisPoints} basis points.");
        }

        BasisPoints = basisPoints;
    }

    public int BasisPoints { get; }

    public double Fraction => BasisPoints / (double)MaximumBasisPoints;

    public static CoolingDemandLevel Zero { get; } = new(MinimumBasisPoints);

    public static CoolingDemandLevel Maximum { get; } = new(MaximumBasisPoints);

    public static CoolingDemandLevel FromPercent(int percent)
    {
        if (percent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percent));
        }

        return new CoolingDemandLevel(checked(percent * 100));
    }

    public static CoolingDemandLevel Max(
        CoolingDemandLevel left,
        CoolingDemandLevel right) => left.BasisPoints >= right.BasisPoints
            ? left
            : right;
}

public readonly record struct CoolingPadFanTarget
{
    public const int MinimumRpm = 500;
    public const int MaximumRpm = 3_200;
    public const int StepRpm = 50;

    [JsonConstructor]
    public CoolingPadFanTarget(int rpm)
    {
        if (rpm is < MinimumRpm or > MaximumRpm
            || rpm % StepRpm != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rpm),
                rpm,
                $"Cooling-pad fan target must be from {MinimumRpm} to {MaximumRpm} RPM in {StepRpm}-RPM steps.");
        }

        Rpm = rpm;
    }

    public int Rpm { get; }

    public static CoolingPadFanTarget FromDemand(CoolingDemandLevel demand)
    {
        double unquantized = MaximumRpm * demand.Fraction;
        int quantized = checked((int)Math.Round(
            unquantized / StepRpm,
            MidpointRounding.AwayFromZero)) * StepRpm;
        return new CoolingPadFanTarget(Math.Clamp(
            quantized,
            MinimumRpm,
            MaximumRpm));
    }

    public CoolingDemandLevel ToDemand() => new(checked((int)Math.Round(
        Rpm / (double)MaximumRpm * CoolingDemandLevel.MaximumBasisPoints,
        MidpointRounding.AwayFromZero)));
}

public sealed record CoolingPadCurvePoint
{
    public CoolingPadCurvePoint(
        int temperatureCelsius,
        CoolingDemandLevel demand)
    {
        if (temperatureCelsius is < 20 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(temperatureCelsius),
                temperatureCelsius,
                "Cooling-pad curve temperature must be from 20 to 100 degrees Celsius.");
        }

        TemperatureCelsius = temperatureCelsius;
        Demand = demand;
    }

    public int TemperatureCelsius { get; }

    public CoolingDemandLevel Demand { get; }
}

public sealed class CoolingPadSmartCurve : IEquatable<CoolingPadSmartCurve>
{
    public CoolingPadSmartCurve(
        IReadOnlyList<CoolingPadCurvePoint> cpu,
        IReadOnlyList<CoolingPadCurvePoint> gpu,
        bool useHigherTemperatureForBoth)
    {
        Cpu = ValidateCurve(cpu, nameof(cpu));
        Gpu = ValidateCurve(gpu, nameof(gpu));
        UseHigherTemperatureForBoth = useHigherTemperatureForBoth;
    }

    public IReadOnlyList<CoolingPadCurvePoint> Cpu { get; }

    public IReadOnlyList<CoolingPadCurvePoint> Gpu { get; }

    public bool UseHigherTemperatureForBoth { get; }

    public static CoolingPadSmartCurve Default { get; } = new(
        [
            new(40, new CoolingDemandLevel(3_000)),
            new(90, new CoolingDemandLevel(6_500)),
            new(100, new CoolingDemandLevel(6_500)),
        ],
        [
            new(30, new CoolingDemandLevel(3_000)),
            new(80, new CoolingDemandLevel(6_500)),
            new(90, new CoolingDemandLevel(6_500)),
        ],
        useHigherTemperatureForBoth: false);

    public CoolingDemandLevel InterpolateCpu(int temperatureCelsius) =>
        Interpolate(Cpu, temperatureCelsius);

    public CoolingDemandLevel InterpolateGpu(int temperatureCelsius) =>
        Interpolate(Gpu, temperatureCelsius);

    public bool Equals(CoolingPadSmartCurve? other) =>
        other is not null
        && UseHigherTemperatureForBoth == other.UseHigherTemperatureForBoth
        && Cpu.SequenceEqual(other.Cpu)
        && Gpu.SequenceEqual(other.Gpu);

    public override bool Equals(object? obj) => Equals(obj as CoolingPadSmartCurve);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(UseHigherTemperatureForBoth);
        foreach (CoolingPadCurvePoint point in Cpu)
        {
            hash.Add(point);
        }
        foreach (CoolingPadCurvePoint point in Gpu)
        {
            hash.Add(point);
        }
        return hash.ToHashCode();
    }

    private static IReadOnlyList<CoolingPadCurvePoint> ValidateCurve(
        IReadOnlyList<CoolingPadCurvePoint>? points,
        string parameterName)
    {
        if (points is null || points.Count is < 2 or > 10)
        {
            throw new ArgumentException(
                "A cooling-pad curve must contain from 2 to 10 points.",
                parameterName);
        }

        var copy = points.ToArray();
        for (int index = 0; index < copy.Length; index++)
        {
            ArgumentNullException.ThrowIfNull(copy[index]);
            if (index > 0
                && (copy[index].TemperatureCelsius
                        <= copy[index - 1].TemperatureCelsius
                    || copy[index].Demand.BasisPoints
                        < copy[index - 1].Demand.BasisPoints))
            {
                throw new ArgumentException(
                    "Cooling-pad curve temperatures must increase and demand must not decrease.",
                    parameterName);
            }
        }

        return Array.AsReadOnly(copy);
    }

    private static CoolingDemandLevel Interpolate(
        IReadOnlyList<CoolingPadCurvePoint> points,
        int temperatureCelsius)
    {
        if (temperatureCelsius <= points[0].TemperatureCelsius)
        {
            return points[0].Demand;
        }

        for (int index = 1; index < points.Count; index++)
        {
            CoolingPadCurvePoint upper = points[index];
            if (temperatureCelsius <= upper.TemperatureCelsius)
            {
                CoolingPadCurvePoint lower = points[index - 1];
                double position = (double)(temperatureCelsius
                    - lower.TemperatureCelsius)
                    / (upper.TemperatureCelsius - lower.TemperatureCelsius);
                int interpolated = lower.Demand.BasisPoints
                    + (int)Math.Round(
                        (upper.Demand.BasisPoints - lower.Demand.BasisPoints)
                        * position,
                        MidpointRounding.AwayFromZero);
                return new CoolingDemandLevel(interpolated);
            }
        }

        return points[^1].Demand;
    }
}

public sealed record CoolingPadFanIntent
{
    public CoolingPadFanIntent(
        CoolingPadFanMode mode,
        CoolingDemandLevel fixedDemand,
        CoolingPadSmartCurve smartCurve)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        Mode = mode;
        FixedDemand = fixedDemand;
        SmartCurve = smartCurve ?? throw new ArgumentNullException(nameof(smartCurve));
    }

    public CoolingPadFanMode Mode { get; }

    public CoolingDemandLevel FixedDemand { get; }

    public CoolingPadSmartCurve SmartCurve { get; }

    public static CoolingPadFanIntent Default { get; } = new(
        CoolingPadFanMode.Automatic,
        new CoolingDemandLevel(6_875),
        CoolingPadSmartCurve.Default);

    public bool IsWellFormed => Enum.IsDefined(Mode) && SmartCurve is not null;
}

public sealed record CoolingPadCapabilities(
    DeviceFeatureAccess FanObservationAccess,
    DeviceFeatureAccess FanControlAccess,
    DeviceFeatureAccess LightingAccess,
    DeviceFeatureAccess LightingSyncAccess,
    bool SupportsIntentPersistence,
    CoolingDemandApiAccess CoolingDemandApiAccess)
{
    public DeviceFeatureAccess ActiveFanModeObservationAccess { get; init; } =
        DeviceFeatureAccess.ObservedOnly;

    public bool IsWellFormed =>
        Enum.IsDefined(FanObservationAccess)
        && Enum.IsDefined(FanControlAccess)
        && Enum.IsDefined(LightingAccess)
        && Enum.IsDefined(LightingSyncAccess)
        && Enum.IsDefined(ActiveFanModeObservationAccess)
        && Enum.IsDefined(CoolingDemandApiAccess)
        && (CoolingDemandApiAccess
                != global::OpenBlade.Contracts.CoolingDemandApiAccess.AppliedControl
            || FanControlAccess == DeviceFeatureAccess.ReadWrite);
}

public sealed record CoolingPadStatus(
    CoolingPadConnectionState Connection,
    CoolingPadCapabilities Capabilities,
    CoolingPadFanIntent DesiredFanIntent,
    CoolingDemandLevel? ComputedDemand,
    CoolingDemandLevel? AppliedDemand,
    CoolingPadFanTarget? ConfiguredFanTarget,
    bool ConfiguredFanTargetIsFresh,
    CoolingPadControlOwnershipState Ownership,
    bool CoolingDemandApiEnabled,
    int ActiveDemandLeases,
    bool LightingSyncEnabled,
    CoolingPadLightingSyncState LightingSyncState,
    string Message)
{
    public CoolingPadActiveFanControlMode? ConfirmedActiveFanControlMode { get; init; }

    public bool LightingRecoveryRequired { get; init; }

    /// <summary>
    /// The target selected by OpenBlade's planning calculation. This is not a
    /// submitted command, device readback, physical speed, or ownership proof.
    /// </summary>
    public CoolingPadFanTarget? PlannedFanTarget { get; init; }

    public bool IsWellFormed =>
        Enum.IsDefined(Connection)
        && Capabilities is { IsWellFormed: true }
        && DesiredFanIntent is { IsWellFormed: true }
        && Enum.IsDefined(Ownership)
        && ActiveDemandLeases is >= 0 and <= 32
        && Enum.IsDefined(LightingSyncState)
        && Message is not null
        && (ComputedDemand is null
            ? PlannedFanTarget is null
            : PlannedFanTarget
                == CoolingPadFanTarget.FromDemand(ComputedDemand.Value))
        && (ConfiguredFanTarget is null
            || Connection is CoolingPadConnectionState.PresentExact
                or CoolingPadConnectionState.ObservationFailed
                && Capabilities.FanObservationAccess is (
                    DeviceFeatureAccess.ReadOnly or DeviceFeatureAccess.ReadWrite))
        && (!ConfiguredFanTargetIsFresh
            || (Connection == CoolingPadConnectionState.PresentExact
                && Capabilities.FanObservationAccess is (
                    DeviceFeatureAccess.ReadOnly or DeviceFeatureAccess.ReadWrite)))
        && (ConfirmedActiveFanControlMode is null
            || Enum.IsDefined(ConfirmedActiveFanControlMode.Value)
                && Connection == CoolingPadConnectionState.PresentExact
                && Capabilities.ActiveFanModeObservationAccess is
                    DeviceFeatureAccess.ReadOnly or DeviceFeatureAccess.ReadWrite)
        && (!CoolingDemandApiEnabled
            || Capabilities.CoolingDemandApiAccess
                != global::OpenBlade.Contracts.CoolingDemandApiAccess.Unavailable)
        && (ActiveDemandLeases == 0 || CoolingDemandApiEnabled)
        && (AppliedDemand is null
            || Connection == CoolingPadConnectionState.PresentExact
                && Ownership == CoolingPadControlOwnershipState.Owned
                && Capabilities.FanControlAccess == DeviceFeatureAccess.ReadWrite)
        && (Ownership != CoolingPadControlOwnershipState.Owned
            || Connection == CoolingPadConnectionState.PresentExact
                && Capabilities.FanControlAccess == DeviceFeatureAccess.ReadWrite)
        && (LightingSyncEnabled
            ? Capabilities.LightingSyncAccess == DeviceFeatureAccess.ReadWrite
                && LightingSyncState is not (
                    CoolingPadLightingSyncState.Unavailable
                    or CoolingPadLightingSyncState.Disabled)
            : LightingSyncState is CoolingPadLightingSyncState.Unavailable
                or CoolingPadLightingSyncState.Disabled
                or CoolingPadLightingSyncState.Faulted)
        && (!LightingRecoveryRequired
            || !LightingSyncEnabled
                && LightingSyncState is CoolingPadLightingSyncState.Faulted
                    or CoolingPadLightingSyncState.Unavailable);
}
