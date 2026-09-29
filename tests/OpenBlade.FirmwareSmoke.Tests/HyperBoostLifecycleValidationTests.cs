using System.Diagnostics;
using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.FirmwareSmoke;

namespace OpenBlade.FirmwareSmoke.Tests;

public sealed class HyperBoostLifecycleValidationTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        "OpenBlade.HyperBoostLifecycleValidationTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ProcessIdentityProbe_UsesNormalUserInventoryAndFailsClosed()
    {
        using Process process = Process.GetCurrentProcess();

        HyperBoostLifecycleServiceProcess identity = Assert.IsType<
            HyperBoostLifecycleServiceProcess>(
                HyperBoostLifecycleValidationRunner.CaptureProcessIdentity(
                    process.Id));

        Assert.Equal(process.Id, identity.ProcessId);
        Assert.Equal(
            identity,
            HyperBoostLifecycleValidationRunner.CaptureProcessIdentity(process.Id));
        Assert.InRange(
            Math.Abs(
                process.StartTime.ToUniversalTime().Ticks
                - identity.StartUtcTicks),
            0,
            9);
        Assert.True(HyperBoostLifecycleValidationRunner.IsProcessAlive(identity));
        Assert.False(HyperBoostLifecycleValidationRunner.IsProcessAlive(
            identity with { StartUtcTicks = identity.StartUtcTicks + 1 }));
        Assert.Null(HyperBoostLifecycleValidationRunner.CaptureProcessIdentity(
            int.MaxValue));
        Assert.True(HyperBoostLifecycleValidationRunner.IsProcessAlive(
            identity,
            new HyperBoostLifecycleProcessProbe(false, null)));
        Assert.False(HyperBoostLifecycleValidationRunner.IsProcessAlive(
            identity,
            new HyperBoostLifecycleProcessProbe(true, null)));
        Assert.True(HyperBoostLifecycleValidationRunner.IsProcessAlive(
            identity,
            new HyperBoostLifecycleProcessProbe(true, identity)));
        Assert.False(HyperBoostLifecycleValidationRunner.IsProcessAlive(
            identity,
            new HyperBoostLifecycleProcessProbe(
                true,
                identity with { StartUtcTicks = identity.StartUtcTicks + 1 })));
    }

    [Fact]
    public void MarkerIsWellFormed_RejectsMissingServiceProcessWithoutThrowing()
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schemaVersion = HyperBoostLifecycleMarker.CurrentSchemaVersion,
                scenario = HyperBoostLifecycleScenario.UnexpectedExit,
                targetModel = "RZ09-0581",
                installedGenerationSha256 = new string('A', 64),
                baseMode = PerformancePreset.Balanced,
                requiresCoolingPad = false,
                armedAtUtc = DateTimeOffset.UtcNow,
                ownershipJournalSha256 = new string('B', 64),
                restoreIntent = RestoreIntent(),
            },
            FirmwareSmokeFiles.JsonOptions);
        HyperBoostLifecycleMarker marker = Assert.IsType<HyperBoostLifecycleMarker>(
            JsonSerializer.Deserialize<HyperBoostLifecycleMarker>(
                bytes,
                FirmwareSmokeFiles.JsonOptions));

        Assert.False(marker.IsWellFormed);
    }

    [Theory]
    [InlineData(PerformancePreset.BatterySaver)]
    [InlineData(PerformancePreset.BatteryBalanced)]
    public void MarkerIsWellFormed_RejectsBatteryRestoreIntent(
        PerformancePreset performance)
    {
        var marker = new HyperBoostLifecycleMarker(
            HyperBoostLifecycleMarker.CurrentSchemaVersion,
            HyperBoostLifecycleScenario.LowPower,
            "RZ09-0581",
            new string('A', 64),
            PerformancePreset.Balanced,
            RequiresCoolingPad: false,
            DateTimeOffset.UtcNow,
            new HyperBoostLifecycleServiceProcess(42_001, 123_456),
            new string('B', 64),
            RestoreIntent(performance));

        Assert.False(marker.IsWellFormed);
    }

    [Fact]
    public async Task ArmAsync_UsesTypedServiceAndCreatesOwnedMarker()
    {
        Directory.CreateDirectory(directory);
        var client = new FakeClient();
        FirmwareSmokeSnapshot snapshot = Snapshot(client.Status);
        HyperBoostLifecycleJournalObservation journal = OwnedJournal();
        HyperBoostLifecycleServiceProcess process = new(42_001, 123_456);
        HyperBoostLifecycleValidationRunner runner = Runner(
            client,
            snapshot,
            () => process,
            () => journal);
        string markerPath = Path.Combine(directory, "arm.json");

        HyperBoostLifecycleArmResult result = await runner.ArmAsync(
            snapshot,
            HyperBoostLifecycleScenario.LowPower,
            markerPath,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.HyperBoostConfirmed);
        Assert.True(result.DurableOwnershipConfirmed);
        Assert.True(result.MarkerCreated);
        Assert.False(result.CleanupAttempted);
        Assert.Equal(1, client.EnableCalls);
        Assert.Equal(0, client.DisableCalls);
        Assert.True(client.Status.ConfirmedState?.HyperBoostActive);
        Assert.True(File.Exists(markerPath));
        HyperBoostLifecycleMarker marker =
            await FirmwareSmokeFiles.ReadJsonAsync<HyperBoostLifecycleMarker>(
                markerPath,
                CancellationToken.None);
        Assert.True(marker.IsWellFormed);
        Assert.Equal(PerformancePreset.Balanced, marker.BaseMode);
        Assert.False(marker.RequiresCoolingPad);
        Assert.Equal(process, marker.ServiceProcess);
    }

    [Fact]
    public async Task ArmAsync_PreservesBalancedBaseMode()
    {
        Directory.CreateDirectory(directory);
        const PerformancePreset baseMode = PerformancePreset.Balanced;
        var client = new FakeClient(baseMode);
        FirmwareSmokeSnapshot snapshot = Snapshot(client.Status);
        HyperBoostLifecycleValidationRunner runner = Runner(
            client,
            snapshot,
            () => new HyperBoostLifecycleServiceProcess(42_006, 123_463),
            () => OwnedJournal(baseMode));

        HyperBoostLifecycleArmResult result = await runner.ArmAsync(
            snapshot,
            HyperBoostLifecycleScenario.LowPower,
            Path.Combine(directory, $"{baseMode}.json"),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(baseMode, result.Marker!.BaseMode);
        Assert.Equal(1, client.EnableCalls);
    }

    [Fact]
    public async Task ArmAsync_StaleFirmwareSmokeBuildFailsBeforeWrite()
    {
        Directory.CreateDirectory(directory);
        var client = new FakeClient();
        FirmwareSmokeSnapshot snapshot = Snapshot(client.Status);
        HyperBoostLifecycleValidationRunner runner = Runner(
            client,
            snapshot,
            () => new HyperBoostLifecycleServiceProcess(42_015, 123_470),
            () => OwnedJournal(),
            toolProductVersion: "stale");

        HyperBoostLifecycleArmResult result = await runner.ArmAsync(
            snapshot,
            HyperBoostLifecycleScenario.LowPower,
            Path.Combine(directory, "stale-tool.json"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.ValidationAttempted);
        Assert.Equal(0, client.EnableCalls);
        Assert.Equal("stale", result.ToolProductVersion);
        Assert.Equal(
            "The running FirmwareSmoke build must exactly match the installed OpenBlade Service generation.",
            result.Message);
    }

    [Theory]
    [InlineData((int)HyperBoostLifecycleScenario.LowPower)]
    [InlineData((int)HyperBoostLifecycleScenario.UnexpectedExit)]
    public async Task ArmAsync_AdmittedPadlessBalancedCreatesPadlessMarker(
        int scenarioValue)
    {
        Directory.CreateDirectory(directory);
        var client = new FakeClient(
            padlessAdmitted: true,
            coolingPadConnection: CoolingPadConnectionState.Absent);
        FirmwareSmokeSnapshot snapshot = Snapshot(client.Status);
        HyperBoostLifecycleValidationRunner runner = Runner(
            client,
            snapshot,
            () => new HyperBoostLifecycleServiceProcess(42_007, 123_464),
            () => OwnedJournal(requiresCoolingPad: false));
        string markerPath = Path.Combine(directory, $"padless-{scenarioValue}.json");

        HyperBoostLifecycleArmResult result = await runner.ArmAsync(
            snapshot,
            (HyperBoostLifecycleScenario)scenarioValue,
            markerPath,
            CancellationToken.None);

        Assert.True(result.Success);
        HyperBoostLifecycleMarker marker =
            Assert.IsType<HyperBoostLifecycleMarker>(result.Marker);
        Assert.False(marker.RequiresCoolingPad);
        Assert.Equal(PerformancePreset.Balanced, marker.BaseMode);
        Assert.Equal(1, client.EnableCalls);
        Assert.Equal(0, client.DisableCalls);
    }

    [Fact]
    public async Task ArmAsync_ObservedOnlyPadlessUsesValidationBuildRequest()
    {
        Directory.CreateDirectory(directory);
        var client = new FakeClient(
            padlessAdmitted: false,
            padlessObservedOnly: true,
            coolingPadConnection: CoolingPadConnectionState.Absent);
        FirmwareSmokeSnapshot snapshot = Snapshot(client.Status);
        HyperBoostLifecycleValidationRunner runner = Runner(
            client,
            snapshot,
            () => new HyperBoostLifecycleServiceProcess(42_011, 123_468),
            () => OwnedJournal(requiresCoolingPad: false));
        var validationBuild = new PadlessHyperBoostLifecycleValidationBuildManifest(
            1,
            Guid.NewGuid(),
            new string('A', 40),
            new string('B', 64),
            new string('C', 64));

        HyperBoostLifecycleArmResult result = await runner.ArmAsync(
            snapshot,
            HyperBoostLifecycleScenario.LowPower,
            Path.Combine(directory, "validation-build.json"),
            validationBuild,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0, client.EnableCalls);
        Assert.Equal(1, client.ValidationEnableCalls);
        Assert.False(Assert.IsType<HyperBoostLifecycleMarker>(result.Marker)
            .RequiresCoolingPad);
    }

    [Theory]
    [InlineData((int)CoolingPadConnectionState.Ambiguous)]
    [InlineData((int)CoolingPadConnectionState.ObservationFailed)]
    public async Task ArmAsync_PadlessRequiresConfirmedAbsence(int connectionValue)
    {
        Directory.CreateDirectory(directory);
        var client = new FakeClient(
            padlessAdmitted: true,
            coolingPadConnection: (CoolingPadConnectionState)connectionValue);
        FirmwareSmokeSnapshot snapshot = Snapshot(client.Status);
        HyperBoostLifecycleValidationRunner runner = Runner(
            client,
            snapshot,
            () => new HyperBoostLifecycleServiceProcess(42_008, 123_465),
            () => OwnedJournal(requiresCoolingPad: false));
        string markerPath = Path.Combine(directory, $"blocked-{connectionValue}.json");

        HyperBoostLifecycleArmResult result = await runner.ArmAsync(
            snapshot,
            HyperBoostLifecycleScenario.LowPower,
            markerPath,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(0, client.EnableCalls);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task ArmAsync_PadlessRequiresReadWriteAdmission()
    {
        Directory.CreateDirectory(directory);
        var client = new FakeClient(
            padlessAdmitted: false,
            coolingPadConnection: CoolingPadConnectionState.Absent);
        FirmwareSmokeSnapshot snapshot = Snapshot(client.Status);
        HyperBoostLifecycleValidationRunner runner = Runner(
            client,
            snapshot,
            () => new HyperBoostLifecycleServiceProcess(42_010, 123_467),
            () => OwnedJournal(requiresCoolingPad: false));
        string markerPath = Path.Combine(directory, "blocked-observed-only.json");

        HyperBoostLifecycleArmResult result = await runner.ArmAsync(
            snapshot,
            HyperBoostLifecycleScenario.LowPower,
            markerPath,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(0, client.EnableCalls);
        Assert.False(File.Exists(markerPath));
    }

    [Theory]
    [MemberData(nameof(FullPowerProfileFanMatrix))]
    public async Task ArmAsync_PadlessAllowsEveryFullPowerProfileAndFanIntent(
        PerformancePreset performance,
        FanMode fans)
    {
        Directory.CreateDirectory(directory);
        var client = new FakeClient(
            performance,
            fans,
            padlessAdmitted: true,
            coolingPadConnection: CoolingPadConnectionState.Absent);
        FirmwareSmokeSnapshot snapshot = Snapshot(client.Status);
        HyperBoostLifecycleValidationRunner runner = Runner(
            client,
            snapshot,
            () => new HyperBoostLifecycleServiceProcess(42_009, 123_466),
            () => OwnedJournal(
                performance,
                fans,
                requiresCoolingPad: false));
        string markerPath = Path.Combine(directory, $"allowed-{performance}-{fans}.json");

        HyperBoostLifecycleArmResult result = await runner.ArmAsync(
            snapshot,
            HyperBoostLifecycleScenario.LowPower,
            markerPath,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(PerformancePreset.Balanced, result.Marker!.BaseMode);
        Assert.Equal(RestoreIntent(performance, fans), result.Marker.RestoreIntent);
        Assert.Equal(1, client.EnableCalls);
        Assert.True(File.Exists(markerPath));
    }

    public static TheoryData<PerformancePreset, FanMode>
        FullPowerProfileFanMatrix
    {
        get
        {
            var values = new TheoryData<PerformancePreset, FanMode>();
            foreach (PerformancePreset performance in new[]
            {
                PerformancePreset.Silent,
                PerformancePreset.Balanced,
                PerformancePreset.Performance,
                PerformancePreset.Custom,
            })
            {
                foreach (FanMode fans in Enum.GetValues<FanMode>())
                {
                    values.Add(performance, fans);
                }
            }

            return values;
        }
    }

    [Fact]
    public async Task ArmAsync_MissingOwnedJournalRequiresRecoveryWithoutWrite()
    {
        Directory.CreateDirectory(directory);
        var client = new FakeClient();
        FirmwareSmokeSnapshot snapshot = Snapshot(client.Status);
        HyperBoostLifecycleValidationRunner runner = Runner(
            client,
            snapshot,
            () => new HyperBoostLifecycleServiceProcess(42_002, 123_457),
            () => HyperBoostLifecycleJournalObservation.Missing);
        string markerPath = Path.Combine(directory, "missing-journal.json");

        HyperBoostLifecycleArmResult result = await runner.ArmAsync(
            snapshot,
            HyperBoostLifecycleScenario.UnexpectedExit,
            markerPath,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.CleanupAttempted);
        Assert.False(result.FinalRestorationConfirmed);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(1, client.EnableCalls);
        Assert.Equal(0, client.DisableCalls);
        Assert.True(client.Status.ConfirmedState?.HyperBoostActive);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task ArmAsync_IndeterminateApplyInspectsWithoutCleanupWrite()
    {
        Directory.CreateDirectory(directory);
        var client = new FakeClient { ThrowAfterEnable = true };
        FirmwareSmokeSnapshot snapshot = Snapshot(client.Status);
        HyperBoostLifecycleValidationRunner runner = Runner(
            client,
            snapshot,
            () => new HyperBoostLifecycleServiceProcess(42_005, 123_462),
            () => OwnedJournal());

        HyperBoostLifecycleArmResult result = await runner.ArmAsync(
            snapshot,
            HyperBoostLifecycleScenario.LowPower,
            Path.Combine(directory, "indeterminate.json"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.HyperBoostConfirmed);
        Assert.False(result.CleanupAttempted);
        Assert.False(result.FinalRestorationConfirmed);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(1, client.EnableCalls);
        Assert.Equal(0, client.DisableCalls);
        Assert.True(client.Status.ConfirmedState?.HyperBoostActive);
    }

    [Fact]
    public async Task ArmAsync_FailedResultWithConfirmedOwnershipCreatesMarkerWithoutReplay()
    {
        Directory.CreateDirectory(directory);
        var client = new FakeClient { FailResultAfterConfirmedEnable = true };
        FirmwareSmokeSnapshot snapshot = Snapshot(client.Status);
        HyperBoostLifecycleValidationRunner runner = Runner(
            client,
            snapshot,
            () => new HyperBoostLifecycleServiceProcess(42_016, 123_471),
            () => OwnedJournal());
        string markerPath = Path.Combine(directory, "failed-confirmed-owned.json");

        HyperBoostLifecycleArmResult result = await runner.ArmAsync(
            snapshot,
            HyperBoostLifecycleScenario.UnexpectedExit,
            markerPath,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.HyperBoostConfirmed);
        Assert.True(result.DurableOwnershipConfirmed);
        Assert.True(result.MarkerCreated);
        Assert.False(result.CleanupAttempted);
        Assert.Equal(1, client.EnableCalls);
        Assert.Equal(0, client.DisableCalls);
        Assert.True(client.Status.ConfirmedState?.HyperBoostActive);
        Assert.True(File.Exists(markerPath));
    }

    [Fact]
    public async Task ArmAsync_FailedResultWithoutConfirmedOwnershipDoesNotWrite()
    {
        Directory.CreateDirectory(directory);
        var client = new FakeClient { FailResultAfterConfirmedEnable = true };
        FirmwareSmokeSnapshot snapshot = Snapshot(client.Status);
        HyperBoostLifecycleValidationRunner runner = Runner(
            client,
            snapshot,
            () => new HyperBoostLifecycleServiceProcess(42_017, 123_472),
            () => HyperBoostLifecycleJournalObservation.Missing);
        string markerPath = Path.Combine(directory, "failed-confirmed-unowned.json");

        HyperBoostLifecycleArmResult result = await runner.ArmAsync(
            snapshot,
            HyperBoostLifecycleScenario.UnexpectedExit,
            markerPath,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.HyperBoostConfirmed);
        Assert.False(result.DurableOwnershipConfirmed);
        Assert.False(result.MarkerCreated);
        Assert.False(result.CleanupAttempted);
        Assert.False(result.FinalRestorationConfirmed);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(1, client.EnableCalls);
        Assert.Equal(0, client.DisableCalls);
        Assert.True(client.Status.ConfirmedState?.HyperBoostActive);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task ArmAsync_BlockedResultDoesNotClaimConcurrentOwnedActivation()
    {
        Directory.CreateDirectory(directory);
        var client = new FakeClient
        {
            BlockEnableAndActivateExternallyOnNextObservation = true,
        };
        FirmwareSmokeSnapshot snapshot = Snapshot(client.Status);
        HyperBoostLifecycleValidationRunner runner = Runner(
            client,
            snapshot,
            () => new HyperBoostLifecycleServiceProcess(42_018, 123_473),
            () => OwnedJournal());
        string markerPath = Path.Combine(directory, "blocked-concurrent-owned.json");

        HyperBoostLifecycleArmResult result = await runner.ArmAsync(
            snapshot,
            HyperBoostLifecycleScenario.UnexpectedExit,
            markerPath,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.ValidationAttempted);
        Assert.False(result.HyperBoostConfirmed);
        Assert.False(result.MarkerCreated);
        Assert.False(result.CleanupAttempted);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(1, client.EnableCalls);
        Assert.Equal(0, client.DisableCalls);
        Assert.Equal(1, client.ObserveCalls);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task ArmAsync_FailedResultDoesNotClaimConcurrentOwnedActivation()
    {
        Directory.CreateDirectory(directory);
        var client = new FakeClient
        {
            FailEnableAndActivateExternallyOnNextObservation = true,
        };
        FirmwareSmokeSnapshot snapshot = Snapshot(client.Status);
        HyperBoostLifecycleValidationRunner runner = Runner(
            client,
            snapshot,
            () => new HyperBoostLifecycleServiceProcess(42_019, 123_474),
            () => OwnedJournal() with { TransitionId = Guid.NewGuid() });
        string markerPath = Path.Combine(directory, "failed-concurrent-owned.json");

        HyperBoostLifecycleArmResult result = await runner.ArmAsync(
            snapshot,
            HyperBoostLifecycleScenario.UnexpectedExit,
            markerPath,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.ValidationAttempted);
        Assert.False(result.MarkerCreated);
        Assert.False(result.CleanupAttempted);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(1, client.EnableCalls);
        Assert.Equal(0, client.DisableCalls);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task ArmAsync_InactiveFailedEntryRequiresExactRestoreConfirmation()
    {
        Directory.CreateDirectory(directory);
        var client = new FakeClient(PerformancePreset.Silent)
        {
            FailEnableAfterTransitionBaseline = true,
        };
        FirmwareSmokeSnapshot snapshot = Snapshot(client.Status);
        HyperBoostLifecycleValidationRunner runner = Runner(
            client,
            snapshot,
            () => new HyperBoostLifecycleServiceProcess(42_011, 123_468),
            () => HyperBoostLifecycleJournalObservation.Missing);

        HyperBoostLifecycleArmResult result = await runner.ArmAsync(
            snapshot,
            HyperBoostLifecycleScenario.LowPower,
            Path.Combine(directory, "inactive-failed-entry.json"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.HyperBoostConfirmed);
        Assert.False(result.CleanupAttempted);
        Assert.False(result.FinalRestorationConfirmed);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(1, client.EnableCalls);
        Assert.Equal(0, client.DisableCalls);
        Assert.Equal(
            PerformancePreset.Balanced,
            client.Status.ConfirmedState?.Performance);
        Assert.Equal(FanMode.Automatic, client.Status.ConfirmedState?.Fans);
        Assert.False(client.Status.ConfirmedState?.HyperBoostActive);
    }

    [Fact]
    public async Task VerifyAsync_LowPowerRequiresSameProcessOneRestoreAndRetiredJournal()
    {
        Directory.CreateDirectory(directory);
        var client = new FakeClient();
        FirmwareSmokeSnapshot snapshot = Snapshot(client.Status);
        HyperBoostLifecycleServiceProcess process = new(42_003, 123_458);
        HyperBoostLifecycleJournalObservation journal = OwnedJournal();
        IReadOnlyList<HyperBoostLifecycleDiagnosticResult> diagnostics = [];
        HyperBoostLifecycleValidationRunner runner = Runner(
            client,
            snapshot,
            () => process,
            () => journal,
            () => diagnostics);
        string markerPath = Path.Combine(directory, "low-power.json");
        HyperBoostLifecycleArmResult armed = await runner.ArmAsync(
            snapshot,
            HyperBoostLifecycleScenario.LowPower,
            markerPath,
            CancellationToken.None);
        Assert.True(armed.Success);
        client.RestoreWithoutClientWrite();
        journal = HyperBoostLifecycleJournalObservation.Missing;
        diagnostics = RecoveryDiagnostics(
            armed.Marker!.ArmedAtUtc,
            "before low power",
            armed.Marker.TransitionId);

        HyperBoostLifecycleVerifyResult result = await runner.VerifyAsync(
            snapshot,
            HyperBoostLifecycleScenario.LowPower,
            markerPath,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.ExpectedProcessBoundary);
        Assert.True(result.ExactRestoreIntentConfirmed);
        Assert.True(result.JournalRetired);
        Assert.Equal(1, result.RecoveryAttemptsObserved);
        Assert.Equal(1, result.RecoveryCompletionsObserved);
        Assert.True(result.StableSecondObservation);
        Assert.Equal(0, client.DisableCalls);
    }

    [Fact]
    public async Task VerifyAsync_RejectsRecoveryEvidenceFromAnotherTransition()
    {
        Directory.CreateDirectory(directory);
        var client = new FakeClient();
        FirmwareSmokeSnapshot snapshot = Snapshot(client.Status);
        HyperBoostLifecycleServiceProcess process = new(42_015, 123_470);
        HyperBoostLifecycleJournalObservation journal = OwnedJournal();
        IReadOnlyList<HyperBoostLifecycleDiagnosticResult> diagnostics = [];
        HyperBoostLifecycleValidationRunner runner = Runner(
            client,
            snapshot,
            () => process,
            () => journal,
            () => diagnostics);
        string markerPath = Path.Combine(directory, "substituted-transition.json");
        HyperBoostLifecycleArmResult armed = await runner.ArmAsync(
            snapshot,
            HyperBoostLifecycleScenario.LowPower,
            markerPath,
            CancellationToken.None);
        Assert.True(armed.Success);
        client.RestoreWithoutClientWrite();
        journal = HyperBoostLifecycleJournalObservation.Missing;
        diagnostics = RecoveryDiagnostics(
            armed.Marker!.ArmedAtUtc,
            "before low power",
            Guid.NewGuid());

        HyperBoostLifecycleVerifyResult result = await runner.VerifyAsync(
            snapshot,
            HyperBoostLifecycleScenario.LowPower,
            markerPath,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(0, result.RecoveryAttemptsObserved);
        Assert.Equal(0, result.RecoveryCompletionsObserved);
        Assert.Equal(0, client.DisableCalls);
    }

    [Fact]
    public async Task VerifyAsync_LowPowerAcceptsModernStandbyDisplayOffFallback()
    {
        Directory.CreateDirectory(directory);
        var client = new FakeClient();
        FirmwareSmokeSnapshot snapshot = Snapshot(client.Status);
        HyperBoostLifecycleServiceProcess process = new(42_013, 123_468);
        HyperBoostLifecycleJournalObservation journal = OwnedJournal();
        IReadOnlyList<HyperBoostLifecycleDiagnosticResult> diagnostics = [];
        HyperBoostLifecycleValidationRunner runner = Runner(
            client,
            snapshot,
            () => process,
            () => journal,
            () => diagnostics);
        string markerPath = Path.Combine(directory, "low-power-display-off.json");
        HyperBoostLifecycleArmResult armed = await runner.ArmAsync(
            snapshot,
            HyperBoostLifecycleScenario.LowPower,
            markerPath,
            CancellationToken.None);
        Assert.True(armed.Success);
        client.RestoreWithoutClientWrite();
        journal = HyperBoostLifecycleJournalObservation.Missing;
        diagnostics = RecoveryDiagnostics(
            armed.Marker!.ArmedAtUtc,
            "before display-off safety boundary",
            armed.Marker.TransitionId);

        HyperBoostLifecycleVerifyResult result = await runner.VerifyAsync(
            snapshot,
            HyperBoostLifecycleScenario.LowPower,
            markerPath,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.RecoveryAttemptsObserved);
        Assert.Equal(1, result.RecoveryCompletionsObserved);
        Assert.Equal(0, client.DisableCalls);
    }

    [Fact]
    public async Task VerifyAsync_UnexpectedExitRequiresReplacementProcessAndExactlyOnce()
    {
        Directory.CreateDirectory(directory);
        var client = new FakeClient();
        FirmwareSmokeSnapshot snapshot = Snapshot(client.Status);
        HyperBoostLifecycleServiceProcess process = new(int.MaxValue, 123_459);
        HyperBoostLifecycleJournalObservation journal = OwnedJournal();
        IReadOnlyList<HyperBoostLifecycleDiagnosticResult> diagnostics = [];
        HyperBoostLifecycleValidationRunner runner = Runner(
            client,
            snapshot,
            () => process,
            () => journal,
            () => diagnostics);
        string markerPath = Path.Combine(directory, "crash.json");
        HyperBoostLifecycleArmResult armed = await runner.ArmAsync(
            snapshot,
            HyperBoostLifecycleScenario.UnexpectedExit,
            markerPath,
            CancellationToken.None);
        Assert.True(armed.Success);
        process = new HyperBoostLifecycleServiceProcess(int.MaxValue - 1, 123_460);
        client.RestoreWithoutClientWrite();
        journal = HyperBoostLifecycleJournalObservation.Missing;
        diagnostics = RecoveryDiagnostics(
            armed.Marker!.ArmedAtUtc,
            "before unexpected-exit recovery",
            armed.Marker.TransitionId);

        HyperBoostLifecycleVerifyResult result = await runner.VerifyAsync(
            snapshot,
            HyperBoostLifecycleScenario.UnexpectedExit,
            markerPath,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.True(result.ExpectedProcessBoundary);
        Assert.Equal(1, result.RecoveryAttemptsObserved);
        Assert.Equal(1, result.RecoveryCompletionsObserved);
        Assert.Equal(0, client.DisableCalls);
    }

    [Fact]
    public async Task VerifyAsync_DuplicateRecoveryEvidenceFailsClosedWithoutWrite()
    {
        Directory.CreateDirectory(directory);
        var client = new FakeClient();
        FirmwareSmokeSnapshot snapshot = Snapshot(client.Status);
        HyperBoostLifecycleServiceProcess process = new(42_004, 123_461);
        HyperBoostLifecycleJournalObservation journal = OwnedJournal();
        IReadOnlyList<HyperBoostLifecycleDiagnosticResult> diagnostics = [];
        HyperBoostLifecycleValidationRunner runner = Runner(
            client,
            snapshot,
            () => process,
            () => journal,
            () => diagnostics);
        string markerPath = Path.Combine(directory, "duplicate.json");
        HyperBoostLifecycleArmResult armed = await runner.ArmAsync(
            snapshot,
            HyperBoostLifecycleScenario.LowPower,
            markerPath,
            CancellationToken.None);
        client.RestoreWithoutClientWrite();
        journal = HyperBoostLifecycleJournalObservation.Missing;
        diagnostics = RecoveryDiagnostics(
            armed.Marker!.ArmedAtUtc,
            "before low power",
            armed.Marker.TransitionId).Concat(
                RecoveryDiagnostics(
                    armed.Marker.ArmedAtUtc.AddMilliseconds(1),
                    "before low power",
                    armed.Marker.TransitionId))
            .ToArray();

        HyperBoostLifecycleVerifyResult result = await runner.VerifyAsync(
            snapshot,
            HyperBoostLifecycleScenario.LowPower,
            markerPath,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(2, result.RecoveryAttemptsObserved);
        Assert.Equal(2, result.RecoveryCompletionsObserved);
        Assert.Equal(0, client.DisableCalls);
    }

    [Fact]
    public async Task VerifyAsync_LowPowerRejectsSuspendAndDisplayOffDuplicateRecovery()
    {
        Directory.CreateDirectory(directory);
        var client = new FakeClient();
        FirmwareSmokeSnapshot snapshot = Snapshot(client.Status);
        HyperBoostLifecycleServiceProcess process = new(42_014, 123_469);
        HyperBoostLifecycleJournalObservation journal = OwnedJournal();
        IReadOnlyList<HyperBoostLifecycleDiagnosticResult> diagnostics = [];
        HyperBoostLifecycleValidationRunner runner = Runner(
            client,
            snapshot,
            () => process,
            () => journal,
            () => diagnostics);
        string markerPath = Path.Combine(directory, "duplicate-boundaries.json");
        HyperBoostLifecycleArmResult armed = await runner.ArmAsync(
            snapshot,
            HyperBoostLifecycleScenario.LowPower,
            markerPath,
            CancellationToken.None);
        client.RestoreWithoutClientWrite();
        journal = HyperBoostLifecycleJournalObservation.Missing;
        diagnostics = RecoveryDiagnostics(
            armed.Marker!.ArmedAtUtc,
            "before low power",
            armed.Marker.TransitionId).Concat(
                RecoveryDiagnostics(
                    armed.Marker.ArmedAtUtc.AddMilliseconds(3),
                    "before display-off safety boundary",
                    armed.Marker.TransitionId))
            .ToArray();

        HyperBoostLifecycleVerifyResult result = await runner.VerifyAsync(
            snapshot,
            HyperBoostLifecycleScenario.LowPower,
            markerPath,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(2, result.RecoveryAttemptsObserved);
        Assert.Equal(2, result.RecoveryCompletionsObserved);
        Assert.Equal(0, client.DisableCalls);
    }

    [Fact]
    public void ValidateInstalledGeneration_AllowsExpectedFanRecoveryDiagnostic()
    {
        FirmwareSmokeSnapshot baseline =
            FirmwareSmokeAssessmentTests.CreateSnapshot("1.5.0.0");
        FirmwareSmokeSnapshot current = baseline with
        {
            ExternalChanges = [DeviceSubsystem.Fans.ToString()],
        };

        Assert.True(HyperBoostLifecycleValidationRunner.ValidateInstalledGeneration(
            baseline,
            current));
    }

    [Fact]
    public void ValidateInstalledGeneration_AllowsAdmittedPadlessLifecycleCapability()
    {
        FirmwareSmokeSnapshot baseline =
            FirmwareSmokeAssessmentTests.CreateSnapshot("1.5.0.0");

        Assert.Equal(
            DeviceFeatureAccess.ReadWrite.ToString(),
            baseline.Capabilities.HyperBoostWithoutCoolingPadAccess);
        Assert.True(HyperBoostLifecycleValidationRunner.ValidateInstalledGeneration(
            baseline,
            baseline));
    }

    [Fact]
    public void ValidateInstalledGeneration_RejectsOtherExternalChange()
    {
        FirmwareSmokeSnapshot baseline =
            FirmwareSmokeAssessmentTests.CreateSnapshot("1.5.0.0");
        FirmwareSmokeSnapshot current = baseline with
        {
            ExternalChanges = [DeviceSubsystem.Performance.ToString()],
        };

        Assert.False(HyperBoostLifecycleValidationRunner.ValidateInstalledGeneration(
            baseline,
            current));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public void ParseOwnershipJournal_AcceptsTypedRestoreIntent(int schemaVersion)
    {
        HyperBoostLifecycleRestoreIntent expected = RestoreIntent(
            PerformancePreset.Custom,
            FanMode.ManualCurve);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schemaVersion,
                state = 1,
                baseMode = (int)PerformancePreset.Balanced,
                updatedAtUtc = DateTimeOffset.UtcNow,
                requiresCoolingPad = false,
                restorePerformance = (int)expected.Performance,
                restoreCustomPerformance = expected.CustomPerformance,
                restoreGpuClockTuning = expected.GpuClockTuning,
                restoreFanControl = expected.FanControl,
            },
            FirmwareSmokeFiles.JsonOptions);

        HyperBoostLifecycleJournalObservation observation =
            HyperBoostLifecycleValidationRunner.ParseOwnershipJournal(bytes);

        Assert.True(observation.Valid);
        Assert.Equal("OwnedConfirmed", observation.State);
        Assert.Equal(PerformancePreset.Balanced, observation.BaseMode);
        Assert.Equal(expected, observation.RestoreIntent);
        Assert.False(observation.RequiresCoolingPad);
    }

    [Theory]
    [InlineData(PerformancePreset.BatterySaver)]
    [InlineData(PerformancePreset.BatteryBalanced)]
    public void ParseOwnershipJournal_AcceptsBatteryRestoreAndRecoveryState(
        PerformancePreset performance)
    {
        byte[] bytes = SchemaFourJournalBytes(
            state: 2,
            recoveryBaseline: new
            {
                performance = (int?)performance,
                fans = (int)FanMode.Automatic,
                hyperBoostActive = false,
            },
            restorePerformance: performance);

        HyperBoostLifecycleJournalObservation observation =
            HyperBoostLifecycleValidationRunner.ParseOwnershipJournal(bytes);

        Assert.True(observation.Valid);
        Assert.Equal("RestorePending", observation.State);
        Assert.Equal(performance, observation.RestoreIntent?.Performance);
    }

    [Fact]
    public void ParseOwnershipJournal_RejectsUnknownFutureSchema()
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schemaVersion = 8,
                state = 1,
                baseMode = (int)PerformancePreset.Balanced,
                updatedAtUtc = DateTimeOffset.UtcNow,
                requiresCoolingPad = false,
                restorePerformance = (int)PerformancePreset.Balanced,
                restoreCustomPerformance = (CustomPerformanceSettings?)null,
                restoreGpuClockTuning = (GpuClockTuningSettings?)null,
                restoreFanControl = FanControlSettings.Default,
            },
            FirmwareSmokeFiles.JsonOptions);

        HyperBoostLifecycleJournalObservation observation =
            HyperBoostLifecycleValidationRunner.ParseOwnershipJournal(bytes);

        Assert.False(observation.Valid);
        Assert.Null(observation.BaseMode);
        Assert.Null(observation.RestoreIntent);
    }

    [Fact]
    public void ParseOwnershipJournal_RejectsRecoveryBaselineOnOwnedSchemaFourJournal()
    {
        byte[] bytes = SchemaFourJournalBytes(
            state: 1,
            recoveryBaseline: new
            {
                performance = (int?)null,
                fans = (int)FanMode.Maximum,
                hyperBoostActive = false,
            });

        HyperBoostLifecycleJournalObservation observation =
            HyperBoostLifecycleValidationRunner.ParseOwnershipJournal(bytes);

        Assert.False(observation.Valid);
        Assert.Null(observation.BaseMode);
        Assert.Null(observation.RestoreIntent);
    }

    [Fact]
    public void ParseOwnershipJournal_AcceptsRecoveryBaselineOnRestorePendingSchemaFourJournal()
    {
        byte[] bytes = SchemaFourJournalBytes(
            state: 2,
            recoveryBaseline: new
            {
                performance = (int?)null,
                fans = (int)FanMode.Maximum,
                hyperBoostActive = false,
            });

        HyperBoostLifecycleJournalObservation observation =
            HyperBoostLifecycleValidationRunner.ParseOwnershipJournal(bytes);

        Assert.True(observation.Valid);
        Assert.Equal("RestorePending", observation.State);
        Assert.Equal(PerformancePreset.Balanced, observation.BaseMode);
    }

    [Fact]
    public void ParseOwnershipJournal_RejectsDefaultUpdateTimestamp()
    {
        byte[] bytes = SchemaFourJournalBytes(
            state: 1,
            recoveryBaseline: null,
            updatedAtUtc: DateTimeOffset.MinValue);

        HyperBoostLifecycleJournalObservation observation =
            HyperBoostLifecycleValidationRunner.ParseOwnershipJournal(bytes);

        Assert.False(observation.Valid);
    }

    [Fact]
    public void ParseOwnershipJournalDiagnostics_AcceptsServiceOwnedJournal()
    {
        HyperBoostLifecycleRestoreIntent expected = RestoreIntent(
            PerformancePreset.Silent,
            FanMode.ManualCurve);
        Guid transitionId = Guid.Parse("fd61ce01-f77c-445d-92a1-2b124646d640");
        string serviceJson = JsonSerializer.Serialize(
            new
            {
                exists = true,
                valid = true,
                state = "OwnedConfirmed",
                baseMode = PerformancePreset.Balanced,
                requiresCoolingPad = false,
                restoreIntent = expected,
                sha256 = new string('A', 64),
                transitionId,
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        HyperBoostLifecycleJournalDiagnostic? diagnostic =
            JsonSerializer.Deserialize<HyperBoostLifecycleJournalDiagnostic>(
                serviceJson,
                FirmwareSmokeFiles.JsonOptions);

        HyperBoostLifecycleJournalObservation observation =
            HyperBoostLifecycleValidationRunner.ParseOwnershipJournalDiagnostics(
                diagnostic);

        Assert.True(observation.Valid);
        Assert.Equal("OwnedConfirmed", observation.State);
        Assert.Equal(PerformancePreset.Balanced, observation.BaseMode);
        Assert.Equal(expected, observation.RestoreIntent);
        Assert.False(observation.RequiresCoolingPad);
        Assert.Equal(new string('A', 64), observation.Sha256);
        Assert.Equal(transitionId, observation.TransitionId);
    }

    [Fact]
    public void ParseOwnershipJournalDiagnostics_AcceptsConfirmedAbsence()
    {
        HyperBoostLifecycleJournalObservation observation =
            HyperBoostLifecycleValidationRunner.ParseOwnershipJournalDiagnostics(
                new HyperBoostLifecycleJournalDiagnostic(
                    Exists: false,
                    Valid: true,
                    State: null,
                    BaseMode: null,
                    RequiresCoolingPad: null,
                    RestoreIntent: null,
                    Sha256: null));

        Assert.Equal(HyperBoostLifecycleJournalObservation.Missing, observation);
    }

    [Fact]
    public void ParseOwnershipJournalDiagnostics_FailsClosedWhenEvidenceIsIncomplete()
    {
        HyperBoostLifecycleJournalObservation observation =
            HyperBoostLifecycleValidationRunner.ParseOwnershipJournalDiagnostics(
                new HyperBoostLifecycleJournalDiagnostic(
                    Exists: true,
                    Valid: true,
                    State: "OwnedConfirmed",
                    BaseMode: PerformancePreset.Balanced,
                    RequiresCoolingPad: false,
                    RestoreIntent: null,
                    Sha256: new string('A', 64)));

        Assert.True(observation.Exists);
        Assert.False(observation.Valid);
    }

    [Theory]
    [InlineData("low-power", (int)HyperBoostLifecycleScenario.LowPower)]
    [InlineData("crash", (int)HyperBoostLifecycleScenario.UnexpectedExit)]
    [InlineData("unexpected-exit", (int)HyperBoostLifecycleScenario.UnexpectedExit)]
    public void ParseScenario_AcceptsExplicitLifecycleNames(
        string value,
        int expectedValue)
    {
        var expected = (HyperBoostLifecycleScenario)expectedValue;
        Assert.Equal(
            expected,
            FirmwareSmokeProgram.ParseHyperBoostLifecycleScenario(value));
        Assert.Equal(
            expected == HyperBoostLifecycleScenario.LowPower
                ? "RZ09-0581-HYPERBOOST-LOW-POWER-LIFECYCLE"
                : "RZ09-0581-HYPERBOOST-UNEXPECTED-EXIT-LIFECYCLE",
            FirmwareSmokeProgram.HyperBoostLifecycleConfirmation(
                "RZ09-0581",
                expected));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] SchemaFourJournalBytes(
        int state,
        object? recoveryBaseline,
        DateTimeOffset? updatedAtUtc = null,
        PerformancePreset restorePerformance = PerformancePreset.Balanced) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                schemaVersion = 4,
                state,
                baseMode = (int)PerformancePreset.Balanced,
                updatedAtUtc = updatedAtUtc ?? DateTimeOffset.UtcNow,
                requiresCoolingPad = false,
                restorePerformance = (int)restorePerformance,
                restoreCustomPerformance = (CustomPerformanceSettings?)null,
                restoreGpuClockTuning = (GpuClockTuningSettings?)null,
                restoreFanControl = FanControlSettings.Default,
                recoveryBaseline,
            },
            FirmwareSmokeFiles.JsonOptions);

    // RZ09-0528 base-mode access mirrors the exact model catalog: only the Performance transition is writable.
    private static HyperBoostBaseModeCapabilities Rz090528BaseModes() => new(
        DeviceFeatureAccess.ObservedOnly,
        DeviceFeatureAccess.ObservedOnly,
        DeviceFeatureAccess.ReadWrite);

    [Fact]
    public async Task ArmAsync_Rz090528UsesExactPerformanceTransition()
    {
        Directory.CreateDirectory(directory);
        var client = new FakeClient(PerformancePreset.Performance, baseModes: Rz090528BaseModes());
        FirmwareSmokeSnapshot snapshot = Snapshot(client.Status, "RZ09-0528");
        HyperBoostLifecycleValidationRunner runner = Runner(
            client,
            snapshot,
            () => new HyperBoostLifecycleServiceProcess(42_101, 123_501),
            () => OwnedJournal(PerformancePreset.Performance, journalBase: PerformancePreset.Performance));

        HyperBoostLifecycleArmResult result = await runner.ArmAsync(
            snapshot,
            HyperBoostLifecycleScenario.UnexpectedExit,
            Path.Combine(directory, "rz090528.json"),
            CancellationToken.None);

        Assert.True(result.Success);
        HyperBoostLifecycleMarker marker = Assert.IsType<HyperBoostLifecycleMarker>(result.Marker);
        Assert.Equal(PerformancePreset.Performance, marker.BaseMode);
        Assert.Equal(PerformancePreset.Performance, marker.RestoreIntent.Performance);
        Assert.True(marker.IsWellFormed);
        Assert.Equal(1, client.EnableCalls);
    }

    [Theory]
    [InlineData(PerformancePreset.Balanced, FanMode.Automatic)]
    [InlineData(PerformancePreset.Silent, FanMode.Automatic)]
    [InlineData(PerformancePreset.Custom, FanMode.Automatic)]
    [InlineData(PerformancePreset.Performance, FanMode.ManualCurve)]
    [InlineData(PerformancePreset.Performance, FanMode.Fixed)]
    public async Task ArmAsync_Rz090528RefusesUncapturedEntryIntentBeforeAnyWrite(
        PerformancePreset performance,
        FanMode fans)
    {
        Directory.CreateDirectory(directory);
        var client = new FakeClient(performance, fans, baseModes: Rz090528BaseModes());
        FirmwareSmokeSnapshot snapshot = Snapshot(client.Status, "RZ09-0528");
        HyperBoostLifecycleValidationRunner runner = Runner(
            client,
            snapshot,
            () => new HyperBoostLifecycleServiceProcess(42_102, 123_502),
            () => OwnedJournal(performance, fans, journalBase: PerformancePreset.Performance));

        HyperBoostLifecycleArmResult result = await runner.ArmAsync(
            snapshot,
            HyperBoostLifecycleScenario.UnexpectedExit,
            Path.Combine(directory, $"refused-{performance}-{fans}.json"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(0, client.EnableCalls);
        Assert.Equal(0, client.ValidationEnableCalls);
    }

    [Fact]
    public async Task ArmAsync_Rz090528RefusesBalancedOnlyWritableCapabilities()
    {
        Directory.CreateDirectory(directory);
        var client = new FakeClient(PerformancePreset.Performance, baseModes: new HyperBoostBaseModeCapabilities(
            DeviceFeatureAccess.ObservedOnly, DeviceFeatureAccess.ReadWrite, DeviceFeatureAccess.ObservedOnly));
        FirmwareSmokeSnapshot snapshot = Snapshot(client.Status, "RZ09-0528");
        HyperBoostLifecycleValidationRunner runner = Runner(
            client,
            snapshot,
            () => new HyperBoostLifecycleServiceProcess(42_103, 123_503),
            () => OwnedJournal(PerformancePreset.Performance, journalBase: PerformancePreset.Performance));

        HyperBoostLifecycleArmResult result = await runner.ArmAsync(
            snapshot,
            HyperBoostLifecycleScenario.LowPower,
            Path.Combine(directory, "balanced-only.json"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(0, client.EnableCalls);
    }

    [Fact]
    public async Task ArmAsync_UnknownModelHasNoLifecycleTransition()
    {
        Directory.CreateDirectory(directory);
        var client = new FakeClient();
        FirmwareSmokeSnapshot snapshot = Snapshot(client.Status, "RZ09-0999");
        HyperBoostLifecycleValidationRunner runner = Runner(
            client,
            snapshot,
            () => new HyperBoostLifecycleServiceProcess(42_104, 123_504),
            () => OwnedJournal());

        HyperBoostLifecycleArmResult result = await runner.ArmAsync(
            snapshot,
            HyperBoostLifecycleScenario.LowPower,
            Path.Combine(directory, "unknown.json"),
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(0, client.EnableCalls);
    }

    [Theory]
    [InlineData("RZ09-0528", PerformancePreset.Balanced, PerformancePreset.Performance)]
    [InlineData("RZ09-0528", PerformancePreset.Performance, PerformancePreset.Balanced)]
    [InlineData("RZ09-0581", PerformancePreset.Performance, PerformancePreset.Balanced)]
    [InlineData("RZ09-0999", PerformancePreset.Balanced, PerformancePreset.Balanced)]
    public void MarkerIsWellFormed_RejectsCrossModelTransition(
        string model,
        PerformancePreset baseMode,
        PerformancePreset restore)
    {
        var marker = new HyperBoostLifecycleMarker(
            HyperBoostLifecycleMarker.CurrentSchemaVersion,
            HyperBoostLifecycleScenario.LowPower,
            model,
            new string('A', 64),
            baseMode,
            RequiresCoolingPad: false,
            DateTimeOffset.UtcNow,
            new HyperBoostLifecycleServiceProcess(42_105, 123_505),
            new string('B', 64),
            RestoreIntent(restore))
        {
            TransitionId = Guid.NewGuid(),
        };

        Assert.False(marker.IsWellFormed);
    }

    [Fact]
    public void ParseOwnershipJournalDiagnostics_AcceptsExactPerformanceBase()
    {
        string serviceJson = JsonSerializer.Serialize(
            new
            {
                exists = true,
                valid = true,
                state = "OwnedConfirmed",
                baseMode = PerformancePreset.Performance,
                requiresCoolingPad = false,
                restoreIntent = RestoreIntent(PerformancePreset.Performance),
                sha256 = new string('A', 64),
                transitionId = Guid.NewGuid(),
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        HyperBoostLifecycleJournalObservation observation =
            HyperBoostLifecycleValidationRunner.ParseOwnershipJournalDiagnostics(
                JsonSerializer.Deserialize<HyperBoostLifecycleJournalDiagnostic>(
                    serviceJson,
                    FirmwareSmokeFiles.JsonOptions));

        Assert.True(observation.Valid);
        Assert.Equal(PerformancePreset.Performance, observation.BaseMode);
    }

    private static HyperBoostLifecycleValidationRunner Runner(
        FakeClient client,
        FirmwareSmokeSnapshot snapshot,
        Func<HyperBoostLifecycleServiceProcess?> process,
        Func<HyperBoostLifecycleJournalObservation> journal,
        Func<IReadOnlyList<HyperBoostLifecycleDiagnosticResult>>? diagnostics = null,
        string toolProductVersion = "test") =>
        new(
            client,
            _ => Task.FromResult(snapshot),
            (_, _) => true,
            process,
            _ =>
            {
                HyperBoostLifecycleJournalObservation observation = journal();
                if (observation.Exists
                    && observation.TransitionId is null
                    && client.LastTransitionId is { } transitionId)
                {
                    observation = observation with { TransitionId = transitionId };
                }
                return Task.FromResult(observation);
            },
            _ => Task.FromResult(diagnostics?.Invoke() ?? []),
            (_, _) => Task.CompletedTask,
            new FixedTimeProvider(new DateTimeOffset(
                2026,
                8,
                12,
                1,
                0,
                0,
                TimeSpan.Zero)),
            toolProductVersion);

    private static HyperBoostLifecycleJournalObservation OwnedJournal(
        PerformancePreset restorePerformance = PerformancePreset.Balanced,
        FanMode restoreFans = FanMode.Automatic,
        bool requiresCoolingPad = false,
        PerformancePreset journalBase = PerformancePreset.Balanced) => new(
        true,
        true,
        "OwnedConfirmed",
        journalBase,
        RestoreIntent(restorePerformance, restoreFans),
        RequiresCoolingPad: requiresCoolingPad,
        Sha256: new string('A', 64));

    private static HyperBoostLifecycleRestoreIntent RestoreIntent(
        PerformancePreset performance = PerformancePreset.Balanced,
        FanMode fans = FanMode.Automatic) => new(
        performance,
        performance == PerformancePreset.Custom
            ? CustomPerformanceSettings.Default
            : null,
        GpuClockTuning: null,
        FanControlSettings.FromLegacy(fans));

    private static IReadOnlyList<HyperBoostLifecycleDiagnosticResult>
        RecoveryDiagnostics(
            DateTimeOffset timestamp,
            string boundary,
            Guid? transitionId) =>
        [
            new(
                timestamp.AddMilliseconds(1),
                "Service",
                "Reconcile",
                "Stored",
                $"Attempting to exit OpenBlade-owned HyperBoost and restore its entry-time profile {boundary}.",
                transitionId),
            new(
                timestamp.AddMilliseconds(2),
                "Service",
                "Reconcile",
                "Applied",
                $"OpenBlade-owned HyperBoost exited and its entry-time profile was restored {boundary}.",
                transitionId),
        ];

    private static FirmwareSmokeSnapshot Snapshot(ServiceStatusSnapshot status, string model = "RZ09-0581") => new(
        SchemaVersion: FirmwareSmokeSnapshotCollector.CurrentSchemaVersion,
        CapturedAtUtc: DateTimeOffset.UtcNow,
        ObservationAttempts: 1,
        TargetModel: model,
        ServiceVersion: status.ServiceVersion,
        DeviceVerified: true,
        WritesBlocked: false,
        PotentialControllers: [],
        ControllerDetectionWarning: null,
        ExternalChanges: [],
        UnconfirmedWrites: [],
        Message: "Verified",
        Firmware: new FirmwareIdentity("4.00", "1.00", "1.00", "1.00"),
        Capabilities: new FirmwareSmokeCapabilities(
            ["Balanced"],
            ["Automatic"],
            [],
            [],
            null,
            null,
            null,
            [],
            false,
            false,
            [],
            [],
            null,
            null,
            null,
            null,
            [],
            DeviceFeatureAccess.ReadWrite.ToString(),
            DeviceFeatureAccess.ObservedOnly.ToString()),
        Controls: new FirmwareSmokeControlState(
            "Balanced",
            "Automatic",
            null,
            null,
            null,
            null,
            null,
            LightingRendererOwnershipState.None.ToString(),
            null,
            null,
            null,
            null,
            PowerSource.PluggedIn.ToString()),
        LiveReadings: new FirmwareSmokeLiveReadings(null, null, null, null, null),
        Runtime: new FirmwareSmokeRuntime(
            1,
            1,
            1,
            [
                new InstalledComponent("OpenBlade.Service.exe", "test", 100),
                new InstalledComponent("OpenBlade.SessionAgent.exe", "test", 100),
                new InstalledComponent("OpenBlade.Tray.exe", "test", 100),
            ]));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeClient : IFirmwareSmokeClient
    {
        private readonly PerformancePreset baseMode;
        private readonly FanMode fanMode;

        internal FakeClient(
            PerformancePreset baseMode = PerformancePreset.Balanced,
            FanMode fanMode = FanMode.Automatic,
            bool padlessAdmitted = true,
            bool padlessObservedOnly = false,
            CoolingPadConnectionState coolingPadConnection =
                CoolingPadConnectionState.Absent,
            HyperBoostBaseModeCapabilities? baseModes = null)
        {
            this.baseMode = baseMode;
            this.fanMode = fanMode;
            DeviceCapabilities capabilities = DeviceCapabilities.SafeTargetDefaults() with
            {
                HyperBoostAccess = DeviceFeatureAccess.ReadWrite,
                HyperBoostWithoutCoolingPadAccess = padlessAdmitted
                    ? DeviceFeatureAccess.ReadWrite
                    : padlessObservedOnly
                        ? DeviceFeatureAccess.ObservedOnly
                        : null,
                HyperBoostBaseModes = baseModes ?? new HyperBoostBaseModeCapabilities(
                    DeviceFeatureAccess.ReadWrite,
                    DeviceFeatureAccess.ReadWrite,
                    DeviceFeatureAccess.ReadWrite),
            };
            Status = new ServiceStatusSnapshot(
                Revision: 1,
                DeviceVerified: true,
                WritesBlocked: false,
                PotentialControllers: [],
                ControllerDetectionWarning: null,
                ExternalChanges: [],
                Message: "Verified",
                capabilities,
                new ConfirmedDeviceState(
                    IsManualFanMode(fanMode) ? null : baseMode,
                    fanMode,
                    new ConfirmedBatteryState(true, 80, false),
                    Lighting: null)
                {
                    CustomPerformance = baseMode == PerformancePreset.Custom
                        ? CustomPerformanceSettings.Default
                        : null,
                },
                ProfileDefaults.CreateSafeDefaults(),
                new DesiredDeviceState(
                    new ResolvedSetting<PerformancePreset>(
                        baseMode,
                        SettingValueSource.PowerProfile),
                    new ResolvedSetting<FanMode>(
                        fanMode,
                        SettingValueSource.PowerProfile),
                    new ResolvedSetting<LightingSettings>(
                        LightingSettingsFactory.SpectrumCycling(50),
                        SettingValueSource.PowerProfile),
                    new DesiredBatteryState(BatteryPolicy.Create(80), true, false))
                {
                    CustomPerformance = baseMode == PerformancePreset.Custom
                        ? CustomPerformanceSettings.Default
                        : null,
                    FanControl = FanControlSettings.FromLegacy(fanMode),
                },
                ActivePowerSource: PowerSource.PluggedIn,
                LiveReadings: null,
                PerformanceOverride: null,
                FanOverride: null,
                LightingOverride: null,
                FullChargeOverride: false,
                Firmware: new FirmwareVersions("4.00", "1.00", "1.00", "RZ09-0581"),
                ServiceVersion: "test",
                LightingRendererOwnership: LightingRendererOwnershipState.None,
                LightingSuppressions: [],
                StartupAnimationEnabled: true,
                FunctionKeyPrimary: FunctionKeyPrimaryMode.MultimediaKeys,
                GamingModeEnabled: false,
                AlwaysOverrideExternalLightingChanges: false,
                KeyboardDriverModeActive: true)
            {
                PadlessHyperBoostEligibility = coolingPadConnection
                    == CoolingPadConnectionState.Absent
                        ? PadlessHyperBoostEligibilityState.CoolingPadAbsent
                        : PadlessHyperBoostEligibilityState.CoolingPadPresent,
            };
        }

        internal ServiceStatusSnapshot Status { get; private set; }

        internal int EnableCalls { get; private set; }

        internal int DisableCalls { get; private set; }

        internal int ValidationEnableCalls { get; private set; }

        internal Guid? LastTransitionId { get; private set; }

        internal int ObserveCalls { get; private set; }

        internal bool ThrowAfterEnable { get; init; }

        internal bool FailResultAfterConfirmedEnable { get; init; }

        internal bool BlockEnableAndActivateExternallyOnNextObservation { get; init; }

        internal bool FailEnableAndActivateExternallyOnNextObservation { get; init; }

        internal bool FailEnableAfterTransitionBaseline { get; init; }

        internal void RestoreWithoutClientWrite() => Status = Status with
        {
            ConfirmedState = Status.ConfirmedState! with
            {
                HyperBoostActive = false,
                Performance = IsManualFanMode(fanMode) ? null : baseMode,
                Fans = fanMode,
                CustomPerformance = baseMode == PerformancePreset.Custom
                    ? CustomPerformanceSettings.Default
                    : null,
            },
        };

        public Task<ServiceStatusSnapshot> GetStatusAsync(
            CancellationToken cancellationToken) => Task.FromResult(Status);

        public Task<ServiceStatusSnapshot> ObserveStateAsync(
            LiveReadingSensors sensors,
            CancellationToken cancellationToken)
        {
            ObserveCalls++;
            if ((BlockEnableAndActivateExternallyOnNextObservation
                    || FailEnableAndActivateExternallyOnNextObservation)
                && EnableCalls > 0)
            {
                Status = Status with
                {
                    ConfirmedState = Status.ConfirmedState! with
                    {
                        HyperBoostActive = true,
                        Performance = null,
                        Fans = FanMode.Automatic,
                    },
                };
            }

            return Task.FromResult(Status);
        }

        public Task<ControlOperationResult> SetHyperBoostAsync(
            bool enabled,
            CancellationToken cancellationToken)
        {
            if (enabled)
            {
                EnableCalls++;
            }
            else
            {
                DisableCalls++;
            }
            if (enabled && BlockEnableAndActivateExternallyOnNextObservation)
            {
                return Task.FromResult(new ControlOperationResult(
                    ControlOperationStatus.Blocked,
                    "The enable request was blocked before submission."));
            }
            if (enabled && FailEnableAndActivateExternallyOnNextObservation)
            {
                return Task.FromResult(new ControlOperationResult(
                    ControlOperationStatus.Failed,
                    "The enable request failed before the concurrent owner ran."));
            }
            if (enabled && FailEnableAfterTransitionBaseline)
            {
                Status = Status with
                {
                    ConfirmedState = Status.ConfirmedState! with
                    {
                        HyperBoostActive = false,
                        Performance = PerformancePreset.Balanced,
                        Fans = FanMode.Automatic,
                        CustomPerformance = null,
                    },
                };
                return Task.FromResult(new ControlOperationResult(
                    ControlOperationStatus.Failed,
                    "HyperBoost entry failed after staging the transition baseline."));
            }
            Status = Status with
            {
                ConfirmedState = Status.ConfirmedState! with
                {
                    HyperBoostActive = enabled,
                    Performance = enabled || IsManualFanMode(fanMode)
                        ? null
                        : baseMode,
                    Fans = enabled ? FanMode.Automatic : fanMode,
                    CustomPerformance = !enabled
                        && baseMode == PerformancePreset.Custom
                            ? CustomPerformanceSettings.Default
                            : null,
                },
            };
            if (enabled && FailResultAfterConfirmedEnable)
            {
                return Task.FromResult(new ControlOperationResult(
                    ControlOperationStatus.Failed,
                    "The enable outcome was indeterminate."));
            }
            if (enabled && ThrowAfterEnable)
            {
                throw new IOException("The apply result was indeterminate.");
            }
            return Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Applied,
                "Applied"));
        }

        public Task<ControlOperationResult> SetHyperBoostAsync(
            bool enabled,
            Guid requestId,
            CancellationToken cancellationToken)
        {
            LastTransitionId = requestId;
            return SetHyperBoostAsync(enabled, cancellationToken);
        }

        public Task<ControlOperationResult> ValidatePadlessHyperBoostLifecycleAsync(
            PadlessHyperBoostLifecycleScenario scenario,
            Guid validationRunId,
            string expectedSourceRevision,
            string expectedServiceAssemblySha256,
            string expectedAdmissionManifestSha256,
            CancellationToken cancellationToken)
        {
            Assert.True(Enum.IsDefined(scenario));
            Assert.NotEqual(Guid.Empty, validationRunId);
            Assert.Equal(40, expectedSourceRevision.Length);
            Assert.Equal(64, expectedServiceAssemblySha256.Length);
            Assert.Equal(64, expectedAdmissionManifestSha256.Length);
            ValidationEnableCalls++;
            LastTransitionId = validationRunId;
            Status = Status with
            {
                ConfirmedState = Status.ConfirmedState! with
                {
                    HyperBoostActive = true,
                    Performance = null,
                    Fans = FanMode.Automatic,
                },
            };
            return Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Applied,
                "Validation applied"));
        }

        public Task<DiagnosticArchive> ExportDiagnosticsAsync(
            bool liveReadingsEnabled,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        private static bool IsManualFanMode(FanMode mode) =>
            mode is FanMode.Fixed or FanMode.ManualCurve or FanMode.Maximum;

        public Task<ControlOperationResult> SetPerformanceOverrideAsync(
            PerformanceOverride? value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ControlOperationResult> SetFanOverrideAsync(
            ManualOverride<FanMode>? value,
            FanControlSettings? settings,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ControlOperationResult> SetLightingOverrideAsync(
            ManualOverride<LightingSettings>? value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ControlOperationResult> SetBatteryPolicyAsync(
            BatteryPolicy value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ControlOperationResult> ApplyLaptopControlAsync(
            ApplyOneShotLaptopControl value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
