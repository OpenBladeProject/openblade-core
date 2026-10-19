using System.Globalization;
using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class McuVersionProtocolTests
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(1);

    [Fact]
    public void CreateQuery_UsesCapturedReadOnlyExtendedFirmwareCommand()
    {
        byte[] encoded = McuVersionProtocol.CreateQuery(0x02).Encode();

        Assert.Equal(0x02, encoded[1]);
        Assert.Equal(4, encoded[5]);
        Assert.Equal(0x00, encoded[6]);
        Assert.Equal(0x87, encoded[7]);
        Assert.Equal([0x00, 0x00, 0x00, 0x00], encoded.AsSpan(8, 4).ToArray());
    }

    [Theory]
    [InlineData("01050000", "1.5.0.0")]
    [InlineData("01060000", "1.6.0.0")]
    [InlineData("FFFFFFFF", "255.255.255.255")]
    public void TryDecodeQuery_FormatsFourVersionComponents(
        string payloadHex,
        string expected)
    {
        Assert.True(McuVersionProtocol.TryDecodeQuery(
            Convert.FromHexString(payloadHex),
            out string actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("010500")]
    [InlineData("0105000000")]
    public void TryDecodeQuery_RejectsMalformedPayload(string payloadHex)
    {
        Assert.False(McuVersionProtocol.TryDecodeQuery(
            Convert.FromHexString(payloadHex),
            out string actual));
        Assert.Empty(actual);
    }

    [Fact]
    public async Task CommandClient_UsesExactCommandAndReturnsTypedVersion()
    {
        var session = new ScriptedSession(
            new HidSessionResult<ReadOnlyMemory<byte>>(
                Convert.FromHexString("01050000"),
                null));
        var client = new McuVersionCommandClient(session, ExchangeTimeout);

        HidSessionResult<string?> result = await client.QueryAsync(0x02, default);

        Assert.Null(result.Failure);
        Assert.Equal("1.5.0.0", result.Value);
        RazerCommandSpec command = Assert.Single(session.Commands);
        Assert.Equal((0x00, 0x87), (command.ClassId, command.CommandId));
        Assert.Equal([0x00, 0x00, 0x00, 0x00], command.Arguments.ToArray());
        Assert.Equal(4, command.ExpectedPayloadLength);
        Assert.True(command.PayloadValidator!(Convert.FromHexString("01050000")));
        Assert.Equal(0x02, session.TransactionIds.Single());
    }

    [Fact]
    public async Task CommandClient_MalformedReplyFailsClosed()
    {
        var client = new McuVersionCommandClient(
            new ScriptedSession(new HidSessionResult<ReadOnlyMemory<byte>>(
                new byte[] { 0x01, 0x05, 0x00 },
                null)),
            ExchangeTimeout);

        HidSessionResult<string?> result = await client.QueryAsync(1, default);

        Assert.Null(result.Value);
        Assert.Equal(HidSessionFailureKind.ProtocolFailure, result.Failure);
        Assert.Equal(
            HidProtocolValidationFailure.PayloadValidator,
            result.Diagnostic?.ProtocolValidationFailure);
    }

    [Fact]
    public void SanitizedFixture_ValidatesCapturedEnvelopeAndSynapseSemantics()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(FixturePath()));
        JsonElement root = document.RootElement;
        JsonElement exchange = root.GetProperty("exchange");

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("RZ09-0581", root.GetProperty("device").GetProperty("model").GetString());
        Assert.Equal("1532", root.GetProperty("device").GetProperty("vendorId").GetString());
        Assert.Equal("02E0", root.GetProperty("device").GetProperty("productId").GetString());
        Assert.Equal("3.01", root.GetProperty("device").GetProperty("biosVersion").GetString());
        Assert.True(root.GetProperty("readOnly").GetBoolean());
        Assert.True(root.GetProperty("productionWired").GetBoolean());
        Assert.Equal(73, exchange.GetProperty("setReportFrame").GetInt32());
        Assert.Equal(75, exchange.GetProperty("getReportFrame").GetInt32());
        Assert.Equal(76, exchange.GetProperty("responseFrame").GetInt32());

        byte transactionId = byte.Parse(
            exchange.GetProperty("transactionIdHex").GetString()!,
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture);
        byte[] requestEnvelope = Convert.FromHexString(
            exchange.GetProperty("requestEnvelopeHex").GetString()!);
        byte[] responseEnvelope = Convert.FromHexString(
            exchange.GetProperty("responseEnvelopeHex").GetString()!);
        RazerReport request = McuVersionProtocol.CreateQuery(transactionId);

        Assert.Equal(RazerReport.ReportLength, requestEnvelope.Length);
        Assert.Equal(RazerReport.ReportLength, responseEnvelope.Length);
        Assert.Equal(requestEnvelope, request.Encode());
        Assert.Equal(0x02, responseEnvelope[0]);
        ReadOnlyMemory<byte> payload = request.ValidateResponse(
            responseEnvelope,
            McuVersionProtocol.PayloadLength);
        Assert.Equal(
            exchange.GetProperty("expectedResponsePayloadHex").GetString(),
            Convert.ToHexString(payload.Span));
        Assert.True(McuVersionProtocol.TryDecodeQuery(payload.Span, out string version));
        Assert.Equal(exchange.GetProperty("decodedVersion").GetString(), version);

        JsonElement published = root.GetProperty("synapsePublishedCode");
        Assert.Equal([4, 0, 135], published.GetProperty("requestDescriptor")
            .EnumerateArray().Select(value => value.GetInt32()).ToArray());
        Assert.Equal("firmwareInfo.currentFWVersion", published.GetProperty("runtimeField").GetString());
        Assert.Equal("MCU", published.GetProperty("synapseLabel").GetString());
    }

    private static string FixturePath() => RepositoryFile(
        "evidence", "legacy-captures",
        "decoded",
        "rz09-0581-pid-02e0-bios-3.01-mcu-version-fixture.json");

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

    private sealed class ScriptedSession(
        params HidSessionResult<ReadOnlyMemory<byte>>[] results)
        : IRazerCommandSession
    {
        private readonly Queue<HidSessionResult<ReadOnlyMemory<byte>>> results =
            new(results);

        public RazerCommandSessionDisposition Disposition =>
            RazerCommandSessionDisposition.Healthy;

        public List<RazerCommandSpec> Commands { get; } = [];

        public List<byte> TransactionIds { get; } = [];

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
            RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId,
            TimeSpan exchangeTimeout,
            CancellationToken cancellationToken)
        {
            Commands.Add(command);
            TransactionIds.Add(assignTransactionId().TransactionId);
            return Task.FromResult(results.Dequeue());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
