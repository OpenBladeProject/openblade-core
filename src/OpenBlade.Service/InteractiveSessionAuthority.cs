using System.Runtime.InteropServices;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public interface IInteractiveSessionAuthority
{
    int? GetControlSessionId();
}

public sealed class WindowsInteractiveSessionAuthority : IInteractiveSessionAuthority
{
    public int? GetControlSessionId()
    {
        uint consoleSession = WTSGetActiveConsoleSessionId();
        return consoleSession == uint.MaxValue
            ? null
            : unchecked((int)consoleSession);
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
}

internal readonly record struct ServiceRequestAccessDecision(
    SessionControlAccess Access,
    string? Rejection);

public static class ServiceRequestAccessPolicy
{
    public static SessionControlAccess GetAccess(
        ServiceClientContext client,
        IInteractiveSessionAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(authority);
        if (client.IsLocalSystem)
        {
            return SessionControlAccess.Active;
        }

        if (!client.IsInteractive)
        {
            return SessionControlAccess.NotInteractive;
        }

        if (client.LogonKind == ServiceClientLogonKind.RemoteInteractive)
        {
            return SessionControlAccess.InactiveSession;
        }

        int? activeSession = authority.GetControlSessionId();
        return activeSession.HasValue && activeSession.Value == client.SessionId
            ? SessionControlAccess.Active
            : SessionControlAccess.InactiveSession;
    }

    public static string? GetRejection(
        ServiceRequest request,
        ServiceClientContext client,
        IInteractiveSessionAuthority authority)
        => Evaluate(request, client, authority).Rejection;

    internal static ServiceRequestAccessDecision Evaluate(
        ServiceRequest request,
        ServiceClientContext client,
        IInteractiveSessionAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request is ShutdownOpenBladeRequest)
        {
            return new ServiceRequestAccessDecision(
                SessionControlAccess.Active,
                client.IsLocalSystem || client.IsAdministrator
                    ? null
                    : "This operation requires an administrator.");
        }

        SessionControlAccess access = GetAccess(client, authority);
        if (request is GetStatusRequest
            or ObserveStateRequest
            or SubscribeStatusRequest)
        {
            return new ServiceRequestAccessDecision(
                access,
                access == SessionControlAccess.NotInteractive
                    ? "OpenBlade accepts requests only from an interactive Windows session."
                    : null);
        }

        return new ServiceRequestAccessDecision(
            access,
            access switch
            {
                SessionControlAccess.Active => null,
                SessionControlAccess.NotInteractive =>
                    "OpenBlade accepts control requests only from an interactive Windows session.",
                _ => "Another Windows session currently controls OpenBlade hardware.",
            });
    }
}
