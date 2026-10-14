namespace OpenBlade.Contracts;

public sealed record ExperimentalControlObservation(
    DateTimeOffset Timestamp,
    string Operation,
    string Stage,
    string Outcome,
    bool Submitted,
    string? RequestedValue = null,
    string? ConfirmedValue = null,
    string? FailureCategory = null);

public sealed record DiagnosticExperimentalSupport(
    string? ModelNumber,
    string? EvidenceSource,
    bool Detected,
    bool ConsentEnabled,
    IReadOnlyList<ExperimentalControlObservation> Observations)
{
    public const int MaxObservations = 32;

    public string EvidenceLevel { get; init; } = "UpstreamDerived";

    public static DiagnosticExperimentalSupport NotDetected { get; } = new(
        null, null, false, false, [])
    {
        EvidenceLevel = "None",
    };
}
