using OpenBlade.Contracts;

namespace OpenBlade.Capture.Tests;

public sealed class So6120OwnedStopSessionTests
{
    private static So6120DirectDutySession Create(So6120DirectDutySessionTests.Fixture fixture,
        Func<bool>? entryAllowed = null, Func<bool>? continueAllowed = null) =>
        So6120DirectDutySession.CreateForOwnedStop(fixture.Root, fixture.Binding, fixture.Port,
            fixture.Source, fixture.Clock, So6120SupervisedSessionAdmissionPolicy.Instance,
            new(entryAllowed ?? (() => true), entryAllowed ?? (() => true), continueAllowed ?? (() => true)));

    [Fact]
    public void DirectZeroEntryPersistsThroughOsOnlyMonitoringThenRestoresCapturedFields()
    {
        using var fixture = new So6120DirectDutySessionTests.Fixture(PerformancePreset.Balanced, 0, zeroDuty: true);
        using (var session = Create(fixture))
        {
            var entered = session.EnterStopped();
            Assert.True(entered.Success);
            Assert.Equal(new (byte, byte)[] { (0xB8, 0), (0xB9, 0), (0xF0, 0x51), (0x1A, 0xFE) }, fixture.Port.Writes);
            Assert.Equal(0, entered.Observations[^1].Snapshot.Actual1);
            Assert.Equal(0, entered.Observations[^1].Snapshot.Actual2);
            int calls = fixture.Port.Calls;
            long journalBytes = new FileInfo(Path.Combine(fixture.Root, So6120DirectDutyJournal.FileName)).Length;
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            for (int index = 0; index < 100; index++) { Assert.True(session.ContinueStopAdmitted()); }
            Assert.Equal(calls, fixture.Port.Calls);
            Assert.Equal(journalBytes, new FileInfo(Path.Combine(fixture.Root, So6120DirectDutyJournal.FileName)).Length);
            Assert.Equal(So6120DutyIoDisposition.Confirmed, session.VerifyOwnedStop().Disposition);

            var automatic = session.ReturnToAutomatic();
            Assert.True(automatic.Success);
            Assert.True(automatic.CleanupConfirmed);
            Assert.Equal(0, fixture.Port.Mapped[0x1A]);
            Assert.Equal(0x11, fixture.Port.Mapped[0xF0]);
            Assert.Equal(10, fixture.Port.Mapped[0xB8]);
            Assert.Equal(20, fixture.Port.Mapped[0xB9]);
            Assert.False(session.ContinueStopAdmitted());
        }
        Assert.True(So6120DirectDutyJournal.Inspect(fixture.Root).Complete);
    }

    [Fact]
    public void UnsafeInitialEntryPerformsNoPortOperation()
    {
        using var fixture = new So6120DirectDutySessionTests.Fixture(PerformancePreset.Balanced, 0, zeroDuty: true);
        using var session = Create(fixture, () => false);
        var result = session.EnterStopped();
        Assert.Equal(So6120DutyFailure.AdmissionRefused, result.Failure);
        Assert.False(result.CommandsFenced);
        Assert.Equal(0, fixture.Port.Calls);
        Assert.True(session.CommandEvidence.CleanNoMutation);
    }

    [Fact]
    public void AutoBeforeEntryHasPositiveNoCommandEvidence()
    {
        using var fixture = new So6120DirectDutySessionTests.Fixture(PerformancePreset.Balanced, 0, zeroDuty: true);
        using var session = Create(fixture);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.Throws<OperationCanceledException>(() => session.EnterStopped(stop.Token));
        Assert.True(session.CommandEvidence.CleanNoMutation);
        Assert.Equal(0, session.CommandEvidence.CommandsCompleted);
        Assert.Empty(fixture.Port.Publications);
    }

    [Fact]
    public void CompletedBaselineReadsBeforeEntryRefusalAreNotNativeAmbiguity()
    {
        using var fixture = new So6120DirectDutySessionTests.Fixture(PerformancePreset.Balanced, 0, zeroDuty: true);
        using var session = Create(fixture, () => fixture.Port.Publications.Count < 13);
        Assert.False(session.EnterStopped().Success);
        Assert.Empty(fixture.Port.Writes);
        Assert.True(session.CommandEvidence.CommandsCompleted > 0);
        Assert.True(session.CommandEvidence.CleanNoMutation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StorageFailureBeforeFirstCommandDoesNotInventNativeAmbiguity(bool duringCreation)
    {
        using var fixture = new So6120DirectDutySessionTests.Fixture(PerformancePreset.Balanced, 0, zeroDuty: true);
        bool armed = duringCreation;
        So6120DirectDutySession Make() => So6120DirectDutySession.CreateForOwnedStop(fixture.Root,
            fixture.Binding, fixture.Port, fixture.Source, fixture.Clock, So6120SupervisedSessionAdmissionPolicy.Instance,
            new(() => true, () => true, () => true), storageFault: stage =>
            {
                if (armed && stage == So6120JournalWriteStage.BeforeFlush) { throw new IOException("Synthetic storage failure"); }
            });
        if (duringCreation)
        {
            Assert.Throws<IOException>(Make);
            Assert.True(fixture.Port.Disposed);
        }
        else
        {
            using var session = Make();
            armed = true;
            Assert.False(session.EnterStopped().Success);
            Assert.True(session.CommandEvidence.CleanNoMutation);
        }
        Assert.Empty(fixture.Port.Publications);
        Assert.Empty(fixture.Port.Writes);
    }

    [Theory]
    [InlineData(1, 0, 0, false, false)]
    [InlineData(1, 1, 1, false, false)]
    [InlineData(0, 0, 0, true, false)]
    [InlineData(0, 0, 0, false, true)]
    public void PendingOrMutatingOrAmbiguousCommandsNeverProveCleanRefusal(int possible, int completed,
        int mutations, bool ambiguous, bool inFlight) =>
        Assert.False(new So6120SessionCommandEvidence(possible, completed, mutations, ambiguous, inFlight).CleanNoMutation);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CoastIsBoundedAndResolvedNonzeroTachCanReturnToAuto(bool eventuallyStops)
    {
        using var fixture = new So6120DirectDutySessionTests.Fixture(PerformancePreset.Balanced, 0, zeroDuty: true);
        fixture.Port.Mapped[0x08] = fixture.Port.Mapped[0x09] = 20;
        using var session = Create(fixture);
        Assert.True(session.EnterStopped().Success);
        int before = fixture.Port.Publications.Count, waits = 0;
        var result = session.ObserveStoppedCoast(wait: (_, _) =>
        {
            waits++;
            if (eventuallyStops && waits == 2) { fixture.Port.Mapped[0x08] = fixture.Port.Mapped[0x09] = 0; }
        });
        Assert.Equal(eventuallyStops ? So6120StopCoastResult.Stopped : So6120StopCoastResult.AutomaticRequired, result);
        Assert.Equal(eventuallyStops ? 2 : 4, waits);
        Assert.Equal(waits * 13, fixture.Port.Publications.Count - before);
        Assert.True(session.ReturnToAutomatic().Success);
    }

    [Fact]
    public void AmbiguousCoastObservationDoesNotAttemptAnotherCommand()
    {
        using var fixture = new So6120DirectDutySessionTests.Fixture(PerformancePreset.Balanced, 0, zeroDuty: true);
        using var session = Create(fixture);
        Assert.True(session.EnterStopped().Success);
        fixture.Port.FailNextReadUnperformed = true;
        Assert.Equal(So6120StopCoastResult.Unresolved, session.ObserveStoppedCoast(wait: (_, _) => { }));
        int calls = fixture.Port.Calls;
        Assert.Throws<InvalidOperationException>(() => session.ReturnToAutomatic());
        Assert.Equal(calls, fixture.Port.Calls);
    }

    [Fact]
    public void EntrySafetyLossPreservesConfirmedPrefixForIndependentAuto()
    {
        using var fixture = new So6120DirectDutySessionTests.Fixture(PerformancePreset.Balanced, 0, zeroDuty: true);
        using var session = Create(fixture, () => fixture.Port.Writes.Count == 0);
        var result = session.EnterStopped();
        Assert.False(result.Success);
        Assert.False(result.CommandsFenced);
        Assert.Single(fixture.Port.Writes);
        Assert.True(session.ReturnToAutomatic().Success);
        Assert.Equal(new (byte, byte)[] { (0xB8, 0), (0xB8, 10) }, fixture.Port.Writes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void CancellationAfterPublicationCompletesCheckpointBeforeAuto(int writeNumber)
    {
        using var fixture = new So6120DirectDutySessionTests.Fixture(PerformancePreset.Balanced, 0, zeroDuty: true);
        using var cancellation = new CancellationTokenSource();
        fixture.Port.AfterWrite = (_, _) =>
        {
            if (fixture.Port.Writes.Count == writeNumber) { cancellation.Cancel(); }
        };
        using var session = Create(fixture);
        var result = session.EnterStopped(cancellation.Token);
        Assert.False(result.CommandsFenced);
        Assert.NotNull(result.CleanupCheckpoint);
        Assert.Equal(writeNumber, fixture.Port.Writes.Count);
        Assert.True(session.ReturnToAutomatic().Success);
        Assert.Equal(0, fixture.Port.Mapped[0x1A]);
        Assert.Equal(0x11, fixture.Port.Mapped[0xF0]);
    }

    [Fact]
    public void AmbiguousEntryFencesAutoWithoutAnotherCommand()
    {
        using var fixture = new So6120DirectDutySessionTests.Fixture(PerformancePreset.Balanced, 0, zeroDuty: true);
        fixture.Port.AmbiguousFirstWrite = true;
        using var session = Create(fixture);
        Assert.True(session.EnterStopped().CommandsFenced);
        Assert.False(session.CommandEvidence.CleanNoMutation);
        int calls = fixture.Port.Calls;
        Assert.Throws<InvalidOperationException>(() => session.ReturnToAutomatic());
        Assert.Equal(calls, fixture.Port.Calls);
    }

    [Fact]
    public void OccasionalVerificationReadsOneSnapshotAndKeepsOwnership()
    {
        using var fixture = new So6120DirectDutySessionTests.Fixture(PerformancePreset.Balanced, 0, zeroDuty: true);
        bool continuing = true;
        using var session = Create(fixture, continueAllowed: () => continuing);
        Assert.True(session.EnterStopped().Success);
        int publications = fixture.Port.Publications.Count;
        int writes = fixture.Port.Writes.Count;
        var verified = session.VerifyOwnedStop();
        Assert.Equal(So6120DutyIoDisposition.Confirmed, verified.Disposition);
        Assert.Equal(13, fixture.Port.Publications.Count - publications);
        Assert.All(fixture.Port.Publications.Skip(publications), publication => Assert.Equal(0xA0, publication.Command));
        Assert.Equal(writes, fixture.Port.Writes.Count);
        continuing = false;
        Assert.False(session.ContinueStopAdmitted());
        Assert.True(session.ReturnToAutomatic().Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedOrIncompleteVerificationFencesWithoutAuto(bool nativeReadFails)
    {
        using var fixture = new So6120DirectDutySessionTests.Fixture(PerformancePreset.Balanced, 0, zeroDuty: true);
        using var session = Create(fixture);
        Assert.True(session.EnterStopped().Success);
        if (nativeReadFails) { fixture.Port.FailNextReadUnperformed = true; }
        else { fixture.Port.Mapped[0xB0] = 1; }
        Assert.Equal(So6120DutyIoDisposition.Unresolved, session.VerifyOwnedStop().Disposition);
        int calls = fixture.Port.Calls;
        Assert.Throws<InvalidOperationException>(() => session.ReturnToAutomatic());
        Assert.Equal(calls, fixture.Port.Calls);
    }
}
