using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.Tray.Tests;

public sealed partial class RustSessionAgentTests
{
    [RustSessionAgentFact]
    public async Task TrayDisplayClient_IccCancellationAfterIntentLeavesCleanRestart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        CancellationToken token = timeout.Token;
        string id = Guid.NewGuid().ToString("N");
        string path = DisplayJournalPath(id);
        var client = new SessionDisplaySettingsAdapter(PipeName(id));
        await using var original = Host.Start(id, "icc-stalled-admission");
        await original.WaitForReadyAsync(token);
        string clean = await File.ReadAllTextAsync(path, token);
        Assert.True((await client.SetColorProfileAsync(PowerSource.PluggedIn, FactoryProfile, token)).OperationSucceeded);
        Assert.Equal(clean, await File.ReadAllTextAsync(path, token));

        var selection = client.SetColorProfileAsync(PowerSource.PluggedIn, NativeProfile, cancellation.Token);
        await original.WaitForSignalAsync("ICC ADMISSION", token);
        Assert.Contains("\"operation\":\"icc\"", await File.ReadAllTextAsync(path, token));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection);
        Assert.Equal(FactoryProfile, (await client.GetStateAsync(token)).SelectedColorProfileKey);
        Assert.Equal(clean, await File.ReadAllTextAsync(path, token));
        await original.StopAsync(token);

        await using var restarted = Host.Start(id, "icc-available", mode: "resume-display");
        await restarted.WaitForReadyAsync(token);
        Assert.True((await client.SetColorProfileAsync(PowerSource.PluggedIn, NativeProfile, token)).OperationSucceeded);
        Assert.Equal(NativeProfile, (await client.GetStateAsync(token)).SelectedColorProfileKey);
        Assert.Equal(clean, await File.ReadAllTextAsync(path, token));
        Assert.True((await client.SetBrightnessAsync(PowerSource.PluggedIn, 70, token)).OperationSucceeded);
        await restarted.StopAsync(token);
    }

    [RustSessionAgentFact]
    public async Task TrayDisplayClient_IccRestartRequiresExactReadOnlyRecovery()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        CancellationToken token = timeout.Token;
        string id = Guid.NewGuid().ToString("N");
        string path = DisplayJournalPath(id);
        var client = new SessionDisplaySettingsAdapter(PipeName(id));
        await using var original = Host.Start(id, "icc-stalled-reply");
        await original.WaitForReadyAsync(token);
        string clean = await File.ReadAllTextAsync(path, token);
        var selection = client.SetColorProfileAsync(PowerSource.PluggedIn, NativeProfile, cancellation.Token);
        await original.WaitForSignalAsync("ICC CONFIRMED", token);
        string pending = await File.ReadAllTextAsync(path, token);
        Assert.Contains("\"operation\":\"icc\"", pending);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => selection);

        Assert.Equal(NativeProfile, (await client.GetStateAsync(token)).SelectedColorProfileKey);
        await AssertIccAndDisplayWritesFencedAsync(client, token);
        await using (var contender = Host.Start(id, "icc-recovery-applied", mode: "reconcile-display"))
        {
            await contender.AssertRejectedAsync(token);
        }
        Assert.Equal(pending, await File.ReadAllTextAsync(path, token));
        await original.KillAsync(token);

        await using (var resumed = Host.Start(id, "icc-recovery-applied", mode: "resume-display"))
        {
            await resumed.WaitForReadyAsync(token);
            Assert.Equal(NativeProfile, (await client.GetStateAsync(token)).SelectedColorProfileKey);
            await AssertIccAndDisplayWritesFencedAsync(client, token);
            Assert.Equal(pending, await File.ReadAllTextAsync(path, token));
            await resumed.StopAsync(token);
        }

        foreach (var (scenario, expectedProfile) in new (string, string?)[]
        {
            ("icc-recovery-wrong-scope", NativeProfile),
            ("icc-recovery-wrong-associations", NativeProfile),
            ("icc-recovery-hdr", null),
            ("icc-unavailable", null),
        })
        {
            await using var uncertain = Host.Start(id, scenario, mode: "reconcile-display");
            await uncertain.WaitForReadyAsync(token);
            var observed = await client.GetStateAsync(token);
            Assert.True(observed.IsAvailable);
            Assert.Equal(expectedProfile, observed.SelectedColorProfileKey);
            await AssertIccAndDisplayWritesFencedAsync(client, token);
            Assert.Equal(pending, await File.ReadAllTextAsync(path, token));
            await uncertain.StopAsync(token);
        }

        foreach (string fault in new[] { "wrong-panel", "missing-default" })
        {
            JsonNode record = JsonNode.Parse(pending)!;
            JsonNode attempt = record["state"]!["attempt"]!;
            if (fault == "wrong-panel")
            {
                attempt["panel"]!["monitor_path"] = "different monitor path";
            }
            else
            {
                Assert.True(attempt["change"]!["evidence"]!["before"]!.AsObject().Remove("user_default"));
            }
            string invalid = record.ToJsonString();
            await File.WriteAllTextAsync(path, invalid, token);
            await using var refused = Host.Start(id, "icc-recovery-applied", mode: "reconcile-display");
            await refused.WaitForReadyAsync(token);
            Assert.Equal(NativeProfile, (await client.GetStateAsync(token)).SelectedColorProfileKey);
            await AssertIccAndDisplayWritesFencedAsync(client, token);
            Assert.Equal(invalid, await File.ReadAllTextAsync(path, token));
            await refused.StopAsync(token);
        }
        await File.WriteAllTextAsync(path, pending, token);

        await using var recovered = Host.Start(id, "icc-recovery-applied", mode: "reconcile-display");
        await recovered.WaitForReadyAsync(token);
        await AssertIccAndDisplayWritesFencedAsync(client, token);
        Assert.Equal(NativeProfile, (await client.GetStateAsync(token)).SelectedColorProfileKey);
        // Retirement follows delivery. The next connection waits for it to finish.
        Assert.Equal(NativeProfile, (await client.GetStateAsync(token)).SelectedColorProfileKey);
        Assert.Equal(clean, await File.ReadAllTextAsync(path, token));
        Assert.True((await client.SetBrightnessAsync(PowerSource.PluggedIn, 70, token)).OperationSucceeded);
        await recovered.StopAsync(token);
    }

    [RustSessionAgentFact]
    public async Task TrayDisplayClient_IccPartialFailureFencesUntilBaselineRecovery()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = timeout.Token;
        string id = Guid.NewGuid().ToString("N");
        string path = DisplayJournalPath(id);
        var client = new SessionDisplaySettingsAdapter(PipeName(id));
        await using var original = Host.Start(id, "icc-partial-submission-error");
        await original.WaitForReadyAsync(token);
        string clean = await File.ReadAllTextAsync(path, token);

        var failure = await client.SetColorProfileAsync(PowerSource.PluggedIn, NativeProfile, token);
        Assert.False(failure.OperationSucceeded);
        Assert.Contains("indeterminate", failure.StatusMessage);
        Assert.Equal(FactoryProfile, (await client.GetStateAsync(token)).SelectedColorProfileKey);
        string pending = await File.ReadAllTextAsync(path, token);
        Assert.Contains("\"operation\":\"icc\"", pending);
        await original.StopAsync(token);

        await using (var resumed = Host.Start(id, "icc-available", mode: "resume-display"))
        {
            await resumed.WaitForReadyAsync(token);
            Assert.Equal(FactoryProfile, (await client.GetStateAsync(token)).SelectedColorProfileKey);
            await AssertIccAndDisplayWritesFencedAsync(client, token);
            Assert.Equal(pending, await File.ReadAllTextAsync(path, token));
            await resumed.StopAsync(token);
        }

        await using (var partial = Host.Start(id, "icc-recovery-partial", mode: "reconcile-display"))
        {
            await partial.WaitForReadyAsync(token);
            // The failing associate call changed the user list while leaving the displayed baseline ID.
            Assert.Equal(FactoryProfile, (await client.GetStateAsync(token)).SelectedColorProfileKey);
            await AssertIccAndDisplayWritesFencedAsync(client, token);
            Assert.Equal(pending, await File.ReadAllTextAsync(path, token));
            await partial.StopAsync(token);
        }

        await using var recovered = Host.Start(id, "icc-available", mode: "reconcile-display");
        await recovered.WaitForReadyAsync(token);
        Assert.Equal(FactoryProfile, (await client.GetStateAsync(token)).SelectedColorProfileKey);
        // Retirement follows delivery. The next connection waits for it to finish.
        Assert.Equal(FactoryProfile, (await client.GetStateAsync(token)).SelectedColorProfileKey);
        Assert.Equal(clean, await File.ReadAllTextAsync(path, token));
        Assert.True((await client.SetColorProfileAsync(PowerSource.PluggedIn, NativeProfile, token)).OperationSucceeded);
        await recovered.StopAsync(token);
    }

    private static async Task AssertIccAndDisplayWritesFencedAsync(
        SessionDisplaySettingsAdapter client, CancellationToken token)
    {
        Assert.Contains("writes are paused",
            (await client.SetColorProfileAsync(PowerSource.PluggedIn, NativeProfile, token)).StatusMessage);
        await AssertDisplayWritesFencedAsync(client, token);
    }
}
