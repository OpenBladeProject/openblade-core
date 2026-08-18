using OpenBlade.Contracts;
using OpenBlade.FirmwareSmoke;

namespace OpenBlade.FirmwareSmoke.Tests;

public sealed class ReversibleSmokeRunnerTests
{
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

    [Theory]
    [InlineData(PerformancePreset.Silent)]
    [InlineData(PerformancePreset.Performance)]
    public async Task RunAsync_WritableDirectHyperBoostBaseRestoresWithoutBalancedBridge(
        PerformancePreset baseMode)
    {
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
    public async Task RunAsync_PadlessRejectsNonBalancedBase()
    {
        await using var client = new FakeFirmwareSmokeClient(
            PerformancePreset.Performance,
            padlessAdmitted: true,
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
    public async Task RunAsync_StillRestoresAfterApplicationFailure()
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
        Assert.True(client.PerformanceMutationCalls >= 2);
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
    public async Task RunAsync_HyperBoostFailureAfterApplyRestoresOnceFromReadback()
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
        Assert.Equal(2, client.HyperBoostMutationCalls);
        client.AssertOriginalState();
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
    public async Task RunAsync_RetriesOneProvenPreWriteReadFailure()
    {
        await using var client = new FakeFirmwareSmokeClient
        {
            FailFirstPerformancePreWrite = true,
        };

        IReadOnlyList<FirmwareSmokeCheck> checks =
            await new ReversibleSmokeRunner(client).RunAsync(CancellationToken.None);

        Assert.Equal(8, checks.Count);
        Assert.All(
            checks,
            check => Assert.Equal(FirmwareSmokeOutcome.Passed, check.Outcome));
        Assert.Equal(4, client.PerformanceMutationCalls);
        client.AssertOriginalState();
    }

    [Fact]
    public async Task RunAsync_UsesIndependentReadbackAfterIndeterminateRestore()
    {
        await using var client = new FakeFirmwareSmokeClient
        {
            IndeterminatePerformanceRestore = true,
        };

        IReadOnlyList<FirmwareSmokeCheck> checks =
            await new ReversibleSmokeRunner(client).RunAsync(CancellationToken.None);

        Assert.Equal(8, checks.Count);
        Assert.All(
            checks,
            check => Assert.Equal(FirmwareSmokeOutcome.Passed, check.Outcome));
        client.AssertOriginalState();
    }

    private sealed class FakeFirmwareSmokeClient : IFirmwareSmokeClient
    {
        private readonly ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        private readonly PerformancePreset originalPerformance;
        private ServiceStatusSnapshot status;
        private PerformancePreset? hyperBoostRestoreBase;

        public FakeFirmwareSmokeClient(
            PerformancePreset originalPerformance = PerformancePreset.Balanced,
            bool padlessAdmitted = false,
            CoolingPadConnectionState coolingPadConnection =
                CoolingPadConnectionState.PresentExact)
        {
            this.originalPerformance = originalPerformance;
            var capabilities = new DeviceCapabilities(
                new HashSet<PerformancePreset>(Enum.GetValues<PerformancePreset>()),
                new HashSet<FanMode>(Enum.GetValues<FanMode>()),
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
                    DeviceFeatureAccess.ReadWrite,
                    originalPerformance == PerformancePreset.Performance
                        ? DeviceFeatureAccess.ReadWrite
                        : DeviceFeatureAccess.ObservedOnly),
                CoolingPadSupportAccess = DeviceFeatureAccess.ObservedOnly,
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
                CoolingPad = new CoolingPadStatus(
                    coolingPadConnection,
                    new CoolingPadCapabilities(
                        DeviceFeatureAccess.ReadOnly,
                        DeviceFeatureAccess.ObservedOnly,
                        DeviceFeatureAccess.ObservedOnly,
                        DeviceFeatureAccess.ObservedOnly,
                        SupportsIntentPersistence: true,
                        CoolingDemandApiAccess.IntentOnly),
                    CoolingPadFanIntent.Default,
                    ComputedDemand: null,
                    AppliedDemand: null,
                    ConfiguredFanTarget: null,
                    ConfiguredFanTargetIsFresh: false,
                    CoolingPadControlOwnershipState.Unowned,
                    CoolingDemandApiEnabled: false,
                    ActiveDemandLeases: 0,
                    LightingSyncEnabled: false,
                    CoolingPadLightingSyncState.Unavailable,
                    coolingPadConnection == CoolingPadConnectionState.Absent
                        ? "Pad absence confirmed"
                        : "Cooling-pad state observed"),
            };
        }

        public bool FailFirstPerformanceApply { get; init; }

        public bool FailFirstPerformancePreWrite { get; init; }

        public bool IndeterminatePerformanceRestore { get; init; }

        public bool FailHyperBoostEnableKnownNotApplied { get; init; }

        public bool FailHyperBoostEnableAfterApplying { get; init; }

        public bool FailHyperBoostRestore { get; init; }

        public int PerformanceMutationCalls { get; private set; }

        public int HyperBoostMutationCalls { get; private set; }

        public List<PerformancePreset> HyperBoostEnableBases { get; } = [];

        public List<PerformancePreset> HyperBoostRestoredBases { get; } = [];

        public Task<ServiceStatusSnapshot> GetStatusAsync(
            CancellationToken cancellationToken) => Task.FromResult(status);

        public Task<ServiceStatusSnapshot> ObserveStateAsync(
            LiveReadingSensors sensors,
            CancellationToken cancellationToken) => Task.FromResult(status);

        public Task<DiagnosticArchive> ExportDiagnosticsAsync(
            bool liveReadingsEnabled,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DiagnosticArchive("test.zip", []));

        public Task<ControlOperationResult> SetPerformanceOverrideAsync(
            PerformanceOverride? value,
            CancellationToken cancellationToken)
        {
            PerformanceMutationCalls++;
            if (FailFirstPerformanceApply && PerformanceMutationCalls == 1)
            {
                return Failed();
            }

            if (FailFirstPerformancePreWrite && PerformanceMutationCalls == 1)
            {
                return Task.FromResult(new ControlOperationResult(
                    ControlOperationStatus.Failed,
                    "Performance could not be read before writing: Battery state query failed."));
            }

            PerformancePreset selected = value?.Value ?? originalPerformance;
            status = status with
            {
                ConfirmedState = status.ConfirmedState! with { Performance = selected },
                PerformanceOverride = value,
            };
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
                    Fans = FanMode.Automatic,
                },
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
            FanMode selected = value?.Value ?? profiles.PluggedIn.Fans;
            status = status with
            {
                ConfirmedState = status.ConfirmedState! with { Fans = selected },
                FanOverride = value,
                FanControlOverride = settings,
            };
            return Applied();
        }

        public Task<ControlOperationResult> SetLightingOverrideAsync(
            ManualOverride<LightingSettings>? value,
            CancellationToken cancellationToken)
        {
            status = status with
            {
                LightingOverride = value,
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
