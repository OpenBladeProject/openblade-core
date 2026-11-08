using System.Text.Json;
using OpenBlade.Core.Native;
using OpenBlade.Testing;

namespace OpenBlade.Protocol.Tests;

public sealed class NativeActivationTests
{
    public static IEnumerable<object[]> ResponseCases() =>
        ParityFixture.Rows("razer-report.json", "razer-report", "responses");

    [Theory]
    [MemberData(nameof(ResponseCases))]
    public void ProductionResponsePreservesIndependentBaselineIncludingMessages(string id)
    {
        JsonElement row = ParityFixture.Row("razer-report.json", "razer-report", "responses", id);
        JsonElement request = row.GetProperty("request");
        byte transaction = request.GetProperty("transactionId").GetByte();
        byte commandClass = request.GetProperty("commandClass").GetByte();
        byte commandId = request.GetProperty("commandId").GetByte();
        byte[] payload = ParityFixture.Hex(request.GetProperty("payloadHex"));
        byte[] response = ParityFixture.Hex(row.GetProperty("responseHex"));
        int? expected = row.GetProperty("expectedPayloadLength").ValueKind == JsonValueKind.Null
            ? null : row.GetProperty("expectedPayloadLength").GetInt32();
        var baseline = ManagedReportBaseline.Create(transaction, commandClass, commandId, payload);
        var actual = RazerReport.Create(transaction, commandClass, commandId, payload);
        byte[]? expectedOutput = null;
        byte[]? actualOutput = null;
        Exception? expectedError = Record.Exception(() => expectedOutput = baseline.ValidateResponse(response, expected).ToArray());
        Exception? actualError = Record.Exception(() => actualOutput = actual.ValidateResponse(response, expected).ToArray());
        Assert.Equal(expectedOutput, actualOutput);
        Assert.Equal(expectedError?.GetType(), actualError?.GetType());
        Assert.Equal(expectedError?.Message, actualError?.Message);
        if (expectedError is RazerProtocolException protocol)
        {
            Assert.Equal(protocol.ValidationFailure, Assert.IsType<RazerProtocolException>(actualError).ValidationFailure);
        }
    }

    [Fact]
    public void ProductionEncoderPreservesAllPayloadLengthsAndReusedExactDestination()
    {
        var random = new Random(701);
        for (int length = 0; length <= 80; length++)
        {
            byte[] payload = new byte[length];
            random.NextBytes(payload);
            byte[] destination = Enumerable.Repeat((byte)0xA5, 92).ToArray();
            RazerReport.Encode(0xFF, 0x07, 0x8F, payload, destination.AsSpan(1, 90));
            Assert.Equal(ManagedReportBaseline.Create(0xFF, 0x07, 0x8F, payload).Encode(), destination.AsSpan(1, 90).ToArray());
            Assert.Equal(0xA5, destination[0]);
            Assert.Equal(0xA5, destination[91]);
        }
    }

    [Theory]
    [InlineData(89)]
    [InlineData(91)]
    public void HostLengthAndPayloadValidationPreserveExceptionOrderWithoutNativeCall(int length)
    {
        byte[] output = Enumerable.Repeat((byte)0xA5, length).ToArray();
        Exception baseline = Assert.Throws<ArgumentOutOfRangeException>(() => ManagedReportBaseline.Encode(1, 2, 3, new byte[81], output));
        Exception actual = Assert.Throws<ArgumentOutOfRangeException>(() => RazerReport.Encode(1, 2, 3, new byte[81], output));
        Assert.Equal(baseline.Message, actual.Message);
        baseline = Assert.Throws<ArgumentException>(() => ManagedReportBaseline.Encode(1, 2, 3, [], output));
        actual = Assert.Throws<ArgumentException>(() => RazerReport.Encode(1, 2, 3, [], output));
        Assert.Equal(baseline.Message, actual.Message);
        Assert.All(output, value => Assert.Equal(0xA5, value));
    }

    [Theory]
    [InlineData(NativeBoundaryErrorKind.InternalFailure)]
    [InlineData(NativeBoundaryErrorKind.InvalidNativeResult)]
    [InlineData(NativeBoundaryErrorKind.Disposed)]
    public void BoundaryFailureNeverBecomesAResponseOrProtocolRejection(NativeBoundaryErrorKind kind)
    {
        var failure = new NativeCodecResult(null, new(kind, 0, 0, "sensitive ignored native text"));
        NativeComponentException error = Assert.Throws<NativeComponentException>(() => RazerReport.InterpretResponse(failure, []));
        Assert.DoesNotContain("sensitive", error.Message);
    }
}
