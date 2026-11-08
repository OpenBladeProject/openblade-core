using OpenBlade.Contracts;
using OpenBlade.FanStop;
using OpenBlade.FanStop.Guardian;

namespace OpenBlade.Service.Tests;

public sealed class FanStopGuardianProgressTests
{
    [Fact]
    public void PartialEntryKeepsItsActualRefusalThroughAutomaticCleanup()
    {
        var progress = new So6120ServiceStopProgress();
        progress.RecordEntryFailure(So6120DutyFailure.NotSubmitted, "stage=EntryThermal; cpuC=74; cpuEntryLimitC=65");

        Assert.False(progress.StopConfirmed);
        Assert.Contains("NotSubmitted", progress.ReturningReason);
        Assert.Contains("cpuC=74; cpuEntryLimitC=65", progress.ReturningReason);
        Assert.Equal(progress.ReturningReason, progress.IncompleteReason);
    }

    [Theory]
    [InlineData((int)So6120StopCoastResult.AutomaticRequired)]
    [InlineData((int)So6120StopCoastResult.Unresolved)]
    public void ZeroPwmWithoutConfirmedCoastDoesNotEstablishStoppedFans(int outcome)
    {
        var coast = (So6120StopCoastResult)outcome;
        var progress = new So6120ServiceStopProgress();
        progress.RecordCoast(coast, FanStopEndReason.SafetyCondition);

        Assert.False(progress.StopConfirmed);
        Assert.Contains(coast.ToString(), progress.ReturningReason);
        Assert.Contains("SafetyCondition", progress.ReturningReason);
    }

    [Fact]
    public void ConfirmedStopHistorySurvivesLaterCoastOrSafetyLoss()
    {
        var progress = new So6120ServiceStopProgress();
        progress.RecordCoast(So6120StopCoastResult.Stopped, FanStopEndReason.None);
        progress.RecordCoast(So6120StopCoastResult.AutomaticRequired, FanStopEndReason.SafetyCondition);

        Assert.True(progress.StopConfirmed);
        Assert.Null(progress.IncompleteReason);
    }

    [Fact]
    public void IncompleteEntryKeepsFirstBoundedCause()
    {
        var progress = new So6120ServiceStopProgress();
        progress.RecordEntryFailure(So6120DutyFailure.PwmNotObserved, new string('x', 2000));
        string first = progress.IncompleteReason!;
        progress.RecordEntryFailure(So6120DutyFailure.NotSubmitted, "later failure");

        Assert.Equal(900, first.Length);
        Assert.Contains("PwmNotObserved", first);
        Assert.Equal(first, progress.IncompleteReason);
    }
}
