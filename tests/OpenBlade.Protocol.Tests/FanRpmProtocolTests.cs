using System.Globalization;
using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class FanRpmProtocolTests
{
    [Theory]
    [InlineData(1, 0x01)]
    [InlineData(2, 0x02)]
    public void CreateQuery_UsesExactReadOnlyCommand(
        int selectorValue,
        byte selectorByte)
    {
        var selector = (FanRpmSelector)selectorValue;
        byte[] encoded = FanRpmProtocol.CreateQuery(0x31, selector).Encode();

        Assert.Equal(0x31, encoded[1]);
        Assert.Equal(3, encoded[5]);
        Assert.Equal(0x0D, encoded[6]);
        Assert.Equal(0x88, encoded[7]);
        Assert.Equal([0x01, selectorByte, 0x00], encoded[8..11]);
    }

    [Theory]
    [InlineData(1, 0x20, 3200)]
    [InlineData(2, 0x1E, 3000)]
    [InlineData(2, 0x1F, 3100)]
    [InlineData(1, 0x00, 0)]
    [InlineData(2, 0xFF, 25500)]
    public void TryDecodeQuery_DecodesHundredsOfRpm(
        int selectorValue,
        byte encodedRpm,
        int expectedRpm)
    {
        var selector = (FanRpmSelector)selectorValue;
        Assert.True(FanRpmProtocol.TryDecodeQuery(
            [0x01, (byte)selector, encodedRpm],
            selector,
            out int rpm));
        Assert.Equal(expectedRpm, rpm);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x01, 0x01 })]
    [InlineData(new byte[] { 0x01, 0x01, 0x20, 0x00 })]
    [InlineData(new byte[] { 0x00, 0x01, 0x20 })]
    [InlineData(new byte[] { 0x01, 0x02, 0x20 })]
    public void TryDecodeQuery_RejectsMalformedOrMismatchedPayload(byte[] payload)
    {
        Assert.False(FanRpmProtocol.TryDecodeQuery(
            payload,
            FanRpmSelector.First,
            out _));
    }

    [Fact]
    public void UnknownSelector_IsRejected()
    {
        var unknown = (FanRpmSelector)0x03;

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FanRpmProtocol.CreateQuery(0x31, unknown));
        Assert.False(FanRpmProtocol.TryDecodeQuery(
            [0x01, 0x03, 0x20],
            unknown,
            out _));
    }

    [Fact]
    public void SanitizedFixture_ValidatesEveryCapturedEnvelopeAndDecodedRpm()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(FixturePath()));
        JsonElement root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("RZ09-0581", root.GetProperty("device").GetProperty("model").GetString());
        Assert.Equal("1532", root.GetProperty("device").GetProperty("vendorId").GetString());
        Assert.Equal("02E0", root.GetProperty("device").GetProperty("productId").GetString());
        Assert.Equal("3.01", root.GetProperty("device").GetProperty("biosVersion").GetString());
        Assert.True(root.GetProperty("readOnly").GetBoolean());
        Assert.True(root.GetProperty("productionWired").GetBoolean());

        JsonElement[] exchanges = root.GetProperty("exchanges").EnumerateArray().ToArray();
        Assert.Equal(4, exchanges.Length);
        foreach (JsonElement exchange in exchanges)
        {
            var selector = (FanRpmSelector)exchange.GetProperty("selector").GetByte();
            byte transactionId = byte.Parse(
                exchange.GetProperty("transactionIdHex").GetString()!,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture);
            byte[] requestEnvelope = Convert.FromHexString(
                exchange.GetProperty("requestEnvelopeHex").GetString()!);
            byte[] responseEnvelope = Convert.FromHexString(
                exchange.GetProperty("responseEnvelopeHex").GetString()!);

            RazerReport request = FanRpmProtocol.CreateQuery(transactionId, selector);
            Assert.Equal(RazerReport.ReportLength, requestEnvelope.Length);
            Assert.Equal(RazerReport.ReportLength, responseEnvelope.Length);
            Assert.Equal(requestEnvelope, request.Encode());
            Assert.Equal(0x02, responseEnvelope[0]);
            Assert.Equal(
                exchange.GetProperty("expectedStatusHex").GetString(),
                responseEnvelope[0].ToString("X2", CultureInfo.InvariantCulture));

            ReadOnlyMemory<byte> payload = request.ValidateResponse(responseEnvelope, 3);
            Assert.Equal(
                exchange.GetProperty("expectedResponsePayloadHex").GetString(),
                Convert.ToHexString(payload.Span));
            Assert.True(FanRpmProtocol.TryDecodeQuery(payload.Span, selector, out int rpm));
            Assert.Equal(exchange.GetProperty("expectedRpm").GetInt32(), rpm);
        }
    }

    private static string FixturePath() => RepositoryFile(
        "evidence", "legacy-captures",
        "decoded",
        "rz09-0581-pid-02e0-bios-3.01-fan-rpm-fixture.json");

    private static string RepositoryFile(params string[] relativeSegments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. relativeSegments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate repository file {Path.Combine(relativeSegments)}.");
    }
}
