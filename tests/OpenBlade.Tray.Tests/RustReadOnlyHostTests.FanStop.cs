using OpenBlade.Contracts;

namespace OpenBlade.Tray.Tests;

public sealed partial class RustReadOnlyHostTests
{
    [RustReadOnlyHostFact]
    public async Task TrayClient_FanStopGuardianReleasesOnlyAfterReadbackCleanupAndExit()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = timeout.Token;
        string endpoint = Guid.NewGuid().ToString("N");
        await using var host = DevHost.Start(endpoint, "foreground-fan-stop", "complete");
        await host.WaitUntilReadyAsync(token);
        await using var client = new ServiceClient(PipeName(endpoint));

        var initial = await client.GetStatusAsync(token);
        Assert.Equal(PowerSource.UsbC, initial.ActivePowerSource);
        Assert.Equal(PerformancePreset.Balanced, initial.ConfirmedState!.Performance);
        Assert.True(initial.Capabilities!.SupportsFanStop);
        Assert.True(initial.FanStopStatus.CanStop);

        var stop = client.SetFanStopModeAsync(FanStopMode.Stop, token);
        await WaitForFanStopStateAsync(client, FanStopState.Starting, token);
        Assert.False(stop.IsCompleted);
        await AssertFanStopOrdinaryWriteAsync(client, ControlOperationStatus.Blocked, token);
        var stopped = await AdvanceFanStopAsync(host, client, token);
        Assert.Equal(FanStopState.Stopped, stopped.FanStopStatus.State);
        Assert.Equal(FanStopMode.Stop, stopped.FanStopStatus.RequestedMode);
        Assert.True(stopped.FanStopStatus.CanReturnToAutomatic);
        Assert.Equal(ControlOperationStatus.Applied, (await stop).Status);

        var auto = client.SetFanStopModeAsync(FanStopMode.Automatic, token);
        await WaitForFanStopStateAsync(client, FanStopState.ReturningToAutomatic, token);
        var readback = await AdvanceFanStopAsync(host, client, token);
        Assert.Contains("readback observed", readback.FanStopStatus.Message);
        Assert.True(readback.WritesBlocked);
        Assert.Equal(FanStopState.ReturningToAutomatic, readback.FanStopStatus.State);
        Assert.False(auto.IsCompleted);
        Assert.Equal(ControlOperationStatus.Blocked,
            (await client.SetFanStopModeAsync(FanStopMode.Automatic, token)).Status);
        await AssertFanStopOrdinaryWriteAsync(client, ControlOperationStatus.Blocked, token);

        var receipt = await AdvanceFanStopAsync(host, client, token);
        Assert.Contains("independent exit", receipt.FanStopStatus.Message);
        Assert.True(receipt.WritesBlocked);
        Assert.Null(receipt.ConfirmedState!.Fans);
        Assert.False(auto.IsCompleted);
        await AssertFanStopOrdinaryWriteAsync(client, ControlOperationStatus.Blocked, token);

        var released = await AdvanceFanStopAsync(host, client, token);
        Assert.Equal(ControlOperationStatus.Applied, (await auto).Status);
        Assert.Equal(FanStopState.Automatic, released.FanStopStatus.State);
        Assert.Equal(FanStopMode.Automatic, released.FanStopStatus.RequestedMode);
        Assert.False(released.FanStopStatus.CanReturnToAutomatic);
        Assert.False(released.WritesBlocked);
        Assert.Equal(FanMode.Automatic, released.ConfirmedState!.Fans);
        Assert.Equal(initial.Profiles, released.Profiles);
        Assert.Null(released.FanControlOverride);

        // The existing ordinary fan script also requires full-adapter admission.
        await AssertFanStopOrdinaryWriteAsync(client, ControlOperationStatus.Blocked, token);
        Assert.Equal(PowerSource.PluggedIn, (await AdvanceFanStopAsync(host, client, token)).ActivePowerSource);
        await AssertFanStopOrdinaryWriteAsync(client, ControlOperationStatus.Applied, token);
        Assert.Equal(FanMode.Fixed, (await client.GetStatusAsync(token)).FanControlOverride!.Mode);
        await host.StopAsync(token);
    }

    [RustReadOnlyHostFact]
    public async Task TrayClient_FanStopGuardianKeepsFenceAfterIncompleteStaleOrAmbiguousEvidence()
    {
        foreach (string scenario in new[] { "incomplete", "stale", "ambiguous" })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            CancellationToken token = timeout.Token;
            string endpoint = Guid.NewGuid().ToString("N");
            await using var host = DevHost.Start(endpoint, "foreground-fan-stop", scenario);
            await host.WaitUntilReadyAsync(token);
            await using var client = new ServiceClient(PipeName(endpoint));

            var stop = client.SetFanStopModeAsync(FanStopMode.Stop, token);
            await WaitForFanStopStateAsync(client, FanStopState.Starting, token);
            Assert.Equal(FanStopState.Stopped, (await AdvanceFanStopAsync(host, client, token)).FanStopStatus.State);
            Assert.Equal(ControlOperationStatus.Applied, (await stop).Status);
            if (scenario == "stale")
            {
                await AdvanceFanStopAsync(host, client, token);
            }
            else
            {
                var auto = client.SetFanStopModeAsync(FanStopMode.Automatic, token);
                if (scenario == "incomplete")
                {
                    await WaitForFanStopStateAsync(client, FanStopState.ReturningToAutomatic, token);
                    Assert.True((await AdvanceFanStopAsync(host, client, token)).WritesBlocked);
                    Assert.True((await AdvanceFanStopAsync(host, client, token)).WritesBlocked);
                    await AdvanceFanStopAsync(host, client, token);
                }

                Assert.Equal(ControlOperationStatus.Blocked, (await auto).Status);
            }

            var fenced = await client.GetStatusAsync(token);
            Assert.Equal(FanStopState.RecoveryRequired, fenced.FanStopStatus.State);
            Assert.True(fenced.WritesBlocked);
            Assert.False(fenced.FanStopStatus.CanStop);
            Assert.False(fenced.FanStopStatus.CanReturnToAutomatic);
            Assert.Null(fenced.ConfirmedState!.Fans);

            Assert.Equal(ControlOperationStatus.Blocked, (await client.SetFanStopModeAsync(FanStopMode.Stop, token)).Status);
            Assert.Equal(ControlOperationStatus.Blocked, (await client.SetFanStopModeAsync(FanStopMode.Automatic, token)).Status);
            await AssertFanStopOrdinaryWriteAsync(client, ControlOperationStatus.Blocked, token);
            Assert.Equal(fenced.Revision, (await client.GetStatusAsync(token)).Revision);
            await host.StopAsync(token);
        }
    }

    [RustReadOnlyHostFact]
    public async Task TrayClient_FanStopGuardianDistinguishesEntryRefusalFromProvenNeverSubmittedHandback()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = timeout.Token;
        foreach (string scenario in new[] { "entry-stale", "entry-power", "entry-console", "refused" })
        {
            string endpoint = Guid.NewGuid().ToString("N");
            await using var host = DevHost.Start(endpoint, "foreground-fan-stop", scenario);
            await host.WaitUntilReadyAsync(token);
            await using var client = new ServiceClient(PipeName(endpoint));

            var initial = await client.GetStatusAsync(token);
            var stop = client.SetFanStopModeAsync(FanStopMode.Stop, token);
            if (scenario == "refused")
            {
                await WaitForFanStopStateAsync(client, FanStopState.Starting, token);
                var pending = await AdvanceFanStopAsync(host, client, token);
                Assert.True(pending.WritesBlocked);
                Assert.Contains("refused before submission", pending.FanStopStatus.Message);
                Assert.False(stop.IsCompleted);
                await AdvanceFanStopAsync(host, client, token);
            }
            else
            {
                Assert.False(initial.FanStopStatus.CanStop);
            }

            Assert.Equal(ControlOperationStatus.Blocked, (await stop).Status);
            var refused = await client.GetStatusAsync(token);
            Assert.Equal(FanStopState.Inactive, refused.FanStopStatus.State);
            Assert.False(refused.WritesBlocked);
            if (scenario != "refused")
            {
                Assert.Equal(initial.Revision, refused.Revision);
            }

            await host.StopAsync(token);
        }
    }

    [RustReadOnlyHostFact]
    public async Task TrayClient_FanStopGuardianAutoCallerCancellationDoesNotCancelOwner()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = timeout.Token;
        string endpoint = Guid.NewGuid().ToString("N");
        await using var host = DevHost.Start(endpoint, "foreground-fan-stop", "complete");
        await host.WaitUntilReadyAsync(token);
        await using var client = new ServiceClient(PipeName(endpoint));

        var stop = client.SetFanStopModeAsync(FanStopMode.Stop, token);
        await WaitForFanStopStateAsync(client, FanStopState.Starting, token);
        await AdvanceFanStopAsync(host, client, token);
        Assert.Equal(ControlOperationStatus.Applied, (await stop).Status);

        using var waiter = CancellationTokenSource.CreateLinkedTokenSource(token);
        var auto = client.SetFanStopModeAsync(FanStopMode.Automatic, waiter.Token);
        await WaitForFanStopStateAsync(client, FanStopState.ReturningToAutomatic, token);
        await waiter.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auto);
        Assert.True((await AdvanceFanStopAsync(host, client, token)).WritesBlocked);
        Assert.True((await AdvanceFanStopAsync(host, client, token)).WritesBlocked);
        var released = await AdvanceFanStopAsync(host, client, token);
        Assert.Equal(FanStopState.Automatic, released.FanStopStatus.State);
        Assert.False(released.WritesBlocked);
        await host.StopAsync(token);
    }

    [RustReadOnlyHostFact]
    public async Task TrayClient_FanStopGuardianCanceledStopWaitStartsIndependentCleanup()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = timeout.Token;
        string endpoint = Guid.NewGuid().ToString("N");
        await using var host = DevHost.Start(endpoint, "foreground-fan-stop", "complete");
        await host.WaitUntilReadyAsync(token);
        await using var client = new ServiceClient(PipeName(endpoint));

        using var waiter = CancellationTokenSource.CreateLinkedTokenSource(token);
        var stop = client.SetFanStopModeAsync(FanStopMode.Stop, waiter.Token);
        await WaitForFanStopStateAsync(client, FanStopState.Starting, token);
        await waiter.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stop);
        await WaitForFanStopStateAsync(client, FanStopState.ReturningToAutomatic, token);

        Assert.True((await AdvanceFanStopAsync(host, client, token)).WritesBlocked);
        Assert.True((await AdvanceFanStopAsync(host, client, token)).WritesBlocked);
        var released = await AdvanceFanStopAsync(host, client, token);
        Assert.Equal(FanStopState.Automatic, released.FanStopStatus.State);
        Assert.False(released.WritesBlocked);
        await host.StopAsync(token);
    }

    private static async Task WaitForFanStopStateAsync(ServiceClient client, FanStopState state, CancellationToken token)
    {
        while ((await client.GetStatusAsync(token)).FanStopStatus.State != state)
        {
            await Task.Delay(10, token);
        }
    }

    private static async Task<ServiceStatusSnapshot> AdvanceFanStopAsync(
        DevHost host, ServiceClient client, CancellationToken token)
    {
        await using var watch = client.WatchStatusAsync(token).GetAsyncEnumerator();
        var before = await ReadStatusAsync(watch, subscriptionOpened: true);
        await host.AdvanceAsync(token);
        var after = await ReadStatusAsync(watch, subscriptionOpened: false);
        Assert.True(after.Revision > before.Revision);
        return after;
    }

    private static async Task AssertFanStopOrdinaryWriteAsync(
        ServiceClient client, ControlOperationStatus expected, CancellationToken token)
    {
        var request = new ManualOverride<FanMode>(FanMode.Fixed, OverrideLifetime.Persistent);
        var settings = new FanControlSettings(FanMode.Fixed, 4000, ManualFanCurve.Default);
        Assert.Equal(expected, (await client.SetFanControlOverrideAsync(request, settings, token)).Status);
    }
}
