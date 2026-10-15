using System.Collections.Concurrent;
using System.Threading.Channels;
using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ServiceAmbientLightingLoopTests
{
    [Fact]
    public async Task CaptureFailureDisposesAndReplacesSampler()
    {
        var delays = new ControlledDelay();
        var failed = new ThrowingSampler(new IOException("Capture failed."));
        var replacement = new ConstantSampler(Frame(1));
        var factory = new SequenceSamplerFactory(
            () => failed,
            () => replacement);
        var client = new WatchClient();
        var failures = new ConcurrentQueue<Exception>();
        await using var loop = new ServiceAmbientLightingLoop(
            client,
            factory,
            failures.Enqueue,
            delays.DelayAsync,
            client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        client.PublishConnected();
        DelayRequest retry = await delays.ReadAsync(cancellation.Token);
        Assert.Equal(TimeSpan.FromSeconds(1), retry.Interval);
        Assert.True(failed.Disposed);
        retry.Release();

        _ = await client.ReadRequestAsync(cancellation.Token);
        Assert.Equal(2, factory.CreateCount);
        Assert.Contains(failures, exception => exception.Message == "Capture failed.");

        await CancelAndAwaitAsync(cancellation, running);
        Assert.True(replacement.Disposed);
    }

    [Fact]
    public async Task SamplerCreationFailureIsRetried()
    {
        var delays = new ControlledDelay();
        var replacement = new ConstantSampler(Frame(1));
        var factory = new SequenceSamplerFactory(
            () => throw new IOException("Creation failed."),
            () => replacement);
        var client = new WatchClient();
        await using var loop = new ServiceAmbientLightingLoop(
            client,
            factory,
            delay: delays.DelayAsync,
            statusWatcher: client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        client.PublishConnected();
        DelayRequest retry = await delays.ReadAsync(cancellation.Token);
        Assert.Equal(TimeSpan.FromSeconds(1), retry.Interval);
        retry.Release();

        _ = await client.ReadRequestAsync(cancellation.Token);
        Assert.Equal(2, factory.CreateCount);

        await CancelAndAwaitAsync(cancellation, running);
        Assert.True(replacement.Disposed);
    }

    [Fact]
    public async Task ServiceFailureRetainsSampler()
    {
        var delays = new ControlledDelay();
        var sampler = new ConstantSampler(Frame(1));
        var factory = new SequenceSamplerFactory(() => sampler);
        var client = new WatchClient();
        client.EnqueueResult(new(
            ControlOperationStatus.Failed,
            "Transport failed."));
        await using var loop = new ServiceAmbientLightingLoop(
            client,
            factory,
            delay: delays.DelayAsync,
            statusWatcher: client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        client.PublishConnected();
        _ = await client.ReadRequestAsync(cancellation.Token);
        DelayRequest retry = await delays.ReadAsync(cancellation.Token);
        Assert.Equal(TimeSpan.FromSeconds(1), retry.Interval);
        retry.Release();

        _ = await client.ReadRequestAsync(cancellation.Token);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(2, sampler.CaptureCount);

        await CancelAndAwaitAsync(cancellation, running);
        Assert.True(sampler.Disposed);
    }

    [Fact]
    public async Task DisposeWaitsForConcurrentRendererShutdown()
    {
        var sampler = new ConstantSampler(Frame(1));
        var client = new WatchClient();
        client.BlockNextRequest();
        var loop = new ServiceAmbientLightingLoop(
            client,
            new SequenceSamplerFactory(() => sampler),
            statusWatcher: client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        client.PublishConnected();
        await client.RequestStarted.Task.WaitAsync(cancellation.Token);
        client.PublishReconnecting();
        await client.RequestCancellationObserved.Task.WaitAsync(cancellation.Token);

        Task disposal = loop.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);

        client.ReleaseBlockedRequest();
        await disposal.WaitAsync(cancellation.Token);
        await CancelAndAwaitAsync(cancellation, running);
        Assert.True(sampler.Disposed);
    }

    [Fact]
    public async Task RefreshRateStatusChangeRestartsRendererAtSixtyHzCadence()
    {
        var delays = new ControlledDelay();
        var first = new ConstantSampler(Frame(1));
        var replacement = new ConstantSampler(Frame(2));
        var factory = new SequenceSamplerFactory(() => first, () => replacement);
        var client = new WatchClient();
        await using var loop = new ServiceAmbientLightingLoop(
            client,
            factory,
            delay: delays.DelayAsync,
            statusWatcher: client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        client.PublishConnected(LightingRefreshRate.ThirtyHz);
        _ = await client.ReadRequestAsync(cancellation.Token);
        DelayRequest thirtyHz = await delays.ReadAsync(cancellation.Token);
        Assert.Equal(
            TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30),
            thirtyHz.Interval);

        client.PublishConnected(LightingRefreshRate.SixtyHz);
        AmbientLightingFrame sixtyHzFrame = await client.ReadRequestAsync(
            cancellation.Token);
        DelayRequest sixtyHz = await delays.ReadAsync(cancellation.Token);

        Assert.Equal(Frame(2).Slots, sixtyHzFrame.Slots);
        Assert.Equal(
            TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60),
            sixtyHz.Interval);
        Assert.True(first.Disposed);
        Assert.Equal(2, factory.CreateCount);

        await CancelAndAwaitAsync(cancellation, running);
        Assert.True(replacement.Disposed);
    }

    [Fact]
    public async Task UniformCapability_SendsAveragedTypedColorInsteadOfSpatialFrame()
    {
        var client = new WatchClient();
        await using var loop = new ServiceAmbientLightingLoop(
            client,
            new SequenceSamplerFactory(() => new ConstantSampler(Frame(7))),
            statusWatcher: client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        client.PublishConnected(uniformFrames: true);
        UniformLightingFrame frame = await client.ReadUniformRequestAsync(cancellation.Token);

        Assert.Equal(new RgbColor(7, 0, 0), frame.Color);
        await CancelAndAwaitAsync(cancellation, running);
    }

    private static async Task CancelAndAwaitAsync(
        CancellationTokenSource cancellation,
        Task running)
    {
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await running);
    }

    private static AmbientLightingFrame Frame(byte red)
    {
        var slots = new RgbColor[AmbientLightingFrame.SlotCount];
        for (int slot = 0; slot < slots.Length; slot++)
        {
            if (!AmbientLightingFrame.IsHole(slot))
            {
                slots[slot] = new(red, 0, 0);
            }
        }
        return new(slots);
    }

    private sealed class WatchClient : OpenBlade.Testing.UnavailableServiceClient
    {
        private readonly Channel<ServiceWatchUpdate> updates =
            Channel.CreateUnbounded<ServiceWatchUpdate>();
        private readonly Channel<AmbientLightingFrame> requests =
            Channel.CreateUnbounded<AmbientLightingFrame>();
        private readonly Channel<UniformLightingFrame> uniformRequests =
            Channel.CreateUnbounded<UniformLightingFrame>();
        private readonly ConcurrentQueue<ControlOperationResult> results = new();
        private TaskCompletionSource? blockedRequestRelease;
        private int blockNextRequest;

        internal TaskCompletionSource RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource RequestCancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void PublishConnected(
            LightingRefreshRate refreshRate = LightingRefreshRate.ThirtyHz,
            bool uniformFrames = false) =>
            updates.Writer.TryWrite(new ServiceWatchUpdate.Connected(
                Status(refreshRate, uniformFrames)));

        internal void PublishReconnecting() =>
            updates.Writer.TryWrite(new ServiceWatchUpdate.Reconnecting());

        internal void BlockNextRequest()
        {
            blockedRequestRelease = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref blockNextRequest, 1);
        }

        internal void ReleaseBlockedRequest() =>
            blockedRequestRelease?.TrySetResult();

        internal void EnqueueResult(ControlOperationResult result) =>
            results.Enqueue(result);

        internal ValueTask<AmbientLightingFrame> ReadRequestAsync(
            CancellationToken cancellationToken) =>
            requests.Reader.ReadAsync(cancellationToken);

        internal ValueTask<UniformLightingFrame> ReadUniformRequestAsync(
            CancellationToken cancellationToken) =>
            uniformRequests.Reader.ReadAsync(cancellationToken);

        public override async IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken token)
        {
            await foreach (ServiceWatchUpdate update in updates.Reader.ReadAllAsync(token))
            {
                yield return update;
            }
        }

        public override async Task<ControlOperationResult> PushAmbientLightingFrameAsync(
            AmbientLightingFrame frame,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            requests.Writer.TryWrite(frame);
            if (Interlocked.Exchange(ref blockNextRequest, 0) != 0)
            {
                RequestStarted.TrySetResult();
                using CancellationTokenRegistration registration = token.Register(
                    () => RequestCancellationObserved.TrySetResult());
                await blockedRequestRelease!.Task.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
            return results.TryDequeue(out ControlOperationResult? result)
                ? result
                : new ControlOperationResult(ControlOperationStatus.Applied, "Applied");
        }

        public override Task<ControlOperationResult> PushUniformLightingFrameAsync(
            UniformLightingFrame frame,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            uniformRequests.Writer.TryWrite(frame);
            return Task.FromResult(new ControlOperationResult(
                ControlOperationStatus.Applied,
                "Applied"));
        }
    }

    private sealed class SequenceSamplerFactory(
        params Func<IAmbientScreenSampler>[] create)
        : IAmbientScreenSamplerFactory
    {
        private readonly ConcurrentQueue<Func<IAmbientScreenSampler>> remaining =
            new(create);
        private int createCount;

        internal int CreateCount => Volatile.Read(ref createCount);

        public IAmbientScreenSampler Create()
        {
            Interlocked.Increment(ref createCount);
            return remaining.TryDequeue(out Func<IAmbientScreenSampler>? next)
                ? next()
                : throw new InvalidOperationException("No sampler was configured.");
        }
    }

    private sealed class ConstantSampler(AmbientLightingFrame frame)
        : IAmbientScreenSampler
    {
        private int captureCount;
        private int disposed;

        internal int CaptureCount => Volatile.Read(ref captureCount);

        internal bool Disposed => Volatile.Read(ref disposed) != 0;

        public AmbientLightingFrame Capture(AmbientAwarenessRegion region)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            Interlocked.Increment(ref captureCount);
            return frame;
        }

        public void Dispose() => Volatile.Write(ref disposed, 1);
    }

    private sealed class ThrowingSampler(Exception failure) : IAmbientScreenSampler
    {
        private int disposed;

        internal bool Disposed => Volatile.Read(ref disposed) != 0;

        public AmbientLightingFrame Capture(AmbientAwarenessRegion region) => throw failure;

        public void Dispose() => Volatile.Write(ref disposed, 1);
    }

    private sealed class ControlledDelay
    {
        private readonly Channel<DelayRequest> requests =
            Channel.CreateUnbounded<DelayRequest>();

        internal Task DelayAsync(TimeSpan interval, CancellationToken cancellationToken)
        {
            var request = new DelayRequest(interval);
            requests.Writer.TryWrite(request);
            return request.Completion.WaitAsync(cancellationToken);
        }

        internal ValueTask<DelayRequest> ReadAsync(CancellationToken cancellationToken) =>
            requests.Reader.ReadAsync(cancellationToken);
    }

    private sealed class DelayRequest(TimeSpan interval)
    {
        private readonly TaskCompletionSource<bool> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TimeSpan Interval { get; } = interval;

        internal Task Completion => completion.Task;

        internal void Release() => completion.TrySetResult(true);
    }

    private static ServiceStatusSnapshot Status(
        LightingRefreshRate refreshRate = LightingRefreshRate.ThirtyHz,
        bool uniformFrames = false)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        LightingSettings lighting = LightingSettingsFactory.AmbientAwareness(
            AmbientAwarenessRegion.Full,
            brightness: 50);
        return new ServiceStatusSnapshot(
            1,
            DeviceVerified: true,
            WritesBlocked: false,
            [],
            null,
            [],
            "Ready",
            DeviceCapabilities.Unavailable() with
            {
                SupportsUniformLightingFrames = uniformFrames,
            },
            null,
            profiles,
            new DesiredDeviceState(
                new ResolvedSetting<PerformancePreset>(
                    PerformancePreset.Balanced,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<FanMode>(
                    FanMode.Automatic,
                    SettingValueSource.PowerProfile),
                new ResolvedSetting<LightingSettings>(
                    lighting,
                    SettingValueSource.PowerProfile),
                new DesiredBatteryState(
                    profiles.BatteryPolicy,
                    protectionEnabled: true,
                    temporaryFullCharge: false)),
            PowerSource.PluggedIn,
            null,
            null,
            null,
            null,
            FullChargeOverride: false,
            LightingRendererOwnership:
                LightingRendererOwnershipState.AmbientAwareness,
            LightingRefreshRate: refreshRate);
    }
}
