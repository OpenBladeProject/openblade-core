using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class Pid02E0MatrixReportTests
{
    private static readonly int[] ExpectedHoleSlots =
        [0, 17, 31, 34, 48, 51, 64, 65, 68, 70, 81, 82, 85, 89, 92, 93];

    [Fact]
    public void Fixture_RecordsExactExtendedReportContractOutsideProduction()
    {
        using JsonDocument fixture = LoadFixture();
        JsonElement root = fixture.RootElement;
        JsonElement protocol = root.GetProperty("protocol");

        Assert.Equal("RZ09-0581", root.GetProperty("device").GetProperty("model").GetString());
        Assert.Equal("02E0", root.GetProperty("device").GetProperty("pid").GetString());
        Assert.False(root.GetProperty("rawCapturesCommitted").GetBoolean());
        Assert.False(root.GetProperty("productionWired").GetBoolean());
        Assert.False(root.GetProperty("productionAdmitted").GetBoolean());
        Assert.Equal(Pid02E0MatrixReportCodec.FeatureReportBufferLength,
            protocol.GetProperty("featureReportBufferLength").GetInt32());
        Assert.Equal(Pid02E0MatrixReportCodec.EnvelopeLength,
            protocol.GetProperty("envelopeLength").GetInt32());
        Assert.Equal("0136", protocol.GetProperty("payloadLengthHex").GetString());
        Assert.Equal("03", protocol.GetProperty("classHex").GetString());
        Assert.Equal("0B", protocol.GetProperty("idHex").GetString());
        Assert.Equal("00000065", protocol.GetProperty("payloadHeaderHex").GetString());
        Assert.Equal(102, protocol.GetProperty("rgbSlotCount").GetInt32());
        Assert.Equal(ExpectedHoleSlots,
            root.GetProperty("layout").GetProperty("holeSlots")
                .EnumerateArray().Select(slot => slot.GetInt32()).ToArray());
        Assert.False(typeof(Pid02E0MatrixReportCodec).IsPublic);
        Assert.False(typeof(Pid02E0MatrixRenderers).IsPublic);
        Assert.False(typeof(Pid02E0ReactiveFrame).IsPublic);
    }

    [Fact]
    public void Codec_RoundTripsEveryExactCapturedEnvelopeAndReportIdBuffer()
    {
        using JsonDocument fixture = LoadFixture();

        foreach (JsonElement item in fixture.RootElement.GetProperty("frames").EnumerateArray())
        {
            byte[] capturedEnvelope = Convert.FromBase64String(
                item.GetProperty("envelopeBase64").GetString()!);
            byte expectedTransaction = Convert.ToByte(
                item.GetProperty("transactionIdHex").GetString(), 16);

            Pid02E0MatrixReport decoded =
                Pid02E0MatrixReportCodec.DecodeEnvelope(capturedEnvelope);

            Assert.Equal(expectedTransaction, decoded.TransactionId);
            Assert.Equal(capturedEnvelope, Pid02E0MatrixReportCodec.EncodeEnvelope(decoded));

            byte[] reportIdBuffer = Pid02E0MatrixReportCodec.EncodeFeatureReportBuffer(decoded);
            Assert.Equal(375, reportIdBuffer.Length);
            Assert.Equal(0, reportIdBuffer[0]);
            Assert.Equal(capturedEnvelope, reportIdBuffer[1..]);

            Pid02E0MatrixReport decodedBuffer =
                Pid02E0MatrixReportCodec.DecodeFeatureReportBuffer(reportIdBuffer);
            Assert.Equal(expectedTransaction, decodedBuffer.TransactionId);
            Assert.Equal(capturedEnvelope, Pid02E0MatrixReportCodec.EncodeEnvelope(decodedBuffer));
        }
    }

    [Fact]
    public void StaticRenderer_MatchesExactCapturedStableFrameAndBlackHoleMask()
    {
        JsonElement fixture = Frame("Static");
        byte[] captured = Envelope(fixture);
        byte transaction = Transaction(fixture);
        MatrixRgb green = ParseRgb(fixture.GetProperty("configuredColorHex").GetString()!);

        Pid02E0MatrixFrame frame = Pid02E0MatrixRenderers.RenderStatic(green);
        byte[] rendered = Pid02E0MatrixReportCodec.EncodeEnvelope(
            new Pid02E0MatrixReport(transaction, frame));

        Assert.Equal(captured, rendered);
        Assert.Equal(ExpectedHoleSlots, Pid02E0MatrixLayout.CapturedHoleSlots);
        for (int slot = 0; slot < Pid02E0MatrixFrame.SlotCount; slot++)
        {
            Assert.Equal(slot is 0 or 17 or 31 or 34 or 48 or 51 or 64 or 65
                or 68 or 70 or 81 or 82 or 85 or 89 or 92 or 93,
                Pid02E0MatrixLayout.IsHole(slot));
            Assert.Equal(Pid02E0MatrixLayout.IsHole(slot) ? MatrixRgb.Black : green, frame[slot]);
        }
    }

    [Fact]
    public void WithSlot_RejectsEveryCapturedLayoutHole()
    {
        Pid02E0MatrixFrame frame =
            Pid02E0MatrixLayout.RenderUniform(new MatrixRgb(1, 2, 3));

        foreach (int hole in ExpectedHoleSlots)
        {
            ArgumentOutOfRangeException exception = Assert.Throws<
                ArgumentOutOfRangeException>(
                    () => frame.WithSlot(hole, new MatrixRgb(0xFF, 0, 0)));

            Assert.Equal("index", exception.ParamName);
        }
    }

    [Fact]
    public void WithSlot_ReplacesOnlyTheRequestedPhysicalSlot()
    {
        var originalColor = new MatrixRgb(1, 2, 3);
        var replacement = new MatrixRgb(0xFF, 0, 0);
        Pid02E0MatrixFrame original =
            Pid02E0MatrixLayout.RenderUniform(originalColor);

        Pid02E0MatrixFrame changed = original.WithSlot(2, replacement);

        Assert.Equal(originalColor, original[2]);
        Assert.Equal(replacement, changed[2]);
        Assert.Equal(originalColor, changed[3]);
    }

    [Fact]
    public void SpectrumSampleRenderer_MatchesExactFirstCapturedSpectrumFrame()
    {
        JsonElement fixture = Frame("Spectrum");
        MatrixRgb sample = ParseRgb(fixture.GetProperty("hostColorSampleHex").GetString()!);

        Pid02E0MatrixFrame frame = Pid02E0MatrixRenderers.RenderSpectrumSample(sample);
        byte[] rendered = Pid02E0MatrixReportCodec.EncodeEnvelope(
            new Pid02E0MatrixReport(Transaction(fixture), frame));

        Assert.Equal(Envelope(fixture), rendered);
    }

    [Fact]
    public void BreathingSampleRenderer_MatchesExactCapturedIntensityFrame()
    {
        JsonElement fixture = Frame("Breathing");
        MatrixRgb configured = ParseRgb(fixture.GetProperty("configuredColorHex").GetString()!);
        byte intensity = Convert.ToByte(fixture.GetProperty("intensityHex").GetString(), 16);

        Pid02E0MatrixFrame frame =
            Pid02E0MatrixRenderers.RenderBreathingSample(configured, intensity);
        byte[] rendered = Pid02E0MatrixReportCodec.EncodeEnvelope(
            new Pid02E0MatrixReport(Transaction(fixture), frame));

        Assert.Equal(new MatrixRgb(0, 130, 0), frame[1]);
        Assert.Equal(Envelope(fixture), rendered);
    }

    [Fact]
    public void ReactiveBlackIdleFrame_MatchesExactCapturedIdleFrame()
    {
        JsonElement fixture = Frame("Reactive");
        Pid02E0ReactiveFrame reactive = Pid02E0ReactiveFrame.BlackIdle();

        byte[] rendered = Pid02E0MatrixReportCodec.EncodeEnvelope(
            new Pid02E0MatrixReport(Transaction(fixture), reactive.ToMatrixFrame()));

        Assert.All(Enumerable.Range(0, Pid02E0MatrixFrame.SlotCount),
            slot => Assert.Equal(MatrixRgb.Black, reactive[slot]));
        Assert.Equal(Envelope(fixture), rendered);
    }

    [Fact]
    public void ReactiveFrame_RejectsColorInCapturedLayoutHole()
    {
        MatrixRgb[] slots = new MatrixRgb[Pid02E0MatrixFrame.SlotCount];
        slots[17] = new MatrixRgb(1, 2, 3);

        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new Pid02E0ReactiveFrame(slots));

        Assert.Contains("slot 17", error.Message);
    }

    [Fact]
    public void Codec_RejectsMalformedLengthHeaderPaddingChecksumTerminatorAndReportId()
    {
        byte[] captured = Envelope(Frame("Static"));

        Assert.Throws<FormatException>(() =>
            Pid02E0MatrixReportCodec.DecodeEnvelope(captured[..^1]));

        AssertMalformed(captured, 5, 0x35);
        AssertMalformed(captured, 6, 0x04);
        AssertMalformed(captured, 318, 0x01);
        AssertMalformed(captured, 372, (byte)(captured[372] ^ 0x01));
        AssertMalformed(captured, 373, 0x01);

        byte[] featureBuffer = [0x01, .. captured];
        Assert.Throws<FormatException>(() =>
            Pid02E0MatrixReportCodec.DecodeFeatureReportBuffer(featureBuffer));
    }

    private static void AssertMalformed(byte[] valid, int offset, byte value)
    {
        byte[] malformed = (byte[])valid.Clone();
        malformed[offset] = value;
        Assert.Throws<FormatException>(() =>
            Pid02E0MatrixReportCodec.DecodeEnvelope(malformed));
    }

    private static JsonElement Frame(string effect)
    {
        using JsonDocument fixture = LoadFixture();
        return fixture.RootElement.GetProperty("frames").EnumerateArray()
            .Single(item => item.GetProperty("effect").GetString() == effect)
            .Clone();
    }

    private static byte[] Envelope(JsonElement fixture) =>
        Convert.FromBase64String(fixture.GetProperty("envelopeBase64").GetString()!);

    private static byte Transaction(JsonElement fixture) =>
        Convert.ToByte(fixture.GetProperty("transactionIdHex").GetString(), 16);

    private static MatrixRgb ParseRgb(string hex) => new(
        Convert.ToByte(hex[..2], 16),
        Convert.ToByte(hex.Substring(2, 2), 16),
        Convert.ToByte(hex.Substring(4, 2), 16));

    private static JsonDocument LoadFixture() =>
        JsonDocument.Parse(File.ReadAllText(RepositoryFile(
            "captures",
            "decoded",
            "rz09-0581-pid-02e0-bios-3.01-lighting-matrix-fixture.json")));

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
