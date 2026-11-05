using OpenBlade.Capture;
using OpenBlade.Contracts;

namespace OpenBlade.Capture.Tests;

public sealed class DirectDutyAutomaticExitTests
{
    [Theory]
    [InlineData(PerformancePreset.Silent)]
    [InlineData(PerformancePreset.Balanced)]
    [InlineData(PerformancePreset.Performance)]
    [InlineData(PerformancePreset.BatterySaver)]
    [InlineData(PerformancePreset.BatteryBalanced)]
    public async Task AutoPreservesTheCapturedProfileAndExplicitlySubmitsBothChannels(PerformancePreset performance)
    {
        var fake = new FakePort(Ownership(performance));
        var result = await new DirectDutyAutomaticExit(fake).RunAsync(fake.Ownership);

        Assert.True(result.Success);
        Assert.True(result.FanHandbackConfirmed);
        Assert.True(result.CleanupConfirmed);
        Assert.False(result.RequiresReadOnlyReconciliation);
        Assert.Equal(7, result.SubmissionAttempts);
        Assert.Equal(Enum.GetValues<DirectDutyExitStep>(), fake.Submitted.Select(item => item.Step));
        Assert.All(fake.Submitted, item => Assert.Equal(performance, item.Destination.Performance));
        Assert.Equal(new DirectDutyEcState(false, new(performance), 0, 0x12, 70, 80), fake.Ec);
        Assert.Equal(new DirectDutyAccessState(3, false), fake.Access);
        Assert.DoesNotContain(fake.Events.Skip(fake.Events.IndexOf("submit:RestoreGate") + 1), item => item == "read:ec");
        foreach (var command in fake.Submitted)
        {
            int intent = fake.Events.IndexOf($"intent:{command.Step}");
            int admission = fake.Events.IndexOf($"admit:{command.Step}");
            int submit = fake.Events.IndexOf($"submit:{command.Step}");
            int confirmed = fake.Events.IndexOf($"confirm:{command.Step}");
            Assert.True(intent < admission && admission < submit && submit < confirmed);
            Assert.Contains(fake.Events.Skip(submit + 1).Take(confirmed - submit - 1), item => item.StartsWith("read:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task ExplicitIntendedProfileReplacesCapturedProfileWithoutAnIntermediateDefault()
    {
        var fake = new FakePort(Ownership(PerformancePreset.Silent));
        var target = new DirectDutyAutoDestination(PerformancePreset.Performance);
        var result = await new DirectDutyAutomaticExit(fake).RunAsync(fake.Ownership, target);
        Assert.True(result.Success);
        Assert.All(fake.Submitted, item => Assert.Equal(target, item.Destination));
    }

    [Fact]
    public async Task CustomAutoPreservesLevelsAndClearsItsMaximumFanIntent()
    {
        var custom = new CustomPerformanceSettings(CustomPerformanceLevel.High, CustomPerformanceLevel.Low) { MaximumFans = true };
        var ownership = Ownership(PerformancePreset.Custom) with { CapturedProfile = new(PerformancePreset.Custom, custom) };
        var fake = new FakePort(ownership);
        var result = await new DirectDutyAutomaticExit(fake).RunAsync(ownership);
        Assert.True(result.Success);
        Assert.Equal(custom with { MaximumFans = false }, fake.Ec.NormalProfile!.Custom);
    }

    [Theory]
    [InlineData("bank")]
    [InlineData("bits")]
    [InlineData("permission")]
    [InlineData("already-owned")]
    [InlineData("custom")]
    [InlineData("owner")]
    public async Task InvalidCaptureNeverAcquiresATransport(string defect)
    {
        var ownership = Ownership();
        ownership = defect switch
        {
            "bank" => ownership with { OriginalBank = 8 },
            "bits" => ownership with { OwnedOverrideBits = 0x60 },
            "permission" => ownership with { OwnedOverrideBits = 0 },
            "already-owned" => ownership with { OriginalOverride = 0x52 },
            "custom" => ownership with { CapturedProfile = new(PerformancePreset.Custom) },
            _ => ownership with { Owner = ownership.Owner with { OperationId = Guid.Empty } },
        };
        var fake = new FakePort(ownership);
        var result = await new DirectDutyAutomaticExit(fake).RunAsync(ownership);
        Assert.Equal(DirectDutyExitFailure.InvalidOwnership, result.Failure);
        Assert.Equal(0, fake.Acquisitions);
        Assert.Empty(fake.Submitted);
    }

    [Theory]
    [InlineData("cached")]
    [InlineData("unsettled")]
    [InlineData("inactive")]
    [InlineData("stale")]
    [InlineData("owner")]
    [InlineData("bank")]
    [InlineData("gate")]
    [InlineData("manual-hold")]
    [InlineData("manual-other-bits")]
    public async Task UnprovedOrChangedStateCannotReachTheFirstWrite(string defect)
    {
        var fake = new FakePort(Ownership()) { ProofDefect = defect };
        if (defect == "bank") { fake.Access = fake.Access with { Bank = 1 }; }
        if (defect == "gate") { fake.Access = fake.Access with { GateOpen = false }; }
        if (defect == "manual-hold")
        {
            fake = new FakePort(Ownership() with { AppliedManualFlags = 0x80 });
        }
        if (defect == "manual-other-bits")
        {
            fake = new FakePort(Ownership() with { AppliedManualFlags = 0x02 });
        }
        var result = await new DirectDutyAutomaticExit(fake).RunAsync(fake.Ownership);
        Assert.False(result.Success);
        Assert.True(result.RequiresReadOnlyReconciliation);
        Assert.Empty(fake.Submitted);
    }

    [Theory]
    [InlineData((int)DirectDutyExitStep.AutomaticFirst)]
    [InlineData((int)DirectDutyExitStep.AutomaticSecond)]
    [InlineData((int)DirectDutyExitStep.RestoreOverride)]
    [InlineData((int)DirectDutyExitStep.RestoreDuty1)]
    [InlineData((int)DirectDutyExitStep.RestoreDuty2)]
    [InlineData((int)DirectDutyExitStep.RestoreBank)]
    [InlineData((int)DirectDutyExitStep.RestoreGate)]
    public async Task AmbiguousSubmissionStopsAndCannotBeReplayedByANewCoordinator(int failedValue)
    {
        var failed = (DirectDutyExitStep)failedValue;
        var fake = new FakePort(Ownership()) { FaultStep = failed, Outcome = DirectDutySubmission.Uncertain };
        var result = await new DirectDutyAutomaticExit(fake).RunAsync(fake.Ownership);
        Assert.Equal(DirectDutyExitFailure.Uncertain, result.Failure);
        Assert.Equal(failed, result.Step);
        Assert.Equal((int)failed + 1, fake.Submitted.Count);
        Assert.Equal(failedValue > (int)DirectDutyExitStep.AutomaticSecond, result.FanHandbackConfirmed);
        Assert.False(result.CleanupConfirmed);
        Assert.DoesNotContain($"confirm:{failed}", fake.Events);
        int submitted = fake.Submitted.Count;
        var retried = await new DirectDutyAutomaticExit(fake).RunAsync(fake.Ownership);
        Assert.Equal(DirectDutyExitFailure.AdmissionRefused, retried.Failure);
        Assert.Equal(submitted, fake.Submitted.Count);
    }

    [Theory]
    [InlineData("FE")]
    [InlineData("manual")]
    [InlineData("queue")]
    public async Task AcceptedCommandDoesNotReplaceActualEcConfirmation(string defect)
    {
        var fake = new FakePort(Ownership()) { AfterAutomaticDefect = defect };
        var result = await new DirectDutyAutomaticExit(fake).RunAsync(fake.Ownership);
        Assert.False(result.Success);
        Assert.False(result.FanHandbackConfirmed);
        Assert.Single(fake.Submitted);
    }

    [Fact]
    public async Task UnrelatedOverrideDriftPreventsWholeByteCleanup()
    {
        var fake = new FakePort(Ownership()) { ChangeOverrideAfterHandback = true };
        var result = await new DirectDutyAutomaticExit(fake).RunAsync(fake.Ownership);
        Assert.Equal(DirectDutyExitFailure.StateChanged, result.Failure);
        Assert.True(result.FanHandbackConfirmed);
        Assert.False(result.CleanupConfirmed);
        Assert.Equal(2, fake.Submitted.Count);
        Assert.Equal(0x53, fake.Ec.OverrideFlags);
    }

    [Fact]
    public async Task AdmissionRechecksAccessAfterDurableIntent()
    {
        var fake = new FakePort(Ownership()) { ChangeBankDuringIntent = true };
        var result = await new DirectDutyAutomaticExit(fake).RunAsync(fake.Ownership);
        Assert.Equal(DirectDutyExitFailure.AdmissionRefused, result.Failure);
        Assert.Empty(fake.Submitted);
        Assert.Contains("intent:AutomaticFirst", fake.Events);
    }

    [Fact]
    public async Task ReadbackLossAfterFanHandbackStopsCleanup()
    {
        var fake = new FakePort(Ownership()) { FailReadAfter = DirectDutyExitStep.RestoreOverride };
        var result = await new DirectDutyAutomaticExit(fake).RunAsync(fake.Ownership);
        Assert.Equal(DirectDutyExitFailure.Uncertain, result.Failure);
        Assert.True(result.FanHandbackConfirmed);
        Assert.False(result.CleanupConfirmed);
        Assert.Equal(3, fake.Submitted.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JournalFailurePreventsUnrecordedProgress(bool confirmation)
    {
        var fake = new FakePort(Ownership()) { FailIntent = !confirmation, FailConfirmation = confirmation };
        var result = await new DirectDutyAutomaticExit(fake).RunAsync(fake.Ownership);
        Assert.False(result.Success);
        Assert.Equal(confirmation ? 1 : 0, fake.Submitted.Count);
        Assert.Equal(confirmation ? DirectDutyExitFailure.Uncertain : DirectDutyExitFailure.JournalFailed, result.Failure);
    }

    [Fact]
    public async Task KnownNotSubmittedStopsWithoutTryingAnAlternateProfile()
    {
        var fake = new FakePort(Ownership()) { FaultStep = DirectDutyExitStep.AutomaticFirst, Outcome = DirectDutySubmission.NotSubmitted };
        var result = await new DirectDutyAutomaticExit(fake).RunAsync(fake.Ownership);
        Assert.Equal(DirectDutyExitFailure.NotSubmitted, result.Failure);
        Assert.Single(fake.Submitted);
    }

    [Fact]
    public async Task PreexistingGateAndPermissionArePreservedAndUnchangedFieldsAreSkipped()
    {
        var owner = Ownership() with { OriginalOverride = 0x52, OwnedOverrideBits = 0, OriginalDuty1 = 0, OriginalDuty2 = 0, OriginalBank = 0, OriginalGateOpen = true };
        var fake = new FakePort(owner);
        var result = await new DirectDutyAutomaticExit(fake).RunAsync(owner);
        Assert.True(result.Success);
        Assert.Equal(2, fake.Submitted.Count);
        Assert.Equal(0x52, fake.Ec.OverrideFlags);
        Assert.True(fake.Access.GateOpen);
    }

    [Fact]
    public async Task CancellationBeforeSubmissionNeverStartsAnExit()
    {
        var fake = new FakePort(Ownership());
        var result = await new DirectDutyAutomaticExit(fake).RunAsync(fake.Ownership, cancellationToken: new CancellationToken(true));
        Assert.Equal(DirectDutyExitFailure.Canceled, result.Failure);
        Assert.Empty(fake.Submitted);
        Assert.Equal(0, fake.Acquisitions);
    }

    [Fact]
    public async Task CancellationInsideSubmissionIsUncertainAndCannotTriggerCleanup()
    {
        var fake = new FakePort(Ownership()) { ThrowFromSubmission = true };
        var result = await new DirectDutyAutomaticExit(fake).RunAsync(fake.Ownership);
        Assert.Equal(DirectDutyExitFailure.Uncertain, result.Failure);
        Assert.Single(fake.Submitted);
    }

    private static DirectDutyOwnership Ownership(PerformancePreset performance = PerformancePreset.Silent) => new(
        new(Guid.NewGuid(), "fixture-device", "fixture-reviewed-image", Guid.NewGuid()),
        new(performance), 0x12, 0x40, 70, 80, 0, 0, 0, 3, false);

    // No hardware calls. The retained claimed flag models the adapter's mandatory atomic,
    // durable single-use claim; it survives coordinator/session disposal in these tests.
    private sealed class FakePort : IDirectDutyAutoExitPort, IDirectDutyAutoExitSession
    {
        private bool claimed;
        private long sequence;
        private DirectDutyExitStep? lastSubmission;
        public FakePort(DirectDutyOwnership ownership)
        {
            Ownership = ownership;
            Ec = new(true, null, ownership.AppliedManualFlags, (byte)(ownership.OriginalOverride | ownership.OwnedOverrideBits), ownership.AppliedDuty1, ownership.AppliedDuty2);
        }
        public DirectDutyOwnership Ownership { get; }
        public DirectDutyEcState Ec { get; set; }
        public DirectDutyAccessState Access { get; set; } = new(0, true);
        public List<string> Events { get; } = [];
        public List<DirectDutyExitMutation> Submitted { get; } = [];
        public int Acquisitions { get; private set; }
        public string? ProofDefect { get; init; }
        public string? AfterAutomaticDefect { get; init; }
        public DirectDutyExitStep? FaultStep { get; init; }
        public DirectDutySubmission Outcome { get; init; } = DirectDutySubmission.Acknowledged;
        public DirectDutyExitStep? FailReadAfter { get; init; }
        public bool ChangeOverrideAfterHandback { get; init; }
        public bool ChangeBankDuringIntent { get; init; }
        public bool FailIntent { get; init; }
        public bool FailConfirmation { get; init; }
        public bool ThrowFromSubmission { get; init; }

        public Task<IDirectDutyAutoExitSession?> AcquireOwnedSessionAsync(DirectDutyOwnership ownership, DirectDutyAutoDestination destination, CancellationToken cancellationToken)
        {
            Acquisitions++;
            if (claimed || ownership != Ownership) { return Task.FromResult<IDirectDutyAutoExitSession?>(null); }
            claimed = true;
            return Task.FromResult<IDirectDutyAutoExitSession?>(this);
        }
        public Task<DirectDutyProof<DirectDutyEcState>> ReadEcAsync(CancellationToken cancellationToken)
        {
            Events.Add("read:ec");
            if (FailReadAfter is not null && lastSubmission == FailReadAfter) { throw new IOException("Lost readback"); }
            return Task.FromResult(Proof(Ec));
        }
        public Task<DirectDutyProof<DirectDutyAccessState>> ReadAccessAsync(CancellationToken cancellationToken)
        {
            Events.Add("read:access");
            return Task.FromResult(Proof(Access));
        }
        private DirectDutyProof<T> Proof<T>(T state) => new(
            ProofDefect == "owner" ? Ownership.Owner with { ControllerSession = Guid.NewGuid() } : Ownership.Owner,
            ProofDefect == "stale" ? 1 : ++sequence,
            ProofDefect != "cached", ProofDefect != "unsettled" && !(AfterAutomaticDefect == "queue" && lastSubmission is not null),
            ProofDefect != "inactive", state);
        public Task RecordIntentAsync(DirectDutyExitMutation mutation, CancellationToken cancellationToken)
        {
            Events.Add($"intent:{mutation.Step}");
            if (FailIntent) { throw new IOException("Journal unavailable"); }
            if (ChangeBankDuringIntent) { Access = Access with { Bank = 7 }; }
            return Task.CompletedTask;
        }
        public Task<bool> AdmitSubmissionAsync(DirectDutyExitMutation mutation, DirectDutyProof<DirectDutyEcState> expectedEc,
            DirectDutyProof<DirectDutyAccessState> expectedAccess, CancellationToken cancellationToken)
        {
            Events.Add($"admit:{mutation.Step}");
            return Task.FromResult(expectedEc.State == Ec && expectedAccess.State == Access);
        }
        public Task<DirectDutySubmission> SubmitAsync(DirectDutyExitMutation mutation, CancellationToken cancellationToken)
        {
            Submitted.Add(mutation);
            Events.Add($"submit:{mutation.Step}");
            lastSubmission = mutation.Step;
            if (ThrowFromSubmission) { throw new OperationCanceledException(); }
            if (mutation.Step == FaultStep && Outcome == DirectDutySubmission.NotSubmitted) { return Task.FromResult(Outcome); }
            switch (mutation.Step)
            {
                case DirectDutyExitStep.AutomaticFirst:
                case DirectDutyExitStep.AutomaticSecond:
                    Ec = AfterAutomaticDefect == "FE" ? Ec : Ec with { DirectDuty = false, NormalProfile = mutation.Destination, ManualFlags = AfterAutomaticDefect == "manual" ? (byte)1 : (byte)0 };
                    break;
                case DirectDutyExitStep.RestoreOverride: Ec = Ec with { OverrideFlags = mutation.Value }; break;
                case DirectDutyExitStep.RestoreDuty1: Ec = Ec with { Duty1 = mutation.Value }; break;
                case DirectDutyExitStep.RestoreDuty2: Ec = Ec with { Duty2 = mutation.Value }; break;
                case DirectDutyExitStep.RestoreBank: Access = Access with { Bank = mutation.Value }; break;
                case DirectDutyExitStep.RestoreGate: Access = Access with { GateOpen = mutation.Value != 0 }; break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
            return Task.FromResult(mutation.Step == FaultStep ? Outcome : DirectDutySubmission.Acknowledged);
        }
        public Task RecordConfirmationAsync(DirectDutyExitMutation mutation, long observationSequence, CancellationToken cancellationToken)
        {
            Events.Add($"confirm:{mutation.Step}");
            if (FailConfirmation) { throw new IOException("Confirmation not durable"); }
            if (ChangeOverrideAfterHandback && mutation.Step == DirectDutyExitStep.AutomaticSecond) { Ec = Ec with { OverrideFlags = (byte)(Ec.OverrideFlags ^ 1) }; }
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { Events.Add("dispose"); return ValueTask.CompletedTask; }
    }
}
