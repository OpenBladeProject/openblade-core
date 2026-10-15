using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal readonly record struct HyperBoostTemperatureObservation(
    int? CpuCelsius,
    int? GpuCelsius,
    bool GpuInactive = false);

internal interface IHyperBoostThermalTelemetrySource
{
    ValueTask<HyperBoostTemperatureObservation> ReadAsync(
        CancellationToken cancellationToken);
}

internal enum HyperBoostExternalWorkloadObservation
{
    RunningExact,
    NotRunning,
    IdentityMismatch,
    ObservationFailed,
}

internal readonly record struct HyperBoostExternalWorkloadIdentity(
    string ExecutableName,
    string ExecutableSha256)
{
    internal bool IsWellFormed =>
        ExecutableName is { Length: > 0 and <= 128 }
        && Path.GetFileName(ExecutableName) == ExecutableName
        && ExecutableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && ReviewedHyperBoostThermalPlan.IsSha256ForReviewedPlan(
            ExecutableSha256);
}

internal interface IHyperBoostExternalWorkloadSource
{
    ValueTask<HyperBoostExternalWorkloadObservation> ObserveAsync(
        ReviewedHyperBoostThermalPlan plan,
        CancellationToken cancellationToken);
}

internal enum HyperBoostSustainedValidationFailure
{
    None,
    CoolingPadPresent,
    CoolingPadAbsenceObservationFailed,
    AdapterPowerReadFailed,
    FullPowerAdapterRequired,
    ThermalStateReadFailed,
    ThermalStatePairMismatch,
    HyperBoostStateLost,
    CpuTemperatureUnavailable,
    CpuTemperatureExceeded,
    GpuTemperatureUnavailable,
    GpuTemperatureExceeded,
    FanRpmReadFailed,
    Fan1BelowMinimum,
    Fan2BelowMinimum,
    ExternalWorkloadNotRunning,
    ExternalWorkloadIdentityMismatch,
    ExternalWorkloadObservationFailed,
    Canceled,
    UnexpectedFailure,
}

internal sealed record HyperBoostThermalValidationSample(
    int SampleNumber,
    TimeSpan Elapsed,
    int CpuCelsius,
    int? GpuCelsius,
    bool GpuInactive,
    int Fan1Rpm,
    int Fan2Rpm);

internal sealed record HyperBoostSustainedValidationResult(
    string PlanId,
    string PlanSha256,
    int PlannedSampleCount,
    int CompletedSampleCount,
    bool CompletedFullDuration,
    bool ContinuousPadAbsenceConfirmed,
    bool FullPowerAdapterMaintained,
    bool HyperBoostStateMaintained,
    bool RequiredSensorsMaintained,
    bool ExternalWorkloadMaintained,
    IReadOnlyList<HyperBoostThermalValidationSample> Samples,
    HyperBoostSustainedValidationFailure Failure)
{
    public bool Success => Failure == HyperBoostSustainedValidationFailure.None
        && CompletedFullDuration
        && CompletedSampleCount == PlannedSampleCount
        && ContinuousPadAbsenceConfirmed
        && FullPowerAdapterMaintained
        && HyperBoostStateMaintained
        && RequiredSensorsMaintained
        && ExternalWorkloadMaintained;
}

internal enum HyperBoostAdapterTransitionAuthorization
{
    Rejected,
    PadAbsentConfirmed,
    CoolingPadPresent,
    CoolingPadAbsenceObservationFailed,
}

internal enum HyperBoostAdapterTransitionValidationFailure
{
    None,
    TransitionNotConfirmed,
    AdapterPowerReadFailed,
    AdapterPowerUnknown,
    ReviewedNonFullPowerNotObserved,
    CoolingPadPresent,
    CoolingPadAbsenceObservationFailed,
    Canceled,
    UnexpectedFailure,
}

internal sealed record HyperBoostAdapterTransitionValidationResult(
    string PlanId,
    string PlanSha256,
    bool TransitionPromptConfirmed,
    bool TransitionTimePadAbsentConfirmed,
    bool FreshAdapterReadCompleted,
    AdapterPowerState InitialAdapterState,
    AdapterPowerState ObservedAdapterState,
    bool TransitionConfirmed,
    HyperBoostAdapterTransitionValidationFailure Failure)
{
    internal bool Success =>
        Failure == HyperBoostAdapterTransitionValidationFailure.None
        && TransitionPromptConfirmed
        && TransitionTimePadAbsentConfirmed
        && FreshAdapterReadCompleted
        && HyperBoostValidationPower.IsAdmittedFullPower(InitialAdapterState)
        && ObservedAdapterState == AdapterPowerState.UsbCPower100W
        && TransitionConfirmed;
}

internal static class HyperBoostValidationPower
{
    internal static bool IsAdmittedFullPower(AdapterPowerState state) =>
        state is AdapterPowerState.FullPower280W
            or AdapterPowerState.FullPower330W
            or AdapterPowerState.SufficientExternal;
}
