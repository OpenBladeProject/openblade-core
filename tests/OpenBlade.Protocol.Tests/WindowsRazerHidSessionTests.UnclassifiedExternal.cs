using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class WindowsRazerHidSessionTests
{
    [Theory]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    [InlineData(3, 80)]
    [InlineData(5, 0)]
    [InlineData(5, 2)]
    [InlineData(5, 80)]
    public async Task AdapterTerminalRejection_RetainsFailureAndAllowsIndependentNextQuery(int status, int bodyLength)
    {
        byte[] reply = Response(1, 0x07, 0x8C, Enumerable.Repeat((byte)0xFF, bodyLength).ToArray());
        reply[1] = (byte)status;
        var io = new ScriptedIo(SetSuccess(), GetResult(reply),
            SetSuccess(), GetSuccess(2, 0x07, 0x8C, [0x11, 0x11]));
        var diagnostics = new FirmwareQueryDiagnostics();
        await using var actor = CreateActor(io, queryDiagnostics: diagnostics);
        var client = new AdapterPowerCommandClient(actor, Timeout);
        var unavailable = await client.QueryAsync(1, default);
        Assert.Equal(HidSessionFailureKind.ProtocolFailure, unavailable.Failure);
        Assert.True(unavailable.UnclassifiedAdapterResponseAvailable);
        Assert.Equal(HidProtocolValidationFailure.Status, unavailable.Diagnostic?.ProtocolValidationFailure);
        Assert.Equal(RazerCommandSessionDisposition.Healthy, actor.Disposition);
        Assert.Equal(2, io.Calls.Count);
        var known = await client.QueryAsync(2, default);
        Assert.Null(known.Failure);
        Assert.Equal(OpenBlade.Contracts.AdapterPowerState.FullPower280W, known.Value);
        var record = Assert.Single(diagnostics.Snapshot());
        Assert.Equal(1, record.FailureCount);
        Assert.Equal((byte)status, record.LastFailure!.ResponseStatus);
        Assert.Null(record.LastFailure.ResponsePayloadHex);
        Assert.Equal(4, io.Calls.Count);
    }

    [Theory]
    [InlineData("report-id")]
    [InlineData("checksum")]
    [InlineData("transaction")]
    [InlineData("class")]
    [InlineData("command")]
    [InlineData("remaining-low")]
    [InlineData("remaining-high")]
    [InlineData("protocol")]
    [InlineData("body-overflow")]
    [InlineData("reserved-tail")]
    public async Task AdapterRejection_InvalidFrameCannotCreateOrdinaryAuthority(string corruption)
    {
        byte[] reply = Response(1, 0x07, 0x8C, []);
        reply[1] = 3;
        int index = corruption switch
        {
            "report-id" => 0,
            "checksum" => 89,
            "transaction" => 2,
            "class" => 7,
            "command" => 8,
            "remaining-low" => 3,
            "remaining-high" => 4,
            "protocol" => 5,
            "body-overflow" => 6,
            "reserved-tail" => 90,
            _ => throw new InvalidOperationException(),
        };
        reply[index] = corruption == "body-overflow" ? (byte)81 : (byte)(reply[index] + 1);
        if (corruption != "checksum") { Rechecksum(reply); }
        var io = new ScriptedIo(SetSuccess(), GetResult(reply));
        await using var actor = CreateActor(io);
        var result = await new AdapterPowerCommandClient(actor, Timeout).QueryAsync(1, default);
        Assert.NotNull(result.Failure);
        Assert.False(result.UnclassifiedAdapterResponseAvailable);
        Assert.NotEqual(RazerCommandSessionDisposition.Healthy, actor.Disposition);
        Assert.Equal(2, io.Calls.Count);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public async Task AdapterOuterNativeError_IsNotRawFirmwareStatus(int outerError)
    {
        var io = new ScriptedIo(SetSuccess(), (_, _, buffer, _, _) =>
        {
            byte[] reply = Response(1, 0x07, 0x8C, []);
            reply[1] = 3;
            reply.CopyTo(buffer, 0);
            return Task.FromResult(new HidFeatureIoResult(new NativeIoCompletion(false, 91, outerError),
                true, HidFeatureTermination.Completed));
        });
        await using var actor = CreateActor(io);
        var result = await new AdapterPowerCommandClient(actor, Timeout).QueryAsync(1, default);
        Assert.Equal(HidSessionFailureKind.NativeIoFailure, result.Failure);
        Assert.False(result.UnclassifiedAdapterResponseAvailable);
        Assert.Equal(outerError, result.Diagnostic?.NativeWin32Error);
        Assert.NotEqual(RazerCommandSessionDisposition.Healthy, actor.Disposition);
    }

    [Fact]
    public async Task AdapterRejection_CancellationCannotEstablishCompletedOrdinaryAuthority()
    {
        using var canceled = new CancellationTokenSource();
        var io = new ScriptedIo(SetSuccess(), (_, _, buffer, _, _) =>
        {
            byte[] reply = Response(1, 0x07, 0x8C, []);
            reply[1] = 5;
            reply.CopyTo(buffer, 0);
            canceled.Cancel();
            return Task.FromResult(new HidFeatureIoResult(new NativeIoCompletion(true, 91, 0),
                true, HidFeatureTermination.Completed));
        });
        await using var actor = CreateActor(io);
        var result = await new AdapterPowerCommandClient(actor, Timeout).QueryAsync(1, canceled.Token);
        Assert.Equal(HidSessionFailureKind.Canceled, result.Failure);
        Assert.False(result.UnclassifiedAdapterResponseAvailable);
    }

    [Fact]
    public void AdapterRejection_UnretiredNativeOperationCannotEstablishAuthority()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] raw = Response(1, 0x07, 0x8C, [])[1..];
        raw[0] = 5;
        var query = new RazerCommandSpec(7, 0x8C, new byte[] { 0, 0 }, 2);
        Assert.False(OrdinaryThermalPowerAdmission.IsCompletedAdapterRejection(query, raw,
            HidProtocolValidationFailure.Status, pending.Task));
        pending.SetResult();
        Assert.True(OrdinaryThermalPowerAdmission.IsCompletedAdapterRejection(query, raw,
            HidProtocolValidationFailure.Status, pending.Task));
        Assert.False(OrdinaryThermalPowerAdmission.IsCompletedAdapterRejection(
            query with { CommandId = 0x8F }, raw, HidProtocolValidationFailure.Status, null));
    }
}
