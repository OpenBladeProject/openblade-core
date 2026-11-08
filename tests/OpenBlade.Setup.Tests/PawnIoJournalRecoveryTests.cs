using OpenBlade.Platform;
using OpenBlade.Setup;

namespace OpenBlade.Setup.Tests;

public sealed class PawnIoJournalRecoveryTests
{
    private const long Boot = 1_000_000;
    private const long NextBoot = 1_000;
    private static readonly DateTime Created = new(2026, 11, 7, 2, 0, 0, DateTimeKind.Utc);
    private static PawnIoInstallation Trusted() => new(new Version(2, 2, 0, 0), true, "catalog");
    private static PawnIoInstallJournal Journal(string stage, int? exit = null) =>
        new(2, WindowsPawnIoSetupOperations.InstallerSha256, stage, Boot,
            @"C:\Program Files\OpenBlade.installing.0123456789abcdef0123456789abcdef\PawnIO_setup.exe",
            stage == "NoProcessCreated" ? null : 123, stage == "NoProcessCreated" ? null : Created, exit);

    [Theory]
    [InlineData(1)]
    [InlineData(1603)]
    public void ObservedFailureAllowsFreshExplicitRetryOnlyAfterReadOnlyAbsence(int exit)
    {
        int reads = 0;
        PawnIoSetupInspection result = PawnIoJournalRecovery.Reconcile(Journal("ExitObserved", exit), Boot,
            PawnIoRecordedProcessState.NotRunning, () => { reads++; return null; }, out bool clear);
        Assert.True(clear);
        Assert.Null(result.Installation);
        Assert.False(result.RebootPending);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void KnownCreationFailureCanRecoverWithoutInventingSubmission()
    {
        var result = PawnIoJournalRecovery.Reconcile(Journal("NoProcessCreated", 5), Boot,
            PawnIoRecordedProcessState.NotRunning, () => null, out bool clear);
        Assert.True(clear);
        Assert.Null(result.Installation);
    }

    [Theory]
    [InlineData("SubmissionIntent")]
    [InlineData("ProcessStarted")]
    public void UnboundSameBootIntentCannotClearMerelyBecausePidIsAbsent(string stage)
    {
        PawnIoInstallJournal journal = Journal(stage) with { ProcessId = null, ProcessCreatedAt = null };
        int reads = 0;
        Assert.Throws<InvalidOperationException>(() => PawnIoJournalRecovery.Reconcile(journal, Boot,
            PawnIoRecordedProcessState.NotRunning, () => { reads++; return Trusted(); }, out _));
        Assert.Equal(0, reads);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PotentiallyRunningInstallerBlocksBeforeReadingOrClearingPackage(bool running)
    {
        PawnIoRecordedProcessState process = running ? PawnIoRecordedProcessState.Running : PawnIoRecordedProcessState.Indeterminate;
        int reads = 0;
        Assert.Throws<InvalidOperationException>(() => PawnIoJournalRecovery.Reconcile(Journal("ProcessStarted"), Boot,
            process, () => { reads++; return Trusted(); }, out _));
        Assert.Equal(0, reads);
    }

    [Fact]
    public void ExitedIdentityBoundProcessCanRecoverTrustedCompletedInstallation()
    {
        var result = PawnIoJournalRecovery.Reconcile(Journal("ProcessStarted"), Boot,
            PawnIoRecordedProcessState.NotRunning, Trusted, out bool clear);
        Assert.True(clear);
        Assert.NotNull(result.Installation);
    }

    [Fact]
    public void RebootAndFreshTrustedBindingReconcileIntentWithoutReinstall()
    {
        var journal = Journal("SubmissionIntent") with { ProcessId = null, ProcessCreatedAt = null };
        var result = PawnIoJournalRecovery.Reconcile(journal, NextBoot,
            PawnIoRecordedProcessState.Indeterminate, Trusted, out bool clear);
        Assert.True(clear);
        Assert.NotNull(result.Installation);
    }

    [Fact]
    public void RebootAndMissingBindingNeverAuthorizeReplayOfUncertainSubmission()
    {
        var journal = Journal("SubmissionIntent") with { ProcessId = null, ProcessCreatedAt = null };
        Assert.Throws<InvalidOperationException>(() => PawnIoJournalRecovery.Reconcile(journal, NextBoot,
            PawnIoRecordedProcessState.NotRunning, () => null, out _));
    }

    [Fact]
    public void KnownLiveInstallerAlwaysBlocksEvenWhenUptimeObservationsConflict()
    {
        int reads = 0;
        Assert.Throws<InvalidOperationException>(() => PawnIoJournalRecovery.Reconcile(Journal("ProcessStarted"), NextBoot,
            PawnIoRecordedProcessState.Running, () => { reads++; return Trusted(); }, out _));
        Assert.Equal(0, reads);
    }

    [Fact]
    public void UptimeGrowthCannotMisclassifyWallClockShiftAsReboot()
    {
        // Wall-clock provenance can change without authorizing an unbound intent.
        var journal = Journal("SubmissionIntent") with { ProcessId = null, ProcessCreatedAt = Created.AddYears(-1) };
        Assert.Throws<InvalidOperationException>(() => PawnIoJournalRecovery.Reconcile(journal, Boot + 1000,
            PawnIoRecordedProcessState.Indeterminate, Trusted, out _));
    }

    [Fact]
    public void UnboundIntentWithAgedNewBootRemainsPausedInsteadOfGuessing()
    {
        var journal = Journal("SubmissionIntent") with { ProcessId = null, ProcessCreatedAt = null };
        Assert.Throws<InvalidOperationException>(() => PawnIoJournalRecovery.Reconcile(journal, Boot + 100000,
            PawnIoRecordedProcessState.Indeterminate, Trusted, out _));
    }

    [Fact]
    public void NegativeUptimeIsMalformedAndCannotBecomeRebootEvidence()
    {
        var journal = Journal("SubmissionIntent") with { SubmittedUptimeMilliseconds = -1 };
        Assert.Throws<InvalidDataException>(() => PawnIoJournalRecovery.Reconcile(journal, Boot,
            PawnIoRecordedProcessState.NotRunning, Trusted, out _));
        Assert.Throws<InvalidDataException>(() => PawnIoJournalRecovery.Reconcile(Journal("SubmissionIntent"), -1,
            PawnIoRecordedProcessState.NotRunning, Trusted, out _));
    }

    [Fact]
    public void RebootPendingWithoutBindingRemainsPendingAndRetainsJournal()
    {
        var result = PawnIoJournalRecovery.Reconcile(Journal("ExitObserved", 3010), Boot,
            PawnIoRecordedProcessState.NotRunning, () => null, out bool clear);
        Assert.False(clear);
        Assert.True(result.RebootPending);
        Assert.Null(result.Installation);
    }

    [Fact]
    public void UntrustedReadbackDoesNotClearKnownFailureEvidence()
    {
        Assert.Throws<InvalidDataException>(() => PawnIoJournalRecovery.Reconcile(Journal("ExitObserved", 1), Boot,
            PawnIoRecordedProcessState.NotRunning, () => throw new InvalidDataException("untrusted"), out _));
    }

    [Fact]
    public void PidReuseCannotBeMistakenForTheOriginalInstaller()
    {
        Assert.Equal(PawnIoRecordedProcessState.NotRunning, PawnIoJournalRecovery.ClassifyProcessIdentity(
            Created, "old", Created.AddMinutes(1), "unrelated"));
        Assert.Equal(PawnIoRecordedProcessState.Indeterminate, PawnIoJournalRecovery.ClassifyProcessIdentity(
            Created, "expected", Created, "different"));
        Assert.Equal(PawnIoRecordedProcessState.Running, PawnIoJournalRecovery.ClassifyProcessIdentity(
            Created, "expected", Created, "expected"));
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(193, true)]
    [InlineData(740, true)]
    [InlineData(0, false)]
    [InlineData(123456, false)]
    public void OnlyKnownCreateProcessFailuresEstablishNonSubmission(int error, bool expected) =>
        Assert.Equal(expected, PawnIoJournalRecovery.IsKnownNoProcessCreated(error));
}
