using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

/// <summary>
/// Closed, typed production lighting surface. Raw Razer commands and matrix
/// buffers never cross the protocol boundary.
/// </summary>
public interface IProductionLightingPort : IAsyncDisposable
{
    DeviceCapabilities Capabilities { get; }

    LightingRendererOwnershipState OwnershipState { get; }

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

    Task<LightingSettings?> ReadStateAsync(CancellationToken cancellationToken);

    Task<LogoLightingMode?> ReadLogoStateAsync(CancellationToken cancellationToken) =>
        Task.FromResult<LogoLightingMode?>(null);

    Task SetLightingAsync(
        LightingSettings settings,
        CancellationToken cancellationToken);

    /// <summary>
    /// Changes brightness by the firmware-native step and returns the resulting
    /// UI percentage. Implementations may use a cached raw level, but must
    /// invalidate it when the write outcome is not confirmed.
    /// </summary>
    Task<byte> StepBrightnessAsync(
        KeyboardBrightnessDirection direction,
        CancellationToken cancellationToken) => Task.FromException<byte>(
            new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "Keyboard brightness stepping is unavailable."));

    Task PulseReactiveKeyAsync(
        ReactiveKeyPulse pulse,
        CancellationToken cancellationToken);

    Task PushAmbientLightingFrameAsync(
        AmbientLightingFrame frame,
        CancellationToken cancellationToken) => Task.FromException(
            new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "Ambient Awareness input is unavailable."));

    Task SetPlaybackMuteIndicatorAsync(
        bool muted,
        CancellationToken cancellationToken) => Task.FromException(
            new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "Playback mute indication is unavailable."));

    /// <summary>
    /// Stops and disposes the live renderer, returning its final ownership state
    /// after the render stream has settled and before ownership is relinquished.
    /// </summary>
    Task<LightingRendererOwnershipState> InvalidateAsync();
}

public interface IProductionLightingPortFactory
{
    DeviceCapabilities Capabilities { get; }

    Task<HidSessionResult<IProductionLightingPort>> OpenAsync(
        CancellationToken cancellationToken);
}
