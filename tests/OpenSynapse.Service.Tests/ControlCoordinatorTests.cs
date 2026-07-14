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

    private sealed class FakeConflictDetector(IReadOnlyList<string> conflicts) : IConflictDetector
    {
        public IReadOnlyList<string> GetRunningConflicts() => conflicts;
    }

    private sealed class FakeBladeDevice : IBladeDevice
    {
        public PerformancePreset Performance { get; set; } = PerformancePreset.Silent;

        public int PerformanceWriteCount { get; private set; }

        public Task<DeviceCapabilities> ReadCapabilitiesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(DeviceCapabilities.SafeTargetDefaults());

        public Task<ConfirmedDeviceState> ReadStateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ConfirmedDeviceState(Performance, FanMode.Automatic, null, null));

        public Task SetPerformanceAsync(PerformancePreset value, CancellationToken cancellationToken)
        {
            PerformanceWriteCount++;
            Performance = value;
            return Task.CompletedTask;
        }

        public Task SetFanModeAsync(FanMode value, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetBatteryPolicyAsync(BatteryPolicy value, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetLightingAsync(LightingSettings value, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
