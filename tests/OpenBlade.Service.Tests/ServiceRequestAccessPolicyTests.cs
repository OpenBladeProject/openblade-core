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
    public void BackgroundSession_CanReadButCannotMutate()
    {
        ServiceClientContext client = Client(sessionId: 3);
        var authority = new FixedSessionAuthority(4);

        Assert.Null(ServiceRequestAccessPolicy.GetRejection(
            new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
            client,
            authority));
        Assert.Null(ServiceRequestAccessPolicy.GetRejection(
            new SubscribeStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
            client,
            authority));
        Assert.NotNull(ServiceRequestAccessPolicy.GetRejection(
            new ObserveStateRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                IncludeLiveReadings: true),
            client,
            authority));
        Assert.NotNull(ServiceRequestAccessPolicy.GetRejection(
            new ExportDiagnosticsRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                LiveReadingsEnabled: false),
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
    public void RemoteInteractiveSession_IsReadOnlyEvenWhenSessionIdMatchesConsole()
    {
        ServiceClientContext client = Client(sessionId: 4) with
        {
            LogonKind = ServiceClientLogonKind.RemoteInteractive,
        };
        var authority = new FixedSessionAuthority(4);

        Assert.Equal(
            SessionControlAccess.InactiveSession,
            ServiceRequestAccessPolicy.GetAccess(client, authority));
        Assert.Null(ServiceRequestAccessPolicy.GetRejection(
            new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
            client,
            authority));
        Assert.NotNull(ServiceRequestAccessPolicy.GetRejection(
            new ObserveStateRequest(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                IncludeLiveReadings: true),
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
    public void ThermalExternalDriftValidation_RequiresActiveLocalAdministrator()
    {
        var authority = new FixedSessionAuthority(4);
        var request = new ValidateThermalExternalDriftRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            Guid.NewGuid(),
            new string('A', 40),
            new string('B', 64),
            new string('C', 64),
            new string('D', 64));

        Assert.Null(ServiceRequestAccessPolicy.GetRejection(
            request,
            Client(sessionId: 4, administrator: true),
            authority));
        Assert.NotNull(ServiceRequestAccessPolicy.GetRejection(
            request,
            Client(sessionId: 4, administrator: false),
            authority));
        Assert.NotNull(ServiceRequestAccessPolicy.GetRejection(
            request,
            Client(sessionId: 3, administrator: true),
            authority));
        Assert.NotNull(ServiceRequestAccessPolicy.GetRejection(
            request,
            new ServiceClientContext(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                SessionId: 0,
                IsAdministrator: true,
                ServiceClientLogonKind.LocalSystem),
            authority));
    }

    [Fact]
    public void FirmwareCompatibilityOverride_AllowsActiveStandardUserOnly()
    {
        var request = new SetFirmwareCompatibilityOverrideRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            Enabled: true);

        Assert.Null(ServiceRequestAccessPolicy.GetRejection(
            request,
            Client(sessionId: 4, administrator: false),
            new FixedSessionAuthority(4)));
        Assert.NotNull(ServiceRequestAccessPolicy.GetRejection(
            request,
            Client(sessionId: 3, administrator: false),
            new FixedSessionAuthority(4)));
    }

    [Fact]
    public void ExperimentalFeaturesConsent_AllowsActiveStandardUserOnly()
    {
        var request = new SetExperimentalFeaturesRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            Enabled: true);

        Assert.Null(ServiceRequestAccessPolicy.GetRejection(
            request,
            Client(sessionId: 4, administrator: false),
            new FixedSessionAuthority(4)));
        Assert.NotNull(ServiceRequestAccessPolicy.GetRejection(
            request,
            Client(sessionId: 3, administrator: false),
            new FixedSessionAuthority(4)));
    }

    [Fact]
    public void DisplayBrightnessHidUsage_AllowsOnlyActiveLocalInteractiveSession()
    {
        var request = new SetDisplayBrightnessHidUsageRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            DisplayBrightnessHidUsage.Decrement);

        Assert.Null(ServiceRequestAccessPolicy.GetRejection(
            request,
            Client(sessionId: 4),
            new FixedSessionAuthority(4)));
        Assert.NotNull(ServiceRequestAccessPolicy.GetRejection(
            request,
            Client(sessionId: 3),
            new FixedSessionAuthority(4)));
        Assert.NotNull(ServiceRequestAccessPolicy.GetRejection(
            request,
            Client(sessionId: 4) with
            {
                LogonKind = ServiceClientLogonKind.RemoteInteractive,
            },
            new FixedSessionAuthority(4)));
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
        Assert.NotNull(ServiceRequestAccessPolicy.GetRejection(
            new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()),
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
