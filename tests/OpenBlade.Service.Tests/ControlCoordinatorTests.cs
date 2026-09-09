using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ControlCoordinatorTests
{
    [Fact]
    public async Task BatteryOperations_UseDistinctTypedSetters()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.Capabilities = device.Capabilities with
        {
            SupportsBatteryProtectionOff = true,
            SupportsTemporaryFullCharge = true,
        };
        var coordinator = new ControlCoordinator(device);

        await coordinator.ApplyChargeLimitAsync(BatteryPolicy.Create(75), CancellationToken.None);
        await coordinator.ApplyBatteryProtectionAsync(
            enabled: false,
            BatteryPolicy.Create(75),
            CancellationToken.None);
        device.State = device.State with { Battery = new ConfirmedBatteryState(true, 75, false) };
        await coordinator.ApplyTemporaryFullChargeAsync(enabled: true, CancellationToken.None);

        Assert.Equal(
            ["SetChargeLimit", "SetProtectionOff", "SetTemporaryFullCharge"],
            device.BatterySetterCalls);
    }

    [Fact]
    public async Task CancelTemporaryFullCharge_ReportsCancellationInsteadOfActivation()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.State = device.State with
        {
            Battery = new ConfirmedBatteryState(true, 80, true),
        };
        device.Capabilities = device.Capabilities with
        {
            SupportsTemporaryFullCharge = true,
        };
        var coordinator = new ControlCoordinator(device);

        ControlResult result = await coordinator.ApplyTemporaryFullChargeAsync(
            enabled: false,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, result.Status);
        Assert.Equal(
            "Temporary full charge cancellation setting applied and confirmed by readback.",
            result.Message);
        Assert.Equal(["SetTemporaryFullCharge"], device.BatterySetterCalls);
    }

    [Theory]
    [MemberData(nameof(InvalidProtectionDisableStates))]
    public async Task DisableProtection_RejectsInvalidConfirmedStateBeforeSetter(
        ConfirmedBatteryState? confirmed)
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.State = device.State with { Battery = confirmed };
        device.Capabilities = device.Capabilities with
        {
            SupportsBatteryProtectionOff = true,
        };
        var coordinator = new ControlCoordinator(device);

        ControlResult result = await coordinator.ApplyBatteryProtectionAsync(
            enabled: false,
            BatteryPolicy.Create(75),
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Blocked, result.Status);
        Assert.Empty(device.BatterySetterCalls);
    }

    [Fact]
    public async Task DisableProtection_WhenAlreadyOff_IsUnchangedWithoutSetter()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.State = device.State with { Battery = new ConfirmedBatteryState(false, null, false) };
        var coordinator = new ControlCoordinator(device);

        ControlResult result = await coordinator.ApplyBatteryProtectionAsync(
            enabled: false,
            BatteryPolicy.Create(75),
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Unchanged, result.Status);
        Assert.Empty(device.BatterySetterCalls);
    }

    [Fact]
    public async Task ActiveTemporaryFullCharge_BlocksLimitAndProtectionBeforeSetters()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.State = device.State with
        {
            Battery = new ConfirmedBatteryState(true, 80, true),
        };
        var coordinator = new ControlCoordinator(device);

        ControlResult limit = await coordinator.ApplyChargeLimitAsync(
            BatteryPolicy.Create(75),
            CancellationToken.None);
        ControlResult protection = await coordinator.ApplyBatteryProtectionAsync(
            enabled: false,
            BatteryPolicy.Create(80),
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Blocked, limit.Status);
        Assert.Equal(ControlResultStatus.Blocked, protection.Status);
        Assert.Empty(device.BatterySetterCalls);
    }

    [Fact]
    public async Task ChargeLimit_ValidAckWithMismatchedReadback_IsIndeterminate()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.AutoConfirmWrites = false;
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator.ApplyChargeLimitOutcomeAsync(
            BatteryPolicy.Create(75),
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Failed, outcome.Result.Status);
        Assert.True(outcome.WriteAttempted);
        Assert.False(outcome.ReadbackMatched);
        Assert.Equal(["SetChargeLimit"], device.BatterySetterCalls);
        Assert.Equal(2, device.StateReadCalls);
    }

    [Fact]
    public async Task ChargeLimit_KnownNotApplied_IsFailedWithoutIndeterminateWrite()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.BatteryWriteFailure = new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            "firmware rejected setter");
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator.ApplyChargeLimitOutcomeAsync(
            BatteryPolicy.Create(75),
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Failed, outcome.Result.Status);
        Assert.False(outcome.WriteAttempted);
        Assert.False(outcome.ReadbackMatched);
        Assert.Equal(1, device.StateReadCalls);
    }

    [Fact]
    public async Task Lighting_NativeSuccessWithoutEffectReadback_IsAppliedButUnconfirmed()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.SuppressConfirmationFor = DeviceSubsystem.Lighting;
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator.ApplyLightingOutcomeAsync(
            TargetLighting,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, outcome.Result.Status);
        Assert.Contains("does not report the resulting state", outcome.Result.Message);
        Assert.True(outcome.WriteAttempted);
        Assert.False(outcome.ReadbackMatched);
        Assert.Equal(1, device.LightingSetCalls);
    }

    [Fact]
    public async Task Lighting_PostSetterReadFailure_KeepsValidatedWriteApplied()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.ReadLightingStateFailureOnCall = 2;
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator.ApplyLightingOutcomeAsync(
            TargetLighting,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, outcome.Result.Status);
        Assert.True(outcome.WriteAttempted);
        Assert.False(outcome.ReadbackMatched);
        Assert.False(outcome.DeviceAccessFailed);
        Assert.Equal(1, device.LightingSetCalls);
    }

    [Fact]
    public async Task Lighting_BatteryStateFailureDoesNotBlockIndependentLightingWrite()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.ReadStateFailureOnCall = 1;
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator.ApplyLightingOutcomeAsync(
            TargetLighting,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, outcome.Result.Status);
        Assert.True(outcome.WriteAttempted);
        Assert.True(outcome.ReadbackMatched);
        Assert.Equal(0, device.StateReadCalls);
        Assert.Equal(2, device.LightingStateReadCalls);
        Assert.Equal(1, device.LightingSetCalls);
    }

    [Fact]
    public async Task Lighting_IndependentPreWriteReadFailureBlocksSetter()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.ReadLightingStateFailureOnCall = 1;
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator.ApplyLightingOutcomeAsync(
            TargetLighting,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Failed, outcome.Result.Status);
        Assert.False(outcome.WriteAttempted);
        Assert.False(outcome.ReadbackMatched);
        Assert.Equal(1, device.LightingStateReadCalls);
        Assert.Equal(0, device.LightingSetCalls);
    }

    [Fact]
    public async Task FixedFans_PostSetterReadFailure_KeepsValidatedWriteApplied()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.State = device.State with { Performance = PerformancePreset.Balanced };
        device.ReadStateFailureOnCall = 2;
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator.ApplyFanControlOutcomeAsync(
            new FanControlSettings(FanMode.Fixed, 2800, ManualFanCurve.Default),
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, outcome.Result.Status);
        Assert.True(outcome.WriteAttempted);
        Assert.False(outcome.ReadbackMatched);
        Assert.False(outcome.DeviceAccessFailed);
        Assert.Equal(1, device.FanSetCalls);
    }

    [Fact]
    public async Task EnableProtection_UnadmittedStoredLimit_BlocksBeforeSetter()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.State = device.State with { Battery = new ConfirmedBatteryState(false, null, false) };
        device.Capabilities = device.Capabilities with
        {
            ChargeLimits = new HashSet<int> { 80 },
        };
        var coordinator = new ControlCoordinator(device);

        ControlResult result = await coordinator.ApplyBatteryProtectionAsync(
            enabled: true,
            BatteryPolicy.Create(75),
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Blocked, result.Status);
        Assert.Empty(device.BatterySetterCalls);
    }

    [Fact]
    public async Task ChargeLimit_PostAckReadbackFailure_IsIndeterminate()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.ReadStateFailureOnCall = 2;
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator.ApplyChargeLimitOutcomeAsync(
            BatteryPolicy.Create(75),
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Failed, outcome.Result.Status);
        Assert.True(outcome.WriteAttempted);
        Assert.False(outcome.ReadbackMatched);
        Assert.Equal(["SetChargeLimit"], device.BatterySetterCalls);
    }

    [Fact]
    public async Task ChargeLimit_IndeterminateSubmission_DoesNotPerformSemanticReadback()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.BatteryWriteFailure = new BladeWriteException(
            WriteSubmissionState.Indeterminate,
            "session lost after submission");
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator.ApplyChargeLimitOutcomeAsync(
            BatteryPolicy.Create(75),
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Failed, outcome.Result.Status);
        Assert.True(outcome.WriteAttempted);
        Assert.Equal(1, device.StateReadCalls);
        Assert.True(outcome.ConfirmationUnavailable);
        Assert.Equal(device.State, outcome.PreWriteState);
        Assert.Equal(WriteSubmissionState.Indeterminate, outcome.WriteSubmissionState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChargeLimit_PartialFinalRead_RemainsUnconfirmed(bool missingBattery)
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        ConfirmedDeviceState initial = device.State;
        device.AfterSetter = _ => device.State = device.State with
        {
            Battery = missingBattery ? null : new ConfirmedBatteryState(true, 75, null),
        };
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator.ApplyChargeLimitOutcomeAsync(
            BatteryPolicy.Create(75),
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Failed, outcome.Result.Status);
        Assert.True(outcome.WriteAttempted);
        Assert.False(outcome.ReadbackMatched);
        Assert.True(outcome.ConfirmationUnavailable);
        Assert.Equal(initial, outcome.PreWriteState);
        Assert.Equal(2, device.StateReadCalls);
        Assert.Equal(["SetChargeLimit"], device.BatterySetterCalls);
    }

    [Fact]
    public async Task ChargeLimit_CanceledAfterAck_PropagatesIndeterminateCancellation()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        using var cancellation = new CancellationTokenSource();
        device.AfterSetter = _ => cancellation.Cancel();
        var coordinator = new ControlCoordinator(device);

        await Assert.ThrowsAsync<IntentionalWriteCanceledException>(
            () => coordinator.ApplyChargeLimitOutcomeAsync(
                BatteryPolicy.Create(75),
                cancellation.Token));

        Assert.Equal(["SetChargeLimit"], device.BatterySetterCalls);
        Assert.Equal(1, device.StateReadCalls);
    }

    public static TheoryData<ConfirmedBatteryState?> InvalidProtectionDisableStates() => new()
    {
        null,
        new ConfirmedBatteryState(true, 80, false),
        new ConfirmedBatteryState(true, 75, null),
        new ConfirmedBatteryState(true, 75, true),
    };

    [Theory]
    [InlineData(DeviceSubsystem.Performance, "SetPerformance")]
    [InlineData(DeviceSubsystem.Fans, "SetFans")]
    [InlineData(DeviceSubsystem.Battery, "SetChargeLimit")]
    [InlineData(DeviceSubsystem.Lighting, "SetLighting")]
    public async Task ScopedApply_UsesOneNamedSetterAndOneMatchingReadback(
        DeviceSubsystem subsystem,
        string setterCall)
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        if (subsystem == DeviceSubsystem.Fans)
        {
            device.State = device.State with { Performance = PerformancePreset.Balanced };
        }
        var coordinator = new ControlCoordinator(device);

        ControlResult result = await ApplyScopedAsync(coordinator, subsystem);

        Assert.Equal(ControlResultStatus.Applied, result.Status);
        string stateCall = subsystem == DeviceSubsystem.Lighting
            ? "LightingState"
            : "State";
        Assert.Equal(["Capabilities", stateCall, setterCall, stateCall], device.Calls);
        Assert.Equal(1, device.TotalSetterCalls);
    }

    [Theory]
    [InlineData(DeviceSubsystem.Performance)]
    [InlineData(DeviceSubsystem.Battery)]
    [InlineData(DeviceSubsystem.Lighting)]
    public async Task ScopedApply_WhenAlreadyMatching_PerformsNoSetterOrSecondRead(
        DeviceSubsystem subsystem)
    {
        RecordingBladeDevice device = CreateMatchingDevice();
        var coordinator = new ControlCoordinator(device);

        ControlResult result = await ApplyScopedAsync(coordinator, subsystem);

        Assert.Equal(ControlResultStatus.Unchanged, result.Status);
        Assert.Equal(
            [
                "Capabilities",
                subsystem == DeviceSubsystem.Lighting ? "LightingState" : "State",
            ],
            device.Calls);
        Assert.Equal(0, device.TotalSetterCalls);
    }

    [Fact]
    public async Task ScopedApply_UnsupportedNamedValue_BlocksOnlyBeforeSetterAndReadback()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.Capabilities = device.Capabilities with
        {
            FanModes = new HashSet<FanMode> { FanMode.Automatic },
        };
        var coordinator = new ControlCoordinator(device);

        ControlResult result = await coordinator.ApplyFanAsync(
            FanMode.Maximum,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Blocked, result.Status);
        Assert.Equal(["Capabilities"], device.Calls);
        Assert.Equal(0, device.TotalSetterCalls);
    }

    [Fact]
    public async Task ScopedPerformance_IsBlockedWhileMaximumFansAreConfirmed()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.State = device.State with
        {
            Performance = null,
            Fans = FanMode.Maximum,
        };
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator
            .ApplyPerformancePreservingGpuClockManagementOutcomeAsync(
            PerformancePreset.Performance,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Blocked, outcome.Result.Status);
        Assert.False(outcome.WriteAttempted);
        Assert.Equal(["Capabilities", "State"], device.Calls);
        Assert.Equal(0, device.PerformanceSetCalls);
    }

    [Fact]
    public async Task ScopedPerformance_SingleAttemptModeFailsClosedWithoutDeviceSeam()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        var coordinator = new ControlCoordinator(
            device,
            device.ReadStateAsync,
            singleAttemptPerformanceWrites: true);
        DesiredDeviceState desired = CreateDesiredState() with
        {
            Performance = new ResolvedSetting<PerformancePreset>(
                PerformancePreset.Performance,
                SettingValueSource.PowerProfile),
        };

        ScopedApplyOutcome outcome = await coordinator.ApplyPerformanceOutcomeAsync(
            desired,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Blocked, outcome.Result.Status);
        Assert.Contains("single-attempt thermal writes", outcome.Result.Message);
        Assert.False(outcome.WriteAttempted);
        Assert.Equal(0, device.PerformanceSetCalls);
    }

    [Fact]
    public async Task ScopedCustomPerformance_AppliesAndConfirmsBothComponentLevels()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        var levels = new CustomPerformanceSettings(
            CustomPerformanceLevel.High,
            CustomPerformanceLevel.Medium);
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator
            .ApplyPerformancePreservingGpuClockManagementOutcomeAsync(
            PerformancePreset.Custom,
            levels,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, outcome.Result.Status);
        Assert.True(outcome.ReadbackMatched);
        Assert.Equal(PerformancePreset.Custom, device.State.Performance);
        Assert.Equal(levels, device.State.CustomPerformance);
        Assert.Equal(["Capabilities", "State", "SetPerformance", "State"], device.Calls);
    }

    [Fact]
    public async Task ScopedCustomPerformance_ClockOnlyChangeSkipsFirmwareWrite()
    {
        var currentLevels = new CustomPerformanceSettings(
            CustomPerformanceLevel.High,
            CustomPerformanceLevel.Medium)
        {
            GpuClockOffsets = new GpuClockOffsetSettings(0, 0),
        };
        var requestedLevels = currentLevels with
        {
            GpuClockOffsets = new GpuClockOffsetSettings(5, 0),
        };
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.Capabilities = device.Capabilities with
        {
            GpuOverclock = WritableGpuOverclockCapabilities,
        };
        device.State = device.State with
        {
            Performance = PerformancePreset.Custom,
            Fans = FanMode.Automatic,
            CustomPerformance = currentLevels,
            GpuClockOffsets = new GpuClockOffsetCapabilities(
                "Test NVIDIA GPU",
                new GpuClockOffsetRange(-1000, 1000, 0),
                new GpuClockOffsetRange(-2000, 6000, 0)),
        };
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator
            .ApplyPerformancePreservingGpuClockManagementOutcomeAsync(
            PerformancePreset.Custom,
            requestedLevels,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, outcome.Result.Status);
        Assert.True(outcome.ReadbackMatched);
        Assert.Equal(0, device.PerformanceSetCalls);
        Assert.Equal(1, device.GpuClockSetCalls);
        Assert.Equal(
            ["Capabilities", "State", "SetGpuClockOffsets", "State"],
            device.Calls);
    }

    [Fact]
    public async Task OmittedStandaloneGpuTuning_PreservesManagedGpuClockOffsets()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.Capabilities = device.Capabilities with
        {
            GpuOverclock = WritableGpuOverclockCapabilities,
        };
        device.State = device.State with
        {
            Performance = PerformancePreset.Silent,
            Fans = FanMode.Automatic,
            GpuClockOffsets = new GpuClockOffsetCapabilities(
                "Test NVIDIA GPU",
                new GpuClockOffsetRange(-1000, 1000, 125),
                new GpuClockOffsetRange(-2000, 6000, 500)),
        };
        await device.SetGpuClockOffsetsAsync(
            new GpuClockOffsetSettings(125, 500),
            retainManagement: true,
            default);
        device.Calls.Clear();
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator
            .ApplyPerformancePreservingGpuClockManagementOutcomeAsync(
                PerformancePreset.Balanced,
                customPerformance: null,
                default);

        Assert.Equal(ControlResultStatus.Applied, outcome.Result.Status);
        Assert.Equal(0, device.GpuClockRelinquishCalls);
        Assert.Equal(
            GpuClockOffsetManagementState.Managed,
            device.GpuClockOffsetManagementState);
        Assert.Equal(
            new GpuClockOffsetSettings(125, 500),
            device.State.GpuClockOffsets?.Current);
        Assert.DoesNotContain("RelinquishGpuClockOffsets", device.Calls);
    }

    [Fact]
    public async Task AuthoritativeNullGpuTuning_RelinquishesBeforeStateRead()
    {
        RecordingBladeDevice device = CreateMatchingDevice();
        device.Capabilities = device.Capabilities with
        {
            GpuOverclock = WritableGpuOverclockCapabilities,
        };
        device.State = device.State with
        {
            GpuClockOffsets = new GpuClockOffsetCapabilities(
                "Test NVIDIA GPU",
                new GpuClockOffsetRange(-1000, 1000, 125),
                new GpuClockOffsetRange(-2000, 6000, 500)),
        };
        await device.SetGpuClockOffsetsAsync(
            new GpuClockOffsetSettings(125, 500),
            retainManagement: true,
            default);
        device.Calls.Clear();
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator.ApplyPerformanceOutcomeAsync(
            PerformancePreset.Balanced,
            customPerformance: null,
            gpuClockTuning: null,
            default);

        Assert.Equal(ControlResultStatus.Unchanged, outcome.Result.Status);
        Assert.Equal(1, device.GpuClockRelinquishCalls);
        Assert.Equal(
            ["Capabilities", "RelinquishGpuClockOffsets", "State"],
            device.Calls);
        Assert.Equal(
            GpuClockOffsetManagementState.Relinquished,
            device.GpuClockOffsetManagementState);
    }

    [Fact]
    public async Task ScopedPerformance_UnsupportedGpuTuningDropsOnlyGpuComponent()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator.ApplyPerformanceOutcomeAsync(
            PerformancePreset.Balanced,
            customPerformance: null,
            new GpuClockTuningSettings(
                Enabled: true,
                new GpuClockOffsetSettings(5, 10)),
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, outcome.Result.Status);
        Assert.True(outcome.WriteAttempted);
        Assert.True(outcome.ReadbackMatched);
        Assert.Equal(1, device.PerformanceSetCalls);
        Assert.Equal(0, device.GpuClockSetCalls);
        Assert.Equal(PerformancePreset.Balanced, device.State.Performance);
        Assert.Equal(
            ["Capabilities", "State", "SetPerformance", "State"],
            device.Calls);
    }

    [Fact]
    public async Task FullReconcile_BalancedClockOnlyChangeSkipsFirmwareWrite()
    {
        var requestedOffsets = new GpuClockOffsetSettings(5, 0);
        RecordingBladeDevice device = CreateMatchingDevice();
        device.State = device.State with
        {
            Performance = PerformancePreset.Balanced,
            Fans = FanMode.Automatic,
            CustomPerformance = null,
            GpuClockOffsets = new GpuClockOffsetCapabilities(
                "Test NVIDIA GPU",
                new GpuClockOffsetRange(-1000, 1000, 0),
                new GpuClockOffsetRange(-2000, 6000, 0)),
        };
        device.Capabilities = device.Capabilities with
        {
            PerformancePresets = new HashSet<PerformancePreset>
            {
                PerformancePreset.Balanced,
            },
            FanModes = new HashSet<FanMode> { FanMode.Automatic },
            GpuOverclock = WritableGpuOverclockCapabilities,
        };
        var desired = new DesiredDeviceState(
            new ResolvedSetting<PerformancePreset>(
                PerformancePreset.Balanced,
                SettingValueSource.PowerProfile),
            new ResolvedSetting<FanMode>(
                FanMode.Automatic,
                SettingValueSource.PowerProfile),
            new ResolvedSetting<LightingSettings>(
                TargetLighting,
                SettingValueSource.PowerProfile),
            new DesiredBatteryState(BatteryPolicy.Create(75), true, false))
        {
            GpuClockTuning = new GpuClockTuningSettings(
                Enabled: true,
                requestedOffsets),
        };
        var coordinator = new ControlCoordinator(device);

        FullTransitionApplyOutcome outcome =
            await coordinator.ApplyDesiredStateOutcomeAsync(desired, default);

        Assert.Equal(ControlResultStatus.Applied, outcome.Results.Performance.Status);
        Assert.Equal(0, device.PerformanceSetCalls);
        Assert.Equal(1, device.GpuClockSetCalls);
        Assert.Equal(requestedOffsets, device.State.GpuClockOffsets?.Current);
    }

    [Fact]
    public async Task FullReconcile_ManualTopologyGpuDriftSkipsFirmwareAndFanWrites()
    {
        var requestedOffsets = new GpuClockOffsetSettings(75, 150);
        RecordingBladeDevice device = CreateMatchingDevice();
        device.State = device.State with
        {
            Performance = null,
            Fans = FanMode.Fixed,
            GpuClockOffsets = new GpuClockOffsetCapabilities(
                "Test NVIDIA GPU",
                new GpuClockOffsetRange(-1000, 1000, 0),
                new GpuClockOffsetRange(-2000, 6000, 0)),
        };
        device.Capabilities = device.Capabilities with
        {
            PerformancePresets = new HashSet<PerformancePreset>
            {
                PerformancePreset.Balanced,
            },
            FanModes = new HashSet<FanMode> { FanMode.Fixed },
            GpuOverclock = WritableGpuOverclockCapabilities,
        };
        DesiredDeviceState desired = CreateDesiredState() with
        {
            Fans = new ResolvedSetting<FanMode>(
                FanMode.Fixed,
                SettingValueSource.PowerProfile),
            GpuClockTuning = new GpuClockTuningSettings(
                Enabled: true,
                requestedOffsets),
        };
        var coordinator = new ControlCoordinator(device);

        FullTransitionApplyOutcome outcome =
            await coordinator.ApplyDesiredStateOutcomeAsync(desired, default);

        Assert.Equal(ControlResultStatus.Applied, outcome.Results.Performance.Status);
        Assert.Equal(0, device.PerformanceSetCalls);
        Assert.Equal(0, device.FanSetCalls);
        Assert.Equal(1, device.GpuClockSetCalls);
        Assert.Equal(requestedOffsets, device.State.GpuClockOffsets?.Current);
    }

    [Fact]
    public async Task ScopedPerformance_DisablingManagedDefaultOffsetsRelinquishesPolling()
    {
        RecordingBladeDevice device = CreateMatchingDevice();
        device.State = device.State with
        {
            Performance = PerformancePreset.Balanced,
            Fans = FanMode.Automatic,
            CustomPerformance = null,
            GpuClockOffsets = new GpuClockOffsetCapabilities(
                "Test NVIDIA GPU",
                new GpuClockOffsetRange(-1000, 1000, 0),
                new GpuClockOffsetRange(-2000, 6000, 0)),
        };
        device.Capabilities = device.Capabilities with
        {
            PerformancePresets = new HashSet<PerformancePreset>
            {
                PerformancePreset.Balanced,
            },
            FanModes = new HashSet<FanMode> { FanMode.Automatic },
            GpuOverclock = WritableGpuOverclockCapabilities,
        };
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator.ApplyPerformanceOutcomeAsync(
            PerformancePreset.Balanced,
            customPerformance: null,
            GpuClockTuningSettings.Disabled,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, outcome.Result.Status);
        Assert.Equal(0, device.PerformanceSetCalls);
        Assert.Equal(1, device.GpuClockSetCalls);
        Assert.False(device.LastGpuClockRetainManagement);
        Assert.Null(device.State.GpuClockOffsets);
    }

    [Fact]
    public async Task ScopedPerformance_EnabledDefaultOffsetsRemainManaged()
    {
        RecordingBladeDevice device = CreateMatchingDevice();
        device.State = device.State with
        {
            Performance = PerformancePreset.Balanced,
            Fans = FanMode.Automatic,
            CustomPerformance = null,
            GpuClockOffsets = new GpuClockOffsetCapabilities(
                "Test NVIDIA GPU",
                new GpuClockOffsetRange(-1000, 1000, 125),
                new GpuClockOffsetRange(-2000, 6000, 500)),
        };
        device.Capabilities = device.Capabilities with
        {
            PerformancePresets = new HashSet<PerformancePreset>
            {
                PerformancePreset.Balanced,
            },
            FanModes = new HashSet<FanMode> { FanMode.Automatic },
            GpuOverclock = WritableGpuOverclockCapabilities,
        };
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator.ApplyPerformanceOutcomeAsync(
            PerformancePreset.Balanced,
            customPerformance: null,
            new GpuClockTuningSettings(
                Enabled: true,
                GpuClockOffsetSettings.Default),
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, outcome.Result.Status);
        Assert.True(outcome.ReadbackMatched);
        Assert.True(device.LastGpuClockRetainManagement);
        Assert.Equal(GpuClockOffsetSettings.Default, device.State.GpuClockOffsets?.Current);
    }

    [Fact]
    public async Task ScopedPerformance_DisabledUnknownTuningResetsOnlyOnce()
    {
        RecordingBladeDevice device = CreateMatchingDevice();
        device.State = device.State with
        {
            Performance = PerformancePreset.Balanced,
            Fans = FanMode.Automatic,
            CustomPerformance = null,
            GpuClockOffsets = null,
        };
        device.Capabilities = device.Capabilities with
        {
            PerformancePresets = new HashSet<PerformancePreset>
            {
                PerformancePreset.Balanced,
            },
            FanModes = new HashSet<FanMode> { FanMode.Automatic },
            GpuOverclock = WritableGpuOverclockCapabilities,
        };
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome first = await coordinator.ApplyPerformanceOutcomeAsync(
            PerformancePreset.Balanced,
            customPerformance: null,
            GpuClockTuningSettings.Disabled,
            CancellationToken.None);
        ScopedApplyOutcome second = await coordinator.ApplyPerformanceOutcomeAsync(
            PerformancePreset.Balanced,
            customPerformance: null,
            GpuClockTuningSettings.Disabled,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, first.Result.Status);
        Assert.Equal(ControlResultStatus.Unchanged, second.Result.Status);
        Assert.Equal(0, device.PerformanceSetCalls);
        Assert.Equal(1, device.GpuClockSetCalls);
        Assert.Equal(
            GpuClockOffsetManagementState.ResetConfirmed,
            device.GpuClockOffsetManagementState);
    }

    [Fact]
    public async Task FullReconcile_UnconfiguredGpuTuningRelinquishesBeforeStateRead()
    {
        RecordingBladeDevice device = CreateMatchingDevice();
        device.State = device.State with
        {
            GpuClockOffsets = new GpuClockOffsetCapabilities(
                "Test NVIDIA GPU",
                new GpuClockOffsetRange(-1000, 1000, 125),
                new GpuClockOffsetRange(-2000, 6000, 500)),
        };
        var coordinator = new ControlCoordinator(device);

        FullTransitionApplyOutcome outcome = await coordinator.ApplyDesiredStateOutcomeAsync(
            CreateDesiredState(),
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Unchanged, outcome.Results.Performance.Status);
        Assert.Equal(1, device.GpuClockRelinquishCalls);
        Assert.Equal(["Capabilities", "RelinquishGpuClockOffsets", "State"], device.Calls);
        Assert.Null(device.State.GpuClockOffsets);
        Assert.Equal(0, device.TotalSetterCalls);
    }

    [Fact]
    public async Task FullReconcile_UnavailableLightingConfirmationSkipsOnlyLighting()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        var coordinator = new ControlCoordinator(device);
        int transitionReads = 0;

        FullTransitionApplyOutcome outcome =
            await coordinator.ApplyDesiredStateOutcomeAsync(
                CreateDesiredState(),
                CancellationToken.None,
                stateReader: _ =>
                {
                    transitionReads++;
                    return Task.FromResult(new PowerTransitionDeviceStateReadResult(
                        transitionReads == 1
                            ? device.State with { Lighting = null }
                            : device.State,
                        LightingOffConfirmationUnavailable: transitionReads == 1));
                });

        Assert.Equal(2, transitionReads);
        Assert.Equal(ControlResultStatus.Applied, outcome.Results.Performance.Status);
        Assert.Equal(ControlResultStatus.Applied, outcome.Results.Fans.Status);
        Assert.Equal(ControlResultStatus.Applied, outcome.Results.Battery.Status);
        Assert.Equal(ControlResultStatus.Blocked, outcome.Results.Lighting.Status);
        Assert.Contains(
            "could not be confirmed during the power-source transition",
            outcome.Results.Lighting.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, device.PerformanceSetCalls);
        Assert.Equal(1, device.FanSetCalls);
        Assert.Equal(1, device.BatterySetCalls);
        Assert.Equal(0, device.LightingSetCalls);
        Assert.False(outcome.DeviceAccessFailed);
    }

    [Fact]
    public async Task FullReconcile_FinalLightingConfirmationUnavailableAcceptsCompletedWrite()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        var coordinator = new ControlCoordinator(device);
        int transitionReads = 0;

        FullTransitionApplyOutcome outcome =
            await coordinator.ApplyDesiredStateOutcomeAsync(
                CreateDesiredState(),
                CancellationToken.None,
                stateReader: _ =>
                {
                    transitionReads++;
                    return Task.FromResult(new PowerTransitionDeviceStateReadResult(
                        transitionReads == 1
                            ? device.State
                            : device.State with { Lighting = null },
                        LightingOffConfirmationUnavailable: transitionReads > 1));
                });

        Assert.Equal(ControlResultStatus.Applied, outcome.Results.Performance.Status);
        Assert.Equal(ControlResultStatus.Applied, outcome.Results.Fans.Status);
        Assert.Equal(ControlResultStatus.Applied, outcome.Results.Battery.Status);
        Assert.Equal(ControlResultStatus.Applied, outcome.Results.Lighting.Status);
        Assert.Contains(
            "does not report the resulting state",
            outcome.Results.Lighting.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(outcome.ConfirmationUnavailable);
        Assert.Null(outcome.PreWriteState);
        Assert.False(outcome.DeviceAccessFailed);
    }

    [Fact]
    public async Task FullReconcile_FinalLightingUnavailableTreatsLogoMismatchAsContradiction()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.Capabilities = device.Capabilities with
        {
            LogoLightingModes = new HashSet<LogoLightingMode>
            {
                LogoLightingMode.Off,
                LogoLightingMode.Static,
            },
        };
        device.State = device.State with { LogoLighting = LogoLightingMode.Off };
        DesiredDeviceState desired = CreateDesiredState() with
        {
            Lighting = new ResolvedSetting<LightingSettings>(
                TargetLighting with { LogoMode = LogoLightingMode.Static },
                SettingValueSource.PowerProfile),
        };
        var coordinator = new ControlCoordinator(device);
        int transitionReads = 0;

        FullTransitionApplyOutcome outcome =
            await coordinator.ApplyDesiredStateOutcomeAsync(
                desired,
                CancellationToken.None,
                stateReader: _ =>
                {
                    transitionReads++;
                    return Task.FromResult(new PowerTransitionDeviceStateReadResult(
                        transitionReads == 1
                            ? device.State
                            : device.State with { Lighting = null },
                        LightingOffConfirmationUnavailable: transitionReads > 1));
                });

        Assert.Equal(ControlResultStatus.Failed, outcome.Results.Lighting.Status);
        Assert.Null(outcome.ConfirmationUnavailable);
        Assert.Null(outcome.PreWriteState);
    }

    [Fact]
    public async Task FullReconcile_FinalLightingUnavailableAcceptsMissingLogoEvidence()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.Capabilities = device.Capabilities with
        {
            LogoLightingModes = new HashSet<LogoLightingMode>
            {
                LogoLightingMode.Off,
                LogoLightingMode.Static,
            },
        };
        device.State = device.State with { LogoLighting = LogoLightingMode.Off };
        DesiredDeviceState desired = CreateDesiredState() with
        {
            Lighting = new ResolvedSetting<LightingSettings>(
                TargetLighting with { LogoMode = LogoLightingMode.Static },
                SettingValueSource.PowerProfile),
        };
        var coordinator = new ControlCoordinator(device);
        int transitionReads = 0;

        FullTransitionApplyOutcome outcome =
            await coordinator.ApplyDesiredStateOutcomeAsync(
                desired,
                CancellationToken.None,
                stateReader: _ =>
                {
                    transitionReads++;
                    return Task.FromResult(new PowerTransitionDeviceStateReadResult(
                        transitionReads == 1
                            ? device.State
                            : device.State with
                            {
                                Lighting = null,
                                LogoLighting = null,
                            },
                        LightingOffConfirmationUnavailable: transitionReads > 1));
                });

        Assert.Equal(ControlResultStatus.Applied, outcome.Results.Lighting.Status);
        Assert.Contains(
            "does not report the resulting state",
            outcome.Results.Lighting.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Null(outcome.ConfirmationUnavailable);
        Assert.Null(outcome.PreWriteState);
    }

    [Fact]
    public async Task FullReconcile_FinalLightingUnavailableDoesNotAcceptFailedSetter()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.LightingFailure = new IOException("lighting write failed");
        var coordinator = new ControlCoordinator(device);
        int transitionReads = 0;

        FullTransitionApplyOutcome outcome =
            await coordinator.ApplyDesiredStateOutcomeAsync(
                CreateDesiredState(),
                CancellationToken.None,
                stateReader: _ =>
                {
                    transitionReads++;
                    return Task.FromResult(new PowerTransitionDeviceStateReadResult(
                        transitionReads == 1
                            ? device.State
                            : device.State with { Lighting = null },
                        LightingOffConfirmationUnavailable: transitionReads > 1));
                });

        Assert.Equal(ControlResultStatus.Failed, outcome.Results.Lighting.Status);
        Assert.Contains(
            DeviceSubsystem.Lighting,
            Assert.IsAssignableFrom<IReadOnlySet<DeviceSubsystem>>(
                outcome.ConfirmationUnavailable));
        Assert.NotNull(outcome.PreWriteState);
    }

    [Fact]
    public async Task FullReconcile_FinalReadFailureKeepsAcceptedLightingSetterApplied()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.ReadStateFailureOnCall = 2;
        var coordinator = new ControlCoordinator(device);

        FullTransitionApplyOutcome outcome =
            await coordinator.ApplyDesiredStateOutcomeAsync(
                CreateDesiredState(),
                CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, outcome.Results.Lighting.Status);
        Assert.Equal(
            "Lighting command completed, but this laptop does not report the resulting state.",
            outcome.Results.Lighting.Message);
        Assert.True(outcome.DeviceAccessFailed);
        Assert.Contains(DeviceSubsystem.Lighting, outcome.WriteAttempted);
        Assert.DoesNotContain(DeviceSubsystem.Lighting, outcome.ReadbackMatched);
        IReadOnlySet<DeviceSubsystem> confirmationUnavailable =
            Assert.IsAssignableFrom<IReadOnlySet<DeviceSubsystem>>(
                outcome.ConfirmationUnavailable);
        Assert.DoesNotContain(DeviceSubsystem.Lighting, confirmationUnavailable);
        Assert.Contains(DeviceSubsystem.Performance, confirmationUnavailable);
        Assert.Contains(DeviceSubsystem.Fans, confirmationUnavailable);
        Assert.Equal(ControlResultStatus.Failed, outcome.Results.Performance.Status);
        Assert.Contains(
            "indeterminate",
            outcome.Results.Performance.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, device.LightingSetCalls);
    }

    [Fact]
    public async Task FullReconcile_FinalReadFailureKeepsAcceptedManualFanSetterApplied()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.ReadStateFailureOnCall = 2;
        var coordinator = new ControlCoordinator(device);
        DesiredDeviceState desired = CreateDesiredState() with
        {
            Fans = new ResolvedSetting<FanMode>(
                FanMode.Fixed,
                SettingValueSource.PowerProfile),
        };

        FullTransitionApplyOutcome outcome =
            await coordinator.ApplyDesiredStateOutcomeAsync(
                desired,
                CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, outcome.Results.Fans.Status);
        Assert.Equal(
            "Fans command completed, but this laptop does not report the resulting state.",
            outcome.Results.Fans.Message);
        Assert.True(outcome.DeviceAccessFailed);
        Assert.Contains(DeviceSubsystem.Fans, outcome.WriteAttempted);
        Assert.DoesNotContain(DeviceSubsystem.Fans, outcome.ReadbackMatched);
        IReadOnlySet<DeviceSubsystem> confirmationUnavailable =
            Assert.IsAssignableFrom<IReadOnlySet<DeviceSubsystem>>(
                outcome.ConfirmationUnavailable);
        Assert.DoesNotContain(DeviceSubsystem.Fans, confirmationUnavailable);
        Assert.Contains(DeviceSubsystem.Performance, confirmationUnavailable);
        Assert.Equal(ControlResultStatus.Failed, outcome.Results.Performance.Status);
        Assert.Contains(
            "indeterminate",
            outcome.Results.Performance.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, device.FanSetCalls);
    }

    [Fact]
    public async Task FullReconcile_FinalReadFailureKeepsOffLightingIndeterminate()
    {
        RecordingBladeDevice device = CreateMatchingDevice();
        device.ReadStateFailureOnCall = 2;
        var coordinator = new ControlCoordinator(device);
        DesiredDeviceState desired = CreateDesiredState() with
        {
            Lighting = new ResolvedSetting<LightingSettings>(
                new LightingSettings(LightingEffect.Off, default, 0),
                SettingValueSource.PowerProfile),
        };

        FullTransitionApplyOutcome outcome =
            await coordinator.ApplyDesiredStateOutcomeAsync(
                desired,
                CancellationToken.None);

        Assert.Equal(ControlResultStatus.Failed, outcome.Results.Lighting.Status);
        Assert.Contains(
            "indeterminate",
            outcome.Results.Lighting.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            DeviceSubsystem.Lighting,
            Assert.IsAssignableFrom<IReadOnlySet<DeviceSubsystem>>(
                outcome.ConfirmationUnavailable));
    }

    [Fact]
    public async Task FullReconcile_FinalLightingConfirmationUnavailableDoesNotUndoOtherWrites()
    {
        RecordingBladeDevice device = CreateMatchingDevice();
        device.State = device.State with
        {
            Battery = new ConfirmedBatteryState(true, 80, false),
        };
        var coordinator = new ControlCoordinator(device);
        int transitionReads = 0;

        FullTransitionApplyOutcome outcome =
            await coordinator.ApplyDesiredStateOutcomeAsync(
                CreateDesiredState(),
                CancellationToken.None,
                stateReader: _ =>
                {
                    transitionReads++;
                    return Task.FromResult(new PowerTransitionDeviceStateReadResult(
                        transitionReads == 1
                            ? device.State
                            : device.State with { Lighting = null },
                        LightingOffConfirmationUnavailable: transitionReads > 1));
                });

        Assert.Equal(ControlResultStatus.Applied, outcome.Results.Battery.Status);
        Assert.Equal(ControlResultStatus.Blocked, outcome.Results.Lighting.Status);
        Assert.Equal(1, device.BatterySetCalls);
        Assert.Equal(0, device.LightingSetCalls);
        Assert.Null(outcome.ConfirmationUnavailable);
        Assert.False(outcome.DeviceAccessFailed);
    }

    [Fact]
    public async Task FullReconcile_CanceledTransitionStateReadPropagatesBeforeWrites()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        var coordinator = new ControlCoordinator(device);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.ApplyDesiredStateOutcomeAsync(
                CreateDesiredState(),
                cancellation.Token,
                stateReader: _ =>
                {
                    cancellation.Cancel();
                    return Task.FromCanceled<PowerTransitionDeviceStateReadResult>(
                        cancellation.Token);
                }));

        Assert.Equal(0, device.TotalSetterCalls);
    }

    [Fact]
    public async Task FullReconcile_WritableGpuWithoutManagementContractFailsClosed()
    {
        var device = new NonManagingWritableGpuDevice();
        var coordinator = new ControlCoordinator(device);
        DesiredDeviceState desired = CreateDesiredState() with
        {
            GpuClockTuning = GpuClockTuningSettings.Disabled,
        };

        FullTransitionApplyOutcome outcome =
            await coordinator.ApplyDesiredStateOutcomeAsync(desired, default);

        Assert.True(outcome.DeviceAccessFailed);
        Assert.Null(outcome.Readback);
        Assert.Equal(0, device.StateReadCalls);
        Assert.Equal(0, device.SetterCalls);
        Assert.All(
            new[]
            {
                outcome.Results.Performance,
                outcome.Results.Fans,
                outcome.Results.Battery,
                outcome.Results.Lighting,
            },
            result => Assert.Equal(ControlResultStatus.Failed, result.Status));
    }

    [Fact]
    public async Task FullReconcile_UnsupportedGpuTuningDropsOnlyGpuComponent()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        DesiredDeviceState desired = CreateDesiredState() with
        {
            GpuClockTuning = new GpuClockTuningSettings(
                Enabled: true,
                new GpuClockOffsetSettings(5, 10)),
        };
        var coordinator = new ControlCoordinator(device);

        SubsystemApplyResults results = await coordinator.ApplyDesiredStateAsync(
            desired,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, results.Performance.Status);
        Assert.Equal(ControlResultStatus.Applied, results.Fans.Status);
        Assert.Equal(1, device.PerformanceSetCalls);
        Assert.Equal(1, device.FanSetCalls);
        Assert.Equal(0, device.GpuClockSetCalls);
        Assert.Equal(PerformancePreset.Balanced, device.State.Performance);
        Assert.Equal(FanMode.Maximum, device.State.Fans);
    }

    [Fact]
    public async Task ScopedMaximumFans_RequireConfirmedBalancedAutomaticState()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator.ApplyFanOutcomeAsync(
            FanMode.Maximum,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Blocked, outcome.Result.Status);
        Assert.False(outcome.WriteAttempted);
        Assert.Equal(["Capabilities", "State"], device.Calls);
        Assert.Equal(0, device.FanSetCalls);
    }

    [Fact]
    public async Task ScopedAutomaticFans_AreAllowedFromMaximumState()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.State = device.State with
        {
            Performance = null,
            Fans = FanMode.Maximum,
        };
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator.ApplyFanOutcomeAsync(
            FanMode.Automatic,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, outcome.Result.Status);
        Assert.True(outcome.ReadbackMatched);
        Assert.Equal(["Capabilities", "State", "SetFans", "State"], device.Calls);
    }

    [Fact]
    public async Task ScopedFixedFans_ResubmitTargetFromProductionGenericManualReadback()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.State = device.State with
        {
            Performance = null,
            Fans = FanMode.Maximum,
        };
        device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Fans)
            {
                device.State = device.State with
                {
                    Performance = null,
                    Fans = FanMode.Maximum,
                };
            }
        };
        var coordinator = new ControlCoordinator(device);
        var requested = new FanControlSettings(
            FanMode.Fixed,
            2800,
            ManualFanCurve.Default);

        ScopedApplyOutcome outcome = await coordinator.ApplyFanControlOutcomeAsync(
            requested,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, outcome.Result.Status);
        Assert.True(outcome.WriteAttempted);
        Assert.Equal(1, device.FanSetCalls);
        Assert.Equal(["Capabilities", "State", "SetFans", "State"], device.Calls);
    }

    [Fact]
    public async Task ScopedManualFans_RequireBalancedWhenFirmwareIsAutomatic()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        var coordinator = new ControlCoordinator(device);
        var requested = new FanControlSettings(
            FanMode.ManualCurve,
            2800,
            ManualFanCurve.Default);

        ScopedApplyOutcome outcome = await coordinator.ApplyFanControlOutcomeAsync(
            requested,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Blocked, outcome.Result.Status);
        Assert.False(outcome.WriteAttempted);
        Assert.Empty(device.FanControlWrites);
        Assert.Equal(["Capabilities", "State"], device.Calls);
    }

    [Fact]
    public async Task ManualFanSubtypeTransition_BridgesThroughAutomatic()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.State = device.State with
        {
            Performance = null,
            Fans = FanMode.Maximum,
        };
        device.AfterSetter = subsystem =>
        {
            if (subsystem != DeviceSubsystem.Fans)
            {
                return;
            }

            device.State = device.State.Fans == FanMode.Automatic
                ? device.State with { Performance = PerformancePreset.Balanced }
                : device.State with
                {
                    Performance = null,
                    Fans = FanMode.Maximum,
                };
        };
        var coordinator = new ControlCoordinator(device);
        var previous = new FanControlSettings(FanMode.Fixed, 2800, ManualFanCurve.Default);
        var requested = new FanControlSettings(
            FanMode.ManualCurve,
            2800,
            ManualFanCurve.Default);

        ScopedApplyOutcome outcome = await coordinator.ApplyFanControlTransitionOutcomeAsync(
            previous,
            requested,
            PerformancePreset.Balanced,
            customPerformanceIntent: null,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, outcome.Result.Status);
        Assert.Equal([FanMode.Automatic, FanMode.ManualCurve], device.FanControlWrites);
    }

    [Fact]
    public async Task SameManualFanSubtypeUpdate_DoesNotBridgeThroughAutomatic()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.State = device.State with
        {
            Performance = null,
            Fans = FanMode.Maximum,
        };
        var coordinator = new ControlCoordinator(device);
        var previous = new FanControlSettings(FanMode.Fixed, 2800, ManualFanCurve.Default);
        var requested = new FanControlSettings(
            FanMode.Fixed,
            3200,
            ManualFanCurve.Default);

        ScopedApplyOutcome outcome = await coordinator.ApplyFanControlTransitionOutcomeAsync(
            previous,
            requested,
            PerformancePreset.Balanced,
            customPerformanceIntent: null,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, outcome.Result.Status);
        Assert.Equal([FanMode.Fixed], device.FanControlWrites);
    }

    [Fact]
    public async Task ManualSubtypeTransition_BalancedRejectionRetainsAutomaticWriteEvidence()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.State = device.State with
        {
            Performance = null,
            Fans = FanMode.Fixed,
        };
        device.PerformanceFailure = new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            "firmware rejected performance mode");
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator.ApplyFanControlTransitionOutcomeAsync(
            FanControlSettings.FromLegacy(FanMode.Fixed),
            FanControlSettings.FromLegacy(FanMode.ManualCurve),
            PerformancePreset.Performance,
            customPerformanceIntent: null,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Failed, outcome.Result.Status);
        Assert.True(outcome.WriteAttempted);
        Assert.Equal([FanMode.Automatic], device.FanControlWrites);
        Assert.Equal([PerformancePreset.Balanced], device.PerformanceWrites);
        Assert.Equal(FanMode.Automatic, device.State.Fans);
    }

    [Fact]
    public async Task ManualFanEntry_FromPerformanceBridgesThroughBalancedAutomatic()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.State = device.State with
        {
            Performance = PerformancePreset.Performance,
            Fans = FanMode.Automatic,
        };
        device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Fans)
            {
                device.State = device.State with { Performance = null };
            }
        };
        var coordinator = new ControlCoordinator(device);
        var requested = new FanControlSettings(
            FanMode.Fixed,
            3200,
            ManualFanCurve.Default);

        ScopedApplyOutcome outcome = await coordinator
            .ApplyFanControlTransitionOutcomeAsync(
                FanControlSettings.FromLegacy(FanMode.Automatic),
                requested,
                PerformancePreset.Performance,
                customPerformanceIntent: null,
                CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, outcome.Result.Status);
        Assert.Equal([PerformancePreset.Balanced], device.PerformanceWrites);
        Assert.Equal([FanMode.Fixed], device.FanControlWrites);
        Assert.True(
            device.Calls.IndexOf("SetPerformance") < device.Calls.IndexOf("SetFans"));
        Assert.Null(device.State.Performance);
        Assert.Equal(FanMode.Fixed, device.State.Fans);
    }

    [Fact]
    public async Task AutomaticFanEntry_FromManualRestoresSavedPerformanceIntent()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.State = device.State with
        {
            Performance = null,
            Fans = FanMode.Maximum,
        };
        device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Fans
                && device.State.Fans == FanMode.Automatic)
            {
                device.State = device.State with
                {
                    Performance = PerformancePreset.Balanced,
                };
            }
        };
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator
            .ApplyFanControlTransitionOutcomeAsync(
                FanControlSettings.FromLegacy(FanMode.Fixed),
                FanControlSettings.FromLegacy(FanMode.Automatic),
                PerformancePreset.Performance,
                customPerformanceIntent: null,
                CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, outcome.Result.Status);
        Assert.Equal([FanMode.Automatic], device.FanControlWrites);
        Assert.Equal([PerformancePreset.Performance], device.PerformanceWrites);
        Assert.True(
            device.Calls.IndexOf("SetFans") < device.Calls.IndexOf("SetPerformance"));
        Assert.Equal(PerformancePreset.Performance, device.State.Performance);
        Assert.Equal(FanMode.Automatic, device.State.Fans);
    }

    [Fact]
    public async Task ManualFanEntry_KnownNotAppliedRestoresPerformanceWithoutFanRetry()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.State = device.State with
        {
            Performance = PerformancePreset.Performance,
            Fans = FanMode.Automatic,
        };
        device.FanFailure = new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            "firmware rejected fan mode");
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator
            .ApplyFanControlTransitionOutcomeAsync(
                FanControlSettings.FromLegacy(FanMode.Automatic),
                FanControlSettings.FromLegacy(FanMode.Fixed),
                PerformancePreset.Performance,
                customPerformanceIntent: null,
                CancellationToken.None);

        Assert.Equal(ControlResultStatus.Failed, outcome.Result.Status);
        Assert.Contains("restored", outcome.Result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            [PerformancePreset.Balanced, PerformancePreset.Performance],
            device.PerformanceWrites);
        Assert.Equal(1, device.FanSetCalls);
        Assert.Equal(PerformancePreset.Performance, device.State.Performance);
        Assert.Equal(FanMode.Automatic, device.State.Fans);
    }

    [Fact]
    public async Task ManualFanEntry_IndeterminateWriteDoesNotRestoreOrRetry()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.State = device.State with
        {
            Performance = PerformancePreset.Performance,
            Fans = FanMode.Automatic,
        };
        device.FanFailure = new BladeWriteException(
            WriteSubmissionState.Indeterminate,
            "fan acknowledgement was lost");
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator
            .ApplyFanControlTransitionOutcomeAsync(
                FanControlSettings.FromLegacy(FanMode.Automatic),
                FanControlSettings.FromLegacy(FanMode.Fixed),
                PerformancePreset.Performance,
                customPerformanceIntent: null,
                CancellationToken.None);

        Assert.Equal(ControlResultStatus.Failed, outcome.Result.Status);
        Assert.True(outcome.WriteAttempted);
        Assert.Equal([PerformancePreset.Balanced], device.PerformanceWrites);
        Assert.Equal(1, device.FanSetCalls);
        Assert.Equal(PerformancePreset.Balanced, device.State.Performance);
        Assert.Equal(FanMode.Automatic, device.State.Fans);
    }

    [Fact]
    public async Task ManualFanEntry_CanceledAfterBalancedWriteReportsPerformanceAttempt()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.State = device.State with
        {
            Performance = PerformancePreset.Performance,
            Fans = FanMode.Automatic,
        };
        using var cancellation = new CancellationTokenSource();
        device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Performance)
            {
                cancellation.Cancel();
            }
        };
        var coordinator = new ControlCoordinator(device);

        IntentionalWriteCanceledException exception = await Assert.ThrowsAsync<
            IntentionalWriteCanceledException>(
            () => coordinator.ApplyFanControlTransitionOutcomeAsync(
                FanControlSettings.FromLegacy(FanMode.Automatic),
                FanControlSettings.FromLegacy(FanMode.Fixed),
                PerformancePreset.Performance,
                customPerformanceIntent: null,
                cancellation.Token));

        Assert.Equal([DeviceSubsystem.Performance], exception.WriteAttempted);
        Assert.Equal([PerformancePreset.Balanced], device.PerformanceWrites);
        Assert.Equal(0, device.FanSetCalls);
    }

    [Fact]
    public async Task ScopedKnownNotApplied_IsRejectedWithoutAttemptedOrIndeterminateState()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.PerformanceFailure = new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            "firmware rejected setter");
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator
            .ApplyPerformancePreservingGpuClockManagementOutcomeAsync(
            PerformancePreset.Balanced,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Failed, outcome.Result.Status);
        Assert.Contains("not applied", outcome.Result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(outcome.WriteAttempted);
        Assert.False(outcome.DeviceAccessFailed);
        Assert.Same(device.State, outcome.Readback);
        Assert.DoesNotContain("indeterminate", outcome.Result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScopedCapabilityRefusal_IsBlockedWithoutAttemptingReadback()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.PerformanceFailure =
            new CapabilityUnavailableException("performance capability changed");
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator
            .ApplyPerformancePreservingGpuClockManagementOutcomeAsync(
            PerformancePreset.Balanced,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Blocked, outcome.Result.Status);
        Assert.Contains("unavailable", outcome.Result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(outcome.WriteAttempted);
        Assert.False(outcome.DeviceAccessFailed);
        Assert.Same(device.State, outcome.Readback);
        Assert.Equal(["Capabilities", "State", "SetPerformance"], device.Calls);
    }

    [Fact]
    public async Task ScopedApply_InvalidLighting_IsBlockedBeforeDeviceAccess()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        var coordinator = new ControlCoordinator(device);

        ControlResult result = await coordinator.ApplyLightingAsync(
            new LightingSettings(LightingEffect.Spectrum, new RgbColor(1, 2, 3), 20),
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Blocked, result.Status);
        Assert.Empty(device.Calls);
    }

    [Fact]
    public async Task ScopedApply_MismatchedReadbackFailsWithoutRetry()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.AutoConfirmWrites = false;
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator
            .ApplyPerformancePreservingGpuClockManagementOutcomeAsync(
            PerformancePreset.Balanced,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Failed, outcome.Result.Status);
        Assert.Contains("indeterminate", outcome.Result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(device.State, outcome.Readback);
        Assert.False(outcome.ReadbackMatched);
        Assert.False(outcome.ConfirmationUnavailable);
        Assert.Equal(["Capabilities", "State", "SetPerformance", "State"], device.Calls);
        Assert.Equal(1, device.PerformanceSetCalls);
    }

    [Fact]
    public async Task ScopedApply_PostWriteReadbackFailureConfirmsTargetWithoutRetry()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.ReadStateFailureOnCall = 2;
        var coordinator = new ControlCoordinator(
            device,
            device.ReadStateAsync,
            resolveIndeterminatePerformanceWrites: true);

        ScopedApplyOutcome outcome = await coordinator
            .ApplyPerformancePreservingGpuClockManagementOutcomeAsync(
                PerformancePreset.Balanced,
                CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, outcome.Result.Status);
        Assert.Contains(
            "fresh readback",
            outcome.Result.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(outcome.ReadbackMatched);
        Assert.False(outcome.ConfirmationUnavailable);
        Assert.Equal(WriteSubmissionState.Indeterminate, outcome.WriteSubmissionState);
        Assert.Equal(
            ["Capabilities", "State", "SetPerformance", "State", "State"],
            device.Calls);
        Assert.Equal(1, device.PerformanceSetCalls);
    }

    [Fact]
    public async Task ScopedApply_PostWriteReadbackFailureConfirmsPriorWithoutRetry()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.AutoConfirmWrites = false;
        device.ReadStateFailureOnCall = 2;
        var coordinator = new ControlCoordinator(
            device,
            device.ReadStateAsync,
            resolveIndeterminatePerformanceWrites: true);

        ScopedApplyOutcome outcome = await coordinator
            .ApplyPerformancePreservingGpuClockManagementOutcomeAsync(
                PerformancePreset.Balanced,
                CancellationToken.None);

        Assert.Equal(ControlResultStatus.Failed, outcome.Result.Status);
        Assert.Contains(
            "prior performance state remained active",
            outcome.Result.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(outcome.WriteAttempted);
        Assert.False(outcome.ConfirmationUnavailable);
        Assert.Equal(WriteSubmissionState.Indeterminate, outcome.WriteSubmissionState);
        Assert.Equal(
            ["Capabilities", "State", "SetPerformance", "State", "State"],
            device.Calls);
        Assert.Equal(1, device.PerformanceSetCalls);
    }

    [Fact]
    public async Task ScopedApply_PostWriteAndFreshReadFailuresRemainIndeterminate()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.ReadStateFailureCalls.UnionWith([2, 3]);
        var coordinator = new ControlCoordinator(
            device,
            device.ReadStateAsync,
            resolveIndeterminatePerformanceWrites: true);

        ConfirmedDeviceState before = device.State;
        ScopedApplyOutcome outcome = await coordinator
            .ApplyPerformancePreservingGpuClockManagementOutcomeAsync(
                PerformancePreset.Balanced,
                CancellationToken.None);

        Assert.Equal(ControlResultStatus.Failed, outcome.Result.Status);
        Assert.Contains(
            "indeterminate",
            outcome.Result.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(outcome.WriteAttempted);
        Assert.True(outcome.ConfirmationUnavailable);
        Assert.Same(before, outcome.PreWriteState);
        Assert.Null(outcome.Readback);
        Assert.Equal(WriteSubmissionState.Indeterminate, outcome.WriteSubmissionState);
        Assert.Equal(
            ["Capabilities", "State", "SetPerformance", "State", "State"],
            device.Calls);
        Assert.Equal(1, device.PerformanceSetCalls);
    }

    [Fact]
    public async Task ScopedApply_CancellationBeforeSetterPropagatesWithoutWrite()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        using var cancellation = new CancellationTokenSource();
        device.AfterReadState = call =>
        {
            if (call == 1)
            {
                cancellation.Cancel();
            }
        };
        var coordinator = new ControlCoordinator(device);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.ApplyPerformanceAsync(
                PerformancePreset.Balanced,
                cancellation.Token));

        Assert.Equal(["Capabilities", "State"], device.Calls);
        Assert.Equal(0, device.TotalSetterCalls);
    }

    [Fact]
    public async Task ScopedApply_CancellationAfterSetterPropagatesWithoutReadbackOrRetry()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        using var cancellation = new CancellationTokenSource();
        device.AfterSetter = _ => cancellation.Cancel();
        var coordinator = new ControlCoordinator(device);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.ApplyPerformanceAsync(
                PerformancePreset.Balanced,
                cancellation.Token));

        Assert.Equal(["Capabilities", "State", "SetPerformance"], device.Calls);
        Assert.Equal(1, device.PerformanceSetCalls);
    }

    [Fact]
    public async Task TemporaryFullCharge_IsBlockedWithoutBatterySetter()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        var coordinator = new ControlCoordinator(device);
        var temporary = new DesiredBatteryState(
            BatteryPolicy.Create(80),
            protectionEnabled: true,
            temporaryFullCharge: true);

        ScopedApplyOutcome outcome = await coordinator.ApplyBatteryOutcomeAsync(
            temporary,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Blocked, outcome.Result.Status);
        Assert.Equal(["Capabilities", "State"], device.Calls);
        Assert.Equal(0, device.BatterySetCalls);
    }

    [Fact]
    public async Task ApplyDesiredState_UsesOneInitialAndOneFinalAggregateRead()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        var coordinator = new ControlCoordinator(device);

        SubsystemApplyResults results = await coordinator.ApplyDesiredStateAsync(
            CreateDesiredState(),
            CancellationToken.None);

        Assert.All(
            [results.Performance, results.Fans, results.Battery, results.Lighting],
            result => Assert.Equal(ControlResultStatus.Applied, result.Status));
        Assert.Equal(
            [
                "Capabilities",
                "State",
                "SetPerformance",
                "SetFans",
                "SetChargeLimit",
                "SetLighting",
                "State",
            ],
            device.Calls);
        Assert.Equal(4, device.TotalSetterCalls);
    }

    [Theory]
    [InlineData(PerformancePreset.BatterySaver)]
    [InlineData(PerformancePreset.BatteryBalanced)]
    public async Task ApplyDesiredState_AcceptsBatteryThermalPairOnlyWithExplicitCapability(
        PerformancePreset performance)
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.State = device.State with
        {
            Performance = PerformancePreset.Balanced,
            Fans = FanMode.Automatic,
        };
        device.Capabilities = device.Capabilities with
        {
            PerformancePresets = new HashSet<PerformancePreset> { performance },
            FanModes = new HashSet<FanMode> { FanMode.Automatic },
        };
        var coordinator = new ControlCoordinator(device);
        DesiredDeviceState desired = CreateThermalDesired(
            device.State,
            performance,
            FanMode.Automatic);

        SubsystemApplyResults results = await coordinator.ApplyDesiredStateAsync(
            desired,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, results.Performance.Status);
        Assert.Equal(ControlResultStatus.Unchanged, results.Fans.Status);
        Assert.Equal(1, device.PerformanceSetCalls);
        Assert.Equal(0, device.FanSetCalls);
    }

    [Fact]
    public async Task ApplyDesiredState_MaximumToAutomatic_WritesFansBeforePerformance()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.State = device.State with
        {
            Performance = null,
            Fans = FanMode.Maximum,
        };
        var coordinator = new ControlCoordinator(device);
        DesiredDeviceState desired = CreateThermalDesired(
            device.State,
            PerformancePreset.Silent,
            FanMode.Automatic);

        SubsystemApplyResults results = await coordinator.ApplyDesiredStateAsync(
            desired,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, results.Fans.Status);
        Assert.Equal(ControlResultStatus.Applied, results.Performance.Status);
        Assert.True(device.Calls.IndexOf("SetFans") < device.Calls.IndexOf("SetPerformance"));
    }

    [Fact]
    public async Task ApplyDesiredState_AutomaticToMaximum_WritesPerformanceBeforeFans()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        var coordinator = new ControlCoordinator(device);
        DesiredDeviceState desired = CreateThermalDesired(
            device.State,
            PerformancePreset.Balanced,
            FanMode.Maximum);

        SubsystemApplyResults results = await coordinator.ApplyDesiredStateAsync(
            desired,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, results.Performance.Status);
        Assert.Equal(ControlResultStatus.Applied, results.Fans.Status);
        Assert.True(device.Calls.IndexOf("SetPerformance") < device.Calls.IndexOf("SetFans"));
    }

    [Fact]
    public async Task ApplyDesiredState_AutomaticToMaximum_RejectedPerformanceBlocksFans()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.PerformanceFailure = new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            "performance setter rejected");
        var coordinator = new ControlCoordinator(device);
        DesiredDeviceState desired = CreateThermalDesired(
            device.State,
            PerformancePreset.Balanced,
            FanMode.Maximum);

        SubsystemApplyResults results = await coordinator.ApplyDesiredStateAsync(
            desired,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Failed, results.Performance.Status);
        Assert.Equal(ControlResultStatus.Blocked, results.Fans.Status);
        Assert.Equal(1, device.PerformanceSetCalls);
        Assert.Equal(0, device.FanSetCalls);
    }

    [Fact]
    public async Task ApplyDesiredState_MaximumToAutomatic_RejectedFansBlockPerformance()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.State = device.State with
        {
            Performance = null,
            Fans = FanMode.Maximum,
        };
        device.FanFailure = new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            "fan setter rejected");
        var coordinator = new ControlCoordinator(device);
        DesiredDeviceState desired = CreateThermalDesired(
            device.State,
            PerformancePreset.Silent,
            FanMode.Automatic);

        SubsystemApplyResults results = await coordinator.ApplyDesiredStateAsync(
            desired,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Failed, results.Fans.Status);
        Assert.Equal(ControlResultStatus.Blocked, results.Performance.Status);
        Assert.Equal(1, device.FanSetCalls);
        Assert.Equal(0, device.PerformanceSetCalls);
    }

    [Fact]
    public async Task ApplyDesiredState_FinalMismatchFailsOnlyMismatchedAttempt()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.SuppressConfirmationFor = DeviceSubsystem.Fans;
        var coordinator = new ControlCoordinator(device);

        SubsystemApplyResults results = await coordinator.ApplyDesiredStateAsync(
            CreateDesiredState(),
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, results.Performance.Status);
        Assert.Equal(ControlResultStatus.Failed, results.Fans.Status);
        Assert.Equal(ControlResultStatus.Applied, results.Battery.Status);
        Assert.Equal(ControlResultStatus.Applied, results.Lighting.Status);
        Assert.Equal(1, device.FanSetCalls);
        Assert.Equal(2, device.StateReadCalls);
    }

    [Fact]
    public async Task ApplyDesiredState_ManualFansKeepPerformanceAsLatentIntent()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.State = device.State with { Fans = FanMode.Maximum };
        var coordinator = new ControlCoordinator(device);

        SubsystemApplyResults results = await coordinator.ApplyDesiredStateAsync(
            CreateDesiredState(),
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Unchanged, results.Performance.Status);
        Assert.Equal(ControlResultStatus.Unchanged, results.Fans.Status);
        Assert.Equal(0, device.PerformanceSetCalls);
        Assert.Equal(0, device.FanSetCalls);
        Assert.Equal(FanMode.Maximum, device.State.Fans);
        Assert.Equal(2, device.StateReadCalls);
    }

    [Fact]
    public async Task ApplyDesiredState_ManualFansBridgeAutomaticPerformanceThroughBalanced()
    {
        RecordingBladeDevice device = CreateMatchingDevice();
        device.State = device.State with
        {
            Performance = PerformancePreset.Performance,
            Fans = FanMode.Automatic,
        };
        device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Fans)
            {
                device.State = device.State with { Performance = null };
            }
        };
        var coordinator = new ControlCoordinator(device);
        DesiredDeviceState desired = CreateThermalDesired(
            device.State,
            PerformancePreset.Balanced,
            FanMode.Maximum);

        SubsystemApplyResults results = await coordinator.ApplyDesiredStateAsync(
            desired,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, results.Performance.Status);
        Assert.Equal(ControlResultStatus.Applied, results.Fans.Status);
        Assert.Equal([PerformancePreset.Balanced], device.PerformanceWrites);
        Assert.Equal([FanMode.Maximum], device.FanControlWrites);
        Assert.True(
            device.Calls.IndexOf("SetPerformance") < device.Calls.IndexOf("SetFans"));
        Assert.Null(device.State.Performance);
        Assert.Equal(FanMode.Maximum, device.State.Fans);
    }

    [Fact]
    public async Task ApplyDesiredState_TerminalSetterFailureStopsLaterWritesButStillReadsBackOnce()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.PerformanceFailure = new IOException("device disconnected");
        var coordinator = new ControlCoordinator(device);

        SubsystemApplyResults results = await coordinator.ApplyDesiredStateAsync(
            CreateDesiredState(),
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Failed, results.Performance.Status);
        Assert.Equal(ControlResultStatus.Blocked, results.Fans.Status);
        Assert.Equal(ControlResultStatus.Blocked, results.Battery.Status);
        Assert.Equal(ControlResultStatus.Blocked, results.Lighting.Status);
        Assert.Equal(["Capabilities", "State", "SetPerformance", "State"], device.Calls);
        Assert.Equal(1, device.PerformanceSetCalls);
        Assert.Equal(0, device.FanSetCalls);
        Assert.Equal(0, device.BatterySetCalls);
        Assert.Equal(0, device.LightingSetCalls);
    }

    private static Task<ControlResult> ApplyScopedAsync(
        ControlCoordinator coordinator,
        DeviceSubsystem subsystem) => subsystem switch
        {
            DeviceSubsystem.Performance => coordinator.ApplyPerformanceAsync(
                PerformancePreset.Balanced,
                CancellationToken.None),
            DeviceSubsystem.Fans => coordinator.ApplyFanAsync(
                FanMode.Maximum,
                CancellationToken.None),
            DeviceSubsystem.Battery => coordinator.ApplyChargeLimitAsync(
                BatteryPolicy.Create(75),
                CancellationToken.None),
            DeviceSubsystem.Lighting => coordinator.ApplyLightingAsync(
                TargetLighting,
                CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(subsystem)),
        };

    private static RecordingBladeDevice CreateMismatchedDevice() => new()
    {
        State = new ConfirmedDeviceState(
            PerformancePreset.Silent,
            FanMode.Automatic,
            new ConfirmedBatteryState(true, 80, false),
            new LightingSettings(LightingEffect.Off, default, 0)),
    };

    private static RecordingBladeDevice CreateMatchingDevice() => new()
    {
        State = new ConfirmedDeviceState(
            PerformancePreset.Balanced,
            FanMode.Maximum,
            new ConfirmedBatteryState(true, 75, false),
            TargetLighting),
    };

    private static DesiredDeviceState CreateDesiredState() => new(
        new ResolvedSetting<PerformancePreset>(
            PerformancePreset.Balanced,
            SettingValueSource.PowerProfile),
        new ResolvedSetting<FanMode>(FanMode.Maximum, SettingValueSource.PowerProfile),
        new ResolvedSetting<LightingSettings>(
            TargetLighting,
            SettingValueSource.PowerProfile),
        new DesiredBatteryState(BatteryPolicy.Create(75), true, false));

    private static DesiredDeviceState CreateThermalDesired(
        ConfirmedDeviceState current,
        PerformancePreset performance,
        FanMode fans) => new(
        new ResolvedSetting<PerformancePreset>(
            performance,
            SettingValueSource.PowerProfile),
        new ResolvedSetting<FanMode>(fans, SettingValueSource.PowerProfile),
        new ResolvedSetting<LightingSettings>(
            current.Lighting!,
            SettingValueSource.PowerProfile),
        new DesiredBatteryState(
            BatteryPolicy.Create(current.Battery!.ChargeLimitPercent!.Value),
            current.Battery.ProtectionEnabled,
            current.Battery.TemporaryFullCharge ?? false));

    private static LightingSettings TargetLighting { get; } = new(
        LightingEffect.Static,
        new RgbColor(10, 20, 30),
        40);

    private static GpuOverclockCapabilities WritableGpuOverclockCapabilities { get; } =
        new(
            new DeviceNumericRange(-1000, 400, null),
            new DeviceNumericRange(-1000, 3000, null),
            DeviceFeatureAccess.ReadWrite);

    private sealed class NonManagingWritableGpuDevice : IBladeDevice
    {
        public int StateReadCalls { get; private set; }
        public int SetterCalls { get; private set; }

        public Task<DeviceCapabilities> ReadCapabilitiesAsync(
            CancellationToken cancellationToken) => Task.FromResult(
            DeviceCapabilities.SafeTargetDefaults() with
            {
                GpuOverclock = WritableGpuOverclockCapabilities,
            });

        public Task<ConfirmedDeviceState> ReadStateAsync(
            CancellationToken cancellationToken)
        {
            StateReadCalls++;
            return Task.FromResult(new ConfirmedDeviceState(null, null, null, null));
        }

        public async Task<PowerTransitionDeviceStateReadResult>
            ReadStateForPowerTransitionAsync(CancellationToken cancellationToken) =>
            new(
                await ReadStateAsync(cancellationToken),
                LightingOffConfirmationUnavailable: false);

        public Task<FanRpmReadings?> ReadFanRpmAsync(
            CancellationToken cancellationToken) => Task.FromResult<FanRpmReadings?>(null);

        public Task<AdapterPowerState> ReadAdapterPowerStateAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(AdapterPowerState.FullPower280W);

        public Task SetPerformanceAsync(
            PerformancePreset value,
            CancellationToken cancellationToken) => UnexpectedSetter();

        public Task SetPerformanceAsync(
            PerformancePreset value,
            CustomPerformanceSettings? customPerformance,
            CancellationToken cancellationToken) => UnexpectedSetter();

        public Task SetPerformanceAsync(
            PerformancePreset value,
            CustomPerformanceSettings? customPerformance,
            GpuClockOffsetSettings? gpuClockOffsets,
            CancellationToken cancellationToken) => UnexpectedSetter();

        public Task SetGpuClockOffsetsAsync(
            GpuClockOffsetSettings value,
            CancellationToken cancellationToken) => UnexpectedSetter();

        public Task SetFanModeAsync(
            FanMode value,
            CancellationToken cancellationToken) => UnexpectedSetter();

        public Task SetFanControlAsync(
            FanControlSettings value,
            CancellationToken cancellationToken) => UnexpectedSetter();

        public Task SetFanTargetsAsync(
            int fan1Rpm,
            int fan2Rpm,
            CancellationToken cancellationToken) => UnexpectedSetter();

        public Task SetChargeLimitAsync(
            BatteryPolicy value,
            CancellationToken cancellationToken) => UnexpectedSetter();

        public Task SetBatteryProtectionOffAsync(
            CancellationToken cancellationToken) => UnexpectedSetter();

        public Task SetTemporaryFullChargeAsync(
            bool enabled,
            CancellationToken cancellationToken) => UnexpectedSetter();

        public Task SetLightingAsync(
            LightingSettings value,
            CancellationToken cancellationToken) => UnexpectedSetter();

        public Task<byte> StepKeyboardBrightnessAsync(
            KeyboardBrightnessDirection direction,
            CancellationToken cancellationToken) => Task.FromResult((byte)0);

        public Task ApplyOneShotLaptopControlAsync(
            ApplyOneShotLaptopControl value,
            CancellationToken cancellationToken) => UnexpectedSetter();

        public Task PulseReactiveKeyAsync(
            ReactiveKeyPulse pulse,
            CancellationToken cancellationToken) => UnexpectedSetter();

        public Task PushAmbientLightingFrameAsync(
            AmbientLightingFrame frame,
            CancellationToken cancellationToken) => UnexpectedSetter();

        public Task SetPlaybackMuteIndicatorAsync(
            bool muted,
            CancellationToken cancellationToken) => UnexpectedSetter();

        private Task UnexpectedSetter()
        {
            SetterCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingBladeDevice :
        IBladeDevice,
        IGpuClockOffsetManagementDevice
    {
        public List<string> Calls { get; } = [];

        public List<string> BatterySetterCalls { get; } = [];

        public List<PerformancePreset> PerformanceWrites { get; } = [];

        public List<FanMode> FanControlWrites { get; } = [];

        public DeviceCapabilities Capabilities { get; set; } =
            DeviceCapabilities.SafeTargetDefaults() with
            {
                ChargeLimits = new HashSet<int> { 50, 55, 60, 65, 70, 75, 80 },
                LightingEffects = new HashSet<LightingEffect>
                {
                    LightingEffect.Off,
                    LightingEffect.Static,
                },
            };

        public ConfirmedDeviceState State { get; set; } = new(null, null, null, null);

        public bool AutoConfirmWrites { get; set; } = true;

        public DeviceSubsystem? SuppressConfirmationFor { get; set; }

        public int? ReadStateFailureOnCall { get; set; }

        public HashSet<int> ReadStateFailureCalls { get; } = [];

        public int? ReadLightingStateFailureOnCall { get; set; }

        public Exception? PerformanceFailure { get; set; }

        public Exception? FanFailure { get; set; }

        public Exception? BatteryWriteFailure { get; set; }

        public Exception? LightingFailure { get; set; }

        public Action<int>? AfterReadState { get; set; }

        public Action<DeviceSubsystem>? AfterSetter { get; set; }

        public int PerformanceSetCalls { get; private set; }

        public int GpuClockSetCalls { get; private set; }

        public int GpuClockRelinquishCalls { get; private set; }

        public bool? LastGpuClockRetainManagement { get; private set; }

        public GpuClockOffsetManagementState GpuClockOffsetManagementState
        {
            get;
            private set;
        }

        public int FanSetCalls { get; private set; }

        public int BatterySetCalls { get; private set; }

        public int LightingSetCalls { get; private set; }

        public int StateReadCalls { get; private set; }

        public int LightingStateReadCalls { get; private set; }

        public int TotalSetterCalls => PerformanceSetCalls
            + GpuClockSetCalls
            + FanSetCalls
            + BatterySetCalls
            + LightingSetCalls;

        public Task<DeviceCapabilities> ReadCapabilitiesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("Capabilities");
            return Task.FromResult(Capabilities);
        }

        public Task<ConfirmedDeviceState> ReadStateAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("State");
            StateReadCalls++;
            if (ReadStateFailureOnCall == StateReadCalls
                || ReadStateFailureCalls.Contains(StateReadCalls))
            {
                throw new IOException("readback failed");
            }

            AfterReadState?.Invoke(StateReadCalls);
            return Task.FromResult(State);
        }

        public Task<ConfirmedDeviceState> ReadLightingStateAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("LightingState");
            LightingStateReadCalls++;
            if (ReadLightingStateFailureOnCall == LightingStateReadCalls)
            {
                throw new IOException("lighting readback failed");
            }

            return Task.FromResult(State);
        }

        public async Task<PowerTransitionDeviceStateReadResult>
            ReadStateForPowerTransitionAsync(CancellationToken cancellationToken) =>
            new(
                await ReadStateAsync(cancellationToken),
                LightingOffConfirmationUnavailable: false);

        public Task<FanRpmReadings?> ReadFanRpmAsync(CancellationToken cancellationToken) =>
            Task.FromResult<FanRpmReadings?>(null);

        public Task<AdapterPowerState> ReadAdapterPowerStateAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(AdapterPowerState.FullPower280W);

        public Task SetPerformanceAsync(
            PerformancePreset value,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("SetPerformance");
            PerformanceSetCalls++;
            PerformanceWrites.Add(value);
            if (PerformanceFailure is not null)
            {
                throw PerformanceFailure;
            }

            if (ShouldConfirm(DeviceSubsystem.Performance))
            {
                State = State with { Performance = value };
            }

            AfterSetter?.Invoke(DeviceSubsystem.Performance);
            return Task.CompletedTask;
        }

        public Task SetPerformanceAsync(
            PerformancePreset value,
            CustomPerformanceSettings? customPerformance,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("SetPerformance");
            PerformanceSetCalls++;
            PerformanceWrites.Add(value);
            if (PerformanceFailure is not null)
            {
                throw PerformanceFailure;
            }

            if (ShouldConfirm(DeviceSubsystem.Performance))
            {
                State = State with
                {
                    Performance = value,
                    CustomPerformance = value == PerformancePreset.Custom
                        ? customPerformance
                        : null,
                };
            }

            AfterSetter?.Invoke(DeviceSubsystem.Performance);
            return Task.CompletedTask;
        }

        public async Task SetPerformanceAsync(
            PerformancePreset value,
            CustomPerformanceSettings? customPerformance,
            GpuClockOffsetSettings? gpuClockOffsets,
            CancellationToken cancellationToken)
        {
            await SetPerformanceAsync(
                value,
                customPerformance,
                gpuClockOffsets,
                retainGpuClockOffsetManagement: true,
                cancellationToken);
        }

        public async Task SetPerformanceAsync(
            PerformancePreset value,
            CustomPerformanceSettings? customPerformance,
            GpuClockOffsetSettings? gpuClockOffsets,
            bool retainGpuClockOffsetManagement,
            CancellationToken cancellationToken)
        {
            await SetPerformanceAsync(
                value,
                customPerformance,
                cancellationToken);
            if (gpuClockOffsets is not null)
            {
                await SetGpuClockOffsetsAsync(
                    gpuClockOffsets,
                    retainGpuClockOffsetManagement,
                    cancellationToken);
            }
        }

        public Task SetGpuClockOffsetsAsync(
            GpuClockOffsetSettings value,
            CancellationToken cancellationToken) => SetGpuClockOffsetsAsync(
                value,
                retainManagement: true,
                cancellationToken);

        public Task SetGpuClockOffsetsAsync(
            GpuClockOffsetSettings value,
            bool retainManagement,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("SetGpuClockOffsets");
            GpuClockSetCalls++;
            LastGpuClockRetainManagement = retainManagement;
            GpuClockOffsetManagementState = retainManagement
                ? GpuClockOffsetManagementState.Managed
                : GpuClockOffsetManagementState.ResetConfirmed;
            if (PerformanceFailure is not null)
            {
                throw PerformanceFailure;
            }

            if (ShouldConfirm(DeviceSubsystem.Performance)
                && State.GpuClockOffsets is { } capabilities)
            {
                State = State with
                {
                    CustomPerformance = State.CustomPerformance is { } custom
                        ? custom with { GpuClockOffsets = value }
                        : null,
                    GpuClockOffsets = retainManagement
                        ? capabilities with
                        {
                            Core = capabilities.Core with
                            {
                                CurrentMegahertz = value.CoreMegahertz,
                            },
                            Memory = capabilities.Memory with
                            {
                                CurrentMegahertz = value.MemoryMegahertz,
                            },
                        }
                        : null,
                };
            }

            AfterSetter?.Invoke(DeviceSubsystem.Performance);
            return Task.CompletedTask;
        }

        public Task RelinquishGpuClockOffsetManagementAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (State.GpuClockOffsets is not null)
            {
                Calls.Add("RelinquishGpuClockOffsets");
                GpuClockRelinquishCalls++;
                State = State with { GpuClockOffsets = null };
            }
            GpuClockOffsetManagementState = GpuClockOffsetManagementState is
                GpuClockOffsetManagementState.ResetConfirmed
                or GpuClockOffsetManagementState.RelinquishedDefaultConfirmed
                ? GpuClockOffsetManagementState.RelinquishedDefaultConfirmed
                : GpuClockOffsetManagementState.Relinquished;

            return Task.CompletedTask;
        }

        public Task<bool> InvalidateGpuClockOffsetConfirmationAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool requiresRevalidation = GpuClockOffsetManagementState is
                GpuClockOffsetManagementState.Managed
                or GpuClockOffsetManagementState.ResetConfirmed;
            if (GpuClockOffsetManagementState ==
                GpuClockOffsetManagementState.ResetConfirmed)
            {
                GpuClockOffsetManagementState =
                    GpuClockOffsetManagementState.Unknown;
            }
            if (GpuClockOffsetManagementState == GpuClockOffsetManagementState.Managed)
            {
                State = State with { GpuClockOffsets = null };
            }
            if (GpuClockOffsetManagementState ==
                GpuClockOffsetManagementState.RelinquishedDefaultConfirmed)
            {
                GpuClockOffsetManagementState =
                    GpuClockOffsetManagementState.Relinquished;
            }

            return Task.FromResult(requiresRevalidation);
        }

        public Task SetFanModeAsync(FanMode value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("SetFans");
            FanSetCalls++;
            if (FanFailure is not null)
            {
                throw FanFailure;
            }

            if (ShouldConfirm(DeviceSubsystem.Fans))
            {
                State = State with { Fans = value };
            }

            AfterSetter?.Invoke(DeviceSubsystem.Fans);
            return Task.CompletedTask;
        }

        public Task SetFanControlAsync(
            FanControlSettings value,
            CancellationToken cancellationToken)
        {
            FanControlWrites.Add(value.Mode);
            return SetFanModeAsync(value.Mode, cancellationToken);
        }

        public Task SetFanTargetsAsync(
            int fan1Rpm,
            int fan2Rpm,
            CancellationToken cancellationToken) =>
            Refuse("Manual fan targets are unavailable in this test device.");

        public Task SetChargeLimitAsync(
            BatteryPolicy value,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("SetChargeLimit");
            BatterySetterCalls.Add("SetChargeLimit");
            BatterySetCalls++;
            if (BatteryWriteFailure is not null)
            {
                throw BatteryWriteFailure;
            }

            if (ShouldConfirm(DeviceSubsystem.Battery))
            {
                State = State with
                {
                    Battery = new ConfirmedBatteryState(
                        protectionEnabled: true,
                        chargeLimitPercent: value.ChargeLimitPercent,
                        temporaryFullCharge: false),
                };
            }

            AfterSetter?.Invoke(DeviceSubsystem.Battery);
            return Task.CompletedTask;
        }

        public Task SetBatteryProtectionOffAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("SetProtectionOff");
            BatterySetterCalls.Add("SetProtectionOff");
            BatterySetCalls++;
            if (BatteryWriteFailure is not null)
            {
                throw BatteryWriteFailure;
            }

            if (ShouldConfirm(DeviceSubsystem.Battery))
            {
                State = State with { Battery = new ConfirmedBatteryState(false, null, false) };
            }

            AfterSetter?.Invoke(DeviceSubsystem.Battery);
            return Task.CompletedTask;
        }

        public Task SetTemporaryFullChargeAsync(
            bool enabled,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("SetTemporaryFullCharge");
            BatterySetterCalls.Add("SetTemporaryFullCharge");
            BatterySetCalls++;
            if (BatteryWriteFailure is not null)
            {
                throw BatteryWriteFailure;
            }

            if (ShouldConfirm(DeviceSubsystem.Battery) && State.Battery is { } battery)
            {
                State = State with
                {
                    Battery = new ConfirmedBatteryState(
                        battery.ProtectionEnabled,
                        battery.ChargeLimitPercent,
                        enabled),
                };
            }

            AfterSetter?.Invoke(DeviceSubsystem.Battery);
            return Task.CompletedTask;
        }

        public Task SetLightingAsync(
            LightingSettings value,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add("SetLighting");
            LightingSetCalls++;
            if (LightingFailure is not null)
            {
                throw LightingFailure;
            }

            if (ShouldConfirm(DeviceSubsystem.Lighting))
            {
                State = State with { Lighting = value };
            }

            AfterSetter?.Invoke(DeviceSubsystem.Lighting);
            return Task.CompletedTask;
        }

        public Task<byte> StepKeyboardBrightnessAsync(
            KeyboardBrightnessDirection direction,
            CancellationToken cancellationToken) =>
            Refuse<byte>("Keyboard brightness stepping is unavailable in this test device.");

        public Task ApplyOneShotLaptopControlAsync(
            ApplyOneShotLaptopControl value,
            CancellationToken cancellationToken) =>
            Refuse("One-shot laptop controls are unavailable in this test device.");

        public Task PulseReactiveKeyAsync(
            ReactiveKeyPulse pulse,
            CancellationToken cancellationToken) =>
            Refuse("Reactive lighting input is unavailable in this test device.");

        public Task PushAmbientLightingFrameAsync(
            AmbientLightingFrame frame,
            CancellationToken cancellationToken) =>
            Refuse("Ambient Awareness input is unavailable in this test device.");

        public Task SetPlaybackMuteIndicatorAsync(
            bool muted,
            CancellationToken cancellationToken) =>
            Refuse("Playback mute indication is unavailable in this test device.");

        private bool ShouldConfirm(DeviceSubsystem subsystem) =>
            AutoConfirmWrites && SuppressConfirmationFor != subsystem;

        private static Task Refuse(string message) =>
            Task.FromException(new CapabilityUnavailableException(message));

        private static Task<T> Refuse<T>(string message) =>
            Task.FromException<T>(new CapabilityUnavailableException(message));
    }
}
