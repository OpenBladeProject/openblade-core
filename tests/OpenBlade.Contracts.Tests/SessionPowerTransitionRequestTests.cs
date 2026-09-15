namespace OpenBlade.Contracts.Tests;

public sealed class SessionPowerTransitionRequestTests
{
    [Theory]
    [InlineData(true, 10, 1, 20)]
    [InlineData(false, 0, 1, 20)]
    [InlineData(false, -1, 1, 20)]
    [InlineData(false, 10, 0, 20)]
    [InlineData(false, 10, -1, 20)]
    [InlineData(false, 10, 1, 9)]
    public async Task MalformedEpochOrSequenceDoesNotWriteAnyFrame(
        bool missingInstance,
        long started,
        long sequence,
        long timestamp)
    {
        var request = new ReportSessionPowerTransitionRequest(
            OpenBladeIpc.ProtocolVersion,
            Guid.NewGuid(),
            missingInstance ? Guid.Empty : Guid.NewGuid(),
            started,
            sequence,
            timestamp,
            true);
        using var stream = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            IpcMessageCodec.WriteRequestAsync(stream, request, CancellationToken.None));

        Assert.Equal(0, stream.Length);
    }
}
