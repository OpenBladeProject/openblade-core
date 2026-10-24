using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using OpenBlade.Contracts;
using OpenBlade.Session;
using OpenBlade.SessionAgent;

namespace OpenBlade.SessionAgent.Tests;

public sealed class WindowsBatteryReportIpcTests
{
    [Fact]
    public async Task Codec_RoundTripsQueryAndTypedReport()
    {
        Guid requestId = Guid.NewGuid();
        var request = new SessionBatteryRequest(SessionBatteryIpc.ProtocolVersion, requestId);
        var response = new SessionBatteryResponse(
            SessionBatteryIpc.ProtocolVersion,
            requestId,
            SessionBatteryOperationStatus.Succeeded,
            null,
            Report());
        using var requestStream = new MemoryStream();
        using var responseStream = new MemoryStream();

        await SessionBatteryIpcCodec.WriteRequestAsync(requestStream, request);
        await SessionBatteryIpcCodec.WriteResponseAsync(responseStream, response);
        requestStream.Position = 0;
        responseStream.Position = 0;

        Assert.Equal(request, await SessionBatteryIpcCodec.ReadRequestAsync(requestStream));
        SessionBatteryResponse decoded = await SessionBatteryIpcCodec.ReadResponseAsync(
            responseStream);
        Assert.Equal(requestId, decoded.RequestId);
        Assert.Equal(SessionBatteryOperationStatus.Succeeded, decoded.Status);
        Assert.NotNull(decoded.Report);
        Assert.Equal(WindowsBatteryReportSource.WindowsPowerCfg, decoded.Report.Source);
        Assert.Single(decoded.Report.Intervals);
    }

    [Fact]
    public async Task Codec_RejectsUnknownRequestMembersAndEmptyCorrelationId()
    {
        string json = $$"""
            {"version":{{SessionBatteryIpc.ProtocolVersion}},"requestId":"{{Guid.NewGuid()}}","outputPath":"C:\\report.xml"}
            """;
        using MemoryStream malformed = Frame(json);
        using var emptyId = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(
            () => SessionBatteryIpcCodec.ReadRequestAsync(malformed));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SessionBatteryIpcCodec.WriteRequestAsync(
                emptyId,
                new SessionBatteryRequest(SessionBatteryIpc.ProtocolVersion, Guid.Empty)));

        string duplicateJson = $$"""
            {"version":{{SessionBatteryIpc.ProtocolVersion}},"requestId":"{{Guid.NewGuid()}}","REQUESTID":"{{Guid.NewGuid()}}"}
            """;
        using MemoryStream duplicate = Frame(duplicateJson);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => SessionBatteryIpcCodec.ReadRequestAsync(duplicate));
    }

    [Fact]
    public async Task Codec_RejectsOversizedFrameBeforeAllocation()
    {
        byte[] prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(
            prefix,
            SessionBatteryIpc.MaximumMessageBytes + 1);
        using var stream = new MemoryStream(prefix);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => SessionBatteryIpcCodec.ReadRequestAsync(stream));
    }

    [Fact]
    public async Task Codec_RejectsFailureWithReportAndInvalidReportValues()
    {
        var failed = new SessionBatteryResponse(
            SessionBatteryIpc.ProtocolVersion,
            Guid.NewGuid(),
            SessionBatteryOperationStatus.Failed,
            "failed",
            Report());
        WindowsBatteryReport valid = Report();
        var invalidStatus = new SessionBatteryResponse(
            SessionBatteryIpc.ProtocolVersion,
            Guid.NewGuid(),
            SessionBatteryOperationStatus.Succeeded,
            null,
            new WindowsBatteryReport(
                valid.Source,
                valid.GeneratedAtUtc,
                valid.WindowStartUtc,
                valid.WindowEndUtc,
                new WindowsBatteryStatus(100, 90, double.NaN, 1),
                valid.Intervals));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SessionBatteryIpcCodec.WriteResponseAsync(new MemoryStream(), failed));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            SessionBatteryIpcCodec.WriteResponseAsync(new MemoryStream(), invalidStatus));
    }

    [Fact]
    public void BatteryEndpoint_ReusesSessionIdentityAndCurrentUserOnlySecurity()
    {
        var firstSid = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        var secondSid = new SecurityIdentifier("S-1-5-21-1-2-3-1002");

        SessionAgentPipeEndpoint first = SessionBatteryPipeEndpoint.Create(firstSid, 3);
        SessionAgentPipeEndpoint same = SessionBatteryPipeEndpoint.Create(firstSid, 3);
        SessionAgentPipeEndpoint otherSession = SessionBatteryPipeEndpoint.Create(firstSid, 4);
        SessionAgentPipeEndpoint otherUser = SessionBatteryPipeEndpoint.Create(secondSid, 3);

        Assert.Equal(first, same);
        Assert.EndsWith(SessionBatteryIpc.PipeNameSuffix, first.PipeName, StringComparison.Ordinal);
        Assert.DoesNotContain(firstSid.Value, first.PipeName, StringComparison.Ordinal);
        Assert.NotEqual(first, otherSession);
        Assert.NotEqual(first, otherUser);

        PipeSecurity security = SessionAgentPipeSecurity.Create(firstSid);
        AuthorizationRuleCollection rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: false,
            typeof(SecurityIdentifier));
        PipeAccessRule rule = Assert.Single(rules.Cast<PipeAccessRule>());
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(firstSid, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(PipeAccessRights.FullControl, rule.PipeAccessRights);
    }

    [Fact]
    public async Task Dispatcher_PreservesCorrelationAndMapsProviderOutcomes()
    {
        Guid successId = Guid.NewGuid();
        var success = new SessionBatteryRequestDispatcher(_ => Task.FromResult<WindowsBatteryReport?>(Report()));
        var unavailable = new SessionBatteryRequestDispatcher(_ => Task.FromResult<WindowsBatteryReport?>(null));
        var failures = new List<DiagnosticFailure>();
        var failed = new SessionBatteryRequestDispatcher(
            _ => throw new IOException(
                @"provider failed at C:\Users\Alice\battery.html"),
            failures.Add);

        SessionBatteryResponse successResponse = await success.DispatchAsync(
            new SessionBatteryRequest(SessionBatteryIpc.ProtocolVersion, successId),
            default);
        SessionBatteryResponse unavailableResponse = await unavailable.DispatchAsync(
            new SessionBatteryRequest(SessionBatteryIpc.ProtocolVersion, Guid.NewGuid()),
            default);
        SessionBatteryResponse failedResponse = await failed.DispatchAsync(
            new SessionBatteryRequest(SessionBatteryIpc.ProtocolVersion, Guid.NewGuid()),
            default);

        Assert.Equal(successId, successResponse.RequestId);
        Assert.Equal(SessionBatteryOperationStatus.Succeeded, successResponse.Status);
        Assert.NotNull(successResponse.Report);
        Assert.Equal(SessionBatteryOperationStatus.Unavailable, unavailableResponse.Status);
        Assert.Null(unavailableResponse.Report);
        Assert.Equal(SessionBatteryOperationStatus.Failed, failedResponse.Status);
        Assert.DoesNotContain("Alice", failedResponse.Error, StringComparison.Ordinal);
        DiagnosticFailure failure = Assert.Single(failures);
        Assert.True(failure.IsValid());
        Assert.Equal("session-agent", failure.Component);
        Assert.Equal("windows-battery-report", failure.Operation);
        Assert.Equal("dispatcher", failure.Stage);
        Assert.Equal("exception", failure.Reason);
        Assert.Equal(nameof(IOException), failure.ExceptionType);
        Assert.Contains("provider failed", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Alice", failure.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PipeServer_UsesStrictCodecAndReturnsCorrelatedResponse()
    {
        SecurityIdentifier sid = WindowsIdentity.GetCurrent().User!;
        var endpoint = new SessionAgentPipeEndpoint(
            $"OpenBlade.Session.Battery.Tests.{Guid.NewGuid():N}");
        Assert.True(SessionAgentPipeOwner.TryAcquire(
            endpoint,
            sid,
            out SessionAgentPipeOwner? owner));
        await using var owned = owner!;
        var dispatcher = new SessionBatteryRequestDispatcher(
            _ => Task.FromResult<WindowsBatteryReport?>(Report()));
        await using var server = new SessionBatteryPipeServer(owned, dispatcher);
        var request = new SessionBatteryRequest(
            SessionBatteryIpc.ProtocolVersion,
            Guid.NewGuid());
        using var requestBytes = new MemoryStream();
        await SessionBatteryIpcCodec.WriteRequestAsync(requestBytes, request);
        await using var connection = new DuplexStream(requestBytes.ToArray());

        await server.ServeConnectionAsync(connection, default);
        connection.RewindOutput();
        SessionBatteryResponse response = await SessionBatteryIpcCodec.ReadResponseAsync(connection);

        Assert.Equal(request.RequestId, response.RequestId);
        Assert.Equal(SessionBatteryOperationStatus.Succeeded, response.Status);
    }

    private static WindowsBatteryReport Report()
    {
        DateTimeOffset generated = Utc(2026, 1, 2, 12);
        DateTimeOffset start = generated - TimeSpan.FromHours(24);
        return new WindowsBatteryReport(
            WindowsBatteryReportSource.WindowsPowerCfg,
            generated,
            start,
            generated,
            new WindowsBatteryStatus(60000, 50000, 83.333, 120),
            [
                new WindowsBatteryReportInterval(
                    generated - TimeSpan.FromHours(1),
                    generated,
                    WindowsBatteryPowerSource.Battery,
                    WindowsBatteryActivity.Active,
                    40000,
                    50000,
                    5000,
                    80),
            ]);
    }

    private static MemoryStream Frame(string json)
    {
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(sizeof(int)));
        return new MemoryStream(frame);
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);

    private sealed class DuplexStream(byte[] input) : Stream
    {
        private readonly MemoryStream reader = new(input, writable: false);
        private readonly MemoryStream writer = new();
        private bool readingOutput;

        public void RewindOutput()
        {
            reader.Dispose();
            writer.Position = 0;
            readingOutput = true;
        }

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
            readingOutput ? writer.Read(buffer, offset, count) : reader.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            readingOutput
                ? writer.ReadAsync(buffer, cancellationToken)
                : reader.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) =>
            writer.Write(buffer, offset, count);
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            writer.WriteAsync(buffer, cancellationToken);
        public override void Flush() => writer.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            writer.FlushAsync(cancellationToken);
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
}
