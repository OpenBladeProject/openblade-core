using System.Text.Json;
using OpenBlade.Contracts;
using OpenBlade.FirmwareSmoke;

namespace OpenBlade.FirmwareSmoke.Tests;

public sealed class CpuVoltageOptimizerSmokeRunnerTests : IDisposable
{
    private const string Version = "0.24.0+26f61284";
    private static readonly int[] Indices = [0, 2, 4, 6, 8, 9, 10, 11, 12, 13, 14, 15];
    private readonly string directory = Path.Combine(Path.GetTempPath(), "OpenBlade-co-smoke-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    public async Task OneCurveOnlyTransitionAndOwnedRestoreRequireIndependentReadbacks(int original, bool lightingOnly)
    {
        var client = new Client(original, directory) { ExternalChanges = lightingOnly ? [DeviceSubsystem.Lighting] : [] };
        var result = await Run(client);
        Assert.Equal("Passed", result.Outcome); Assert.True(result.OriginalVerified);
        Assert.Equal(new[] { "status", "observe", "status", "observe", "apply", "status", "observe", "status", "restore", "status", "observe" }, client.Calls);
        Assert.Equal(1, client.ApplyCalls); Assert.Equal(1, client.RestoreCalls);
        Assert.Null(client.Settings!.PowerLimitWatts);
        Assert.Equal(original == -1 ? 0 : -1, client.Settings.CurveOptimizer!.EffectiveValue);
        Assert.All(new[] { result.Baseline!, result.Forward!, result.Final! }, state =>
        {
            Assert.Equal(Indices, state.CoreValues.Select(core => core.CoreIndex));
            Assert.Equal(12, state.CoreValues.Count);
        });
        Assert.All(result.Baseline!.CoreValues, core => Assert.Equal(original, core.Value));
        Assert.All(result.Forward!.CoreValues, core => Assert.Equal(original == -1 ? 0 : -1, core.Value));
        Assert.Equal(Indices, result.Final!.CoreValues.Select(core => core.CoreIndex));
        Assert.All(result.Final.CoreValues, core => Assert.Equal(original, core.Value));
        using var baseline = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, CpuVoltageOptimizerSmokeRunner.BaselineFile)));
        Assert.Equal(12, baseline.RootElement.GetProperty("cpuTuning").GetProperty("liveState").GetProperty("coreValues").GetArrayLength());
        Assert.Equal(0xFF55, baseline.RootElement.GetProperty("coreMask").GetInt32());
        Assert.True(File.Exists(Path.Combine(directory, CpuVoltageOptimizerSmokeRunner.ResultFile)));
    }

    [Theory]
    [InlineData("wrong", Version)]
    [InlineData(CpuVoltageOptimizerSmokeRunner.Confirmation, null)]
    [InlineData(CpuVoltageOptimizerSmokeRunner.Confirmation, "0.24.0")]
    [InlineData(CpuVoltageOptimizerSmokeRunner.Confirmation, "bad+26f61284")]
    [InlineData(CpuVoltageOptimizerSmokeRunner.Confirmation, "0.24.0+unknown")]
    [InlineData(CpuVoltageOptimizerSmokeRunner.Confirmation, "0.24+26f61284")]
    [InlineData(CpuVoltageOptimizerSmokeRunner.Confirmation, "0.24.0+voltage-validation")]
    [InlineData(CpuVoltageOptimizerSmokeRunner.Confirmation, "0.24.0+voltage-validation.1234567")]
    [InlineData(CpuVoltageOptimizerSmokeRunner.Confirmation, "0.24.0+voltage-validation.12345678.extra")]
    [InlineData(CpuVoltageOptimizerSmokeRunner.Confirmation, "0.24.0+other.12345678")]
    [InlineData(CpuVoltageOptimizerSmokeRunner.Confirmation, "0.24.0+Voltage-validation.12345678")]
    [InlineData(CpuVoltageOptimizerSmokeRunner.Confirmation, " 0.24.0+26f61284")]
    public async Task MissingAuthorizationOrSourceVersionMakesNoClientCall(string confirmation, string? version)
    {
        var client = new Client(-1, directory);
        await Assert.ThrowsAsync<ArgumentException>(() => new CpuVoltageOptimizerSmokeRunner(client)
            .RunAsync(confirmation, version, directory, CancellationToken.None));
        Assert.Empty(client.Calls);
    }

    [Theory]
    [InlineData("0.24.0+voltage-validation.12345678")]
    [InlineData("65535.1.2.3+voltage-validation.1234567890123456789012345678901234567890")]
    [InlineData("0.24.0+1234567890123456789012345678901234567890")]
    public void ExactValidationOrProductionRevisionIsAccepted(string version) =>
        CpuVoltageOptimizerSmokeRunner.ValidateAuthorization(CpuVoltageOptimizerSmokeRunner.Confirmation, version);

    [Theory]
    [InlineData("version")]
    [InlineData("device")]
    [InlineData("power")]
    [InlineData("inactive")]
    [InlineData("controller")]
    [InlineData("warning")]
    [InlineData("blocked")]
    [InlineData("capability")]
    [InlineData("ownership")]
    [InlineData("missing-core")]
    [InlineData("wrong-mask")]
    [InlineData("mixed")]
    [InlineData("value")]
    [InlineData("backend")]
    [InlineData("observation")]
    public async Task AdmissionOwnershipAndExactFullVectorDenialsEmitNoWrite(string denial)
    {
        var client = new Client(-1, directory) { Denial = denial };
        var result = await Run(client);
        Assert.Equal("Blocked", result.Outcome);
        Assert.Equal(0, client.ApplyCalls); Assert.Equal(0, client.RestoreCalls);
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("cancel")]
    [InlineData("failed")]
    [InlineData("mixed-ownership")]
    public async Task PossibleApplyResponseFailureNeverGuessesRestore(string failure)
    {
        var client = new Client(-1, directory) { ApplyFailure = failure };
        var result = await Run(client);
        Assert.Equal("Unknown", result.Outcome); Assert.True(result.ApplyAttempted);
        Assert.Equal(1, client.ApplyCalls); Assert.Equal(0, client.RestoreCalls);
        Assert.Equal(2, client.ObserveCalls);
    }

    [Fact]
    public async Task NonLightingDriftAfterApplyStopsWithoutGuessedRestore()
    {
        var client = new Client(-1, directory)
        { ExternalChanges = [DeviceSubsystem.Lighting], DriftAfterApply = [DeviceSubsystem.Lighting, DeviceSubsystem.Fans] };
        var result = await Run(client);
        Assert.Equal("Unknown", result.Outcome); Assert.Equal("ForwardReadback", result.Phase);
        Assert.True(result.ApplyAttempted); Assert.False(result.RestoreAttempted); Assert.False(result.OriginalVerified);
        Assert.Equal(1, client.ApplyCalls); Assert.Equal(0, client.RestoreCalls); Assert.Equal(2, client.ObserveCalls);
    }

    [Theory]
    [InlineData(DeviceSubsystem.Performance, false)]
    [InlineData(DeviceSubsystem.Fans, false)]
    [InlineData(DeviceSubsystem.Battery, false)]
    [InlineData((DeviceSubsystem)255, false)]
    [InlineData(DeviceSubsystem.Performance, true)]
    public async Task NonLightingOrMixedExternalDriftBlocksBeforeApply(DeviceSubsystem subsystem, bool mixed)
    {
        var client = new Client(-1, directory)
        { ExternalChanges = mixed ? [DeviceSubsystem.Lighting, subsystem] : [subsystem] };
        var result = await Run(client);
        Assert.Equal("Blocked", result.Outcome); Assert.Equal("Admission", result.Phase);
        Assert.Equal(0, client.ApplyCalls); Assert.Equal(0, client.RestoreCalls); Assert.Equal(0, client.ObserveCalls);
    }

    [Fact]
    public async Task IndependentForwardReadbackDriftStopsWithoutRestore()
    {
        var client = new Client(-1, directory) { ForwardDrift = true };
        var result = await Run(client);
        Assert.Equal("Unknown", result.Outcome); Assert.Equal(1, client.ApplyCalls);
        Assert.Equal(0, client.RestoreCalls); Assert.Equal(3, client.ObserveCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshSafetyOrVersionFailureBeforeRestoreStops(bool versionChanged)
    {
        var client = new Client(-1, directory) { BlockRestore = !versionChanged, ChangeVersion = versionChanged };
        var result = await Run(client);
        Assert.Equal("Unknown", result.Outcome); Assert.Equal(0, client.RestoreCalls);
    }

    [Fact]
    public async Task RestoreFailureStopsWithoutFurtherObservationOrWrite()
    {
        var client = new Client(-1, directory) { FailRestore = true };
        var result = await Run(client);
        Assert.Equal("Unknown", result.Outcome); Assert.True(result.RestoreAttempted);
        Assert.Equal(1, client.ApplyCalls); Assert.Equal(1, client.RestoreCalls); Assert.Equal(3, client.ObserveCalls);
    }

    [Theory]
    [InlineData(CpuVoltageOptimizerSmokeRunner.BaselineFile)]
    [InlineData(CpuVoltageOptimizerSmokeRunner.ResultFile)]
    public async Task ExistingEvidenceCannotBeOverwrittenOrCauseAnyClientCall(string file)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, file);
        File.WriteAllText(path, "retained-prior-evidence");
        var client = new Client(-1, directory);
        await Assert.ThrowsAsync<IOException>(() => Run(client));
        Assert.Empty(client.Calls); Assert.Equal("retained-prior-evidence", File.ReadAllText(path));
    }

    [Fact]
    public async Task DurableBaselineIoFailureAfterObservationBlocksSetter()
    {
        var client = new Client(-1, directory) { BlockBaselinePersistence = true };
        var result = await Run(client);
        Assert.Equal("Blocked", result.Outcome); Assert.Equal("PersistBaseline", result.Phase);
        Assert.Equal(0, client.ApplyCalls); Assert.Equal(0, client.RestoreCalls);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedBaselineOrOwnershipAfterPersistenceBlocksApply(bool ownership)
    {
        var client = new Client(-1, directory) { PreApplyDrift = !ownership, PreApplyOwnership = ownership };
        var result = await Run(client);
        Assert.Equal("Blocked", result.Outcome); Assert.Equal(0, client.ApplyCalls); Assert.Equal(0, client.RestoreCalls);
    }

    [Fact]
    public async Task CancellationReturnedWithFinalReadbackCannotReportPass()
    {
        using var cancellation = new CancellationTokenSource();
        var client = new Client(-1, directory) { FinalCancellation = cancellation };
        var result = await new CpuVoltageOptimizerSmokeRunner(client).RunAsync(CpuVoltageOptimizerSmokeRunner.Confirmation,
            Version, directory, cancellation.Token);
        Assert.Equal("Unknown", result.Outcome); Assert.False(result.OriginalVerified);
        Assert.Equal(1, client.RestoreCalls);
        Assert.Contains("Unknown", File.ReadAllText(Path.Combine(directory, CpuVoltageOptimizerSmokeRunner.ResultFile)));
    }

    [Fact]
    public async Task PreCanceledPassPublicationLeavesNoPassOrTemporaryReport()
    {
        var result = new CpuVoltageOptimizerSmokeResult("Passed", "OriginalReadback", true, true, true,
            Version, null, null, null, "test");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CpuVoltageOptimizerSmokeRunner.PersistResultAsync(
            result, directory, new CancellationToken(canceled: true)));
        Assert.False(File.Exists(Path.Combine(directory, CpuVoltageOptimizerSmokeRunner.ResultFile)));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task FinalPublicationCollisionPreservesEvidenceAndCannotLeaveReadablePass()
    {
        var client = new Client(-1, directory) { FinalReportCollision = true };
        await Assert.ThrowsAsync<IOException>(() => Run(client));
        Assert.Equal("prior-other-report", File.ReadAllText(Path.Combine(directory, CpuVoltageOptimizerSmokeRunner.ResultFile)));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp-*"));
        Assert.Equal(1, client.ApplyCalls); Assert.Equal(1, client.RestoreCalls); Assert.Equal(4, client.ObserveCalls);
    }

    private Task<CpuVoltageOptimizerSmokeResult> Run(Client client) => new CpuVoltageOptimizerSmokeRunner(client)
        .RunAsync(CpuVoltageOptimizerSmokeRunner.Confirmation, Version, directory, CancellationToken.None);
    public void Dispose() { if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); } }

    private sealed class Client(int original, string directory) : IFirmwareSmokeClient
    {
        public List<string> Calls { get; } = [];
        public string? Denial, ApplyFailure;
        public bool ForwardDrift, FailRestore, BlockRestore, ChangeVersion, BlockBaselinePersistence, PreApplyDrift, PreApplyOwnership, FinalReportCollision;
        public CancellationTokenSource? FinalCancellation;
        public int ApplyCalls, RestoreCalls, ObserveCalls;
        private int statusCalls;
        public CpuTuningSettings? Settings;
        public IReadOnlyList<DeviceSubsystem> ExternalChanges = [];
        public IReadOnlyList<DeviceSubsystem>? DriftAfterApply;
        public Task<ServiceStatusSnapshot> GetStatusAsync(CancellationToken token)
        {
            Calls.Add("status"); statusCalls++;
            ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
            var caps = DeviceCapabilities.SafeTargetDefaults() with
            { CpuVoltageOptimizer = new(new(-30, -1, 1), Denial == "capability" ? DeviceFeatureAccess.ObservedOnly : DeviceFeatureAccess.ReadWrite) { SupportsEnableDisable = true } };
            var status = new ServiceStatusSnapshot(1, Denial != "device", Denial == "blocked" || BlockRestore && statusCalls == 4,
                Denial == "controller" ? ["external"] : [], Denial == "warning" ? "unknown controllers" : null,
                ApplyCalls > 0 && DriftAfterApply is not null ? DriftAfterApply : ExternalChanges, "test", caps, null, profiles,
                new(new(PerformancePreset.Balanced, SettingValueSource.PowerProfile), new(FanMode.Automatic, SettingValueSource.PowerProfile),
                    new(profiles.PluggedIn.Lighting, SettingValueSource.PowerProfile), new(profiles.BatteryPolicy, true, false)),
                Denial == "power" ? PowerSource.Battery : PowerSource.PluggedIn, null, null, null, null, false,
                Firmware: new("any", "any", "any", "any", "RZ09-0528"),
                ServiceVersion: Denial == "version" || ChangeVersion && statusCalls == 4 ? "0.24.0+deadbeef" : Version,
                ControlAccess: Denial == "inactive" ? SessionControlAccess.InactiveSession : SessionControlAccess.Active);
            return Task.FromResult(status);
        }
        public Task<ControlOperationResult> ObserveCpuTuningAsync(bool installed, CancellationToken token)
        {
            Assert.True(installed); Calls.Add("observe"); ObserveCalls++;
            bool forward = ApplyCalls > 0 && RestoreCalls == 0;
            var result = Result(forward && !ForwardDrift || ObserveCalls == 2 && PreApplyDrift ? (original == -1 ? 0 : -1) : original,
                forward || ObserveCalls == 2 && PreApplyOwnership);
            if (ObserveCalls == 1 && BlockBaselinePersistence) { Directory.CreateDirectory(Path.Combine(directory, CpuVoltageOptimizerSmokeRunner.BaselineFile)); }
            if (ObserveCalls == 4) { FinalCancellation?.Cancel(); }
            if (ObserveCalls == 4 && FinalReportCollision) { File.WriteAllText(Path.Combine(directory, CpuVoltageOptimizerSmokeRunner.ResultFile), "prior-other-report"); }
            return Task.FromResult(result);
        }
        public Task<ControlOperationResult> SetCpuTuningAsync(CpuTuningTarget target, CpuTuningSettings settings, CancellationToken token)
        {
            Calls.Add("apply"); ApplyCalls++; Settings = settings;
            Assert.Equal(CpuTuningTarget.LiveNow, target); Assert.Null(settings.PowerLimitWatts);
            Assert.True(File.Exists(Path.Combine(directory, CpuVoltageOptimizerSmokeRunner.BaselineFile)));
            if (ApplyFailure == "throw") { throw new IOException("response lost after possible submission"); }
            if (ApplyFailure == "cancel") { throw new OperationCanceledException(); }
            return Task.FromResult(ApplyFailure == "failed" ? new(ControlOperationStatus.Failed, "unknown")
                : Result(settings.CurveOptimizer!.EffectiveValue, owned: true));
        }
        public Task<ControlOperationResult> RestoreCpuTuningAsync(CpuTuningTarget target, CancellationToken token)
        {
            Calls.Add("restore"); RestoreCalls++; Assert.Equal(CpuTuningTarget.LiveNow, target);
            return Task.FromResult(FailRestore ? new(ControlOperationStatus.Failed, "unknown") : Result(original, owned: false));
        }
        private ControlOperationResult Result(int value, bool owned)
        {
            int[] indices = Denial == "missing-core" ? Indices[..^1] : Denial == "wrong-mask" ? [0, 1, 4, 6, 8, 9, 10, 11, 12, 13, 14, 15] : Indices;
            var vector = indices.Select(index => new CpuCoreCurveOptimizerValue(index, Denial == "value" ? -2 : Denial == "mixed" && index == 2 ? 0 : value)).ToArray();
            var backend = new CpuTuningBackendStatus(Denial == "backend" ? DeviceFeatureAccess.ObservedOnly : DeviceFeatureAccess.ReadWrite, CpuTuningBackendAvailability.Available);
            bool recoveryOwned = owned || Denial == "ownership";
            var status = new CpuTuningStatus(null, new(null, vector), backend, backend, CpuTuningPendingBoot.None, recoveryOwned)
            {
                FirmwareRecovery = new(CpuTuningRecoveryState.None, CpuTuningFeatures.None),
                LiveRecovery = new(recoveryOwned ? CpuTuningRecoveryState.RestoreReady : CpuTuningRecoveryState.None,
                    recoveryOwned ? ApplyFailure == "mixed-ownership" ? CpuTuningFeatures.PowerLimit | CpuTuningFeatures.CurveOptimizer : CpuTuningFeatures.CurveOptimizer : CpuTuningFeatures.None),
            };
            return new(Denial == "observation" ? ControlOperationStatus.Blocked : ControlOperationStatus.Stored, "test") { CpuTuning = status };
        }
        public Task<ServiceStatusSnapshot> ObserveStateAsync(LiveReadingSensors sensors, CancellationToken token) => throw new NotSupportedException();
        public Task<DiagnosticArchive> ExportDiagnosticsAsync(bool enabled, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetPerformanceOverrideAsync(PerformanceOverride? value, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetHyperBoostAsync(bool value, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetFanOverrideAsync(ManualOverride<FanMode>? value, FanControlSettings? settings, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetLightingOverrideAsync(ManualOverride<LightingSettings>? value, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetBatteryPolicyAsync(BatteryPolicy value, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> ApplyLaptopControlAsync(ApplyOneShotLaptopControl value, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
