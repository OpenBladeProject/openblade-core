using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class DeviceStateContentRejectionTests
{
    [Theory]
    [InlineData(HidProtocolValidationFailure.PayloadValidator, true)]
    [InlineData(HidProtocolValidationFailure.ReportId, false)]
    [InlineData(HidProtocolValidationFailure.EnvelopeLength, false)]
    [InlineData(HidProtocolValidationFailure.Checksum, false)]
    [InlineData(HidProtocolValidationFailure.TransactionMismatch, false)]
    [InlineData(HidProtocolValidationFailure.CommandClassMismatch, false)]
    [InlineData(HidProtocolValidationFailure.CommandIdMismatch, false)]
    [InlineData(HidProtocolValidationFailure.Status, false)]
    [InlineData(HidProtocolValidationFailure.PayloadLength, false)]
    public void OnlyTransactionMatchedPayloadContentIsAContentRejection(
        HidProtocolValidationFailure failure,
        bool expected)
    {
        Assert.Equal(expected, DeviceStateContentRejection.IsContentRejection(failure));
        Assert.Equal(
            expected,
            DeviceStateContentRejection.IsContentRejection(
                new RazerProtocolException("rejected", failure)
                {
                    StateQuery = DeviceStateQuery.TemporaryFullCharge,
                    RejectedPayloadHex = "05",
                }));
    }

    [Fact]
    public void RejectionWithoutQueryIdentityOrPayloadEvidenceIsNotIsolated()
    {
        Assert.False(DeviceStateContentRejection.IsContentRejection(
            new RazerProtocolException(
                "rejected",
                HidProtocolValidationFailure.PayloadValidator)
            {
                RejectedPayloadHex = "05",
            }));
        Assert.False(DeviceStateContentRejection.IsContentRejection(
            new RazerProtocolException(
                "defaulted category before any exchange",
                HidProtocolValidationFailure.PayloadValidator)
            {
                StateQuery = DeviceStateQuery.TemporaryFullCharge,
            }));
    }

    [Theory]
    [InlineData(new byte[] { }, "(empty)")]
    [InlineData(new byte[] { 0x05 }, "05")]
    [InlineData(new byte[] { 0x0a, 0xff, 0x00 }, "0A FF 00")]
    [InlineData(
        new byte[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 },
        "00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F ...")]
    public void FormatPayload_IsUppercaseHexCappedAtSixteenBytes(byte[] payload, string expected)
    {
        Assert.Equal(expected, DeviceStateContentRejection.FormatPayload(payload));
    }

    [Fact]
    public void Describe_UsesClosedQueryNameAndNeverExceptionText()
    {
        var exception = new RazerProtocolException(
            "raw report, serial, and device path detail",
            HidProtocolValidationFailure.PayloadValidator)
        {
            StateQuery = DeviceStateQuery.TemporaryFullCharge,
            RejectedPayloadHex = "05",
        };

        string message = DeviceStateContentRejection.Describe(exception, "Device state query");

        Assert.Equal(
            "Temporary full charge query (07/8F) returned unrecognized payload 05 (PayloadValidator).",
            message);
        Assert.DoesNotContain("serial", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", message, StringComparison.OrdinalIgnoreCase);
    }
}
