using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBlade.Contracts;

namespace OpenBlade.Service.Tests;

public sealed class ServiceFanCurveLoopTests
{
    [Fact]
    public async Task ReconciliationRequest_WakesPromptlyAndPreservesCadence()
    {
        var runtime = new RecordingRuntime();
        TimeSpan interval = TimeSpan.FromMilliseconds(60);
        var loop = new ServiceFanCurveLoop(
            runtime,
            NullLogger.Instance,
            TimeProvider.System,
            interval);
        using var cancellation = new CancellationTokenSource();
        Task running = loop.RunAsync(cancellation.Token);

        runtime.RequestReconciliation();
        await runtime.WaitForCoolingPadRunsAsync(1).WaitAsync(TimeSpan.FromSeconds(2));
        runtime.RequestReconciliation();
        await runtime.WaitForCoolingPadRunsAsync(2).WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();
        await running;

        Assert.Equal(2, runtime.CoolingPadRuns);
        Assert.Equal(2, runtime.FanCurveRuns);
        Assert.True(
            runtime.CoolingPadRunTimestamps[1]
                - runtime.CoolingPadRunTimestamps[0]
                >= interval - TimeSpan.FromMilliseconds(5));
    }

    private sealed class RecordingRuntime : IServiceControlRuntime
    {
        private readonly SemaphoreSlim signal = new(0, 1);
        private readonly object gate = new();
        private readonly List<TimeSpan> timestamps = [];
        private TaskCompletionSource changed = NewCompletion();

        internal int FanCurveRuns { get; private set; }

        internal int CoolingPadRuns { get; private set; }

        internal IReadOnlyList<TimeSpan> CoolingPadRunTimestamps
        {
            get
            {
                lock (gate)
                {
                    return [.. timestamps];
                }
            }
        }

        internal void RequestReconciliation()
        {
            try
            {
                signal.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }

        internal async Task WaitForCoolingPadRunsAsync(int count)
        {
            while (true)
            {
                Task wait;
                lock (gate)
                {
                    if (CoolingPadRuns >= count)
                    {
                        return;
                    }
                    wait = changed.Task;
                }
                await wait;
            }
        }

        public Task WaitForCoolingPadReconciliationRequestAsync(
            CancellationToken cancellationToken) => signal.WaitAsync(cancellationToken);

        public Task MaintainFanCurveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FanCurveRuns++;
            return Task.CompletedTask;
        }

        public Task MaintainCoolingPadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                CoolingPadRuns++;
                timestamps.Add(Stopwatch.GetElapsedTime(0));
                TaskCompletionSource previous = changed;
                changed = NewCompletion();
                previous.TrySetResult();
            }
            return Task.CompletedTask;
        }

        public ServiceStatusSnapshot GetStatus() => throw new NotSupportedException();
        public Task<ControlOperationResult> UpdateProfilesAsync(ProfileSet profiles, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetPerformanceOverrideAsync(ManualOverride<PerformancePreset>? value, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetFanOverrideAsync(ManualOverride<FanMode>? value, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetLightingOverrideAsync(ManualOverride<LightingSettings>? value, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetBatteryPolicyAsync(BatteryPolicy value, CancellationToken token) => throw new NotSupportedException();
        public Task<ControlOperationResult> SetFullChargeOverrideAsync(bool enabled, CancellationToken token) => throw new NotSupportedException();
        public Task ObserveAsync(bool includeLiveReadings, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task HandlePowerSourceChangedAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task RefreshControllerPresenceAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        private static TaskCompletionSource NewCompletion() => new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
