using OpenBlade.Contracts;
using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed partial class ServiceReactiveLightingLoopTests
{
    [Fact]
    public async Task HostGamingMode_UsesOptedInProfilePolicyAndStopsOnAdmissionLoss()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        await using var loop = new ServiceReactiveLightingLoop(client, factory);
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);
        ServiceStatusSnapshot initial = Status(LightingRendererOwnershipState.Static);
        ServiceStatusSnapshot enabled = initial with
        {
            Capabilities = initial.Capabilities! with
            {
                HostManagedLaptopControls = new HashSet<OneShotLaptopControl>
                {
                    OneShotLaptopControl.GamingMode,
                },
            },
            GamingModeEnabled = null,
            Profiles = initial.Profiles with
            {
                PluggedIn = initial.Profiles.PluggedIn with
                {
                    LaptopBehavior = new LaptopBehaviorProfileSettings(null, null, true)
                    {
                        GamingModeKeyPolicy = new GamingModeKeyPolicy(true, true),
                    },
                },
            },
        };
        client.Publish(enabled);
        await WaitUntilAsync(() => factory.Last?.ReadGamingModeKeyPolicy()
            == new GamingModeKeyPolicy(true, true));

        client.Publish(enabled with { Revision = 2, WritesBlocked = true });
        await WaitUntilAsync(() => factory.Last?.ReadGamingModeKeyPolicy()
            == GamingModeKeyPolicy.Disabled);

        client.Publish(enabled with { Revision = 3 });
        await WaitUntilAsync(() => factory.Last?.ReadGamingModeKeyPolicy()
            == new GamingModeKeyPolicy(true, true));
        client.Publish(enabled with { Revision = 4, ActivePowerSource = PowerSource.Battery });
        await WaitUntilAsync(() => factory.Last?.ReadGamingModeKeyPolicy()
            == GamingModeKeyPolicy.Disabled);

        client.Publish(enabled with { Revision = 5 });
        await WaitUntilAsync(() => factory.Last?.ReadGamingModeKeyPolicy()
            == new GamingModeKeyPolicy(true, true));
        client.Publish(enabled with
        {
            Revision = 6,
            Capabilities = DeviceCapabilities.Unavailable(),
        });
        await WaitUntilAsync(() => factory.Last?.ReadGamingModeKeyPolicy()
            == GamingModeKeyPolicy.Disabled);

        client.Publish(enabled with { Revision = 7 });
        await WaitUntilAsync(() => factory.Last?.ReadGamingModeKeyPolicy()
            == new GamingModeKeyPolicy(true, true));
        client.Publish(enabled with
        {
            Revision = 8,
            ControlAccess = SessionControlAccess.InactiveSession,
        });
        await WaitUntilAsync(() => factory.Last?.ReadGamingModeKeyPolicy()
            == GamingModeKeyPolicy.Disabled);

        client.Publish(enabled with { Revision = 9 });
        await WaitUntilAsync(() => factory.Last?.ReadGamingModeKeyPolicy()
            == new GamingModeKeyPolicy(true, true));
        client.Reconnect();
        await WaitUntilAsync(() => factory.Last?.ReadGamingModeKeyPolicy()
            == GamingModeKeyPolicy.Disabled);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    [Fact]
    public async Task HostGamingMode_EnablingWithoutKeyPolicyDoesNotBlockKeys()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        await using var loop = new ServiceReactiveLightingLoop(client, factory);
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);
        ServiceStatusSnapshot initial = Status(LightingRendererOwnershipState.Static);
        client.Publish(initial with
        {
            Capabilities = initial.Capabilities! with
            {
                HostManagedLaptopControls = new HashSet<OneShotLaptopControl>
                {
                    OneShotLaptopControl.GamingMode,
                },
            },
            GamingModeEnabled = null,
            Profiles = initial.Profiles with
            {
                PluggedIn = initial.Profiles.PluggedIn with
                {
                    LaptopBehavior = new LaptopBehaviorProfileSettings(null, null, true),
                },
            },
        });
        await WaitUntilAsync(() => client.DeliveryCount >= 1);
        Assert.Equal(GamingModeKeyPolicy.Disabled, factory.Last?.ReadGamingModeKeyPolicy());

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }
}
