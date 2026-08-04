using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace OpenBlade.Service;

public enum ServiceClientLogonKind
{
    Other,
    Interactive,
    RemoteInteractive,
    LocalSystem,
}

public sealed record ServiceClientContext(
    SecurityIdentifier UserSid,
    int SessionId,
    bool IsAdministrator,
    ServiceClientLogonKind LogonKind)
{
    public bool IsInteractive => LogonKind is
        ServiceClientLogonKind.Interactive or ServiceClientLogonKind.RemoteInteractive;

    public bool IsLocalSystem => LogonKind == ServiceClientLogonKind.LocalSystem;

    internal static ServiceClientContext TrustedTestClient { get; } = new(
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        SessionId: 1,
        IsAdministrator: true,
        ServiceClientLogonKind.Interactive);
}

public interface IServiceClientContextResolver
{
    ServiceClientContext Resolve(NamedPipeServerStream pipe);
}

public sealed class WindowsServiceClientContextResolver : IServiceClientContextResolver
{
    public ServiceClientContext Resolve(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        if (!pipe.IsConnected)
        {
            throw new InvalidOperationException("The client pipe is not connected.");
        }

        ServiceClientContext? context = null;
        pipe.RunAsClient(() => context = ReadImpersonatedContext());
        return context
            ?? throw new UnauthorizedAccessException("The pipe client identity is unavailable.");
    }

    private static ServiceClientContext ReadImpersonatedContext()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        SecurityIdentifier userSid = identity.User
            ?? throw new UnauthorizedAccessException("The pipe client SID is unavailable.");
        int sessionId = ReadSessionId(identity);
        bool isLocalSystem = userSid.IsWellKnown(WellKnownSidType.LocalSystemSid);
        bool isRemoteInteractive = IsInGroup(
            identity,
            new SecurityIdentifier("S-1-5-14"));
        bool isInteractive = IsInGroup(identity, WellKnownSidType.InteractiveSid);
        ServiceClientLogonKind logonKind = isLocalSystem
            ? ServiceClientLogonKind.LocalSystem
            : isRemoteInteractive
                ? ServiceClientLogonKind.RemoteInteractive
                : isInteractive
                    ? ServiceClientLogonKind.Interactive
                    : ServiceClientLogonKind.Other;

        return new ServiceClientContext(
            userSid,
            sessionId,
            new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),
            logonKind);
    }

    private static bool IsInGroup(WindowsIdentity identity, WellKnownSidType sidType)
    {
        return IsInGroup(identity, new SecurityIdentifier(sidType, null));
    }

    private static bool IsInGroup(
        WindowsIdentity identity,
        SecurityIdentifier expected) => identity.Groups?.Contains(expected) == true;

    private static int ReadSessionId(WindowsIdentity identity)
    {
        int value = 0;
        int length = sizeof(int);
        if (!GetTokenInformation(
                identity.AccessToken.DangerousGetHandle(),
                TokenInformationClass.TokenSessionId,
                ref value,
                length,
                out _))
        {
            throw new UnauthorizedAccessException(
                "The pipe client session ID is unavailable.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        }

        return value;
    }

    private enum TokenInformationClass
    {
        TokenSessionId = 12,
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        TokenInformationClass tokenInformationClass,
        ref int tokenInformation,
        int tokenInformationLength,
        out int returnLength);
}
