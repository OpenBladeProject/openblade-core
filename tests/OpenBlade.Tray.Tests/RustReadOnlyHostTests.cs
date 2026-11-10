using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using OpenBlade.Contracts;

namespace OpenBlade.Tray.Tests;

public sealed class RustReadOnlyHostTests
{
    [RustReadOnlyHostFact]
    public async Task TrayClient_ReadsAndWatchesCachedStatus_AndRefusesOtherOperations()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string endpointId = Guid.NewGuid().ToString("N");
        await using var host = DevHost.Start(endpointId);
        await host.WaitUntilReadyAsync(timeout.Token);
        await using var client = new ServiceClient(PipeName(endpointId));

        ServiceStatusSnapshot cached = await client.GetStatusAsync(timeout.Token);
        Assert.Equal(1, cached.Revision);
        Assert.Equal(SessionControlAccess.Active, cached.ControlAccess);
        Assert.Equal("RazerAppEngine", Assert.Single(cached.PotentialControllers));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.ObserveStateAsync(includeLiveReadings: true, timeout.Token));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.SetBatteryProtectionAsync(enabled: false, timeout.Token));

        using var watchCancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var watch = client.WatchStatusAsync(watchCancellation.Token).GetAsyncEnumerator();
        Assert.Equal(0, (await ReadStatusAsync(watch, subscriptionOpened: true)).Revision);
        ServiceStatusSnapshot published = await ReadStatusAsync(watch, subscriptionOpened: false);
        Assert.Equal(cached.Revision, published.Revision);
        Assert.Equal(cached.ControlAccess, published.ControlAccess);
        Assert.Equal(cached.LiveReadings, published.LiveReadings);

        Task<bool> pendingUpdate = watch.MoveNextAsync().AsTask();
        await watchCancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pendingUpdate);
        Assert.False(timeout.IsCancellationRequested);

        Assert.Equal(cached.Revision, (await client.GetStatusAsync(timeout.Token)).Revision);
        await host.StopAsync(timeout.Token);
    }

    [RustReadOnlyHostFact]
    public async Task TrayClient_ReceivesBackgroundProjectionOnReadAndSubscription()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string endpointId = Guid.NewGuid().ToString("N");
        await using var host = DevHost.Start(endpointId, "foreground-background");
        await host.WaitUntilReadyAsync(timeout.Token);
        await using var client = new ServiceClient(PipeName(endpointId));

        ServiceStatusSnapshot cached = await client.GetStatusAsync(timeout.Token);
        Assert.Equal(1, cached.Revision);
        AssertBackground(cached);

        await using var watch = client.WatchStatusAsync(timeout.Token).GetAsyncEnumerator();
        AssertBackground(await ReadStatusAsync(watch, subscriptionOpened: true));
        AssertBackground(await ReadStatusAsync(watch, subscriptionOpened: false));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.SetBatteryProtectionAsync(enabled: false, timeout.Token));
        await host.StopAsync(timeout.Token);
    }

    [RustReadOnlyHostFact]
    public async Task TrayClient_ReconnectsAfterHostJoinsIdleAndPartialFrameConnections()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string endpointId = Guid.NewGuid().ToString("N");
        await using var host = DevHost.Start(endpointId);
        await host.WaitUntilReadyAsync(timeout.Token);
        await using var client = new ServiceClient(PipeName(endpointId));
        await using var watch = client.WatchStatusAsync(timeout.Token).GetAsyncEnumerator();
        Assert.Equal(0, (await ReadStatusAsync(watch, subscriptionOpened: true)).Revision);
        Assert.Equal(1, (await ReadStatusAsync(watch, subscriptionOpened: false)).Revision);

        await using (NamedPipeClientStream partial = await ConnectAsync(endpointId, timeout.Token))
        {
            // This connection submits an incomplete prefix and leaves its write side open.
            await partial.WriteAsync(new byte[] { 1 }, timeout.Token);
            await host.StopAsync(timeout.Token);
        }

        Assert.True(await watch.MoveNextAsync());
        Assert.IsType<ServiceWatchUpdate.Reconnecting>(watch.Current);

        await using var restarted = DevHost.Start(endpointId);
        await restarted.WaitUntilReadyAsync(timeout.Token);
        Assert.Equal(0, (await ReadStatusAsync(watch, subscriptionOpened: true)).Revision);
        Assert.Equal(1, (await ReadStatusAsync(watch, subscriptionOpened: false)).Revision);
        await restarted.StopAsync(timeout.Token);
    }

    [RustReadOnlyHostFact]
    public async Task Pipe_CorrelatesRequests_ProjectsV51_AndClosesMalformedInput()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string endpointId = Guid.NewGuid().ToString("N");
        await using var host = DevHost.Start(endpointId);
        await host.WaitUntilReadyAsync(timeout.Token);

        await using (NamedPipeClientStream pipe = await ConnectAsync(endpointId, timeout.Token))
        {
            var request = new SetBatteryProtectionRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), false);
            await IpcMessageCodec.WriteRequestAsync(pipe, request, timeout.Token);
            var response = Assert.IsType<ServiceResponse>(
                await IpcMessageCodec.ReadServerFrameAsync(pipe, timeout.Token));
            Assert.Equal(request.RequestId, response.RequestId);
            Assert.False(response.Success);
            Assert.Null(response.Status);
            Assert.Null(response.Result);
            Assert.False(string.IsNullOrWhiteSpace(response.Error));
        }

        await using (NamedPipeClientStream pipe = await ConnectAsync(endpointId, timeout.Token))
        {
            Guid requestId = Guid.Parse("11111111-2222-4333-8444-555555555555");
            await WriteFrameAsync(pipe,
                """{"operation":"getStatus","version":51,"requestId":"11111111-2222-4333-8444-555555555555"}"""u8.ToArray(),
                timeout.Token);

            byte[] prefix = new byte[sizeof(int)];
            await pipe.ReadExactlyAsync(prefix, timeout.Token);
            int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
            Assert.InRange(length, 1, 4096);
            byte[] payload = new byte[length];
            await pipe.ReadExactlyAsync(payload, timeout.Token);
            using var json = JsonDocument.Parse(payload);
            JsonElement response = json.RootElement;
            Assert.Equal("response", response.GetProperty("frame").GetString());
            Assert.Equal(51, response.GetProperty("version").GetInt32());
            Assert.Equal(requestId, response.GetProperty("requestId").GetGuid());
            Assert.True(response.GetProperty("success").GetBoolean());
            JsonElement status = response.GetProperty("status");
            Assert.Equal(
                ["activePowerSource", "controlAccess", "deviceVerified", "liveReadings", "revision"],
                status.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.Equal(long.MaxValue, status.GetProperty("revision").GetInt64());
            Assert.True(status.GetProperty("deviceVerified").GetBoolean());
            Assert.Equal(2, status.GetProperty("activePowerSource").GetInt32());
            Assert.Equal(0, status.GetProperty("controlAccess").GetInt32());
            JsonElement readings = status.GetProperty("liveReadings");
            Assert.Equal(-32768, readings.GetProperty("batteryPowerFlowMilliwatts").GetInt32());
            Assert.Equal(JsonValueKind.Null, readings.GetProperty("fan2Rpm").ValueKind);
            Assert.Equal(0, await pipe.ReadAsync(prefix, timeout.Token));
        }

        await using (NamedPipeClientStream pipe = await ConnectAsync(endpointId, timeout.Token))
        {
            await WriteFrameAsync(pipe, "{"u8.ToArray(), timeout.Token);
            Assert.Equal(0, await pipe.ReadAsync(new byte[1], timeout.Token));
        }

        await using var client = new ServiceClient(PipeName(endpointId));
        Assert.Equal(1, (await client.GetStatusAsync(timeout.Token)).Revision);
        await host.StopAsync(timeout.Token);
    }

    [RustReadOnlyHostFact]
    public async Task Pipe_DuplicateEndpointFailsBeforeReadiness()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string endpointId = Guid.NewGuid().ToString("N");
        await using var host = DevHost.Start(endpointId);
        await host.WaitUntilReadyAsync(timeout.Token);

        await using var duplicate = DevHost.Start(endpointId);
        await duplicate.AssertRejectedAsync(timeout.Token);

        await using var client = new ServiceClient(PipeName(endpointId));
        Assert.Equal(1, (await client.GetStatusAsync(timeout.Token)).Revision);
        await host.StopAsync(timeout.Token);
    }


    [RustReadOnlyHostFact]
    public async Task TrayClient_ObservesOrderedRecoveryAndPowerChanges_AndReconnectsToLatestStatus()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string endpointId = Guid.NewGuid().ToString("N");
        await using var host = DevHost.Start(endpointId, "foreground-replay");
        await host.WaitUntilReadyAsync(timeout.Token);
        await using var client = new ServiceClient(PipeName(endpointId));
        await using var watch = client.WatchStatusAsync(timeout.Token).GetAsyncEnumerator();

        ServiceStatusSnapshot initial = await ReadStatusAsync(watch, subscriptionOpened: true);
        Assert.Equal(1, initial.Revision);
        Assert.True(initial.WritesBlocked);
        Assert.Null(initial.FunctionKeyPrimary);
        Assert.Null(initial.ConfirmedState?.Performance);
        Assert.Equal(PowerSource.PluggedIn, initial.ActivePowerSource);
        Assert.Equal(initial.Profiles.PluggedIn.Performance, initial.DesiredState.Performance.Value);
        Assert.Equal(initial.Revision, (await client.GetStatusAsync(timeout.Token)).Revision);

        await host.AdvanceAsync(timeout.Token);
        ServiceStatusSnapshot unavailable = await ReadStatusAsync(watch, subscriptionOpened: false);
        Assert.Equal(2, unavailable.Revision);
        Assert.True(unavailable.WritesBlocked);
        Assert.Null(unavailable.FunctionKeyPrimary);

        await host.AdvanceAsync(timeout.Token);
        ServiceStatusSnapshot onBattery = await ReadStatusAsync(watch, subscriptionOpened: false);
        Assert.Equal(3, onBattery.Revision);
        Assert.Equal(PowerSource.Battery, onBattery.ActivePowerSource);
        Assert.Null(onBattery.FunctionKeyPrimary);
        Assert.Equal(SettingValueSource.PowerProfile, onBattery.DesiredState.Performance.Source);
        Assert.Equal(onBattery.Profiles.Battery.Performance, onBattery.DesiredState.Performance.Value);
        Assert.NotEqual(initial.DesiredState.Performance.Value, onBattery.DesiredState.Performance.Value);
        Assert.Null(onBattery.ConfirmedState?.Performance);

        await host.AdvanceAsync(timeout.Token);
        ServiceStatusSnapshot settled = await ReadStatusAsync(watch, subscriptionOpened: false);
        Assert.Equal(4, settled.Revision);
        Assert.True(settled.WritesBlocked);
        Assert.Equal(FunctionKeyPrimaryMode.MultimediaKeys, settled.FunctionKeyPrimary);
        Assert.Equal(onBattery.DesiredState.Performance, settled.DesiredState.Performance);
        ServiceStatusSnapshot cached = await client.GetStatusAsync(timeout.Token);
        Assert.Equal(settled.Revision, cached.Revision);
        Assert.Equal(settled.FunctionKeyPrimary, cached.FunctionKeyPrimary);

        await using var reconnected = client.WatchStatusAsync(timeout.Token).GetAsyncEnumerator();
        ServiceStatusSnapshot latest = await ReadStatusAsync(reconnected, subscriptionOpened: true);
        Assert.Equal(settled.Revision, latest.Revision);
        Assert.Equal(settled.FunctionKeyPrimary, latest.FunctionKeyPrimary);

        // Exercise EOF shutdown with active subscribers and an incomplete first frame.
        await using NamedPipeClientStream partial = await ConnectAsync(endpointId, timeout.Token);
        await partial.WriteAsync(new byte[] { 1 }, timeout.Token);
        using var stopBudget = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        stopBudget.CancelAfter(TimeSpan.FromSeconds(5));
        await host.StopAsync(stopBudget.Token);
        Assert.True(await watch.MoveNextAsync());
        Assert.IsType<ServiceWatchUpdate.Reconnecting>(watch.Current);
    }

    [RustReadOnlyHostFact]
    public Task TrayClient_ReadsCommittedConfigurationAcrossProcessRestart() =>
        ExerciseDurableHostAsync(interrupt: false);

    [RustReadOnlyHostFact]
    public Task DurableHost_RefusesUnresolvedRestartAndPreservesEveryArtifact() =>
        ExerciseDurableHostAsync(interrupt: true);

    private static readonly string[] ConfigurationNames =
        ["profiles.json", "battery-preferences.json", "battery-configuration.commit.json"];

    [RustReadOnlyHostFact]
    public async Task ProfileHost_BootstrapsAndMigratesBeforePublishingAndRestartsReadOnly()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = timeout.Token;
        using Stream aggregate = typeof(RustReadOnlyHostTests).Assembly
            .GetManifestResourceStream("OpenBlade.WindowsReplay.ConfigurationAggregate")!;
        using Stream profileFixture = typeof(RustReadOnlyHostTests).Assembly
            .GetManifestResourceStream("OpenBlade.WindowsReplay.ConfigurationProfile")!;
        using JsonDocument managed = await JsonDocument.ParseAsync(aggregate, cancellationToken: token);
        JsonNode vector = (await JsonNode.ParseAsync(profileFixture, cancellationToken: token))!;
        byte[][] defaults = ReadManagedDocuments(managed.RootElement, "prior");

        foreach (bool migrate in new[] { false, true })
        {
            string endpoint = Guid.NewGuid().ToString("N");
            await using var host = DevHost.Start(endpoint,
                migrate ? "foreground-profile-migrate" : "foreground-profile-bootstrap");
            (string rootId, string identity) = await host.ReadFixtureRootAsync(token);
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Temp", $"openblade-durable-host-{rootId}");
            using SafeFileHandle pin = OpenFixtureDirectory(root);
            Assert.False(pin.IsInvalid, $"Could not pin fixture directory. Win32 {Marshal.GetLastWin32Error()}");
            try
            {
                await host.WaitUntilReadyAsync(token);
                int percent = migrate ? vector["chargeLimitPercent"]!.GetValue<int>() : 80;
                await using var client = new ServiceClient(PipeName(endpoint));
                AssertDurableStatus(await client.GetStatusAsync(token), 2, percent);

                byte[][] actual = ConfigurationNames.Select(name => File.ReadAllBytes(Path.Combine(root, name))).ToArray();
                JsonNode expectedProfile = JsonNode.Parse(defaults[0])!;
                JsonNode expectedPreferences = JsonNode.Parse(defaults[1])!;
                if (migrate)
                {
                    expectedProfile["profiles"]!["batteryPolicy"]!["chargeLimitPercent"] = percent;
                    expectedPreferences["preferences"]!["fanStopSettings"] = vector["currentFanStop"]!.DeepClone();
                }
                Assert.True(JsonNode.DeepEquals(expectedProfile, JsonNode.Parse(actual[0])));
                Assert.True(JsonNode.DeepEquals(expectedPreferences, JsonNode.Parse(actual[1])));
                using JsonDocument marker = JsonDocument.Parse(actual[2]);
                Assert.Equal(25, marker.RootElement.GetProperty("schemaVersion").GetInt32());
                Assert.Equal(Convert.ToHexString(SHA256.HashData(actual[0])),
                    marker.RootElement.GetProperty("profileSha256").GetString());
                Assert.Equal(Convert.ToHexString(SHA256.HashData(actual[1])),
                    marker.RootElement.GetProperty("preferencesSha256").GetString());

                await host.StopAsync(token);
                string restartEndpoint = Guid.NewGuid().ToString("N");
                await using var restart = DevHost.Start(restartEndpoint, "foreground-durable-restart", rootId, identity);
                await restart.WaitUntilReadyAsync(token);
                await using var reader = new ServiceClient(PipeName(restartEndpoint));
                AssertDurableStatus(await reader.GetStatusAsync(token), 2, percent);
                await restart.StopAsync(token);
                AssertPrimaryBytes(root, actual);
                Assert.Equal(4, Directory.GetFileSystemEntries(root).Length);

                if (migrate)
                {
                    // Rehash the damaged payload so envelope checks alone would pass.
                    expectedPreferences["preferences"]!["fanStopSettings"]!["endMode"] = 99;
                    actual[1] = JsonSerializer.SerializeToUtf8Bytes(expectedPreferences);
                    JsonNode damagedMarker = JsonNode.Parse(actual[2])!;
                    damagedMarker["preferencesSha256"] = Convert.ToHexString(SHA256.HashData(actual[1]));
                    actual[2] = JsonSerializer.SerializeToUtf8Bytes(damagedMarker);
                    for (int i = 1; i < actual.Length; i++)
                    {
                        await File.WriteAllBytesAsync(Path.Combine(root, ConfigurationNames[i]), actual[i], token);
                    }
                    await using var refused = DevHost.Start(Guid.NewGuid().ToString("N"),
                        "foreground-durable-restart", rootId, identity);
                    Assert.Contains("unknown Fan Stop end mode", await refused.AssertRejectedAsync(token));
                    AssertPrimaryBytes(root, actual);
                    Assert.Equal(4, Directory.GetFileSystemEntries(root).Length);
                }
            }
            finally
            {
                await host.DisposeAsync();
                foreach (string file in Directory.EnumerateFiles(root))
                {
                    File.Delete(file);
                }
                pin.Dispose();
                Directory.Delete(root);
            }
        }
    }

    private static async Task ExerciseDurableHostAsync(bool interrupt)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = timeout.Token;
        string endpoint = Guid.NewGuid().ToString("N");
        using Stream fixture = typeof(RustReadOnlyHostTests).Assembly
            .GetManifestResourceStream("OpenBlade.WindowsReplay.ConfigurationAggregate")!;
        using JsonDocument managed = await JsonDocument.ParseAsync(fixture, cancellationToken: token);
        byte[][] prior = ReadManagedDocuments(managed.RootElement, "prior");
        byte[][] committed = ReadManagedDocuments(managed.RootElement, "committed");
        string mode = interrupt ? "foreground-durable-interrupt" : "foreground-durable";
        string temp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
        // An unrelated write-access directory handle must remain compatible
        // with creation and replacements in the host's own child directory.
        using SafeFileHandle tempUser = OpenFixtureDirectory(temp, 0x4); // FILE_ADD_SUBDIRECTORY
        Assert.False(tempUser.IsInvalid, $"Could not open test Temp handle. Win32 {Marshal.GetLastWin32Error()}");
        await using var host = DevHost.Start(endpoint, mode);
        (string rootId, string identity) = await host.ReadFixtureRootAsync(token);
        string root = Path.Combine(temp, $"openblade-durable-host-{rootId}");
        // Keep the creator's directory pinned across both process lifetimes.
        using SafeFileHandle pin = OpenFixtureDirectory(root);
        Assert.False(pin.IsInvalid, $"Could not pin fixture directory. Win32 {Marshal.GetLastWin32Error()}");
        try
        {
            await host.WaitUntilReadyAsync(token);
            AssertPrimaryBytes(root, prior);
            await using var client = new ServiceClient(PipeName(endpoint));
            await using var watch = client.WatchStatusAsync(token).GetAsyncEnumerator();
            AssertDurableStatus(await ReadStatusAsync(watch, subscriptionOpened: true), 2, 80);
            await host.AdvanceAsync(token);

            if (interrupt)
            {
                Assert.Contains("fixture interrupted", await host.AssertRejectedAsync(token));
                Assert.True(await watch.MoveNextAsync());
                Assert.IsType<ServiceWatchUpdate.Reconnecting>(watch.Current);
                await AssertQuarantinedRestartAsync(root, rootId, identity, prior, committed, token);
            }
            else
            {
                AssertDurableStatus(await ReadStatusAsync(watch, subscriptionOpened: false), 3, 75);
                AssertPrimaryBytes(root, committed);
                Assert.Equal(4, Directory.GetFileSystemEntries(root).Length);
                await using (var concurrent = DevHost.Start(Guid.NewGuid().ToString("N"),
                    "foreground-durable-restart", rootId, identity))
                {
                    Assert.Contains("(os error 32)", await concurrent.AssertRejectedAsync(token));
                }
                await host.StopAsync(token);
                Assert.ThrowsAny<IOException>(() => Directory.Move(root, root + "-renamed"));
                Assert.True(await watch.MoveNextAsync());
                Assert.IsType<ServiceWatchUpdate.Reconnecting>(watch.Current);

                string restartedEndpoint = Guid.NewGuid().ToString("N");
                await using var restarted = DevHost.Start(restartedEndpoint, "foreground-durable-restart", rootId, identity);
                await restarted.WaitUntilReadyAsync(token);
                await using var reconnected = new ServiceClient(PipeName(restartedEndpoint));
                AssertDurableStatus(await reconnected.GetStatusAsync(token), 2, 75);
                await restarted.StopAsync(token);
                AssertPrimaryBytes(root, committed);

                await using var wrongRoot = DevHost.Start(Guid.NewGuid().ToString("N"),
                    "foreground-durable-restart", rootId, new string('0', 24));
                Assert.Contains("root identity changed", await wrongRoot.AssertRejectedAsync(token));
                await using var pathArgument = DevHost.Start(Guid.NewGuid().ToString("N"),
                    "foreground-durable-restart", root, identity);
                Assert.Contains("lowercase hex ID", await pathArgument.AssertRejectedAsync(token));
                await AssertJunctionRefusedAsync(root, identity, token);
                AssertPrimaryBytes(root, committed);
            }
        }
        finally
        {
            // Dispose the process before removing files, including when an assertion failed.
            await host.DisposeAsync();
            foreach (string file in Directory.EnumerateFiles(root))
            {
                File.Delete(file);
            }
            pin.Dispose();
            Directory.Delete(root);
        }
    }

    private static async Task AssertJunctionRefusedAsync(string root, string identity, CancellationToken token)
    {
        string junctionId = Guid.NewGuid().ToString("N");
        string junction = Path.Combine(Path.GetDirectoryName(root)!, $"openblade-durable-host-{junctionId}");
        // Junction creation requires neither elevation nor Developer Mode. The
        // target is the pinned fixture, so a mistaken follow would pass its identity.
        var setup = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-Command",
            "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path $env:OPENBLADE_TEST_JUNCTION -Target $env:OPENBLADE_TEST_ROOT | Out-Null" })
        {
            setup.ArgumentList.Add(argument);
        }
        setup.Environment["OPENBLADE_TEST_JUNCTION"] = junction;
        setup.Environment["OPENBLADE_TEST_ROOT"] = root;
        using Process process = Process.Start(setup)!;
        Task<string> errors = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token);
            Assert.True(process.ExitCode == 0, await errors);
            await using var redirected = DevHost.Start(Guid.NewGuid().ToString("N"),
                "foreground-durable-restart", junctionId, identity);
            Assert.Contains("not a plain directory", await redirected.AssertRejectedAsync(token));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                using var cleanupBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(cleanupBudget.Token);
            }
            if (Directory.Exists(junction))
            {
                Directory.Delete(junction);
            }
        }
    }

    private static async Task AssertQuarantinedRestartAsync(
        string root, string rootId, string identity, byte[][] prior, byte[][] committed, CancellationToken token,
        string restartMode = "foreground-durable-restart")
    {
        // Interrupt after the profile replacement and before rollback. The marker
        // and preferences are still the preimages. Backups and remaining stages
        // are identified independently from the fixed record, then compared byte for byte.
        AssertPrimaryBytes(root, [committed[0], prior[1], prior[2]]);
        string sidecar = Path.Combine(root, "battery-configuration.commit.json.recovery-required");
        using JsonDocument record = JsonDocument.Parse(await File.ReadAllBytesAsync(sidecar, token));
        Assert.Equal(1, record.RootElement.GetProperty("schemaVersion").GetInt32());
        string transactionId = record.RootElement.GetProperty("transactionId").GetString()!;
        Assert.Matches("^[0-9a-f]{32}$", transactionId);
        JsonElement targets = record.RootElement.GetProperty("targets");
        Assert.Equal(ConfigurationNames.Length, targets.GetArrayLength());
        for (int i = 0; i < ConfigurationNames.Length; i++)
        {
            string primary = Path.Combine(root, ConfigurationNames[i]);
            string artifact = $"{primary}.transaction-{transactionId}";
            Assert.Equal(primary, targets[i].GetProperty("path").GetString(), ignoreCase: true);
            Assert.True(targets[i].GetProperty("priorExists").GetBoolean());
            Assert.Equal(artifact + ".backup", targets[i].GetProperty("backupPath").GetString(), ignoreCase: true);
            Assert.Equal(prior[i], File.ReadAllBytes(artifact + ".backup"));
            Assert.False(File.Exists(artifact + ".restore"));
            if (i == 0)
            {
                Assert.False(File.Exists(artifact + ".stage"));
            }
            else
            {
                byte[] stage = File.ReadAllBytes(artifact + ".stage");
                if (i == 2 && restartMode is "foreground-battery-profile-restart" or "foreground-thermal-profile-restart")
                {
                    Assert.True(JsonNode.DeepEquals(JsonNode.Parse(committed[i]), JsonNode.Parse(stage)));
                }
                else
                {
                    Assert.Equal(committed[i], stage);
                }
            }
        }

        Dictionary<string, byte[]> artifacts = Directory.GetFiles(root).ToDictionary(path => path, File.ReadAllBytes);
        string endpoint = Guid.NewGuid().ToString("N");
        await using var refused = DevHost.Start(endpoint, restartMode, rootId, identity);
        Assert.Contains("Preserve(RecoveryRequired)", await refused.AssertRejectedAsync(token));
        using var connectBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
        connectBudget.CancelAfter(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConnectAsync(endpoint, connectBudget.Token));
        Assert.Equal(artifacts.Keys.Order(StringComparer.Ordinal), Directory.GetFiles(root).Order(StringComparer.Ordinal));
        foreach ((string path, byte[] bytes) in artifacts)
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path, token));
        }
    }

    private static byte[][] ReadManagedDocuments(JsonElement fixture, string name) =>
        fixture.GetProperty(name).EnumerateArray().Select(doc => Encoding.UTF8.GetBytes(doc.GetString()!)).ToArray();

    private static void AssertPrimaryBytes(string root, byte[][] expected)
    {
        for (int i = 0; i < ConfigurationNames.Length; i++)
        {
            Assert.Equal(expected[i], File.ReadAllBytes(Path.Combine(root, ConfigurationNames[i])));
        }
    }

    private static void AssertDurableStatus(ServiceStatusSnapshot status, long revision, int percent)
    {
        Assert.Equal(revision, status.Revision);
        Assert.Equal(percent, status.Profiles.BatteryPolicy.ChargeLimitPercent);
        Assert.Equal(percent, status.DesiredState.Battery.ConfiguredPolicy.ChargeLimitPercent);
        Assert.Null(status.ConfirmedState?.Battery);
        Assert.Null(status.FunctionKeyPrimary);
        Assert.False(status.DeviceVerified);
        Assert.True(status.WritesBlocked);
    }

    private static SafeFileHandle OpenFixtureDirectory(string root, uint access = 0x80000000) =>
        CreateFile(root, access, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open,
            0x02200000, IntPtr.Zero); // Backup semantics and open reparse point.

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string path, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

    [RustReadOnlyHostFact]
    public async Task TrayClient_BatteryParityCommitsOnlyConfirmedTemporaryIntent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = timeout.Token;
        string endpointId = Guid.NewGuid().ToString("N");
        await using var host = DevHost.Start(endpointId, "foreground-battery-parity");
        await host.WaitUntilReadyAsync(token);
        await using var client = new ServiceClient(PipeName(endpointId));

        var initial = await client.GetStatusAsync(token);
        Assert.Equal(80, initial.ConfirmedState!.Battery!.ChargeLimitPercent);
        Assert.Equal(80, initial.Profiles.BatteryPolicy.ChargeLimitPercent);
        Assert.True(initial.Capabilities!.SupportsBatteryProtectionOff);
        Assert.True(initial.Capabilities!.SupportsTemporaryFullCharge);

        const string id = "11111111-2222-4333-8444-555555555555";
        foreach (string malformed in new[]
        {
            $$$"""{"operation":"setBatteryPolicy","version":79,"requestId":"{{{id}}}","value":{"chargeLimitPercent":100}}""",
            $$$"""{"operation":"setBatteryPolicy","version":79,"requestId":"{{{id}}}","value":{"chargeLimitPercent":70,"chargeLimitPercent":75}}""",
            $$"""{"operation":"setBatteryProtection","version":79,"requestId":"{{id}}"}""",
        })
        {
            await using NamedPipeClientStream pipe = await ConnectAsync(endpointId, token);
            await WriteFrameAsync(pipe, System.Text.Encoding.UTF8.GetBytes(malformed), token);
            Assert.Equal(0, await pipe.ReadAsync(new byte[1], token));
        }

        await using (var anonymous = new NamedPipeClientStream(
            ".", PipeName(endpointId), PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous))
        {
            await anonymous.ConnectAsync(token);
            await IpcMessageCodec.WriteRequestAsync(anonymous,
                new SetBatteryProtectionRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), false), token);
            Assert.Equal(0, await anonymous.ReadAsync(new byte[1], token));
        }

        await using (NamedPipeClientStream legacy = await ConnectAsync(endpointId, token))
        {
            string request = $$"""{"operation":"setBatteryProtection","version":51,"requestId":"{{id}}","enabled":false}""";
            await WriteFrameAsync(legacy, System.Text.Encoding.UTF8.GetBytes(request), token);
            byte[] prefix = new byte[4];
            await legacy.ReadExactlyAsync(prefix, token);
            int length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
            Assert.InRange(length, 1, 4096);
            byte[] payload = new byte[length];
            await legacy.ReadExactlyAsync(payload, token);
            using var response = JsonDocument.Parse(payload);
            Assert.Equal(51, response.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(id, response.RootElement.GetProperty("requestId").GetString());
            Assert.False(response.RootElement.GetProperty("success").GetBoolean());
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => client.SetFullChargeOverrideAsync(true, token));
        Assert.Equal(initial.Revision, (await client.GetStatusAsync(token)).Revision);

        async Task ExpectLimitAsync(int percent, ControlOperationStatus expected) =>
            Assert.Equal(expected, (await client.SetBatteryPolicyAsync(BatteryPolicy.Create(percent), token)).Status);

        async Task ExpectProtectionAsync(bool enabled) =>
            Assert.Equal(ControlOperationStatus.Applied, (await client.SetBatteryProtectionAsync(enabled, token)).Status);

        await ExpectLimitAsync(75, ControlOperationStatus.Applied);
        var limit = await client.GetStatusAsync(token);
        Assert.Equal(BatteryPolicy.Create(75), limit.BatteryPolicyOverride);
        Assert.Equal(75, limit.ConfirmedState!.Battery!.ChargeLimitPercent);
        await ExpectLimitAsync(75, ControlOperationStatus.Applied);
        await ExpectLimitAsync(76, ControlOperationStatus.Blocked);
        await ExpectProtectionAsync(false);
        var off = await client.GetStatusAsync(token);
        Assert.False(off.BatteryProtectionOverride);
        Assert.False(off.ConfirmedState!.Battery!.ProtectionEnabled);
        Assert.Null(off.ConfirmedState.Battery.ChargeLimitPercent);
        Assert.Equal(75, off.DesiredState.Battery.ConfiguredPolicy.ChargeLimitPercent);
        await ExpectLimitAsync(70, ControlOperationStatus.Blocked);
        await ExpectProtectionAsync(true);
        var on = await client.GetStatusAsync(token);
        Assert.True(on.ConfirmedState!.Battery!.ProtectionEnabled);
        Assert.Equal(75, on.ConfirmedState.Battery.ChargeLimitPercent);

        await using var watch = client.WatchStatusAsync(token).GetAsyncEnumerator();
        Assert.Equal(on.Revision, (await ReadStatusAsync(watch, subscriptionOpened: true)).Revision);

        await host.AdvanceAsync(token);
        Assert.Contains("cancellation armed", (await ReadStatusAsync(watch, subscriptionOpened: false)).Message);
        await Assert.ThrowsAnyAsync<IOException>(() => client.SetBatteryProtectionAsync(false, token));
        var canceled = await client.GetStatusAsync(token);
        Assert.Empty(canceled.UnconfirmedWrites);
        Assert.Equal(on.ConfirmedState.Battery, canceled.ConfirmedState!.Battery);

        await host.AdvanceAsync(token);
        Assert.Contains("lost response armed", (await ReadStatusAsync(watch, subscriptionOpened: false)).Message);
        await ExpectLimitAsync(70, ControlOperationStatus.Failed);
        var pending = await ReadStatusAsync(watch, subscriptionOpened: false);
        Assert.True(pending.WritesBlocked);
        Assert.Equal(DeviceSubsystem.Battery, Assert.Single(pending.UnconfirmedWrites));
        Assert.Equal(75, pending.ConfirmedState!.Battery!.ChargeLimitPercent);
        Assert.Equal(75, pending.DesiredState.Battery.ConfiguredPolicy.ChargeLimitPercent);
        await ExpectLimitAsync(65, ControlOperationStatus.Blocked);
        Assert.True((await ReadStatusAsync(watch, subscriptionOpened: false)).WritesBlocked);

        await host.AdvanceAsync(token);
        var recovered = await ReadStatusAsync(watch, subscriptionOpened: false);
        Assert.Empty(recovered.UnconfirmedWrites);
        Assert.Empty(recovered.ExternalChanges);
        Assert.False(recovered.WritesBlocked);
        Assert.Equal(70, recovered.ConfirmedState!.Battery!.ChargeLimitPercent);
        Assert.Equal(70, recovered.DesiredState.Battery.ConfiguredPolicy.ChargeLimitPercent);
        Assert.Equal(BatteryPolicy.Create(70), recovered.BatteryPolicyOverride);
        Assert.Equal(80, recovered.Profiles.BatteryPolicy.ChargeLimitPercent);

        await ExpectProtectionAsync(false);
        Assert.False((await ReadStatusAsync(watch, subscriptionOpened: false)).ConfirmedState!.Battery!.ProtectionEnabled);
        await host.AdvanceAsync(token);
        var battery = await ReadStatusAsync(watch, subscriptionOpened: false);
        Assert.Equal(PowerSource.Battery, battery.ActivePowerSource);
        Assert.Null(battery.BatteryProtectionOverride);
        Assert.Equal(BatteryPolicy.Create(70), battery.BatteryPolicyOverride);
        Assert.Equal(70, battery.ConfirmedState!.Battery!.ChargeLimitPercent);
        Assert.Equal(battery.Revision, (await client.GetStatusAsync(token)).Revision);

        await host.StopAsync(token);
    }



    [RustReadOnlyHostFact]
    public Task TrayClient_ComposesMigratedProfilesWithTemporaryBatteryPolicy() =>
        ExerciseComposedBatteryAsync(interrupt: false);

    [RustReadOnlyHostFact]
    public Task TrayClient_InterruptedProfileSaveLeavesLastVerifiedBatteryStateAndBlocksRestart() =>
        ExerciseComposedBatteryAsync(interrupt: true);

    private static async Task ExerciseComposedBatteryAsync(bool interrupt)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = timeout.Token;
        string endpoint = Guid.NewGuid().ToString("N");
        string mode = interrupt ? "foreground-battery-profile-interrupt" : "foreground-battery-profile";
        await using var host = DevHost.Start(endpoint, mode);
        (string rootId, string identity) = await host.ReadFixtureRootAsync(token);
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp", $"openblade-durable-host-{rootId}");
        using SafeFileHandle pin = OpenFixtureDirectory(root);
        Assert.False(pin.IsInvalid, $"Could not pin fixture directory. Win32 {Marshal.GetLastWin32Error()}");
        try
        {
            await host.WaitUntilReadyAsync(token);
            await using var client = new ServiceClient(PipeName(endpoint));
            var initial = await client.GetStatusAsync(token);
            AssertComposedBattery(initial, saved: 77, temporary: null, confirmed: 80);
            Assert.True(initial.DeviceVerified);
            Assert.False(initial.WritesBlocked);
            byte[][] migrated = ReadPrimaryBytes();
            using Stream profileFixture = typeof(RustReadOnlyHostTests).Assembly
                .GetManifestResourceStream("OpenBlade.WindowsReplay.ConfigurationProfile")!;
            JsonNode vector = (await JsonNode.ParseAsync(profileFixture, cancellationToken: token))!;
            Assert.True(JsonNode.DeepEquals(vector["currentFanStop"],
                JsonNode.Parse(migrated[1])!["preferences"]!["fanStopSettings"]));

            var temporary = await client.SetBatteryPolicyAsync(BatteryPolicy.Create(70), token);
            Assert.Equal(ControlOperationStatus.Applied, temporary.Status);
            AssertComposedBattery(await client.GetStatusAsync(token), saved: 77, temporary: 70, confirmed: 70);
            AssertPrimaryBytes(root, migrated);

            var changed = initial.Profiles with { BatteryPolicy = BatteryPolicy.Create(75) };
            if (!interrupt)
            {
                var outsideSubset = changed with
                {
                    Battery = changed.Battery with { StopFansInBatterySaver = true },
                };
                Assert.Equal(ControlOperationStatus.Blocked,
                    (await client.UpdateProfilesAsync(outsideSubset, token)).Status);

                using var encoded = new MemoryStream();
                await IpcMessageCodec.WriteRequestAsync(encoded,
                    new UpdateProfilesRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid(), changed), token);
                string body = Encoding.UTF8.GetString(encoded.ToArray().AsSpan(sizeof(int)));
                string duplicate = body.Replace("\"chargeLimitPercent\":75",
                    "\"chargeLimitPercent\":99,\"chargeLimitPercent\":75", StringComparison.Ordinal);
                Assert.NotEqual(body, duplicate);
                await using NamedPipeClientStream pipe = await ConnectAsync(endpoint, token);
                await WriteFrameAsync(pipe, Encoding.UTF8.GetBytes(duplicate), token);
                Assert.Equal(0, await pipe.ReadAsync(new byte[1], token));
                AssertPrimaryBytes(root, migrated);
            }
            else
            {
                await LoseBatteryResponseAsync(host, client, token);
            }

            var edit = await client.UpdateProfilesAsync(changed, token);
            string restartedEndpoint = Guid.NewGuid().ToString("N");
            if (interrupt)
            {
                Assert.Equal(ControlOperationStatus.Failed, edit.Status);
                var failed = await client.GetStatusAsync(token);
                AssertComposedBattery(failed, saved: 77, temporary: 70, confirmed: 70);
                Assert.True(failed.WritesBlocked);
                Assert.Equal([DeviceSubsystem.Battery], failed.UnconfirmedWrites);
                Assert.Equal(ControlOperationStatus.Blocked,
                    (await client.SetBatteryPolicyAsync(BatteryPolicy.Create(65), token)).Status);
                Assert.Equal(ControlOperationStatus.Blocked,
                    (await client.UpdateProfilesAsync(changed, token)).Status);
                await using var observation = client.WatchStatusAsync(token).GetAsyncEnumerator();
                await ReadStatusAsync(observation, subscriptionOpened: true);
                await host.AdvanceAsync(token);
                var recovered = await ReadStatusAsync(observation, subscriptionOpened: false);
                AssertComposedBattery(recovered, saved: 77, temporary: 65, confirmed: 65);
                Assert.True(recovered.WritesBlocked);
                Assert.Empty(recovered.UnconfirmedWrites);

                await host.AdvanceAsync(token);
                var onBattery = await ReadStatusAsync(observation, subscriptionOpened: false);
                Assert.Equal(PowerSource.Battery, onBattery.ActivePowerSource);
                AssertComposedBattery(onBattery, saved: 77, temporary: 65, confirmed: 65);
                Assert.True(onBattery.WritesBlocked);

                byte[][] interrupted = ReadPrimaryBytes();
                Assert.Equal(migrated[2], interrupted[2]);
                Assert.Equal(75, JsonNode.Parse(interrupted[0])!["profiles"]!["batteryPolicy"]!
                    ["chargeLimitPercent"]!.GetValue<int>());
                JsonNode proposedMarker = JsonNode.Parse(migrated[2])!;
                proposedMarker["profileSha256"] = Convert.ToHexString(SHA256.HashData(interrupted[0]));
                interrupted[2] = JsonSerializer.SerializeToUtf8Bytes(proposedMarker);
                await host.StopAsync(token);
                await AssertQuarantinedRestartAsync(root, rootId, identity, migrated, interrupted, token,
                    "foreground-battery-profile-restart");
                return;
            }

            Assert.Equal(ControlOperationStatus.Stored, edit.Status);
            AssertComposedBattery(await client.GetStatusAsync(token), saved: 75, temporary: 70, confirmed: 70);
            byte[][] committed = ReadPrimaryBytes();
            JsonNode expected = JsonNode.Parse(migrated[0])!;
            expected["profiles"]!["batteryPolicy"]!["chargeLimitPercent"] = 75;
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(committed[0])));
            Assert.Equal(migrated[1], committed[1]);
            using var marker = JsonDocument.Parse(committed[2]);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(committed[0])),
                marker.RootElement.GetProperty("profileSha256").GetString());
            Assert.Equal(Convert.ToHexString(SHA256.HashData(committed[1])),
                marker.RootElement.GetProperty("preferencesSha256").GetString());

            await LoseBatteryResponseAsync(host, client, token);
            await using var watch = client.WatchStatusAsync(token).GetAsyncEnumerator();
            var pending = await ReadStatusAsync(watch, subscriptionOpened: true);
            AssertComposedBattery(pending, saved: 75, temporary: 70, confirmed: 70);
            Assert.True(pending.WritesBlocked);
            Assert.Equal([DeviceSubsystem.Battery], pending.UnconfirmedWrites);
            AssertPrimaryBytes(root, committed);
            await host.AdvanceAsync(token);
            var reconciled = await ReadStatusAsync(watch, subscriptionOpened: false);
            AssertComposedBattery(reconciled, saved: 75, temporary: 65, confirmed: 65);
            Assert.False(reconciled.WritesBlocked);
            Assert.Empty(reconciled.UnconfirmedWrites);
            AssertPrimaryBytes(root, committed);

            await host.StopAsync(token);
            await using var restart = DevHost.Start(restartedEndpoint,
                "foreground-battery-profile-restart", rootId, identity);
            await restart.WaitUntilReadyAsync(token);
            await using var restarted = new ServiceClient(PipeName(restartedEndpoint));
            // The new scripted device starts at its own baseline. There is no
            // persisted override or setter replay during profile startup.
            AssertComposedBattery(await restarted.GetStatusAsync(token), saved: 75, temporary: null, confirmed: 80);
            await restart.StopAsync(token);
            AssertPrimaryBytes(root, committed);
        }
        finally
        {
            await host.DisposeAsync();
            foreach (string file in Directory.EnumerateFiles(root))
            {
                File.Delete(file);
            }
            pin.Dispose();
            Directory.Delete(root);
        }

        byte[][] ReadPrimaryBytes() =>
            ConfigurationNames.Select(name => File.ReadAllBytes(Path.Combine(root, name))).ToArray();
    }

    private static async Task LoseBatteryResponseAsync(DevHost host, ServiceClient client, CancellationToken token)
    {
        await using var watch = client.WatchStatusAsync(token).GetAsyncEnumerator();
        await ReadStatusAsync(watch, subscriptionOpened: true);
        await host.AdvanceAsync(token);
        await ReadStatusAsync(watch, subscriptionOpened: false); // Arm pre-submission cancellation.
        await host.AdvanceAsync(token);
        await ReadStatusAsync(watch, subscriptionOpened: false); // Arm the lost response.
        Assert.Equal(ControlOperationStatus.Failed,
            (await client.SetBatteryPolicyAsync(BatteryPolicy.Create(65), token)).Status);
    }

    private static void AssertComposedBattery(
        ServiceStatusSnapshot status, int saved, int? temporary, int confirmed)
    {
        Assert.Equal(saved, status.Profiles.BatteryPolicy.ChargeLimitPercent);
        Assert.Equal(temporary ?? saved, status.DesiredState.Battery.ConfiguredPolicy.ChargeLimitPercent);
        Assert.Equal(temporary, status.BatteryPolicyOverride?.ChargeLimitPercent);
        Assert.Equal(new ConfirmedBatteryState(true, confirmed, false), status.ConfirmedState!.Battery);
    }
    [RustReadOnlyHostFact]
    public async Task TrayClient_PerformanceParitySeparatesRequestedAndConfirmedState()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = timeout.Token;
        string endpointId = Guid.NewGuid().ToString("N");
        await using var host = DevHost.Start(endpointId, "foreground-performance-parity");
        await host.WaitUntilReadyAsync(token);
        await using var client = new ServiceClient(PipeName(endpointId));

        var balanced = PerformancePreset.Balanced;
        var performance = PerformancePreset.Performance;
        var temporary = new ManualOverride<PerformancePreset>(performance, OverrideLifetime.UntilPowerSourceChange);
        var persistent = new ManualOverride<PerformancePreset>(performance, OverrideLifetime.Persistent);

        var initial = await client.GetStatusAsync(token);
        AssertState(initial, balanced, balanced, null);
        Assert.False(initial.WritesBlocked);
        Assert.Empty(initial.UnconfirmedWrites);

        async Task ExpectAsync(ManualOverride<PerformancePreset>? requested, ControlOperationStatus expected) =>
            Assert.Equal(expected, (await client.SetPerformanceOverrideAsync(requested, token)).Status);

        void AssertState(
            ServiceStatusSnapshot status,
            PerformancePreset confirmed,
            PerformancePreset desired,
            ManualOverride<PerformancePreset>? expectedOverride)
        {
            Assert.Equal(confirmed, status.ConfirmedState!.Performance);
            Assert.Equal(FanMode.Automatic, status.ConfirmedState.Fans);
            Assert.Equal(desired, status.DesiredState.Performance.Value);
            Assert.Equal((PerformanceOverride?)expectedOverride, status.PerformanceOverride);
            Assert.Equal(initial.Profiles, status.Profiles);
        }

        await ExpectAsync(temporary, ControlOperationStatus.Applied);
        var applied = await client.GetStatusAsync(token);
        AssertState(applied, performance, performance, temporary);
        Assert.Equal(SettingValueSource.TemporaryOverride, applied.DesiredState.Performance.Source);
        await ExpectAsync(temporary, ControlOperationStatus.Applied);
        await ExpectAsync(
            new(PerformancePreset.Silent, OverrideLifetime.Persistent),
            ControlOperationStatus.Blocked);
        Assert.Equal(
            ControlOperationStatus.Blocked,
            (await client.SetPerformanceConfigurationOverrideAsync(
                new(PerformancePreset.Custom, OverrideLifetime.Persistent, CustomPerformanceSettings.Default),
                token)).Status);
        AssertState(await client.GetStatusAsync(token), performance, performance, temporary);

        await ExpectAsync(null, ControlOperationStatus.Applied);
        AssertState(await client.GetStatusAsync(token), balanced, balanced, null);

        await using (var watch = client.WatchStatusAsync(token).GetAsyncEnumerator())
        {
            await ReadStatusAsync(watch, subscriptionOpened: true);
            await host.AdvanceAsync(token);
            Assert.Contains("cancellation armed", (await ReadStatusAsync(watch, subscriptionOpened: false)).Message);
        }

        await Assert.ThrowsAnyAsync<IOException>(() => client.SetPerformanceOverrideAsync(persistent, token));
        var canceled = await client.GetStatusAsync(token);
        AssertState(canceled, balanced, balanced, null);
        Assert.Empty(canceled.UnconfirmedWrites);
        Assert.False(canceled.WritesBlocked);

        await using var recoveryWatch = client.WatchStatusAsync(token).GetAsyncEnumerator();
        Assert.Equal(canceled.Revision, (await ReadStatusAsync(recoveryWatch, subscriptionOpened: true)).Revision);
        await host.AdvanceAsync(token);
        var armed = await ReadStatusAsync(recoveryWatch, subscriptionOpened: false);
        Assert.Contains("lost response armed", armed.Message);
        AssertState(armed, balanced, performance, persistent);
        await ExpectAsync(persistent, ControlOperationStatus.Failed);
        var pending = await ReadStatusAsync(recoveryWatch, subscriptionOpened: false);
        AssertState(pending, balanced, performance, persistent);
        Assert.True(pending.WritesBlocked);
        Assert.Equal(DeviceSubsystem.Performance, Assert.Single(pending.UnconfirmedWrites));
        await ExpectAsync(persistent, ControlOperationStatus.Blocked);
        var retry = await ReadStatusAsync(recoveryWatch, subscriptionOpened: false);
        AssertState(retry, balanced, performance, persistent);
        Assert.True(retry.WritesBlocked);
        Assert.Equal(DeviceSubsystem.Performance, Assert.Single(retry.UnconfirmedWrites));

        await host.AdvanceAsync(token);
        var recovered = await ReadStatusAsync(recoveryWatch, subscriptionOpened: false);
        AssertState(recovered, performance, performance, persistent);
        Assert.Equal(SettingValueSource.PersistentOverride, recovered.DesiredState.Performance.Source);
        Assert.Empty(recovered.UnconfirmedWrites);
        Assert.Empty(recovered.ExternalChanges);
        Assert.False(recovered.WritesBlocked);
        Assert.Equal(recovered.Revision, (await client.GetStatusAsync(token)).Revision);

        await host.StopAsync(token);
    }

    [RustReadOnlyHostFact]
    public async Task TrayClient_FanParityPreservesFixedIntentAcrossOwnedHandbackAndReconciliation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = timeout.Token;
        string endpointId = Guid.NewGuid().ToString("N");
        await using var host = DevHost.Start(endpointId, "foreground-fan-parity");
        await host.WaitUntilReadyAsync(token);
        await using var client = new ServiceClient(PipeName(endpointId));

        var request = new ManualOverride<FanMode>(FanMode.Fixed, OverrideLifetime.Persistent);
        var settings = new FanControlSettings(FanMode.Fixed, 4000, ManualFanCurve.Default);
        ServiceStatusSnapshot initial = await client.GetStatusAsync(token);
        Assert.True(initial.DeviceVerified);
        Assert.Equal(PowerSource.PluggedIn, initial.ActivePowerSource);
        Assert.Equal(PerformancePreset.Performance, initial.ConfirmedState!.Performance);
        Assert.Equal(FanMode.Automatic, initial.ConfirmedState.Fans);
        Assert.Null(initial.FanOverride);
        Assert.Null(initial.FanControlOverride);

        async Task ExpectMutationAsync(ControlOperationStatus expected)
        {
            ControlOperationResult result = await client.SetFanControlOverrideAsync(request, settings, token);
            Assert.Equal(expected, result.Status);
        }

        async Task<ServiceStatusSnapshot> AdvanceAsync()
        {
            await using var watch = client.WatchStatusAsync(token).GetAsyncEnumerator();
            ServiceStatusSnapshot before = await ReadStatusAsync(watch, subscriptionOpened: true);
            await host.AdvanceAsync(token);
            ServiceStatusSnapshot after = await ReadStatusAsync(watch, subscriptionOpened: false);
            Assert.True(after.Revision > before.Revision);
            return after;
        }

        void AssertState(ServiceStatusSnapshot status, FanMode confirmed, bool pending = false)
        {
            Assert.Equal(PerformancePreset.Performance, status.ConfirmedState!.Performance);
            Assert.Equal(confirmed, status.ConfirmedState.Fans);
            Assert.Equal(FanMode.Fixed, status.DesiredState.Fans.Value);
            Assert.Equal(SettingValueSource.PersistentOverride, status.DesiredState.Fans.Source);
            Assert.Equal(settings, status.DesiredState.FanControl);
            Assert.Equal(request, status.FanOverride);
            Assert.Equal(settings, status.FanControlOverride);
            Assert.Equal(initial.Profiles, status.Profiles);
            Assert.Equal(pending, status.WritesBlocked);
            if (pending)
            {
                Assert.Equal(DeviceSubsystem.Fans, Assert.Single(status.UnconfirmedWrites));
            }
            else
            {
                Assert.Empty(status.UnconfirmedWrites);
            }
        }

        await ExpectMutationAsync(ControlOperationStatus.Applied);
        AssertState(await client.GetStatusAsync(token), FanMode.Maximum);

        AssertState(await AdvanceAsync(), FanMode.Automatic); // Owned handback.
        AssertState(await AdvanceAsync(), FanMode.Automatic); // Arm a lost response.

        await ExpectMutationAsync(ControlOperationStatus.Failed);
        AssertState(await client.GetStatusAsync(token), FanMode.Automatic, pending: true);
        await ExpectMutationAsync(ControlOperationStatus.Blocked);
        AssertState(await client.GetStatusAsync(token), FanMode.Automatic, pending: true);

        AssertState(await AdvanceAsync(), FanMode.Automatic, pending: true); // Read a split pair.
        AssertState(await AdvanceAsync(), FanMode.Automatic); // Observe the restored pair without replay.
        await ExpectMutationAsync(ControlOperationStatus.Applied);
        AssertState(await client.GetStatusAsync(token), FanMode.Maximum);

        await host.StopAsync(token);
    }


    [RustReadOnlyHostFact]
    public Task TrayClient_ComposesDurableProfilesWithThermalTransitions() =>
        ExerciseComposedThermalAsync(interrupt: false);

    [RustReadOnlyHostFact]
    public Task TrayClient_QuarantinedProfilesAllowReadOnlyThermalReconciliation() =>
        ExerciseComposedThermalAsync(interrupt: true);

    private static async Task ExerciseComposedThermalAsync(bool interrupt)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = timeout.Token;
        string endpoint = Guid.NewGuid().ToString("N");
        await using var host = DevHost.Start(endpoint,
            interrupt ? "foreground-thermal-profile-interrupt" : "foreground-thermal-profile");
        (string rootId, string identity) = await host.ReadFixtureRootAsync(token);
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp", $"openblade-durable-host-{rootId}");
        using SafeFileHandle pin = OpenFixtureDirectory(root);
        Assert.False(pin.IsInvalid, $"Could not pin fixture directory. Win32 {Marshal.GetLastWin32Error()}");
        try
        {
            await host.WaitUntilReadyAsync(token);
            await using var client = new ServiceClient(PipeName(endpoint));
            var initial = await client.GetStatusAsync(token);
            AssertComposedBattery(initial, saved: 77, temporary: null, confirmed: 80);
            Assert.Equal(PerformancePreset.Balanced, initial.Profiles.PluggedIn.Performance);
            Assert.Equal(PerformancePreset.Balanced, initial.ConfirmedState!.Performance);
            Assert.Equal(FanMode.Automatic, initial.ConfirmedState.Fans);
            byte[][] migrated = ReadPrimaryBytes();

            var performance = new ManualOverride<PerformancePreset>(
                PerformancePreset.Performance, OverrideLifetime.UntilPowerSourceChange);
            var fan = new ManualOverride<FanMode>(FanMode.Fixed, OverrideLifetime.UntilPowerSourceChange);
            var settings = new FanControlSettings(FanMode.Fixed, 4000, ManualFanCurve.Default);

            Assert.Equal(ControlOperationStatus.Blocked, await SetFansAsync());
            Assert.Equal(ControlOperationStatus.Applied,
                (await client.SetBatteryPolicyAsync(BatteryPolicy.Create(70), token)).Status);
            Assert.Contains("cancellation armed", (await AdvanceAsync()).Message);
            await Assert.ThrowsAnyAsync<IOException>(() => client.SetPerformanceOverrideAsync(performance, token));
            var canceled = await client.GetStatusAsync(token);
            Assert.Equal(PerformancePreset.Balanced, canceled.ConfirmedState!.Performance);
            Assert.Null(canceled.PerformanceOverride);
            Assert.Empty(canceled.UnconfirmedWrites);

            var canceledObserved = await AdvanceAsync();
            Assert.Equal(PerformancePreset.Balanced, canceledObserved.ConfirmedState!.Performance);
            Assert.Equal((PerformanceOverride?)performance, canceledObserved.PerformanceOverride);
            Assert.Equal(SettingValueSource.TemporaryOverride, canceledObserved.DesiredState.Performance.Source);
            Assert.Empty(canceledObserved.UnconfirmedWrites);
            Assert.Equal(ControlOperationStatus.Blocked, await SetFansAsync());

            Assert.Equal(ControlOperationStatus.Applied,
                (await client.SetPerformanceOverrideAsync(performance, token)).Status);
            Assert.Equal(ControlOperationStatus.Applied, await SetFansAsync());
            var manual = await client.GetStatusAsync(token);
            Assert.Equal(PerformancePreset.Performance, manual.ConfirmedState!.Performance);
            Assert.Equal((PerformanceOverride?)performance, manual.PerformanceOverride);
            Assert.Equal(SettingValueSource.TemporaryOverride, manual.DesiredState.Performance.Source);
            Assert.Equal(FanMode.Maximum, manual.ConfirmedState.Fans);
            Assert.Equal(FanMode.Automatic, manual.Profiles.PluggedIn.Fans);
            Assert.Equal(settings, manual.FanControlOverride);
            Assert.Equal(SettingValueSource.TemporaryOverride, manual.DesiredState.Fans.Source);
            AssertPrimaryBytes(root, migrated);

            var released = await AdvanceAsync();
            Assert.Equal(FanMode.Automatic, released.ConfirmedState!.Fans);
            Assert.Equal(fan, released.FanOverride);
            Assert.Equal(settings, released.DesiredState.FanControl);
            Assert.Contains("response loss armed", (await AdvanceAsync()).Message);
            Assert.Equal(ControlOperationStatus.Failed, await SetFansAsync());
            var pending = await client.GetStatusAsync(token);
            Assert.Equal(FanMode.Automatic, pending.ConfirmedState!.Fans);
            Assert.Equal(DeviceSubsystem.Fans, Assert.Single(pending.UnconfirmedWrites));
            Assert.True(pending.WritesBlocked);
            Assert.Equal(ControlOperationStatus.Blocked, await SetFansAsync());
            Assert.Equal(ControlOperationStatus.Blocked,
                (await client.SetPerformanceOverrideAsync(null, token)).Status);
            AssertPrimaryBytes(root, migrated);

            var changed = initial.Profiles with { BatteryPolicy = BatteryPolicy.Create(75) };
            var edit = await client.UpdateProfilesAsync(changed, token);
            Assert.Equal(interrupt ? ControlOperationStatus.Failed : ControlOperationStatus.Stored, edit.Status);
            var stored = await client.GetStatusAsync(token);
            AssertComposedBattery(stored, saved: interrupt ? 77 : 75, temporary: 70, confirmed: 70);
            Assert.Equal(DeviceSubsystem.Fans, Assert.Single(stored.UnconfirmedWrites));

            var observed = await AdvanceAsync();
            Assert.Contains("observed without writes", observed.Message);
            Assert.Equal(FanMode.Maximum, observed.ConfirmedState!.Fans);
            Assert.Equal(settings, observed.FanControlOverride);
            Assert.Empty(observed.UnconfirmedWrites);
            Assert.Equal(interrupt, observed.WritesBlocked);
            if (interrupt)
            {
                Assert.Equal(ControlOperationStatus.Blocked, await SetFansAsync());
                Assert.Equal(ControlOperationStatus.Blocked,
                    (await client.SetPerformanceOverrideAsync(performance, token)).Status);
                Assert.Equal(ControlOperationStatus.Blocked,
                    (await client.SetBatteryPolicyAsync(BatteryPolicy.Create(65), token)).Status);
                Assert.Equal(ControlOperationStatus.Blocked,
                    (await client.UpdateProfilesAsync(changed, token)).Status);
                byte[][] interrupted = ReadPrimaryBytes();
                Assert.Equal(migrated[2], interrupted[2]);
                JsonNode proposedMarker = JsonNode.Parse(migrated[2])!;
                proposedMarker["profileSha256"] = Convert.ToHexString(SHA256.HashData(interrupted[0]));
                interrupted[2] = JsonSerializer.SerializeToUtf8Bytes(proposedMarker);
                await host.StopAsync(token);
                await AssertQuarantinedRestartAsync(root, rootId, identity, migrated, interrupted, token,
                    "foreground-thermal-profile-restart");
                return;
            }

            Assert.Equal(ControlOperationStatus.Blocked, await SetFansAsync());
            var unowned = await AdvanceAsync();
            Assert.Contains("No confirmed OpenBlade-owned manual fans", unowned.Message);
            Assert.Equal(FanMode.Maximum, unowned.ConfirmedState!.Fans);
            Assert.Equal(settings, unowned.DesiredState.FanControl);
            byte[][] committed = ReadPrimaryBytes();
            JsonNode expected = JsonNode.Parse(migrated[0])!;
            expected["profiles"]!["batteryPolicy"]!["chargeLimitPercent"] = 75;
            Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(committed[0])));
            Assert.Equal(migrated[1], committed[1]);
            await host.StopAsync(token);

            string restartEndpoint = Guid.NewGuid().ToString("N");
            await using var restart = DevHost.Start(restartEndpoint,
                "foreground-thermal-profile-restart", rootId, identity);
            await restart.WaitUntilReadyAsync(token);
            await using var restarted = new ServiceClient(PipeName(restartEndpoint));
            var fresh = await restarted.GetStatusAsync(token);
            AssertComposedBattery(fresh, saved: 75, temporary: null, confirmed: 80);
            Assert.Equal(PerformancePreset.Balanced, fresh.ConfirmedState!.Performance);
            Assert.Equal(FanMode.Automatic, fresh.ConfirmedState.Fans);
            Assert.Null(fresh.PerformanceOverride);
            Assert.Null(fresh.FanOverride);
            Assert.Empty(fresh.UnconfirmedWrites);
            await restart.StopAsync(token);
            AssertPrimaryBytes(root, committed);

            async Task<ControlOperationStatus> SetFansAsync() =>
                (await client.SetFanControlOverrideAsync(fan, settings, token)).Status;

            async Task<ServiceStatusSnapshot> AdvanceAsync()
            {
                await using var watch = client.WatchStatusAsync(token).GetAsyncEnumerator();
                await ReadStatusAsync(watch, subscriptionOpened: true);
                await host.AdvanceAsync(token);
                return await ReadStatusAsync(watch, subscriptionOpened: false);
            }
        }
        finally
        {
            await host.DisposeAsync();
            foreach (string file in Directory.EnumerateFiles(root))
            {
                File.Delete(file);
            }
            pin.Dispose();
            Directory.Delete(root);
        }

        byte[][] ReadPrimaryBytes() =>
            ConfigurationNames.Select(name => File.ReadAllBytes(Path.Combine(root, name))).ToArray();
    }

    private static string PipeName(string endpointId) => $"OpenBlade.Rust.ReadOnly.Dev.{endpointId}";

    private static async Task<ServiceStatusSnapshot> ReadStatusAsync(
        IAsyncEnumerator<ServiceWatchUpdate> watch,
        bool subscriptionOpened)
    {
        Assert.True(await watch.MoveNextAsync());
        var connected = Assert.IsType<ServiceWatchUpdate.Connected>(watch.Current);
        Assert.Equal(subscriptionOpened, connected.SubscriptionOpened);
        return connected.Status;
    }

    private static void AssertBackground(ServiceStatusSnapshot status)
    {
        Assert.Equal(SessionControlAccess.InactiveSession, status.ControlAccess);
        Assert.Equal("Another Windows session currently controls OpenBlade hardware.", status.Message);
        Assert.Null(status.PerformanceOverride);
        Assert.Null(status.FanOverride);
        Assert.Null(status.LightingOverride);
        Assert.False(status.FullChargeOverride);
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string endpointId, CancellationToken token)
    {
        var pipe = new NamedPipeClientStream(
            ".", PipeName(endpointId), PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(token);
            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }

    private static async Task WriteFrameAsync(Stream pipe, byte[] payload, CancellationToken token)
    {
        byte[] frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, sizeof(int));
        await pipe.WriteAsync(frame, token);
    }

    private sealed class DevHost : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Task<string> standardError;
        private bool disposed;

        private DevHost(Process process)
        {
            this.process = process;
            standardError = process.StandardError.ReadToEndAsync();
        }

        internal static DevHost Start(string endpointId, string mode = "foreground", params string[] arguments)
        {
            string path = Environment.GetEnvironmentVariable(RustReadOnlyHostFactAttribute.PathVariable)!;
            Assert.True(
                Path.IsPathFullyQualified(path)
                && string.Equals(Path.GetFileName(path), "openblade-windows-host.exe", StringComparison.OrdinalIgnoreCase)
                && File.Exists(path),
                $"{RustReadOnlyHostFactAttribute.PathVariable} must name an existing absolute path to openblade-windows-host.exe.");

            var startInfo = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add(mode);
            startInfo.ArgumentList.Add(endpointId);
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
            return new DevHost(Process.Start(startInfo)!);
        }

        internal async Task<(string Root, string Identity)> ReadFixtureRootAsync(CancellationToken token)
        {
            string? line = await process.StandardOutput.ReadLineAsync(token);
            if (line is null)
            {
                throw new InvalidOperationException($"Fixture creation failed. {await standardError.WaitAsync(token)}");
            }

            Assert.Matches("^ROOT [0-9a-f]{32} [0-9a-f]{24}$", line);
            string[] parts = line.Split(' ');
            return (parts[1], parts[2]);
        }

        internal async Task WaitUntilReadyAsync(CancellationToken token)
        {
            string? line = await process.StandardOutput.ReadLineAsync(token);
            if (line is null)
            {
                throw new InvalidOperationException(
                    $"The Rust development host exited before readiness. {await standardError.WaitAsync(token)}");
            }

            Assert.Equal("READY", line);
        }


        internal async Task AdvanceAsync(CancellationToken token)
        {
            await process.StandardInput.BaseStream.WriteAsync(new byte[] { (byte)'n' }, token);
            await process.StandardInput.BaseStream.FlushAsync(token);
        }

        internal async Task StopAsync(CancellationToken token)
        {
            process.StandardInput.Close();
            await process.WaitForExitAsync(token);
            Assert.True(process.ExitCode == 0, await standardError.WaitAsync(token));
        }

        internal async Task<string> AssertRejectedAsync(CancellationToken token)
        {
            await process.WaitForExitAsync(token);
            Assert.NotEqual(0, process.ExitCode);
            Assert.Null(await process.StandardOutput.ReadLineAsync(token));
            return await standardError.WaitAsync(token);
        }

        public async ValueTask DisposeAsync()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            using (process)
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(timeout.Token);
                }
            }
        }
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class RustReadOnlyHostFactAttribute : FactAttribute
{
    internal const string PathVariable = "OPENBLADE_RUST_DEV_HOST_EXE";

    public RustReadOnlyHostFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PathVariable)))
        {
            Skip = $"Requires Windows and an explicit {PathVariable} path.";
        }
    }
}
