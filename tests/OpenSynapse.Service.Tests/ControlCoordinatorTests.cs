using OpenSynapse.Contracts;
using OpenSynapse.Protocol;
using OpenSynapse.Service;

namespace OpenSynapse.Service.Tests;

public sealed class ControlCoordinatorTests
{
    [Fact]
    public async Task ApplyPerformance_BlocksWriteWhenSynapseConflictIsRunning()
    {
        FakeBladeDevice device = new();
        ControlCoordinator coordinator = new(
            device,
            new FakeConflictDetector(["Razer Synapse Service"]));

        ControlResult result = await coordinator.ApplyPerformanceAsync(
            PerformancePreset.Balanced,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Blocked, result.Status);
        Assert.Equal(0, device.PerformanceWriteCount);
        Assert.Contains("Razer Synapse Service", result.Message);
    }

    [Fact]
    public async Task ApplyPerformance_SkipsWriteWhenValueIsAlreadyConfirmed()
    {
        FakeBladeDevice device = new() { Performance = PerformancePreset.Balanced };
        ControlCoordinator coordinator = new(device, new FakeConflictDetector([]));

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
        ControlCoordinator coordinator = new(device, new FakeConflictDetector([]));

        ControlResult result = await coordinator.ApplyPerformanceAsync(
            PerformancePreset.Balanced,
            CancellationToken.None);

        Assert.Equal(ControlResultStatus.Applied, result.Status);
        Assert.Equal(1, device.PerformanceWriteCount);
        Assert.Equal(PerformancePreset.Balanced, device.Performance);
    }

    [Fact]
    public async Task ApplyDesiredState_ReportsSubsystemsIndependently()
    {
        FakeBladeDevice device = new()
        {
            Performance = PerformancePreset.Silent,
            Fans = FanMode.Automatic,
            Battery = BatteryPolicy.Create(75),
            ThrowOnFanWrite = true,
        };
        ControlCoordinator coordinator = new(device, new FakeConflictDetector([]));
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

    private sealed class FakeConflictDetector(IReadOnlyList<string> conflicts) : IConflictDetector
    {
        public IReadOnlyList<string> GetRunningConflicts() => conflicts;
    }

    private sealed class FakeBladeDevice : IBladeDevice
    {
        public PerformancePreset Performance { get; set; } = PerformancePreset.Silent;

        public int PerformanceWriteCount { get; private set; }

        public FanMode Fans { get; set; } = FanMode.Automatic;

        public BatteryPolicy? Battery { get; set; }

        public LightingSettings? Lighting { get; set; }

        public bool ThrowOnFanWrite { get; set; }

        public int BatteryWriteCount { get; private set; }

        public Task<DeviceCapabilities> ReadCapabilitiesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(DeviceCapabilities.SafeTargetDefaults());

        public Task<ConfirmedDeviceState> ReadStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ConfirmedDeviceState(Performance, Fans, Battery, Lighting));

        public Task SetPerformanceAsync(PerformancePreset value, CancellationToken cancellationToken)
        {
            PerformanceWriteCount++;
            Performance = value;
            return Task.CompletedTask;
        }

        public Task SetFanModeAsync(FanMode value, CancellationToken cancellationToken)
        {
            if (ThrowOnFanWrite)
            {
                throw new IOException("simulated fan failure");
            }

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
            Lighting = value;
            return Task.CompletedTask;
        }
    }
}
