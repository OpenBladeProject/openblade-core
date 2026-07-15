using OpenBlade.Contracts;
using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class SetupServiceHealthProbeTests
{
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

    private static ServiceStatusSnapshot CreateStatus()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        return new ServiceStatusSnapshot(
            1,
            true,
            false,
            [],
            "Ready",
            DeviceCapabilities.SafeTargetDefaults(),
            null,
            profiles,
            new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(profiles.PluggedIn.Performance, SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(profiles.PluggedIn.Fans, SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile),
                profiles.BatteryPolicy),
            new TelemetrySnapshot(DateTimeOffset.UnixEpoch, PowerSource.PluggedIn, 80, null, null, null),
            null,
            null,
            null,
            false);
    }

    private sealed class TestDuplexStream(Stream response) : Stream
    {
        private readonly MemoryStream written = new();

        public long BytesWritten => written.Length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => written.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => written.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => response.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            response.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => written.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            written.WriteAsync(buffer, cancellationToken);
    }
}
