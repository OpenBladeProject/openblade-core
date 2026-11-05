using OpenBlade.Capture;
using OpenBlade.Contracts;

namespace OpenBlade.Capture.Tests;

public sealed class DirectDutyUnsubmittedReconciliationTests
{
    [Fact]
    public async Task ZeroWriteFailureNeedsExplicitReconciliationBeforeOneFreshAttempt()
    {
        var port = new SimulatedPort { FailFirstRead = true };
        var initial = await new DirectDutyAutomaticExit(port).RunAsync(port.Ownership);
        var previous = port.LastExit!;
        var refused = await new DirectDutyAutomaticExit(port).RunAsync(port.Ownership);

        Assert.Equal(DirectDutyExitFailure.ObservationRejected, initial.Failure);
        Assert.Equal(0, initial.SubmissionAttempts);
        Assert.Equal(DirectDutyExitFailure.AdmissionRefused, refused.Failure);
        Assert.Empty(port.Submitted);
        Assert.False(previous.IsFenced);

        var reconciled = await new DirectDutyUnsubmittedReconciliation(port).RunAsync(port.Ownership);

        Assert.True(reconciled.Success);
        Assert.True(reconciled.FreshAttemptAuthorized);
        Assert.Empty(port.Submitted);
        Assert.True(previous.IsFenced);

        var fresh = await new DirectDutyAutomaticExit(port).RunAsync(port.Ownership);
        var exhausted = await new DirectDutyAutomaticExit(port).RunAsync(port.Ownership);

        Assert.True(fresh.Success);
        Assert.NotEqual(previous.AttemptId, port.LastExit!.AttemptId);
        Assert.Equal(DirectDutyExitFailure.AdmissionRefused, exhausted.Failure);
        Assert.Equal(new[] { DirectDutyExitStep.AutomaticFirst, DirectDutyExitStep.AutomaticSecond }, port.Submitted);
    }

    [Fact]
    public async Task PossiblySubmittedWriteBlocksReconciliationEvenWhenOwnedValuesStillMatch()
    {
        var port = new SimulatedPort { LoseFirstSubmissionOutcome = true };
        var initial = await new DirectDutyAutomaticExit(port).RunAsync(port.Ownership);
        var reconciled = await new DirectDutyUnsubmittedReconciliation(port).RunAsync(port.Ownership);
        var retried = await new DirectDutyAutomaticExit(port).RunAsync(port.Ownership);

        Assert.Equal(DirectDutyExitFailure.Uncertain, initial.Failure);
        Assert.Equal(DirectDutyReconciliationFailure.EvidenceRejected, reconciled.Failure);
        Assert.False(reconciled.FreshAttemptAuthorized);
        Assert.Equal(0, port.AuthorizationChecks);
        Assert.Equal(DirectDutyExitFailure.AdmissionRefused, retried.Failure);
        Assert.Equal(DirectDutyExitStep.AutomaticFirst, Assert.Single(port.Submitted));
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("unreleased")]
    [InlineData("attempt")]
    [InlineData("generation")]
    [InlineData("owner")]
    [InlineData("unknown")]
    [InlineData("submitted")]
    public async Task IncompleteOrUnsafeJournalEvidenceCannotReachAuthorization(string defect)
    {
        var port = new SimulatedPort
        {
            FailFirstRead = true,
            ChangeSnapshot = snapshot => defect switch
            {
                "incomplete" => snapshot with { JournalComplete = false },
                "unreleased" => snapshot with { PreviousSessionReleased = false },
                "attempt" => snapshot with { PreviousAttemptId = Guid.Empty },
                "generation" => snapshot with { Generation = 0 },
                "owner" => snapshot with { Owner = snapshot.Owner with { OperationId = Guid.NewGuid() } },
                "unknown" => snapshot with { Submission = DirectDutySubmissionEvidence.Unknown },
                "submitted" => snapshot with { Submission = DirectDutySubmissionEvidence.Submitted },
                _ => throw new ArgumentOutOfRangeException(nameof(defect)),
            },
        };
        await new DirectDutyAutomaticExit(port).RunAsync(port.Ownership);

        var result = await new DirectDutyUnsubmittedReconciliation(port).RunAsync(port.Ownership);

        Assert.Equal(DirectDutyReconciliationFailure.EvidenceRejected, result.Failure);
        Assert.False(result.FreshAttemptAuthorized);
        Assert.Equal(0, port.AuthorizationChecks);
        Assert.Empty(port.Submitted);
    }

    [Theory]
    [InlineData("cached", false)]
    [InlineData("owner", false)]
    [InlineData("unsettled", false)]
    [InlineData("inactive", false)]
    [InlineData("out-of-order", false)]
    [InlineData("ec-drift", true)]
    [InlineData("access-drift", true)]
    public async Task ReconciliationRequiresFreshOrderedProofOfTheStillOwnedState(string defect, bool stateChanged)
    {
        var port = new SimulatedPort
        {
            FailFirstRead = true,
            ChangeSnapshot = snapshot => defect switch
            {
                "cached" => snapshot with { Ec = snapshot.Ec with { Independent = false } },
                "owner" => snapshot with { Access = snapshot.Access with { Owner = snapshot.Owner with { ControllerSession = Guid.NewGuid() } } },
                "unsettled" => snapshot with { Ec = snapshot.Ec with { ExclusiveAndSettled = false } },
                "inactive" => snapshot with { Access = snapshot.Access with { Active = false } },
                "out-of-order" => snapshot with { Access = snapshot.Access with { Sequence = snapshot.Ec.Sequence } },
                "ec-drift" => snapshot with { Ec = snapshot.Ec with { State = snapshot.Ec.State with { Duty1 = 7 } } },
                "access-drift" => snapshot with { Access = snapshot.Access with { State = new(1, true) } },
                _ => throw new ArgumentOutOfRangeException(nameof(defect)),
            },
        };
        await new DirectDutyAutomaticExit(port).RunAsync(port.Ownership);

        var result = await new DirectDutyUnsubmittedReconciliation(port).RunAsync(port.Ownership);

        Assert.Equal(stateChanged ? DirectDutyReconciliationFailure.StateChanged : DirectDutyReconciliationFailure.ObservationRejected, result.Failure);
        Assert.False(result.FreshAttemptAuthorized);
        Assert.Equal(0, port.AuthorizationChecks);
        Assert.Empty(port.Submitted);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("attempt")]
    [InlineData("raced")]
    public async Task StaleJournalSnapshotCannotAuthorizeANewAttempt(string defect)
    {
        var port = new SimulatedPort
        {
            FailFirstRead = true,
            AdvanceGenerationAtAuthorization = defect == "raced",
            ChangeSnapshot = snapshot => defect switch
            {
                "generation" => snapshot with { Generation = snapshot.Generation + 1 },
                "attempt" => snapshot with { PreviousAttemptId = Guid.NewGuid() },
                _ => snapshot,
            },
        };
        await new DirectDutyAutomaticExit(port).RunAsync(port.Ownership);

        var result = await new DirectDutyUnsubmittedReconciliation(port).RunAsync(port.Ownership);

        Assert.Equal(DirectDutyReconciliationFailure.AdmissionRefused, result.Failure);
        Assert.False(result.FreshAttemptAuthorized);
        Assert.Equal(1, port.AuthorizationChecks);
        Assert.Empty(port.Submitted);
        Assert.Equal(DirectDutyExitFailure.AdmissionRefused,
            (await new DirectDutyAutomaticExit(port).RunAsync(port.Ownership)).Failure);
    }

    [Fact]
    public async Task AuthorizationJournalFailureRemainsUncertainAndIsNeverRetried()
    {
        var port = new SimulatedPort { FailFirstRead = true, LoseAuthorizationOutcome = true };
        await new DirectDutyAutomaticExit(port).RunAsync(port.Ownership);

        var result = await new DirectDutyUnsubmittedReconciliation(port).RunAsync(port.Ownership);

        Assert.Equal(DirectDutyReconciliationFailure.JournalUncertain, result.Failure);
        Assert.False(result.FreshAttemptAuthorized);
        Assert.Equal(1, port.AuthorizationChecks);
        Assert.Empty(port.Submitted);
    }

    [Fact]
    public async Task UnconfirmedReconciliationReleaseBlocksAnOtherwiseAuthorizedAttempt()
    {
        var port = new SimulatedPort { FailFirstRead = true, FailReconciliationRelease = true };
        await new DirectDutyAutomaticExit(port).RunAsync(port.Ownership);

        var result = await new DirectDutyUnsubmittedReconciliation(port).RunAsync(port.Ownership);
        var retried = await new DirectDutyAutomaticExit(port).RunAsync(port.Ownership);

        Assert.Equal(DirectDutyReconciliationFailure.None, result.Failure);
        Assert.True(result.FreshAttemptAuthorized);
        Assert.True(result.SessionReleaseFailed);
        Assert.False(result.Success);
        Assert.Equal(DirectDutyExitFailure.AdmissionRefused, retried.Failure);
        Assert.Empty(port.Submitted);
    }

    [Fact]
    public async Task UnconfirmedExitReleaseBlocksReconciliation()
    {
        var port = new SimulatedPort { FailFirstRead = true, FailExitRelease = true };
        var initial = await new DirectDutyAutomaticExit(port).RunAsync(port.Ownership);

        var result = await new DirectDutyUnsubmittedReconciliation(port).RunAsync(port.Ownership);

        Assert.True(initial.SessionReleaseFailed);
        Assert.Equal(DirectDutyReconciliationFailure.AdmissionRefused, result.Failure);
        Assert.False(result.FreshAttemptAuthorized);
        Assert.Equal(0, port.AuthorizationChecks);
        Assert.Empty(port.Submitted);
    }

    // This in-memory ledger models attempt fencing and release across coordinators.
    // Its synchronous operations do not exercise persistence or hardware.
    private sealed class SimulatedPort : IDirectDutyAutoExitPort, IDirectDutyReconciliationPort, IDirectDutyReconciliationSession
    {
        private sealed record Attempt(Guid Id, long Generation, bool Released, DirectDutySubmissionEvidence Evidence);

        private Attempt? journal;
        private long generation;
        private long? permittedGeneration;
        private long sequence;
        private bool reconciling;
        private DirectDutyEcState ec = new(true, null, 0, 0x40, 0, 0);
        private static readonly DirectDutyAccessState Access = new(0, true);

        public DirectDutyOwnership Ownership { get; } = new(
            new(Guid.NewGuid(), "fixture-device", "fixture-reviewed-image", Guid.NewGuid()),
            new(PerformancePreset.Silent), 0x40, 0, 0, 0, 0, 0, 0, 0, true);
        public bool FailFirstRead { get; init; }
        public bool LoseFirstSubmissionOutcome { get; init; }
        public bool LoseAuthorizationOutcome { get; init; }
        public bool AdvanceGenerationAtAuthorization { get; init; }
        public bool FailExitRelease { get; init; }
        public bool FailReconciliationRelease { get; init; }
        public Func<DirectDutyReconciliationSnapshot, DirectDutyReconciliationSnapshot>? ChangeSnapshot { get; init; }
        public ExitSession? LastExit { get; private set; }
        public int AuthorizationChecks { get; private set; }
        public List<DirectDutyExitStep> Submitted { get; } = [];

        public Task<IDirectDutyAutoExitSession?> AcquireOwnedSessionAsync(DirectDutyOwnership ownership,
            DirectDutyAutoDestination destination, CancellationToken cancellationToken)
        {
            if (ownership != Ownership || reconciling || (journal is not null && permittedGeneration != generation))
            {
                return Task.FromResult<IDirectDutyAutoExitSession?>(null);
            }

            permittedGeneration = null;
            journal = new(Guid.NewGuid(), ++generation, false, DirectDutySubmissionEvidence.DefinitelyUnsubmitted);
            LastExit = new(this, journal.Id, journal.Generation);
            return Task.FromResult<IDirectDutyAutoExitSession?>(LastExit);
        }

        public Task<IDirectDutyReconciliationSession?> AcquireReconciliationSessionAsync(DirectDutyOwnership ownership,
            CancellationToken cancellationToken)
        {
            if (ownership != Ownership || reconciling || journal is not { Released: true } || journal.Generation != generation)
            {
                return Task.FromResult<IDirectDutyReconciliationSession?>(null);
            }

            reconciling = true;
            return Task.FromResult<IDirectDutyReconciliationSession?>(this);
        }

        public Task<DirectDutyReconciliationSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
        {
            Attempt prior = journal!;
            var snapshot = new DirectDutyReconciliationSnapshot(Ownership.Owner, prior.Id, prior.Generation, true,
                prior.Released, prior.Evidence, Proof(ec), Proof(Access));
            return Task.FromResult(ChangeSnapshot?.Invoke(snapshot) ?? snapshot);
        }

        public Task<bool> AuthorizeFreshAttemptAsync(DirectDutyReconciliationSnapshot snapshot, CancellationToken cancellationToken)
        {
            AuthorizationChecks++;
            if (LoseAuthorizationOutcome) { throw new IOException("Lost authorization outcome"); }
            if (AdvanceGenerationAtAuthorization) { generation++; }
            if (!reconciling || journal is not { Released: true, Evidence: DirectDutySubmissionEvidence.DefinitelyUnsubmitted } prior
                || snapshot.Owner != Ownership.Owner || prior.Id != snapshot.PreviousAttemptId
                || prior.Generation != snapshot.Generation || generation != snapshot.Generation
                || snapshot.Ec.State != ec || snapshot.Access.State != Access)
            {
                return Task.FromResult(false);
            }

            permittedGeneration = ++generation;
            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync()
        {
            if (FailReconciliationRelease) { throw new IOException("Lost reconciliation release"); }
            reconciling = false;
            return ValueTask.CompletedTask;
        }

        private DirectDutyProof<T> Proof<T>(T state) => new(Ownership.Owner, ++sequence, true, true, true, state);

        public sealed class ExitSession(SimulatedPort port, Guid attemptId, long attemptGeneration) : IDirectDutyAutoExitSession
        {
            private DirectDutyExitMutation? intent;

            public Guid AttemptId => attemptId;
            public bool SupportsBankPreservingGateClose => false;
            public bool IsFenced => port.generation != attemptGeneration;
            private bool IsCurrent => !IsFenced
                && port.journal is { Released: false } current && current.Id == attemptId;

            public Task<DirectDutyProof<DirectDutyEcState>> ReadEcAsync(CancellationToken cancellationToken)
            {
                if (port.FailFirstRead && attemptGeneration == 1) { throw new IOException("Initial read failed"); }
                return Task.FromResult(port.Proof(port.ec));
            }

            public Task<DirectDutyProof<DirectDutyAccessState>> ReadAccessAsync(CancellationToken cancellationToken) =>
                Task.FromResult(port.Proof(Access));

            public Task RecordIntentAsync(DirectDutyExitMutation mutation, DirectDutyProof<DirectDutyEcState> expectedEc,
                DirectDutyProof<DirectDutyAccessState> expectedAccess, CancellationToken cancellationToken)
            {
                if (!IsCurrent) { throw new IOException("Stale attempt"); }
                intent = mutation;
                return Task.CompletedTask;
            }

            public Task<bool> AdmitSubmissionAsync(DirectDutyExitMutation mutation, DirectDutyProof<DirectDutyEcState> expectedEc,
                DirectDutyProof<DirectDutyAccessState> expectedAccess, CancellationToken cancellationToken) =>
                Task.FromResult(IsCurrent && intent == mutation && expectedEc.State == port.ec && expectedAccess.State == Access);

            public Task<DirectDutySubmission> SubmitAsync(DirectDutyExitMutation mutation, CancellationToken cancellationToken)
            {
                if (!IsCurrent || intent != mutation) { return Task.FromResult(DirectDutySubmission.NotSubmitted); }
                port.journal = port.journal! with { Evidence = DirectDutySubmissionEvidence.PossiblySubmitted };
                port.Submitted.Add(mutation.Step);
                if (port.LoseFirstSubmissionOutcome) { return Task.FromResult(DirectDutySubmission.Uncertain); }
                if (mutation.Step is not (DirectDutyExitStep.AutomaticFirst or DirectDutyExitStep.AutomaticSecond))
                {
                    throw new InvalidOperationException("This fixture admits only the Auto channels");
                }
                port.ec = port.ec with { DirectDuty = false, NormalProfile = mutation.Destination.Performance };
                port.journal = port.journal! with { Evidence = DirectDutySubmissionEvidence.Submitted };
                return Task.FromResult(DirectDutySubmission.Acknowledged);
            }

            public Task RecordConfirmationAsync(DirectDutyExitMutation mutation, DirectDutyConfirmation confirmation,
                CancellationToken cancellationToken)
            {
                intent = null;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                if (port.FailExitRelease) { throw new IOException("Lost exit release"); }
                if (IsCurrent) { port.journal = port.journal! with { Released = true }; }
                return ValueTask.CompletedTask;
            }
        }
    }
}
