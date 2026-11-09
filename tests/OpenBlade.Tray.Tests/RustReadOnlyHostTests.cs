using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
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
        await using var host = DevHost.Start(endpoint, mode);
        (string rootId, string identity) = await host.ReadFixtureRootAsync(token);
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Temp", $"openblade-durable-host-{rootId}");
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
        string root, string rootId, string identity, byte[][] prior, byte[][] committed, CancellationToken token)
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
                Assert.Equal(committed[i], File.ReadAllBytes(artifact + ".stage"));
            }
        }

        Dictionary<string, byte[]> artifacts = Directory.GetFiles(root).ToDictionary(path => path, File.ReadAllBytes);
        string endpoint = Guid.NewGuid().ToString("N");
        await using var refused = DevHost.Start(endpoint, "foreground-durable-restart", rootId, identity);
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

    private static SafeFileHandle OpenFixtureDirectory(string root) =>
        CreateFile(root, 0x80000000, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open,
            0x02200000, IntPtr.Zero); // Generic read, backup semantics, open reparse point.

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string path, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

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
