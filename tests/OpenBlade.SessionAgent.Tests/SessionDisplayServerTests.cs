using OpenBlade.Contracts;
using OpenBlade.Display;
using OpenBlade.Session;
using OpenBlade.SessionAgent;
using System.Security.Principal;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SessionDisplayServerTests
{
    [Fact]
    public async Task Dispatcher_MapsEveryTypedRequestAndPreservesCorrelationId()
    {
        var applier = new RecordingApplier();
        var dispatcher = new SessionDisplayRequestDispatcher(applier);
        SessionDisplayRequest[] requests =
        [
            new QueryDisplayRequest(SessionDisplayIpc.ProtocolVersion, Guid.NewGuid()),
            new SetRefreshRateRequest(
                SessionDisplayIpc.ProtocolVersion,
                Guid.NewGuid(),
                PowerSource.PluggedIn,
                "fixed-240"),
            new SetHdrRequest(
                SessionDisplayIpc.ProtocolVersion,
                Guid.NewGuid(),
                PowerSource.PluggedIn,
                true),
            new SetColorProfileRequest(
                SessionDisplayIpc.ProtocolVersion,
                Guid.NewGuid(),
                PowerSource.PluggedIn,
                "native.icm"),
            new ApplyDisplayProfileRequest(
                SessionDisplayIpc.ProtocolVersion,
                Guid.NewGuid(),
                PowerSource.PluggedIn,
                new DisplayProfileSettings("fixed-60", false, "native.icm")),
        ];

        foreach (SessionDisplayRequest request in requests)
        {
            SessionDisplayResponse response = await dispatcher.DispatchAsync(
                request,
                CancellationToken.None);
            Assert.Equal(request.RequestId, response.RequestId);
            Assert.Equal(SessionDisplayOperationStatus.Succeeded, response.Status);
        }

        Assert.Equal(
            ["query", "refresh:PluggedIn:fixed-240", "hdr:PluggedIn:True",
             "color:PluggedIn:native.icm", "profile:PluggedIn:fixed-60"],
            applier.Calls);
    }

    [Fact]
    public async Task ServeConnection_UsesStrictCodecAndWritesTypedResponse()
    {
        var applier = new RecordingApplier();
        var dispatcher = new SessionDisplayRequestDispatcher(applier);
        await using var owner = AcquireOwner();
        await using var server = new SessionDisplayPipeServer(owner, dispatcher);
        var request = new QueryDisplayRequest(
            SessionDisplayIpc.ProtocolVersion,
            Guid.NewGuid());
        await using var requestBytes = new MemoryStream();
        await SessionDisplayIpcCodec.WriteRequestAsync(requestBytes, request);
        await using var connection = new TestDuplexStream(requestBytes.ToArray());

        await server.ServeConnectionAsync(connection, CancellationToken.None);
        connection.RewindOutput();
        SessionDisplayResponse response = await SessionDisplayIpcCodec.ReadResponseAsync(connection);

        Assert.Equal(request.RequestId, response.RequestId);
        Assert.Equal(SessionDisplayOperationStatus.Succeeded, response.Status);
        Assert.Equal("fixed-60", response.State.CurrentRefreshKey);
    }

    [Fact]
    public async Task ServeConnection_TruncatedFrameClosesOnlyThatConnection()
    {
        var dispatcher = new SessionDisplayRequestDispatcher(new RecordingApplier());
        await using var owner = AcquireOwner();
        await using var server = new SessionDisplayPipeServer(owner, dispatcher);
        await using var connection = new TestDuplexStream([1, 0]);

        await server.ServeConnectionAsync(connection, CancellationToken.None);
    }

    [Fact]
    public async Task ServeConnection_CancelsIncompleteFrameReadAtTwoSeconds()
    {
        var time = new ManualTimeProvider();
        var dispatcher = new SessionDisplayRequestDispatcher(new RecordingApplier());
        await using var owner = AcquireOwner();
        await using var server = new SessionDisplayPipeServer(owner, dispatcher, time);
        await using var connection = new BlockingReadStream();

        Task serving = server.ServeConnectionAsync(connection, CancellationToken.None);
        await connection.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromSeconds(2));

        await serving.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(connection.ReadCancellationObserved);
    }

    [Fact]
    public async Task ServeConnection_ClientDisconnectDuringResponseClosesOnlyThatConnection()
    {
        var dispatcher = new SessionDisplayRequestDispatcher(new RecordingApplier());
        await using var owner = AcquireOwner();
        await using var server = new SessionDisplayPipeServer(owner, dispatcher);
        var request = new QueryDisplayRequest(
            SessionDisplayIpc.ProtocolVersion,
            Guid.NewGuid());
        await using var requestBytes = new MemoryStream();
        await SessionDisplayIpcCodec.WriteRequestAsync(requestBytes, request);
        await using var connection = new DisconnectOnWriteStream(requestBytes.ToArray());

        await server.ServeConnectionAsync(connection, CancellationToken.None);

        Assert.True(connection.WriteAttempted);
    }

    [Fact]
    public async Task ServeConnection_CancelsStalledDispatchWithoutStoppingServer()
    {
        var time = new ManualTimeProvider();
        var applier = new BlockingQueryApplier();
        var dispatcher = new SessionDisplayRequestDispatcher(applier);
        await using var owner = AcquireOwner();
        await using var server = new SessionDisplayPipeServer(owner, dispatcher, time);
        var request = new QueryDisplayRequest(
            SessionDisplayIpc.ProtocolVersion,
            Guid.NewGuid());
        await using var requestBytes = new MemoryStream();
        await SessionDisplayIpcCodec.WriteRequestAsync(requestBytes, request);
        await using var connection = new TestDuplexStream(requestBytes.ToArray());

        Task serving = server.ServeConnectionAsync(connection, CancellationToken.None);
        await applier.QueryStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        time.Advance(TimeSpan.FromSeconds(5));

        await serving.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(applier.CancellationObserved);
    }

    private static SessionAgentPipeOwner AcquireOwner()
    {
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User!;
        var endpoint = new SessionAgentPipeEndpoint(
            $"{SessionDisplayIpc.PipeNamePrefix}.Tests.{Guid.NewGuid():N}");
        Assert.True(SessionAgentPipeOwner.TryAcquire(endpoint, sid, out SessionAgentPipeOwner? owner));
        return owner!;
    }

    private sealed class RecordingApplier : IDisplayProfileApplier
    {
        public List<string> Calls { get; } = [];

        public Task<SessionDisplayExecutionResult> QueryAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("query");
            return Result();
        }

        public Task<SessionDisplayExecutionResult> SetRefreshRateAsync(
            PowerSource expectedPowerSource,
            string refreshRateKey,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"refresh:{expectedPowerSource}:{refreshRateKey}");
            return Result();
        }

        public Task<SessionDisplayExecutionResult> SetHdrAsync(
            PowerSource expectedPowerSource,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"hdr:{expectedPowerSource}:{enabled}");
            return Result();
        }

        public Task<SessionDisplayExecutionResult> SetColorProfileAsync(
            PowerSource expectedPowerSource,
            string colorProfileKey,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"color:{expectedPowerSource}:{colorProfileKey}");
            return Result();
        }

        public Task<SessionDisplayExecutionResult> ApplyProfileAsync(
            PowerSource expectedPowerSource,
            DisplayProfileSettings settings,
            CancellationToken cancellationToken = default)
        {
            Calls.Add($"profile:{expectedPowerSource}:{settings.RefreshRateKey}");
            return Result();
        }

        public Task<SessionDisplayExecutionResult> ApplyProfileForTransitionAsync(
            PowerSource expectedPowerSource,
            long expectedGeneration,
            DisplayProfileSettings settings,
            CancellationToken cancellationToken) => Result();

        private static Task<SessionDisplayExecutionResult> Result() => Task.FromResult(
            new SessionDisplayExecutionResult(
                SessionDisplayOperationStatus.Succeeded,
                null,
                new DisplaySettingsSnapshot(
                    true,
                    "Internal display",
                    2560,
                    1600,
                    "fixed-60",
                    [new DisplayRefreshOption("fixed-60", "60 Hz", 60, 60, 60, false)],
                    true,
                    false,
                    true,
                    "native.icm",
                    [new DisplayColorProfile("native.icm", "Native", true)],
                    null)));
    }

    private sealed class BlockingQueryApplier : IDisplayProfileApplier
    {
        public TaskCompletionSource QueryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }

        public async Task<SessionDisplayExecutionResult> QueryAsync(
            CancellationToken cancellationToken = default)
        {
            QueryStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("The blocking query unexpectedly completed.");
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }

        public Task<SessionDisplayExecutionResult> SetRefreshRateAsync(
            PowerSource expectedPowerSource,
            string refreshRateKey,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SessionDisplayExecutionResult> SetHdrAsync(
            PowerSource expectedPowerSource,
            bool enabled,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SessionDisplayExecutionResult> SetColorProfileAsync(
            PowerSource expectedPowerSource,
            string colorProfileKey,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SessionDisplayExecutionResult> ApplyProfileAsync(
            PowerSource expectedPowerSource,
            DisplayProfileSettings settings,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SessionDisplayExecutionResult> ApplyProfileForTransitionAsync(
            PowerSource expectedPowerSource,
            long expectedGeneration,
            DisplayProfileSettings settings,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestDuplexStream(byte[] input) : Stream
    {
        private readonly MemoryStream reader = new(input, writable: false);
        private readonly MemoryStream writer = new();
        private bool readOutput;

        public void RewindOutput()
        {
            writer.Position = 0;
            readOutput = true;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) =>
            (readOutput ? writer : reader).Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            (readOutput ? writer : reader).ReadAsync(buffer, token);
        public override void Write(byte[] buffer, int offset, int count) => writer.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) =>
            writer.WriteAsync(buffer, token);
        public override void Flush() => writer.Flush();
        public override Task FlushAsync(CancellationToken token) => writer.FlushAsync(token);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                reader.Dispose();
                writer.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ReadCancellationObserved { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken token = default)
        {
            ReadStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 0;
            }
            catch (OperationCanceledException)
            {
                ReadCancellationObserved = true;
                throw;
            }
        }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DisconnectOnWriteStream(byte[] input) : Stream
    {
        private readonly MemoryStream reader = new(input, writable: false);

        public bool WriteAttempted { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) =>
            reader.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken token = default) => reader.ReadAsync(buffer, token);
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteAttempted = true;
            throw new IOException("The display client disconnected.");
        }
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken token = default)
        {
            WriteAttempted = true;
            return ValueTask.FromException(new IOException("The display client disconnected."));
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                reader.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> timers = [];
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => now;
        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, now + dueTime, period);
            timers.Add(timer);
            return timer;
        }
        public void Advance(TimeSpan amount)
        {
            now += amount;
            foreach (ManualTimer timer in timers.ToArray())
            {
                timer.FireIfDue(now);
            }
        }
        private sealed class ManualTimer(
            TimerCallback callback,
            object? state,
            DateTimeOffset due,
            TimeSpan period) : ITimer
        {
            private bool disposed;
            private DateTimeOffset next = due;
            public void FireIfDue(DateTimeOffset now)
            {
                if (disposed || now < next)
                {
                    return;
                }
                callback(state);
                if (period == Timeout.InfiniteTimeSpan)
                {
                    disposed = true;
                }
                else
                {
                    next = now + period;
                }
            }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
