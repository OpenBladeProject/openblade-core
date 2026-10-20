using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalThermalExpansionTests
{
    [Theory]
    [InlineData(0x029F, "", "")]
    [InlineData(0x029F, "00", "00")]
    [InlineData(0x029F, "00010203", "000102")]
    [InlineData(0x02A0, "", "")]
    [InlineData(0x02A0, "00", "00")]
    [InlineData(0x02A0, "00010203", "000102")]
    public async Task SourceBackedFanSetters_AcceptBoundedStatusAcksAndConfirmIndependentGets(
        int pid,
        string modeAckHex,
        string targetAckHex)
    {
        var session = new Session
        {
            FanModeAckPayloadOverride = Convert.FromHexString(modeAckHex),
            FanTargetAckPayloadOverride = Convert.FromHexString(targetAckHex),
        };
        await using var port = Create(pid, session);

        await port.SetFanTargetsAsync(3000, 3000, CancellationToken.None);

        Assert.Equal(1, session.Fan);
        Assert.Equal(30, session.FirstTarget);
        Assert.Equal(30, session.SecondTarget);
        RazerCommandSpec[] modeSetters = session.Writes.Where(
            command => command.ClassId == 0x0D && command.CommandId == 0x02).ToArray();
        RazerCommandSpec[] targetSetters = session.Writes.Where(
            command => command.ClassId == 0x0D && command.CommandId == 0x01).ToArray();
        Assert.Equal(2, modeSetters.Length);
        Assert.Equal(2, targetSetters.Length);
        Assert.All(modeSetters, command =>
        {
            Assert.True(command.AllowVariableResponseLength);
            Assert.NotNull(command.PayloadValidator);
            Assert.True(command.PayloadValidator(Array.Empty<byte>()));
            Assert.True(command.PayloadValidator(new byte[] { 0, 0, 0, 0 }));
            Assert.False(command.PayloadValidator(new byte[] { 0, 0, 0, 0, 0 }));
        });
        Assert.All(targetSetters, command =>
        {
            Assert.True(command.AllowVariableResponseLength);
            Assert.NotNull(command.PayloadValidator);
            Assert.True(command.PayloadValidator(Array.Empty<byte>()));
            Assert.True(command.PayloadValidator(new byte[] { 0, 0, 0 }));
            Assert.False(command.PayloadValidator(new byte[] { 0, 0, 0, 0 }));
        });
        int lastModeSetter = session.Commands.FindLastIndex(
            command => command.ClassId == 0x0D && command.CommandId == 0x02);
        Assert.Contains(session.Commands.Skip(lastModeSetter + 1),
            command => command.ClassId == 0x0D && command.CommandId == 0x82);
        Assert.Equal(2, session.FanModeSetAttempts);
        Assert.Equal(2, session.FanTargetSetAttempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Blade18FanSetters_OversizedAckPausesWithoutReplay(bool modeSetter)
    {
        var session = modeSetter
            ? new Session { FanModeAckPayloadOverride = [0, 0, 0, 0, 0] }
            : new Session { Fan = 1, FanTargetAckPayloadOverride = [0, 0, 0, 0] };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02A0));
        await using var port = Create(0x02A0, session, policy);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetFanTargetsAsync(3000, 3000, CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(1, modeSetter ? session.FanModeSetAttempts : session.FanTargetSetAttempts);
        int commandCount = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetFanTargetsAsync(3000, 3000, CancellationToken.None));
        Assert.Equal(commandCount, session.Commands.Count);
        Assert.Equal(1, modeSetter ? session.FanModeSetAttempts : session.FanTargetSetAttempts);
    }

    [Theory]
    [InlineData(true, HidProtocolValidationFailure.Status)]
    [InlineData(false, HidProtocolValidationFailure.TransactionMismatch)]
    public async Task Blade18FanSetters_StatusOrCorrelationFailurePausesWithoutReplay(
        bool modeSetter,
        HidProtocolValidationFailure protocolFailure)
    {
        var session = modeSetter
            ? new Session { FanModeAckProtocolFailure = protocolFailure }
            : new Session { Fan = 1, FanTargetAckProtocolFailure = protocolFailure };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02A0));
        await using var port = Create(0x02A0, session, policy);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetFanTargetsAsync(3000, 3000, CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(1, modeSetter ? session.FanModeSetAttempts : session.FanTargetSetAttempts);
        int commandCount = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetFanTargetsAsync(3000, 3000, CancellationToken.None));
        Assert.Equal(commandCount, session.Commands.Count);
    }

    [Fact]
    public async Task Blade18FanMode_AckWithoutApplicationPausesBeforeTargets()
    {
        var session = new Session { IgnoreFanMode = true, FanModeAckPayloadOverride = [] };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02A0));
        await using var port = Create(0x02A0, session, policy);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetFanTargetsAsync(3000, 3000, CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(2, session.FanModeSetAttempts);
        Assert.Equal(0, session.FanTargetSetAttempts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Blade18FanSetters_MalformedIndependentGetPausesWithoutReplay(bool modeSetter)
    {
        var session = modeSetter
            ? new Session { FanModeAckPayloadOverride = [], FanModeReadbackPayloadOverride = [] }
            : new Session { Fan = 1, FanTargetAckPayloadOverride = [], FanTargetReadbackPayloadOverride = [] };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02A0));
        await using var port = Create(0x02A0, session, policy);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetFanTargetsAsync(3000, 3000, CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        int commandCount = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetFanTargetsAsync(3000, 3000, CancellationToken.None));
        Assert.Equal(commandCount, session.Commands.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OtherExperimentalFanSetters_RetainExactEchoContract(bool modeSetter)
    {
        var session = modeSetter
            ? new Session { FanModeAckPayloadOverride = [] }
            : new Session { Fan = 1, FanTargetAckPayloadOverride = [] };
        var policy = new ExperimentalFeaturesPolicy(Model(0x029D));
        await using var port = Create(0x029D, session, policy);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetFanTargetsAsync(3000, 3000, CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        RazerCommandSpec setter = Assert.Single(session.Writes);
        Assert.False(setter.AllowVariableResponseLength);
        Assert.Null(setter.PayloadValidator);
        Assert.False(policy.WritesAllowed);
    }
}
