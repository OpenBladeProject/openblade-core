using OpenSynapse.Protocol;

namespace OpenSynapse.Protocol.Tests;

public sealed class RazerReportTests
{
    [Fact]
    public void Encode_MatchesKnownBladeFeatureReport()
    {
        RazerReport report = RazerReport.Create(
            transactionId: 0x1C,
            commandClass: 0x0D,
            commandId: 0x02,
            payload: [0x01, 0x01, 0x00, 0x00]);

        byte[] encoded = report.Encode();

        Assert.Equal(90, encoded.Length);
        Assert.Equal(
            new byte[] { 0x00, 0x1C, 0x00, 0x00, 0x00, 0x04, 0x0D, 0x02, 0x01, 0x01, 0x00, 0x00 },
            encoded[..12]);
        Assert.Equal(0x0B, encoded[88]);
        Assert.Equal(0x00, encoded[89]);
    }

    [Fact]
    public void Create_RejectsPayloadLargerThanEightyBytes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RazerReport.Create(1, 1, 1, new byte[81]));
    }

    [Fact]
    public void ValidateResponse_RejectsMismatchedTransaction()
    {
        RazerReport request = RazerReport.Create(0x10, 0x0D, 0x02, [1]);
        byte[] response = RazerReport.Create(0x11, 0x0D, 0x02, [1]).Encode();
        response[0] = (byte)RazerCommandStatus.Successful;

        RazerProtocolException error = Assert.Throws<RazerProtocolException>(
            () => request.ValidateResponse(response));

        Assert.Equal(HidProtocolValidationFailure.TransactionMismatch, error.ValidationFailure);
    }

    [Fact]
    public void ValidateResponse_RejectsPayloadLengthThatDiffersFromCommandContract()
    {
        RazerReport request = RazerReport.Create(0x10, 0x0D, 0x02, []);
        byte[] response = RazerReport.Create(0x10, 0x0D, 0x02, [0xAA]).Encode();
        response[0] = (byte)RazerCommandStatus.Successful;

        RazerProtocolException error = Assert.Throws<RazerProtocolException>(
            () => request.ValidateResponse(response, expectedPayloadLength: 0));

        Assert.Equal(HidProtocolValidationFailure.PayloadLength, error.ValidationFailure);
    }
}
