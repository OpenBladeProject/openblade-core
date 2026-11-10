using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Fact]
    public async Task RustBatteryParity_ExplicitIntentAndLostAcknowledgementKeepIntentSeparateUntilObservation()
    {
        TestContext context = CreateWritableContext();
        context.Device.Capabilities = context.Device.Capabilities with
        {
            ChargeLimits = new HashSet<int> { 50, 55, 60, 65, 70, 75, 80 },
            SupportsBatteryProtectionOff = true,
            SupportsTemporaryFullCharge = true,
        };
        context.Device.ConfirmedState = context.Device.ConfirmedState with
        {
            Battery = new ConfirmedBatteryState(true, 80, false),
        };
        await context.Runtime.ObserveAsync(CancellationToken.None);
        BatteryConfiguration persistedBefore = await context.Store.LoadAsync(CancellationToken.None);
        Assert.Equal(BatteryPolicy.Create(80), persistedBefore.Profiles.BatteryPolicy);

        ControlOperationResult set75 = await context.Runtime.SetBatteryPolicyAsync(
            BatteryPolicy.Create(75), CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Applied, set75.Status);
        Assert.Equal(["SetChargeLimit"], context.Device.BatterySetterCalls);
        Assert.Equal(75, context.Runtime.GetStatus().ConfirmedState!.Battery!.ChargeLimitPercent);
        Assert.Equal(75, context.Runtime.GetStatus().DesiredState.Battery.ConfiguredPolicy.ChargeLimitPercent);
        Assert.Equal(BatteryPolicy.Create(75), context.Runtime.GetStatus().BatteryPolicyOverride);
        Assert.Equal(BatteryPolicy.Create(80),
            (await context.Store.LoadAsync(CancellationToken.None)).Profiles.BatteryPolicy);

        ControlOperationResult unchanged75 = await context.Runtime.SetBatteryPolicyAsync(
            BatteryPolicy.Create(75), CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Applied, unchanged75.Status);
        Assert.Equal(["SetChargeLimit"], context.Device.BatterySetterCalls);

        ControlOperationResult unsupported76 = await context.Runtime.SetBatteryPolicyAsync(
            BatteryPolicy.Create(76), CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Blocked, unsupported76.Status);
        Assert.Equal(["SetChargeLimit"], context.Device.BatterySetterCalls);

        ControlOperationResult protectionOff = await context.Runtime.SetBatteryProtectionAsync(
            enabled: false, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Applied, protectionOff.Status);
        Assert.Equal(["SetChargeLimit", "SetProtectionOff"], context.Device.BatterySetterCalls);
        Assert.False(context.Runtime.GetStatus().DesiredState.Battery.ProtectionEnabled);
        Assert.False(context.Runtime.GetStatus().ConfirmedState!.Battery!.ProtectionEnabled);

        ControlOperationResult limitWhileOff = await context.Runtime.SetBatteryPolicyAsync(
            BatteryPolicy.Create(70), CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Blocked, limitWhileOff.Status);
        Assert.Equal(["SetChargeLimit", "SetProtectionOff"], context.Device.BatterySetterCalls);

        ControlOperationResult protectionOn = await context.Runtime.SetBatteryProtectionAsync(
            enabled: true, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Applied, protectionOn.Status);
        Assert.Equal(["SetChargeLimit", "SetProtectionOff", "SetChargeLimit"],
            context.Device.BatterySetterCalls);
        Assert.True(context.Runtime.GetStatus().DesiredState.Battery.ProtectionEnabled);
        Assert.Equal(75, context.Runtime.GetStatus().ConfirmedState!.Battery!.ChargeLimitPercent);
        Assert.Null(context.Runtime.GetStatus().BatteryProtectionOverride);

        context.Device.ChargeLimitSetterBoundary = () =>
        {
            context.Device.ConfirmedState = context.Device.ConfirmedState with
            {
                Battery = new ConfirmedBatteryState(true, 70, false),
            };
            throw new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "Lost limiter acknowledgement after the fake applied the request.");
        };
        ControlOperationResult uncertain70 = await context.Runtime.SetBatteryPolicyAsync(
            BatteryPolicy.Create(70), CancellationToken.None);

        Assert.Equal(ControlOperationStatus.Failed, uncertain70.Status);
        Assert.Equal(["SetChargeLimit", "SetProtectionOff", "SetChargeLimit", "SetChargeLimit"],
            context.Device.BatterySetterCalls);
        ServiceStatusSnapshot uncertain = context.Runtime.GetStatus();
        Assert.Equal([DeviceSubsystem.Battery], uncertain.UnconfirmedWrites);
        Assert.Equal(75, uncertain.ConfirmedState!.Battery!.ChargeLimitPercent);
        Assert.Equal(75, uncertain.DesiredState.Battery.ConfiguredPolicy.ChargeLimitPercent);
        Assert.Equal(BatteryPolicy.Create(75), uncertain.BatteryPolicyOverride);
        Assert.Equal(BatteryPolicy.Create(80),
            (await context.Store.LoadAsync(CancellationToken.None)).Profiles.BatteryPolicy);

        ControlOperationResult blockedWhilePending = await context.Runtime.SetBatteryPolicyAsync(
            BatteryPolicy.Create(65), CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Blocked, blockedWhilePending.Status);
        Assert.Equal(["SetChargeLimit", "SetProtectionOff", "SetChargeLimit", "SetChargeLimit"],
            context.Device.BatterySetterCalls);

        context.Device.ChargeLimitSetterBoundary = null;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        ServiceStatusSnapshot reconciled = context.Runtime.GetStatus();
        Assert.Empty(reconciled.UnconfirmedWrites);
        Assert.Empty(reconciled.ExternalChanges);
        Assert.Equal(70, reconciled.ConfirmedState!.Battery!.ChargeLimitPercent);
        Assert.Equal(70, reconciled.DesiredState.Battery.ConfiguredPolicy.ChargeLimitPercent);
        Assert.Equal(BatteryPolicy.Create(70), reconciled.BatteryPolicyOverride);
        Assert.Equal(["SetChargeLimit", "SetProtectionOff", "SetChargeLimit", "SetChargeLimit"],
            context.Device.BatterySetterCalls);

        ControlOperationResult temporaryProtectionOff = await context.Runtime.SetBatteryProtectionAsync(
            enabled: false, CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Applied, temporaryProtectionOff.Status);
        Assert.False(context.Runtime.GetStatus().BatteryProtectionOverride);

        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        await context.Runtime.HandlePowerSourceChangedAsync(CancellationToken.None);
        ServiceStatusSnapshot onBattery = context.Runtime.GetStatus();
        Assert.Equal(PowerSource.Battery, onBattery.ActivePowerSource);
        Assert.Null(onBattery.BatteryProtectionOverride);
        Assert.Equal(BatteryPolicy.Create(70), onBattery.BatteryPolicyOverride);
        Assert.True(onBattery.DesiredState.Battery.ProtectionEnabled);
        Assert.Equal(70, onBattery.DesiredState.Battery.ConfiguredPolicy.ChargeLimitPercent);
        Assert.Equal(70, onBattery.ConfirmedState!.Battery!.ChargeLimitPercent);
        Assert.Equal(["SetChargeLimit", "SetProtectionOff", "SetChargeLimit", "SetChargeLimit", "SetProtectionOff", "SetChargeLimit"],
            context.Device.BatterySetterCalls);
    }
}
