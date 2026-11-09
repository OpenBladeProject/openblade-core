using OpenBlade.Contracts;
using OpenBlade.FanStop;
using OpenBlade.FanStop.Guardian;

namespace OpenBlade.Service.Tests;

public sealed class FanStopGuardianProgressTests
{
    [Fact]
    public void InitialAndPartialEntryRetainActualThermalDecisionInInvariantFormat()
    {
        var policy = new FanStopThermalPolicy(FanStopSettings.Default with { EndMode = FanStopEndMode.Custom }, TimeProvider.System);
        Assert.False(policy.AdmitsEntry(70, 71, false, false, TimeSpan.Zero));
        string refusal = So6120ServiceInitialAdmissionDiagnostics.ThermalRefusal(
            FanStopSettings.Default with { EndMode = FanStopEndMode.Custom },
            new(70, 71, false), TimeSpan.Zero, false, policy.EntryThermalDecision);
        var progress = new So6120ServiceStopProgress();
        progress.RecordEntryFailure(So6120DutyFailure.NotSubmitted, refusal);
        Assert.Contains("thermalRule=ImmediateCutoff; cpuC=70; cpuLimitC=65; gpuC=71; gpuLimitC=65", refusal);
        Assert.Contains("cpuEntryLimitC=65; gpuEntryLimitC=65", refusal);
        Assert.Contains(refusal, progress.CompletionReason!);
        Assert.True(refusal.Length < 640);
        Assert.Equal(FanStopEndReason.None, policy.EndReason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThermalExitSurvivesCoastOrConfirmedStopAndCleanup(bool coastCompleted)
    {
        var progress = new So6120ServiceStopProgress();
        var decision = new FanStopThermalDecision("RollingAverage", new(65.5m, 65), new(66, 65),
            TimeSpan.FromSeconds(5).Ticks, TimeSpan.FromSeconds(5).Ticks);
        progress.RecordCoast(coastCompleted ? So6120StopCoastResult.Stopped : So6120StopCoastResult.AutomaticRequired,
            coastCompleted ? FanStopEndReason.None : FanStopEndReason.ThermalThreshold,
            coastCompleted ? null : decision);
        progress.RecordThermalExit(FanStopEndReason.ThermalThreshold, decision);
        string first = progress.ReturningReason;
        progress.RecordThermalExit(FanStopEndReason.SafetyCondition, null);
        progress.RecordThermalExit(FanStopEndReason.ThermalThreshold, new("ImmediateCutoff", new(90, 75), null));

        Assert.Equal(coastCompleted, progress.StopConfirmed);
        Assert.Equal(first, progress.CompletionReason);
        Assert.Contains("cpuC=65.5; cpuLimitC=65", first);
        Assert.Contains("gpuC=66; gpuLimitC=65", first);
        Assert.Contains("coverageSeconds=5", first);
        Assert.DoesNotContain("ImmediateCutoff", first);
        Assert.True(first.Length < 900);
    }

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
