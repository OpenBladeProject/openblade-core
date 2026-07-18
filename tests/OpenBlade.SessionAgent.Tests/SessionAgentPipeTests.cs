using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using OpenBlade.Session;
using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SessionAgentPipeTests
{
    [Fact]
    public void Endpoint_IsStableForSameUserAndContainsNoRawSid()
    {
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User!;

        SessionAgentPipeEndpoint first = SessionAgentPipeEndpoint.Create(sid);
        SessionAgentPipeEndpoint second = SessionAgentPipeEndpoint.Create(sid);

        Assert.Equal(first, second);
        Assert.StartsWith(SessionDisplayIpc.PipeNamePrefix + ".", first.PipeName);
        Assert.DoesNotContain(sid.Value, first.PipeName, StringComparison.Ordinal);
    }

    [Fact]
    public void Security_IsProtectedAndAllowsOnlyCurrentUser()
    {
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User!;

        PipeSecurity security = SessionAgentPipeSecurity.Create(sid);
        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            typeof(SecurityIdentifier));
        PipeAccessRule rule = Assert.Single(rules.Cast<PipeAccessRule>());

        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(sid, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(PipeAccessRights.FullControl, rule.PipeAccessRights);
    }

    [Fact]
    public async Task PipeOwnership_IsAtomicAndReleasedOnDispose()
    {
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User!;
        var endpoint = new SessionAgentPipeEndpoint(
            $"{SessionDisplayIpc.PipeNamePrefix}.Tests.{Guid.NewGuid():N}");

        Assert.True(SessionAgentPipeOwner.TryAcquire(endpoint, sid, out SessionAgentPipeOwner? first));
        Assert.False(SessionAgentPipeOwner.TryAcquire(endpoint, sid, out SessionAgentPipeOwner? duplicate));
        Assert.Null(duplicate);
        await first!.DisposeAsync();

        Assert.True(SessionAgentPipeOwner.TryAcquire(endpoint, sid, out SessionAgentPipeOwner? replacement));
        await replacement!.DisposeAsync();
    }
}
