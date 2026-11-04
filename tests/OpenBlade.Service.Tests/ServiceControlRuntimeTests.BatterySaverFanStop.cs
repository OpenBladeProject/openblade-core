using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class ServiceControlRuntimeTests
{
    [Fact]
    public async Task BatterySaverFanStop_PreferenceOutsideSaverPreservesEveryOtherProfileField()
    {
        TestContext context = CreateWritableContext();
        using ServiceControlRuntime runtime = context.Runtime;
        context.Device.Capabilities = context.Device.Capabilities with { SupportsBatterySaverFanStop = true };
        await runtime.ObserveAsync(default);
        ProfileSet before = runtime.GetStatus().Profiles;
        int setters = context.Device.TotalSetterCalls;

        ControlOperationResult result = await runtime.SetBatterySaverFanStopAsync(true, default);

        Assert.Equal(ControlOperationStatus.Stored, result.Status);
        Assert.Equal(before with { Battery = before.Battery with { StopFansInBatterySaver = true } },
            runtime.GetStatus().Profiles);
        Assert.Equal(setters, context.Device.TotalSetterCalls);
        Assert.True(runtime.GetStatus().BatterySaverFanStopStatus!.Requested);
        Assert.False(runtime.GetStatus().BatterySaverFanStopStatus!.Applied);
        Assert.False(context.Provider.BatterySaverFanStopReadInterest);
        Assert.True((await context.Store.LoadAsync(default)).Profiles.Battery.StopFansInBatterySaver);
    }

    [Fact]
    public async Task BatterySaverFanStop_UnsupportedEnableRefusesButDisableCanClearSavedIntent()
    {
        TestContext context = CreateWritableContext();
        using ServiceControlRuntime runtime = context.Runtime;
        await runtime.ObserveAsync(default);
        Assert.Equal(ControlOperationStatus.Blocked,
            (await runtime.SetBatterySaverFanStopAsync(true, default)).Status);
        Assert.False(runtime.GetStatus().Profiles.Battery.StopFansInBatterySaver);
        Assert.Equal(ControlOperationStatus.Stored,
            (await runtime.SetBatterySaverFanStopAsync(false, default)).Status);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(56, true)]
    [InlineData(45, false)]
    public async Task BatterySaverFanStop_UnsafeInitialSampleStoresWithoutFanWrite(int? cpu, bool inactiveGpu)
    {
        var temperatures = new BatterySaverTestTemperatureSource(new(cpu, null, inactiveGpu));
        TestContext context = await CreateBatterySaverFanStopContextAsync(temperatures);
        using ServiceControlRuntime runtime = context.Runtime;
        int writes = context.Device.FanSetCalls;

        ControlOperationResult result = await runtime.SetBatterySaverFanStopAsync(true, default);

        Assert.Equal(ControlOperationStatus.Stored, result.Status);
        Assert.Equal(writes, context.Device.FanSetCalls);
        Assert.True(runtime.GetStatus().BatterySaverFanStopStatus!.ThermallySuspended);
        Assert.False(runtime.GetStatus().BatterySaverFanStopStatus!.Applied);
    }

    [Fact]
    public async Task BatterySaverFanStop_OwnedWarmReleaseLatchesAndExplicitCycleReenables()
    {
        var temperatures = new BatterySaverTestTemperatureSource(new(45, null, true));
        TestContext context = await CreateBatterySaverFanStopContextAsync(temperatures);
        using ServiceControlRuntime runtime = context.Runtime;
        ProfileSet normal = runtime.GetStatus().Profiles;
        ControlOperationResult enabled = await runtime.SetBatterySaverFanStopAsync(true, default);
        Assert.True(enabled.Status == ControlOperationStatus.Applied, enabled.Message);
        Assert.Equal(FanMode.Stop, context.Device.ConfirmedState.Fans);
        Assert.True(runtime.GetStatus().BatterySaverFanStopStatus!.Applied);

        temperatures.Readings = new(56, null, true);
        await runtime.MaintainFanCurveAsync(default);
        Assert.Equal(FanMode.Automatic, context.Device.ConfirmedState.Fans);
        Assert.True(runtime.GetStatus().BatterySaverFanStopStatus!.ThermallySuspended);
        Assert.False(runtime.GetStatus().BatterySaverFanStopStatus!.Applied);
        int writes = context.Device.FanSetCalls;
        temperatures.Readings = new(45, null, true);
        await runtime.MaintainFanCurveAsync(default);
        Assert.Equal(writes, context.Device.FanSetCalls);
        Assert.Equal(normal.Battery.FanControl, runtime.GetStatus().Profiles.Battery.FanControl);

        await runtime.SetBatterySaverFanStopAsync(false, default);
        await runtime.SetBatterySaverFanStopAsync(true, default);
        Assert.Equal(FanMode.Stop, context.Device.ConfirmedState.Fans);
        Assert.False(runtime.GetStatus().BatterySaverFanStopStatus!.ThermallySuspended);
    }

    [Fact]
    public async Task BatterySaverFanStop_MatchingObservationNeverOwnsButExplicitVerifiedWriteDoes()
    {
        var temperatures = new BatterySaverTestTemperatureSource(new(45, null, true));
        TestContext context = await CreateBatterySaverFanStopContextAsync(temperatures);
        using ServiceControlRuntime runtime = context.Runtime;
        context.Device.ConfirmedState = context.Device.ConfirmedState with { Fans = FanMode.Stop };
        await runtime.ObserveAsync(default);
        int writes = context.Device.FanSetCalls;
        Assert.False(runtime.GetStatus().BatterySaverFanStopStatus!.Applied);

        await runtime.SetBatterySaverFanStopAsync(true, default);

        Assert.Equal(writes + 1, context.Device.FanSetCalls);
        Assert.True(runtime.GetStatus().BatterySaverFanStopStatus!.Applied);

        await runtime.SetBatterySaverFanStopAsync(false, default);
        Assert.Equal(FanMode.Automatic, context.Device.ConfirmedState.Fans);
        Assert.False(runtime.GetStatus().BatterySaverFanStopStatus!.Applied);
    }

    [Fact]
    public async Task BatterySaverFanStop_ExplicitOffRestoresUnownedConfirmedStop()
    {
        var temperatures = new BatterySaverTestTemperatureSource(new(70, null, false));
        TestContext context = await CreateBatterySaverFanStopContextAsync(temperatures);
        using ServiceControlRuntime runtime = context.Runtime;
        context.Device.ConfirmedState = context.Device.ConfirmedState with { Fans = FanMode.Stop };
        await runtime.ObserveAsync(default);
        Assert.False(runtime.GetStatus().BatterySaverFanStopStatus!.Applied);

        ControlOperationResult disabled = await runtime.SetBatterySaverFanStopAsync(false, default);

        Assert.True(disabled.Status == ControlOperationStatus.Applied, disabled.Message);
        Assert.Equal(FanMode.Automatic, context.Device.ConfirmedState.Fans);
        Assert.False(runtime.GetStatus().BatterySaverFanStopStatus!.Applied);
        Assert.False(runtime.GetStatus().Profiles.Battery.StopFansInBatterySaver);
    }

    [Fact]
    public async Task BatterySaverFanStop_DisableRestoresCurrentNormalOverrideAndPreservesSavedFans()
    {
        var temperatures = new BatterySaverTestTemperatureSource(new(45, null, true));
        TestContext context = await CreateBatterySaverFanStopContextAsync(temperatures);
        using ServiceControlRuntime runtime = context.Runtime;
        PowerProfile saved = runtime.GetStatus().Profiles.Battery;
        await runtime.SetBatterySaverFanStopAsync(true, default);
        var normal = new FanControlSettings(FanMode.Fixed, 4100, ManualFanCurve.Default);

        await runtime.SetFanControlOverrideAsync(new(FanMode.Fixed, OverrideLifetime.Persistent), normal, default);
        Assert.Equal(FanMode.Stop, context.Device.ConfirmedState.Fans);
        Assert.True(runtime.GetStatus().BatterySaverFanStopStatus!.Applied);
        Assert.Equal(normal, runtime.GetStatus().BatterySaverFanStopStatus!.NormalFanControl);
        await runtime.SetBatterySaverFanStopAsync(false, default);

        Assert.Equal(normal, context.Device.FanControlWrites[^1]);
        Assert.Equal(saved.FanControl, runtime.GetStatus().Profiles.Battery.FanControl);
        Assert.Equal(saved.Fans, runtime.GetStatus().Profiles.Battery.Fans);
        Assert.False(runtime.GetStatus().BatterySaverFanStopStatus!.Applied);
    }

    [Fact]
    public async Task BatterySaverFanStop_EffectivePerformanceScopeLeavesAndReturnsWithoutChangingPreference()
    {
        var temperatures = new BatterySaverTestTemperatureSource(new(45, null, true));
        TestContext context = await CreateBatterySaverFanStopContextAsync(temperatures);
        using ServiceControlRuntime runtime = context.Runtime;
        await runtime.SetBatterySaverFanStopAsync(true, default);

        await runtime.SetPerformanceOverrideAsync(new(PerformancePreset.BatteryBalanced, OverrideLifetime.Persistent), default);
        await runtime.MaintainFanCurveAsync(default);
        Assert.NotEqual(FanMode.Stop, context.Device.ConfirmedState.Fans);
        Assert.True(runtime.GetStatus().Profiles.Battery.StopFansInBatterySaver);

        await runtime.SetPerformanceOverrideAsync(null, default);
        await runtime.MaintainFanCurveAsync(default);
        Assert.Equal(FanMode.Stop, context.Device.ConfirmedState.Fans);
        Assert.True(runtime.GetStatus().BatterySaverFanStopStatus!.Applied);
    }

    [Fact]
    public async Task BatterySaverFanStop_NativeAdmissionExpiresAndClosesAtLifecycleBoundary()
    {
        var clock = new ManualTimeProvider();
        var temperatures = new BatterySaverTestTemperatureSource(new(45, null, true));
        TestContext context = await CreateBatterySaverFanStopContextAsync(temperatures, clock);
        using ServiceControlRuntime runtime = context.Runtime;
        await runtime.SetBatterySaverFanStopAsync(true, default);
        Func<bool> admission = Assert.IsType<Func<bool>>(context.Provider.BatterySaverFanStopWriteAdmission);
        Assert.True(admission());

        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(admission());
        await runtime.MaintainFanCurveAsync(default);
        Assert.True(admission());
        context.Telemetry.Current = CreateTelemetry(PowerSource.PluggedIn);
        Assert.Equal(PowerSource.Battery, runtime.GetStatus().ActivePowerSource);
        Assert.False(admission());
        context.Telemetry.Current = CreateTelemetry(PowerSource.Battery);
        Assert.True(admission());
        context.LowPowerGate.Enter();
        Assert.False(admission());
    }

    [Fact]
    public async Task BatterySaverFanStop_ConfirmedLowPowerHandsBackAutomaticWithoutNormalRestore()
    {
        var temperatures = new BatterySaverTestTemperatureSource(new(45, null, true));
        TestContext context = await CreateBatterySaverFanStopContextAsync(temperatures);
        using ServiceControlRuntime runtime = context.Runtime;
        await runtime.SetBatterySaverFanStopAsync(true, default);
        ProfileSet saved = runtime.GetStatus().Profiles;
        context.Device.FanWrites.Clear();
        temperatures.Readings = new(70, null, false);
        context.LowPowerGate.Enter();

        await runtime.PrepareForLowPowerAsync(context.LowPowerGate.Generation, static () => true, default);
        await runtime.MaintainFanCurveAsync(default);

        Assert.Equal([FanMode.Automatic], context.Device.FanWrites);
        Assert.Equal(FanMode.Automatic, context.Device.ConfirmedState.Fans);
        Assert.Equal(saved, runtime.GetStatus().Profiles);
        Assert.False(context.Provider.BatterySaverFanStopWriteAdmission!());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BatterySaverFanStop_LowPowerRefusesControllerOrStaleBoundary(bool competingController)
    {
        var temperatures = new BatterySaverTestTemperatureSource(new(45, null, true));
        TestContext context = await CreateBatterySaverFanStopContextAsync(temperatures);
        using ServiceControlRuntime runtime = context.Runtime;
        await runtime.SetBatterySaverFanStopAsync(true, default);
        context.Device.FanWrites.Clear();
        context.LowPowerGate.Enter();
        if (competingController)
        {
            context.Monitor.Current = new(["RazerAppEngine"], null);
        }

        await runtime.PrepareForLowPowerAsync(context.LowPowerGate.Generation,
            () => competingController, default);

        Assert.Empty(context.Device.FanWrites);
    }

    [Fact]
    public async Task BatterySaverFanStop_LowPowerAmbiguousHandbackNeverReplays()
    {
        var temperatures = new BatterySaverTestTemperatureSource(new(45, null, true));
        TestContext context = await CreateBatterySaverFanStopContextAsync(temperatures);
        using ServiceControlRuntime runtime = context.Runtime;
        await runtime.SetBatterySaverFanStopAsync(true, default);
        context.Device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Fans)
            {
                context.Device.ReadStateFailure = new IOException("Uncertain cleanup readback.");
            }
        };
        context.LowPowerGate.Enter();
        await runtime.PrepareForLowPowerAsync(context.LowPowerGate.Generation, static () => true, default);
        int writes = context.Device.FanSetCalls;

        await runtime.PrepareForLowPowerAsync(context.LowPowerGate.Generation, static () => true, default);
        await runtime.MaintainFanCurveAsync(default);

        Assert.Equal(writes, context.Device.FanSetCalls);
        Assert.True(runtime.GetStatus().WritesBlocked);
    }

    [Fact]
    public async Task BatterySaverFanStop_AmbiguousWriteAndPreferenceSaveDoNotReplay()
    {
        var temperatures = new BatterySaverTestTemperatureSource(new(45, null, true));
        TestContext context = await CreateBatterySaverFanStopContextAsync(temperatures);
        using ServiceControlRuntime runtime = context.Runtime;
        context.Device.AfterSetter = subsystem =>
        {
            if (subsystem == DeviceSubsystem.Fans)
            {
                context.Device.ReadStateFailureOnCall = context.Device.StateReadCalls + 1;
            }
        };
        await runtime.SetBatterySaverFanStopAsync(true, default);
        int writes = context.Device.FanSetCalls;
        Assert.False(runtime.GetStatus().BatterySaverFanStopStatus!.Applied);

        await runtime.SetBatterySaverFanStopAsync(false, default);
        await runtime.MaintainFanCurveAsync(default);

        Assert.Equal(writes, context.Device.FanSetCalls);
        Assert.False(runtime.GetStatus().Profiles.Battery.StopFansInBatterySaver);
    }

    private async Task<TestContext> CreateBatterySaverFanStopContextAsync(IFanTemperatureSource temperatures,
        TimeProvider? clock = null)
    {
        TestContext context = CreateContext(true, initialPowerSource: PowerSource.Battery,
            fanTemperatureSource: temperatures, transitionRetryDelay: static (_, _) => Task.CompletedTask,
            timeProvider: clock);
        context.Device.Capabilities = DeviceCapabilities.SafeTargetDefaults() with
        {
            SupportsBatterySaverFanStop = true,
            SupportsDirectManualFanTransitions = true,
        };
        context.Device.AdapterPowerState = AdapterPowerState.Battery;
        PowerProfile battery = context.Runtime.GetStatus().Profiles.Battery;
        context.Device.ConfirmedState = new(PerformancePreset.BatterySaver, FanMode.Automatic,
            ConfirmedBattery(80), battery.Lighting)
        {
            HyperBoostActive = false,
        };
        await context.Runtime.ObserveAsync(default);
        return context;
    }

    private sealed class BatterySaverTestTemperatureSource(FanTemperatureReadings readings) : IFanTemperatureSource
    {
        internal FanTemperatureReadings Readings { get; set; } = readings;
        public ValueTask<FanTemperatureReadings> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Readings);
        }
    }
}
