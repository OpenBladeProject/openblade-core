using System.Security.Principal;

namespace OpenBlade.Session;

public static class SessionBatteryIpc
{
    public const int ProtocolVersion = 1;
    public const int MaximumMessageBytes = 256 * 1024;
    public const int MaximumErrorCharacters = 1024;
    public const string PipeNameSuffix = ".Battery.v1";
}

public static class SessionBatteryPipeEndpoint
{
    public static SessionAgentPipeEndpoint Create(
        SecurityIdentifier userSid,
        int sessionId)
    {
        SessionAgentPipeEndpoint session = SessionAgentPipeEndpoint.Create(userSid, sessionId);
        return new SessionAgentPipeEndpoint(session.PipeName + SessionBatteryIpc.PipeNameSuffix);
    }
}

public sealed record SessionBatteryRequest(int Version, Guid RequestId);

public enum SessionBatteryOperationStatus
{
    Succeeded,
    Unavailable,
    Failed,
}

public sealed record SessionBatteryResponse(
    int Version,
    Guid RequestId,
    SessionBatteryOperationStatus Status,
    string? Error,
    WindowsBatteryReport? Report);
