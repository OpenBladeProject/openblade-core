using OpenBlade.Contracts;
using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed partial class ServiceReactiveLightingLoopTests
{
    [Fact]
    public async Task Blade18DriverObservationUsesHostIntentWithoutFirmwareDriverMode()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        await using var loop = new ServiceReactiveLightingLoop(client, factory);
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);
        ServiceStatusSnapshot initial = Status(LightingRendererOwnershipState.Static);
        ServiceStatusSnapshot admitted = initial with
        {
            Firmware = new FirmwareVersions("2.09", "1", "Unavailable", "Unavailable", "RZ09-0484"),
            KeyboardDriverModeActive = null,
            FunctionKeyPrimary = null,
            Capabilities = initial.Capabilities! with
            {
                HostManagedLaptopControls = new HashSet<OneShotLaptopControl>
                {
                    OneShotLaptopControl.FunctionKeyPrimary,
                },
            },
        };
        client.Publish(admitted);
        await WaitUntilAsync(() => factory.Last?.IsBlade18DriverFunctionRowAdmitted() == true);
        Assert.False(factory.Last!.ReadDriverModeObservation().AllowsFunctionRowInterception);
        Assert.Equal(FunctionKeyPrimaryMode.FunctionKeys, factory.Last.ReadFunctionKeyPrimaryMode());
        client.Publish(admitted with
        {
            Revision = 2,
            Profiles = admitted.Profiles with
            {
                PluggedIn = admitted.Profiles.PluggedIn with
                {
                    LaptopBehavior = new LaptopBehaviorProfileSettings(null, FunctionKeyPrimaryMode.MultimediaKeys, null),
                },
            },
        });
        await WaitUntilAsync(() => factory.Last.ReadFunctionKeyPrimaryMode() == FunctionKeyPrimaryMode.MultimediaKeys);
        client.Publish(admitted with { Revision = 3, ControlAccess = SessionControlAccess.InactiveSession });
        await WaitUntilAsync(() => !factory.Last.IsBlade18DriverFunctionRowAdmitted());
        client.Publish(admitted with { Revision = 4 });
        await WaitUntilAsync(() => factory.Last.IsBlade18DriverFunctionRowAdmitted());
        client.Reconnect();
        await WaitUntilAsync(() => !factory.Last.IsBlade18DriverFunctionRowAdmitted());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }
}
