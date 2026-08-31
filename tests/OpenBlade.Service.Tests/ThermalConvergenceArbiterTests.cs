using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed class ThermalConvergenceArbiterTests
{
    [Fact]
    public void ClosedAdmissionRejectsCandidates()
    {
        using var arbiter = new ThermalConvergenceArbiter();
        long generation = arbiter.CurrentAdmissionGeneration;

        Assert.Equal(
            ThermalConvergenceNominationResult.AdmissionClosed,
            arbiter.TryNominate(Candidate(generation)));
    }

    [Fact]
    public async Task RepeatedNormalizedClassExecutesOnce()
    {
        using var arbiter = new ThermalConvergenceArbiter();
        long generation = arbiter.OpenAdmission();
        var executed = new List<ThermalConvergenceCandidate>();
        using var cancellation = new CancellationTokenSource();

        Assert.Equal(
            ThermalConvergenceNominationResult.Accepted,
            arbiter.TryNominate(Candidate(generation)));
        Assert.Equal(
            ThermalConvergenceNominationResult.Merged,
            arbiter.TryNominate(Candidate(generation)));

        Task run = arbiter.RunAsync(
            (candidate, _) =>
            {
                executed.Add(candidate);
                cancellation.Cancel();
                return Task.CompletedTask;
            },
            cancellation.Token);
        await run;

        Assert.Single(executed);
    }

    [Fact]
    public void EnsureAdmissionPreservesOpenGenerationAndPendingCandidate()
    {
        using var arbiter = new ThermalConvergenceArbiter();
        long generation = arbiter.EnsureAdmissionOpen();
        Assert.Equal(
            ThermalConvergenceNominationResult.Accepted,
            arbiter.TryNominate(Candidate(generation)));

        long preserved = arbiter.EnsureAdmissionOpen();

        Assert.Equal(generation, preserved);
        Assert.Equal(
            ThermalConvergenceNominationResult.Merged,
            arbiter.TryNominate(Candidate(preserved)));
        long closed = arbiter.CloseAdmission();
        long reopened = arbiter.EnsureAdmissionOpen();
        Assert.NotEqual(closed, reopened);
    }

    [Fact]
    public async Task PendingSlotKeepsOnlyNewestDistinctCandidate()
    {
        using var arbiter = new ThermalConvergenceArbiter();
        long generation = arbiter.OpenAdmission();
        ThermalConvergenceCandidate first = Candidate(generation);
        ThermalConvergenceCandidate replacement = first with
        {
            Destination = PowerSource.Battery,
        };
        var executed = new List<ThermalConvergenceCandidate>();
        using var cancellation = new CancellationTokenSource();

        Assert.Equal(
            ThermalConvergenceNominationResult.Accepted,
            arbiter.TryNominate(first));
        Assert.Equal(
            ThermalConvergenceNominationResult.Accepted,
            arbiter.TryNominate(replacement));

        Task run = arbiter.RunAsync(
            (candidate, _) =>
            {
                executed.Add(candidate);
                cancellation.Cancel();
                return Task.CompletedTask;
            },
            cancellation.Token);
        await run;

        Assert.Equal(replacement, Assert.Single(executed));
    }

    [Fact]
    public void ClosingAdmissionDropsQueuedWorkAndInvalidatesGeneration()
    {
        using var arbiter = new ThermalConvergenceArbiter();
        long generation = arbiter.OpenAdmission();
        Assert.Equal(
            ThermalConvergenceNominationResult.Accepted,
            arbiter.TryNominate(Candidate(generation)));

        long closedGeneration = arbiter.CloseAdmission();

        Assert.NotEqual(generation, closedGeneration);
        Assert.Equal(
            ThermalConvergenceNominationResult.AdmissionClosed,
            arbiter.TryNominate(Candidate(generation)));
    }

    [Fact]
    public void ComponentRevisionOnlyAffectsItsContradictionClass()
    {
        ThermalConvergenceCandidate fan = Candidate(1) with
        {
            Contradiction = ThermalConvergenceContradiction.FanTopology,
        };

        Assert.True(fan.SameNormalizedClass(fan with
        {
            PerformanceDesiredRevision = "different-performance",
        }));
        Assert.False(fan.SameNormalizedClass(fan with
        {
            FanDesiredRevision = "different-fans",
        }));
    }

    private static ThermalConvergenceCandidate Candidate(long generation) => new(
        ActiveUserSid: "S-1-5-21-1-2-3-1001",
        AdmissionGeneration: generation,
        Destination: PowerSource.PluggedIn,
        DeviceAuthorityEpoch: "device-epoch-1",
        PerformanceDesiredRevision: "performance-1",
        FanDesiredRevision: "fans-1",
        Contradiction: ThermalConvergenceContradiction.Performance);
}
