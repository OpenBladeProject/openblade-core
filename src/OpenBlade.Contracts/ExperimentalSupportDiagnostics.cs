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

public sealed record ExperimentalFeatureEvidence(
    string Feature,
    string Basis,
    IReadOnlyList<string> Sources);

public sealed record DiagnosticExperimentalSupport(
    string? ModelNumber,
    string? EvidenceSource,
    bool Detected,
    bool WritesPaused,
    IReadOnlyList<ExperimentalControlObservation> Observations)
{
    public const int MaxObservations = 32;

    public string EvidenceLevel { get; init; } = "UpstreamDerived";

    // Additive diagnostic metadata. Older archives omit this list.
    public IReadOnlyList<ExperimentalFeatureEvidence> FeatureEvidence { get; init; } = [];


    public static DiagnosticExperimentalSupport NotDetected { get; } = new(
        null, null, false, false, [])
    {
        EvidenceLevel = "None",
    };
}
