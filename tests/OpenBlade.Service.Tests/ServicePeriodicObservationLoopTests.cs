using Microsoft.Extensions.Logging.Abstractions;
using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed class ServicePeriodicObservationLoopTests
{
    [Fact]
    public async Task RunAsync_PeriodicallyRequestsReadOnlyObservation()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = new RecordingRuntime(cancellation);
        TimeSpan? requestedDelay = null;
        var loop = new ServicePeriodicObservationLoop(
            runtime,
            NullLogger.Instance,
            (delay, _) =>
            {
                requestedDelay = delay;
                return Task.CompletedTask;
            });

        await loop.RunAsync(cancellation.Token);

        Assert.Equal(ServicePeriodicObservationLoop.ObservationInterval, requestedDelay);
        Assert.Equal(1, runtime.RefreshCalls);
        Assert.Equal(1, runtime.DriverModeMaintenanceCalls);
        Assert.Equal(1, runtime.RestoreCalls);
    }

    private sealed class RecordingRuntime(CancellationTokenSource cancellation)
        : IServiceControlRuntime
    {
        public int RefreshCalls { get; private set; }
        public int DriverModeMaintenanceCalls { get; private set; }
        public int RestoreCalls { get; private set; }

        public ServiceStatusSnapshot GetStatus() => throw new NotSupportedException();

        public Task<ControlOperationResult> UpdateProfilesAsync(
            ProfileSet profiles,
            CancellationToken token) => throw new NotSupportedException();

        public Task<ControlOperationResult> SetPerformanceOverrideAsync(
            ManualOverride<PerformancePreset>? value,
            CancellationToken token) => throw new NotSupportedException();

        public Task<ControlOperationResult> SetFanOverrideAsync(
            ManualOverride<FanMode>? value,
            CancellationToken token) => throw new NotSupportedException();

        public Task<ControlOperationResult> SetLightingOverrideAsync(
            ManualOverride<LightingSettings>? value,
            CancellationToken token) => throw new NotSupportedException();

        public Task<ControlOperationResult> SetBatteryPolicyAsync(
            BatteryPolicy value,
            CancellationToken token) => throw new NotSupportedException();

        public Task<ControlOperationResult> SetFullChargeOverrideAsync(
            bool enabled,
            CancellationToken token) => throw new NotSupportedException();

        public Task ObserveAsync(
            bool includeLiveReadings,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RefreshPowerSourceClassificationAsync(
            CancellationToken cancellationToken)
        {
            RefreshCalls++;
            return Task.CompletedTask;
        }

        public Task RestoreLightingRendererAsync(CancellationToken cancellationToken)
        {
            RestoreCalls++;
            cancellation.Cancel();
            return Task.CompletedTask;
        }

        public Task MaintainDriverModeAsync(CancellationToken cancellationToken)
        {
            DriverModeMaintenanceCalls++;
            return Task.CompletedTask;
        }

        public Task HandlePowerSourceChangedAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RefreshControllerPresenceAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
