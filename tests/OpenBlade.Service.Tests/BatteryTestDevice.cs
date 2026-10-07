using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Service.Tests;

internal sealed class BatteryTestDevice : IBladeDevice
{
    public DeviceCapabilities Capabilities { get; set; } = DeviceCapabilities.SafeTargetDefaults() with
    {
        ChargeLimits = new HashSet<int> { 50, 55, 60, 65, 70, 75, 80 },
    };

    public ConfirmedDeviceState State { get; set; } = new(
        PerformancePreset.Silent,
        FanMode.Automatic,
        new ConfirmedBatteryState(true, 80, false),
        new LightingSettings(LightingEffect.Off, default, 0));

    public List<string> BatterySetterCalls { get; } = [];
    public bool AutoConfirmWrites { get; set; } = true;
    public int? ReadStateFailureOnCall { get; set; }
    public Exception? BatteryWriteFailure { get; set; }
    public Action<DeviceSubsystem>? AfterSetter { get; set; }
    public int StateReadCalls { get; private set; }
    internal Func<CancellationToken, Task<AdapterPowerState>>? ReadAdapterPower;

    public Task<DeviceCapabilities> ReadCapabilitiesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Capabilities);
    }

    public Task<ConfirmedDeviceState> ReadStateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StateReadCalls++;
        if (StateReadCalls == ReadStateFailureOnCall)
        {
            throw new IOException("readback failed");
        }

        return Task.FromResult(State);
    }

    public Task SetChargeLimitAsync(BatteryPolicy value, CancellationToken cancellationToken) =>
        WriteAsync("SetChargeLimit", new ConfirmedBatteryState(true, value.ChargeLimitPercent, false), cancellationToken);

    public Task SetBatteryProtectionOffAsync(CancellationToken cancellationToken) =>
        WriteAsync("SetProtectionOff", new ConfirmedBatteryState(false, null, false), cancellationToken);

    public Task SetTemporaryFullChargeAsync(bool enabled, CancellationToken cancellationToken) =>
        WriteAsync("SetTemporaryFullCharge", State.Battery is { } battery
            ? new ConfirmedBatteryState(battery.ProtectionEnabled, battery.ChargeLimitPercent, enabled)
            : null, cancellationToken);

    private Task WriteAsync(string name, ConfirmedBatteryState? battery, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BatterySetterCalls.Add(name);
        if (BatteryWriteFailure is not null)
        {
            throw BatteryWriteFailure;
        }

        if (AutoConfirmWrites)
        {
            State = State with { Battery = battery };
        }

        AfterSetter?.Invoke(DeviceSubsystem.Battery);
        return Task.CompletedTask;
    }

    public Task<PowerTransitionDeviceStateReadResult> ReadStateForPowerTransitionAsync(
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<FanRpmReadings?> ReadFanRpmAsync(
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<AdapterPowerState> ReadAdapterPowerStateAsync(
        CancellationToken cancellationToken) => ReadAdapterPower?.Invoke(cancellationToken)
            ?? throw new NotSupportedException();

    public Task SetPerformanceAsync(
        PerformancePreset value,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task SetPerformanceAsync(
        PerformancePreset value,
        CustomPerformanceSettings? customPerformance,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task SetPerformanceAsync(
        PerformancePreset value,
        CustomPerformanceSettings? customPerformance,
        GpuClockOffsetSettings? gpuClockOffsets,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task SetGpuClockOffsetsAsync(
        GpuClockOffsetSettings value,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task SetFanModeAsync(
        FanMode value,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task SetFanControlAsync(
        FanControlSettings value,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task SetFanTargetsAsync(
        int fan1Rpm,
        int fan2Rpm,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task SetLightingAsync(
        LightingSettings value,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<byte> StepKeyboardBrightnessAsync(
        KeyboardBrightnessDirection direction,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task ApplyOneShotLaptopControlAsync(
        ApplyOneShotLaptopControl value,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task PulseReactiveKeyAsync(
        ReactiveKeyPulse pulse,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task PushAmbientLightingFrameAsync(
        AmbientLightingFrame frame,
        CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task SetPlaybackMuteIndicatorAsync(
        bool muted,
        CancellationToken cancellationToken) => throw new NotSupportedException();
}
