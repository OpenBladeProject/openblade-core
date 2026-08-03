using System.Runtime.InteropServices;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public interface IInteractiveSessionAuthority
{
    int? GetControlSessionId();
}

public sealed class WindowsInteractiveSessionAuthority : IInteractiveSessionAuthority
{
    private const int CurrentServerHandle = 0;
    private const int CurrentVersion = 1;
    private const int InvalidSessionId = -1;

    public int? GetControlSessionId()
    {
        uint consoleSession = WTSGetActiveConsoleSessionId();
        int[] activeSessions = EnumerateActiveSessions();
        if (consoleSession != uint.MaxValue
            && activeSessions.Contains(unchecked((int)consoleSession)))
        {
            return unchecked((int)consoleSession);
        }

        return activeSessions.Length == 0
            ? null
            : activeSessions.Min();
    }

    private static int[] EnumerateActiveSessions()
    {
        if (!WTSEnumerateSessionsW(
                (IntPtr)CurrentServerHandle,
                0,
                CurrentVersion,
                out IntPtr buffer,
                out int count))
        {
            return [];
        }

        try
        {
            int size = Marshal.SizeOf<WtsSessionInfo>();
            List<int> active = [];
            for (int index = 0; index < count; index++)
            {
                IntPtr item = IntPtr.Add(buffer, index * size);
                WtsSessionInfo info = Marshal.PtrToStructure<WtsSessionInfo>(item);
                if (info.State == WtsConnectState.Active && info.SessionId != InvalidSessionId)
                {
                    active.Add(info.SessionId);
                }
            }

            return [.. active.Distinct().Order()];
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr WindowStationName;
        public WtsConnectState State;
    }

    private enum WtsConnectState
    {
        Active,
        Connected,
        ConnectQuery,
        Shadow,
        Disconnected,
        Idle,
        Listen,
        Reset,
        Down,
        Init,
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnumerateSessionsW(
        IntPtr serverHandle,
        int reserved,
        int version,
        out IntPtr sessionInfo,
        out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}

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

        int? activeSession = authority.GetControlSessionId();
        return activeSession.HasValue && activeSession.Value == client.SessionId
            ? SessionControlAccess.Active
            : SessionControlAccess.InactiveSession;
    }

    public static string? GetRejection(
        ServiceRequest request,
        ServiceClientContext client,
        IInteractiveSessionAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(request);
        SessionControlAccess access = GetAccess(client, authority);
        if (request is ShutdownOpenBladeRequest)
        {
            return client.IsLocalSystem || client.IsAdministrator
                ? null
                : "This operation requires an administrator.";
        }

        if (request is GetStatusRequest
            or ObserveStateRequest
            or SubscribeStatusRequest
            or ExportDiagnosticsRequest)
        {
            return access switch
            {
                SessionControlAccess.Active => null,
                SessionControlAccess.NotInteractive =>
                    "OpenBlade accepts requests only from an interactive Windows session.",
                _ => "Another Windows session currently controls OpenBlade hardware.",
            };
        }

        return access switch
        {
            SessionControlAccess.Active => null,
            SessionControlAccess.NotInteractive =>
                "OpenBlade accepts control requests only from an interactive Windows session.",
            _ => "Another Windows session currently controls OpenBlade hardware.",
        };
    }
}
