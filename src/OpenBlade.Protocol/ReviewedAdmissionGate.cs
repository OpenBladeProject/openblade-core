namespace OpenBlade.Protocol;

internal sealed record AdmissionEvidenceReference(
    string Artifact,
    string Sha256)
{
    internal bool IsWellFormed =>
        !string.IsNullOrWhiteSpace(Artifact)
        && Sha256 is { Length: 64 }
        && Sha256.All(static character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');
}

internal sealed record ReviewedAdmissionGate(
    bool Validated,
    AdmissionEvidenceReference? Evidence)
{
    internal bool IsWellFormed =>
        Validated
            ? Evidence is { IsWellFormed: true }
            : Evidence is null;

    internal static bool AllWellFormed(
        params ReviewedAdmissionGate[] gates) =>
        gates.All(static gate => gate.IsWellFormed);
}
