using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class ControlCoordinatorTests
{
    [Fact]
    public async Task ApplyPerformance_BlocksWriteWhenSynapseConflictIsRunning()
    {
        FakeBladeDevice device = new();
        ControlCoordinator coordinator = new(
            device,
            new FakeHardwareAccessGuard(["Razer Synapse Service"]));

        ControlResult result = await coordinator.ApplyPerformanceAsync(
            PerformancePreset.Balanced,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Blocked, result.Status);
        Assert.Equal(0, device.ReadCount);
        Assert.Equal(0, device.PerformanceWriteCount);
        Assert.Contains("Razer Synapse Service", result.Message);
    }

    [Fact]
    public async Task ApplyPerformance_BlocksDetectionFailureBeforeDeviceAccess()
    {
        FakeBladeDevice device = new();
        ControlCoordinator coordinator = new(
            device,
            new FailedHardwareAccessGuard("Windows service enumeration failed."));

        ControlResult result = await coordinator.ApplyPerformanceAsync(
            PerformancePreset.Balanced,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Blocked, result.Status);
        Assert.Contains("Windows service enumeration failed.", result.Message);
        Assert.Equal(0, device.ReadCount);
        Assert.Equal(0, device.PerformanceWriteCount);
    }

    [Fact]
    public async Task ApplyPerformance_SkipsWriteWhenValueIsAlreadyConfirmed()
    {
        FakeBladeDevice device = new() { Performance = PerformancePreset.Balanced };
        ControlCoordinator coordinator = new(device, new FakeHardwareAccessGuard([]));

        ControlResult result = await coordinator.ApplyPerformanceAsync(
            PerformancePreset.Balanced,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Unchanged, result.Status);
        Assert.Equal(0, device.PerformanceWriteCount);
    }

    [Fact]
    public async Task ApplyPerformance_WritesValidatedChangeWithoutConflicts()
    {
        FakeBladeDevice device = new() { Performance = PerformancePreset.Silent };
        ControlCoordinator coordinator = new(device, new FakeHardwareAccessGuard([]));

        ControlResult result = await coordinator.ApplyPerformanceAsync(
            PerformancePreset.Balanced,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, result.Status);
        Assert.Equal(1, device.PerformanceWriteCount);
        Assert.Equal(PerformancePreset.Balanced, device.Performance);
    }

    [Fact]
    public async Task ApplyPerformance_RechecksConflictsImmediatelyBeforeWrite()
    {
        FakeBladeDevice device = new() { Performance = PerformancePreset.Silent };
        ControlCoordinator coordinator = new(
            device,
            new SequencedHardwareAccessGuard([], ["RazerAppEngine"]));

        ControlResult result = await coordinator.ApplyPerformanceAsync(
            PerformancePreset.Balanced,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Blocked, result.Status);
        Assert.Equal(0, device.PerformanceWriteCount);
    }

    [Fact]
    public async Task ApplyPerformance_BlocksUnsupportedCapability()
    {
        FakeBladeDevice device = new()
        {
            Performance = PerformancePreset.Silent,
            Capabilities = DeviceCapabilities.SafeTargetDefaults() with
            {
                PerformancePresets = new HashSet<PerformancePreset> { PerformancePreset.Silent },
            },
        };
        ControlCoordinator coordinator = new(device, new FakeHardwareAccessGuard([]));

        ControlResult result = await coordinator.ApplyPerformanceAsync(
            PerformancePreset.Balanced,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Blocked, result.Status);
        Assert.Equal(0, device.PerformanceWriteCount);
    }

    [Fact]
    public async Task ApplyPerformance_BlocksInvalidEnumValue()
    {
        FakeBladeDevice device = new() { Performance = PerformancePreset.Silent };
        ControlCoordinator coordinator = new(device, new FakeHardwareAccessGuard([]));

        ControlResult result = await coordinator.ApplyPerformanceAsync(
            (PerformancePreset)999,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Blocked, result.Status);
        Assert.Equal(0, device.PerformanceWriteCount);
    }

    [Fact]
    public async Task ApplyDesiredState_ReportsSubsystemsIndependently()
    {
        FakeBladeDevice device = new()
        {
            Performance = PerformancePreset.Silent,
            Fans = FanMode.Automatic,
            Battery = BatteryPolicy.Create(75),
            FanWriteException = new InvalidOperationException("simulated fan application failure"),
        };
        ControlCoordinator coordinator = new(device, new FakeHardwareAccessGuard([]));
        LightingSettings lighting = new(LightingEffect.Off, new RgbColor(0, 0, 0), 0);
        DesiredDeviceState desired = new(
            new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
            new ResolvedSetting<FanMode>(FanMode.Maximum, SettingValueSource.PowerProfile),
            new ResolvedSetting<LightingSettings>(lighting, SettingValueSource.PowerProfile),
            BatteryPolicy.Create(80));

        SubsystemApplyResults results = await coordinator.ApplyDesiredStateAsync(
            desired,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, results.Performance.Status);
        Assert.Equal(ControlResultStatus.Failed, results.Fans.Status);
        Assert.Equal(ControlResultStatus.Applied, results.Battery.Status);
        Assert.Equal(ControlResultStatus.Applied, results.Lighting.Status);
        Assert.Equal(1, device.BatteryWriteCount);
    }

    [Fact]
    public async Task ApplyDesiredState_StopsWritesAfterTerminalTransportFailure()
    {
        FakeBladeDevice device = new()
        {
            Performance = PerformancePreset.Silent,
            Fans = FanMode.Automatic,
            Battery = BatteryPolicy.Create(75),
            PerformanceWriteException = new IOException("device disconnected"),
        };
        ControlCoordinator coordinator = new(device, new FakeHardwareAccessGuard([]));
        DesiredDeviceState desired = CreateDesiredState();

        SubsystemApplyResults results = await coordinator.ApplyDesiredStateAsync(
            desired,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Failed, results.Performance.Status);
        Assert.Equal(ControlResultStatus.Blocked, results.Fans.Status);
        Assert.Equal(ControlResultStatus.Blocked, results.Battery.Status);
        Assert.Equal(ControlResultStatus.Blocked, results.Lighting.Status);
        Assert.Equal(0, device.FanWriteCount);
        Assert.Equal(0, device.BatteryWriteCount);
        Assert.Equal(0, device.LightingWriteCount);
    }

    [Fact]
    public async Task ApplyDesiredState_StopsWritesAfterIndeterminateTimeout()
    {
        FakeBladeDevice device = new()
        {
            Performance = PerformancePreset.Silent,
            Fans = FanMode.Automatic,
            Battery = BatteryPolicy.Create(75),
            PerformanceWriteException = new TimeoutException("device response timed out"),
        };
        ControlCoordinator coordinator = new(device, new FakeHardwareAccessGuard([]));

        SubsystemApplyResults results = await coordinator.ApplyDesiredStateAsync(
            CreateDesiredState(),
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Failed, results.Performance.Status);
        Assert.Equal(ControlResultStatus.Blocked, results.Fans.Status);
        Assert.Equal(0, device.FanWriteCount);
        Assert.Equal(0, device.BatteryWriteCount);
        Assert.Equal(0, device.LightingWriteCount);
    }

    [Fact]
    public async Task ApplyDesiredState_RechecksConflictsBeforeEachSetter()
    {
        FakeBladeDevice device = new()
        {
            Performance = PerformancePreset.Silent,
            Fans = FanMode.Automatic,
            Battery = BatteryPolicy.Create(75),
        };
        ControlCoordinator coordinator = new(
            device,
            new SequencedHardwareAccessGuard([], [], ["RazerAppEngine"]));

        SubsystemApplyResults results = await coordinator.ApplyDesiredStateAsync(
            CreateDesiredState(),
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, results.Performance.Status);
        Assert.Equal(ControlResultStatus.Blocked, results.Fans.Status);
        Assert.Equal(0, device.FanWriteCount);
        Assert.Equal(0, device.BatteryWriteCount);
        Assert.Equal(0, device.LightingWriteCount);
    }

    private static DesiredDeviceState CreateDesiredState()
    {
        LightingSettings lighting = new(LightingEffect.Off, new RgbColor(0, 0, 0), 0);
        return new DesiredDeviceState(
            new ResolvedSetting<PerformancePreset>(PerformancePreset.Balanced, SettingValueSource.PowerProfile),
            new ResolvedSetting<FanMode>(FanMode.Maximum, SettingValueSource.PowerProfile),
            new ResolvedSetting<LightingSettings>(lighting, SettingValueSource.PowerProfile),
            BatteryPolicy.Create(80));
    }

    private sealed class FakeHardwareAccessGuard(IReadOnlyList<string> conflicts) : IHardwareAccessGuard
    {
        public HardwareAccessDecision Check() => new(conflicts, null);
    }

    private sealed class SequencedHardwareAccessGuard(params IReadOnlyList<string>[] results) : IHardwareAccessGuard
    {
        private int _index;

        public HardwareAccessDecision Check()
        {
            int index = Math.Min(_index, results.Length - 1);
            _index++;
            IReadOnlyList<string> conflicts = results[index];
            return new HardwareAccessDecision(conflicts, null);
        }
    }

    private sealed class FailedHardwareAccessGuard(string failure) : IHardwareAccessGuard
    {
        public HardwareAccessDecision Check() => new([], failure);
    }

    private sealed class FakeBladeDevice : IBladeDevice
    {
        public PerformancePreset Performance { get; set; } = PerformancePreset.Silent;

        public int PerformanceWriteCount { get; private set; }

        public FanMode Fans { get; set; } = FanMode.Automatic;

        public BatteryPolicy? Battery { get; set; }

        public LightingSettings? Lighting { get; set; }

        public DeviceCapabilities Capabilities { get; set; } = DeviceCapabilities.SafeTargetDefaults();

        public Exception? PerformanceWriteException { get; set; }

        public Exception? FanWriteException { get; set; }

        public int FanWriteCount { get; private set; }

        public int BatteryWriteCount { get; private set; }

        public Task<DeviceCapabilities> ReadCapabilitiesAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(Capabilities);
        }

        public Task<ConfirmedDeviceState> ReadStateAsync(CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(new ConfirmedDeviceState(Performance, Fans, Battery, Lighting));
        }

        public Task SetPerformanceAsync(PerformancePreset value, CancellationToken cancellationToken)
        {
            if (PerformanceWriteException is not null)
            {
                throw PerformanceWriteException;
            }

            PerformanceWriteCount++;
            Performance = value;
            return Task.CompletedTask;
        }

        public Task SetFanModeAsync(FanMode value, CancellationToken cancellationToken)
        {
            if (FanWriteException is not null)
            {
                throw FanWriteException;
            }

            FanWriteCount++;
            Fans = value;
            return Task.CompletedTask;
        }

        public Task SetBatteryPolicyAsync(BatteryPolicy value, CancellationToken cancellationToken)
        {
            BatteryWriteCount++;
            Battery = value;
            return Task.CompletedTask;
        }

        public Task SetLightingAsync(LightingSettings value, CancellationToken cancellationToken)
        {
            LightingWriteCount++;
            Lighting = value;
            return Task.CompletedTask;
        }

        public int LightingWriteCount { get; private set; }

        public int ReadCount { get; private set; }
    }
}
