using OpenBlade.Contracts;
using OpenBlade.FirmwareSmoke;
using System.Diagnostics;
using System.Text.Json;

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
    public async Task ArmAsync_MissingOwnedJournalRestoresOnceAndDoesNotCreateMarker()
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
        Assert.True(result.CleanupAttempted);
        Assert.True(result.FinalRestorationConfirmed);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(1, client.EnableCalls);
        Assert.Equal(1, client.DisableCalls);
        Assert.False(client.Status.ConfirmedState?.HyperBoostActive);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public async Task ArmAsync_IndeterminateApplyInspectsAndRestoresOnce()
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
        Assert.True(result.CleanupAttempted);
        Assert.True(result.FinalRestorationConfirmed);
        Assert.False(result.ManualRecoveryRequired);
        Assert.Equal(1, client.EnableCalls);
        Assert.Equal(1, client.DisableCalls);
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
            "before low power");

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
            "before display-off safety boundary");

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
            "before unexpected-exit recovery");

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
            "before low power").Concat(
                RecoveryDiagnostics(
                    armed.Marker.ArmedAtUtc.AddMilliseconds(1),
                    "before low power"))
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
            "before low power").Concat(
                RecoveryDiagnostics(
                    armed.Marker.ArmedAtUtc.AddMilliseconds(3),
                    "before display-off safety boundary"))
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
                schemaVersion = 5,
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

    private static HyperBoostLifecycleValidationRunner Runner(
        FakeClient client,
        FirmwareSmokeSnapshot snapshot,
        Func<HyperBoostLifecycleServiceProcess?> process,
        Func<HyperBoostLifecycleJournalObservation> journal,
        Func<IReadOnlyList<HyperBoostLifecycleDiagnosticResult>>? diagnostics = null) =>
        new(
            client,
            _ => Task.FromResult(snapshot),
            (_, _) => true,
            process,
            _ => Task.FromResult(journal()),
            _ => Task.FromResult(diagnostics?.Invoke() ?? []),
            (_, _) => Task.CompletedTask,
            new FixedTimeProvider(new DateTimeOffset(
                2026,
                8,
                12,
                1,
                0,
                0,
                TimeSpan.Zero)));

    private static HyperBoostLifecycleJournalObservation OwnedJournal(
        PerformancePreset restorePerformance = PerformancePreset.Balanced,
        FanMode restoreFans = FanMode.Automatic,
        bool requiresCoolingPad = false) => new(
        true,
        true,
        "OwnedConfirmed",
        PerformancePreset.Balanced,
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
        RecoveryDiagnostics(DateTimeOffset timestamp, string boundary) =>
        [
            new(
                timestamp.AddMilliseconds(1),
                "Service",
                "Reconcile",
                "Stored",
                $"Attempting to exit OpenBlade-owned HyperBoost and restore its entry-time profile {boundary}."),
            new(
                timestamp.AddMilliseconds(2),
                "Service",
                "Reconcile",
                "Applied",
                $"OpenBlade-owned HyperBoost exited and its entry-time profile was restored {boundary}."),
        ];

    private static FirmwareSmokeSnapshot Snapshot(ServiceStatusSnapshot status) => new(
        SchemaVersion: FirmwareSmokeSnapshotCollector.CurrentSchemaVersion,
        CapturedAtUtc: DateTimeOffset.UtcNow,
        ObservationAttempts: 1,
        TargetModel: "RZ09-0581",
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
                CoolingPadConnectionState.Absent)
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
                HyperBoostBaseModes = new HyperBoostBaseModeCapabilities(
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

        internal bool ThrowAfterEnable { get; init; }

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
            CancellationToken cancellationToken) => Task.FromResult(Status);

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
            if (enabled && ThrowAfterEnable)
            {
                throw new IOException("The apply result was indeterminate.");
            }
            return Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Applied,
                "Applied"));
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
