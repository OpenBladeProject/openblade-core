using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service;

public sealed record LightingRendererInvalidationResult(
    LightingRendererOwnershipState Ownership,
    DriverModeRestoreOutcome DriverModeRestoreOutcome);

public interface IVerifiedBladeDeviceProvider
{
    FirmwareCompatibilityStatus FirmwareCompatibility { get; }

    bool RequiresUntestedFirmwareOverride { get; }

    bool IsExperimentalDevice => false;

    string? ExperimentalSupportSource => null;

    void SetAllowUntestedFirmware(bool enabled);

    bool IsExperimentalWritesPaused => false;

    DeviceCapabilities? ExperimentalCapabilities => null;

    Task<ConfirmedDeviceState> ResumeExperimentalWritesAsync(
        Func<ConfirmedDeviceState, bool> acceptObservedState,
        Func<bool> stillAuthorized,
        CancellationToken cancellationToken) =>
        Task.FromException<ConfirmedDeviceState>(new CapabilityUnavailableException(
            "This provider has no paused experimental controls."));

    DiagnosticExperimentalSupport GetExperimentalSupportDiagnostics() =>
        DiagnosticExperimentalSupport.NotDetected;

    void SetLightingRefreshRate(LightingRefreshRate refreshRate)
    {
    }

    bool TryGetVerifiedDevice(out IBladeDevice device);

    DiagnosticDeviceVerification GetCachedDeviceVerificationDiagnostics() =>
        DiagnosticDeviceVerification.NotObserved;

    bool TryGetRecentlyVerifiedReadOnlyDevice(out IBladeDevice device) =>
        TryGetVerifiedDevice(out device);

    bool TryGetOwnedKeyTriggeredLightingDevice(out IBladeDevice device) =>
        TryGetVerifiedDevice(out device);

    bool TryGetOwnedAmbientLightingDevice(out IBladeDevice device) =>
        TryGetVerifiedDevice(out device);

    Task<LightingRendererOwnershipState> RelinquishLightingRendererAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(LightingRendererOwnershipState.None);

    Task<LightingLowPowerOffResult?> TurnOffRetainedLightingForLowPowerAsync(
        Func<bool> stillSuspending,
        CancellationToken cancellationToken) =>
        Task.FromResult<LightingLowPowerOffResult?>(null);

    Task<bool?> ReleaseRetainedDriverModeForLowPowerAsync(
        Func<bool> stillSuspending,
        CancellationToken cancellationToken) =>
        Task.FromResult<bool?>(null);

    Task<LightingRendererInvalidationResult> InvalidateLightingRendererAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(new LightingRendererInvalidationResult(
            LightingRendererOwnershipState.None,
            DriverModeRestoreOutcome.NotOwned));

    Task<LightingRendererInvalidationResult> InvalidateLightingRendererForLowPowerAsync(
        CancellationToken cancellationToken) =>
        Task.FromResult(new LightingRendererInvalidationResult(
            LightingRendererOwnershipState.None,
            DriverModeRestoreOutcome.NotOwned));

    Task<LightingRendererInvalidationResult> InvalidateLightingRendererForLowPowerAsync(
        Func<bool> stillRetiring,
        CancellationToken cancellationToken) => stillRetiring()
            ? InvalidateLightingRendererForLowPowerAsync(cancellationToken)
            : Task.FromResult(new LightingRendererInvalidationResult(
                LightingRendererOwnershipState.None, DriverModeRestoreOutcome.NotOwned));
}
