using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OpenBlade.Service;

public static class PipeSecurityFactory
{
    public static PipeSecurity CreateForInteractiveUsers()
    {
        SecurityIdentifier localSystem = new(WellKnownSidType.LocalSystemSid, null);
        SecurityIdentifier administrators = new(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);
        SecurityIdentifier users = new(WellKnownSidType.BuiltinUsersSid, null);
        SecurityIdentifier network = new(WellKnownSidType.NetworkSid, null);
        PipeSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            network,
            PipeAccessRights.FullControl,
            AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(
            localSystem,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            administrators,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            users,
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        return security;
    }

    public static PipeSecurity Create(SecurityIdentifier userSid)
    {
        SecurityIdentifier localSystem = new(WellKnownSidType.LocalSystemSid, null);
        PipeSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            localSystem,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            userSid,
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        return security;
    }
}
