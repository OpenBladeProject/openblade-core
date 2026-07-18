using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OpenBlade.SessionAgent;

public static class SessionAgentPipeSecurity
{
    public static PipeSecurity Create(SecurityIdentifier userSid)
    {
        ArgumentNullException.ThrowIfNull(userSid);
        PipeSecurity security = new();
        security.SetOwner(userSid);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            userSid,
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        return security;
    }
}
