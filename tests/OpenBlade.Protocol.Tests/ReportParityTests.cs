using System.Text.Json;
using OpenBlade.Protocol;
using OpenBlade.Testing;

namespace OpenBlade.Protocol.Tests;

public sealed class ReportParityTests
{
    public static IEnumerable<object[]> EncodingCases() =>
        ParityFixture.Rows("razer-report.json", "razer-report", "encoding");

    public static IEnumerable<object[]> ResponseCases() =>
        ParityFixture.Rows("razer-report.json", "razer-report", "responses");

    [Theory]
    [MemberData(nameof(EncodingCases))]
    public void EncodingMatchesSharedContract(string id, string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement row = document.RootElement;
        JsonElement request = row.GetProperty("request");
        byte transaction = request.GetProperty("transactionId").GetByte();
        byte commandClass = request.GetProperty("commandClass").GetByte();
        byte commandId = request.GetProperty("commandId").GetByte();
        byte[] payload = Convert.FromHexString(request.GetProperty("payloadHex").GetString()!);
        byte[] destination = Enumerable.Repeat((byte)0xA5,
            row.GetProperty("destinationLength").GetInt32()).ToArray();
        string? expectedError = row.GetProperty("expectedError").GetString();

        if (expectedError is not null)
        {
            Action encode = () => RazerReport.Encode(transaction, commandClass, commandId, payload, destination);
            if (expectedError == "payload-too-large")
            {
                Assert.Throws<ArgumentOutOfRangeException>(encode);
                Assert.Throws<ArgumentOutOfRangeException>(() =>
                    RazerReport.Create(transaction, commandClass, commandId, payload));
            }
            else
            {
                Assert.Equal("destination-length", expectedError);
                Assert.Throws<ArgumentException>(encode);
                RazerReport report = RazerReport.Create(transaction, commandClass, commandId, payload);
                Assert.Throws<ArgumentException>(() => report.Encode(destination));
            }
            Assert.True(destination.All(value => value == 0xA5), $"{id}: rejected encoding changed output");
            return;
        }

        string expected = row.GetProperty("expectedReportHex").GetString()!;
        RazerReport.Encode(transaction, commandClass, commandId, payload, destination);
        Assert.True(expected == Convert.ToHexString(destination), $"{id}: static encoding: {Convert.ToHexString(destination)}");
        RazerReport instance = RazerReport.Create(transaction, commandClass, commandId, payload);
        Assert.True(expected == Convert.ToHexString(instance.Encode()), $"{id}: allocating encoding differs");
        Array.Fill(destination, (byte)0xA5);
        instance.Encode(destination);
        Assert.True(expected == Convert.ToHexString(destination), $"{id}: instance span encoding differs");
    }

    [Theory]
    [MemberData(nameof(ResponseCases))]
    public void ResponseValidationMatchesSharedContract(string id, string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement row = document.RootElement;
        JsonElement request = row.GetProperty("request");
        RazerReport report = RazerReport.Create(
            request.GetProperty("transactionId").GetByte(),
            request.GetProperty("commandClass").GetByte(),
            request.GetProperty("commandId").GetByte(),
            Convert.FromHexString(request.GetProperty("payloadHex").GetString()!));
        byte[] response = Convert.FromHexString(row.GetProperty("responseHex").GetString()!);
        int? expectedLength = row.GetProperty("expectedPayloadLength").ValueKind == JsonValueKind.Null
            ? null : row.GetProperty("expectedPayloadLength").GetInt32();
        JsonElement failure = row.GetProperty("expectedFailure");
        if (failure.ValueKind != JsonValueKind.Null)
        {
            RazerProtocolException error = Assert.Throws<RazerProtocolException>(() =>
                report.ValidateResponse(response, expectedLength));
            Assert.True(failure.GetProperty("name").GetString() == error.ValidationFailure.ToString(),
                $"{id}: expected {failure}, got {error.ValidationFailure}");
            Assert.Equal(failure.GetProperty("value").GetInt32(), (int)error.ValidationFailure);
            return;
        }

        ReadOnlyMemory<byte> payload = report.ValidateResponse(response, expectedLength);
        Assert.True(row.GetProperty("expectedPayloadHex").GetString() == Convert.ToHexString(payload.Span),
            $"{id}: returned payload: {Convert.ToHexString(payload.Span)}");
        byte[] saved = payload.ToArray();
        Array.Fill(response, (byte)0xA5);
        Assert.Equal(saved, payload.ToArray());
    }
}
