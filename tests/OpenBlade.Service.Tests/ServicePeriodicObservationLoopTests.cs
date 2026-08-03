using Microsoft.Extensions.Logging.Abstractions;
using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed class ServicePeriodicObservationLoopTests
{
    [Fact]
    public async Task RunAsync_PeriodicallyRunsActiveMaintenance()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = new RecordingRuntime(cancellation);
        TimeSpan? requestedDelay = null;
        var loop = new ServicePeriodicObservationLoop(
            runtime,
            new LowPowerGate(),
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

    [Fact]
    public async Task RunAsync_PowerRefreshFailureDoesNotSkipDriverModeMaintenance()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = new RecordingRuntime(cancellation)
        {
            RefreshFailure = new IOException("adapter query failed"),
        };
        var loop = new ServicePeriodicObservationLoop(
            runtime,
            new LowPowerGate(),
            NullLogger.Instance,
            (_, _) => Task.CompletedTask);

        await loop.RunAsync(cancellation.Token);

        Assert.Equal(1, runtime.RefreshCalls);
        Assert.Equal(1, runtime.DriverModeMaintenanceCalls);
        Assert.Equal(1, runtime.RestoreCalls);
    }

    [Fact]
    public async Task RunAsync_LowPowerGateSkipsMutatingMaintenanceAndResumesAfterExit()
    {
        var gate = new LowPowerGate();
        Assert.True(gate.Enter());
        using var gatedCancellation = new CancellationTokenSource();
        var gatedRuntime = new RecordingRuntime(gatedCancellation);
        var gatedLoop = new ServicePeriodicObservationLoop(
            gatedRuntime,
            gate,
            NullLogger.Instance,
            (_, _) =>
            {
                gatedCancellation.Cancel();
                return Task.CompletedTask;
            });

        await gatedLoop.RunAsync(gatedCancellation.Token);

        Assert.Equal(0, gatedRuntime.RefreshCalls);
        Assert.Equal(0, gatedRuntime.DriverModeMaintenanceCalls);
        Assert.Equal(0, gatedRuntime.RestoreCalls);

        Assert.True(gate.Exit());
        using var activeCancellation = new CancellationTokenSource();
        var activeRuntime = new RecordingRuntime(activeCancellation);
        var activeLoop = new ServicePeriodicObservationLoop(
            activeRuntime,
            gate,
            NullLogger.Instance,
            (_, _) => Task.CompletedTask);

        await activeLoop.RunAsync(activeCancellation.Token);

        Assert.Equal(1, activeRuntime.DriverModeMaintenanceCalls);
        Assert.Equal(1, activeRuntime.RestoreCalls);
    }

    [Fact]
    public async Task RunAsync_LowPowerGateLogsOneCombinedSkipAcrossRepeatedIterations()
    {
        var gate = new LowPowerGate();
        Assert.True(gate.Enter());
        using var cancellation = new CancellationTokenSource();
        var runtime = new RecordingRuntime(cancellation);
        using var history = new DiagnosticHistory();
        int delayCount = 0;
        var loop = new ServicePeriodicObservationLoop(
            runtime,
            gate,
            history.CreateLogger("OpenBlade.Service.ServicePeriodicObservationLoop"),
            (_, _) =>
            {
                if (Interlocked.Increment(ref delayCount) == 5)
                {
                    cancellation.Cancel();
                }

                return Task.CompletedTask;
            });

        await loop.RunAsync(cancellation.Token);

        Assert.Equal(5, delayCount);
        Assert.Equal(0, runtime.RefreshCalls);
        Assert.Equal(0, runtime.DriverModeMaintenanceCalls);
        Assert.Equal(0, runtime.RestoreCalls);
        Assert.Single(
            history.Snapshot().LogLines,
            line => line.Contains(
                "Skipping periodic power classification",
                StringComparison.Ordinal));
    }

    private sealed class RecordingRuntime(CancellationTokenSource cancellation)
        : IServiceControlRuntime
    {
        public int RefreshCalls { get; private set; }
        public int DriverModeMaintenanceCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public Exception? RefreshFailure { get; init; }

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
            return RefreshFailure is null
                ? Task.CompletedTask
                : Task.FromException(RefreshFailure);
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
