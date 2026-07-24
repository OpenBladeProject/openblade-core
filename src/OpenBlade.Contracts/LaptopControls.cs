namespace OpenBlade.Contracts;

/// <summary>
/// Individually applied controls whose setters are verified for the exact
/// target laptop. A control may additionally expose typed readback.
/// </summary>
public enum OneShotLaptopControl
{
    StartupAnimation,
    FunctionKeyPrimary,
    GamingMode,
    MicrophoneMuteIndicator,
}

public enum FunctionKeyPrimaryMode
{
    FunctionKeys,
    MultimediaKeys,
}

public sealed record ApplyOneShotLaptopControl(
    OneShotLaptopControl Control,
    bool? Enabled = null,
    FunctionKeyPrimaryMode? FunctionKeyMode = null)
{
    public bool IsWellFormed() => Control switch
    {
        OneShotLaptopControl.StartupAnimation
            or OneShotLaptopControl.GamingMode
            or OneShotLaptopControl.MicrophoneMuteIndicator =>
            Enabled.HasValue && FunctionKeyMode is null,
        OneShotLaptopControl.FunctionKeyPrimary =>
            Enabled is null
            && FunctionKeyMode is { } mode
            && Enum.IsDefined(mode),
        _ => false,
    };
}
