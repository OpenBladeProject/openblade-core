using System.Collections.Concurrent;
using System.Threading.Channels;
using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ServiceLightingIdlePolicyLoopTests
{
    [Fact]
    public async Task IdleBoundarySuppressesOnceAndActivityRestoresOnce()
    {
        var client = new WatchClient();
        var idle = new FakeIdleTimeSource { Current = TimeSpan.FromMinutes(6) };
        var delay = new ManualDelay();
        List<Exception> failures = [];
        var loop = new ServiceLightingIdlePolicyLoop(
            client,
            idle,
            failures.Add,
            delay.DelayAsync);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(new LightingPowerPolicy(false, 5)));
        await client.Delivered.Task.WaitAsync(cancellation.Token);
        await WaitUntilAsync(() => client.Requests.Count == 1, cancellation.Token);

        Assert.Equal(
            (LightingSuppressionReason.UserIdle, true),
            client.Requests.Single());

        client.Publish(Status(new LightingPowerPolicy(false, 5)) with { Revision = 2 });
        await WaitUntilAsync(() => client.DeliveryCount == 2, cancellation.Token);
        await Task.Delay(20, cancellation.Token);
        Assert.Single(client.Requests);

        idle.Current = TimeSpan.Zero;
        await delay.TickAsync(cancellation.Token);
        await WaitUntilAsync(() => client.Requests.Count == 2, cancellation.Token);

        Assert.Equal(
            (LightingSuppressionReason.UserIdle, false),
            client.Requests.Last());
        Assert.Empty(failures);

        await cancellation.CancelAsync();
        await running;
    }

    [Fact]
    public async Task DisabledPolicyClearsStaleServiceSuppressionAfterReconnect()
    {
        var client = new WatchClient();
        var delay = new ManualDelay();
        var loop = new ServiceLightingIdlePolicyLoop(
            client,
            new FakeIdleTimeSource(),
            delay: delay.DelayAsync);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(LightingPowerPolicy.Disabled));
        await client.Delivered.Task.WaitAsync(cancellation.Token);
        await WaitUntilAsync(() => client.Requests.Count == 1, cancellation.Token);

        Assert.Equal(
            (LightingSuppressionReason.UserIdle, false),
            client.Requests.Single());

        await cancellation.CancelAsync();
        await running;
    }

    [Fact]
    public async Task SessionDisplayOffSuppressesAndDisplayOnRestores()
    {
        var client = new WatchClient();
        var display = new FakeDisplayStateSource();
        var loop = new ServiceLightingIdlePolicyLoop(
            client,
            new FakeIdleTimeSource(),
            displayStateSource: display);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(new LightingPowerPolicy(true, null)));
        display.Publish(displayOn: false);
        await WaitUntilAsync(
            () => client.Requests.Any(request => request ==
                (LightingSuppressionReason.DisplayOff, true)),
            cancellation.Token);

        display.Publish(displayOn: true);
        await WaitUntilAsync(
            () => client.Requests.Count(request => request.Reason ==
                LightingSuppressionReason.DisplayOff) == 2,
            cancellation.Token);

        Assert.Equal(
            [
                (LightingSuppressionReason.DisplayOff, true),
                (LightingSuppressionReason.DisplayOff, false),
            ],
            client.Requests.Where(request => request.Reason ==
                LightingSuppressionReason.DisplayOff));

        await cancellation.CancelAsync();
        await running;
    }

    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, true)]
    [InlineData(2u, true)]
    public void SessionDisplayState_MapsOffOnAndDimmed(uint value, bool expected)
    {
        Assert.Equal(
            expected,
            WindowsSessionDisplayStateSource.ClassifyDisplayOn(value));
    }

    [Fact]
    public void SessionDisplayState_IgnoresUnknownValues()
    {
        Assert.Null(WindowsSessionDisplayStateSource.ClassifyDisplayOn(3));
    }

    [Fact]
    public async Task ActivePolicySleepsUntilNearBoundaryInsteadOfPollingEverySecond()
    {
        var client = new WatchClient();
        var idle = new FakeIdleTimeSource { Current = TimeSpan.FromMinutes(1) };
        var delay = new RecordingDelay();
        var loop = new ServiceLightingIdlePolicyLoop(
            client,
            idle,
            delay: delay.DelayAsync);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(new LightingPowerPolicy(false, 5)));
        TimeSpan interval = await delay.NextIntervalAsync(cancellation.Token);

        Assert.Equal(TimeSpan.FromMinutes(4), interval);
        Assert.Equal(
            (LightingSuppressionReason.UserIdle, false),
            client.Requests.Single());

        await cancellation.CancelAsync();
        await running;
    }

    [Fact]
    public async Task PolicyChangeInterruptsLongBoundarySleep()
    {
        var client = new WatchClient();
        var idle = new FakeIdleTimeSource { Current = TimeSpan.FromMinutes(1) };
        var delay = new RecordingDelay();
        var loop = new ServiceLightingIdlePolicyLoop(
            client,
            idle,
            delay: delay.DelayAsync);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(new LightingPowerPolicy(false, 5)));
        Assert.Equal(
            TimeSpan.FromMinutes(4),
            await delay.NextIntervalAsync(cancellation.Token));

        client.Publish(Status(new LightingPowerPolicy(false, 2)) with { Revision = 2 });
        await WaitUntilAsync(() => client.DeliveryCount == 2, cancellation.Token);

        Assert.Equal(
            TimeSpan.FromMinutes(1),
            await delay.NextIntervalAsync(cancellation.Token));
        await cancellation.CancelAsync();
        await running;
    }

    [Fact]
    public async Task IdleSourceFailureUsesRetryDelayInsteadOfHotSpinning()
    {
        var client = new WatchClient();
        var idle = new ThrowingIdleTimeSource();
        var delay = new RecordingDelay();
        List<Exception> failures = [];
        var loop = new ServiceLightingIdlePolicyLoop(
            client,
            idle,
            failures.Add,
            delay.DelayAsync);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(new LightingPowerPolicy(false, 5)));

        Assert.Equal(
            TimeSpan.FromSeconds(5),
            await delay.NextIntervalAsync(cancellation.Token));
        Assert.Single(failures);
        Assert.Equal(1, idle.ReadCount);

        await cancellation.CancelAsync();
        await running;
    }

    [Fact]
    public async Task FailedSuppressionWriteUsesRetryDelayWhenPolicyIsDisabled()
    {
        var client = new WatchClient
        {
            SuppressionResult = new ControlOperationResult(
                ControlOperationStatus.Failed,
                "Synthetic write failure."),
        };
        var delay = new RecordingDelay();
        List<Exception> failures = [];
        var loop = new ServiceLightingIdlePolicyLoop(
            client,
            new FakeIdleTimeSource(),
            failures.Add,
            delay.DelayAsync);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        client.Publish(Status(LightingPowerPolicy.Disabled));

        Assert.Equal(
            TimeSpan.FromSeconds(5),
            await delay.NextIntervalAsync(cancellation.Token));
        Assert.Single(client.Requests);
        Assert.Single(failures);

        await cancellation.CancelAsync();
        await running;
    }

    private static ServiceStatusSnapshot Status(LightingPowerPolicy policy)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        profiles = profiles with
        {
            PluggedIn = profiles.PluggedIn with { LightingPowerPolicy = policy },
        };
        return new ServiceStatusSnapshot(
            Revision: 1,
            DeviceVerified: true,
            WritesBlocked: false,
            PotentialControllers: [],
            ControllerDetectionWarning: null,
            ExternalChanges: [],
            Message: "Ready",
            Capabilities: DeviceCapabilities.Unavailable(),
            ConfirmedState: null,
            Profiles: profiles,
            DesiredState: new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(
                    profiles.PluggedIn.Performance,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(
                    profiles.PluggedIn.Fans,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(
                    profiles.PluggedIn.Lighting,
                    SettingValueSource.PowerProfile),
                new DesiredBatteryState(profiles.BatteryPolicy, true, false)),
            ActivePowerSource: PowerSource.PluggedIn,
            LiveReadings: null,
            PerformanceOverride: null,
            FanOverride: null,
            LightingOverride: null,
            FullChargeOverride: false);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    private sealed class FakeIdleTimeSource : IUserIdleTimeSource
    {
        public TimeSpan? Current { get; set; }

        public TimeSpan? GetIdleTime() => Current;
    }

    private sealed class FakeDisplayStateSource : IUserDisplayStateSource
    {
        private readonly Channel<bool> updates = Channel.CreateUnbounded<bool>();

        public void Publish(bool displayOn) => updates.Writer.TryWrite(displayOn);

        public IAsyncEnumerable<bool> WatchDisplayOnAsync(
            CancellationToken cancellationToken) =>
            updates.Reader.ReadAllAsync(cancellationToken);
    }

    private sealed class ManualDelay
    {
        private readonly Channel<TaskCompletionSource> waits =
            Channel.CreateUnbounded<TaskCompletionSource>();

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var wait = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await waits.Writer.WriteAsync(wait, cancellationToken);
            await wait.Task.WaitAsync(cancellationToken);
        }

        public async Task TickAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource wait = await waits.Reader.ReadAsync(cancellationToken);
            wait.TrySetResult();
        }
    }

    private sealed class ThrowingIdleTimeSource : IUserIdleTimeSource
    {
        public int ReadCount { get; private set; }

        public TimeSpan? GetIdleTime()
        {
            ReadCount++;
            throw new InvalidOperationException("Synthetic idle-source failure.");
        }
    }

    private sealed class RecordingDelay
    {
        private readonly Channel<TimeSpan> intervals = Channel.CreateUnbounded<TimeSpan>();

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            await intervals.Writer.WriteAsync(delay, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public ValueTask<TimeSpan> NextIntervalAsync(CancellationToken cancellationToken) =>
            intervals.Reader.ReadAsync(cancellationToken);
    }

    private sealed class WatchClient : IServiceClient
    {
        private readonly Channel<ServiceWatchUpdate> updates =
            Channel.CreateUnbounded<ServiceWatchUpdate>();
        private int deliveryCount;

        public ConcurrentQueue<(LightingSuppressionReason Reason, bool Suppressed)> Requests
            { get; } = new();

        public TaskCompletionSource Delivered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int DeliveryCount => Volatile.Read(ref deliveryCount);

        public ControlOperationResult SuppressionResult { get; set; } = new(
            ControlOperationStatus.Applied,
            "Applied");

        public void Publish(ServiceStatusSnapshot status) => updates.Writer.TryWrite(
            new ServiceWatchUpdate.Connected(status));

        public async IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken token)
        {
            await foreach (ServiceWatchUpdate update in updates.Reader.ReadAllAsync(token))
            {
                yield return update;
                Interlocked.Increment(ref deliveryCount);
                Delivered.TrySetResult();
            }
        }

        public Task<ControlOperationResult> SetLightingSuppressionAsync(
            LightingSuppressionReason reason,
            bool suppressed,
            CancellationToken token)
        {
            Requests.Enqueue((reason, suppressed));
            return Task.FromResult(SuppressionResult);
        }

        public Task<ServiceStatusSnapshot> GetStatusAsync(CancellationToken token) =>
            throw new NotSupportedException();

        public Task<ServiceStatusSnapshot> ObserveStateAsync(
            bool includeLiveReadings,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DiagnosticArchive> ExportDiagnosticsAsync(
            bool liveReadingsEnabled,
            CancellationToken token) => throw new NotSupportedException();

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

        public Task<ControlOperationResult> SetBatteryProtectionAsync(
            bool enabled,
            CancellationToken token) => throw new NotSupportedException();

        public Task<ControlOperationResult> SetFullChargeOverrideAsync(
            bool enabled,
            CancellationToken token) => throw new NotSupportedException();
    }
}
