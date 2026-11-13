using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed partial class SourceReviewedBladeModelTests
{
    [Theory]
    [MemberData(nameof(Products))]
    [MemberData(nameof(EarlyBatteryProducts))]
    public async Task Battery_NonEchoAckUsesIndependentReadback(int product)
    {
        var session = new Session { BatteryAck = [0xD0] };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy);

        await port.SetChargeLimitAsync(75, default);

        Assert.True(policy.WritesAllowed);
        Assert.Equal((byte)0xCB, session.Battery);
        Assert.Equal(new byte[] { 0x92, 0x12, 0x92 }, session.Commands.Select(command => command.CommandId));
    }

    [Fact]
    public async Task Battery_OptionalAckByteIsNotDecodedAsConfirmedState()
    {
        var session = new Session { BatteryAck = [0xFF] };
        await using var port = Create(0x02C7, session);

        await port.SetChargeLimitAsync(75, default);

        Assert.Equal((byte)0xCB, session.Battery);
        Assert.Equal(new byte[] { 0x92, 0x12, 0x92 }, session.Commands.Select(command => command.CommandId));
    }

    [Theory]
    [MemberData(nameof(Products))]
    [MemberData(nameof(EarlyBatteryProducts))]
    public async Task Battery_PreWriteReadbackConvergesWithoutSetterReplay(int product)
    {
        var session = new Session
        {
            BatteryAck = [0xD0],
            BatteryReadbacks = new([[0xD0], [0xD0], [0xD0]]),
        };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        int delays = 0;
        await using var port = Create(product, session, policy, confirmationDelay: (delay, _) =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(250), delay);
            delays++;
            return Task.CompletedTask;
        });

        await port.SetChargeLimitAsync(75, default);

        Assert.True(policy.WritesAllowed);
        Assert.Equal(2, delays);
        Assert.Single(session.Commands, command => command.CommandId == 0x12);
        Assert.Equal(4, session.Commands.Count(command => command.CommandId == 0x92));
    }

    [Theory]
    [MemberData(nameof(Products))]
    [MemberData(nameof(EarlyBatteryProducts))]
    public async Task Battery_OffConvergesToExactRetainedThreshold(int product)
    {
        var session = new Session
        {
            Battery = 0xB7,
            BatteryAck = [0xB7],
            BatteryReadbacks = new([[0xB7], [0xB7]]),
        };
        await using var port = Create(product, session);

        await port.SetProtectionOffAsync(default);

        Assert.Equal((byte)55, session.Battery);
        Assert.Equal(new byte[] { 0x92, 0x12, 0x92, 0x92 }, session.Commands.Select(command => command.CommandId));
        Assert.Equal((byte)55, session.Commands.Single(command => command.CommandId == 0x12).Arguments.Span[0]);
    }

    [Fact]
    public async Task Battery_UnchangedReadbackExhaustsBoundAndPausesAcrossPorts()
    {
        var session = new Session { BatteryAck = [0xCB], IgnoreWrites = true };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02C7));
        int delays = 0;
        await using var port = Create(0x02C7, session, policy, confirmationDelay: (_, _) =>
        {
            delays++;
            return Task.CompletedTask;
        });

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetChargeLimitAsync(75, default));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(8, delays);
        Assert.Equal(10, session.Commands.Count(command => command.CommandId == 0x92));
        Assert.Single(session.Commands, command => command.CommandId == 0x12);
        var nextSession = new Session();
        await using var nextPort = Create(0x02C7, nextSession, policy);
        await Assert.ThrowsAsync<BladeWriteException>(() => nextPort.SetProtectionOffAsync(default));
        Assert.Empty(nextSession.Commands);
    }

    [Theory]
    [InlineData(0xC6)] // Another admitted enabled limit is not the pre-write value.
    [InlineData(0x4B)] // A different disabled threshold cannot confirm retained-threshold Off.
    [InlineData(0xFF)] // Unsupported enabled state.
    public async Task Battery_UnexpectedReadbackStopsWithoutDelay(int reportedRaw)
    {
        var session = new Session { BatteryReadbacks = new([[0xD0], [(byte)reportedRaw]]) };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02C7));
        int delays = 0;
        await using var port = Create(0x02C7, session, policy, confirmationDelay: (_, _) =>
        {
            delays++;
            return Task.CompletedTask;
        });

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetProtectionOffAsync(default));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(0, delays);
        Assert.Equal(new byte[] { 0x92, 0x12, 0x92 }, session.Commands.Select(command => command.CommandId));
    }

    [Fact]
    public async Task Battery_FailedConfirmationStopsWithoutDelay()
    {
        var session = new Session();
        session.BeforeSubmission = command =>
        {
            if (command.CommandId == 0x92 && session.Commands.Any(item => item.CommandId == 0x12))
            {
                session.RejectBatteryQuery = true;
            }
        };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02C7));
        int delays = 0;
        await using var port = Create(0x02C7, session, policy, confirmationDelay: (_, _) =>
        {
            delays++;
            return Task.CompletedTask;
        });

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetChargeLimitAsync(75, default));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(0, delays);
        Assert.Equal(new byte[] { 0x92, 0x12, 0x92 }, session.Commands.Select(command => command.CommandId));
    }

    [Fact]
    public async Task Battery_CancellationDuringDelayStopsWithoutAnotherExchange()
    {
        var session = new Session { IgnoreWrites = true };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02C7));
        using var cancellation = new CancellationTokenSource();
        await using var port = Create(0x02C7, session, policy, confirmationDelay: (_, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            cancellation.Cancel();
            return Task.CompletedTask;
        });

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetChargeLimitAsync(75, cancellation.Token));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(new byte[] { 0x92, 0x12, 0x92 }, session.Commands.Select(command => command.CommandId));
    }

    [Fact]
    public async Task Battery_InvalidAckLengthPausesBeforeIndependentGet()
    {
        var session = new Session { BatteryAck = [0xCB, 0] };
        var policy = new ExperimentalFeaturesPolicy(Model(0x02C7));
        await using var port = Create(0x02C7, session, policy);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetChargeLimitAsync(75, default));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(new byte[] { 0x92, 0x12 }, session.Commands.Select(command => command.CommandId));
    }

    [Theory]
    [InlineData(0x029F)]
    [InlineData(0x029D)]
    [InlineData(0x02C5)]
    [InlineData(0x02A0)]
    public async Task Battery_LegacyNonEchoAckStillPausesBeforeIndependentGet(int product)
    {
        var session = new Session { BatteryAck = [0xD0] };
        var policy = new ExperimentalFeaturesPolicy(Model(product));
        await using var port = Create(product, session, policy);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() => port.SetChargeLimitAsync(75, default));

        Assert.Equal(WriteSubmissionState.Indeterminate, failure.SubmissionState);
        Assert.False(policy.WritesAllowed);
        Assert.Equal(0x12, session.Commands.Last().CommandId);
        Assert.Single(session.Commands, command => command.CommandId == 0x12);
        Assert.Single(session.Commands, command => command.CommandId == 0x92);
    }
}
