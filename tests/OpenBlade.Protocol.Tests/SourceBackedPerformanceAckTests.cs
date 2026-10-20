using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalThermalExpansionTests
{
    [Theory]
    [InlineData(0x029F, "")]
    [InlineData(0x029F, "00010203")]
    [InlineData(0x02A0, "")]
    [InlineData(0x02A0, "00010203")]
    public async Task SourceBackedPerformance_AcceptsBoundedAckAndConfirmsIndependentGet(
        int pid,
        string ackHex)
    {
        var session = new Session { FanModeAckPayloadOverride = Convert.FromHexString(ackHex) };
        await using var port = Create(pid, session);

        await port.SetPerformanceAsync(PerformancePreset.Silent, CancellationToken.None);

        Assert.Equal(5, session.Mode);
        RazerCommandSpec[] setters = session.Writes.Where(command => command.CommandId == 0x02).ToArray();
        Assert.Equal(2, setters.Length);
        Assert.All(setters, command =>
        {
            Assert.True(command.AllowVariableResponseLength);
            Assert.NotNull(command.PayloadValidator);
            Assert.True(command.PayloadValidator(Array.Empty<byte>()));
            Assert.True(command.PayloadValidator(new byte[] { 0, 0, 0, 0 }));
            Assert.False(command.PayloadValidator(new byte[] { 0, 0, 0, 0, 0 }));
        });
        Assert.Contains(session.Commands.Skip(session.Commands.FindLastIndex(command => command.CommandId == 0x02) + 1),
            command => command.CommandId == 0x82);
    }

    [Theory]
    [InlineData(0x029F)]
    [InlineData(0x02A0)]
    public async Task SourceBackedPerformance_OversizedAckPausesWithoutReplay(int pid)
    {
        var session = new Session { FanModeAckPayloadOverride = [0, 0, 0, 0, 0] };
        var policy = new ExperimentalFeaturesPolicy(Model(pid));
        await using var port = Create(pid, session, policy);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(PerformancePreset.Silent, CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(1, session.FanModeSetAttempts);
        int commandCount = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(PerformancePreset.Silent, CancellationToken.None));
        Assert.Equal(commandCount, session.Commands.Count);
    }

    [Theory]
    [InlineData(0x029F, HidProtocolValidationFailure.Status)]
    [InlineData(0x02A0, HidProtocolValidationFailure.TransactionMismatch)]
    public async Task SourceBackedPerformance_StatusOrCorrelationFailurePausesWithoutReplay(
        int pid,
        HidProtocolValidationFailure protocolFailure)
    {
        var session = new Session { FanModeAckProtocolFailure = protocolFailure };
        var policy = new ExperimentalFeaturesPolicy(Model(pid));
        await using var port = Create(pid, session, policy);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(PerformancePreset.Silent, CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(1, session.FanModeSetAttempts);
        int commandCount = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(PerformancePreset.Silent, CancellationToken.None));
        Assert.Equal(commandCount, session.Commands.Count);
    }

    [Theory]
    [InlineData(0x029F)]
    [InlineData(0x02A0)]
    public async Task SourceBackedPerformance_MalformedIndependentGetPausesWithoutReplay(int pid)
    {
        var session = new Session
        {
            FanModeAckPayloadOverride = [],
            FanModeReadbackPayloadOverride = [],
        };
        var policy = new ExperimentalFeaturesPolicy(Model(pid));
        await using var port = Create(pid, session, policy);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(PerformancePreset.Silent, CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(2, session.FanModeSetAttempts);
        int commandCount = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetPerformanceAsync(PerformancePreset.Silent, CancellationToken.None));
        Assert.Equal(commandCount, session.Commands.Count);
    }

    [Theory]
    [InlineData(0x029F)]
    [InlineData(0x02A0)]
    public async Task SourceBackedCustomEntry_AcceptsStatusOnlyAckAndConfirmsIndependentGet(int pid)
    {
        var session = new Session { FanModeAckPayloadOverride = [] };
        await using var port = Create(pid, session);

        await port.SetCustomPerformanceAsync(CustomPerformanceSettings.Default, CancellationToken.None);

        Assert.Equal(4, session.Mode);
        RazerCommandSpec[] setters = session.Writes.Where(command => command.CommandId == 0x02).ToArray();
        Assert.Equal(2, setters.Length);
        Assert.All(setters, command => Assert.True(command.AllowVariableResponseLength));
        Assert.Contains(session.Commands.Skip(session.Commands.FindLastIndex(command => command.CommandId == 0x02) + 1),
            command => command.CommandId == 0x82);
    }

    [Theory]
    [InlineData(0x029F, "")]
    [InlineData(0x029F, "000102")]
    [InlineData(0x02A0, "")]
    [InlineData(0x02A0, "000102")]
    public async Task SourceBackedCustomLevels_AcceptBoundedAckAndConfirmEachIndependentGet(
        int pid,
        string ackHex)
    {
        var session = new Session
        {
            Mode = 4,
            Cpu = 1,
            Gpu = 2,
            CustomLevelAckPayloadOverride = Convert.FromHexString(ackHex),
        };
        await using var port = Create(pid, session);
        var requested = new CustomPerformanceSettings(CustomPerformanceLevel.Boost, CustomPerformanceLevel.High);

        await port.SetCustomPerformanceAsync(requested, CancellationToken.None);

        RazerCommandSpec[] setters = session.Writes.Where(command => command.CommandId == 0x07).ToArray();
        Assert.NotEmpty(setters);
        Assert.All(setters, command =>
        {
            Assert.True(command.AllowVariableResponseLength);
            Assert.NotNull(command.PayloadValidator);
            Assert.True(command.PayloadValidator(Array.Empty<byte>()));
            Assert.True(command.PayloadValidator(new byte[] { 0, 0, 0 }));
            Assert.False(command.PayloadValidator(new byte[] { 0, 0, 0, 0 }));
        });
        Assert.Equal(setters.Length, session.CustomLevelSetAttempts);
        Assert.Equal(requested,
            (await port.QueryPerformanceFanStateAsync(CancellationToken.None)).CustomPerformance);
    }

    [Theory]
    [InlineData(0x029F)]
    [InlineData(0x02A0)]
    public async Task SourceBackedCustomLevels_OversizedAckPausesWithoutReplay(int pid)
    {
        var session = new Session
        {
            Mode = 4,
            Cpu = 1,
            Gpu = 2,
            CustomLevelAckPayloadOverride = [0, 0, 0, 0],
        };
        var policy = new ExperimentalFeaturesPolicy(Model(pid));
        await using var port = Create(pid, session, policy);
        var requested = new CustomPerformanceSettings(CustomPerformanceLevel.Boost, CustomPerformanceLevel.High);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetCustomPerformanceAsync(requested, CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(1, session.CustomLevelSetAttempts);
        int commandCount = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetCustomPerformanceAsync(requested, CancellationToken.None));
        Assert.Equal(commandCount, session.Commands.Count);
    }

    [Theory]
    [InlineData(0x029F, HidProtocolValidationFailure.Status)]
    [InlineData(0x02A0, HidProtocolValidationFailure.TransactionMismatch)]
    public async Task SourceBackedCustomLevels_StatusOrCorrelationFailurePausesWithoutReplay(
        int pid,
        HidProtocolValidationFailure protocolFailure)
    {
        var session = new Session
        {
            Mode = 4,
            Cpu = 1,
            Gpu = 2,
            CustomLevelAckProtocolFailure = protocolFailure,
        };
        var policy = new ExperimentalFeaturesPolicy(Model(pid));
        await using var port = Create(pid, session, policy);
        var requested = new CustomPerformanceSettings(CustomPerformanceLevel.Boost, CustomPerformanceLevel.High);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetCustomPerformanceAsync(requested, CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(1, session.CustomLevelSetAttempts);
        int commandCount = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetCustomPerformanceAsync(requested, CancellationToken.None));
        Assert.Equal(commandCount, session.Commands.Count);
    }

    [Theory]
    [InlineData(0x029F)]
    [InlineData(0x02A0)]
    public async Task SourceBackedCustomLevels_AckWithoutApplicationPausesWithoutReplay(int pid)
    {
        var session = new Session
        {
            Mode = 4,
            Cpu = 1,
            Gpu = 2,
            CustomLevelAckPayloadOverride = [],
            IgnoreCustomLevels = true,
        };
        var policy = new ExperimentalFeaturesPolicy(Model(pid));
        await using var port = Create(pid, session, policy);
        var requested = new CustomPerformanceSettings(CustomPerformanceLevel.Boost, CustomPerformanceLevel.High);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetCustomPerformanceAsync(requested, CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(1, session.CustomLevelSetAttempts);
        int commandCount = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetCustomPerformanceAsync(requested, CancellationToken.None));
        Assert.Equal(commandCount, session.Commands.Count);
    }

    [Theory]
    [InlineData(0x029F)]
    [InlineData(0x02A0)]
    public async Task SourceBackedCustomLevels_MalformedIndependentGetPausesWithoutReplay(int pid)
    {
        var session = new Session
        {
            Mode = 4,
            Cpu = 1,
            Gpu = 2,
            CustomLevelAckPayloadOverride = [],
            CustomLevelReadbackPayloadOverride = [],
        };
        var policy = new ExperimentalFeaturesPolicy(Model(pid));
        await using var port = Create(pid, session, policy);
        var requested = new CustomPerformanceSettings(CustomPerformanceLevel.Boost, CustomPerformanceLevel.High);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetCustomPerformanceAsync(requested, CancellationToken.None));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(1, session.CustomLevelSetAttempts);
        int commandCount = session.Commands.Count;
        await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetCustomPerformanceAsync(requested, CancellationToken.None));
        Assert.Equal(commandCount, session.Commands.Count);
    }

    [Fact]
    public async Task Product671HyperBoost_UsesSourceBackedModeAckAndIndependentGet()
    {
        var session = new Session { Mode = 2, FanModeAckPayloadOverride = [] };
        await using var port = HyperBoostPort(session, new Pad());

        await port.SetHyperBoostAsync(true, PerformancePreset.Performance, true, CancellationToken.None);

        Assert.Equal(7, session.Mode);
        RazerCommandSpec[] setters = session.Writes.Where(command => command.CommandId == 0x02).ToArray();
        Assert.Equal(2, setters.Length);
        Assert.All(setters, command => Assert.True(command.AllowVariableResponseLength));
        Assert.Contains(session.Commands.Skip(session.Commands.FindLastIndex(command => command.CommandId == 0x02) + 1),
            command => command.CommandId == 0x82);
    }
}
