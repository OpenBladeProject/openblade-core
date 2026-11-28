using System.Text.Json.Nodes;
using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.Tray.Tests;

public sealed partial class RustSessionAgentTests
{
    [RustSessionAgentFact]
    public async Task PrivateDisplay_FencesEachSubmittedOperationAcrossProcessDeath()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        CancellationToken token = timeout.Token;
        foreach (string operation in new[] { "refresh", "hdr", "brightness" })
        {
            string id = Guid.NewGuid().ToString("N");
            string scenario = operation == "brightness" ? "available" : $"{operation}-stalled-submission";
            string? brightness = operation == "brightness" ? "stalled-write" : null;
            string restartScenario = operation == "refresh" ? "refresh-dynamic" : "available";
            await using var original = Host.Start(id, scenario, brightness);
            await original.WaitForReadyAsync(token);
            var client = new SessionDisplaySettingsAdapter(PipeName(id));
            Assert.True((await client.GetStateAsync(token)).IsAvailable);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task<DisplaySettingsSnapshot> apply = operation switch
            {
                "refresh" => client.SetRefreshRateAsync(PowerSource.PluggedIn, "fixed-60", cancellation.Token),
                "hdr" => client.SetHdrEnabledAsync(PowerSource.PluggedIn, false, cancellation.Token),
                _ => client.SetBrightnessAsync(PowerSource.PluggedIn, 70, cancellation.Token),
            };
            await original.WaitForSignalAsync($"{operation.ToUpperInvariant()} SUBMITTED", token);
            string path = DisplayJournalPath(id);
            string pending = await File.ReadAllTextAsync(path, token);
            Assert.Contains($"\"operation\":\"{operation}\"", pending);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
            Assert.True((await client.GetStateAsync(token)).IsAvailable);
            await AssertDisplayWritesFencedAsync(client, token);
            // Even the explicitly authorized recovery mode cannot claim a live
            // fixture. A matching read in the old host leaves the same intent.
            await using (var contender = Host.Start(id, mode: "reconcile-display"))
            {
                await contender.AssertRejectedAsync(token);
            }
            Assert.Equal(pending, await File.ReadAllTextAsync(path, token));
            await original.KillAsync(token);

            await using (var restarted = Host.Start(id, restartScenario, mode: "resume-display"))
            {
                await restarted.WaitForReadyAsync(token);
                DisplaySettingsSnapshot baseline = await client.GetStateAsync(token);
                Assert.True(baseline.IsAvailable);
                Assert.Null(baseline.SelectedRefreshRateKey);
                Assert.True(baseline.IsHdrEnabled);
                Assert.Equal((byte)40, baseline.BrightnessPercent);
                await AssertDisplayWritesFencedAsync(client, token);
                Assert.Equal(pending, await File.ReadAllTextAsync(path, token));
                await restarted.StopAsync(token);
            }

            await using (var unavailable = Host.Start(id, "unavailable",
                mode: "reconcile-display"))
            {
                await unavailable.WaitForReadyAsync(token);
                Assert.False((await client.GetStateAsync(token)).IsAvailable);
                await AssertDisplayWritesFencedAsync(client, token);
                Assert.Equal(pending, await File.ReadAllTextAsync(path, token));
                await unavailable.StopAsync(token);
            }

            if (operation == "refresh")
            {
                JsonNode wrongPanel = JsonNode.Parse(pending)!;
                wrongPanel["state"]!["attempt"]!["panel"]!["monitor_path"] = "different monitor path";
                string foreign = wrongPanel.ToJsonString();
                await File.WriteAllTextAsync(path, foreign, token);
                await using (var different = Host.Start(id, restartScenario, mode: "reconcile-display"))
                {
                    await different.WaitForReadyAsync(token);
                    Assert.True((await client.GetStateAsync(token)).IsAvailable);
                    await AssertDisplayWritesFencedAsync(client, token);
                    Assert.Equal(foreign, await File.ReadAllTextAsync(path, token));
                    await different.StopAsync(token);
                }
                await File.WriteAllTextAsync(path, pending, token);
            }

            if (operation == "brightness")
            {
                await using var different = Host.Start(id, brightness: "starts-at-10", mode: "reconcile-display");
                await different.WaitForReadyAsync(token);
                Assert.Equal((byte)10, (await client.GetStateAsync(token)).BrightnessPercent);
                await AssertDisplayWritesFencedAsync(client, token);
                Assert.Equal(pending, await File.ReadAllTextAsync(path, token));
                await different.StopAsync(token);
            }

            await using var recovery = Host.Start(id, restartScenario,
                brightness: operation == "brightness" ? "starts-at-70" : null, mode: "reconcile-display");
            await recovery.WaitForReadyAsync(token);
            await AssertDisplayWritesFencedAsync(client, token);
            DisplaySettingsSnapshot recovered = await client.GetStateAsync(token);
            Assert.True(recovered.IsAvailable);
            Assert.Equal(operation == "brightness" ? (byte)70 : (byte)40, recovered.BrightnessPercent);
            Assert.True((await client.SetBrightnessAsync(PowerSource.PluggedIn, 10, token)).OperationSucceeded);
            Assert.True((await client.SetHdrEnabledAsync(PowerSource.PluggedIn, false, token)).OperationSucceeded);
            Assert.True((await client.SetRefreshRateAsync(PowerSource.PluggedIn, "fixed-60", token)).OperationSucceeded);
            await recovery.StopAsync(token);
            Assert.Contains("\"status\":\"clean\"", await File.ReadAllTextAsync(path, token));
        }
    }

    [RustSessionAgentFact]
    public async Task PrivateDisplay_StopsDuringOutstandingScriptedRefreshAndRecoversReadOnly()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = timeout.Token;
        string id = Guid.NewGuid().ToString("N");
        await using var original = Host.Start(id, "refresh-stalled-submission");
        await original.WaitForReadyAsync(token);
        var client = new SessionDisplaySettingsAdapter(PipeName(id));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<DisplaySettingsSnapshot> apply =
            client.SetRefreshRateAsync(PowerSource.PluggedIn, "fixed-60", cancellation.Token);
        await original.WaitForSignalAsync("REFRESH SUBMITTED", token);
        await original.KillAsync(token);
        cancellation.Cancel();
        try
        {
            Assert.False((await apply).OperationSucceeded);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        string path = DisplayJournalPath(id);
        Assert.Contains("\"status\":\"pending\"", await File.ReadAllTextAsync(path, token));
        await using (var changed = Host.Start(id, mode: "reconcile-display"))
        {
            await changed.WaitForReadyAsync(token);
            // Both scenarios expose an unset refresh key. The replacement
            // must also match the saved virtual layout and physical timing.
            Assert.Null((await client.GetStateAsync(token)).SelectedRefreshRateKey);
            await AssertDisplayWritesFencedAsync(client, token);
            await changed.StopAsync(token);
        }
        await using var restarted = Host.Start(id, "refresh-dynamic", mode: "reconcile-display");
        await restarted.WaitForReadyAsync(token);
        await AssertDisplayWritesFencedAsync(client, token);
        Assert.Null((await client.GetStateAsync(token)).SelectedRefreshRateKey);
        Assert.True((await client.SetRefreshRateAsync(PowerSource.PluggedIn, "fixed-240", token)).OperationSucceeded);
        await restarted.StopAsync(token);
    }

    [RustSessionAgentFact]
    public async Task PrivateDisplay_InvalidOrMissingJournalAllowsOnlyReadOnlyQueries()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = timeout.Token;
        foreach (string fault in new[] { "missing", "malformed", "foreign", "future", "unreadable" })
        {
            string id = Guid.NewGuid().ToString("N");
            await using var original = Host.Start(id);
            await original.WaitForReadyAsync(token);
            await original.StopAsync(token);
            string path = DisplayJournalPath(id);
            JsonNode record = JsonNode.Parse(await File.ReadAllTextAsync(path, token))!;
            if (fault == "missing")
            {
                File.Delete(path);
            }
            else if (fault == "malformed")
            {
                await File.WriteAllTextAsync(path, "{", token);
            }
            else if (fault is "foreign" or "future")
            {
                if (fault == "foreign")
                {
                    record["owner"] = "different owner or logon";
                }
                else
                {
                    record["version"] = 2;
                }
                await File.WriteAllTextAsync(path, record.ToJsonString(), token);
            }

            using FileStream? held = fault == "unreadable"
                ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)
                : null;
            // Starting as "new" with an existing fixture directory cannot
            // turn a missing record into a clean one either.
            await using var restarted = Host.Start(id,
                mode: fault == "missing" ? "foreground" : "reconcile-display");
            await restarted.WaitForReadyAsync(token);
            var client = new SessionDisplaySettingsAdapter(PipeName(id));
            Assert.True((await client.GetStateAsync(token)).IsAvailable);
            await AssertDisplayWritesFencedAsync(client, token);
            await restarted.StopAsync(token);
            if (fault == "missing")
            {
                Assert.False(File.Exists(path));
            }
        }
    }

    [RustSessionAgentFact]
    public async Task PrivateDisplay_NoOpAndPreSubmissionCancelLeaveCleanRestart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = timeout.Token;
        string id = Guid.NewGuid().ToString("N");
        await using var original = Host.Start(id, "refresh-stalled-admission");
        await original.WaitForReadyAsync(token);
        var client = new SessionDisplaySettingsAdapter(PipeName(id));
        string path = DisplayJournalPath(id);
        string clean = await File.ReadAllTextAsync(path, token);
        Assert.True((await client.SetHdrEnabledAsync(PowerSource.PluggedIn, true, token)).OperationSucceeded);
        Assert.True((await client.SetBrightnessAsync(PowerSource.PluggedIn, 40, token)).OperationSucceeded);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<DisplaySettingsSnapshot> apply =
            client.SetRefreshRateAsync(PowerSource.PluggedIn, "fixed-60", cancellation.Token);
        await original.WaitForSignalAsync("REFRESH ADMISSION", token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
        Assert.Null((await client.GetStateAsync(token)).SelectedRefreshRateKey);
        Assert.Equal(clean, await File.ReadAllTextAsync(path, token));
        await original.StopAsync(token);

        await using var restarted = Host.Start(id, mode: "resume-display");
        await restarted.WaitForReadyAsync(token);
        Assert.True((await client.SetRefreshRateAsync(PowerSource.PluggedIn, "fixed-60", token)).OperationSucceeded);
        await restarted.StopAsync(token);
    }

    [RustSessionAgentFact]
    public async Task PrivateDisplay_RecoveryLaunchCannotRetireAnAttemptItStarted()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = timeout.Token;
        string id = Guid.NewGuid().ToString("N");
        await using var original = Host.Start(id);
        await original.WaitForReadyAsync(token);
        await original.StopAsync(token);

        await using var restarted = Host.Start(id, "hdr-submission-error",
            mode: "reconcile-display");
        await restarted.WaitForReadyAsync(token);
        var client = new SessionDisplaySettingsAdapter(PipeName(id));
        Assert.False((await client.SetHdrEnabledAsync(PowerSource.PluggedIn, false, token)).OperationSucceeded);
        Assert.False((await client.GetStateAsync(token)).IsHdrEnabled);
        Assert.Contains("\"status\":\"pending\"", await File.ReadAllTextAsync(DisplayJournalPath(id), token));
        await AssertDisplayWritesFencedAsync(client, token);
        await restarted.StopAsync(token);
    }

    [RustSessionAgentFact]
    public async Task PrivateDisplay_IntentReplacementFailurePreventsScriptedSubmission()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        CancellationToken token = timeout.Token;
        string id = Guid.NewGuid().ToString("N");
        await using var original = Host.Start(id);
        await original.WaitForReadyAsync(token);
        var client = new SessionDisplaySettingsAdapter(PipeName(id));
        string path = DisplayJournalPath(id);

        // Allow the admission read, while denying the journal's atomic replacement.
        using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var refused = await client.SetRefreshRateAsync(PowerSource.PluggedIn, "fixed-60", token);
            Assert.False(refused.OperationSucceeded);
            Assert.Contains("writes are paused", refused.StatusMessage);
            Assert.Null((await client.GetStateAsync(token)).SelectedRefreshRateKey);
        }
        Assert.Contains("\"status\":\"clean\"", await File.ReadAllTextAsync(path, token));
        Assert.True(File.Exists(Path.Combine(DisplayFixtureDirectory(id), "state.next")));
        await original.StopAsync(token);

        await using var restarted = Host.Start(id, mode: "reconcile-display");
        await restarted.WaitForReadyAsync(token);
        Assert.True((await client.GetStateAsync(token)).IsAvailable);
        await AssertDisplayWritesFencedAsync(client, token);
        await restarted.StopAsync(token);
    }

    private static string DisplayFixtureDirectory(string id) =>
        Path.Combine(Path.GetTempPath(), $"openblade-display-{id}");

    private static string DisplayJournalPath(string id) =>
        Path.Combine(DisplayFixtureDirectory(id), "state.json");

    private static async Task AssertDisplayWritesFencedAsync(
        SessionDisplaySettingsAdapter client, CancellationToken token)
    {
        Assert.Contains("writes are paused",
            (await client.SetRefreshRateAsync(PowerSource.PluggedIn, "fixed-240", token)).StatusMessage);
        Assert.Contains("writes are paused",
            (await client.SetHdrEnabledAsync(PowerSource.PluggedIn, false, token)).StatusMessage);
        Assert.Contains("writes are paused",
            (await client.SetBrightnessAsync(PowerSource.PluggedIn, 70, token)).StatusMessage);
    }
}
