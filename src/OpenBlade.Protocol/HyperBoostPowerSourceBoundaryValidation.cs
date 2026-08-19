using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal readonly record struct HyperBoostPowerSourceBoundaryAuthorization(
    bool Confirmed,
    CoolingPadPhysicalPresenceObservation CoolingPadObservation);

internal enum HyperBoostPowerSourceBoundaryValidationFailure
{
    None,
    TransitionNotConfirmed,
    DestinationNotReviewed,
    CoolingPadPresent,
    CoolingPadAbsenceObservationFailed,
    AdapterPowerReadFailed,
    DestinationPowerNotObserved,
    ThermalStateReadFailed,
    ThermalStatePairMismatch,
    HyperBoostDidNotExit,
    HyperBoostReappearedAfterExit,
    AutomaticStateNotMaintained,
    ReconnectNotConfirmed,
    ReconnectAdapterPowerReadFailed,
    FullPowerReconnectRequired,
    Canceled,
    UnexpectedFailure,
}

internal sealed record HyperBoostPowerSourceBoundarySample(
    int SampleNumber,
    TimeSpan Elapsed,
    AdapterPowerState AdapterState,
    ThermalModeState ThermalState);

internal sealed record HyperBoostPowerSourceBoundaryValidationResult(
    string PlanId,
    string PlanSha256,
    AdapterPowerState DestinationAdapterState,
    bool TransitionPromptConfirmed,
    bool TransitionTimePadAbsentConfirmed,
    bool DestinationAdapterObserved,
    bool DestinationAdapterMaintained,
    bool CompletedFullObservation,
    bool FirmwareAutoExitConfirmed,
    bool HyperBoostReappearedAfterExit,
    ThermalModeState? FirmwareExitState,
    int RequiredConsecutiveAutomaticSamples,
    int MaximumSampleCount,
    IReadOnlyList<HyperBoostPowerSourceBoundarySample> Samples,
    bool OpenBladeDisableIssuedDuringObservation,
    bool ReconnectPromptConfirmed,
    bool ReconnectTimePadAbsentConfirmed,
    bool FullPowerReconnectConfirmed,
    AdapterPowerState ReconnectedAdapterState,
    bool BaselineReturnedWithoutCleanupWrite,
    int OpenBladeCleanupWriteCount,
    HyperBoostPowerSourceBoundaryValidationFailure Failure)
{
    internal bool ObservationSucceeded =>
        Failure == HyperBoostPowerSourceBoundaryValidationFailure.None
        && TransitionPromptConfirmed
        && TransitionTimePadAbsentConfirmed
        && DestinationAdapterObserved
        && DestinationAdapterMaintained
        && CompletedFullObservation
        && FirmwareAutoExitConfirmed
        && !HyperBoostReappearedAfterExit
        && FirmwareExitState is not null
        && !OpenBladeDisableIssuedDuringObservation;
}

internal interface IHyperBoostPowerSourceBoundaryValidationRunner
{
    Task<PerformanceFanSetterValidationResult> RunAsync(
        AdapterPowerState destinationAdapterState,
        Func<CancellationToken, Task<HyperBoostPowerSourceBoundaryAuthorization>>
            authorizeTransition,
        Func<CancellationToken, Task<HyperBoostPowerSourceBoundaryAuthorization>>
            authorizeReconnect,
        CancellationToken cancellationToken);
}

internal sealed record HyperBoostPowerSourceBoundaryRunRequest(
    AdapterPowerState DestinationAdapterState,
    Func<CancellationToken, Task<HyperBoostPowerSourceBoundaryAuthorization>>
        AuthorizeTransition,
    Func<CancellationToken, Task<HyperBoostPowerSourceBoundaryAuthorization>>
        AuthorizeReconnect);
