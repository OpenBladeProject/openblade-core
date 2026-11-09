using OpenBlade.Contracts;
using OpenBlade.FirmwareSmoke;

namespace OpenBlade.FirmwareSmoke.Tests;

public sealed class ReversibleSmokeRunnerTests
{
    [Theory]
    [InlineData(CoolingPadConnectionState.PresentExact, FirmwareSmokeOutcome.Passed)]
    [InlineData(CoolingPadConnectionState.Absent, FirmwareSmokeOutcome.Skipped)]
    [InlineData(CoolingPadConnectionState.Ambiguous, FirmwareSmokeOutcome.Skipped)]
    [InlineData(CoolingPadConnectionState.ObservationFailed, FirmwareSmokeOutcome.Skipped)]
    public async Task RunAsync_PadRequiredPerformanceRequiresExactPresenceAndRestores(
        CoolingPadConnectionState pad, FirmwareSmokeOutcome expected)
    {
        await using var client = new FakeFirmwareSmokeClient(
            PerformancePreset.Performance, padlessAdmitted: false, coolingPadConnection: pad);
        IReadOnlyList<FirmwareSmokeCheck> checks = await new ReversibleSmokeRunner(client)
            .RunAsync(CancellationToken.None);
        Assert.Equal(expected, Assert.Single(checks, check => check.Id == "write.hyper-boost").Outcome);
        Assert.Equal(expected == FirmwareSmokeOutcome.Passed ? 2 : 0, client.HyperBoostMutationCalls);
        client.AssertOriginalState();
    }

    [Fact]
    public async Task SnapshotCollector_CapturesVbiosGpuIdentity()
    {
        await using var client = new FakeFirmwareSmokeClient();

        FirmwareSmokeSnapshot snapshot = await new FirmwareSmokeSnapshotCollector(client)
            .CaptureAsync(CancellationToken.None);

        Assert.Equal(
            "NVIDIA GeForce RTX 5090 Laptop GPU",
            snapshot.Firmware.VbiosGpuName);
    }

    [Fact]
    public async Task RunAsync_AppliesAndRestoresEveryAutomatedControl()
    {
        await using var client = new FakeFirmwareSmokeClient();

        IReadOnlyList<FirmwareSmokeCheck> checks =
            await new ReversibleSmokeRunner(client).RunAsync(CancellationToken.None);

        Assert.Equal(8, checks.Count);
        Assert.All(
            checks,
            check => Assert.Equal(FirmwareSmokeOutcome.Passed, check.Outcome));
        Assert.Contains(checks, check => check.Id == "write.hyper-boost");
        Assert.Equal(2, client.HyperBoostMutationCalls);
        client.AssertOriginalState();
    }

    [Fact]
    public async Task RunAsync_WritablePadlessHyperBoostRestoresBalancedDirectly()
    {
        const PerformancePreset baseMode = PerformancePreset.Balanced;
        await using var client = new FakeFirmwareSmokeClient(baseMode);

        IReadOnlyList<FirmwareSmokeCheck> checks =
            await new ReversibleSmokeRunner(client).RunAsync(CancellationToken.None);

        Assert.All(
            checks,
            check => Assert.Equal(FirmwareSmokeOutcome.Passed, check.Outcome));
        Assert.Equal([baseMode], client.HyperBoostEnableBases);
        Assert.Equal([baseMode], client.HyperBoostRestoredBases);
        client.AssertOriginalState();
    }

    [Fact]
    public async Task RunAsync_AdmittedPadlessBalancedAppliesAndRestores()
    {
        await using var client = new FakeFirmwareSmokeClient(
            padlessAdmitted: true,
            coolingPadConnection: CoolingPadConnectionState.Absent);

        IReadOnlyList<FirmwareSmokeCheck> checks =
            await new ReversibleSmokeRunner(client).RunAsync(CancellationToken.None);

        Assert.All(
            checks,
            check => Assert.Equal(FirmwareSmokeOutcome.Passed, check.Outcome));
        Assert.Equal(2, client.HyperBoostMutationCalls);
        client.AssertOriginalState();
    }

    [Fact]
    public async Task RunHyperBoostRoundTripAsync_CustomRestoresExactCpuAndGpuLevels()
    {
        var custom = new CustomPerformanceSettings(
            CustomPerformanceLevel.Medium,
            CustomPerformanceLevel.High);
        await using var client = new FakeFirmwareSmokeClient(
            PerformancePreset.Custom,
            customPerformance: custom);

        FirmwareSmokeCheck check = await new ReversibleSmokeRunner(client)
            .RunHyperBoostRoundTripAsync(CancellationToken.None);

        Assert.Equal(FirmwareSmokeOutcome.Passed, check.Outcome);
        Assert.Contains(
            "exact Custom (CPU Medium, GPU High)",
            check.Expected,
            StringComparison.Ordinal);
        Assert.Contains(
            "independent readback confirmed mode 7",
            check.Actual,
            StringComparison.Ordinal);
        Assert.Contains(
            "independent readback confirmed exact Custom (CPU Medium, GPU High) and Automatic",
            check.Actual,
            StringComparison.Ordinal);
        Assert.Equal(2, client.HyperBoostMutationCalls);
        Assert.Equal(0, client.PerformanceMutationCalls);
        Assert.Equal(0, client.FanMutationCalls);
        client.AssertOriginalState();
    }

    [Fact]
    public async Task RunHyperBoostRoundTripAsync_CustomLevelMismatchFails()
    {
        var custom = new CustomPerformanceSettings(
            CustomPerformanceLevel.Medium,
            CustomPerformanceLevel.High);
        await using var client = new FakeFirmwareSmokeClient(
            PerformancePreset.Custom,
            customPerformance: custom)
        {
            RestoreWrongCustomPerformance = true,
        };

        FirmwareSmokeCheck check = await new ReversibleSmokeRunner(client)
            .RunHyperBoostRoundTripAsync(CancellationToken.None);

        Assert.Equal(FirmwareSmokeOutcome.Failed, check.Outcome);
        Assert.Contains(
            "did not confirm the exact Custom (CPU Medium, GPU High) baseline",
            check.Actual,
            StringComparison.Ordinal);
        Assert.Equal(2, client.HyperBoostMutationCalls);
        Assert.Equal(0, client.PerformanceMutationCalls);
        Assert.Equal(0, client.FanMutationCalls);
    }

    [Theory]
    [InlineData((int)CoolingPadConnectionState.Ambiguous)]
    [InlineData((int)CoolingPadConnectionState.ObservationFailed)]
    public async Task RunAsync_PadlessRequiresConfirmedAbsence(int connectionValue)
    {
        await using var client = new FakeFirmwareSmokeClient(
            padlessAdmitted: true,
            coolingPadConnection: (CoolingPadConnectionState)connectionValue);

        IReadOnlyList<FirmwareSmokeCheck> checks =
            await new ReversibleSmokeRunner(client).RunAsync(CancellationToken.None);

        FirmwareSmokeCheck hyperBoost = Assert.Single(
            checks,
            check => check.Id == "write.hyper-boost");
        Assert.Equal(FirmwareSmokeOutcome.Skipped, hyperBoost.Outcome);
        Assert.Equal(0, client.HyperBoostMutationCalls);
        client.AssertOriginalState();
    }

    [Fact]
    public async Task RunAsync_PadlessRequiresReadWriteAdmission()
    {
        await using var client = new FakeFirmwareSmokeClient(
            padlessAdmitted: false,
            coolingPadConnection: CoolingPadConnectionState.Absent);

        IReadOnlyList<FirmwareSmokeCheck> checks =
            await new ReversibleSmokeRunner(client).RunAsync(CancellationToken.None);

        FirmwareSmokeCheck hyperBoost = Assert.Single(
            checks,
            check => check.Id == "write.hyper-boost");
        Assert.Equal(FirmwareSmokeOutcome.Skipped, hyperBoost.Outcome);
        Assert.Equal(0, client.HyperBoostMutationCalls);
        client.AssertOriginalState();
    }

    [Fact]
    public async Task RunAsync_PadlessUsesAnotherWritableDirectBase()
    {
        // Performance is a writable direct base here, so pad-less HyperBoost enters from it directly.
        await using var client = new FakeFirmwareSmokeClient(
            PerformancePreset.Performance,
            padlessAdmitted: true,
            coolingPadConnection: CoolingPadConnectionState.Absent);

        IReadOnlyList<FirmwareSmokeCheck> checks =
            await new ReversibleSmokeRunner(client).RunAsync(CancellationToken.None);

        FirmwareSmokeCheck hyperBoost = Assert.Single(
            checks,
            check => check.Id == "write.hyper-boost");
        Assert.Equal(FirmwareSmokeOutcome.Passed, hyperBoost.Outcome);
        Assert.Equal(2, client.HyperBoostMutationCalls);
        client.AssertOriginalState();
    }

    [Fact]
    public async Task RunAsync_FailedApplicationOnlyReconcilesReadOnly()
    {
        await using var client = new FakeFirmwareSmokeClient
        {
            FailFirstPerformanceApply = true,
        };

        IReadOnlyList<FirmwareSmokeCheck> checks =
            await new ReversibleSmokeRunner(client).RunAsync(CancellationToken.None);

        FirmwareSmokeCheck failure = Assert.Single(checks);
        Assert.Equal(
            FirmwareSmokeOutcome.Failed,
            failure.Outcome);
        Assert.Equal("write.performance", failure.Id);
        Assert.Equal(1, client.PerformanceMutationCalls);
        Assert.True(client.ObservationCalls >= 2);
        Assert.Contains("No restoration write submitted", failure.Actual, StringComparison.Ordinal);
        client.AssertOriginalState();
    }

    [Fact]
    public async Task RunAsync_ApplicationExceptionAfterSubmissionDoesNotRestoreOrContinue()
    {
        await using var client = new FakeFirmwareSmokeClient { ThrowFirstPerformanceApply = true };

        FirmwareSmokeCheck check = Assert.Single(
            await new ReversibleSmokeRunner(client).RunAsync(CancellationToken.None));

        Assert.Equal(FirmwareSmokeOutcome.Failed, check.Outcome);
        Assert.Equal(1, client.PerformanceMutationCalls);
        Assert.Equal(0, client.HyperBoostMutationCalls);
        Assert.Equal(0, client.FanMutationCalls);
        Assert.Contains("IOException", check.Actual, StringComparison.Ordinal);
        Assert.Contains("restoration remains unresolved", check.Actual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CanceledSubmissionRetainsReconciliationFailureEvidence()
    {
        using var cancellation = new CancellationTokenSource();
        await using var client = new FakeFirmwareSmokeClient
        {
            CancelFirstPerformanceApply = cancellation,
            FailCandidateReadback = true,
        };

        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new ReversibleSmokeRunner(client).RunAsync(cancellation.Token));

        Assert.Contains("Apply=OperationCanceledException", error.Message, StringComparison.Ordinal);
        Assert.Contains("Restore=IOException", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, client.PerformanceMutationCalls);
        Assert.Equal(0, client.HyperBoostMutationCalls);
    }

    [Fact]
    public async Task RunAsync_MissingCandidateReadbackDoesNotRestore()
    {
        await using var client = new FakeFirmwareSmokeClient { FailCandidateReadback = true };

        FirmwareSmokeCheck check = Assert.Single(
            await new ReversibleSmokeRunner(client).RunAsync(CancellationToken.None));

        Assert.Equal(FirmwareSmokeOutcome.Failed, check.Outcome);
        Assert.Equal(1, client.PerformanceMutationCalls);
        Assert.Contains("Apply=IOException", check.Actual, StringComparison.Ordinal);
        Assert.Contains("Restore=IOException", check.Actual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_StoredFanIntentDoesNotConfirmCandidateHardware()
    {
        await using var client = new FakeFirmwareSmokeClient { StoreFixedFanWithoutApply = true };

        IReadOnlyList<FirmwareSmokeCheck> checks =
            await new ReversibleSmokeRunner(client).RunAsync(CancellationToken.None);

        FirmwareSmokeCheck check = checks[^1];
        Assert.Equal("write.fans", check.Id);
        Assert.Equal(FirmwareSmokeOutcome.Failed, check.Outcome);
        Assert.Equal(1, client.FanMutationCalls);
        Assert.Contains("No restoration write submitted", check.Actual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RestorationRequiresSavedFanOverrideAsWellAsHardwareMode()
    {
        await using var client = new FakeFirmwareSmokeClient { RetainFanOverrideOnRestore = true };

        FirmwareSmokeCheck check = (await new ReversibleSmokeRunner(client)
            .RunAsync(CancellationToken.None))[^1];

        Assert.Equal("write.fans", check.Id);
        Assert.Equal(FirmwareSmokeOutcome.Failed, check.Outcome);
        Assert.Equal(2, client.FanMutationCalls);
        Assert.Contains("did not confirm restoration", check.Actual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_FixedBaselineStillRequiresItsOriginalSavedSpeed()
    {
        await using var client = new FakeFirmwareSmokeClient { RetainFanOverrideOnRestore = true };
        client.SetInitialFanOverride(new FanControlSettings(FanMode.Fixed, 4500, ManualFanCurve.Default));

        FirmwareSmokeCheck check = (await new ReversibleSmokeRunner(client)
            .RunAsync(CancellationToken.None))[^1];

        Assert.Equal("write.fans", check.Id);
        Assert.Equal(FirmwareSmokeOutcome.Failed, check.Outcome);
        Assert.Equal(2, client.FanMutationCalls);
    }

    [Fact]
    public async Task RunAsync_CancellationAfterConfirmedCandidateRestoresAndRetainsTheCallerToken()
    {
        using var cancellation = new CancellationTokenSource();
        await using var client = new FakeFirmwareSmokeClient { CancelAfterCandidateReadback = cancellation };

        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ReversibleSmokeRunner(client).RunAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Contains("Restore=Applied", error.Message, StringComparison.Ordinal);
        Assert.Equal(3, client.PerformanceMutationCalls);
        client.AssertOriginalState();
    }

    [Fact]
    public async Task RunAsync_HyperBoostKnownNotAppliedIsNeverRetriedOrRestored()
    {
        await using var client = new FakeFirmwareSmokeClient
        {
            FailHyperBoostEnableKnownNotApplied = true,
        };

        IReadOnlyList<FirmwareSmokeCheck> checks =
            await new ReversibleSmokeRunner(client).RunAsync(CancellationToken.None);

        Assert.Equal(2, checks.Count);
        FirmwareSmokeCheck failure = checks[1];
        Assert.Equal("write.hyper-boost", failure.Id);
        Assert.Equal(FirmwareSmokeOutcome.Failed, failure.Outcome);
        Assert.Contains("no retry", failure.Actual, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "no restoration write",
            failure.Actual,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, client.HyperBoostMutationCalls);
        client.AssertOriginalState();
    }

    [Fact]
    public async Task RunAsync_HyperBoostFailureAfterApplyOnlyReconcilesReadOnly()
    {
        await using var client = new FakeFirmwareSmokeClient
        {
            FailHyperBoostEnableAfterApplying = true,
        };

        IReadOnlyList<FirmwareSmokeCheck> checks =
            await new ReversibleSmokeRunner(client).RunAsync(CancellationToken.None);

        Assert.Equal(2, checks.Count);
        FirmwareSmokeCheck failure = checks[1];
        Assert.Equal("write.hyper-boost", failure.Id);
        Assert.Equal(FirmwareSmokeOutcome.Failed, failure.Outcome);
        Assert.Equal(1, client.HyperBoostMutationCalls);
        Assert.Contains("no restoration write", failure.Actual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_HyperBoostRestoreFailureFailsDespiteExactReadback()
    {
        await using var client = new FakeFirmwareSmokeClient
        {
            FailHyperBoostRestore = true,
        };

        IReadOnlyList<FirmwareSmokeCheck> checks =
            await new ReversibleSmokeRunner(client).RunAsync(CancellationToken.None);

        Assert.Equal(2, checks.Count);
        FirmwareSmokeCheck failure = checks[1];
        Assert.Equal("write.hyper-boost", failure.Id);
        Assert.Equal(FirmwareSmokeOutcome.Failed, failure.Outcome);
        Assert.Contains(
            "did not confirm the exact Balanced baseline",
            failure.Actual,
            StringComparison.Ordinal);
        Assert.Equal(2, client.HyperBoostMutationCalls);
        client.AssertOriginalState();
    }

    [Fact]
    public async Task RunAsync_DoesNotReplayFailedFanTransition()
    {
        await using var client = new FakeFirmwareSmokeClient(
            PerformancePreset.Silent)
        {
            FailFixedFanPreWrite = true,
        };

        IReadOnlyList<FirmwareSmokeCheck> checks =
            await new ReversibleSmokeRunner(client).RunAsync(CancellationToken.None);

        Assert.Equal(3, checks.Count);
        FirmwareSmokeCheck failure = Assert.Single(
            checks,
            check => check.Id == "write.fans");
        Assert.Equal(FirmwareSmokeOutcome.Failed, failure.Outcome);
        Assert.DoesNotContain("Retry=", failure.Actual, StringComparison.Ordinal);
        Assert.Equal(1, client.FanMutationCalls);
        client.AssertOriginalState();
    }

    [Fact]
    public async Task RunAsync_IndeterminateRestoreDoesNotClearOverrideOrContinue()
    {
        await using var client = new FakeFirmwareSmokeClient
        {
            IndeterminatePerformanceRestore = true,
        };

        IReadOnlyList<FirmwareSmokeCheck> checks =
            await new ReversibleSmokeRunner(client).RunAsync(CancellationToken.None);

        FirmwareSmokeCheck check = Assert.Single(checks);
        Assert.Equal(FirmwareSmokeOutcome.Failed, check.Outcome);
        Assert.Equal(2, client.PerformanceMutationCalls);
        Assert.Equal(0, client.HyperBoostMutationCalls);
        Assert.Contains("override was retained without another write", check.Actual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RejectedPerformanceRestoreReconcilesWithoutAFullPollingWindow()
    {
        await using var client = new FakeFirmwareSmokeClient { RejectPerformanceRestore = true };

        FirmwareSmokeCheck check = Assert.Single(await new ReversibleSmokeRunner(client)
            .RunAsync(CancellationToken.None));

        Assert.Equal(FirmwareSmokeOutcome.Failed, check.Outcome);
        Assert.Equal(2, client.PerformanceMutationCalls);
        Assert.InRange(client.ObservationCalls, 2, 8);
        Assert.Equal(0, client.HyperBoostMutationCalls);
        Assert.Contains("override was retained without another write", check.Actual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_FreshRestorationSafetyRefusalLeavesTheCandidateUnresolved()
    {
        await using var client = new FakeFirmwareSmokeClient { BlockPerformanceRestoration = true };

        FirmwareSmokeCheck check = Assert.Single(await new ReversibleSmokeRunner(client)
            .RunAsync(CancellationToken.None));

        Assert.Equal(FirmwareSmokeOutcome.Failed, check.Outcome);
        Assert.Equal(1, client.PerformanceMutationCalls);
        Assert.Contains("no restoration write was submitted", check.Actual, StringComparison.Ordinal);
        Assert.Contains("unresolved", check.Actual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunHyperBoostRoundTripAsync_UsesTheSameFreshRestorationSafetyAdmission()
    {
        await using var client = new FakeFirmwareSmokeClient { BlockHyperBoostRestoration = true };

        FirmwareSmokeCheck check = await new ReversibleSmokeRunner(client)
            .RunHyperBoostRoundTripAsync(CancellationToken.None);

        Assert.Equal(FirmwareSmokeOutcome.Failed, check.Outcome);
        Assert.Equal(1, client.HyperBoostMutationCalls);
        Assert.Contains("no restoration write was submitted", check.Actual, StringComparison.Ordinal);
        Assert.Contains("unresolved", check.Actual, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_AcknowledgedClearWithRetainedOverrideDoesNotPassOrContinue()
    {
        await using var client = new FakeFirmwareSmokeClient { AcknowledgeClearWithRetainedOverride = true };

        FirmwareSmokeCheck check = Assert.Single(await new ReversibleSmokeRunner(client)
            .RunAsync(CancellationToken.None));

        Assert.Equal(FirmwareSmokeOutcome.Failed, check.Outcome);
        Assert.Contains("cleared performance override", check.Actual, StringComparison.Ordinal);
        Assert.Equal(3, client.PerformanceMutationCalls);
        Assert.Equal(0, client.HyperBoostMutationCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_CustomRestorationRequiresExactLevelsBeforeClearingOverride(bool wrongLevels)
    {
        var custom = new CustomPerformanceSettings(CustomPerformanceLevel.Medium, CustomPerformanceLevel.High);
        await using var client = new FakeFirmwareSmokeClient(PerformancePreset.Custom, customPerformance: custom)
        {
            WrongCustomPerformanceRestore = wrongLevels,
        };

        IReadOnlyList<FirmwareSmokeCheck> checks =
            await new ReversibleSmokeRunner(client).RunAsync(CancellationToken.None);

        Assert.All(client.PerformanceRequests.Where(value => value is not null),
            value => Assert.True(value!.IsWellFormed()));
        Assert.Equal(custom, client.PerformanceRequests[1]?.CustomPerformance);
        Assert.Equal(wrongLevels ? FirmwareSmokeOutcome.Failed : FirmwareSmokeOutcome.Passed, checks[0].Outcome);
        Assert.Equal(wrongLevels ? 2 : 3, client.PerformanceMutationCalls);
        if (wrongLevels)
        {
            Assert.Single(checks);
            Assert.Equal(0, client.HyperBoostMutationCalls);
        }
    }

    [Fact]
    public async Task RunAsync_UnknownCustomBaselineSkipsBeforeAnyPerformanceMutation()
    {
        await using var client = new FakeFirmwareSmokeClient(PerformancePreset.Custom);
        client.ForgetInitialCustomPerformance();

        IReadOnlyList<FirmwareSmokeCheck> checks =
            await new ReversibleSmokeRunner(client).RunAsync(CancellationToken.None);

        Assert.Equal(FirmwareSmokeOutcome.Skipped, checks[0].Outcome);
        Assert.Equal(0, client.PerformanceMutationCalls);
    }

    [Fact]
    public async Task Recovery_CustomSnapshotWithoutSavedLevelsStopsBeforeAnyMutation()
    {
        await using var client = new FakeFirmwareSmokeClient(PerformancePreset.Custom)
        {
            RecoveryFailure = "none",
        };
        client.PrepareRecovery();
        FirmwareSmokeSnapshot baseline = FirmwareSmokeAssessmentTests.CreateSnapshot("1.5.0.0");
        baseline = baseline with { Controls = baseline.Controls with { Performance = "Custom" } };

        IReadOnlyList<FirmwareSmokeCheck> checks = await new FirmwareSmokeRecovery(client)
            .RestoreAsync(baseline, CancellationToken.None);

        Assert.Equal(FirmwareSmokeOutcome.Failed, checks[0].Outcome);
        Assert.Contains("exact Custom CPU/GPU levels", checks[0].Actual, StringComparison.Ordinal);
        Assert.Empty(client.RecoveryMutations);
        Assert.All(checks.Skip(1), check => Assert.Equal(FirmwareSmokeOutcome.Skipped, check.Outcome));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_StaticLightingBaselineRequiresItsSavedOverride(bool retainCandidate)
    {
        await using var client = new FakeFirmwareSmokeClient
        {
            RetainLightingOverrideOnRestore = retainCandidate,
        };
        client.SetInitialLightingOverride(LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 40));

        IReadOnlyList<FirmwareSmokeCheck> checks =
            await new ReversibleSmokeRunner(client).RunAsync(CancellationToken.None);

        FirmwareSmokeCheck lighting = Assert.Single(checks, check => check.Id == "write.lighting");
        Assert.Equal(retainCandidate ? FirmwareSmokeOutcome.Failed : FirmwareSmokeOutcome.Passed, lighting.Outcome);
        Assert.Equal(2, client.LightingMutationCalls);
        if (retainCandidate)
        {
            Assert.Equal("write.lighting", checks[^1].Id);
        }
    }

    [Theory]
    [InlineData("performance", 0)]
    [InlineData("fans", 1)]
    [InlineData("battery", 2)]
    [InlineData("lighting", 3)]
    public async Task Recovery_FailedStageLeavesAllLaterStagesUnattempted(string failure, int failedIndex)
    {
        await using var client = new FakeFirmwareSmokeClient { RecoveryFailure = failure };
        client.PrepareRecovery();

        IReadOnlyList<FirmwareSmokeCheck> checks = await new FirmwareSmokeRecovery(client)
            .RestoreAsync(FirmwareSmokeAssessmentTests.CreateSnapshot("1.5.0.0"), CancellationToken.None);

        Assert.Equal(FirmwareSmokeOutcome.Failed, checks[failedIndex].Outcome);
        Assert.All(checks.Skip(failedIndex + 1), check =>
        {
            Assert.Equal(FirmwareSmokeOutcome.Skipped, check.Outcome);
            Assert.Contains("Not attempted", check.Actual, StringComparison.Ordinal);
        });
        Assert.Equal(failure, client.RecoveryMutations[^1]);
    }

    [Theory]
    [InlineData("StartupAnimation")]
    [InlineData("FunctionKeyPrimary")]
    public async Task Recovery_LaptopControlFailureStopsTheRemainingControls(string failure)
    {
        await using var client = new FakeFirmwareSmokeClient { RecoveryFailure = failure };
        client.PrepareRecovery();

        FirmwareSmokeCheck check = (await new FirmwareSmokeRecovery(client)
            .RestoreAsync(FirmwareSmokeAssessmentTests.CreateSnapshot("1.5.0.0"), CancellationToken.None))[^1];

        Assert.Equal(FirmwareSmokeOutcome.Failed, check.Outcome);
        Assert.Equal(failure, client.RecoveryMutations[^1]);
        Assert.Contains("not attempted", check.Actual, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GamingMode", client.RecoveryMutations);
    }

    [Fact]
    public async Task Recovery_UnavailableStageReadbackStopsAndReportsLaterStages()
    {
        await using var client = new FakeFirmwareSmokeClient
        {
            RecoveryFailure = "none",
            FailCandidateReadback = true,
        };
        client.PrepareRecovery();

        IReadOnlyList<FirmwareSmokeCheck> checks = await new FirmwareSmokeRecovery(client)
            .RestoreAsync(FirmwareSmokeAssessmentTests.CreateSnapshot("1.5.0.0"), CancellationToken.None);

        Assert.Equal(FirmwareSmokeOutcome.Failed, checks[0].Outcome);
        Assert.Contains("IOException", checks[0].Actual, StringComparison.Ordinal);
        Assert.All(checks.Skip(1), check => Assert.Equal(FirmwareSmokeOutcome.Skipped, check.Outcome));
        Assert.Equal(["performance"], client.RecoveryMutations);
    }

    [Fact]
    public async Task Recovery_CancellationRetainsEarlierOutcomesAndStopsLaterStages()
    {
        using var cancellation = new CancellationTokenSource();
        await using var client = new FakeFirmwareSmokeClient
        {
            RecoveryFailure = "none",
            BeforeRecoveryMutation = operation =>
            {
                if (operation == "fans")
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }
            },
        };
        client.PrepareRecovery();

        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FirmwareSmokeRecovery(client).RestoreAsync(
                FirmwareSmokeAssessmentTests.CreateSnapshot("1.5.0.0"), cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Contains("recovery.performance=Passed", error.Message, StringComparison.Ordinal);
        Assert.Contains("Later restorations were not attempted", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("battery", client.RecoveryMutations);
    }

    private sealed class FakeFirmwareSmokeClient : IFirmwareSmokeClient
    {
        private readonly ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        private readonly PerformancePreset originalPerformance;
        private readonly CustomPerformanceSettings? originalCustomPerformance;
        private ServiceStatusSnapshot status;
        private PerformancePreset? hyperBoostRestoreBase;
        private CustomPerformanceSettings? hyperBoostRestoreCustom;

        public FakeFirmwareSmokeClient(
            PerformancePreset originalPerformance = PerformancePreset.Balanced,
            bool padlessAdmitted = true,
            CoolingPadConnectionState coolingPadConnection =
                CoolingPadConnectionState.Absent,
            CustomPerformanceSettings? customPerformance = null)
        {
            this.originalPerformance = originalPerformance;
            originalCustomPerformance = originalPerformance == PerformancePreset.Custom
                ? customPerformance ?? CustomPerformanceSettings.Default
                : null;
            var capabilities = new DeviceCapabilities(
                new HashSet<PerformancePreset>(Enum.GetValues<PerformancePreset>()),
                new HashSet<FanMode>(DeviceCapabilities.SafeTargetDefaults().FanModes),
                new HashSet<LightingEffect>(Enum.GetValues<LightingEffect>()),
                new HashSet<int> { 50, 55, 60, 65, 70, 75, 80 },
                SupportsBatteryProtectionOff: true,
                SupportsTemporaryFullCharge: true)
            {
                HyperBoostAccess = DeviceFeatureAccess.ReadWrite,
                HyperBoostWithoutCoolingPadAccess = padlessAdmitted
                    ? DeviceFeatureAccess.ReadWrite
                    : null,
                HyperBoostBaseModes = new HyperBoostBaseModeCapabilities(
                    originalPerformance == PerformancePreset.Silent
                        ? DeviceFeatureAccess.ReadWrite
                        : DeviceFeatureAccess.ObservedOnly,
                    padlessAdmitted ? DeviceFeatureAccess.ReadWrite : DeviceFeatureAccess.ObservedOnly,
                    originalPerformance == PerformancePreset.Performance
                        ? DeviceFeatureAccess.ReadWrite
                        : DeviceFeatureAccess.ObservedOnly)
                {
                    Custom = originalPerformance == PerformancePreset.Custom
                        ? DeviceFeatureAccess.ReadWrite
                        : DeviceFeatureAccess.ObservedOnly,
                },
                LogoLightingModes =
                    new HashSet<LogoLightingMode>(Enum.GetValues<LogoLightingMode>()),
                OneShotLaptopControls =
                    new HashSet<OneShotLaptopControl>(Enum.GetValues<OneShotLaptopControl>()),
            };
            var confirmed = new ConfirmedDeviceState(
                originalPerformance,
                FanMode.Automatic,
                new ConfirmedBatteryState(true, 80, false),
                Lighting: null)
            {
                LogoLighting = LogoLightingMode.Static,
                CustomPerformance = originalCustomPerformance,
            };
            status = new ServiceStatusSnapshot(
                Revision: 1,
                DeviceVerified: true,
                WritesBlocked: false,
                PotentialControllers: [],
                ControllerDetectionWarning: null,
                ExternalChanges: [],
                Message: "Verified",
                capabilities,
                confirmed,
                profiles,
                new DesiredDeviceState(
                    new ResolvedSetting<PerformancePreset>(
                        originalPerformance,
                        SettingValueSource.PowerProfile),
                    new ResolvedSetting<FanMode>(
                        FanMode.Automatic,
                        SettingValueSource.PowerProfile),
                    new ResolvedSetting<LightingSettings>(
                        LightingSettingsFactory.SpectrumCycling(50),
                        SettingValueSource.PowerProfile),
                    new DesiredBatteryState(BatteryPolicy.Create(80), true, false)),
                ActivePowerSource: PowerSource.PluggedIn,
                LiveReadings: new LiveReadings(0, 0, 0, 45, null),
                PerformanceOverride: null,
                FanOverride: null,
                LightingOverride: null,
                FullChargeOverride: false,
                Firmware: new FirmwareVersions(
                    "3.01",
                    "1.04",
                    "1.5.0.0",
                    "98.03.5C.40.25",
                    VbiosGpuName: "NVIDIA GeForce RTX 5090 Laptop GPU"),
                ServiceVersion: "test",
                LightingRendererOwnership: LightingRendererOwnershipState.Spectrum,
                LightingSuppressions: [],
                StartupAnimationEnabled: true,
                FunctionKeyPrimary: FunctionKeyPrimaryMode.MultimediaKeys,
                GamingModeEnabled: false,
                AlwaysOverrideExternalLightingChanges: false,
                KeyboardDriverModeActive: true);
            status = status with
            {
                DesiredState = status.DesiredState with
                {
                    CustomPerformance = originalCustomPerformance,
                },
                PadlessHyperBoostEligibility = coolingPadConnection switch
                {
                    CoolingPadConnectionState.Absent => PadlessHyperBoostEligibilityState.CoolingPadAbsent,
                    CoolingPadConnectionState.PresentExact => PadlessHyperBoostEligibilityState.CoolingPadPresent,
                    _ => PadlessHyperBoostEligibilityState.Unknown,
                },
            };
        }

        public bool FailFirstPerformanceApply { get; init; }

        public bool ThrowFirstPerformanceApply { get; init; }

        public CancellationTokenSource? CancelFirstPerformanceApply { get; init; }

        public CancellationTokenSource? CancelAfterCandidateReadback { get; init; }

        public bool FailCandidateReadback { get; init; }

        public bool StoreFixedFanWithoutApply { get; init; }

        public bool RetainFanOverrideOnRestore { get; init; }

        public bool RetainLightingOverrideOnRestore { get; init; }

        public int LightingMutationCalls { get; private set; }

        public string? RecoveryFailure { get; init; }

        public Action<string>? BeforeRecoveryMutation { get; init; }

        public List<string> RecoveryMutations { get; } = [];

        public int ObservationCalls { get; private set; }

        public void SetInitialFanOverride(FanControlSettings settings) => status = status with
        {
            ConfirmedState = status.ConfirmedState! with { Fans = settings.Mode },
            FanOverride = new ManualOverride<FanMode>(settings.Mode, OverrideLifetime.UntilPowerSourceChange),
            FanControlOverride = settings,
        };

        public void ForgetInitialCustomPerformance() => status = status with
        {
            ConfirmedState = status.ConfirmedState! with { CustomPerformance = null },
        };

        public void SetInitialLightingOverride(LightingSettings settings) => status = status with
        {
            LightingRendererOwnership = LightingRendererOwnershipState.Static,
            LightingOverride = new ManualOverride<LightingSettings>(settings, OverrideLifetime.UntilPowerSourceChange),
        };

        public void PrepareRecovery() => status = status with
        {
            Firmware = status.Firmware! with { ModelNumber = "RZ09-0581" },
            ConfirmedState = status.ConfirmedState! with
            {
                Performance = PerformancePreset.Silent,
                Fans = FanMode.Fixed,
                Battery = new ConfirmedBatteryState(true, 75, false),
            },
            LightingOverride = new ManualOverride<LightingSettings>(
                LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 15),
                OverrideLifetime.UntilPowerSourceChange),
            StartupAnimationEnabled = false,
            FunctionKeyPrimary = FunctionKeyPrimaryMode.FunctionKeys,
            GamingModeEnabled = true,
        };

        private bool RecoveryFails(string operation)
        {
            if (RecoveryFailure is null)
            {
                return false;
            }
            RecoveryMutations.Add(operation);
            BeforeRecoveryMutation?.Invoke(operation);
            return RecoveryFailure == operation;
        }

        public bool FailFixedFanPreWrite { get; init; }

        public bool IndeterminatePerformanceRestore { get; init; }

        public bool RejectPerformanceRestore { get; init; }

        public bool BlockPerformanceRestoration { get; init; }

        public bool BlockHyperBoostRestoration { get; init; }

        public bool AcknowledgeClearWithRetainedOverride { get; init; }

        public bool WrongCustomPerformanceRestore { get; init; }

        public List<PerformanceOverride?> PerformanceRequests { get; } = [];

        public bool FailHyperBoostEnableKnownNotApplied { get; init; }

        public bool FailHyperBoostEnableAfterApplying { get; init; }

        public bool FailHyperBoostRestore { get; init; }

        public bool RestoreWrongCustomPerformance { get; init; }

        public int PerformanceMutationCalls { get; private set; }

        public int HyperBoostMutationCalls { get; private set; }

        public int FanMutationCalls { get; private set; }

        public List<PerformancePreset> HyperBoostEnableBases { get; } = [];

        public List<PerformancePreset> HyperBoostRestoredBases { get; } = [];

        public Task<ServiceStatusSnapshot> GetStatusAsync(
            CancellationToken cancellationToken) => Task.FromResult(status);

        public Task<ServiceStatusSnapshot> ObserveStateAsync(
            LiveReadingSensors sensors,
            CancellationToken cancellationToken)
        {
            ObservationCalls++;
            if (FailCandidateReadback && PerformanceMutationCalls == 1)
            {
                throw new IOException("Candidate state readback is unavailable.");
            }
            if (PerformanceMutationCalls == 1 && sensors == LiveReadingSensors.None)
            {
                CancelAfterCandidateReadback?.Cancel();
            }
            return Task.FromResult(status);
        }

        public Task<DiagnosticArchive> ExportDiagnosticsAsync(
            bool liveReadingsEnabled,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DiagnosticArchive("test.zip", []));

        public Task<ControlOperationResult> SetPerformanceOverrideAsync(
            PerformanceOverride? value,
            CancellationToken cancellationToken)
        {
            PerformanceMutationCalls++;
            PerformanceRequests.Add(value);
            if (RecoveryFails("performance"))
            {
                return Failed();
            }
            if (FailFirstPerformanceApply && PerformanceMutationCalls == 1)
            {
                return Failed();
            }
            if (RejectPerformanceRestore && PerformanceMutationCalls == 2)
            {
                return Failed();
            }

            PerformancePreset selected = value?.Value ?? originalPerformance;
            status = status with
            {
                ConfirmedState = status.ConfirmedState! with
                {
                    Performance = selected,
                    CustomPerformance = selected != PerformancePreset.Custom ? null
                        : WrongCustomPerformanceRestore && PerformanceMutationCalls == 2
                            ? new CustomPerformanceSettings(CustomPerformanceLevel.Low, CustomPerformanceLevel.Medium)
                            : value?.CustomPerformance ?? originalCustomPerformance,
                },
                PerformanceOverride = AcknowledgeClearWithRetainedOverride && value is null
                    ? status.PerformanceOverride
                    : value,
                WritesBlocked = BlockPerformanceRestoration && PerformanceMutationCalls == 1,
            };
            if (PerformanceMutationCalls == 1)
            {
                if (CancelFirstPerformanceApply is { } cancellation)
                {
                    cancellation.Cancel();
                    throw new OperationCanceledException(cancellation.Token);
                }
                if (ThrowFirstPerformanceApply)
                {
                    throw new IOException("Application completion is unavailable after submission.");
                }
            }
            if (IndeterminatePerformanceRestore
                && PerformanceMutationCalls == 2)
            {
                return Task.FromResult(new ControlOperationResult(
                    ControlOperationStatus.Failed,
                    "Performance write is indeterminate."));
            }

            return Applied();
        }

        public Task<ControlOperationResult> SetHyperBoostAsync(
            bool enabled,
            CancellationToken cancellationToken)
        {
            HyperBoostMutationCalls++;
            if (enabled && FailHyperBoostEnableKnownNotApplied)
            {
                return Failed();
            }

            if (enabled)
            {
                hyperBoostRestoreBase = status.ConfirmedState?.Performance;
                hyperBoostRestoreCustom = status.ConfirmedState?.CustomPerformance;
                if (hyperBoostRestoreBase is { } enabledBase)
                {
                    HyperBoostEnableBases.Add(enabledBase);
                }
            }

            status = status with
            {
                ConfirmedState = status.ConfirmedState! with
                {
                    HyperBoostActive = enabled,
                    Performance = enabled
                        ? null
                        : hyperBoostRestoreBase,
                    CustomPerformance = enabled
                        ? null
                        : RestoreWrongCustomPerformance
                            ? new CustomPerformanceSettings(
                                CustomPerformanceLevel.Low,
                                CustomPerformanceLevel.Medium)
                            : hyperBoostRestoreCustom,
                    Fans = FanMode.Automatic,
                },
                WritesBlocked = enabled && BlockHyperBoostRestoration,
            };
            if (enabled && FailHyperBoostEnableAfterApplying
                || !enabled && FailHyperBoostRestore)
            {
                return Failed();
            }

            if (!enabled && status.ConfirmedState.Performance is { } restoredBase)
            {
                HyperBoostRestoredBases.Add(restoredBase);
            }

            return Applied();
        }

        public Task<ControlOperationResult> SetFanOverrideAsync(
            ManualOverride<FanMode>? value,
            FanControlSettings? settings,
            CancellationToken cancellationToken)
        {
            FanMutationCalls++;
            if (RecoveryFails("fans"))
            {
                return Failed();
            }
            FanMode selected = value?.Value ?? profiles.PluggedIn.Fans;
            if (FailFixedFanPreWrite && selected == FanMode.Fixed)
            {
                return Task.FromResult(new ControlOperationResult(
                    ControlOperationStatus.Failed,
                    "Fan override intent was stored. Hardware result: " +
                    "Fans could not be read before writing: " +
                    "Performance and fan state query failed."));
            }

            status = status with
            {
                ConfirmedState = status.ConfirmedState! with
                {
                    Fans = StoreFixedFanWithoutApply && selected == FanMode.Fixed
                        ? FanMode.Automatic
                        : selected,
                },
                FanOverride = RetainFanOverrideOnRestore && FanMutationCalls == 2 ? status.FanOverride : value,
                FanControlOverride = RetainFanOverrideOnRestore && FanMutationCalls == 2 ? status.FanControlOverride : settings,
            };
            if (StoreFixedFanWithoutApply && selected == FanMode.Fixed)
            {
                return Task.FromResult(new ControlOperationResult(ControlOperationStatus.Stored, "Saved intent only."));
            }
            return Applied();
        }

        public Task<ControlOperationResult> SetLightingOverrideAsync(
            ManualOverride<LightingSettings>? value,
            CancellationToken cancellationToken)
        {
            LightingMutationCalls++;
            if (RecoveryFails("lighting"))
            {
                return Failed();
            }
            status = status with
            {
                LightingOverride = RetainLightingOverrideOnRestore && LightingMutationCalls == 2
                    ? status.LightingOverride
                    : value,
                LightingRendererOwnership = value is null
                    ? LightingRendererOwnershipState.Spectrum
                    : LightingRendererOwnershipState.Static,
            };
            return Applied();
        }

        public Task<ControlOperationResult> SetBatteryPolicyAsync(
            BatteryPolicy value,
            CancellationToken cancellationToken)
        {
            if (RecoveryFails("battery"))
            {
                return Failed();
            }
            status = status with
            {
                ConfirmedState = status.ConfirmedState! with
                {
                    Battery = new ConfirmedBatteryState(
                        protectionEnabled: true,
                        value.ChargeLimitPercent,
                        temporaryFullCharge: false),
                },
            };
            return Applied();
        }

        public Task<ControlOperationResult> ApplyLaptopControlAsync(
            ApplyOneShotLaptopControl value,
            CancellationToken cancellationToken)
        {
            if (RecoveryFails(value.Control.ToString()))
            {
                return Failed();
            }
            status = value.Control switch
            {
                OneShotLaptopControl.StartupAnimation => status with
                {
                    StartupAnimationEnabled = value.Enabled,
                },
                OneShotLaptopControl.FunctionKeyPrimary => status with
                {
                    FunctionKeyPrimary = value.FunctionKeyMode,
                },
                OneShotLaptopControl.GamingMode => status with
                {
                    GamingModeEnabled = value.Enabled,
                },
                _ => status,
            };
            return Applied();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void AssertOriginalState()
        {
            Assert.Equal(originalPerformance, status.ConfirmedState?.Performance);
            Assert.Equal(
                originalCustomPerformance,
                status.ConfirmedState?.CustomPerformance);
            Assert.Equal(FanMode.Automatic, status.ConfirmedState?.Fans);
            Assert.False(status.ConfirmedState?.HyperBoostActive);
            Assert.Equal(80, status.ConfirmedState?.Battery?.ChargeLimitPercent);
            Assert.Equal(LightingRendererOwnershipState.Spectrum, status.LightingRendererOwnership);
            Assert.True(status.StartupAnimationEnabled);
            Assert.Equal(FunctionKeyPrimaryMode.MultimediaKeys, status.FunctionKeyPrimary);
            Assert.False(status.GamingModeEnabled);
            Assert.Null(status.PerformanceOverride);
            Assert.Null(status.FanOverride);
            Assert.Null(status.LightingOverride);
        }

        private static Task<ControlOperationResult> Applied() =>
            Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Applied,
                "Applied"));

        private static Task<ControlOperationResult> Failed() =>
            Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Failed,
                "Failed"));
    }
}
