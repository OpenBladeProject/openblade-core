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

    [Fact]
    public async Task DuplicateStartupDoesNotRegisterPersistentLogging()
    {
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User!;
        var endpoint = new SessionAgentPipeEndpoint(
            $"{SessionDisplayIpc.PipeNamePrefix}.Tests.{Guid.NewGuid():N}");
        Assert.True(
            SessionAgentPipeOwner.TryAcquire(
                endpoint,
                sid,
                out SessionAgentPipeOwner? existing));
        int registrationCount = 0;

        bool acquired = Program.TryAcquireStartupOwnership(
            endpoint,
            sid,
            () =>
            {
                registrationCount++;
                return new CancellationTokenSource();
            },
            out SessionAgentPipeOwner? duplicate,
            out IDisposable? traceRegistration);

        Assert.False(acquired);
        Assert.Equal(0, registrationCount);
        Assert.Null(duplicate);
        Assert.Null(traceRegistration);
        await existing!.DisposeAsync();
    }

    [Fact]
    public async Task SuccessfulStartupRegistersLoggingAfterOwnership()
    {
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User!;
        var endpoint = new SessionAgentPipeEndpoint(
            $"{SessionDisplayIpc.PipeNamePrefix}.Tests.{Guid.NewGuid():N}");
        int registrationCount = 0;

        Assert.True(Program.TryAcquireStartupOwnership(
            endpoint,
            sid,
            () =>
            {
                registrationCount++;
                return new CancellationTokenSource();
            },
            out SessionAgentPipeOwner? owner,
            out IDisposable? traceRegistration));
        Assert.Equal(1, registrationCount);

        traceRegistration!.Dispose();
        await owner!.DisposeAsync();
    }

    [Fact]
    public async Task FailedLoggingRegistrationReleasesStartupOwnership()
    {
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User!;
        var endpoint = new SessionAgentPipeEndpoint(
            $"{SessionDisplayIpc.PipeNamePrefix}.Tests.{Guid.NewGuid():N}");

        Assert.Throws<InvalidOperationException>(() =>
            Program.TryAcquireStartupOwnership(
                endpoint,
                sid,
                static () => throw new InvalidOperationException("registration failed"),
                out _,
                out _));

        Assert.True(
            SessionAgentPipeOwner.TryAcquire(
                endpoint,
                sid,
                out SessionAgentPipeOwner? replacement));
        await replacement!.DisposeAsync();
    }
}
