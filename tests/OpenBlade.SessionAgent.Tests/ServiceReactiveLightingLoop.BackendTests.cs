using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed partial class ServiceReactiveLightingLoopTests
{
    [Fact]
    public async Task DriverHandoffRequiresSourceOrFactoryProofAndAllTrackedFunctionKeysReleased()
    {
        var client = new WatchClient();
        var factory = new RecordingSourceFactory();
        var statuses = new List<SnapTapRuntimeStatus>();
        await using var loop = new ServiceReactiveLightingLoop(client, factory,
            new ServiceReactiveLightingLoopOptions(PublishFunctionRowStatus: statuses.Add));
        Assert.True(loop.CanTransferFunctionRowInput); // factory certifies no previous owner

        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);
        client.Publish(Status(LightingRendererOwnershipState.Static) with
        {
            KeyboardDriverModeActive = true,
        });
        await WaitUntilAsync(() => factory.CreateCount == 1);
        Assert.True(loop.CanTransferFunctionRowInput);

        factory.Last!.HasActiveFunctionRowKeys = true;
        Assert.False(loop.CanTransferFunctionRowInput);
        factory.Last.HasActiveFunctionRowKeys = false;
        Assert.True(loop.CanTransferFunctionRowInput);
        await WaitUntilAsync(() => statuses.Count != 0);
        Assert.All(statuses, status => Assert.Equal(KeyboardInputBackend.WindowsHook, status.Backend));
        int before = statuses.Count;
        loop.InvalidateFunctionRowBackendStatus();
        client.Publish(Status(LightingRendererOwnershipState.Static) with { KeyboardDriverModeActive = true });
        await WaitUntilAsync(() => statuses.Count > before);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await loop.DisposeAsync();
        Assert.True(loop.CanTransferFunctionRowInput); // completed fake cleanup is independently certified
    }
}
