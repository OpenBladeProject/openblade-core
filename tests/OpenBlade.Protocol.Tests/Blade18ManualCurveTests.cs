using OpenBlade.Contracts;

namespace OpenBlade.Protocol.Tests;

public sealed partial class ExperimentalThermalExpansionTests
{
    [Fact]
    public async Task Blade18ManualCurve_EntersBalancedManualModeAtMaximumLinkedTarget()
    {
        var session = new Session { FirstTarget = 30, SecondTarget = 30 };
        await using var port = Create(0x02A0, session);

        await port.SetFanControlAsync(
            new FanControlSettings(FanMode.ManualCurve, 3000, ManualFanCurve.Default),
            CancellationToken.None);

        Assert.Equal(0, session.Mode);
        Assert.Equal(1, session.Fan);
        Assert.Equal(38, session.FirstTarget);
        Assert.Equal(38, session.SecondTarget);
        Assert.Equal(
            new byte[] { 0x02, 0x02, 0x01, 0x01 },
            session.Writes.Select(command => command.CommandId));
    }

    [Fact]
    public async Task Blade18ManualCurveContinuation_LinksZonesAtHigherRequestedTarget()
    {
        var session = new Session
        {
            Fan = 1,
            FirstTarget = 38,
            SecondTarget = 38,
        };
        await using var port = Create(0x02A0, session);

        await port.SetFanTargetsAsync(2600, 3400, CancellationToken.None);

        Assert.Equal(34, session.FirstTarget);
        Assert.Equal(34, session.SecondTarget);
        Assert.Equal(
            new byte[] { 0x01, 0x01 },
            session.Writes.Select(command => command.CommandId));
        Assert.All(
            session.Writes,
            command => Assert.Equal(34, command.Arguments.Span[2]));
    }

    [Fact]
    public async Task Blade18ManualCurveContinuation_StillRejectsOutOfRangeInputBeforeTraffic()
    {
        var session = new Session { Fan = 1 };
        await using var port = Create(0x02A0, session);

        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            port.SetFanTargetsAsync(2100, 3400, CancellationToken.None));

        Assert.Empty(session.Commands);
    }

    [Fact]
    public async Task Blade18ManualCurve_RejectsSilentWithoutChangingIt()
    {
        var session = new Session
        {
            Mode = 5,
            FirstTarget = 45,
            SecondTarget = 45,
        };
        await using var port = Create(0x02A0, session);

        BladeWriteException failure = await Assert.ThrowsAsync<BladeWriteException>(() =>
            port.SetFanControlAsync(
                new FanControlSettings(FanMode.ManualCurve, 4500, ManualFanCurve.Default),
                CancellationToken.None));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, failure.SubmissionState);
        Assert.Empty(session.Writes);
        Assert.Equal(5, session.Mode);
    }
}
