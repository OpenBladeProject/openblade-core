namespace OpenBlade.SessionAgent;

public enum KeyboardDriverModeKnowledge
{
    Unknown,
    Inactive,
    Active,
}

/// <summary>
/// Session-local knowledge of firmware Driver mode. The timestamp uses the
/// monotonic Stopwatch clock and records the last authoritative confirmation.
/// Unknown observations deliberately retain that timestamp for diagnostics.
/// </summary>
public readonly record struct KeyboardDriverModeObservation(
    KeyboardDriverModeKnowledge Knowledge,
    long LastConfirmedTimestamp)
{
    public static KeyboardDriverModeObservation Unknown =>
        new(KeyboardDriverModeKnowledge.Unknown, 0);

    public bool AllowsFunctionRowInterception =>
        Knowledge == KeyboardDriverModeKnowledge.Active;
}
