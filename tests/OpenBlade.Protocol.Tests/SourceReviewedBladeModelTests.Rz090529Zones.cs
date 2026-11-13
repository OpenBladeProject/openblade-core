using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed partial class SourceReviewedBladeModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rz090529_ZoneRoundTripIsIndependentAndRestoresExactBoolean(bool touchpad)
    {
        var session = new ZoneSession();
        await using var port = CreateZonePort(session);
        IndependentLightingSettings fields = ZoneFields(touchpad, true);
        IndependentLightingSettings? baseline = await port.ReadIndependentLightingAsync(fields, default);
        Assert.Equal(ZoneFields(touchpad, false), baseline);
        await port.SetIndependentLightingAsync(fields, default);
        Assert.Equal(fields, await port.ReadIndependentLightingAsync(fields, default));
        await port.SetIndependentLightingAsync(baseline!, default);
        Assert.Equal(baseline, await port.ReadIndependentLightingAsync(fields, default));
        Assert.False(session.VaporChamber);
        Assert.False(session.TouchpadIndicator);
        Assert.All(session.Commands, command => Assert.True(
            command.ClassId == 3 && command.CommandId is 0 or 0x80 && command.Arguments.Span[1] == 38
            || command.ClassId == 0 && command.CommandId is 0x59 or 0xD9 && command.Arguments.Span[1] == 1));
        Assert.Equal(2, session.Commands.Count(command => command.CommandId < 0x80));
    }

    [Theory]
    [InlineData(false, "001F000000030300012601", 0x26)]
    [InlineData(true, "001F000000030059000101", 0x5A)]
    public async Task Rz090529_ZoneSetterHasKnownAnswerReport(bool touchpad, string prefix, int checksum)
    {
        var session = new ZoneSession();
        await using var port = CreateZonePort(session);
        await port.SetIndependentLightingAsync(ZoneFields(touchpad, true), default);
        RazerCommandSpec setter = Assert.Single(session.Commands, command => command.CommandId < 0x80);
        byte[] report = RazerReport.Create(0x1F, setter.ClassId, setter.CommandId, setter.Arguments.Span).Encode();
        Assert.Equal(prefix, Convert.ToHexString(report.AsSpan(0, 11)));
        Assert.All(report.AsSpan(11, 77).ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(checksum, report[88]);
        Assert.Equal(0, report[89]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rz090529_UnknownZoneBaselineNeverWrites(bool touchpad)
    {
        var session = new ZoneSession { MalformedRead = true };
        await using var port = CreateZonePort(session);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetIndependentLightingAsync(ZoneFields(touchpad, true), default));
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
    }

    [Fact]
    public async Task Rz090529_AmbiguousFirstZoneStopsSecondZoneAndDoesNotRestoreOrReplay()
    {
        var session = new ZoneSession { IgnoreWrites = true };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02C7));
        await using var port = CreateZonePort(session, policy);
        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetIndependentLightingAsync(new(null, null, true, true), default));
        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.DoesNotContain(session.Commands, command => command.ClassId == 0);
        Assert.Single(session.Commands, command => command.CommandId < 0x80);
        int count = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetIndependentLightingAsync(new(null, null, true, true), default));
        Assert.Equal(count, session.Commands.Count);
    }

    [Fact]
    public async Task Rz090529_IdentityLossAtZoneSubmissionBlocksWrite()
    {
        bool identity = true;
        var session = new ZoneSession
        {
            BeforeSubmission = command =>
            {
                if (command.CommandId < 0x80)
                {
                    identity = false;
                }
            },
        };
        await using var port = CreateZonePort(session, identity: () => identity);
        await Assert.ThrowsAsync<BladeWriteException>(() => port.SetIndependentLightingAsync(new(null, null, true), default));
        Assert.DoesNotContain(session.Commands, command => command.CommandId < 0x80);
    }

    [Theory]
    [InlineData(0x0245)]
    [InlineData(0x02C5)]
    [InlineData(0x02B8)]
    public async Task OtherModelsRejectRz090529ZoneWritesBeforeQuery(int product)
    {
        var session = new ZoneSession();
        await using var port = new ExperimentalBladePort(session, new(), Model(product), new(Model(product)),
            () => true, () => PowerSource.PluggedIn);
        await Assert.ThrowsAsync<CapabilityUnavailableException>(() => port.SetIndependentLightingAsync(new(null, null, true, true), default));
        Assert.Empty(session.Commands);
    }

    private static IndependentLightingSettings ZoneFields(bool touchpad, bool value) => touchpad
        ? new(null, null, null, value) : new(null, null, value);

    private static ExperimentalBladePort CreateZonePort(ZoneSession session,
        ExperimentalFeaturesPolicy? policy = null, Func<bool>? identity = null) =>
        new(session, new(), Model(0x02C7), policy ?? new(Model(0x02C7)), identity ?? (() => true), () => PowerSource.PluggedIn);

    private sealed class ZoneSession : IRazerCommandSession
    {
        internal bool VaporChamber;
        internal bool TouchpadIndicator;
        internal bool IgnoreWrites;
        internal bool MalformedRead;
        internal Action<RazerCommandSpec>? BeforeSubmission;
        internal List<RazerCommandSpec> Commands { get; } = [];
        public RazerCommandSessionDisposition Disposition => RazerCommandSessionDisposition.Healthy;

        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId, TimeSpan exchangeTimeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeSubmission?.Invoke(command);
            if (command.SubmissionAllowed?.Invoke() == false)
            {
                return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.IdentityBlocked));
            }
            Commands.Add(command);
            assignTransactionId();
            byte[] arguments = command.Arguments.ToArray();
            byte[] response;
            switch (command.ClassId, command.CommandId)
            {
                case (3, 0x80): response = [0, 38, MalformedRead ? (byte)2 : VaporChamber ? (byte)1 : (byte)0]; break;
                case (0, 0xD9): response = [0, 1, MalformedRead ? (byte)2 : TouchpadIndicator ? (byte)1 : (byte)0]; break;
                case (3, 0):
                    if (!IgnoreWrites)
                    {
                        VaporChamber = arguments[2] == 1;
                    }
                    response = [];
                    break;
                case (0, 0x59):
                    if (!IgnoreWrites)
                    {
                        TouchpadIndicator = arguments[2] == 1;
                    }
                    response = [];
                    break;
                default: throw new InvalidOperationException($"Unexpected command {command.ClassId:X2}/{command.CommandId:X2}");
            }
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(response, null));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
