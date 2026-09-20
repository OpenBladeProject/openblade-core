using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

public sealed class ExactFanControlOwnershipTests
{
    private static readonly FanControlSettings Intent = new(
        FanMode.ManualCurve,
        3700,
        ManualFanCurve.Default);

    [Fact]
    public void MatchesRequiresExactIntentAndBothContinuityStamps()
    {
        ExactFanControlOwnership owned = ExactFanControlOwnership.Owned(Intent, 7, 11);

        Assert.True(owned.Matches(Intent, 7, 11));
        Assert.False(owned.Matches(new FanControlSettings(Intent.Mode, 3800, Intent.ManualCurve), 7, 11));
        Assert.False(owned.Matches(Intent, 8, 11));
        Assert.False(owned.Matches(Intent, 7, 12));
        foreach (ExactFanControlOwnership ownership in UnownedStates())
        {
            Assert.False(ownership.Matches(Intent, 7, 11));
            Assert.Null(ownership.Settings);
            Assert.Equal(-1, ownership.AuthorityEpoch);
            Assert.Equal(-1, ownership.DesiredRevision);
        }
    }

    [Fact]
    public void AutomaticObservationRevokesEveryStateAndClearsTargetsEvenWhenAlreadyUnowned()
    {
        foreach (ExactFanControlOwnership ownership in AllStates())
        {
            ExactFanOwnershipTransition transition = ownership.ObserveTopology(FanMode.Automatic);

            Assert.Equal(ExactFanControlOwnership.Unowned, transition.Ownership);
            Assert.True(transition.ClearCurveTargets);
            Assert.False(transition.RequiresLiveAdmission);
        }
    }

    [Theory]
    [InlineData(FanMode.Fixed)]
    [InlineData(FanMode.ManualCurve)]
    [InlineData(FanMode.Maximum)]
    public void ManualObservationClassifiesTopologyWithoutEstablishingExactOwnership(FanMode mode)
    {
        foreach (ExactFanControlOwnership ownership in UnownedStates())
        {
            ExactFanOwnershipTransition transition = ownership.ObserveTopology(mode);

            Assert.Equal(ExactFanControlOwnership.ManualTopologyOnly, transition.Ownership);
            Assert.True(transition.ClearCurveTargets);
            Assert.False(transition.RequiresLiveAdmission);
        }

        ExactFanControlOwnership owned = ExactFanControlOwnership.Owned(Intent, 7, 11);
        AssertNoChange(owned.ObserveTopology(mode));
    }

    [Fact]
    public void MissingOrUnknownTopologyPreservesEveryStateAndTargets()
    {
        foreach (ExactFanControlOwnership ownership in AllStates())
        {
            AssertNoChange(ownership.ObserveTopology(null));
            AssertNoChange(ownership.ObserveTopology((FanMode)999));
        }
    }

    [Theory]
    [InlineData(ControlResultStatus.Unchanged, false, false)]
    [InlineData(ControlResultStatus.Unchanged, true, true)]
    [InlineData(ControlResultStatus.Applied, true, true)]
    [InlineData(ControlResultStatus.Applied, false, true)]
    public void ConfirmedAutomaticOutcomeClearsEveryStateBeforeSubmissionUncertainty(
        ControlResultStatus status,
        bool writeAttempted,
        bool readbackMatched)
    {
        foreach (ExactFanControlOwnership ownership in AllStates())
        {
            ExactFanOwnershipTransition transition = ownership.EvaluateOutcome(
                FanMode.Automatic,
                status,
                writeAttempted,
                readbackMatched,
                writeSubmissionState: WriteSubmissionState.Indeterminate,
                confirmationUnavailable: true);

            Assert.Equal(ExactFanControlOwnership.Unowned, transition.Ownership);
            Assert.True(transition.ClearCurveTargets);
            Assert.False(transition.RequiresLiveAdmission);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void IndeterminateSubmissionLosesExactOwnershipWhenSubmissionOrConfirmationIsPossible(
        bool writeAttempted,
        bool confirmationUnavailable)
    {
        foreach (ExactFanControlOwnership ownership in AllStates())
        {
            AssertUnresolved(ownership.EvaluateOutcome(
                Intent.Mode,
                ControlResultStatus.Failed,
                writeAttempted,
                readbackMatched: false,
                writeSubmissionState: WriteSubmissionState.Indeterminate,
                confirmationUnavailable: confirmationUnavailable));
        }
    }

    [Fact]
    public void IndeterminateWithoutSubmissionOrUnavailableConfirmationPreservesOwnership()
    {
        foreach (ExactFanControlOwnership ownership in AllStates())
        {
            AssertNoChange(ownership.EvaluateOutcome(
                Intent.Mode,
                ControlResultStatus.Failed,
                writeAttempted: false,
                readbackMatched: false,
                writeSubmissionState: WriteSubmissionState.Indeterminate));
        }
    }

    [Theory]
    [InlineData(ControlResultStatus.Blocked)]
    [InlineData(ControlResultStatus.Failed)]
    [InlineData(ControlResultStatus.Unchanged)]
    public void AttemptedUnconfirmedNonAppliedOutcomeClearsTargetsAndBecomesUnresolved(
        ControlResultStatus status)
    {
        foreach (ExactFanControlOwnership ownership in AllStates())
        {
            AssertUnresolved(ownership.EvaluateOutcome(
                Intent.Mode,
                status,
                writeAttempted: true,
                readbackMatched: false,
                writeSubmissionState: WriteSubmissionState.KnownNotApplied,
                confirmationUnavailable: true));
        }
    }

    [Theory]
    [InlineData(ControlResultStatus.Blocked, false)]
    [InlineData(ControlResultStatus.Blocked, true)]
    [InlineData(ControlResultStatus.Failed, false)]
    [InlineData(ControlResultStatus.Failed, true)]
    [InlineData(ControlResultStatus.Applied, false)]
    public void OutcomesWithoutAConfirmedAppliedWritePreserveStateWhenSubmissionIsResolved(
        ControlResultStatus status,
        bool writeAttempted)
    {
        foreach (ExactFanControlOwnership ownership in AllStates())
        {
            AssertNoChange(ownership.EvaluateOutcome(
                Intent.Mode,
                status,
                writeAttempted,
                readbackMatched: false,
                writeSubmissionState: WriteSubmissionState.KnownNotApplied));
        }
    }

    [Fact]
    public void AppliedWriteWithoutMatchingReadbackBecomesUnresolvedAndClearsTargets()
    {
        foreach (ExactFanControlOwnership ownership in AllStates())
        {
            AssertUnresolved(ownership.EvaluateOutcome(
                Intent.Mode,
                ControlResultStatus.Applied,
                writeAttempted: true,
                readbackMatched: false));
        }
    }

    [Fact]
    public void UnchangedManualResultOnlyClassifiesMatchingReadback()
    {
        foreach (ExactFanControlOwnership ownership in AllStates())
        {
            AssertNoChange(ownership.EvaluateOutcome(
                Intent.Mode,
                ControlResultStatus.Unchanged,
                writeAttempted: false,
                readbackMatched: false));
            Assert.Equal(
                ownership.ObserveTopology(Intent.Mode),
                ownership.EvaluateOutcome(
                    Intent.Mode,
                    ControlResultStatus.Unchanged,
                    writeAttempted: false,
                    readbackMatched: true));
        }
    }

    [Fact]
    public void ConfirmedAppliedWriteRequestsLiveAdmissionWithoutChangingStateOrTargets()
    {
        foreach (ExactFanControlOwnership ownership in AllStates()
            .Where(state => state.Disposition != ExactFanControlOwnershipDisposition.SubmissionUnresolved))
        {
            ExactFanOwnershipTransition transition = ownership.EvaluateOutcome(
                Intent.Mode,
                ControlResultStatus.Applied,
                writeAttempted: true,
                readbackMatched: true,
                confirmationUnavailable: true);

            Assert.Null(transition.Ownership);
            Assert.False(transition.ClearCurveTargets);
            Assert.True(transition.RequiresLiveAdmission);
        }
    }

    [Fact]
    public void UnresolvedOwnershipRequiresExplicitRearmBeforeRequestingLiveAdmission()
    {
        ExactFanControlOwnership unresolved = ExactFanControlOwnership.SubmissionUnresolved;

        AssertNoChange(unresolved.EvaluateOutcome(
            Intent.Mode,
            ControlResultStatus.Applied,
            writeAttempted: true,
            readbackMatched: true));
        ExactFanOwnershipTransition transition = unresolved.EvaluateOutcome(
            Intent.Mode,
            ControlResultStatus.Applied,
            writeAttempted: true,
            readbackMatched: true,
            allowFanOwnershipRearm: true);

        Assert.Null(transition.Ownership);
        Assert.False(transition.ClearCurveTargets);
        Assert.True(transition.RequiresLiveAdmission);
    }

    private static IEnumerable<ExactFanControlOwnership> UnownedStates() =>
    [
        ExactFanControlOwnership.Unowned,
        ExactFanControlOwnership.ManualTopologyOnly,
        ExactFanControlOwnership.SubmissionUnresolved,
    ];

    private static IEnumerable<ExactFanControlOwnership> AllStates() =>
    [
        .. UnownedStates(),
        ExactFanControlOwnership.Owned(Intent, 7, 11),
    ];

    private static void AssertNoChange(ExactFanOwnershipTransition transition)
    {
        Assert.Null(transition.Ownership);
        Assert.False(transition.ClearCurveTargets);
        Assert.False(transition.RequiresLiveAdmission);
    }

    private static void AssertUnresolved(ExactFanOwnershipTransition transition)
    {
        Assert.Equal(ExactFanControlOwnership.SubmissionUnresolved, transition.Ownership);
        Assert.True(transition.ClearCurveTargets);
        Assert.False(transition.RequiresLiveAdmission);
    }
}
