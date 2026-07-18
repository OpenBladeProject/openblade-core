using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class LightingCaptureReportDecoderTests
{
    [Fact]
    public void CapturedBrightnessFixture_DecodesExactIndependentReadback()
    {
        string fixturePath = RepositoryFile(
            "captures",
            "decoded",
            "rz09-0581-pid-02e0-bios-3.01-lighting-brightness-query-fixture.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fixturePath));
        JsonElement root = document.RootElement;
        JsonElement exchange = root.GetProperty("exchange");
        byte[] request = Convert.FromHexString(
            exchange.GetProperty("requestEnvelopeHex").GetString()!);
        byte[] response = Convert.FromHexString(
            exchange.GetProperty("responseEnvelopeHex").GetString()!);

        Assert.Equal(RazerReport.ReportLength, request.Length);
        Assert.Equal(RazerReport.ReportLength, response.Length);
        Assert.True(LightingCaptureReportDecoder.TryDecode(request, out var decodedRequest));
        Assert.True(LightingCaptureReportDecoder.TryDecode(response, out var decodedResponse));
        Assert.Equal(LightingCaptureSemantic.BrightnessQuery, decodedRequest.Semantic);
        Assert.Equal(LightingCaptureSemantic.BrightnessQuery, decodedResponse.Semantic);
        Assert.Equal(decodedRequest.TransactionId, decodedResponse.TransactionId);
        Assert.Equal([0x01, 0x05, 0x00], decodedRequest.Payload.ToArray());
        Assert.Equal([0x01, 0x05, 0xFF], decodedResponse.Payload.ToArray());
        Assert.True(decodedResponse.IsIndependentBrightnessReadback);
        Assert.False(root.GetProperty("productionWired").GetBoolean());
        Assert.False(root.GetProperty("rawCaptureCommitted").GetBoolean());
    }

    [Fact]
    public void Decoder_RecognizesValidatedBrightnessQueryResponseAsIndependentReadback()
    {
        byte[] response = Report(
            status: 0x02,
            commandId: 0x83,
            payload: [0x01, 0x05, 0x7B]);

        Assert.True(LightingCaptureReportDecoder.TryDecode(response, out var decoded));
        Assert.Equal(LightingCaptureSemantic.BrightnessQuery, decoded.Semantic);
        Assert.Equal(0x31, decoded.TransactionId);
        Assert.Equal([0x01, 0x05, 0x7B], decoded.Payload.ToArray());
        Assert.True(decoded.IsSuccessfulResponse);
        Assert.True(decoded.IsIndependentBrightnessReadback);
    }

    [Fact]
    public void Decoder_DoesNotTreatBrightnessSetterAcknowledgementAsIndependentReadback()
    {
        byte[] acknowledgement = Report(
            status: 0x02,
            commandId: 0x03,
            payload: [0x01, 0x05, 0xFF]);

        Assert.True(LightingCaptureReportDecoder.TryDecode(acknowledgement, out var decoded));
        Assert.Equal(LightingCaptureSemantic.BrightnessSet, decoded.Semantic);
        Assert.True(decoded.IsSuccessfulResponse);
        Assert.False(decoded.IsIndependentBrightnessReadback);
    }

    [Fact]
    public void Decoder_DistinguishesPid02E0MatrixEntryFromReferenceEffectPacket()
    {
        byte[] matrixEntry = Report(
            status: 0x00,
            commandId: 0x0A,
            payload: [0x05, 0x00]);
        byte[] referenceStatic = Report(
            status: 0x00,
            commandId: 0x0A,
            payload: [0x06, 0x44, 0xD6, 0x2C, .. new byte[76]]);

        Assert.True(LightingCaptureReportDecoder.TryDecode(matrixEntry, out var entry));
        Assert.True(LightingCaptureReportDecoder.TryDecode(referenceStatic, out var candidate));
        Assert.Equal(LightingCaptureSemantic.CustomMatrixModeEntry, entry.Semantic);
        Assert.Equal(
            LightingCaptureSemantic.ReferenceHardwareEffectStaticCandidate,
            candidate.Semantic);
        Assert.False(entry.IsIndependentBrightnessReadback);
        Assert.False(candidate.IsIndependentBrightnessReadback);
    }

    [Theory]
    [InlineData("00", "ReferenceHardwareEffectOffCandidate")]
    [InlineData("0101", "ReferenceHardwareEffectWaveCandidate")]
    [InlineData("0102", "ReferenceHardwareEffectWaveCandidate")]
    [InlineData("04", "ReferenceHardwareEffectSpectrumCandidate")]
    [InlineData("06112233", "ReferenceHardwareEffectStaticCandidate")]
    public void Decoder_ClassifiesOnlyExactReferenceHardwareEffectShapes(
        string prefixHex,
        string expected)
    {
        byte[] prefix = Convert.FromHexString(prefixHex);
        byte[] payload = new byte[80];
        prefix.CopyTo(payload, 0);

        Assert.True(LightingCaptureReportDecoder.TryDecode(
            Report(0x00, 0x0A, payload),
            out var decoded));
        Assert.Equal(expected, decoded.Semantic.ToString());
    }

    [Theory]
    [InlineData("0103")]
    [InlineData("0200")]
    [InlineData("0401")]
    [InlineData("0611223301")]
    public void Decoder_LeavesUnrecognizedReferencePayloadsUnknown(string prefixHex)
    {
        byte[] prefix = Convert.FromHexString(prefixHex);
        byte[] payload = new byte[80];
        prefix.CopyTo(payload, 0);

        Assert.True(LightingCaptureReportDecoder.TryDecode(
            Report(0x00, 0x0A, payload),
            out var decoded));
        Assert.Equal(LightingCaptureSemantic.UnknownLightingCommand, decoded.Semantic);
    }

    [Theory]
    [InlineData(0x00, "010400", "ReferenceLogoOffCandidate")]
    [InlineData(0x00, "010401", "ReferenceLogoOnCandidate")]
    [InlineData(0x02, "010400", "ReferenceLogoStaticCandidate")]
    [InlineData(0x02, "010402", "ReferenceLogoBreathingCandidate")]
    public void Decoder_ClassifiesExactReferenceLogoShapes(
        byte commandId,
        string payloadHex,
        string expected)
    {
        Assert.True(LightingCaptureReportDecoder.TryDecode(
            Report(0x00, commandId, Convert.FromHexString(payloadHex)),
            out var decoded));
        Assert.Equal(expected, decoded.Semantic.ToString());
        Assert.False(decoded.IsIndependentBrightnessReadback);
    }

    [Theory]
    [InlineData(0x00, "010402")]
    [InlineData(0x02, "010401")]
    [InlineData(0x02, "000402")]
    public void Decoder_LeavesUnrecognizedReferenceLogoShapesUnknown(
        byte commandId,
        string payloadHex)
    {
        Assert.True(LightingCaptureReportDecoder.TryDecode(
            Report(0x00, commandId, Convert.FromHexString(payloadHex)),
            out var decoded));
        Assert.Equal(LightingCaptureSemantic.UnknownLightingCommand, decoded.Semantic);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(88)]
    [InlineData(89)]
    public void Decoder_RejectsDamagedEnvelopeOrChecksum(int changedIndex)
    {
        byte[] report = Report(
            status: 0x00,
            commandId: 0x83,
            payload: [0x01, 0x05, 0x00]);
        report[changedIndex] ^= 0x01;

        Assert.False(LightingCaptureReportDecoder.TryDecode(report, out _));
    }

    [Fact]
    public void Decoder_RejectsNonLightingReports()
    {
        byte[] report = Report(
            status: 0x00,
            commandId: 0x83,
            payload: [0x01, 0x05, 0x00]);
        report[6] = 0x07;
        Rechecksum(report);

        Assert.False(LightingCaptureReportDecoder.TryDecode(report, out _));
    }

    private static byte[] Report(byte status, byte commandId, byte[] payload)
    {
        byte[] report = new byte[RazerReport.ReportLength];
        report[0] = status;
        report[1] = 0x31;
        report[5] = checked((byte)payload.Length);
        report[6] = 0x03;
        report[7] = commandId;
        payload.CopyTo(report, 8);
        Rechecksum(report);
        return report;
    }

    private static void Rechecksum(byte[] report)
    {
        byte checksum = 0;
        for (int index = 2; index <= 87; index++)
        {
            checksum ^= report[index];
        }

        report[88] = checksum;
    }

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
