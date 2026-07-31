using System.Collections.Concurrent;
using System.Threading.Channels;
using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ServiceAudioMeterLightingLoopTests
{
    [Fact]
    public void RenderFrame_MapsPeakAcrossColumnsAndPreservesHoles()
    {
        AmbientLightingFrame frame =
            WindowsAudioMeterLightingSampler.RenderFrame(0.5f, colorBoost: 1);

        Assert.True(frame.IsWellFormed());
        Assert.NotEqual(default, frame.Slots[1]);
        Assert.NotEqual(default, frame.Slots[8]);
        Assert.Equal(default, frame.Slots[9]);
        foreach (int hole in AmbientLightingFrame.LayoutHoleSlots)
        {
            Assert.Equal(default, frame.Slots[hole]);
        }
    }

    [Fact]
    public void RenderFrame_ColorBoostIncreasesLitColumns()
    {
        AmbientLightingFrame normal =
            WindowsAudioMeterLightingSampler.RenderFrame(0.1f, colorBoost: 1);
        AmbientLightingFrame boosted =
            WindowsAudioMeterLightingSampler.RenderFrame(0.1f, colorBoost: 1.25);

        Assert.True(
            boosted.Slots.Count(color => color != default)
            > normal.Slots.Count(color => color != default));
    }

    [Theory]
    [InlineData(-0.1f, 1)]
    [InlineData(1.1f, 1)]
    [InlineData(0.5f, 0)]
    [InlineData(0.5f, 4.25)]
    [InlineData(0.5f, 1.1)]
    public void RenderFrame_RejectsInvalidInputs(float peak, double boost)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WindowsAudioMeterLightingSampler.RenderFrame(peak, boost));
    }

    [Fact]
    public async Task ConstantFrameIsSuppressedUntilKeepalive()
    {
        var time = new ManualTimeProvider();
        var delays = new ControlledDelay();
        var sampler = new ConstantSampler(Frame(1));
        var factory = new SequenceSamplerFactory(() => sampler);
        var client = new WatchClient();
        await using var loop = new ServiceAudioMeterLightingLoop(
            client,
            factory,
            delay: delays.DelayAsync,
            statusWatcher: client,
            timeProvider: time);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        client.PublishConnected();
        _ = await client.ReadRequestAsync(cancellation.Token);
        DelayRequest firstDelay = await delays.ReadAsync(cancellation.Token);
        firstDelay.Release();

        DelayRequest secondDelay = await delays.ReadAsync(cancellation.Token);
        Assert.False(client.TryReadRequest(out _));
        time.Advance(SessionLightingFrameCadence.KeepaliveInterval);
        secondDelay.Release();

        _ = await client.ReadRequestAsync(cancellation.Token);
        Assert.Equal(3, sampler.CaptureCount);

        await CancelAndAwaitAsync(cancellation, running);
        Assert.True(sampler.Disposed);
    }

    [Fact]
    public async Task CaptureFailureDisposesAndReplacesSampler()
    {
        var time = new ManualTimeProvider();
        var delays = new ControlledDelay();
        var failed = new ThrowingSampler(new IOException("Capture failed."));
        var replacement = new ConstantSampler(Frame(1));
        var factory = new SequenceSamplerFactory(
            () => failed,
            () => replacement);
        var client = new WatchClient();
        var failures = new ConcurrentQueue<Exception>();
        await using var loop = new ServiceAudioMeterLightingLoop(
            client,
            factory,
            failures.Enqueue,
            delays.DelayAsync,
            client,
            time);
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
        var time = new ManualTimeProvider();
        var delays = new ControlledDelay();
        var replacement = new ConstantSampler(Frame(1));
        var factory = new SequenceSamplerFactory(
            () => throw new IOException("Creation failed."),
            () => replacement);
        var client = new WatchClient();
        await using var loop = new ServiceAudioMeterLightingLoop(
            client,
            factory,
            delay: delays.DelayAsync,
            statusWatcher: client,
            timeProvider: time);
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
        var time = new ManualTimeProvider();
        var delays = new ControlledDelay();
        var sampler = new ConstantSampler(Frame(1));
        var factory = new SequenceSamplerFactory(() => sampler);
        var client = new WatchClient();
        client.EnqueueResult(new(
            ControlOperationStatus.Failed,
            "Transport failed."));
        await using var loop = new ServiceAudioMeterLightingLoop(
            client,
            factory,
            delay: delays.DelayAsync,
            statusWatcher: client,
            timeProvider: time);
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
        private readonly ConcurrentQueue<ControlOperationResult> results = new();

        internal void PublishConnected() =>
            updates.Writer.TryWrite(new ServiceWatchUpdate.Connected(Status()));

        internal void EnqueueResult(ControlOperationResult result) =>
            results.Enqueue(result);

        internal ValueTask<AmbientLightingFrame> ReadRequestAsync(
            CancellationToken cancellationToken) =>
            requests.Reader.ReadAsync(cancellationToken);

        internal bool TryReadRequest(out AmbientLightingFrame? frame) =>
            requests.Reader.TryRead(out frame);

        public override async IAsyncEnumerable<ServiceWatchUpdate> WatchStatusAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken token)
        {
            await foreach (ServiceWatchUpdate update in updates.Reader.ReadAllAsync(token))
            {
                yield return update;
            }
        }

        public override Task<ControlOperationResult> PushAmbientLightingFrameAsync(
            AmbientLightingFrame frame,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            requests.Writer.TryWrite(frame);
            return Task.FromResult(results.TryDequeue(out ControlOperationResult? result)
                ? result
                : new ControlOperationResult(ControlOperationStatus.Applied, "Applied"));
        }
    }

    private sealed class SequenceSamplerFactory(
        params Func<IAudioMeterLightingSampler>[] create)
        : IAudioMeterLightingSamplerFactory
    {
        private readonly ConcurrentQueue<Func<IAudioMeterLightingSampler>> remaining =
            new(create);
        private int createCount;

        internal int CreateCount => Volatile.Read(ref createCount);

        public IAudioMeterLightingSampler Create()
        {
            Interlocked.Increment(ref createCount);
            return remaining.TryDequeue(out Func<IAudioMeterLightingSampler>? next)
                ? next()
                : throw new InvalidOperationException("No sampler was configured.");
        }
    }

    private sealed class ConstantSampler(AmbientLightingFrame frame)
        : IAudioMeterLightingSampler
    {
        private int captureCount;
        private int disposed;

        internal int CaptureCount => Volatile.Read(ref captureCount);

        internal bool Disposed => Volatile.Read(ref disposed) != 0;

        public AmbientLightingFrame Capture(double colorBoost)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            Interlocked.Increment(ref captureCount);
            return frame;
        }

        public void Dispose() => Volatile.Write(ref disposed, 1);
    }

    private sealed class ThrowingSampler(Exception failure) : IAudioMeterLightingSampler
    {
        private int disposed;

        internal bool Disposed => Volatile.Read(ref disposed) != 0;

        public AmbientLightingFrame Capture(double colorBoost) => throw failure;

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

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => now;

        internal void Advance(TimeSpan elapsed) => now += elapsed;
    }

    private static ServiceStatusSnapshot Status()
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        LightingSettings lighting = LightingSettingsFactory.AudioMeter(
            colorBoost: 1,
            brightness: 50);
        return new ServiceStatusSnapshot(
            1,
            DeviceVerified: true,
            WritesBlocked: false,
            [],
            null,
            [],
            "Ready",
            DeviceCapabilities.Unavailable(),
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
                LightingRendererOwnershipState.AudioMeter);
    }
}
