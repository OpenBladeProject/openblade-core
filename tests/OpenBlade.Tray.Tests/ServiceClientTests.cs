using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using OpenBlade.Contracts;
using OpenBlade.Tray;

namespace OpenBlade.Tray.Tests;

public sealed class ServiceClientTests
{
    [Fact]
    public async Task GetStatusAsync_ValidatesVersionRequestIdAndResponseSubtype()
    {
        string pipeName = NewPipeName();
        ServiceStatusSnapshot expected = CreateStatus(3);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task server = RunServerAsync(
            pipeName,
            request =>
            {
                Assert.IsType<GetStatusRequest>(request);
                Assert.Equal(OpenBladeIpc.ProtocolVersion, request.Version);
                Assert.NotEqual(Guid.Empty, request.RequestId);
                return new ServiceResponse(2, request.RequestId, true, null, expected, null);
            },
            timeout.Token);
        IServiceClient client = new ServiceClient(pipeName);

        ServiceStatusSnapshot actual = await client.GetStatusAsync(timeout.Token);
        await server;

        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(expected.DeviceVerified, actual.DeviceVerified);
        Assert.Equal(expected.WritesBlocked, actual.WritesBlocked);
        Assert.Equal(expected.Conflicts, actual.Conflicts);
        Assert.Equal(expected.Message, actual.Message);
    }

    [Fact]
    public async Task GetStatusAsync_RejectsMismatchedResponseId()
    {
        await AssertInvalidStatusResponseAsync(request => new ServiceResponse(
            2, Guid.NewGuid(), true, null, CreateStatus(0), null));
    }

    [Fact]
    public async Task GetStatusAsync_RejectsWrongServerFrameSubtype()
    {
        await AssertInvalidStatusResponseAsync(_ =>
            new ServiceStatusEvent(2, CreateStatus(1)));
    }

    [Fact]
    public async Task GetStatusAsync_RejectsNonSuccessResponse()
    {
        await AssertInvalidStatusResponseAsync(request => new ServiceResponse(
            2, request.RequestId, false, "Unavailable", null, null));
    }

    [Fact]
    public async Task GetStatusAsync_RejectsVersionMismatch()
    {
        string pipeName = NewPipeName();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task server = RunRawServerAsync(
            pipeName,
            request => Frame(Encoding.UTF8.GetBytes(
                $"{{\"frame\":\"response\",\"version\":1,\"requestId\":\"{request.RequestId}\",\"success\":false,\"error\":\"bad version\",\"status\":null,\"result\":null}}")),
            timeout.Token);
        IServiceClient client = new ServiceClient(pipeName);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetStatusAsync(timeout.Token));
        await server;
    }

    [Fact]
    public async Task TypedMutations_SendEveryClosedMutationRequestOnFreshConnections()
    {
        string pipeName = NewPipeName();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        var observed = new List<ServiceRequest>();
        Task server = RunMutationServersAsync(pipeName, observed, timeout.Token);
        IServiceClient client = new ServiceClient(pipeName);
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();

        ControlOperationResult[] results =
        [
            await client.UpdateProfilesAsync(profiles, timeout.Token),
            await client.SetPerformanceOverrideAsync(
                new ManualOverride<PerformancePreset>(PerformancePreset.Performance, OverrideLifetime.Persistent),
                timeout.Token),
            await client.SetFanOverrideAsync(
                new ManualOverride<FanMode>(FanMode.Maximum, OverrideLifetime.UntilPowerSourceChange),
                timeout.Token),
            await client.SetLightingOverrideAsync(null, timeout.Token),
            await client.SetBatteryPolicyAsync(BatteryPolicy.Create(75), timeout.Token),
            await client.SetFullChargeOverrideAsync(true, timeout.Token),
        ];
        await server;

        Assert.All(results, result => Assert.Equal(ControlOperationStatus.Stored, result.Status));
        Assert.Collection(
            observed,
            request => Assert.IsType<UpdateProfilesRequest>(request),
            request => Assert.IsType<SetPerformanceOverrideRequest>(request),
            request => Assert.IsType<SetFanOverrideRequest>(request),
            request => Assert.IsType<SetLightingOverrideRequest>(request),
            request => Assert.IsType<SetBatteryPolicyRequest>(request),
            request => Assert.IsType<SetFullChargeOverrideRequest>(request));
        Assert.All(observed, request => Assert.NotEqual(Guid.Empty, request.RequestId));
        Assert.Equal(6, observed.Select(request => request.RequestId).Distinct().Count());
    }

    [Fact]
    public async Task MutationIoFailure_IsNeverRetried()
    {
        int attempts = 0;
        var client = new ServiceClient(
            _ =>
            {
                attempts++;
                throw new IOException("offline");
            },
            new FakeTimeProvider());

        await Assert.ThrowsAsync<IOException>(() =>
            client.SetFullChargeOverrideAsync(true, CancellationToken.None));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task WatchStatusAsync_YieldsInitialResponseThenStatusEvents()
    {
        string pipeName = NewPipeName();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task server = RunSubscriptionServerAsync(
            pipeName,
            CreateStatus(4),
            [CreateStatus(5), CreateStatus(6)],
            timeout.Token);
        IServiceClient client = new ServiceClient(pipeName);
        await using IAsyncEnumerator<ServiceWatchUpdate> reader =
            client.WatchStatusAsync(timeout.Token).GetAsyncEnumerator();

        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(4, Assert.IsType<ServiceWatchUpdate.Connected>(reader.Current).Status.Revision);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(5, Assert.IsType<ServiceWatchUpdate.Connected>(reader.Current).Status.Revision);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(6, Assert.IsType<ServiceWatchUpdate.Connected>(reader.Current).Status.Revision);
        await server;
    }

    [Fact]
    public async Task GetStatusAsync_RejectsOversizedFramePrefix()
    {
        string pipeName = NewPipeName();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task server = RunRawServerAsync(
            pipeName,
            _ =>
            {
                byte[] prefix = new byte[sizeof(int)];
                BinaryPrimitives.WriteInt32LittleEndian(
                    prefix,
                    OpenBladeIpc.MaximumMessageBytes + 1);
                return prefix;
            },
            timeout.Token);
        IServiceClient client = new ServiceClient(pipeName);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetStatusAsync(timeout.Token));
        await server;
    }

    [Fact]
    public async Task CancellationDuringConnect_ExitsImmediatelyWithoutRetryDelay()
    {
        var time = new FakeTimeProvider();
        int attempts = 0;
        var client = new ServiceClient(
            async token =>
            {
                attempts++;
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new InvalidOperationException("Unreachable.");
            },
            time);
        using CancellationTokenSource cancellation = new();
        await using IAsyncEnumerator<ServiceWatchUpdate> reader =
            client.WatchStatusAsync(cancellation.Token).GetAsyncEnumerator();
        Task<bool> move = reader.MoveNextAsync().AsTask();
        await WaitUntilAsync(() => attempts == 1);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.Empty(time.Delays);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task CancellationDuringSubscriptionHandshake_DisposesAcquiredStream()
    {
        var stream = new BlockingHandshakeStream();
        int attempts = 0;
        var client = new ServiceClient(
            _ =>
            {
                attempts++;
                return ValueTask.FromResult<Stream>(stream);
            },
            new FakeTimeProvider());
        using CancellationTokenSource cancellation = new();
        await using IAsyncEnumerator<ServiceWatchUpdate> reader =
            client.WatchStatusAsync(cancellation.Token).GetAsyncEnumerator();
        Task<bool> move = reader.MoveNextAsync().AsTask();
        await stream.ReadStarted.Task;

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.True(stream.Disposed);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task WatchStatusAsync_ReconnectsAfterDisconnect()
    {
        string pipeName = NewPipeName();
        var time = new FakeTimeProvider();
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(5));
        Task firstServer = RunSubscriptionServerAsync(
            pipeName, CreateStatus(1), [], cancellation.Token);
        IServiceClient client = new ServiceClient(pipeName, time);
        await using IAsyncEnumerator<ServiceWatchUpdate> reader =
            client.WatchStatusAsync(cancellation.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(1, Assert.IsType<ServiceWatchUpdate.Connected>(reader.Current).Status.Revision);
        await firstServer;
        Task<bool> reconnectMove = reader.MoveNextAsync().AsTask();
        Assert.True(await reconnectMove);
        Assert.IsType<ServiceWatchUpdate.Reconnecting>(reader.Current);
        reconnectMove = reader.MoveNextAsync().AsTask();
        await WaitUntilAsync(() => time.Delays.Count == 1);
        Task secondServer = RunSubscriptionServerAsync(
            pipeName, CreateStatus(2), [], cancellation.Token);

        time.Advance(TimeSpan.FromMilliseconds(250));

        Assert.True(await reconnectMove);
        Assert.Equal(2, Assert.IsType<ServiceWatchUpdate.Connected>(reader.Current).Status.Revision);
        await secondServer;
    }

    [Fact]
    public async Task WatchStatusAsync_UsesExponentialReconnectBackoffCappedAtFiveSeconds()
    {
        var time = new FakeTimeProvider();
        int attempts = 0;
        var client = new ServiceClient(
            _ =>
            {
                attempts++;
                throw new IOException("offline");
            },
            time);
        using CancellationTokenSource cancellation = new();
        await using IAsyncEnumerator<ServiceWatchUpdate> reader =
            client.WatchStatusAsync(cancellation.Token).GetAsyncEnumerator();
        Task<bool> move = reader.MoveNextAsync().AsTask();
        TimeSpan[] expected =
        [
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(5),
        ];

        for (int index = 0; index < expected.Length; index++)
        {
            Assert.True(await move);
            Assert.IsType<ServiceWatchUpdate.Reconnecting>(reader.Current);
            move = reader.MoveNextAsync().AsTask();
            await WaitUntilAsync(() => time.Delays.Count == index + 1);
            Assert.Equal(expected[index], time.Delays[index]);
            time.Advance(expected[index]);
        }

        Assert.True(await move);
        Assert.IsType<ServiceWatchUpdate.Reconnecting>(reader.Current);
        move = reader.MoveNextAsync().AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move);
        Assert.Equal(expected.Length + 1, attempts);
    }

    [Fact]
    public async Task WatchStatusAsync_ProtocolFailureTerminatesWithoutReconnect()
    {
        int attempts = 0;
        var stream = new ScriptedStream(Frame(Encoding.UTF8.GetBytes("not-json")));
        var client = new ServiceClient(
            _ =>
            {
                attempts++;
                return ValueTask.FromResult<Stream>(stream);
            },
            new FakeTimeProvider());
        await using IAsyncEnumerator<ServiceWatchUpdate> reader =
            client.WatchStatusAsync(CancellationToken.None).GetAsyncEnumerator();

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.MoveNextAsync().AsTask());

        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WatchStatusAsync_VersionOrNonSuccessFailureTerminatesWithoutReconnect(
        bool wrongVersion)
    {
        string pipeName = NewPipeName();
        int attempts = 0;
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task server = wrongVersion
            ? RunRawServerAsync(
                pipeName,
                request => Frame(Encoding.UTF8.GetBytes(
                    $"{{\"frame\":\"response\",\"version\":1,\"requestId\":\"{request.RequestId}\",\"success\":false,\"error\":\"bad version\",\"status\":null,\"result\":null}}")),
                timeout.Token)
            : RunServerAsync(
                pipeName,
                request => new ServiceResponse(
                    OpenBladeIpc.ProtocolVersion,
                    request.RequestId,
                    false,
                    "Rejected",
                    null,
                    null),
                timeout.Token);
        var client = new ServiceClient(
            async token =>
            {
                attempts++;
                var pipe = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(token);
                return pipe;
            },
            new FakeTimeProvider());
        await using IAsyncEnumerator<ServiceWatchUpdate> reader =
            client.WatchStatusAsync(timeout.Token).GetAsyncEnumerator();

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.MoveNextAsync().AsTask());
        await server;

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task WatchStatusAsync_OversizedFrameTerminatesWithoutReconnect()
    {
        int attempts = 0;
        byte[] prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(
            prefix,
            OpenBladeIpc.MaximumMessageBytes + 1);
        var client = new ServiceClient(
            _ =>
            {
                attempts++;
                return ValueTask.FromResult<Stream>(new ScriptedStream(prefix));
            },
            new FakeTimeProvider());
        await using IAsyncEnumerator<ServiceWatchUpdate> reader =
            client.WatchStatusAsync(CancellationToken.None).GetAsyncEnumerator();

        await Assert.ThrowsAsync<InvalidDataException>(() => reader.MoveNextAsync().AsTask());

        Assert.Equal(1, attempts);
    }

    private static async Task AssertInvalidStatusResponseAsync(
        Func<ServiceRequest, ServiceServerFrame> response)
    {
        string pipeName = NewPipeName();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        Task server = RunServerAsync(pipeName, response, timeout.Token);
        IServiceClient client = new ServiceClient(pipeName);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => client.GetStatusAsync(timeout.Token));
        await server;
    }

    private static async Task RunMutationServersAsync(
        string pipeName,
        List<ServiceRequest> observed,
        CancellationToken token)
    {
        for (int index = 0; index < 6; index++)
        {
            await using NamedPipeServerStream server = CreateServer(pipeName);
            await server.WaitForConnectionAsync(token);
            ServiceRequest request = await IpcMessageCodec.ReadRequestAsync(server, token);
            observed.Add(request);
            await IpcMessageCodec.WriteServerFrameAsync(
                server,
                new ServiceResponse(
                    2,
                    request.RequestId,
                    true,
                    null,
                    null,
                    new ControlOperationResult(ControlOperationStatus.Stored, "Stored")),
                token);
        }
    }

    private static async Task RunServerAsync(
        string pipeName,
        Func<ServiceRequest, ServiceServerFrame> response,
        CancellationToken token)
    {
        await using NamedPipeServerStream server = CreateServer(pipeName);
        await server.WaitForConnectionAsync(token);
        ServiceRequest request = await IpcMessageCodec.ReadRequestAsync(server, token);
        await IpcMessageCodec.WriteServerFrameAsync(server, response(request), token);
    }

    private static async Task RunRawServerAsync(
        string pipeName,
        Func<ServiceRequest, byte[]> response,
        CancellationToken token)
    {
        await using NamedPipeServerStream server = CreateServer(pipeName);
        await server.WaitForConnectionAsync(token);
        ServiceRequest request = await IpcMessageCodec.ReadRequestAsync(server, token);
        await server.WriteAsync(response(request), token);
        await server.FlushAsync(token);
    }

    private static async Task RunSubscriptionServerAsync(
        string pipeName,
        ServiceStatusSnapshot initial,
        IReadOnlyList<ServiceStatusSnapshot> events,
        CancellationToken token)
    {
        await using NamedPipeServerStream server = CreateServer(pipeName);
        await server.WaitForConnectionAsync(token);
        SubscribeStatusRequest request = Assert.IsType<SubscribeStatusRequest>(
            await IpcMessageCodec.ReadRequestAsync(server, token));
        await IpcMessageCodec.WriteServerFrameAsync(
            server,
            new ServiceResponse(2, request.RequestId, true, null, initial, null),
            token);
        foreach (ServiceStatusSnapshot status in events)
        {
            await IpcMessageCodec.WriteServerFrameAsync(
                server,
                new ServiceStatusEvent(2, status),
                token);
        }
    }

    private static NamedPipeServerStream CreateServer(string pipeName) => new(
        pipeName,
        PipeDirection.InOut,
        1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous);

    private static string NewPipeName() => $"OpenBlade.Tray.Tests.{Guid.NewGuid():N}";

    private static byte[] Frame(byte[] payload)
    {
        byte[] frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, sizeof(int));
        return frame;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        for (int index = 0; index < 10_000 && !predicate(); index++)
        {
            await Task.Yield();
        }

        Assert.True(predicate(), "The asynchronous condition was not reached.");
    }

    private static ServiceStatusSnapshot CreateStatus(long revision)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        return new ServiceStatusSnapshot(
            revision,
            false,
            true,
            ["RazerAppEngine"],
            "Blocked",
            DeviceCapabilities.Unavailable(),
            null,
            profiles,
            new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(FanMode.Automatic, SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
                profiles.BatteryPolicy),
            new TelemetrySnapshot(DateTimeOffset.UnixEpoch, PowerSource.PluggedIn, null, null, null, null),
            null,
            null,
            null,
            false);
    }

    private sealed class ScriptedStream(byte[] response) : Stream
    {
        private readonly MemoryStream input = new(response, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            input.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            input.ReadAsync(buffer, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class BlockingHandshakeStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly object sync = new();
        private readonly List<FakeTimer> timers = [];
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public List<TimeSpan> Delays { get; } = [];
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(callback, state, now + dueTime, period);
            lock (sync)
            {
                timers.Add(timer);
                Delays.Add(dueTime);
            }
            return timer;
        }
        public void Advance(TimeSpan amount)
        {
            FakeTimer[] due;
            lock (sync)
            {
                now += amount;
                due = timers.Where(timer => timer.IsDue(now)).ToArray();
            }
            foreach (FakeTimer timer in due) timer.Fire(now);
        }
        private sealed class FakeTimer(TimerCallback callback, object? state, DateTimeOffset due, TimeSpan period) : ITimer
        {
            private int disposed;
            private DateTimeOffset next = due;
            public bool IsDue(DateTimeOffset current) => Volatile.Read(ref disposed) == 0 && current >= next;
            public void Fire(DateTimeOffset current)
            {
                if (Volatile.Read(ref disposed) != 0) return;
                callback(state);
                if (period == Timeout.InfiniteTimeSpan) Dispose();
                else next = current + period;
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Interlocked.Exchange(ref disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
