namespace OpenBlade.Contracts;

/// <summary>
/// Controls whose setters are verified for the exact target laptop, but whose
/// current firmware values cannot be queried independently.
/// </summary>
public enum OneShotLaptopControl
{
    StartupAnimation,
    FunctionKeyPrimary,
    GamingMode,
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
        OneShotLaptopControl.StartupAnimation or OneShotLaptopControl.GamingMode =>
            Enabled.HasValue && FunctionKeyMode is null,
        OneShotLaptopControl.FunctionKeyPrimary =>
            Enabled is null
            && FunctionKeyMode is { } mode
            && Enum.IsDefined(mode),
        _ => false,
    };
}
