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
        Assert.Equal(new DirectDutyEcState(false, performance, 0, 0x12, 70, 80), fake.Ec);
        Assert.Equal(new DirectDutyAccessState(null, false), fake.Access);
        Assert.DoesNotContain(fake.Events.Skip(fake.Events.IndexOf("submit:RestoreBank") + 1), item => item == "read:ec");
        var gateIntent = fake.IntentProofs[DirectDutyExitStep.RestoreGate].Access;
        var gateConfirmation = fake.Confirmations[DirectDutyExitStep.RestoreGate];
        Assert.Equal(new DirectDutyAccessState(3, true), gateIntent.State);
        Assert.True(gateIntent.Sequence > fake.Confirmations[DirectDutyExitStep.RestoreBank].ObservationSequence);
        Assert.Equal(gateIntent.Sequence, gateConfirmation.PreservedBankSequence);
        Assert.Equal(gateConfirmation.ObservationSequence - 1, gateIntent.Sequence);
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
    public async Task CustomAutoUsesIndependentHardwareLevelsAndOmitsHostOnlyIntent()
    {
        var custom = new CustomPerformanceSettings(CustomPerformanceLevel.High, CustomPerformanceLevel.Low)
        {
            MaximumFans = true,
            GpuClockOffsets = new(100, 200),
        };
        var ownership = Ownership(PerformancePreset.Custom) with { CapturedProfile = new(PerformancePreset.Custom, custom) };
        var fake = new FakePort(ownership);
        var retained = new DirectDutyCustomState(custom.Cpu, custom.Gpu, false);
        fake.Ec = fake.Ec with { RetainedCustom = retained };
        var result = await new DirectDutyAutomaticExit(fake).RunAsync(ownership);

        Assert.True(result.Success);
        Assert.Equal(PerformancePreset.Custom, fake.Ec.NormalProfile);
        Assert.Equal(retained, fake.Ec.RetainedCustom);
        Assert.Equal(new DirectDutyAutoDestination(PerformancePreset.Custom, custom with { MaximumFans = false, GpuClockOffsets = null }),
            fake.AcquiredDestination);
        Assert.All(fake.Submitted, mutation => Assert.Equal(fake.AcquiredDestination, mutation.Destination));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("cpu")]
    [InlineData("gpu")]
    [InlineData("maximum-custom")]
    [InlineData("maximum-silent")]
    public async Task AutoRefusesDestinationsThatWouldRequireSeparateCustomWrites(string defect)
    {
        var fake = new FakePort(Ownership());
        var custom = new CustomPerformanceSettings(CustomPerformanceLevel.High, CustomPerformanceLevel.Low);
        var retained = new DirectDutyCustomState(custom.Cpu, custom.Gpu, false);
        fake.Ec = fake.Ec with
        {
            RetainedCustom = defect switch
            {
                "missing" => null,
                "cpu" => retained with { Cpu = CustomPerformanceLevel.Low },
                "gpu" => retained with { Gpu = CustomPerformanceLevel.High },
                _ => retained with { MaximumFans = true },
            },
        };
        var destination = defect == "maximum-silent" ? fake.Ownership.CapturedProfile : new(PerformancePreset.Custom, custom);

        var result = await new DirectDutyAutomaticExit(fake).RunAsync(fake.Ownership, destination);

        Assert.Equal(DirectDutyExitFailure.UnsupportedDestination, result.Failure);
        Assert.Empty(fake.IntentProofs);
        Assert.Empty(fake.Submitted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidRetainedLevelsCannotReachTheFirstIntent(bool invalidCpu)
    {
        var fake = new FakePort(Ownership());
        var invalid = (CustomPerformanceLevel)int.MaxValue;
        fake.Ec = fake.Ec with
        {
            RetainedCustom = new(invalidCpu ? invalid : CustomPerformanceLevel.High,
                invalidCpu ? CustomPerformanceLevel.Low : invalid, false),
        };

        var result = await new DirectDutyAutomaticExit(fake).RunAsync(fake.Ownership);

        Assert.Equal(DirectDutyExitFailure.ObservationRejected, result.Failure);
        Assert.Empty(fake.IntentProofs);
        Assert.Empty(fake.Submitted);
    }

    [Fact]
    public async Task GateCloseCapabilityIsRequiredBeforeAnyMutation()
    {
        var fake = new FakePort(Ownership()) { SupportsBankPreservingGateClose = false };
        var result = await new DirectDutyAutomaticExit(fake).RunAsync(fake.Ownership);

        Assert.Equal(DirectDutyExitFailure.AdmissionRefused, result.Failure);
        Assert.Equal(DirectDutyClaimState.Acquired, result.ClaimState);
        Assert.Empty(fake.IntentProofs);
        Assert.Empty(fake.Submitted);
    }

    [Fact]
    public async Task ClosedGateProofCannotClaimAFreshBankValue()
    {
        var fake = new FakePort(Ownership()) { IncludeBankInClosedGateProof = true };
        var result = await new DirectDutyAutomaticExit(fake).RunAsync(fake.Ownership);

        Assert.Equal(DirectDutyExitFailure.Uncertain, result.Failure);
        Assert.Equal(DirectDutyExitFailure.StateChanged, result.Cause);
        Assert.True(result.FanHandbackConfirmed);
        Assert.False(result.CleanupConfirmed);
        Assert.Equal(DirectDutyExitStep.RestoreGate, result.Step);
        Assert.False(fake.Confirmations.ContainsKey(DirectDutyExitStep.RestoreGate));
        Assert.DoesNotContain(fake.Events.Skip(fake.Events.IndexOf("submit:RestoreBank") + 1), item => item == "read:ec");
    }

    [Theory]
    [InlineData("bank")]
    [InlineData("bits")]
    [InlineData("permission")]
    [InlineData("already-owned")]
    [InlineData("custom")]
    [InlineData("owner")]
    [InlineData("manual-hold")]
    [InlineData("manual-other-bits")]
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
            "manual-hold" => ownership with { AppliedManualFlags = 0x80 },
            "manual-other-bits" => ownership with { AppliedManualFlags = 0x02 },
            _ => ownership with { Owner = ownership.Owner with { OperationId = Guid.Empty } },
        };
        var fake = new FakePort(ownership);
        var result = await new DirectDutyAutomaticExit(fake).RunAsync(ownership);
        Assert.Equal(DirectDutyExitFailure.InvalidOwnership, result.Failure);
        Assert.Equal(DirectDutyClaimState.NotAcquired, result.ClaimState);
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
    public async Task UnprovedOrChangedStateCannotReachTheFirstWrite(string defect)
    {
        var fake = new FakePort(Ownership()) { ProofDefect = defect };
        if (defect == "bank") { fake.Access = fake.Access with { Bank = 1 }; }
        if (defect == "gate") { fake.Access = fake.Access with { GateOpen = false }; }
        if (defect == "manual-hold") { fake.Ec = fake.Ec with { ManualFlags = 0x80 }; }
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
        Assert.Equal(DirectDutyExitFailure.Uncertain, result.Failure);
        Assert.Equal(defect == "queue" ? DirectDutyExitFailure.ObservationRejected : DirectDutyExitFailure.StateChanged, result.Cause);
        Assert.False(result.FanHandbackConfirmed);
        Assert.Single(fake.Submitted);
        Assert.Empty(fake.Confirmations);
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
        Assert.Equal(DirectDutyExitFailure.ObservationRejected, result.Cause);
        Assert.True(result.FanHandbackConfirmed);
        Assert.False(result.CleanupConfirmed);
        Assert.Equal(3, fake.Submitted.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JournalFailurePreventsUnrecordedProgress(bool confirmation)
    {
        var fake = new FakePort(Ownership())
        {
            FailIntent = !confirmation,
            FailConfirmationAt = confirmation ? DirectDutyExitStep.AutomaticFirst : null,
        };
        var result = await new DirectDutyAutomaticExit(fake).RunAsync(fake.Ownership);
        Assert.False(result.Success);
        Assert.Equal(confirmation ? 1 : 0, fake.Submitted.Count);
        Assert.Equal(confirmation ? DirectDutyExitFailure.Uncertain : DirectDutyExitFailure.JournalFailed, result.Failure);
        Assert.Equal(DirectDutyExitFailure.JournalFailed, result.Cause);
    }

    [Fact]
    public async Task SecondAutoConfirmationFailurePreservesObservedFanHandback()
    {
        var fake = new FakePort(Ownership()) { FailConfirmationAt = DirectDutyExitStep.AutomaticSecond };
        var result = await new DirectDutyAutomaticExit(fake).RunAsync(fake.Ownership);

        Assert.Equal(DirectDutyExitFailure.Uncertain, result.Failure);
        Assert.Equal(DirectDutyExitFailure.JournalFailed, result.Cause);
        Assert.True(result.FanHandbackConfirmed);
        Assert.False(result.CleanupConfirmed);
        Assert.Equal(new[] { DirectDutyExitStep.AutomaticFirst, DirectDutyExitStep.AutomaticSecond },
            fake.Submitted.Select(mutation => mutation.Step));
        Assert.Single(fake.Confirmations);
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
        var fake = new FakePort(owner) { SupportsBankPreservingGateClose = false };
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
        Assert.Equal(DirectDutyExitFailure.Canceled, result.Cause);
        Assert.Equal(DirectDutyClaimState.NotAcquired, result.ClaimState);
        Assert.Empty(fake.Submitted);
        Assert.Equal(0, fake.Acquisitions);
    }

    [Fact]
    public async Task CancellationInsideSubmissionIsUncertainAndCannotTriggerCleanup()
    {
        var fake = new FakePort(Ownership()) { ThrowFromSubmission = true };
        var result = await new DirectDutyAutomaticExit(fake).RunAsync(fake.Ownership);
        Assert.Equal(DirectDutyExitFailure.Uncertain, result.Failure);
        Assert.Equal(DirectDutyExitFailure.Canceled, result.Cause);
        Assert.Equal(DirectDutyClaimState.Acquired, result.ClaimState);
        Assert.Single(fake.Submitted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedAcquisitionPreservesUnknownClaimState(bool canceled)
    {
        var fake = new FakePort(Ownership())
        {
            AcquisitionError = canceled ? new OperationCanceledException() : new IOException("Ownership store unavailable"),
        };
        var result = await new DirectDutyAutomaticExit(fake).RunAsync(fake.Ownership);

        var expected = canceled ? DirectDutyExitFailure.Canceled : DirectDutyExitFailure.AcquisitionFailed;
        Assert.Equal(expected, result.Failure);
        Assert.Equal(expected, result.Cause);
        Assert.Equal(DirectDutyClaimState.Unknown, result.ClaimState);
        Assert.Equal(0, result.SubmissionAttempts);
        Assert.Equal(1, fake.Acquisitions);
        Assert.Empty(fake.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleaseFailurePreservesPriorOutcomeAndObservedProgress(bool observationFailed)
    {
        var fake = new FakePort(Ownership())
        {
            FailRelease = true,
            AfterAutomaticDefect = observationFailed ? "queue" : null,
        };
        var result = await new DirectDutyAutomaticExit(fake).RunAsync(fake.Ownership);

        Assert.False(result.Success);
        Assert.True(result.RequiresReadOnlyReconciliation);
        Assert.True(result.SessionReleaseFailed);
        Assert.Equal(DirectDutyClaimState.Acquired, result.ClaimState);
        Assert.Equal(observationFailed ? DirectDutyExitFailure.Uncertain : DirectDutyExitFailure.None, result.Failure);
        Assert.Equal(observationFailed ? DirectDutyExitFailure.ObservationRejected : DirectDutyExitFailure.None, result.Cause);
        Assert.Equal(!observationFailed, result.FanHandbackConfirmed);
        Assert.Equal(!observationFailed, result.CleanupConfirmed);
        Assert.Equal(observationFailed ? 1 : 7, result.SubmissionAttempts);
        Assert.Equal("dispose", fake.Events[^1]);
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
        public bool SupportsBankPreservingGateClose { get; init; } = true;
        public bool IncludeBankInClosedGateProof { get; init; }
        public DirectDutyAutoDestination? AcquiredDestination { get; private set; }
        public List<string> Events { get; } = [];
        public List<DirectDutyExitMutation> Submitted { get; } = [];
        public Dictionary<DirectDutyExitStep, (DirectDutyProof<DirectDutyEcState> Ec, DirectDutyProof<DirectDutyAccessState> Access)> IntentProofs { get; } = [];
        public Dictionary<DirectDutyExitStep, DirectDutyConfirmation> Confirmations { get; } = [];
        public int Acquisitions { get; private set; }
        public string? ProofDefect { get; init; }
        public string? AfterAutomaticDefect { get; init; }
        public DirectDutyExitStep? FaultStep { get; init; }
        public DirectDutySubmission Outcome { get; init; } = DirectDutySubmission.Acknowledged;
        public DirectDutyExitStep? FailReadAfter { get; init; }
        public bool ChangeOverrideAfterHandback { get; init; }
        public bool ChangeBankDuringIntent { get; init; }
        public bool FailIntent { get; init; }
        public DirectDutyExitStep? FailConfirmationAt { get; init; }
        public Exception? AcquisitionError { get; init; }
        public bool FailRelease { get; init; }
        public bool ThrowFromSubmission { get; init; }

        public Task<IDirectDutyAutoExitSession?> AcquireOwnedSessionAsync(DirectDutyOwnership ownership, DirectDutyAutoDestination destination, CancellationToken cancellationToken)
        {
            Acquisitions++;
            if (AcquisitionError is not null) { throw AcquisitionError; }
            if (claimed || ownership != Ownership) { return Task.FromResult<IDirectDutyAutoExitSession?>(null); }
            claimed = true;
            AcquiredDestination = destination;
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
        public Task RecordIntentAsync(DirectDutyExitMutation mutation, DirectDutyProof<DirectDutyEcState> expectedEc,
            DirectDutyProof<DirectDutyAccessState> expectedAccess, CancellationToken cancellationToken)
        {
            Events.Add($"intent:{mutation.Step}");
            if (FailIntent) { throw new IOException("Journal unavailable"); }
            IntentProofs.Add(mutation.Step, (expectedEc, expectedAccess));
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
                    Ec = AfterAutomaticDefect == "FE" ? Ec : Ec with { DirectDuty = false, NormalProfile = mutation.Destination.Performance, ManualFlags = AfterAutomaticDefect == "manual" ? (byte)1 : (byte)0 };
                    break;
                case DirectDutyExitStep.RestoreOverride: Ec = Ec with { OverrideFlags = mutation.Value }; break;
                case DirectDutyExitStep.RestoreDuty1: Ec = Ec with { Duty1 = mutation.Value }; break;
                case DirectDutyExitStep.RestoreDuty2: Ec = Ec with { Duty2 = mutation.Value }; break;
                case DirectDutyExitStep.RestoreBank: Access = Access with { Bank = mutation.Value }; break;
                case DirectDutyExitStep.RestoreGate: Access = new(IncludeBankInClosedGateProof ? Access.Bank : null, false); break;
                default: throw new ArgumentOutOfRangeException(nameof(mutation));
            }
            return Task.FromResult(mutation.Step == FaultStep ? Outcome : DirectDutySubmission.Acknowledged);
        }
        public Task RecordConfirmationAsync(DirectDutyExitMutation mutation, DirectDutyConfirmation confirmation, CancellationToken cancellationToken)
        {
            Events.Add($"confirm:{mutation.Step}");
            if (FailConfirmationAt == mutation.Step) { throw new IOException("Confirmation not durable"); }
            Confirmations.Add(mutation.Step, confirmation);
            if (ChangeOverrideAfterHandback && mutation.Step == DirectDutyExitStep.AutomaticSecond) { Ec = Ec with { OverrideFlags = (byte)(Ec.OverrideFlags ^ 1) }; }
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            Events.Add("dispose");
            if (FailRelease) { throw new IOException("Session release failed"); }
            return ValueTask.CompletedTask;
        }
    }
}
