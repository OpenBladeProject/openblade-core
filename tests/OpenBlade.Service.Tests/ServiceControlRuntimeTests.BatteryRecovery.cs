using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Fact]
    public async Task ResumeExperimentalWrites_PolicyOnlyReadbackRecoversWithoutOverrideOrSetterReplay()
    {
        TestContext context = CreateWritableContext();
        context.Provider.AvailableWhenExperimentalAllowed = true;
        context.Device.Capabilities = context.Device.Capabilities with { SupportsTemporaryFullCharge = false };
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Battery = new ConfirmedBatteryState(true, 80, null),
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.ChargeLimitSetterBoundary = () =>
        {
            context.Device.ConfirmedState = context.Device.ConfirmedState with
            {
                Battery = new ConfirmedBatteryState(true, 75, null),
            };
            context.Provider.IsExperimentalWritesPaused = true;
            throw new BladeWriteException(WriteSubmissionState.Indeterminate, "Lost limiter ACK.");
        };

        await context.Runtime.SetBatteryPolicyAsync(BatteryPolicy.Create(75), CancellationToken.None);
        Assert.True(context.Provider.IsExperimentalWritesPaused);
        int setters = context.Device.TotalSetterCalls;
        Assert.Equal(1, context.Device.BatterySetCalls);
        ControlOperationResult resumed = await context.Runtime.ResumeExperimentalWritesAsync(
            static () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Applied, resumed.Status);
        Assert.False(context.Provider.IsExperimentalWritesPaused);
        Assert.Equal(setters, context.Device.TotalSetterCalls);
        Assert.Null(context.Runtime.GetStatus().ConfirmedState!.Battery!.TemporaryFullCharge);
        Assert.Equal(75, context.Runtime.GetStatus().ConfirmedState!.Battery!.ChargeLimitPercent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullTransition_BatteryConfirmationTracksOverrideCleanup(bool cancelOverride)
    {
        TestContext context = CreateWritableContext();
        context.Device.Capabilities = context.Device.Capabilities with { SupportsTemporaryFullCharge = cancelOverride };
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Battery = new ConfirmedBatteryState(true, 80, cancelOverride ? true : null),
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        DesiredDeviceState desired = context.Runtime.GetStatus().DesiredState with
        {
            Battery = new DesiredBatteryState(BatteryPolicy.Create(75), true, false),
        };
        context.Device.ChargeLimitSetterBoundary = () =>
            throw new BladeWriteException(WriteSubmissionState.Indeterminate, "Lost limiter ACK after possible cleanup.");
        var coordinator = new ControlCoordinator(context.Device);
        FullTransitionApplyOutcome outcome = await coordinator.ApplyDesiredStateOutcomeAsync(
            desired, CancellationToken.None,
            automaticWriteExclusions: new HashSet<DeviceSubsystem>
            {
                DeviceSubsystem.Performance, DeviceSubsystem.Fans, DeviceSubsystem.Lighting,
            });
        Assert.Contains(DeviceSubsystem.Battery, outcome.ConfirmationUnavailable!);
        Assert.Equal(cancelOverride ? BatteryConfirmationScope.All : BatteryConfirmationScope.PolicyOnly,
            outcome.BatteryConfirmation);
        Assert.Equal(cancelOverride ? new[] { "SetTemporaryFullCharge", "SetChargeLimit" } : new[] { "SetChargeLimit" },
            context.Device.BatterySetterCalls);
    }

    [Fact]
    public async Task ResumeExperimentalWrites_ActualOverrideStillRequiresIndependentFlag()
    {
        TestContext context = CreateWritableContext();
        context.Provider.AvailableWhenExperimentalAllowed = true;
        context.Device.Capabilities = context.Device.Capabilities with { SupportsTemporaryFullCharge = true };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        context.Device.TemporaryFullChargeFailure =
            new BladeWriteException(WriteSubmissionState.Indeterminate, "Lost override ACK.");
        await context.Runtime.SetFullChargeOverrideAsync(true, CancellationToken.None);
        context.Provider.IsExperimentalWritesPaused = true;
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Battery = new ConfirmedBatteryState(true, 80, null),
        };
        int setters = context.Device.TotalSetterCalls;

        ControlOperationResult unknown = await context.Runtime.ResumeExperimentalWritesAsync(
            static () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Blocked, unknown.Status);
        Assert.True(context.Provider.IsExperimentalWritesPaused);
        Assert.Equal(setters, context.Device.TotalSetterCalls);

        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Battery = new ConfirmedBatteryState(true, 80, true),
        };
        ControlOperationResult confirmed = await context.Runtime.ResumeExperimentalWritesAsync(
            static () => true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Applied, confirmed.Status);
        Assert.False(context.Provider.IsExperimentalWritesPaused);
        Assert.Equal(setters, context.Device.TotalSetterCalls);
    }
}
