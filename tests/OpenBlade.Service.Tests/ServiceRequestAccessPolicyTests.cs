using System.Security.Principal;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ServiceRequestAccessPolicyTests
{
    private static readonly SecurityIdentifier UserSid = new(
        "S-1-5-21-1-2-3-1001");

    [Fact]
    public void ActiveInteractiveSession_CanReadAndMutate()
    {
        ServiceClientContext client = Client(sessionId: 4);
        var authority = new FixedSessionAuthority(4);

        Assert.Null(ServiceRequestAccessPolicy.GetRejection(
            new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
            client,
            authority));
        Assert.Null(ServiceRequestAccessPolicy.GetRejection(
            new SetFullChargeOverrideRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                Enabled: true),
            client,
            authority));
    }

    [Fact]
    public void BackgroundSession_CannotReadOrMutate()
    {
        ServiceClientContext client = Client(sessionId: 3);
        var authority = new FixedSessionAuthority(4);

        Assert.NotNull(ServiceRequestAccessPolicy.GetRejection(
            new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
            client,
            authority));
        Assert.NotNull(ServiceRequestAccessPolicy.GetRejection(
            new SetFullChargeOverrideRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                Enabled: true),
            client,
            authority));
    }

    [Fact]
    public void Shutdown_RequiresAdministrator()
    {
        var authority = new FixedSessionAuthority(4);
        var request = new ShutdownOpenBladeRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid());

        Assert.NotNull(ServiceRequestAccessPolicy.GetRejection(
            request,
            Client(sessionId: 4, administrator: false),
            authority));
        Assert.Null(ServiceRequestAccessPolicy.GetRejection(
            request,
            Client(sessionId: 4, administrator: true),
            authority));
    }

    [Fact]
    public void NonInteractiveToken_IsRejected()
    {
        ServiceClientContext client = Client(sessionId: 4) with
        {
            LogonKind = ServiceClientLogonKind.Other,
        };

        Assert.Equal(
            SessionControlAccess.NotInteractive,
            ServiceRequestAccessPolicy.GetAccess(
                client,
                new FixedSessionAuthority(4)));
    }

    private static ServiceClientContext Client(
        int sessionId,
        bool administrator = false) => new(
            UserSid,
            sessionId,
            administrator,
            ServiceClientLogonKind.Interactive);

    private sealed class FixedSessionAuthority(int? sessionId)
        : IInteractiveSessionAuthority
    {
        public int? GetControlSessionId() => sessionId;
    }
}
