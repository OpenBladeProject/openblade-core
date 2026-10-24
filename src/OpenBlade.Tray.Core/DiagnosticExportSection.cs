using OpenBlade.Contracts;

namespace OpenBlade.Tray;

internal enum DiagnosticExportStatus
{
    Included,
    NotObserved,
    NotRequested,
    Partial,
    Failed,
}

internal sealed record DiagnosticExportSection(
    string Name,
    DiagnosticExportStatus Status,
    int IncludedItems = 0,
    IReadOnlyList<DiagnosticFailure>? Failures = null);
