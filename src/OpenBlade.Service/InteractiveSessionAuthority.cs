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
    public static bool IsActiveLocalInteractiveClient(
        ServiceClientContext client,
        IInteractiveSessionAuthority authority) =>
        client.LogonKind == ServiceClientLogonKind.Interactive
        && GetAccess(client, authority) == SessionControlAccess.Active;

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

        if (request is ValidateThermalExternalDriftRequest)
        {
            SessionControlAccess validationAccess = GetAccess(client, authority);
            return new ServiceRequestAccessDecision(
                validationAccess,
                !client.IsAdministrator
                    ? "Thermal external-drift validation requires a local administrator."
                    : !IsActiveLocalInteractiveClient(client, authority)
                        ? "Thermal external-drift validation requires the active local interactive session."
                        : null);
        }

        if (request is ValidatePadlessHyperBoostLifecycleRequest)
        {
            SessionControlAccess validationAccess = GetAccess(client, authority);
            return new ServiceRequestAccessDecision(
                validationAccess,
                !client.IsAdministrator
                    ? "Pad-less HyperBoost lifecycle validation requires a local administrator."
                    : validationAccess != SessionControlAccess.Active
                        ? "Pad-less HyperBoost lifecycle validation requires the active local interactive session."
                        : null);
        }

        if (request is SetThermalControlPolicyRequest
            or AcknowledgeThermalPolicyUpgradeDisclosureRequest)
        {
            SessionControlAccess thermalPolicyAccess = GetAccess(client, authority);
            return new ServiceRequestAccessDecision(
                thermalPolicyAccess,
                IsActiveLocalInteractiveClient(client, authority)
                    ? null
                    : "Thermal policy changes require the active local interactive Windows session.");
        }

        SessionControlAccess access = GetAccess(client, authority);
        if (request is GetStatusRequest or SubscribeStatusRequest)
        {
            return new ServiceRequestAccessDecision(
                access,
                access == SessionControlAccess.NotInteractive
                    ? "OpenBlade accepts requests only from an interactive Windows session."
                    : null);
        }

        // Firmware override is intentionally governed by the same single active,
        // local interactive-session boundary as the other hardware controls. It
        // does not relax exact model, SKU, USB identity, or typed-command gates;
        // requiring elevation here would make the tray setting unusable without
        // a separate brokered elevation flow. Service shutdown remains the sole
        // administrator-only request above.
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
