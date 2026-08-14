using System.Text.Json.Serialization;

namespace OpenBlade.Session;

public static class SessionGpuIpc
{
    public const int ProtocolVersion = 1;
    public const int MaximumApps = 128;
    public const int MaximumMessageBytes = 64 * 1024;
    public const string PipeNamePrefix = "OpenBlade.Session.Gpu.v1";
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "operation")]
[JsonDerivedType(typeof(QueryGpuAppsRequest), "query")]
[JsonDerivedType(typeof(CloseGpuAppsRequest), "close")]
public abstract record SessionGpuRequest(int Version, Guid RequestId);

public sealed record QueryGpuAppsRequest(int Version, Guid RequestId)
    : SessionGpuRequest(Version, RequestId);

public sealed record SessionGpuAppIdentity(
    int ProcessId,
    long StartTimeUtcTicks,
    string Name);

public sealed record CloseGpuAppsRequest(
    int Version,
    Guid RequestId,
    bool Force,
    IReadOnlyList<SessionGpuAppIdentity> Targets)
    : SessionGpuRequest(Version, RequestId);

public sealed record SessionGpuApp(
    int ProcessId,
    long StartTimeUtcTicks,
    string Name,
    string? ExecutablePath,
    double UtilizationPercent,
    bool CanClose,
    string? BlockReason);

public sealed record SessionGpuActionResult(
    int ProcessId,
    long StartTimeUtcTicks,
    bool Succeeded,
    string? Error);

public enum SessionGpuOperationStatus
{
    Succeeded,
    PartiallySucceeded,
    Failed,
}

public sealed record SessionGpuResponse(
    int Version,
    Guid RequestId,
    SessionGpuOperationStatus Status,
    string? Error,
    IReadOnlyList<SessionGpuApp> Apps,
    IReadOnlyList<SessionGpuActionResult> Results);
