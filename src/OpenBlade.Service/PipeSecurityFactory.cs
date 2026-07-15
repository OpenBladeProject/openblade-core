using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OpenBlade.Service;

public static class PipeSecurityFactory
{
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
