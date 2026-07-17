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
        var coordinator = new ControlCoordinator(device, new FakeHardwareAccessGuard([]));

        ControlResult result = await ApplyScopedAsync(coordinator, subsystem);

        Assert.Equal(ControlResultStatus.Applied, result.Status);
        Assert.Equal(["Capabilities", "State", setterCall, "State"], device.Calls);
        Assert.Equal(1, device.TotalSetterCalls);
    }

    [Theory]
    [InlineData(DeviceSubsystem.Performance)]
    [InlineData(DeviceSubsystem.Fans)]
    [InlineData(DeviceSubsystem.Battery)]
    [InlineData(DeviceSubsystem.Lighting)]
    public async Task ScopedApply_WhenAlreadyMatching_PerformsNoSetterOrSecondRead(
        DeviceSubsystem subsystem)
    {
        RecordingBladeDevice device = CreateMatchingDevice();
        var coordinator = new ControlCoordinator(device, new FakeHardwareAccessGuard([]));

        ControlResult result = await ApplyScopedAsync(coordinator, subsystem);

        Assert.Equal(ControlResultStatus.Unchanged, result.Status);
        Assert.Equal(["Capabilities", "State"], device.Calls);
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
        var coordinator = new ControlCoordinator(device, new FakeHardwareAccessGuard([]));

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

        ScopedApplyOutcome outcome = await coordinator.ApplyPerformanceOutcomeAsync(
            PerformancePreset.Performance,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Blocked, outcome.Result.Status);
        Assert.False(outcome.WriteAttempted);
        Assert.Equal(["Capabilities", "State"], device.Calls);
        Assert.Equal(0, device.PerformanceSetCalls);
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
    public async Task ScopedKnownNotApplied_IsRejectedWithoutAttemptedOrIndeterminateState()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.PerformanceFailure = new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            "firmware rejected setter");
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator.ApplyPerformanceOutcomeAsync(
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
    public async Task ScopedApply_InvalidLighting_IsBlockedBeforeDeviceAccess()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        var coordinator = new ControlCoordinator(device, new FakeHardwareAccessGuard([]));

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
        var coordinator = new ControlCoordinator(device, new FakeHardwareAccessGuard([]));

        ScopedApplyOutcome outcome = await coordinator.ApplyPerformanceOutcomeAsync(
            PerformancePreset.Balanced,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Failed, outcome.Result.Status);
        Assert.Contains("indeterminate", outcome.Result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(device.State, outcome.Readback);
        Assert.False(outcome.ReadbackMatched);
        Assert.Equal(["Capabilities", "State", "SetPerformance", "State"], device.Calls);
        Assert.Equal(1, device.PerformanceSetCalls);
    }

    [Fact]
    public async Task ScopedApply_PostWriteReadbackFailureFailsWithoutRetry()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.ReadStateFailureOnCall = 2;
        var coordinator = new ControlCoordinator(device, new FakeHardwareAccessGuard([]));

        ControlResult result = await coordinator.ApplyPerformanceAsync(
            PerformancePreset.Balanced,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Failed, result.Status);
        Assert.Contains("indeterminate", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["Capabilities", "State", "SetPerformance", "State"], device.Calls);
        Assert.Equal(1, device.PerformanceSetCalls);
    }

    [Fact]
    public async Task ScopedApply_ControllerWarningIsAdvisory()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        var coordinator = new ControlCoordinator(
            device,
            new FakeHardwareAccessGuard(["RazerAppEngine"], "enumeration warning"));

        ControlResult result = await coordinator.ApplyPerformanceAsync(
            PerformancePreset.Balanced,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, result.Status);
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
        var coordinator = new ControlCoordinator(device, new FakeHardwareAccessGuard([]));

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
        var coordinator = new ControlCoordinator(device, new FakeHardwareAccessGuard([]));

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
        var coordinator = new ControlCoordinator(device, new FakeHardwareAccessGuard([]));
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
        var coordinator = new ControlCoordinator(device, new FakeHardwareAccessGuard([]));

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
        var coordinator = new ControlCoordinator(device, new FakeHardwareAccessGuard([]));

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
    public async Task ApplyDesiredState_InitiallyMatchingSubsystemMustMatchFinalAggregateReadback()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.State = device.State with { Fans = FanMode.Maximum };
        device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Performance)
            {
                device.State = device.State with { Fans = FanMode.Automatic };
            }
        };
        var coordinator = new ControlCoordinator(device, new FakeHardwareAccessGuard([]));

        SubsystemApplyResults results = await coordinator.ApplyDesiredStateAsync(
            CreateDesiredState(),
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, results.Performance.Status);
        Assert.Equal(ControlResultStatus.Failed, results.Fans.Status);
        Assert.Contains("final", results.Fans.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, device.FanSetCalls);
        Assert.Equal(FanMode.Automatic, device.State.Fans);
        Assert.Equal(2, device.StateReadCalls);
    }

    [Fact]
    public async Task ApplyDesiredState_TerminalSetterFailureStopsLaterWritesButStillReadsBackOnce()
    {
        RecordingBladeDevice device = CreateMismatchedDevice();
        device.PerformanceFailure = new IOException("device disconnected");
        var coordinator = new ControlCoordinator(device, new FakeHardwareAccessGuard([]));

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
            DeviceSubsystem.Battery => coordinator.ApplyBatteryAsync(
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

    private sealed class FakeHardwareAccessGuard(
        IReadOnlyList<string> conflicts,
        string? failure = null) : IHardwareAccessGuard
    {
        public HardwareAccessDecision Check() => new(conflicts, failure);
    }

    private sealed class RecordingBladeDevice : IBladeDevice
    {
        public List<string> Calls { get; } = [];

        public List<string> BatterySetterCalls { get; } = [];

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

        public Exception? PerformanceFailure { get; set; }

        public Exception? FanFailure { get; set; }

        public Exception? BatteryWriteFailure { get; set; }

        public Action<int>? AfterReadState { get; set; }

        public Action<DeviceSubsystem>? AfterSetter { get; set; }

        public int PerformanceSetCalls { get; private set; }

        public int FanSetCalls { get; private set; }

        public int BatterySetCalls { get; private set; }

        public int LightingSetCalls { get; private set; }

        public int StateReadCalls { get; private set; }

        public int TotalSetterCalls => PerformanceSetCalls
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
            if (ReadStateFailureOnCall == StateReadCalls)
            {
                throw new IOException("readback failed");
            }

            AfterReadState?.Invoke(StateReadCalls);
            return Task.FromResult(State);
        }

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
            if (ShouldConfirm(DeviceSubsystem.Lighting))
            {
                State = State with { Lighting = value };
            }

            AfterSetter?.Invoke(DeviceSubsystem.Lighting);
            return Task.CompletedTask;
        }

        private bool ShouldConfirm(DeviceSubsystem subsystem) =>
            AutoConfirmWrites && SuppressConfirmationFor != subsystem;
    }
}
