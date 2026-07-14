using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using OpenSynapse.Contracts;
using OpenSynapse.Service;

namespace OpenSynapse.Service.Tests;

public sealed class NamedPipeControlServerTests
{
    [Fact]
    public async Task ServeOnceAsync_RoundTripsTypedRequestOverAclProtectedPipe()
    {
        TestServer context = CreateServer();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task serverTask = context.Server.ServeOnceAsync(timeout.Token);
        await using NamedPipeClientStream client = await ConnectAsync(context.PipeName, timeout.Token);
        var request = new GetStatusRequest(2, Guid.NewGuid());

        await IpcMessageCodec.WriteRequestAsync(client, request, timeout.Token);
        ServiceResponse response = Assert.IsType<ServiceResponse>(
            await IpcMessageCodec.ReadServerFrameAsync(client, timeout.Token));
        await serverTask;

        Assert.True(response.Success);
        Assert.Equal(request.RequestId, response.RequestId);
        Assert.NotNull(response.Status);
    }

    [Theory]
    [MemberData(nameof(InvalidMessages))]
    public async Task ServeOnceAsync_ReturnsFailureForMalformedFrame(byte[] message)
    {
        TestServer context = CreateServer();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task serverTask = context.Server.ServeOnceAsync(timeout.Token);
        await using NamedPipeClientStream client = await ConnectAsync(context.PipeName, timeout.Token);

        await client.WriteAsync(message, timeout.Token);
        await client.FlushAsync(timeout.Token);
        ServiceResponse response = Assert.IsType<ServiceResponse>(
            await IpcMessageCodec.ReadServerFrameAsync(client, timeout.Token));
        await serverTask;

        Assert.False(response.Success);
        Assert.Equal(OpenSynapseIpc.ProtocolVersion, response.Version);
    }

    [Fact]
    public async Task Subscribe_WritesInitialResponseThenNewStatusEvents()
    {
        TestServer context = CreateServer();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task serverTask = context.Server.ServeOnceAsync(timeout.Token);
        await using NamedPipeClientStream client = await ConnectAsync(context.PipeName, timeout.Token);
        var request = new SubscribeStatusRequest(2, Guid.NewGuid());
        await IpcMessageCodec.WriteRequestAsync(client, request, timeout.Token);

        ServiceResponse initial = Assert.IsType<ServiceResponse>(
            await IpcMessageCodec.ReadServerFrameAsync(client, timeout.Token));
        context.Hub.Publish(CreateStatus(1));
        ServiceStatusEvent update = Assert.IsType<ServiceStatusEvent>(
            await IpcMessageCodec.ReadServerFrameAsync(client, timeout.Token));

        Assert.True(initial.Success);
        Assert.Equal(0, initial.Status!.Revision);
        Assert.Equal(1, update.Status.Revision);
        timeout.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => serverTask);
    }

    [Fact]
    public async Task Subscribe_WhenFourLeasesAreActive_ReturnsFailureWithoutLeakingSlot()
    {
        TestServer context = CreateServer();
        StatusSubscriptionLease[] leases =
        [
            context.Hub.Subscribe(CancellationToken.None),
            context.Hub.Subscribe(CancellationToken.None),
            context.Hub.Subscribe(CancellationToken.None),
            context.Hub.Subscribe(CancellationToken.None),
        ];
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            Task serverTask = context.Server.ServeOnceAsync(timeout.Token);
            await using NamedPipeClientStream client = await ConnectAsync(context.PipeName, timeout.Token);
            await IpcMessageCodec.WriteRequestAsync(
                client,
                new SubscribeStatusRequest(2, Guid.NewGuid()),
                timeout.Token);

            ServiceResponse response = Assert.IsType<ServiceResponse>(
                await IpcMessageCodec.ReadServerFrameAsync(client, timeout.Token));
            await serverTask;

            Assert.False(response.Success);
            Assert.Contains("maximum", response.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (StatusSubscriptionLease lease in leases)
            {
                await lease.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task ServeConnectionAsync_CancelsBlockedFrameWriteAtTwoSeconds()
    {
        var time = new ManualTimeProvider();
        TestServer context = CreateServer(time);
        await using var stream = new BlockingWriteStream(
            await EncodeAsync(new GetStatusRequest(2, Guid.NewGuid())));

        Task serving = context.Server.ServeConnectionAsync(stream, CancellationToken.None);
        await stream.WriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromSeconds(2));

        await serving.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(stream.WriteCancellationObserved);
    }

    [Fact]
    public async Task Subscribe_DisconnectDisposesLeaseAndReleasesCapacity()
    {
        TestServer context = CreateServer();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task serverTask = context.Server.ServeOnceAsync(timeout.Token);
        NamedPipeClientStream client = await ConnectAsync(context.PipeName, timeout.Token);
        await IpcMessageCodec.WriteRequestAsync(
            client,
            new SubscribeStatusRequest(2, Guid.NewGuid()),
            timeout.Token);
        _ = await IpcMessageCodec.ReadServerFrameAsync(client, timeout.Token);
        await client.DisposeAsync();
        await serverTask.WaitAsync(timeout.Token);

        StatusSubscriptionLease[] leases =
        [
            context.Hub.Subscribe(CancellationToken.None),
            context.Hub.Subscribe(CancellationToken.None),
            context.Hub.Subscribe(CancellationToken.None),
            context.Hub.Subscribe(CancellationToken.None),
        ];
        Assert.Throws<InvalidOperationException>(() => context.Hub.Subscribe(CancellationToken.None));
        foreach (StatusSubscriptionLease lease in leases)
        {
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task Subscribe_CompletesWhenFifteenMinuteLeaseExpires()
    {
        var time = new ManualTimeProvider();
        TestServer context = CreateServer(time);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task serverTask = context.Server.ServeOnceAsync(timeout.Token);
        await using NamedPipeClientStream client = await ConnectAsync(context.PipeName, timeout.Token);
        await IpcMessageCodec.WriteRequestAsync(
            client,
            new SubscribeStatusRequest(2, Guid.NewGuid()),
            timeout.Token);
        _ = await IpcMessageCodec.ReadServerFrameAsync(client, timeout.Token);

        time.Advance(TimeSpan.FromMinutes(15));

        await serverTask.WaitAsync(timeout.Token);
    }

    public static TheoryData<byte[]> InvalidMessages()
    {
        TheoryData<byte[]> data = [];
        data.Add(Frame(Encoding.UTF8.GetBytes("null")));
        data.Add(Frame(Encoding.UTF8.GetBytes(
            "{\"version\":2,\"requestId\":\"23c09fe2-cbb8-4b74-a175-78dfb7831cf1\",\"operation\":\"unknown\"}")));
        data.Add(Frame(Encoding.UTF8.GetBytes(
            "{\"version\":1,\"requestId\":\"23c09fe2-cbb8-4b74-a175-78dfb7831cf1\",\"operation\":\"getStatus\"}")));
        data.Add(Frame(Encoding.UTF8.GetBytes(
            "{\"version\":2,\"requestId\":\"00000000-0000-0000-0000-000000000000\",\"operation\":\"getStatus\"}")));
        byte[] oversized = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(oversized, OpenSynapseIpc.MaximumMessageBytes + 1);
        data.Add(oversized);
        return data;
    }

    private static TestServer CreateServer(TimeProvider? timeProvider = null)
    {
        string pipeName = $"OpenSynapse.Tests.{Guid.NewGuid():N}";
        var runtime = new StubRuntime(CreateStatus(0));
        TimeProvider time = timeProvider ?? TimeProvider.System;
        var hub = new StatusSubscriptionHub(runtime.Status, time);
        var server = new NamedPipeControlServer(
            pipeName,
            WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Current user has no SID."),
            new ControlRequestHandler(runtime),
            hub,
            time);
        return new TestServer(pipeName, server, hub);
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(
        string pipeName,
        CancellationToken token)
    {
        var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(token);
        return client;
    }

    private static async Task<byte[]> EncodeAsync(ServiceRequest request)
    {
        await using var stream = new MemoryStream();
        await IpcMessageCodec.WriteRequestAsync(stream, request, CancellationToken.None);
        return stream.ToArray();
    }

    private static byte[] Frame(byte[] payload)
    {
        byte[] frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, sizeof(int));
        return frame;
    }

    private static ServiceStatusSnapshot CreateStatus(long revision)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        return new ServiceStatusSnapshot(
            revision, false, true, [], "Unavailable", DeviceCapabilities.Unavailable(), null,
            profiles,
            new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(FanMode.Automatic, SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
                profiles.BatteryPolicy),
            new TelemetrySnapshot(DateTimeOffset.UnixEpoch, PowerSource.PluggedIn, null, null, null, null),
            null, null, null, false);
    }

    private sealed record TestServer(
        string PipeName,
        NamedPipeControlServer Server,
        StatusSubscriptionHub Hub);

    private sealed class StubRuntime(ServiceStatusSnapshot status) : IServiceControlRuntime
    {
        public ServiceStatusSnapshot Status { get; } = status;
        public ServiceStatusSnapshot GetStatus() => Status;
        public Task<ControlOperationResult> UpdateProfilesAsync(ProfileSet profiles, CancellationToken token) => Result();
        public Task<ControlOperationResult> SetPerformanceOverrideAsync(ManualOverride<PerformancePreset>? value, CancellationToken token) => Result();
        public Task<ControlOperationResult> SetFanOverrideAsync(ManualOverride<FanMode>? value, CancellationToken token) => Result();
        public Task<ControlOperationResult> SetLightingOverrideAsync(ManualOverride<LightingSettings>? value, CancellationToken token) => Result();
        public Task<ControlOperationResult> SetBatteryPolicyAsync(BatteryPolicy value, CancellationToken token) => Result();
        public Task<ControlOperationResult> SetFullChargeOverrideAsync(bool enabled, CancellationToken token) => Result();
        public Task RefreshAsync(CancellationToken token) => Task.CompletedTask;
        private static Task<ControlOperationResult> Result() => Task.FromResult(
            new ControlOperationResult(ControlOperationStatus.Stored, "Stored"));
    }

    private sealed class BlockingWriteStream(byte[] input) : Stream
    {
        private readonly MemoryStream reader = new(input, writable: false);
        public TaskCompletionSource WriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool WriteCancellationObserved { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => reader.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            reader.ReadAsync(buffer, token);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            WriteStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            catch (OperationCanceledException)
            {
                WriteCancellationObserved = true;
                throw;
            }
        }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) reader.Dispose(); base.Dispose(disposing); }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> timers = [];
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, now + dueTime, period);
            timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan amount)
        {
            now += amount;
            foreach (ManualTimer timer in timers.ToArray()) timer.FireIfDue(now);
        }
        private sealed class ManualTimer(TimerCallback callback, object? state, DateTimeOffset due, TimeSpan period) : ITimer
        {
            private bool disposed;
            private DateTimeOffset next = due;
            public void FireIfDue(DateTimeOffset now)
            {
                if (disposed || now < next) return;
                callback(state);
                if (period == Timeout.InfiniteTimeSpan) disposed = true;
                else next = now + period;
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
