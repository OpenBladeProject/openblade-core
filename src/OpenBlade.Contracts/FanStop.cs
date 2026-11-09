using System.Text.Json.Serialization;

namespace OpenBlade.Contracts;

public enum FanStopMode { Automatic, Stop }

public enum FanStopState { Unavailable, Inactive, Stopped, Automatic, RecoveryRequired, Starting, ReturningToAutomatic }

public enum FanStopUnavailableReason { None, UnsupportedDevice, RecoveryNotValidated, GuardianUnavailable }

public enum FanStopEndMode { Auto, Custom }

public enum FanStopEndReason { None, ThermalThreshold, SafetyCondition, PowerChanged, SessionChanged, CallerRequest }

public enum FanStopAutomationState { Disabled, Waiting, Paused, Stopped }

public enum FanStopAutomationPauseReason { None, ManualAuto, ModeChanged, SessionChanged, PowerChanged, SafetyCondition, AttemptFailed, Restarted }

/// <summary>User policy, distinct from the transient ownership of a Stop operation.</summary>
public sealed record FanStopSettings
{
    public const int MinimumTemperatureCelsius = 10;
    public const int MaximumTemperatureCelsius = 100;
    public const int MaximumSecondsAboveLimit = 300;
    public const int MaximumSecondsBelowLimits = 3600;
    public const int TemperatureWarningCelsius = 60;
    public const int DurationWarningSeconds = 10;

    public FanStopEndMode EndMode { get; init; } = FanStopEndMode.Auto;
    public int CpuLimitCelsius { get; init; } = 55;
    public int GpuLimitCelsius { get; init; } = 55;
    public int CpuImmediateCutoffCelsius { get; init; } = 65;
    public int GpuImmediateCutoffCelsius { get; init; } = 65;
    // Retain the serialized name when migrating the former continuous timer.
    // Custom now uses this duration as its trailing, time-weighted window.
    public int SecondsAboveLimit { get; init; } = 5;
    public bool AutomaticEnabled { get; init; }
    public int AutomaticCpuBelowCelsius { get; init; } = 50;
    public int AutomaticGpuBelowCelsius { get; init; } = 50;
    public int SecondsBelowLimits { get; init; } = 30;

    public static FanStopSettings Default { get; } = new();

    public static bool IsWellFormed(FanStopSettings? value) => value is not null
        && Enum.IsDefined(value.EndMode)
        && ValidTemperature(value.CpuLimitCelsius) && ValidTemperature(value.GpuLimitCelsius)
        && ValidTemperature(value.CpuImmediateCutoffCelsius) && ValidTemperature(value.GpuImmediateCutoffCelsius)
        && value.CpuImmediateCutoffCelsius >= value.CpuLimitCelsius
        && value.GpuImmediateCutoffCelsius >= value.GpuLimitCelsius
        && ValidTemperature(value.AutomaticCpuBelowCelsius) && ValidTemperature(value.AutomaticGpuBelowCelsius)
        && value.SecondsAboveLimit is >= 1 and <= MaximumSecondsAboveLimit
        && value.SecondsBelowLimits is >= 1 and <= MaximumSecondsBelowLimits
        && (!value.AutomaticEnabled
            || value.AutomaticCpuBelowCelsius < (value.EndMode == FanStopEndMode.Auto ? 55 : value.CpuLimitCelsius)
                && (value.EndMode == FanStopEndMode.Auto || value.AutomaticGpuBelowCelsius < value.GpuLimitCelsius));

    private static bool ValidTemperature(int value) => value is >= MinimumTemperatureCelsius and <= MaximumTemperatureCelsius;
}

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
    string? Message = null)
{
    public FanStopSettings Settings { get; init; } = FanStopSettings.Default;
    public FanStopAutomationState AutomationState { get; init; }
    public FanStopAutomationPauseReason AutomationPauseReason { get; init; }
}

public sealed record SetFanStopModeRequest(
    int Version, Guid RequestId, [property: JsonRequired] FanStopMode Mode) : ServiceRequest(Version, RequestId);

public sealed record SetFanStopSettingsRequest(
    int Version, Guid RequestId, [property: JsonRequired] FanStopSettings Settings) : ServiceRequest(Version, RequestId);

public sealed record ResumeAutomaticFanStopRequest(int Version, Guid RequestId) : ServiceRequest(Version, RequestId);
