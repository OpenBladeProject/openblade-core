using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
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

        private DevHost(Process process)
        {
            this.process = process;
            standardError = process.StandardError.ReadToEndAsync();
        }

        internal static DevHost Start(string endpointId, string mode = "foreground")
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
            return new DevHost(Process.Start(startInfo)!);
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

        internal async Task AssertRejectedAsync(CancellationToken token)
        {
            await process.WaitForExitAsync(token);
            Assert.NotEqual(0, process.ExitCode);
            Assert.Null(await process.StandardOutput.ReadLineAsync(token));
        }

        public async ValueTask DisposeAsync()
        {
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
