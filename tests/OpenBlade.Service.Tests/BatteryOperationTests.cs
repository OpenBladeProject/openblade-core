using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class BatteryOperationTests
{
    [Fact]
    public async Task BatteryOperations_UseDistinctTypedSetters()
    {
        BatteryTestDevice device = new BatteryTestDevice();
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
        BatteryTestDevice device = new BatteryTestDevice();
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
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task TemporaryFullChargeBlockedByOtherLaptopSettings_ReportsPlainReasonWithoutChange(
        bool enabled,
        bool current)
    {
        string reason = ProductionBatteryPort.OtherPowerModeSettingsBlockMessage(0x08);
        BatteryTestDevice device = new BatteryTestDevice();
        device.State = device.State with
        {
            Battery = new ConfirmedBatteryState(true, 80, current),
        };
        device.Capabilities = device.Capabilities with
        {
            SupportsTemporaryFullCharge = true,
        };
        device.BatteryWriteFailure = new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            reason);
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome outcome = await coordinator.ApplyTemporaryFullChargeOutcomeAsync(
            enabled,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Failed, outcome.Result.Status);
        Assert.Contains(reason, outcome.Result.Message, StringComparison.Ordinal);
        Assert.Contains("local dimming", outcome.Result.Message, StringComparison.Ordinal);
        Assert.False(outcome.WriteAttempted);
        Assert.False(outcome.DeviceAccessFailed);
        Assert.Equal(current, device.State.Battery?.TemporaryFullCharge);
    }

    [Theory]
    [MemberData(nameof(InvalidProtectionDisableStates))]
    public async Task DisableProtection_RejectsInvalidConfirmedStateBeforeSetter(
        ConfirmedBatteryState? confirmed)
    {
        BatteryTestDevice device = new BatteryTestDevice();
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
    public async Task DisableProtection_DriftedLimitDoesNotRequireStoredLimitReset()
    {
        var device = new BatteryTestDevice();
        device.Capabilities = device.Capabilities with { SupportsBatteryProtectionOff = true };
        var coordinator = new ControlCoordinator(device);

        ControlResult result = await coordinator.ApplyBatteryProtectionAsync(
            enabled: false, BatteryPolicy.Create(75), CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, result.Status);
        Assert.Equal(new[] { "SetProtectionOff" }, device.BatterySetterCalls);
        Assert.Equal(new ConfirmedBatteryState(false, null, false), device.State.Battery);
    }

    [Fact]
    public async Task DisableProtection_WhenAlreadyOff_IsUnchangedWithoutSetter()
    {
        BatteryTestDevice device = new BatteryTestDevice();
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
        BatteryTestDevice device = new BatteryTestDevice();
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
        BatteryTestDevice device = new BatteryTestDevice();
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
        BatteryTestDevice device = new BatteryTestDevice();
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
        BatteryTestDevice device = new BatteryTestDevice();
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
        BatteryTestDevice device = new BatteryTestDevice();
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
        BatteryTestDevice device = new BatteryTestDevice();
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
        BatteryTestDevice device = new BatteryTestDevice();
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
        BatteryTestDevice device = new BatteryTestDevice();
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
        new ConfirmedBatteryState(true, 75, null),
        new ConfirmedBatteryState(true, 75, true),
    };
}
