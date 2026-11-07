using System.Text.Json.Serialization;

namespace OpenBlade.Contracts;

public enum FanStopMode { Automatic, Stop }

public enum FanStopState { Unavailable, Inactive, Stopped, Automatic, RecoveryRequired, Starting, ReturningToAutomatic }

public enum FanStopUnavailableReason { None, UnsupportedDevice, RecoveryNotValidated, GuardianUnavailable }

/// <summary>Separate SO6120 ownership state; ordinary fan settings remain saved.
/// Automatic is reported only after an independently confirmed owned handback.
/// RecoveryRequired never authorizes an Auto retry.</summary>
public sealed record FanStopStatus(
    FanStopMode RequestedMode = FanStopMode.Automatic,
    FanStopState State = FanStopState.Unavailable,
    FanStopUnavailableReason UnavailableReason = FanStopUnavailableReason.UnsupportedDevice,
    bool CanStop = false,
    bool CanReturnToAutomatic = false,
    FanControlSettings? NormalFanControl = null,
    string? Message = null);

public sealed record SetFanStopModeRequest(
    int Version, Guid RequestId, [property: JsonRequired] FanStopMode Mode) : ServiceRequest(Version, RequestId);
