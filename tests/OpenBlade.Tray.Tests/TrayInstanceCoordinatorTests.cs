using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class TrayInstanceCoordinatorTests
{
    [Fact]
    public void Endpoint_IsStableForSameUserAcrossBuildLocations()
    {
        TrayInstanceEndpoint installed = TrayInstanceEndpoint.Create("S-1-5-21-1000");
        TrayInstanceEndpoint development = TrayInstanceEndpoint.Create("S-1-5-21-1000");

        Assert.Equal(installed, development);
        Assert.Equal(
            @"Local\OpenBlade.Tray.Instance.S-1-5-21-1000",
            installed.InstanceEventName);
    }

    [Fact]
    public void Endpoint_DiffersAcrossUsers()
    {
        TrayInstanceEndpoint baseline = TrayInstanceEndpoint.Create("S-1-5-21-1000");

        Assert.NotEqual(
            baseline,
            TrayInstanceEndpoint.Create("S-1-5-21-2000"));
    }

    [Fact]
    public async Task SecondaryInstance_SignalsOwnerAndDoesNotAcquireOwnership()
    {
        TrayInstanceEndpoint endpoint = UniqueEndpoint();
        TaskCompletionSource signaled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(TrayInstanceCoordinator.TryStart(endpoint, signaled.SetResult, out TrayInstanceCoordinator? owner));
        await using (owner!)
        {
            Assert.False(TrayInstanceCoordinator.TryStart(endpoint, static () => { }, out TrayInstanceCoordinator? secondary));
            Assert.Null(secondary);
            await signaled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task DisposedOwner_ReleasesInstanceForNextLaunch()
    {
        TrayInstanceEndpoint endpoint = UniqueEndpoint();
        Assert.True(TrayInstanceCoordinator.TryStart(endpoint, static () => { }, out TrayInstanceCoordinator? first));
        await first!.DisposeAsync();

        Assert.True(TrayInstanceCoordinator.TryStart(endpoint, static () => { }, out TrayInstanceCoordinator? next));
        await next!.DisposeAsync();
    }

    private static TrayInstanceEndpoint UniqueEndpoint() =>
        TrayInstanceEndpoint.Create($"S-1-5-21-TEST-{Guid.NewGuid():N}");
}
