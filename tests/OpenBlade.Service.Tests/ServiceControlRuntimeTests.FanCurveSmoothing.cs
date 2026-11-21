using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Fact]
    public async Task MaintainFanCurve_SingleSampleSpikeDoesNotWriteButSustainedHeatAndRawEmergencyDo()
    {
        var time = new ManualTimeProvider();
        var temperatures = new SettableFanTemperatureSource { Readings = new(65, 60) };
        TestContext context = CreateContext(
            providerAvailable: true,
            fanTemperatureSource: temperatures,
            timeProvider: time);
        context.Device.Capabilities = ManualFanCurveCapabilities();
        context.Device.FanTargetsAvailable = true;
        await context.Runtime.ObserveAsync(CancellationToken.None);
        // 2000 RPM up to 75 C, 4000 RPM from 80 C.
        FanCurvePoint[] stepped = [new(40, 2000), new(75, 2000), new(80, 4000)];
        var settings = new FanControlSettings(
            FanMode.ManualCurve,
            3700,
            new ManualFanCurve(stepped, stepped, useHigherTemperatureForBoth: false));
        ControlOperationResult applied = await context.Runtime.SetFanControlOverrideAsync(
            new ManualOverride<FanMode>(FanMode.ManualCurve, OverrideLifetime.UntilPowerSourceChange),
            settings,
            CancellationToken.None);
        Assert.Equal(ControlOperationStatus.Applied, applied.Status);
        time.Advance(TimeSpan.FromSeconds(1));
        await context.Runtime.MaintainFanCurveAsync(CancellationToken.None);
        int established = context.Device.FanTargets.Count;

        // One 90 C package sample moves the 5 s filter to about 69 C: still on the 2000 RPM step.
        temperatures.Readings = new(90, 60);
        time.Advance(TimeSpan.FromSeconds(1));
        await context.Runtime.MaintainFanCurveAsync(CancellationToken.None);
        temperatures.Readings = new(65, 60);
        time.Advance(TimeSpan.FromSeconds(1));
        await context.Runtime.MaintainFanCurveAsync(CancellationToken.None);
        Assert.Equal(established, context.Device.FanTargets.Count);

        temperatures.Readings = new(85, 60);
        for (int second = 0; second < 15; second++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            await context.Runtime.MaintainFanCurveAsync(CancellationToken.None);
        }
        Assert.True(context.Device.FanTargets.Count > established);
        Assert.Equal(4000, context.Device.FanTargets[^1].Fan1Rpm);
        Assert.Equal(2000, context.Device.FanTargets[^1].Fan2Rpm);

        // Emergency limits use the raw reading, so one sample forces maximum speed at once.
        int beforeEmergency = context.Device.FanTargets.Count;
        temperatures.Readings = new(85, 87);
        time.Advance(TimeSpan.FromSeconds(1));
        await context.Runtime.MaintainFanCurveAsync(CancellationToken.None);
        Assert.Equal(beforeEmergency + 1, context.Device.FanTargets.Count);
        Assert.True(context.Device.FanTargets[^1].Fan2Rpm > 4000);
        Assert.Equal(
            "Manual fan curve is using maximum speed for thermal safety.",
            context.Runtime.GetStatus().Message);
    }

    private sealed class SettableFanTemperatureSource : IFanTemperatureSource
    {
        public FanTemperatureReadings Readings { get; set; }

        public ValueTask<FanTemperatureReadings> ReadAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(Readings);
    }
}
