using System.Text.Json;
using OpenBlade.Core.Native;
using OpenBlade.Testing;

namespace OpenBlade.Protocol.Tests;

// Explicit developer-only PURE comparison. C# remains authoritative; no transport or device is used.
public sealed class NativeShadowComparisonTests
{
    public static IEnumerable<object[]> EncodingCases() =>
        ParityFixture.Rows("razer-report.json", "razer-report", "encoding");

    public static IEnumerable<object[]> ResponseCases() =>
        ParityFixture.Rows("razer-report.json", "razer-report", "responses");

    [Fact]
    public void ComparisonCoversAllCanonicalCases()
    {
        Assert.Equal(9, EncodingCases().Count());
        Assert.Equal(20, ResponseCases().Count());
    }

    [Theory]
    [MemberData(nameof(EncodingCases))]
    public void NativeEncodingMatchesCurrentCSharp(string id)
    {
        using NativeReportCodec codec = Load();
        JsonElement row = ParityFixture.Row("razer-report.json", "razer-report", "encoding", id);
        JsonElement request = row.GetProperty("request");
        byte transaction = request.GetProperty("transactionId").GetByte();
        byte commandClass = request.GetProperty("commandClass").GetByte();
        byte commandId = request.GetProperty("commandId").GetByte();
        byte[] payload = ParityFixture.Hex(request.GetProperty("payloadHex"));
        byte[] destination = Enumerable.Repeat((byte)0xA5, row.GetProperty("destinationLength").GetInt32()).ToArray();
        Exception? managedError = Record.Exception(() =>
            RazerReport.Encode(transaction, commandClass, commandId, payload, destination));
        NativeCodecResult native = codec.Encode(transaction, commandClass, commandId, payload);
        string? expectedError = row.GetProperty("expectedError").GetString();

        if (expectedError == "payload-too-large")
        {
            ArgumentOutOfRangeException error = Assert.IsType<ArgumentOutOfRangeException>(managedError);
            Assert.Equal("payload", error.ParamName);
            Assert.False(native.IsSuccess);
            Assert.Null(native.Output);
            Assert.Equal(NativeBoundaryErrorKind.InvalidArgument, native.Error!.Kind);
            Assert.Equal(1u, native.Error.Detail);
            Assert.Equal(0u, native.Error.RequiredLength);
            Assert.All(destination, value => Assert.Equal((byte)0xA5, value));
            return;
        }

        Assert.True(native.IsSuccess, native.Error?.ToString());
        Assert.Null(native.Error);
        if (expectedError is not null)
        {
            Assert.Equal("destination-length", expectedError);
            Assert.Contains(destination.Length, new[] { 89, 91 });
            ArgumentException error = Assert.IsType<ArgumentException>(managedError);
            Assert.Equal("destination", error.ParamName);
            Assert.All(destination, value => Assert.Equal((byte)0xA5, value));
            // Bounded compatibility projection: C# requires an exact 90-byte span,
            // whereas the native allocating wrapper always supplies capacity 90.
            // This does NOT assert that the raw native capacity API rejects 91.
            destination = new byte[RazerReport.ReportLength];
            RazerReport.Encode(transaction, commandClass, commandId, payload, destination);
        }
        else
        {
            Assert.Null(managedError);
            Assert.Equal(ParityFixture.Hex(row.GetProperty("expectedReportHex")), destination);
        }
        Assert.Equal(destination, native.Output);
        Assert.Equal(RazerReport.Create(transaction, commandClass, commandId, payload).Encode(), native.Output);
    }

    [Theory]
    [MemberData(nameof(ResponseCases))]
    public void NativeResponseMatchesCurrentCSharpTaxonomy(string id)
    {
        using NativeReportCodec codec = Load();
        JsonElement row = ParityFixture.Row("razer-report.json", "razer-report", "responses", id);
        JsonElement request = row.GetProperty("request");
        byte transaction = request.GetProperty("transactionId").GetByte();
        byte commandClass = request.GetProperty("commandClass").GetByte();
        byte commandId = request.GetProperty("commandId").GetByte();
        byte[] payload = ParityFixture.Hex(request.GetProperty("payloadHex"));
        byte[] response = ParityFixture.Hex(row.GetProperty("responseHex"));
        byte[] originalResponse = response.ToArray();
        int? expectedLength = row.GetProperty("expectedPayloadLength").ValueKind == JsonValueKind.Null
            ? null : row.GetProperty("expectedPayloadLength").GetInt32();
        RazerReport report = RazerReport.Create(transaction, commandClass, commandId, payload);
        byte[]? managedOutput = null;
        Exception? managedError = Record.Exception(() =>
            { managedOutput = report.ValidateResponse(response, expectedLength).ToArray(); });
        NativeCodecResult native = codec.ValidateResponse(transaction, commandClass, commandId, payload, response, expectedLength);
        Assert.Equal(originalResponse, response);
        JsonElement failure = row.GetProperty("expectedFailure");
        if (failure.ValueKind != JsonValueKind.Null)
        {
            RazerProtocolException error = Assert.IsType<RazerProtocolException>(managedError);
            Assert.Equal(failure.GetProperty("name").GetString(), error.ValidationFailure.ToString());
            Assert.Equal(failure.GetProperty("value").GetInt32(), (int)error.ValidationFailure);
            Assert.Null(managedOutput);
            Assert.False(native.IsSuccess);
            Assert.Null(native.Output);
            Assert.Equal(NativeBoundaryErrorKind.ProtocolValidation, native.Error!.Kind);
            Assert.Equal((uint)error.ValidationFailure, native.Error.Detail);
            Assert.Equal(0u, native.Error.RequiredLength);
            return;
        }

        Assert.Null(managedError);
        Assert.True(native.IsSuccess, native.Error?.ToString());
        Assert.Null(native.Error);
        Assert.Equal(ParityFixture.Hex(row.GetProperty("expectedPayloadHex")), managedOutput);
        Assert.Equal(managedOutput, native.Output);
        Array.Fill(response, (byte)0xA5);
        Assert.Equal(ParityFixture.Hex(row.GetProperty("expectedPayloadHex")), managedOutput);
        Assert.Equal(managedOutput, native.Output);
    }

    private static NativeReportCodec Load()
    {
        string path = Environment.GetEnvironmentVariable("OPENBLADE_NATIVE_LIBRARY")
            ?? throw new InvalidOperationException("Set OPENBLADE_NATIVE_LIBRARY to the explicitly supplied native artifact; shadow comparisons never skip.");
        NativeCodecLoadResult result = NativeReportCodec.Load(path);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        return result.Codec!;
    }
}
