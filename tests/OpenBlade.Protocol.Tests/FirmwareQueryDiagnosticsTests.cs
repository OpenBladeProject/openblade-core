using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class FirmwareQueryDiagnosticsTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0x00, 0x84, "0000")]
    [InlineData(0x00, 0x87, "00000000")]
    [InlineData(0x00, 0x88, "00000000")]
    [InlineData(0x02, 0x86, "0100")]
    [InlineData(0x03, 0x80, "010400")]
    [InlineData(0x03, 0x80, "000400")]
    [InlineData(0x03, 0x82, "010400")]
    [InlineData(0x03, 0x82, "000400")]
    [InlineData(0x03, 0x83, "010500")]
    [InlineData(0x07, 0x8C, "0000")]
    [InlineData(0x07, 0x8F, "00")]
    [InlineData(0x07, 0x92, "00")]
    [InlineData(0x0D, 0x82, "00010000")]
    [InlineData(0x0D, 0x82, "01020000")]
    [InlineData(0x0D, 0x87, "000100")]
    [InlineData(0x0D, 0x87, "010200")]
    [InlineData(0x0D, 0x88, "010100")]
    [InlineData(0x0D, 0x8E, "00")]
    [InlineData(0x0D, 0x8F, "00")]
    [InlineData(0x0F, 0x98, "00")]
    public void CanRecord_AdmitsOnlyKnownGetterShapes(int commandClass, int commandId, string request)
    {
        Assert.True(FirmwareQueryDiagnostics.CanRecord(Command(commandClass, commandId, request)));
    }

    [Theory]
    [InlineData(0x00, 0x82, "0000000000000000000000000000000000000000000000")]
    [InlineData(0x00, 0x86, "000000")]
    [InlineData(0x07, 0x12, "D0")]
    [InlineData(0x07, 0x8C, "0001")]
    [InlineData(0x07, 0x8C, "00")]
    [InlineData(0x03, 0x83, "010400")]
    [InlineData(0x0D, 0x82, "01030000")]
    [InlineData(0x0D, 0x87, "020100")]
    [InlineData(0x0D, 0x88, "000100")]
    [InlineData(0xFF, 0xFF, "00")]
    public void Record_ExcludesSerialWritesAndUnknownRequests(int commandClass, int commandId, string request)
    {
        var recorder = new FirmwareQueryDiagnostics();
        Assert.Null(recorder.Record(Command(commandClass, commandId, request), Success("AB"), Timestamp));
        Assert.Empty(recorder.Snapshot());
    }

    [Fact]
    public void CanRecord_RejectsChangedLengthAndVariableResponseContracts()
    {
        RazerCommandSpec adapter = Command(0x07, 0x8C, "0000");
        Assert.False(FirmwareQueryDiagnostics.CanRecord(adapter with { ExpectedPayloadLength = 80 }));
        Assert.False(FirmwareQueryDiagnostics.CanRecord(adapter with { AllowVariableResponseLength = true }));
        RazerCommandSpec effect = Command(0x0F, 0x82, "010500") with
        {
            AllowVariableResponseLength = true,
            PayloadValidator = static payload => payload.Length is >= 3 and <= 80,
        };
        Assert.True(FirmwareQueryDiagnostics.CanRecord(effect));
        Assert.False(FirmwareQueryDiagnostics.CanRecord(effect with { PayloadValidator = null }));
        Assert.True(FirmwareQueryDiagnostics.CanRecord(new(0x0D, 0x80, new byte[80], 80)));
    }

    [Fact]
    public void FailureEvidence_SurvivesSuccessAndSubsequentFailureWithoutReply()
    {
        var recorder = new FirmwareQueryDiagnostics();
        RazerCommandSpec command = Command(0x07, 0x8C, "0000");
        FirmwareQueryObservation first = recorder.Record(command, Success("0111"), Timestamp)!;
        recorder.RecordRejection(first, FirmwareQueryOutcome.UnrecognizedValue);
        recorder.Record(command, Success("0711"), Timestamp.AddSeconds(1));
        recorder.Record(command, new(default, HidSessionFailureKind.Timeout), Timestamp.AddSeconds(2));

        FirmwareQueryDiagnostic entry = Assert.Single(recorder.Snapshot());
        Assert.Equal(3, entry.ObservationCount);
        Assert.Equal(2, entry.FailureCount);
        Assert.Equal(FirmwareQueryOutcome.ExchangeFailed, entry.Latest.Outcome);
        Assert.Equal("0111", entry.FirstFailure!.ResponsePayloadHex);
        Assert.Null(entry.LastFailure!.ResponsePayloadHex);
        Assert.Equal(first.Id, entry.LastResponseFailure!.Id);
    }

    [Fact]
    public void Rejection_CorrelatesOlderObservationWithoutReplacingNewerReplyOrDoubleCounting()
    {
        var recorder = new FirmwareQueryDiagnostics();
        RazerCommandSpec command = Command(0x07, 0x8C, "0000");
        FirmwareQueryObservation old = recorder.Record(command, Success("1211"), Timestamp)!;
        FirmwareQueryObservation current = recorder.Record(command, Success("0711"), Timestamp)!;
        recorder.RecordRejection(old, FirmwareQueryOutcome.ModelAdmissionRejected);
        recorder.RecordRejection(old, FirmwareQueryOutcome.ContentRejected);
        new FirmwareQueryDiagnostics().RecordRejection(current, FirmwareQueryOutcome.ContentRejected);

        FirmwareQueryDiagnostic entry = Assert.Single(recorder.Snapshot());
        Assert.Equal(2, entry.ObservationCount);
        Assert.Equal(1, entry.FailureCount);
        Assert.Equal(current.Id, entry.Latest.Id);
        Assert.Equal(FirmwareQueryOutcome.Succeeded, entry.Latest.Outcome);
        Assert.Equal(FirmwareQueryOutcome.ModelAdmissionRejected, entry.FirstFailure!.Outcome);
    }

    [Fact]
    public void Snapshot_IsImmutableAndSelectorsRemainSeparate()
    {
        var recorder = new FirmwareQueryDiagnostics();
        RazerCommandSpec first = Command(0x0D, 0x82, "01010000");
        RazerCommandSpec second = Command(0x0D, 0x82, "01020000");
        FirmwareQueryObservation observed = recorder.Record(first, Success("01010000"), Timestamp)!;
        IReadOnlyList<FirmwareQueryDiagnostic> snapshot = recorder.Snapshot();
        recorder.RecordRejection(observed, FirmwareQueryOutcome.ContentRejected);
        for (int index = 0; index < 1000; index++)
        {
            recorder.Record(second, Success("01020000"), Timestamp);
        }

        Assert.Single(snapshot);
        Assert.Equal(0, snapshot[0].FailureCount);
        Assert.Equal(FirmwareQueryOutcome.Succeeded, snapshot[0].Latest.Outcome);
        Assert.Throws<NotSupportedException>(() => ((IList<FirmwareQueryDiagnostic>)snapshot).Clear());
        Assert.Equal(2, recorder.Snapshot().Count);
        Assert.Equal(1000, recorder.Snapshot()[1].ObservationCount);
    }

    [Fact]
    public void Record_PreservesFullValidatedPayloadAndTypedFailureMetadata()
    {
        var recorder = new FirmwareQueryDiagnostics();
        string fullPayload = string.Concat(Enumerable.Repeat("AB", 80));
        var failure = new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.ProtocolFailure)
        {
            QueryResponsePayloadHex = fullPayload,
            Diagnostic = new(HidSessionFailurePhase.ProtocolValidation, HidSessionFailureKind.ProtocolFailure, 5, 91)
            {
                ProtocolValidationFailure = HidProtocolValidationFailure.PayloadValidator,
                RejectedPayloadHex = "AB ...",
            },
        };
        FirmwareQueryObservation observed = recorder.Record(new(0x0D, 0x80, new byte[80], 80), failure, Timestamp)!;
        Assert.Equal(fullPayload, observed.ResponsePayloadHex);
        Assert.Equal(HidProtocolValidationFailure.PayloadValidator, observed.ProtocolValidationFailure);
        Assert.Equal(5, observed.NativeWin32Error);
        Assert.Equal(91, observed.TransferredByteCount);
        Assert.DoesNotContain("Registration", System.Text.Json.JsonSerializer.Serialize(recorder.Snapshot()));
    }

    [Theory]
    [InlineData("AB CD")]
    [InlineData("serial-or-path")]
    [InlineData("A")]
    public void Record_RejectsNonHexEvidence(string value)
    {
        var recorder = new FirmwareQueryDiagnostics();
        Assert.Null(recorder.Record(Command(0x07, 0x8C, "0000"), Success(value), Timestamp)!.ResponsePayloadHex);
        Assert.Null(recorder.Record(Command(0x07, 0x8C, "0000"), Success(new string('A', 162)), Timestamp)!.ResponsePayloadHex);
    }

    [Fact]
    public void ConcurrentRecording_KeepsExactCounts()
    {
        var recorder = new FirmwareQueryDiagnostics();
        Parallel.For(0, 100, _ =>
        {
            FirmwareQueryObservation observation = recorder.Record(Command(0x07, 0x8C, "0000"), Success("0111"), Timestamp)!;
            recorder.RecordRejection(observation, FirmwareQueryOutcome.UnrecognizedValue);
        });
        FirmwareQueryDiagnostic entry = Assert.Single(recorder.Snapshot());
        Assert.Equal(100, entry.ObservationCount);
        Assert.Equal(100, entry.FailureCount);
        Assert.Equal(1, entry.FirstFailure!.Id);
        Assert.Equal(100, entry.LastFailure!.Id);
    }

    private static RazerCommandSpec Command(int commandClass, int commandId, string request)
    {
        byte[] arguments = Convert.FromHexString(request);
        return new((byte)commandClass, (byte)commandId, arguments, arguments.Length);
    }

    private static HidSessionResult<ReadOnlyMemory<byte>> Success(string payload) => new(ReadOnlyMemory<byte>.Empty, null)
    {
        QueryResponsePayloadHex = payload,
    };
}
