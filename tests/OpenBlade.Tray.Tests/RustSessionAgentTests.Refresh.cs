using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.Tray.Tests;

public sealed partial class RustSessionAgentTests
{
    [RustSessionAgentFact]
    public async Task TrayDisplayClient_RepairsBothFixedRatesFromSplitTiming()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = timeout.Token;
        foreach (string key in new[] { "fixed-60", "fixed-240" })
        {
            string id = Guid.NewGuid().ToString("N");
            await using var host = Host.Start(id);
            await host.WaitForReadyAsync(token);
            var client = new SessionDisplaySettingsAdapter(PipeName(id));
            Assert.Null((await client.GetStateAsync(token)).SelectedRefreshRateKey);
            AssertRefresh(key, await client.SetRefreshRateAsync(PowerSource.PluggedIn, key, token));
            AssertRefresh(key, await client.GetStateAsync(token));
            await host.StopAsync(token);
        }
    }

    [RustSessionAgentFact]
    public async Task TrayDisplayClient_PreservesFixedAndDynamicTimingAcrossOtherDisplayWrites()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = timeout.Token;
        string id = Guid.NewGuid().ToString("N");
        await using var host = Host.Start(id, "refresh-dynamic");
        await host.WaitForReadyAsync(token);
        var client = new SessionDisplaySettingsAdapter(PipeName(id));
        const string dynamicKey = "dynamic-60-240";

        AssertRefresh("fixed-60", await client.SetRefreshRateAsync(PowerSource.PluggedIn, "fixed-60", token));
        AssertRefresh(dynamicKey, await client.SetRefreshRateAsync(PowerSource.PluggedIn, dynamicKey, token));
        AssertRefresh(dynamicKey, await client.SetHdrEnabledAsync(PowerSource.PluggedIn, false, token));
        AssertRefresh(dynamicKey, await client.SetBrightnessAsync(PowerSource.PluggedIn, 70, token));
        AssertRefresh("fixed-240", await client.SetRefreshRateAsync(PowerSource.PluggedIn, "fixed-240", token));
        AssertRefresh("fixed-240", await client.GetStateAsync(token));
        await host.StopAsync(token);
    }

    [RustSessionAgentFact]
    public async Task PrivateHost_RejectsRefreshWithoutFreshAdmission()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = timeout.Token;
        foreach (var (scenario, power, key, status, error) in new[]
        {
            ("available", PowerSource.Battery, "fixed-60", SessionDisplayOperationStatus.Blocked, "expected power"),
            ("available", PowerSource.PluggedIn, "dynamic-60-240", SessionDisplayOperationStatus.Failed, "option is unavailable"),
            ("refresh-stale-power", PowerSource.PluggedIn, "fixed-60", SessionDisplayOperationStatus.Blocked, "became stale"),
            ("refresh-stale-target", PowerSource.PluggedIn, "fixed-60", SessionDisplayOperationStatus.Blocked, "display changed"),
            ("refresh-inactive", PowerSource.PluggedIn, "fixed-60", SessionDisplayOperationStatus.Blocked, "active session"),
            ("refresh-stale-after-validation", PowerSource.PluggedIn, "fixed-60", SessionDisplayOperationStatus.Blocked, "changed after validation"),
            ("refresh-validation-error", PowerSource.PluggedIn, "fixed-60", SessionDisplayOperationStatus.Failed, "validated"),
        })
        {
            string id = Guid.NewGuid().ToString("N");
            await using var host = Host.Start(id, scenario);
            await host.WaitForReadyAsync(token);
            SessionDisplayResponse response = await SetRefreshOnPipeAsync(id, power, key, token);
            Assert.Equal(status, response.Status);
            Assert.Contains(error, response.Error);
            Assert.Null(response.State.CurrentRefreshKey);
            var client = new SessionDisplaySettingsAdapter(PipeName(id));
            Assert.Null((await client.GetStateAsync(token)).SelectedRefreshRateKey);
            if (scenario == "refresh-validation-error")
            {
                Assert.True((await client.SetBrightnessAsync(PowerSource.PluggedIn, 70, token)).OperationSucceeded);
            }
            await host.StopAsync(token);
        }
    }

    [RustSessionAgentFact]
    public async Task PrivateHost_PausesAllDisplayWritesAfterUncertainRefresh()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = timeout.Token;
        foreach (var (scenario, error, readback) in new[]
        {
            ("refresh-submission-error", "native result", "fixed-60"),
            ("refresh-readback-mismatch", "did not confirm", (string?)null),
            ("refresh-sibling-readback", "exact target", "fixed-60"),
        })
        {
            string id = Guid.NewGuid().ToString("N");
            await using var host = Host.Start(id, scenario);
            await host.WaitForReadyAsync(token);
            SessionDisplayResponse response = await SetRefreshOnPipeAsync(id, PowerSource.PluggedIn, "fixed-60", token);
            Assert.Equal(SessionDisplayOperationStatus.Indeterminate, response.Status);
            Assert.Contains(error, response.Error);
            Assert.Equal(scenario == "refresh-submission-error" ? readback : null, response.State.CurrentRefreshKey);
            var client = new SessionDisplaySettingsAdapter(PipeName(id));
            Assert.Equal(readback, (await client.GetStateAsync(token)).SelectedRefreshRateKey);

            var retry = await client.SetRefreshRateAsync(PowerSource.PluggedIn, "fixed-60", token);
            Assert.False(retry.OperationSucceeded);
            Assert.Contains("writes are paused", retry.StatusMessage);
            Assert.False((await client.SetHdrEnabledAsync(PowerSource.PluggedIn, false, token)).OperationSucceeded);
            Assert.False((await client.SetBrightnessAsync(PowerSource.PluggedIn, 70, token)).OperationSucceeded);
            await host.StopAsync(token);
        }
    }

    [RustSessionAgentFact]
    public async Task TrayDisplayClient_RefreshCancellationBeforeAndAfterSubmission()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = timeout.Token;
        foreach (bool submitted in new[] { false, true })
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            string id = Guid.NewGuid().ToString("N");
            await using var host = Host.Start(id, submitted ? "refresh-stalled-submission" : "refresh-stalled-admission");
            await host.WaitForReadyAsync(token);
            var client = new SessionDisplaySettingsAdapter(PipeName(id));
            Task<DisplaySettingsSnapshot> apply = client.SetRefreshRateAsync(PowerSource.PluggedIn, "fixed-60", cancellation.Token);
            await host.WaitForSignalAsync(submitted ? "REFRESH SUBMITTED" : "REFRESH ADMISSION", token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
            Assert.Equal((byte)40, await ObserveBrightnessAsync(client, token));
            Assert.Equal(submitted ? "fixed-60" : null, (await client.GetStateAsync(token)).SelectedRefreshRateKey);
            Assert.Equal(!submitted, (await client.SetBrightnessAsync(PowerSource.PluggedIn, 70, token)).OperationSucceeded);
            await host.StopAsync(token);
        }
    }

    private static async Task<SessionDisplayResponse> SetRefreshOnPipeAsync(
        string id, PowerSource power, string key, CancellationToken token)
    {
        using var pipe = Pipe(id);
        await pipe.ConnectAsync(token);
        Guid requestId = Guid.NewGuid();
        await SessionDisplayIpcCodec.WriteRequestAsync(
            pipe, new SetRefreshRateRequest(SessionDisplayIpc.ProtocolVersion, requestId, power, key), token);
        SessionDisplayResponse response = await SessionDisplayIpcCodec.ReadResponseAsync(pipe, token);
        Assert.Equal(requestId, response.RequestId);
        return response;
    }

    private static void AssertRefresh(string key, DisplaySettingsSnapshot snapshot)
    {
        Assert.True(snapshot.OperationSucceeded, snapshot.StatusMessage);
        Assert.Equal(key, snapshot.SelectedRefreshRateKey);
    }
}
