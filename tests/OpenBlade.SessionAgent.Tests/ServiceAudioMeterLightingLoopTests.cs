using System.Collections.Concurrent;
using System.Threading.Channels;
using OpenBlade.Contracts;
using OpenBlade.SessionAgent;
using OpenBlade.Tray;

namespace OpenBlade.SessionAgent.Tests;

public sealed class ServiceAudioMeterLightingLoopTests
{
    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(0.5, 1, 128)]
    [InlineData(0.5, 2, 255)]
    public void UniformAudioSample_UsesScalarPeakWithoutKeyboardGeometry(
        float peak,
        double colorBoost,
        byte expectedIntensity)
    {
        UniformLightingFrame frame = WindowsAudioMeterLightingSampler.RenderUniformFrame(
            peak,
            colorBoost);

        Assert.Equal(new RgbColor(0, expectedIntensity, 0), frame.Color);
    }

    [Fact]
    public void SamplerConstruction_EndpointFailureReleasesEnumerator()
    {
        var nativeApi = new FakeAudioMeterNativeApi
        {
            EndpointFailure = new InvalidOperationException("Endpoint failed."),
        };

        _ = Assert.Throws<InvalidOperationException>(() =>
            new WindowsAudioMeterLightingSampler(nativeApi));

        Assert.Equal([nativeApi.Enumerator], nativeApi.Released);
    }

    [Fact]
    public void SamplerConstruction_ActivationFailureReleasesEndpointThenEnumerator()
    {
        var nativeApi = new FakeAudioMeterNativeApi
        {
            ActivationFailure = new InvalidOperationException("Activation failed."),
        };

        _ = Assert.Throws<InvalidOperationException>(() =>
            new WindowsAudioMeterLightingSampler(nativeApi));

        Assert.Equal(
            [nativeApi.Device, nativeApi.Enumerator],
            nativeApi.Released);
    }

    [Fact]
    public void SamplerConstruction_MeterCastFailureReleasesAllInReverseOrder()
    {
        var nativeApi = new FakeAudioMeterNativeApi
        {
            ValidationFailure = new InvalidCastException("Meter cast failed."),
        };

        _ = Assert.Throws<InvalidCastException>(() =>
            new WindowsAudioMeterLightingSampler(nativeApi));

        Assert.Equal(
            [nativeApi.Meter, nativeApi.Device, nativeApi.Enumerator],
            nativeApi.Released);
    }

    [Fact]
    public void SamplerConstruction_SuccessTransfersOwnershipUntilDispose()
    {
        var nativeApi = new FakeAudioMeterNativeApi();
        var sampler = new WindowsAudioMeterLightingSampler(nativeApi);

        Assert.Empty(nativeApi.Released);

        sampler.Dispose();
        sampler.Dispose();

        Assert.Equal(
            [nativeApi.Meter, nativeApi.Device, nativeApi.Enumerator],
            nativeApi.Released);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultOutputChange_RebindsEvenWhenPreviousMeterRemainsReadable(bool uniform)
    {
        var native = new SwitchingAudioMeterNativeApi();
        var changes = new DefaultOutputChanges();
        using var sampler = new WindowsAudioMeterLightingSampler(native, changes.Subscribe);
        AssertPeak(0);

        native.DefaultEndpoint = 1;
        changes.Publish();

        AssertPeak(1);
        AssertPeak(1);
        Assert.Equal(2, native.EndpointReads);
        Assert.Equal([native.Endpoints[0].Meter, native.Endpoints[0].Device], native.Released);

        void AssertPeak(float expected)
        {
            if (uniform)
            {
                Assert.Equal(WindowsAudioMeterLightingSampler.RenderUniformFrame(expected, 1),
                    sampler.CaptureUniform(1));
            }
            else
            {
                Assert.Equal(CountLitColumns(WindowsAudioMeterLightingSampler.RenderFrame(expected, 1)),
                    CountLitColumns(sampler.Capture(1)));
            }
        }
    }

    [Fact]
    public void DefaultOutputChanges_CoalesceWithoutEnumeratingOnEveryFrame()
    {
        var native = new SwitchingAudioMeterNativeApi();
        var changes = new DefaultOutputChanges();
        var sampler = new WindowsAudioMeterLightingSampler(native, changes.Subscribe);
        _ = sampler.CaptureUniform(1);
        Assert.Equal(1, native.EndpointReads); // Subscribe publishes its initial notification.

        native.DefaultEndpoint = 1;
        changes.Publish();
        changes.Publish();
        changes.Publish();
        _ = sampler.CaptureUniform(1);
        _ = sampler.Capture(1);
        Assert.Equal(2, native.EndpointReads);

        sampler.Dispose();
        sampler.Dispose();
        changes.Publish();
        Assert.Equal(1, changes.DisposeCount);
        Assert.Equal([native.Endpoints[0].Meter, native.Endpoints[0].Device,
            native.Endpoints[1].Meter, native.Endpoints[1].Device, native.Enumerator], native.Released);
        Assert.Throws<ObjectDisposedException>(() => sampler.CaptureUniform(1));
    }

    [Fact]
    public void DefaultOutputChange_DuringBindingRebindsOnNextCaptureWithoutFailureBackoff()
    {
        var native = new SwitchingAudioMeterNativeApi();
        var changes = new DefaultOutputChanges();
        using var sampler = new WindowsAudioMeterLightingSampler(native, changes.Subscribe);
        native.DefaultEndpoint = 1;
        changes.Publish();
        native.ValidatingMeter = () =>
        {
            native.ValidatingMeter = null;
            native.DefaultEndpoint = 2;
            changes.Publish();
        };

        Assert.Equal(WindowsAudioMeterLightingSampler.RenderUniformFrame(1, 1), sampler.CaptureUniform(1));
        Assert.Equal(WindowsAudioMeterLightingSampler.RenderUniformFrame(0.5f, 1), sampler.CaptureUniform(1));
        Assert.Equal([native.Endpoints[1].Meter, native.Endpoints[2].Meter], native.MetersRead);
        Assert.Equal(3, native.EndpointReads);
        Assert.Equal([native.Endpoints[0].Meter, native.Endpoints[0].Device,
            native.Endpoints[1].Meter, native.Endpoints[1].Device], native.Released);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultOutputChange_DuringSamplingRebindsOnNextCaptureWithoutFailureBackoff(bool uniform)
    {
        var native = new SwitchingAudioMeterNativeApi();
        var changes = new DefaultOutputChanges();
        using var sampler = new WindowsAudioMeterLightingSampler(native, changes.Subscribe);
        native.ReadingMeter = () =>
        {
            native.ReadingMeter = null;
            native.DefaultEndpoint = 1;
            changes.Publish();
        };

        if (uniform)
        {
            Assert.Equal(WindowsAudioMeterLightingSampler.RenderUniformFrame(0, 1), sampler.CaptureUniform(1));
            Assert.Equal(WindowsAudioMeterLightingSampler.RenderUniformFrame(1, 1), sampler.CaptureUniform(1));
        }
        else
        {
            Assert.Equal(0, CountLitColumns(sampler.Capture(1)));
            Assert.Equal(AmbientLightingFrame.Columns, CountLitColumns(sampler.Capture(1)));
        }
        Assert.Equal(2, native.EndpointReads);
        Assert.Equal([native.Endpoints[0].Meter, native.Endpoints[1].Meter], native.MetersRead);
        Assert.Equal([native.Endpoints[0].Meter, native.Endpoints[0].Device], native.Released);
        Assert.Equal(0, changes.DisposeCount);
    }

    [Fact]
    public void UnrelatedEndpointEventBurstDoesNotInvalidateOrDisposeTheAudioSampler()
    {
        var native = new SwitchingAudioMeterNativeApi();
        var changes = new DefaultOutputChanges();
        WindowsThxActiveOutput.Notification? notification = null;
        using var sampler = new WindowsAudioMeterLightingSampler(native, callback =>
        {
            notification = new(callback, defaultMultimediaOnly: true);
            return changes.Subscribe(callback);
        });
        native.ReadingMeter = () =>
        {
            for (int index = 0; index < 100; index++)
            {
                _ = notification!.OnDeviceAdded("capture");
                _ = notification.OnDeviceRemoved("unrelated-render");
                _ = notification.OnDeviceStateChanged("capture", 1);
                _ = notification.OnPropertyValueChanged("unrelated-render", default);
                _ = notification.OnDefaultDeviceChanged(CoreAudioDataFlow.Capture, CoreAudioRole.Multimedia, "capture");
                _ = notification.OnDefaultDeviceChanged(CoreAudioDataFlow.Render, CoreAudioRole.Console, "console");
            }
        };

        _ = sampler.CaptureUniform(1);
        _ = sampler.Capture(1);

        Assert.Equal(1, native.EndpointReads);
        Assert.Empty(native.Released);
        Assert.Equal(0, changes.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidColorBoostIsRejectedBeforeNativeSampling(bool uniform)
    {
        var native = new SwitchingAudioMeterNativeApi();
        using var sampler = new WindowsAudioMeterLightingSampler(native);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            if (uniform) { _ = sampler.CaptureUniform(1.1); }
            else { _ = sampler.Capture(1.1); }
        });

        Assert.Empty(native.MetersRead);
    }

    [Fact]
    public void DefaultOutputChange_FailedBindingReleasesPartialResourcesAndCanRetry()
    {
        var native = new SwitchingAudioMeterNativeApi();
        var changes = new DefaultOutputChanges();
        using var sampler = new WindowsAudioMeterLightingSampler(native, changes.Subscribe);
        native.DefaultEndpoint = 1;
        changes.Publish();
        native.ActivationFailure = new InvalidOperationException("Endpoint activation failed.");

        Assert.Throws<InvalidOperationException>(() => sampler.CaptureUniform(1));
        Assert.Empty(native.MetersRead);
        Assert.Equal([native.Endpoints[1].Device], native.Released);

        native.ActivationFailure = null;
        Assert.Equal(WindowsAudioMeterLightingSampler.RenderUniformFrame(1, 1), sampler.CaptureUniform(1));
        Assert.Equal(3, native.EndpointReads);
    }

    [Fact]
    public void InitialBindingFailureUnsubscribesAndReleasesNativeResources()
    {
        var native = new SwitchingAudioMeterNativeApi
        {
            ActivationFailure = new InvalidOperationException("Endpoint activation failed."),
        };
        var changes = new DefaultOutputChanges();

        Assert.Throws<InvalidOperationException>(() =>
            new WindowsAudioMeterLightingSampler(native, changes.Subscribe));

        Assert.Equal(1, changes.DisposeCount);
        Assert.Equal([native.Endpoints[0].Device, native.Enumerator], native.Released);
    }

    [Fact]
    public async Task SamplerDisposalWaitsForInProgressCaptureBeforeReleasingNativeResources()
    {
        var native = new SwitchingAudioMeterNativeApi();
        var changes = new DefaultOutputChanges();
        var sampler = new WindowsAudioMeterLightingSampler(native, changes.Subscribe);
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        native.ReadingMeter = () =>
        {
            reading.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            Assert.Empty(native.Released);
        };
        Task capture = Task.Run(() => sampler.CaptureUniform(1));
        Task? disposal = null;
        try
        {
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(5));
            disposal = Task.Run(() =>
            {
                disposing.TrySetResult();
                sampler.Dispose();
            });
            await disposing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(disposal.IsCompleted);
        }
        finally
        {
            release.Set();
            await capture.WaitAsync(TimeSpan.FromSeconds(5));
            if (disposal is not null) { await disposal.WaitAsync(TimeSpan.FromSeconds(5)); }
            sampler.Dispose();
        }
        Assert.Equal(1, changes.DisposeCount);
        Assert.Equal([native.Endpoints[0].Meter, native.Endpoints[0].Device, native.Enumerator], native.Released);
    }

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
    public void Decay_AttackRemainsImmediate()
    {
        var smoother = new AudioMeterDecaySmoother();
        _ = smoother.Apply(FrameWithColumns(4), TimeSpan.Zero, 1000);

        AmbientLightingFrame attacked = smoother.Apply(
            FrameWithColumns(15),
            TimeSpan.FromMilliseconds(100),
            1000);

        Assert.Equal(15, CountLitColumns(attacked));
    }

    [Fact]
    public void Decay_SmoothsOnlyFallingFrames()
    {
        var smoother = new AudioMeterDecaySmoother();
        _ = smoother.Apply(FrameWithColumns(17), TimeSpan.Zero, 1000);

        AmbientLightingFrame decayed = smoother.Apply(
            FrameWithColumns(4),
            TimeSpan.FromMilliseconds(100),
            1000);

        Assert.Equal(16, CountLitColumns(decayed));
    }

    [Fact]
    public void Decay_SilenceFadesToBlackAndClearsState()
    {
        var smoother = new AudioMeterDecaySmoother();
        _ = smoother.Apply(FrameWithColumns(17), TimeSpan.Zero, 1000);

        AmbientLightingFrame fading = smoother.Apply(
            FrameWithColumns(0),
            TimeSpan.FromMilliseconds(100),
            1000);
        AmbientLightingFrame silent = smoother.Apply(
            FrameWithColumns(0),
            TimeSpan.FromMilliseconds(1000),
            1000);
        AmbientLightingFrame resumed = smoother.Apply(
            FrameWithColumns(4),
            TimeSpan.FromMilliseconds(100),
            1000);

        Assert.Equal(16, CountLitColumns(fading));
        Assert.Equal(0, CountLitColumns(silent));
        Assert.Equal(4, CountLitColumns(resumed));
    }

    [Fact]
    public void Decay_OffClearsSilenceImmediately()
    {
        var smoother = new AudioMeterDecaySmoother();
        _ = smoother.Apply(FrameWithColumns(17), TimeSpan.Zero, 1000);

        AmbientLightingFrame silent = smoother.Apply(
            FrameWithColumns(0),
            TimeSpan.FromMilliseconds(100),
            0);

        Assert.Equal(0, CountLitColumns(silent));
    }

    [Fact]
    public void Decay_ResetDoesNotRetainPriorPeak()
    {
        var smoother = new AudioMeterDecaySmoother();
        _ = smoother.Apply(FrameWithColumns(17), TimeSpan.Zero, 1000);

        smoother.Reset();
        AmbientLightingFrame resumed = smoother.Apply(
            FrameWithColumns(4),
            TimeSpan.FromMilliseconds(100),
            1000);

        Assert.Equal(4, CountLitColumns(resumed));
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
    public async Task SourceReconnectStartsDecayFromTheNewLevel()
    {
        var time = new ManualTimeProvider();
        var delays = new ControlledDelay();
        var first = new ConstantSampler(FrameWithColumns(17));
        var replacement = new ConstantSampler(FrameWithColumns(4));
        var factory = new SequenceSamplerFactory(
            () => first,
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

        client.PublishConnected(decayMilliseconds: 1000);
        Assert.Equal(17, CountLitColumns(
            await client.ReadRequestAsync(cancellation.Token)));
        _ = await delays.ReadAsync(cancellation.Token);
        client.PublishReconnecting();
        client.PublishConnected(decayMilliseconds: 1000);

        Assert.Equal(4, CountLitColumns(
            await client.ReadRequestAsync(cancellation.Token)));
        Assert.True(first.Disposed);

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

    [Fact]
    public async Task DisposeWaitsForConcurrentRendererShutdown()
    {
        var sampler = new ConstantSampler(Frame(1));
        var client = new WatchClient();
        client.BlockNextRequest();
        var loop = new ServiceAudioMeterLightingLoop(
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
        await using var loop = new ServiceAudioMeterLightingLoop(
            client,
            factory,
            delay: delays.DelayAsync,
            statusWatcher: client);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task running = loop.RunAsync(cancellation.Token);

        client.PublishConnected(refreshRate: LightingRefreshRate.ThirtyHz);
        _ = await client.ReadRequestAsync(cancellation.Token);
        DelayRequest thirtyHz = await delays.ReadAsync(cancellation.Token);
        Assert.Equal(
            TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30),
            thirtyHz.Interval);

        client.PublishConnected(refreshRate: LightingRefreshRate.SixtyHz);
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
    public async Task UniformCapability_SendsTypedAudioColorWithoutSpatialFrame()
    {
        var client = new WatchClient();
        await using var loop = new ServiceAudioMeterLightingLoop(
            client,
            new SequenceSamplerFactory(() => new ConstantSampler(
                Frame(99),
                new UniformLightingFrame(new RgbColor(7, 0, 0)))),
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

    private static AmbientLightingFrame FrameWithColumns(int columns) =>
        WindowsAudioMeterLightingSampler.RenderColumns(columns);

    private static int CountLitColumns(AmbientLightingFrame frame)
    {
        int litColumns = 0;
        for (int column = 0; column < AmbientLightingFrame.Columns; column++)
        {
            bool lit = Enumerable.Range(0, AmbientLightingFrame.Rows).Any(row =>
            {
                int slot = (row * AmbientLightingFrame.Columns) + column;
                return !AmbientLightingFrame.IsHole(slot) && frame.Slots[slot] != default;
            });
            if (lit)
            {
                litColumns = column + 1;
            }
        }
        return litColumns;
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
            int decayMilliseconds = 0,
            LightingRefreshRate refreshRate = LightingRefreshRate.ThirtyHz,
            bool uniformFrames = false) =>
            updates.Writer.TryWrite(new ServiceWatchUpdate.Connected(
                Status(decayMilliseconds, refreshRate, uniformFrames)));

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

    private sealed class DefaultOutputChanges
    {
        private Action? changed;
        internal int DisposeCount { get; private set; }

        internal IDisposable Subscribe(Action callback)
        {
            changed = callback;
            callback();
            return new Subscription(this);
        }

        internal void Publish() => changed?.Invoke();

        private sealed class Subscription(DefaultOutputChanges owner) : IDisposable
        {
            public void Dispose()
            {
                owner.DisposeCount++;
                owner.changed = null;
            }
        }
    }

    private sealed class SwitchingAudioMeterNativeApi : IAudioMeterNativeApi
    {
        internal sealed record Endpoint(object Device, object Meter, float Peak);
        internal object Enumerator { get; } = new();
        internal Endpoint[] Endpoints { get; } =
            [new(new(), new(), 0), new(new(), new(), 1), new(new(), new(), 0.5f)];
        internal int DefaultEndpoint { get; set; }
        internal int EndpointReads { get; private set; }
        internal List<object> Released { get; } = [];
        internal List<object> MetersRead { get; } = [];
        internal Exception? ActivationFailure { get; set; }
        internal Action? ValidatingMeter { get; set; }
        internal Action? ReadingMeter { get; set; }

        public object CreateEnumerator() => Enumerator;

        public object GetDefaultAudioEndpoint(object enumeratorObject)
        {
            Assert.Same(Enumerator, enumeratorObject);
            EndpointReads++;
            return Endpoints[DefaultEndpoint].Device;
        }

        public object ActivateMeter(object deviceObject) => ActivationFailure is null
            ? Endpoints.Single(endpoint => ReferenceEquals(endpoint.Device, deviceObject)).Meter
            : throw ActivationFailure;

        public void ValidateMeter(object meterObject)
        {
            Assert.Contains(Endpoints, endpoint => ReferenceEquals(endpoint.Meter, meterObject));
            ValidatingMeter?.Invoke();
        }

        public float GetPeakValue(object meterObject)
        {
            ReadingMeter?.Invoke();
            MetersRead.Add(meterObject);
            return Endpoints.Single(endpoint => ReferenceEquals(endpoint.Meter, meterObject)).Peak;
        }

        public void Release(object value) => Released.Add(value);
    }

    private sealed class FakeAudioMeterNativeApi : IAudioMeterNativeApi
    {
        internal object Enumerator { get; } = new();

        internal object Device { get; } = new();

        internal object Meter { get; } = new();

        internal Exception? EndpointFailure { get; init; }

        internal Exception? ActivationFailure { get; init; }

        internal Exception? ValidationFailure { get; init; }

        internal List<object> Released { get; } = [];

        public object CreateEnumerator() => Enumerator;

        public object GetDefaultAudioEndpoint(object enumeratorObject)
        {
            Assert.Same(Enumerator, enumeratorObject);
            return EndpointFailure is null
                ? Device
                : throw EndpointFailure;
        }

        public object ActivateMeter(object deviceObject)
        {
            Assert.Same(Device, deviceObject);
            return ActivationFailure is null
                ? Meter
                : throw ActivationFailure;
        }

        public void ValidateMeter(object meterObject)
        {
            Assert.Same(Meter, meterObject);
            if (ValidationFailure is not null)
            {
                throw ValidationFailure;
            }
        }

        public float GetPeakValue(object meterObject)
        {
            Assert.Same(Meter, meterObject);
            return 0;
        }

        public void Release(object value) => Released.Add(value);
    }

    private sealed class ConstantSampler(
        AmbientLightingFrame frame,
        UniformLightingFrame? uniformFrame = null)
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

        public UniformLightingFrame CaptureUniform(double colorBoost)
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            Interlocked.Increment(ref captureCount);
            return uniformFrame
                ?? throw new InvalidOperationException("No uniform sample was configured.");
        }

        public void Dispose() => Volatile.Write(ref disposed, 1);
    }

    private sealed class ThrowingSampler(Exception failure) : IAudioMeterLightingSampler
    {
        private int disposed;

        internal bool Disposed => Volatile.Read(ref disposed) != 0;

        public AmbientLightingFrame Capture(double colorBoost) => throw failure;

        public UniformLightingFrame CaptureUniform(double colorBoost) => throw failure;

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

    private static ServiceStatusSnapshot Status(
        int decayMilliseconds = 0,
        LightingRefreshRate refreshRate = LightingRefreshRate.ThirtyHz,
        bool uniformFrames = false)
    {
        ProfileSet profiles = ProfileDefaults.CreateSafeDefaults();
        LightingSettings lighting = LightingSettingsFactory.AudioMeter(
            colorBoost: 1,
            brightness: 50,
            decayMilliseconds);
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
                LightingRendererOwnershipState.AudioMeter,
            LightingRefreshRate: refreshRate);
    }
}
