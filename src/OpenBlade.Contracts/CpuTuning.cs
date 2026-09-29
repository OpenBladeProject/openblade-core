namespace OpenBlade.Contracts;

public enum CpuTuningTarget
{
    FirmwareNextBoot,
    LiveNow,
}

public enum CpuTuningPendingBoot
{
    Unknown,
    Requested,
    None,
}

public enum CpuTuningBackendAvailability
{
    Unknown,
    Available,
    MissingDependency,
    Unavailable,
    Failed,
}

public sealed record CpuTuningBackendStatus(
    DeviceFeatureAccess Access,
    CpuTuningBackendAvailability Availability)
{
    public bool IsWellFormed => Enum.IsDefined(Access) && Enum.IsDefined(Availability);

    public bool CanWrite => IsWellFormed
        && Access == DeviceFeatureAccess.ReadWrite
        && Availability == CpuTuningBackendAvailability.Available;
}

public sealed record CpuCurveOptimizerSettings(bool Enabled, int Value)
{
    // Remember the editor value while disabled; zero is the effective setting.
    public int EffectiveValue => Enabled ? Value : 0;

    public bool IsWellFormed => Value is >= -30 and <= -1;
}

public sealed record CpuTuningSettings(
    int? PowerLimitWatts,
    CpuCurveOptimizerSettings? CurveOptimizer)
{
    // Null leaves the corresponding feature unmanaged, including old profiles.
    public bool IsConfigured => PowerLimitWatts.HasValue || CurveOptimizer is not null;

    public bool IsWellFormed =>
        (PowerLimitWatts is null || PowerLimitWatts is >= 45 and <= 95)
        && (CurveOptimizer is null || CurveOptimizer.IsWellFormed);
}

public enum CpuCurveOptimizerMode
{
    AllCore,
    PerCore,
}

public sealed record CpuCoreCurveOptimizerValue(int CoreIndex, int Value)
{
    public bool IsWellFormed => CoreIndex is >= 0 and < CpuCurveOptimizerState.MaximumCoreCount
        && CpuCurveOptimizerState.IsBaselineValue(Value);
}

public sealed record CpuCurveOptimizerState(
    CpuCurveOptimizerMode Mode,
    int? AllCoreValue,
    IReadOnlyList<CpuCoreCurveOptimizerValue> CoreValues)
{
    public const int MaximumCoreCount = 64;

    // Firmware has unsigned 16-bit magnitudes. Restoration must retain prior
    // positive values and magnitudes beyond the narrower negative editor range.
    public static bool IsBaselineValue(int value) => value is >= -65535 and <= 65535;

    public bool IsWellFormed => Enum.IsDefined(Mode)
        && CoreValues is not null
        && (Mode == CpuCurveOptimizerMode.AllCore
            ? AllCoreValue is { } value && IsBaselineValue(value) && CoreValues.Count == 0
            : AllCoreValue is null && AreCoreValuesWellFormed(CoreValues));

    public static bool AreCoreValuesWellFormed(IReadOnlyList<CpuCoreCurveOptimizerValue>? values)
    {
        if (values is null || values.Count is < 1 or > MaximumCoreCount)
        {
            return false;
        }

        // Preserve physical identifiers even when disabled cores leave gaps.
        // The backend separately checks completeness against fresh topology.
        return values.All(value => value is not null && value.IsWellFormed)
            && values.Select(value => value.CoreIndex).Distinct().Count() == values.Count;
    }
}

// This is decoded saved configuration, never proof of the running SMU state.
// A service restoration journal separately retains the complete raw payload,
// including flags, inactive CO fields and other coupled power-limit fields.
public sealed record CpuSavedConfiguration(
    uint PowerLimitMilliwatts,
    byte Policy,
    byte PowerMode,
    CpuCurveOptimizerState? CurveOptimizer)
{
    // Only decoded, observed flag combinations establish custom enablement.
    public bool? CustomPowerLimitEnabled => (Policy, PowerMode) switch
    {
        (2, 2) => true,
        (2, 0xFF) => false,
        _ => null,
    };

    public bool IsWellFormed => CurveOptimizer is null or { IsWellFormed: true };
}

public sealed record CpuLivePowerLimits(
    double? EffectiveWatts,
    uint? Message32Milliwatts,
    uint? Message33Milliwatts)
{
    // The runtime setter changes two distinct SMU fields. One measured scalar
    // is insufficient authority to restore both fields to their prior values.
    public bool HasRestoreBaseline => Message32Milliwatts.HasValue && Message33Milliwatts.HasValue;

    public bool IsWellFormed =>
        (EffectiveWatts is null
            || double.IsFinite(EffectiveWatts.Value) && EffectiveWatts is >= 0 and <= 10000)
        && (EffectiveWatts.HasValue || Message32Milliwatts.HasValue || Message33Milliwatts.HasValue);
}

public sealed record CpuLiveState(
    CpuLivePowerLimits PowerLimits,
    IReadOnlyList<CpuCoreCurveOptimizerValue> CoreValues)
{
    public bool IsWellFormed => PowerLimits is { IsWellFormed: true }
        && CpuCurveOptimizerState.AreCoreValuesWellFormed(CoreValues)
        && CoreValues.All(value => value.Value is >= short.MinValue and <= short.MaxValue);

    // Null means mixed or invalid, never an average or the first core's value.
    public int? UniformCurveOptimizerValue => IsWellFormed
        && CoreValues.All(value => value.Value == CoreValues[0].Value)
            ? CoreValues[0].Value
            : null;
}

public sealed record CpuTuningStatus(
    CpuSavedConfiguration? SavedConfiguration,
    CpuLiveState? LiveState,
    CpuTuningBackendStatus Firmware,
    CpuTuningBackendStatus Live,
    CpuTuningPendingBoot PendingBoot,
    bool RestorePending)
{
    public bool IsWellFormed =>
        (SavedConfiguration is null || SavedConfiguration.IsWellFormed)
        && (LiveState is null || LiveState.IsWellFormed)
        && Firmware is { IsWellFormed: true }
        && Live is { IsWellFormed: true }
        && Enum.IsDefined(PendingBoot);
}
