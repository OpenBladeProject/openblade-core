using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

public enum DriverModeRestoreOutcome
{
    NotOwned,
    ForeignChange,
    Released,
    Indeterminate,
    Failed,
}

public enum LightingLowPowerOffOutcome
{
    ConfirmedOff,
    KnownNotApplied,
    Indeterminate,
}

public sealed record LightingLowPowerOffResult(
    LightingRendererOwnershipState OwnershipBefore,
    LightingLowPowerOffOutcome Outcome,
    string? Message = null);

internal static class OwnedRendererOffObservation
{
    internal static readonly TimeSpan OffConfirmationDelay =
        TimeSpan.FromMilliseconds(750);

    internal static bool RequiresOffConfirmation(
        LightingRendererOwnershipState ownership) =>
        ownership is not LightingRendererOwnershipState.None
            and not LightingRendererOwnershipState.Faulted;
}

/// <summary>
/// Closed, typed production lighting surface. Raw Razer commands and matrix
/// buffers never cross the protocol boundary.
/// </summary>
public interface IProductionLightingPort : IAsyncDisposable
{
    DeviceCapabilities Capabilities { get; }

    LightingRendererOwnershipState OwnershipState { get; }

    DriverModeRestoreOutcome DriverModeRestoreOutcome =>
        DriverModeRestoreOutcome.NotOwned;

    /// <summary>
    /// Verifies that the firmware is in Driver mode and restores it when the
    /// device has fallen back to Normal mode.
    /// </summary>
    Task EnsureDriverModeAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>
    /// Reads whether the firmware is currently in Driver mode. A null result
    /// means the mode is not available from this lighting port.
    /// </summary>
    Task<bool?> ReadDriverModeAsync(CancellationToken cancellationToken) =>
        Task.FromResult<bool?>(null);

    /// <summary>
    /// Restores Driver mode when necessary and returns the confirmed result.
    /// Implementations can combine the ensure and readback into one serialized
    /// transaction sequence instead of querying the firmware twice.
    /// </summary>
    async Task<bool?> MaintainDriverModeAsync(CancellationToken cancellationToken)
    {
        await EnsureDriverModeAsync(cancellationToken).ConfigureAwait(false);
        return await ReadDriverModeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Explicitly returns the firmware to Normal mode before OpenBlade stops.
    /// A false result confirms that Driver mode is no longer active.
    /// </summary>
    Task<bool?> ReleaseDriverModeAsync(CancellationToken cancellationToken) =>
        Task.FromResult<bool?>(null);

    Task<bool?> ReleaseDriverModeForLowPowerAsync(
        Func<bool> stillSuspending,
        CancellationToken cancellationToken) =>
        Task.FromResult<bool?>(null);

    Task<LightingSettings?> ReadStateAsync(CancellationToken cancellationToken);

    Task<LogoLightingMode?> ReadLogoStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult<LogoLightingMode?>(null);

    Task SetLightingAsync(
        LightingSettings settings,
        CancellationToken cancellationToken);

    /// <summary>
    /// Retires the renderer and confirms keyboard brightness zero at an active
    /// suspend boundary. Never rolls back to On, acquires Driver mode, or
    /// changes logo lighting.
    /// </summary>
    Task<LightingLowPowerOffResult> TurnOffForLowPowerAsync(
        Func<bool> stillSuspending,
        CancellationToken cancellationToken) =>
        Task.FromResult(new LightingLowPowerOffResult(
            OwnershipState,
            LightingLowPowerOffOutcome.KnownNotApplied,
            "This lighting port does not support retained suspend cleanup."));

    Task SetLightingRefreshRateAsync(
        LightingRefreshRate refreshRate,
        CancellationToken cancellationToken) =>
        Task.FromException(new NotSupportedException(
            "Selectable lighting refresh rates are unavailable."));

    /// <summary>
    /// Changes brightness by the firmware-native step and returns the resulting
    /// UI percentage. Implementations may use a cached raw level, but must
    /// invalidate it when the write outcome is not confirmed.
    /// </summary>
    Task<byte> StepBrightnessAsync(
        KeyboardBrightnessDirection direction,
        CancellationToken cancellationToken);

    Task PulseReactiveKeyAsync(
        ReactiveKeyPulse pulse,
        CancellationToken cancellationToken);

    Task PushAmbientLightingFrameAsync(
        AmbientLightingFrame frame,
        CancellationToken cancellationToken);

    Task PushUniformLightingFrameAsync(
        UniformLightingFrame frame,
        CancellationToken cancellationToken) =>
        Task.FromException(new CapabilityUnavailableException(
            "Uniform host-rendered lighting is unavailable."));

    Task SetPlaybackMuteIndicatorAsync(
        bool muted,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stops the live renderer and relinquishes effect ownership without
    /// closing the command port or changing keyboard Driver mode.
    /// </summary>
    Task<LightingRendererOwnershipState> RelinquishRendererAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Stops and disposes the live renderer, returning its final ownership state
    /// after the render stream has settled and before ownership is relinquished.
    /// </summary>
    Task<LightingRendererOwnershipState> InvalidateAsync();

    /// <summary>
    /// Retires the port without firmware mutations. This close mode remains in
    /// effect for subsequent disposal; Driver release is a separate guarded step.
    /// </summary>
    Task<LightingRendererOwnershipState> InvalidateForLowPowerAsync() =>
        Task.FromException<LightingRendererOwnershipState>(new NotSupportedException(
            "This port does not support a write-free low-power close."));

    /// <summary>
    /// Disposes the port within its own cleanup deadline. True means the close
    /// task completed; false means cleanup remains safely retained and the
    /// caller must not force-close the handle.
    /// </summary>
    Task<bool> DisposeWithinCleanupDeadlineAsync() =>
        DisposeWithinCleanupDeadlineDefaultAsync(this);

    private static async Task<bool> DisposeWithinCleanupDeadlineDefaultAsync(
        IAsyncDisposable target)
    {
        await target.DisposeAsync().ConfigureAwait(false);
        return true;
    }
}

public interface IProductionLightingPortFactory
{
    DeviceCapabilities Capabilities { get; }

    Task<HidSessionResult<IProductionLightingPort>> OpenAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Opens a port whose Driver-mode writes retain the supplied admission
    /// predicate through native submission and cleanup.
    /// </summary>
    Task<HidSessionResult<IProductionLightingPort>> OpenAsync(
        Func<bool> driverModeWriteAllowed,
        CancellationToken cancellationToken) => Task.FromResult(
            new HidSessionResult<IProductionLightingPort>(null, HidSessionFailureKind.ConflictBlocked));
}
