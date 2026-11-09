using System.Text.Json;
using OpenBlade.Core.Native;
using OpenBlade.Testing;

namespace OpenBlade.Core.Native.Tests;

public sealed class NativeParityTests
{
    internal static string LibraryPath => Environment.GetEnvironmentVariable("OPENBLADE_NATIVE_LIBRARY")
        ?? throw new InvalidOperationException("OPENBLADE_NATIVE_LIBRARY must name the explicitly built native library; native tests never skip.");

    internal static NativeReportCodec Load()
    {
        NativeCodecLoadResult result = NativeReportCodec.Load(LibraryPath);
        Assert.True(result.IsSuccess, result.Error?.ToString());
        return result.Codec!;
    }

    public static IEnumerable<object[]> EncodingCases() =>
        ParityFixture.Rows("razer-report.json", "razer-report", "encoding");

    public static IEnumerable<object[]> ResponseCases() =>
        ParityFixture.Rows("razer-report.json", "razer-report", "responses");

    [Theory]
    [MemberData(nameof(EncodingCases))]
    public unsafe void RealNativeEncodingUsesCanonicalVectors(string id)
    {
        using NativeReportCodec codec = Load();
        JsonElement row = ParityFixture.Row("razer-report.json", "razer-report", "encoding", id);
        JsonElement request = row.GetProperty("request");
        NativeCodecResult result = codec.Encode(request.GetProperty("transactionId").GetByte(),
            request.GetProperty("commandClass").GetByte(), request.GetProperty("commandId").GetByte(),
            ParityFixture.Hex(request.GetProperty("payloadHex")));
        string? error = row.GetProperty("expectedError").GetString();
        NativeReportRequest record = NativeReportRequest.Create(request.GetProperty("transactionId").GetByte(),
            request.GetProperty("commandClass").GetByte(), request.GetProperty("commandId").GetByte());
        NativeErrorRecord rawError = NativeErrorRecord.Create();
        byte[] source = ParityFixture.Hex(request.GetProperty("payloadHex"));
        int capacity = row.GetProperty("destinationLength").GetInt32();
        byte[] destination = Enumerable.Repeat((byte)0xA5, capacity + 2).ToArray();
        uint written = 42;
        fixed (byte* input = source)
        fixed (byte* output = destination)
        {
            uint status = codec.RawEncode(NativeReportCodec.AbiVersion, &record, input, (uint)source.Length,
                output + 1, (uint)capacity, &written, &rawError);
            Assert.Equal(error == "payload-too-large" ? 2u : capacity < 90 ? 3u : 0u, status);
            Assert.Equal(status, rawError.Category);
            if (status == 0)
            {
                Assert.Equal(90u, written);
                Assert.Equal(result.Output, destination.AsSpan(1, 90).ToArray());
                Assert.All(destination.Skip(91), value => Assert.Equal(0xA5, value));
            }
            else
            {
                Assert.Equal(status == 3 ? 90u : 0u, written);
                Assert.All(destination, value => Assert.Equal(0xA5, value));
            }
            Assert.Equal(0xA5, destination[0]);
            Assert.Equal(0xA5, destination[^1]);
        }
        if (error == "payload-too-large")
        {
            Assert.False(result.IsSuccess);
            Assert.Null(result.Output);
            Assert.Equal(NativeBoundaryErrorKind.InvalidArgument, result.Error!.Kind);
            Assert.Equal(1u, result.Error.Detail);
            return;
        }
        Assert.True(result.IsSuccess, result.Error?.ToString());
        Assert.Equal(90, result.Output!.Length);
        if (error is null)
        {
            Assert.Equal(ParityFixture.Hex(row.GetProperty("expectedReportHex")), result.Output);
        }
        else
        {
            // These fixture rows specify the C# exact-span API. The allocating
            // wrapper always provides 90 bytes; ABI capacity cases are tested raw.
            Assert.Equal("destination-length", error);
        }
    }

    [Theory]
    [MemberData(nameof(ResponseCases))]
    public void RealNativeResponseUsesCanonicalResultsAndErrors(string id)
    {
        using NativeReportCodec codec = Load();
        JsonElement row = ParityFixture.Row("razer-report.json", "razer-report", "responses", id);
        JsonElement request = row.GetProperty("request");
        byte[] response = ParityFixture.Hex(row.GetProperty("responseHex"));
        int? expected = row.GetProperty("expectedPayloadLength").ValueKind == JsonValueKind.Null
            ? null : row.GetProperty("expectedPayloadLength").GetInt32();
        NativeCodecResult result = codec.ValidateResponse(request.GetProperty("transactionId").GetByte(),
            request.GetProperty("commandClass").GetByte(), request.GetProperty("commandId").GetByte(),
            ParityFixture.Hex(request.GetProperty("payloadHex")), response, expected);
        JsonElement failure = row.GetProperty("expectedFailure");
        if (failure.ValueKind != JsonValueKind.Null)
        {
            Assert.False(result.IsSuccess);
            Assert.Null(result.Output);
            Assert.Equal(NativeBoundaryErrorKind.ProtocolValidation, result.Error!.Kind);
            Assert.Equal(failure.GetProperty("value").GetUInt32(), result.Error.Detail);
            return;
        }
        Assert.True(result.IsSuccess, result.Error?.ToString());
        byte[] saved = ParityFixture.Hex(row.GetProperty("expectedPayloadHex"));
        Assert.Equal(saved, result.Output);
        Array.Fill(response, (byte)0xA5);
        Assert.Equal(saved, result.Output);
    }

    [Fact]
    public void IndependentConcurrentCallsReturnOwnedBytes()
    {
        using NativeReportCodec codec = Load();
        Parallel.For(0, 512, index =>
        {
            byte[] payload = [(byte)index, (byte)(index >> 8)];
            NativeCodecResult result = codec.Encode((byte)index, 3, 11, payload);
            Assert.True(result.IsSuccess, result.Error?.ToString());
            Assert.Equal((byte)index, result.Output![1]);
            Assert.Equal(payload, result.Output.AsSpan(8, 2).ToArray());
            payload[0] ^= 0xFF;
            Assert.Equal((byte)index, result.Output[8]);
        });
    }

    [Fact]
    public void DisposeRacingCallsKeepsNativeModuleAlive()
    {
        NativeReportCodec codec = Load();
        Parallel.Invoke(() => Parallel.For(0, 256, index =>
        {
            NativeCodecResult result = codec.Encode((byte)index, 3, 11, []);
            Assert.True(result.IsSuccess || result.Error!.Kind == NativeBoundaryErrorKind.Disposed);
        }), codec.Dispose);
        Assert.Equal(NativeBoundaryErrorKind.Disposed, codec.Encode(1, 2, 3, []).Error!.Kind);
        codec.Dispose();
    }

    [Fact]
    public void LoadingRequiresControlledExistingAbsolutePath()
    {
        Assert.False(NativeReportCodec.Load(Path.GetFileName(LibraryPath)).IsSuccess);
        Assert.False(NativeReportCodec.Load(Path.Combine(Path.GetDirectoryName(LibraryPath)!, "missing", Path.GetFileName(LibraryPath))).IsSuccess);
    }
}
