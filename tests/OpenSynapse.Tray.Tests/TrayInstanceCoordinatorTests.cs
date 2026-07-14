using OpenSynapse.Tray;

namespace OpenSynapse.Tray.Tests;

public sealed class TrayInstanceCoordinatorTests
{
    [Fact]
    public void Endpoint_IsStableForSameUserAndPathCasing()
    {
        TrayInstanceEndpoint first = TrayInstanceEndpoint.Create(
            @"C:\Program Files\OpenSynapse\OpenSynapse.Tray.exe",
            "S-1-5-21-1000");
        TrayInstanceEndpoint second = TrayInstanceEndpoint.Create(
            @"c:\program files\opensynapse\opensynapse.tray.exe",
            "S-1-5-21-1000");

        Assert.Equal(first, second);
        Assert.StartsWith(@"Local\OpenSynapse.Tray.Instance.", first.InstanceEventName, StringComparison.Ordinal);
    }

    [Fact]
    public void Endpoint_DiffersAcrossUsersAndPortableCopies()
    {
        TrayInstanceEndpoint baseline = TrayInstanceEndpoint.Create(
            @"C:\Program Files\OpenSynapse\OpenSynapse.Tray.exe",
            "S-1-5-21-1000");

        Assert.NotEqual(
            baseline,
            TrayInstanceEndpoint.Create(
                @"C:\Program Files\OpenSynapse\OpenSynapse.Tray.exe",
                "S-1-5-21-2000"));
        Assert.NotEqual(
            baseline,
            TrayInstanceEndpoint.Create(
                @"D:\Portable\OpenSynapse.Tray.exe",
                "S-1-5-21-1000"));
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

    private static TrayInstanceEndpoint UniqueEndpoint() => TrayInstanceEndpoint.Create(
        $@"C:\OpenSynapse.Tests\{Guid.NewGuid():N}\OpenSynapse.Tray.exe",
        "S-1-5-21-TEST");
}
