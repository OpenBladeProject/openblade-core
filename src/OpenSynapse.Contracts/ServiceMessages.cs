namespace OpenSynapse.Contracts;

public sealed record ServiceRequest(int Version, Guid RequestId, string Operation);

public sealed record ServiceStatusSnapshot(
    bool DeviceVerified,
    bool WritesBlocked,
    IReadOnlyList<string> Conflicts,
    string Message);

public sealed record ServiceResponse(
    int Version,
    Guid RequestId,
    bool Success,
    string? Error,
    ServiceStatusSnapshot? Status);
