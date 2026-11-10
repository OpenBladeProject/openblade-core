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
