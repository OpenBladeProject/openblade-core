using System.IO.Pipes;
using System.Security.Principal;
using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Fact]
    public async Task RecoveryRequiredActivation_ReturnsPipeFailureAndNextHealthyRequestSucceeds()
    {
        string machineRoot = Path.Combine(directory, "configuration-pipe-recovery");
        var repository = new UserConfigurationRepository(
            machineRoot, ProfileSetValidator.CreateUnverifiedSafeDefaults());
        var firstSid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var secondSid = new SecurityIdentifier("S-1-5-21-1-2-3-1002");
        await repository.LoadAsync(firstSid, CancellationToken.None);
        await repository.LoadAsync(secondSid, CancellationToken.None);
        TestContext context = CreateContext(
            providerAvailable: true, userConfigurationRepository: repository);
        await context.Runtime.ActivateUserAsync(firstSid, CancellationToken.None);
        ServiceStatusSnapshot before = context.Runtime.GetStatus();
        int setters = context.Device.TotalSetterCalls;
        string recoveryPath = Path.Combine(machineRoot, "users", secondSid.Value,
            "battery-configuration.commit.json.recovery-required");
        await File.WriteAllTextAsync(recoveryPath, "unresolved transaction");
        var firstClient = new ServiceClientContext(firstSid, 4, false, ServiceClientLogonKind.Interactive);
        var resolver = new RecoveryClientResolver(
            new ServiceClientContext(secondSid, 4, false, ServiceClientLogonKind.Interactive));
        var handler = new ControlRequestHandler(context.Runtime, new RecoveryArchiveProvider(),
            NullServiceShutdownSignal.Instance, new RecoverySessionAuthority());
        var hub = new StatusSubscriptionHub(before, TimeProvider.System);
        string pipeName = $"OpenBlade.ConfigurationRecovery.Tests.{Guid.NewGuid():N}";
        var server = new NamedPipeControlServer(pipeName, handler, hub, resolver);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        ServiceResponse rejected = await SendRecoveryStatusRequestAsync(server, pipeName, timeout.Token);

        Assert.False(rejected.Success);
        Assert.Contains("recovery", rejected.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(recoveryPath, rejected.Error, StringComparison.Ordinal);
        Assert.Equal(before.Profiles, context.Runtime.GetStatus().Profiles);
        Assert.Equal(before.ConfirmedState, context.Runtime.GetStatus().ConfirmedState);
        Assert.Equal(setters, context.Device.TotalSetterCalls);
        resolver.Client = firstClient;
        ServiceResponse accepted = await SendRecoveryStatusRequestAsync(server, pipeName, timeout.Token);
        Assert.True(accepted.Success);
        Assert.Equal(before.Profiles, accepted.Status?.Profiles);
        Assert.Equal(setters, context.Device.TotalSetterCalls);
        Assert.Equal("unresolved transaction", await File.ReadAllTextAsync(recoveryPath));
    }

    [Fact]
    public async Task ActivateUserAsync_RecoveryRequiredLoadRetainsActiveIntentAndConfirmedState()
    {
        string machineRoot = Path.Combine(directory, "configuration-recovery");
        var repository = new UserConfigurationRepository(
            machineRoot, ProfileSetValidator.CreateUnverifiedSafeDefaults());
        var firstSid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var secondSid = new SecurityIdentifier("S-1-5-21-1-2-3-1002");
        UserConfigurationContext first = await repository.LoadAsync(firstSid, CancellationToken.None);
        ProfileSet firstProfiles = first.Configuration.Profiles with
        {
            BatteryPolicy = BatteryPolicy.Create(75),
        };
        await first.Store.SaveProfilesAsync(firstProfiles, CancellationToken.None);
        await repository.LoadAsync(secondSid, CancellationToken.None);
        TestContext context = CreateContext(
            providerAvailable: true, userConfigurationRepository: repository);
        await context.Runtime.ActivateUserAsync(firstSid, CancellationToken.None);
        ServiceStatusSnapshot before = context.Runtime.GetStatus();
        Assert.Equal(firstProfiles, before.Profiles);
        Assert.NotNull(before.ConfirmedState);
        int setterCalls = context.Device.TotalSetterCalls;
        string recoveryPath = Path.Combine(machineRoot, "users", secondSid.Value,
            "battery-configuration.commit.json.recovery-required");
        await File.WriteAllTextAsync(recoveryPath, "unresolved transaction");

        await Assert.ThrowsAsync<ConfigurationQuarantinedException>(() =>
            context.Runtime.ActivateUserAsync(secondSid, CancellationToken.None));

        ServiceStatusSnapshot retained = context.Runtime.GetStatus();
        Assert.Equal(before.Profiles, retained.Profiles);
        Assert.Equal(before.DesiredState, retained.DesiredState);
        Assert.Equal(before.ConfirmedState, retained.ConfirmedState);
        Assert.Equal(before.BatteryPolicyOverride, retained.BatteryPolicyOverride);
        Assert.Equal(before.FanControlOverride, retained.FanControlOverride);
        Assert.Equal(before.LightingOverride, retained.LightingOverride);
        Assert.Equal(setterCalls, context.Device.TotalSetterCalls);
        // The failed load did not switch the active user or turn a later A
        // activation into a second hardware application.
        await context.Runtime.ActivateUserAsync(firstSid, CancellationToken.None);
        Assert.Equal(setterCalls, context.Device.TotalSetterCalls);
        Assert.Equal("unresolved transaction", await File.ReadAllTextAsync(recoveryPath));
    }

    private static async Task<ServiceResponse> SendRecoveryStatusRequestAsync(
        NamedPipeControlServer server, string pipeName, CancellationToken token)
    {
        Task serving = server.ServeOnceAsync(token);
        try
        {
            await using var client = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(token);
            var request = new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid());
            await IpcMessageCodec.WriteRequestAsync(client, request, token);
            ServiceResponse response = Assert.IsType<ServiceResponse>(
                await IpcMessageCodec.ReadServerFrameAsync(client, token));
            Assert.Equal(request.RequestId, response.RequestId);
            return response;
        }
        finally
        {
            await serving;
        }
    }

    private sealed class RecoveryClientResolver(ServiceClientContext client) : IServiceClientContextResolver
    {
        public ServiceClientContext Client { get; set; } = client;
        public ServiceClientContext Resolve(NamedPipeServerStream pipe) => Client;
    }

    private sealed class RecoverySessionAuthority : IInteractiveSessionAuthority
    {
        public int? GetControlSessionId() => 4;
    }

    private sealed class RecoveryArchiveProvider : IDiagnosticArchiveProvider
    {
        public Task<DiagnosticArchive> CreateAsync(bool liveReadingsEnabled, CancellationToken token) =>
            throw new InvalidOperationException("The recovery-status test does not export diagnostics.");
    }
}
