using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Theory]
    [InlineData(PowerSource.UsbC, PowerSource.Battery)]
    [InlineData(PowerSource.PluggedIn, PowerSource.UsbC)]
    public async Task AutomaticFanStopLowerPowerTransitionKeepsWaitingIntent(PowerSource before, PowerSource after)
    {
        var (context, owner, clock, _) = await CreateAutomaticFanStopContextAsync();
        if (before != PowerSource.UsbC)
        {
            context.Telemetry.Current = context.Telemetry.Current with { PowerSource = before };
            await context.Runtime.HandlePowerSourceChangedAsync(default);
        }
        await SaveAutomaticFanStopSettingsAsync(context, AutomaticCustomSettings());
        await SampleAutomaticFanStopAsync(context, clock, 1);
        context.Telemetry.Current = context.Telemetry.Current with { PowerSource = after };

        await context.Runtime.HandlePowerSourceChangedAsync(default);

        Assert.Equal(FanStopAutomationState.Waiting, context.Runtime.GetStatus().FanStopStatus.AutomationState);
        Assert.Equal(FanStopAutomationPauseReason.None, context.Runtime.GetStatus().FanStopStatus.AutomationPauseReason);
        Assert.Equal(0, owner.StartCalls);
    }

    [Fact]
    public async Task AutomaticFanStopPowerIncreaseDiscoveredByBatteryRequestAlsoPauses()
    {
        var (context, owner, _, _) = await CreateAutomaticFanStopContextAsync(AutomaticCustomSettings());
        context.Telemetry.Current = context.Telemetry.Current with { PowerSource = PowerSource.PluggedIn };

        await context.Runtime.SetBatteryProtectionAsync(true, default);

        Assert.Equal(FanStopAutomationState.Paused, context.Runtime.GetStatus().FanStopStatus.AutomationState);
        Assert.Equal(FanStopAutomationPauseReason.PowerChanged, context.Runtime.GetStatus().FanStopStatus.AutomationPauseReason);
        Assert.Equal(0, owner.StartCalls);
    }

    [Fact]
    public async Task AutomaticFanStopGuardianNotificationDoesNotReadSensorsOrStartStop()
    {
        var (context, owner, clock, temperatures) = await CreateAutomaticFanStopContextAsync(AutomaticCustomSettings());
        await SampleAutomaticFanStopAsync(context, clock, 3);
        Assert.Equal(1, owner.StartCalls);
        int reads = temperatures.ReadCalls;
        owner.Complete(FanStopEndReason.ThermalThreshold);

        owner.Notify();

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (context.Runtime.GetStatus().FanStopStatus.State != FanStopState.Automatic)
        {
            await Task.Delay(10, deadline.Token);
        }
        Assert.Equal(reads, temperatures.ReadCalls);
        Assert.Equal(1, owner.StartCalls);
        await SampleAutomaticFanStopAsync(context, clock, 3);
        Assert.Equal(2, owner.StartCalls);
    }
}
