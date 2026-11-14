using System.IO.Pipes;
using OpenBlade.Contracts;
using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class SetupServiceHealthProbeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifyAsync_RecordsActualPipeWrites(bool cancelAfterConnect)
    {
        string pipeName = $"OpenBlade-setup-test-{Guid.NewGuid():N}";
        await using var listener = new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var testDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(testDeadline.Token);
        Task accept = listener.WaitForConnectionAsync(testDeadline.Token);
        var request = new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid());
        var timing = new SetupServiceHealthTiming("test", TimeSpan.FromSeconds(10));
        var probe = new SetupServiceHealthProbe(async token =>
        {
            await client.ConnectAsync(token);
            if (cancelAfterConnect)
            {
                probeDeadline.Cancel();
            }
            return client;
        }, () => request);

        Task verification = probe.VerifyAsync(probeDeadline.Token, timing);
        await accept;
        if (cancelAfterConnect)
        {
            await Assert.ThrowsAsync<TaskCanceledException>(() => verification);
            AssertStage(timing.RequestWrite.Header, succeeded: false, canceledAtEntry: true, canceledAtExit: true);
            Assert.Null(timing.RequestWrite.Body.BeginQpc);
            Assert.Null(timing.ResponseRead.BeginQpc);
            var buffer = new byte[1];
            Assert.Equal(0, await listener.ReadAsync(buffer, testDeadline.Token));
        }
        else
        {
            Assert.Equal(request, await IpcMessageCodec.ReadRequestAsync(listener, testDeadline.Token));
            await IpcMessageCodec.WriteServerFrameAsync(
                listener,
                CreateStatusResponse(request),
                testDeadline.Token);
            await verification;
            AssertStage(timing.RequestWrite.Header, succeeded: true);
            AssertStage(timing.RequestWrite.Body, succeeded: true);
            AssertStage(timing.RequestWrite.Flush, succeeded: true);
            AssertStage(timing.ResponseRead, succeeded: true);
            Assert.True(timing.RequestWrite.Header.EndQpc <= timing.RequestWrite.Body.BeginQpc);
            Assert.True(timing.RequestWrite.Body.EndQpc <= timing.ResponseRead.BeginQpc);
        }

        AssertStage(timing.Connect, succeeded: true, canceledAtExit: cancelAfterConnect);
        Assert.True(timing.Connect.EndQpc <= timing.RequestWrite.Header.BeginQpc);
        Assert.Equal((uint)Environment.ProcessId, timing.ServerProcessId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerifyAsync_DistinguishesPendingBodyWriteFromPendingResponseRead(bool stallWrite)
    {
        using var deadline = new CancellationTokenSource();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var response = new MemoryStream();
        await using var connection = new TestDuplexStream(
            response, stallWrite ? sizeof(int) : null, pending);
        var timing = new SetupServiceHealthTiming("test", TimeSpan.FromSeconds(10));
        var probe = new SetupServiceHealthProbe(
            _ => ValueTask.FromResult<Stream>(connection),
            () => new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()));

        Task verification = probe.VerifyAsync(deadline.Token, timing);
        await pending.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(verification.IsCompleted);
        deadline.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verification);

        AssertStage(timing.RequestWrite.Header, succeeded: true);
        if (stallWrite)
        {
            AssertStage(timing.RequestWrite.Body, succeeded: false, canceledAtExit: true);
            Assert.Null(timing.ResponseRead.BeginQpc);
        }
        else
        {
            AssertStage(timing.RequestWrite.Body, succeeded: true);
            AssertStage(timing.ResponseRead, succeeded: false, canceledAtExit: true);
        }
    }

    [Fact]
    public async Task VerifyAsync_CompletesOnlyAfterValidStatusResponse()
    {
        ServiceStatusSnapshot expected = CreateStatus();
        await using MemoryStream response = new();
        var request = new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid());
        await IpcMessageCodec.WriteServerFrameAsync(
            response,
            new ServiceResponse(
                OpenBladeIpc.ProtocolVersion,
                request.RequestId,
                true,
                null,
                expected,
                null),
            CancellationToken.None);
        response.Position = 0;
        await using TestDuplexStream connection = new(response);
        SetupServiceHealthProbe probe = new(_ => ValueTask.FromResult<Stream>(connection), () => request);

        await probe.VerifyAsync(CancellationToken.None);

        Assert.True(connection.BytesWritten > sizeof(int));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetupHealthCheck_ReconnectsOnceAfterDroppedPipe(bool respondsOnRetry)
    {
        var request = new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid());
        await using var firstResponse = new MemoryStream();
        await using var retryResponse = new MemoryStream();
        if (respondsOnRetry)
        {
            await IpcMessageCodec.WriteServerFrameAsync(
                retryResponse,
                new ServiceResponse(OpenBladeIpc.ProtocolVersion, request.RequestId, true, null, CreateStatus(), null),
                CancellationToken.None);
            retryResponse.Position = 0;
        }

        int connections = 0;
        var probe = new SetupServiceHealthProbe(
            _ => ValueTask.FromResult<Stream>(new TestDuplexStream(
                ++connections == 1 ? firstResponse : retryResponse)),
            () => request);
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(healthProbe: probe);

        if (respondsOnRetry)
        {
            await platform.VerifyServiceHealthAsync(CancellationToken.None);
        }
        else
        {
            await Assert.ThrowsAsync<EndOfStreamException>(
                () => platform.VerifyServiceHealthAsync(CancellationToken.None));
        }

        Assert.Equal(2, connections);
    }

    [Fact]
    public async Task VerifyAsync_RejectsResponseForDifferentRequest()
    {
        await using MemoryStream response = new();
        var request = new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid());
        await IpcMessageCodec.WriteServerFrameAsync(
            response,
            new ServiceResponse(
                OpenBladeIpc.ProtocolVersion,
                Guid.NewGuid(),
                true,
                null,
                CreateStatus(),
                null),
            CancellationToken.None);
        response.Position = 0;
        await using TestDuplexStream connection = new(response);
        SetupServiceHealthProbe probe = new(_ => ValueTask.FromResult<Stream>(connection), () => request);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => probe.VerifyAsync(CancellationToken.None));

        Assert.Equal("Service health response ID does not match the request.", error.Message);
    }

    [Fact]
    public async Task SetupHealthCheck_RejectsIncompatibleResponseWithoutReconnect()
    {
        await using var response = new MemoryStream();
        ReadOnlySpan<byte> payload = """{"version":-1}"""u8;
        using (var writer = new BinaryWriter(response, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(payload.Length);
            writer.Write(payload);
        }
        response.Position = 0;

        int connections = 0;
        var probe = new SetupServiceHealthProbe(
            _ =>
            {
                connections++;
                return ValueTask.FromResult<Stream>(new TestDuplexStream(response));
            },
            () => new GetStatusRequest(OpenBladeIpc.ProtocolVersion, Guid.NewGuid()));
        WindowsSetupPlatform platform = WindowsSetupPlatformTestFactory.Create(healthProbe: probe);

        await Assert.ThrowsAsync<IpcProtocolVersionMismatchException>(
            () => platform.VerifyServiceHealthAsync(CancellationToken.None));
        Assert.Equal(1, connections);
    }

    private static void AssertStage(
        IpcStageTiming stage, bool succeeded, bool canceledAtEntry = false, bool canceledAtExit = false)
    {
        Assert.True(stage.BeginQpc <= stage.EndQpc);
        Assert.Equal(succeeded, stage.Succeeded);
        Assert.Equal(canceledAtEntry, stage.CanceledAtEntry);
        Assert.Equal(canceledAtExit, stage.CanceledAtExit);
    }

    internal static ServiceResponse CreateStatusResponse(GetStatusRequest request) =>
        new(OpenBladeIpc.ProtocolVersion, request.RequestId, true, null, CreateStatus(), null);

    private static ServiceStatusSnapshot CreateStatus()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        return new ServiceStatusSnapshot(
            1,
            true,
            false,
            [],
            null,
            [],
            "Ready",
            DeviceCapabilities.SafeTargetDefaults(),
            null,
            profiles,
            new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(profiles.PluggedIn.Performance, SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(profiles.PluggedIn.Fans, SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
                new DesiredBatteryState(profiles.BatteryPolicy, true, false)),
            PowerSource.PluggedIn,
            null,
            null,
            null,
            null,
            false,
            Firmware: null,
            ServiceVersion: "0.1.0-test");
    }

    internal sealed class TestDuplexStream(
        Stream response, int? stallAfterWrittenBytes = null, TaskCompletionSource? pending = null) : Stream
    {
        private readonly MemoryStream written = new();

        public long BytesWritten => written.Length;

        public Task<ServiceRequest> ReadWrittenRequestAsync(CancellationToken cancellationToken)
        {
            written.Position = 0;
            return IpcMessageCodec.ReadRequestAsync(written, cancellationToken);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => written.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => written.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => response.Read(buffer, offset, count);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (pending is not null && stallAfterWrittenBytes is null)
            {
                await StallAsync(cancellationToken);
            }
            return await response.ReadAsync(buffer, cancellationToken);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => written.Write(buffer, offset, count);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (written.Length == stallAfterWrittenBytes)
            {
                await StallAsync(cancellationToken);
            }
            await written.WriteAsync(buffer, cancellationToken);
        }

        private Task StallAsync(CancellationToken cancellationToken)
        {
            Task wait = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            pending!.SetResult();
            return wait;
        }
    }
}
