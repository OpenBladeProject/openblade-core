using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Protocol.Tests;

public sealed class CoolingPadLightingLifecycleCoordinatorTests
{
    [Fact]
    public void Constructor_RejectsCurrentIncompleteEvidence()
    {
        Assert.Throws<UnvalidatedCommandException>(() =>
            new CoolingPadLightingLifecycleCoordinator(
            new CoolingPadLightingAdmission(
                CoolingPadLightingEvidenceGates.CurrentObservedWholeStrip,
                CoolingPadMirrorMapAvailability.Resolved),
            WholeStripCoolingPadKeyboardMirrorMapper.Instance,
            new RecordingSink()));
    }

    [Fact]
    public async Task Frames_CoalesceToNewestWhileOneSubmissionIsInFlight()
    {
        var sink = new RecordingSink(blockFirstFrame: true);
        await using CoolingPadLightingLifecycleCoordinator coordinator = Create(sink);
        await coordinator.SetConnectedAsync(true, default);
        await coordinator.SetEnabledAsync(true, default);

        Assert.True(coordinator.Snapshot.Enabled);
        Assert.Equal(DeviceFeatureAccess.ReadWrite, coordinator.Snapshot.Access);
        Assert.Equal(CoolingPadLightingSyncState.WaitingForFrame, coordinator.State);

        coordinator.TryPublish(KeyboardFrame(new RgbColor(10, 0, 0)));
        await sink.FirstFrameStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.TryPublish(KeyboardFrame(new RgbColor(20, 0, 0)));
        coordinator.TryPublish(KeyboardFrame(new RgbColor(30, 0, 0)));
        sink.ReleaseFirstFrame();

        await WaitUntilAsync(() => sink.FrameCount == 2);
        Assert.Equal(
            [new RgbColor(10, 0, 0), new RgbColor(30, 0, 0)],
            sink.FrameSnapshot().Select(frame => frame[0]).ToArray());
        Assert.Equal(CoolingPadLightingSyncState.Mirroring, coordinator.State);
    }

    [Fact]
    public async Task Suppression_StopsFramesThenRestoresBrightnessBeforeReplay()
    {
        var sink = new RecordingSink();
        await using CoolingPadLightingLifecycleCoordinator coordinator = Create(sink);
        await coordinator.SetConnectedAsync(true, default);
        await coordinator.SetEnabledAsync(true, default);
        coordinator.TryPublish(KeyboardFrame(new RgbColor(12, 34, 56)));
        await WaitUntilAsync(() => sink.EventCount == 1);
        coordinator.TryPublishBrightness(50);
        await WaitUntilAsync(() => sink.EventCount == 2);

        await coordinator.SetSuppressedAsync(true, default);
        coordinator.TryPublishBrightness(0);
        coordinator.TryPublish(KeyboardFrame(new RgbColor(90, 80, 70)));
        coordinator.TryPublishBrightness(60);
        await coordinator.SetSuppressedAsync(false, default);

        await WaitUntilAsync(() => sink.EventCount == 5);
        Assert.Equal(
            [
                "frame:0C2238",
                "brightness:50",
                "brightness:0",
                "brightness:60",
                "frame:0C2238",
            ],
            sink.EventSnapshot());
        Assert.True(coordinator.IsInterested);
        Assert.Equal(CoolingPadLightingSyncState.Mirroring, coordinator.State);
    }

    [Fact]
    public async Task IndeterminateFrameFailure_FaultsWithoutRetry()
    {
        var sink = new RecordingSink
        {
            NextFrameFailure = CoolingPadLightingSinkFailure.Indeterminate,
        };
        await using CoolingPadLightingLifecycleCoordinator coordinator = Create(sink);
        await coordinator.SetConnectedAsync(true, default);
        await coordinator.SetEnabledAsync(true, default);

        coordinator.TryPublish(KeyboardFrame(new RgbColor(1, 2, 3)));
        await WaitUntilAsync(() => coordinator.State == CoolingPadLightingSyncState.Faulted);
        coordinator.TryPublish(KeyboardFrame(new RgbColor(4, 5, 6)));
        await Task.Delay(20);

        Assert.False(coordinator.IsInterested);
        Assert.Single(sink.FrameSnapshot());
    }

    [Fact]
    public async Task PumpFault_PublishesSpecificSnapshotAndIsolatesObservers()
    {
        var sink = new RecordingSink
        {
            NextFrameFailure = CoolingPadLightingSinkFailure.Indeterminate,
        };
        await using CoolingPadLightingLifecycleCoordinator coordinator = Create(sink);
        var faulted = new TaskCompletionSource<CoolingPadLightingCoordinatorSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.SnapshotChanged += _ => throw new InvalidOperationException("observer");
        coordinator.SnapshotChanged += snapshot =>
        {
            if (snapshot.State == CoolingPadLightingSyncState.Faulted)
            {
                faulted.TrySetResult(snapshot);
            }
        };
        await coordinator.SetConnectedAsync(true, default);
        await coordinator.SetEnabledAsync(true, default);

        coordinator.TryPublish(KeyboardFrame(new RgbColor(1, 2, 3)));

        CoolingPadLightingCoordinatorSnapshot snapshot =
            await faulted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(snapshot.Enabled);
        Assert.Equal(CoolingPadLightingSyncState.Faulted, snapshot.State);
        Assert.Equal("FrameWrite:Indeterminate", snapshot.Reason);
        Assert.False(coordinator.IsInterested);
        Assert.Single(sink.FrameSnapshot());
    }

    [Fact]
    public async Task InFlightIndeterminateFrame_PreventsFollowUpBlackout()
    {
        var sink = new RecordingSink(
            blockFirstFrame: true,
            ignoreFirstFrameCancellation: true)
        {
            NextFrameFailure = CoolingPadLightingSinkFailure.Indeterminate,
        };
        await using CoolingPadLightingLifecycleCoordinator coordinator = Create(sink);
        await coordinator.SetConnectedAsync(true, default);
        await coordinator.SetEnabledAsync(true, default);
        coordinator.TryPublish(KeyboardFrame(new RgbColor(1, 2, 3)));
        await sink.FirstFrameStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task suppress = coordinator.SetSuppressedAsync(true, default);
        sink.ReleaseFirstFrame();
        await suppress;

        Assert.Equal(CoolingPadLightingSyncState.Faulted, coordinator.State);
        Assert.Equal(["frame:010203"], sink.EventSnapshot());
    }

    [Fact]
    public async Task ActiveBrightnessUpdates_CoalesceToLatestWithoutBlockingPublisher()
    {
        var sink = new RecordingSink(blockFirstBrightness: true);
        await using CoolingPadLightingLifecycleCoordinator coordinator = Create(sink);
        await coordinator.SetConnectedAsync(true, default);
        await coordinator.SetEnabledAsync(true, default);

        coordinator.TryPublishBrightness(10);
        await sink.FirstBrightnessStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.TryPublishBrightness(20);
        coordinator.TryPublishBrightness(30);
        sink.ReleaseFirstBrightness();

        await WaitUntilAsync(() => sink.EventCount == 2);
        Assert.Equal(["brightness:10", "brightness:30"], sink.EventSnapshot());
    }

    [Fact]
    public async Task RepeatedBrightnessSeed_DoesNotDuplicateInFlightValue()
    {
        var sink = new RecordingSink(blockFirstBrightness: true);
        await using CoolingPadLightingLifecycleCoordinator coordinator = Create(sink);
        await coordinator.SetConnectedAsync(true, default);
        await coordinator.SetEnabledAsync(true, default);

        coordinator.TryPublishBrightness(10);
        await sink.FirstBrightnessStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        coordinator.TryPublishBrightness(10);
        coordinator.TryPublishBrightness(10);
        sink.ReleaseFirstBrightness();

        await Task.Delay(25);
        Assert.Equal(["brightness:10"], sink.EventSnapshot());
    }

    [Fact]
    public async Task DisconnectedBrightnessObservation_SeedsReconnectWithoutFrameInterest()
    {
        var sink = new RecordingSink();
        await using CoolingPadLightingLifecycleCoordinator coordinator = Create(sink);
        await coordinator.SetEnabledAsync(true, default);

        Assert.False(coordinator.IsInterested);
        Assert.True(coordinator.IsBrightnessInterested);
        coordinator.TryPublishBrightness(40);
        Assert.Empty(sink.EventSnapshot());

        await coordinator.SetConnectedAsync(true, default);

        await WaitUntilAsync(() => sink.EventCount == 1);
        Assert.Equal(["brightness:40"], sink.EventSnapshot());
    }

    [Fact]
    public async Task Disconnect_StopsInterestAndWaitsWithoutWriting()
    {
        var sink = new RecordingSink();
        await using CoolingPadLightingLifecycleCoordinator coordinator = Create(sink);
        await coordinator.SetConnectedAsync(true, default);
        await coordinator.SetEnabledAsync(true, default);
        Assert.True(coordinator.IsInterested);

        await coordinator.SetConnectedAsync(false, default);
        coordinator.TryPublish(KeyboardFrame(new RgbColor(1, 2, 3)));
        await Task.Delay(20);

        Assert.False(coordinator.IsInterested);
        Assert.Equal(CoolingPadLightingSyncState.WaitingForPad, coordinator.State);
        Assert.Empty(sink.EventSnapshot());
    }

    [Fact]
    public async Task Dispose_RestoresCapturedBaselineBrightnessOnce()
    {
        var sink = new RecordingSink
        {
            QueryBrightnessRaw = 0x40,
        };
        CoolingPadLightingLifecycleCoordinator coordinator = Create(sink);
        await coordinator.SetConnectedAsync(true, default);
        await coordinator.SetEnabledAsync(true, default);
        coordinator.TryPublish(KeyboardFrame(new RgbColor(1, 2, 3)));
        await WaitUntilAsync(() => sink.FrameCount == 1);

        await coordinator.DisposeAsync();
        await coordinator.DisposeAsync();

        Assert.Equal(["frame:010203", "brightness-raw:64"], sink.EventSnapshot());
        Assert.Equal(CoolingPadLightingSyncState.Unavailable, coordinator.State);
    }

    [Fact]
    public async Task DisconnectAfterSubmittedFrame_FaultsWithoutReplay()
    {
        var sink = new RecordingSink();
        await using CoolingPadLightingLifecycleCoordinator coordinator = Create(sink);
        await coordinator.SetConnectedAsync(true, default);
        await coordinator.SetEnabledAsync(true, default);
        coordinator.TryPublish(KeyboardFrame(new RgbColor(1, 2, 3)));
        await WaitUntilAsync(() => sink.FrameCount == 1);

        await coordinator.SetConnectedAsync(false, default);
        await coordinator.SetConnectedAsync(true, default);

        await Task.Delay(25);
        Assert.Equal(CoolingPadLightingSyncState.Faulted, coordinator.State);
        Assert.Equal("DisconnectAfterWrite:Indeterminate", coordinator.Reason);
        Assert.False(coordinator.IsInterested);
        Assert.Equal(1, sink.FrameCount);
    }

    [Fact]
    public async Task IndeterminateFault_RemainsStickyAcrossLifecycleChanges()
    {
        var sink = new RecordingSink
        {
            NextFrameFailure = CoolingPadLightingSinkFailure.Indeterminate,
        };
        await using CoolingPadLightingLifecycleCoordinator coordinator = Create(sink);
        await coordinator.SetConnectedAsync(true, default);
        await coordinator.SetEnabledAsync(true, default);
        coordinator.TryPublish(KeyboardFrame(new RgbColor(1, 2, 3)));
        await WaitUntilAsync(() => coordinator.State == CoolingPadLightingSyncState.Faulted);

        await coordinator.SetConnectedAsync(false, default);
        await coordinator.SetSuppressedAsync(true, default);
        await coordinator.SetEnabledAsync(false, default);
        await coordinator.SetConnectedAsync(true, default);

        Assert.Equal(CoolingPadLightingSyncState.Faulted, coordinator.State);
        Assert.False(coordinator.IsInterested);
        Assert.Equal(["frame:010203"], sink.EventSnapshot());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.SetEnabledAsync(true, default));
        Assert.Equal(CoolingPadLightingSyncState.Faulted, coordinator.State);

        string[] beforeAcknowledgement = sink.EventSnapshot();
        await coordinator.AcknowledgeRecoveryAsync(default);

        Assert.Equal(beforeAcknowledgement, sink.EventSnapshot());
        Assert.Equal(CoolingPadLightingSyncState.Disabled, coordinator.State);
        Assert.Equal("Admitted", coordinator.Reason);
    }

    [Fact]
    public async Task DisableAfterFault_ReportsContractValidDisabledState()
    {
        var sink = new RecordingSink
        {
            NextFrameFailure = CoolingPadLightingSinkFailure.Indeterminate,
        };
        await using CoolingPadLightingLifecycleCoordinator coordinator = Create(sink);
        await coordinator.SetConnectedAsync(true, default);
        await coordinator.SetEnabledAsync(true, default);
        coordinator.TryPublish(KeyboardFrame(new RgbColor(1, 2, 3)));
        await WaitUntilAsync(() => coordinator.State == CoolingPadLightingSyncState.Faulted);

        await coordinator.SetEnabledAsync(false, default);

        Assert.False(coordinator.Snapshot.Enabled);
        Assert.Equal(CoolingPadLightingSyncState.Faulted, coordinator.State);
        Assert.False(coordinator.IsBrightnessInterested);

        await coordinator.SetConnectedAsync(false, default);
        await coordinator.SetConnectedAsync(true, default);
        await coordinator.InvalidateAsync(default);
        Assert.Equal(CoolingPadLightingSyncState.Faulted, coordinator.State);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.SetEnabledAsync(true, default));
        Assert.Equal(CoolingPadLightingSyncState.Faulted, coordinator.State);
    }

    [Fact]
    public async Task Invalidate_ClearsRetainedFrameAndRequiresFreshConnectionFrame()
    {
        var sink = new RecordingSink();
        await using CoolingPadLightingLifecycleCoordinator coordinator = Create(sink);
        await coordinator.SetConnectedAsync(true, default);
        await coordinator.SetEnabledAsync(true, default);
        coordinator.TryPublish(KeyboardFrame(new RgbColor(1, 2, 3)));
        await WaitUntilAsync(() => sink.FrameCount == 1);

        await coordinator.InvalidateAsync(default);
        Assert.Equal(["frame:010203", "brightness-raw:255"], sink.EventSnapshot());
        await coordinator.SetConnectedAsync(true, default);

        await Task.Delay(25);
        Assert.Equal(1, sink.FrameCount);
        Assert.True(coordinator.IsInterested);
        Assert.Equal(CoolingPadLightingSyncState.WaitingForFrame, coordinator.State);

        coordinator.TryPublish(KeyboardFrame(new RgbColor(4, 5, 6)));
        await WaitUntilAsync(() => sink.FrameCount == 2);
        Assert.Equal(new RgbColor(4, 5, 6), sink.FrameSnapshot()[1][0]);
    }

    [Fact]
    public async Task Suppression_WhileDisabledOrDisconnected_PerformsNoIo()
    {
        var sink = new RecordingSink();
        await using CoolingPadLightingLifecycleCoordinator coordinator = Create(sink);

        await coordinator.SetSuppressedAsync(true, default);
        await coordinator.SetEnabledAsync(true, default);
        await coordinator.SetSuppressedAsync(true, default);

        Assert.Equal(CoolingPadLightingSyncState.WaitingForPad, coordinator.State);
        Assert.Empty(sink.EventSnapshot());
    }

    [Fact]
    public async Task Suppression_RestoresExactRawBaselineBeforeBrightnessSeed()
    {
        var sink = new RecordingSink
        {
            QueryBrightnessRaw = 0x80,
        };
        await using CoolingPadLightingLifecycleCoordinator coordinator = Create(sink);
        await coordinator.SetConnectedAsync(true, default);
        await coordinator.SetEnabledAsync(true, default);

        await coordinator.SetSuppressedAsync(true, default);
        await coordinator.SetSuppressedAsync(false, default);

        Assert.Equal(["brightness:0", "brightness-raw:128"], sink.EventSnapshot());
    }

    [Fact]
    public async Task RepeatedSuppression_BlackensOnce()
    {
        var sink = new RecordingSink();
        await using CoolingPadLightingLifecycleCoordinator coordinator = Create(sink);
        await coordinator.SetConnectedAsync(true, default);
        await coordinator.SetEnabledAsync(true, default);

        await coordinator.SetSuppressedAsync(true, default);
        await coordinator.SetSuppressedAsync(true, default);

        Assert.Equal(["brightness:0"], sink.EventSnapshot());
    }

    [Fact]
    public async Task Disable_RestoresCapturedBaselineBrightnessOnce()
    {
        var sink = new RecordingSink
        {
            QueryBrightnessRaw = 0x80,
        };
        await using CoolingPadLightingLifecycleCoordinator coordinator = Create(sink);
        await coordinator.SetConnectedAsync(true, default);
        await coordinator.SetEnabledAsync(true, default);
        coordinator.TryPublish(KeyboardFrame(new RgbColor(1, 2, 3)));
        await WaitUntilAsync(() => sink.FrameCount == 1);

        await coordinator.SetEnabledAsync(false, default);

        Assert.Equal(1, sink.QueryBrightnessCount);
        Assert.Equal(["frame:010203", "brightness-raw:128"], sink.EventSnapshot());
        Assert.Equal(CoolingPadLightingSyncState.Disabled, coordinator.State);
    }

    [Fact]
    public async Task BaselineQueryFailure_FaultsBeforeInterestOrWrite()
    {
        var sink = new RecordingSink
        {
            QueryBrightnessFailure = HidSessionFailureKind.Timeout,
        };
        await using CoolingPadLightingLifecycleCoordinator coordinator = Create(sink);
        await coordinator.SetConnectedAsync(true, default);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.SetEnabledAsync(true, default));

        Assert.Equal(1, sink.QueryBrightnessCount);
        Assert.Equal(CoolingPadLightingSyncState.Faulted, coordinator.State);
        Assert.False(coordinator.IsInterested);
        Assert.Empty(sink.EventSnapshot());

        await coordinator.SetEnabledAsync(false, default);
        sink.QueryBrightnessFailure = null;
        await coordinator.SetEnabledAsync(true, default);

        Assert.Equal(2, sink.QueryBrightnessCount);
        Assert.Equal(CoolingPadLightingSyncState.WaitingForFrame, coordinator.State);
        Assert.True(coordinator.IsInterested);
    }

    [Fact]
    public async Task BaselineRestoreFailure_FaultsAndNeverRetries()
    {
        var sink = new RecordingSink();
        await using CoolingPadLightingLifecycleCoordinator coordinator = Create(sink);
        await coordinator.SetConnectedAsync(true, default);
        await coordinator.SetEnabledAsync(true, default);
        coordinator.TryPublish(KeyboardFrame(new RgbColor(1, 2, 3)));
        await WaitUntilAsync(() => sink.FrameCount == 1);
        sink.NextBrightnessFailure = CoolingPadLightingSinkFailure.Indeterminate;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.SetEnabledAsync(false, default));
        await coordinator.SetEnabledAsync(false, default);

        Assert.Equal(["frame:010203", "brightness-raw:255"], sink.EventSnapshot());
        Assert.False(coordinator.IsInterested);
        Assert.Equal(CoolingPadLightingSyncState.Faulted, coordinator.State);
        Assert.False(coordinator.Snapshot.Enabled);
        Assert.Contains("BaselineRestore:Indeterminate", coordinator.Reason);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.SetEnabledAsync(true, default));
    }

    [Fact]
    public async Task FramePump_EnforcesConfiguredMinimumCadence()
    {
        var sink = new RecordingSink();
        var timeProvider = new ManualTimeProvider();
        var delays = new List<TimeSpan>();
        await using CoolingPadLightingLifecycleCoordinator coordinator = Create(
            sink,
            timeProvider,
            TimeSpan.FromMilliseconds(50),
            (duration, cancellationToken) =>
            {
                delays.Add(duration);
                timeProvider.Advance(duration);
                return Task.CompletedTask;
            });
        await coordinator.SetConnectedAsync(true, default);
        await coordinator.SetEnabledAsync(true, default);

        coordinator.TryPublish(KeyboardFrame(new RgbColor(1, 0, 0)));
        await WaitUntilAsync(() => sink.FrameCount == 1);
        coordinator.TryPublish(KeyboardFrame(new RgbColor(2, 0, 0)));
        await WaitUntilAsync(() => sink.FrameCount == 2);

        Assert.Equal([TimeSpan.FromMilliseconds(50)], delays);
    }

    private static CoolingPadLightingLifecycleCoordinator Create(
        ICoolingPadLightingSink sink,
        TimeProvider? timeProvider = null,
        TimeSpan? minimumFrameInterval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null) => new(
        new CoolingPadLightingAdmission(
            CoolingPadLightingEvidenceGates.UniformForTest(admitted: true),
            CoolingPadMirrorMapAvailability.Resolved),
        WholeStripCoolingPadKeyboardMirrorMapper.Instance,
        sink,
        timeProvider,
        minimumFrameInterval,
        delay);

    private static AmbientLightingFrame KeyboardFrame(RgbColor color)
    {
        RgbColor[] slots = new RgbColor[AmbientLightingFrame.SlotCount];
        for (int index = 0; index < slots.Length; index++)
        {
            if (!AmbientLightingFrame.IsHole(index))
            {
                slots[index] = color;
            }
        }
        return new AmbientLightingFrame(slots);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(2);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("The lighting coordinator did not converge.");
            }
            await Task.Delay(5);
        }
    }

    private sealed class RecordingSink(
        bool blockFirstFrame = false,
        bool blockFirstBrightness = false,
        bool ignoreFirstFrameCancellation = false)
        : ICoolingPadLightingSink
    {
        private readonly object sync = new();
        private readonly TaskCompletionSource releaseFirstFrame =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int frameCalls;
        private int brightnessCalls;

        internal int QueryBrightnessCount { get; private set; }

        internal byte QueryBrightnessRaw { get; init; } = 0xFF;

        internal HidSessionFailureKind? QueryBrightnessFailure { get; set; }

        internal TaskCompletionSource FirstFrameStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource FirstBrightnessStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource ReleaseBrightness { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private List<CoolingPadLightingFrame> Frames { get; } = [];

        private List<string> Events { get; } = [];

        internal int FrameCount
        {
            get
            {
                lock (sync)
                {
                    return Frames.Count;
                }
            }
        }

        internal int EventCount
        {
            get
            {
                lock (sync)
                {
                    return Events.Count;
                }
            }
        }

        internal CoolingPadLightingFrame[] FrameSnapshot()
        {
            lock (sync)
            {
                return [.. Frames];
            }
        }

        internal string[] EventSnapshot()
        {
            lock (sync)
            {
                return [.. Events];
            }
        }

        internal CoolingPadLightingSinkFailure NextFrameFailure { get; set; }

        internal CoolingPadLightingSinkFailure NextBrightnessFailure { get; set; }

        internal void ReleaseFirstFrame() => releaseFirstFrame.TrySetResult();

        internal void ReleaseFirstBrightness() => ReleaseBrightness.TrySetResult();

        public Task<HidSessionResult<CoolingPadBrightnessReading>>
            QueryBrightnessAsync(CancellationToken cancellationToken)
        {
            QueryBrightnessCount++;
            return Task.FromResult(QueryBrightnessFailure is { } failure
                ? new HidSessionResult<CoolingPadBrightnessReading>(default, failure)
                : new HidSessionResult<CoolingPadBrightnessReading>(
                    new CoolingPadBrightnessReading(QueryBrightnessRaw),
                    Failure: null));
        }

        public async Task<CoolingPadLightingSinkResult> SubmitFrameAsync(
            CoolingPadLightingFrame frame,
            CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref frameCalls);
            lock (sync)
            {
                Frames.Add(frame);
                RgbColor color = frame[0];
                Events.Add($"frame:{color.Red:X2}{color.Green:X2}{color.Blue:X2}");
            }
            FirstFrameStarted.TrySetResult();
            if (blockFirstFrame && call == 1)
            {
                if (ignoreFirstFrameCancellation)
                {
                    await releaseFirstFrame.Task;
                }
                else
                {
                    await releaseFirstFrame.Task.WaitAsync(cancellationToken);
                }
            }

            CoolingPadLightingSinkFailure failure = NextFrameFailure;
            NextFrameFailure = CoolingPadLightingSinkFailure.None;
            return new(failure);
        }

        public async Task<CoolingPadLightingSinkResult> SetBrightnessAsync(
            byte percent,
            CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref brightnessCalls);
            lock (sync)
            {
                Events.Add($"brightness:{percent}");
            }
            FirstBrightnessStarted.TrySetResult();
            if (blockFirstBrightness && call == 1)
            {
                await ReleaseBrightness.Task.WaitAsync(cancellationToken);
            }

            CoolingPadLightingSinkFailure failure = NextBrightnessFailure;
            NextBrightnessFailure = CoolingPadLightingSinkFailure.None;
            return new CoolingPadLightingSinkResult(failure);
        }

        public Task<CoolingPadLightingSinkResult> SetBrightnessRawAsync(
            byte raw,
            CancellationToken cancellationToken)
        {
            lock (sync)
            {
                Events.Add($"brightness-raw:{raw}");
            }
            CoolingPadLightingSinkFailure failure = NextBrightnessFailure;
            NextBrightnessFailure = CoolingPadLightingSinkFailure.None;
            return Task.FromResult(new CoolingPadLightingSinkResult(failure));
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long timestamp = 1;

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() => timestamp;

        internal void Advance(TimeSpan duration) => timestamp += checked(
            (long)(duration.TotalSeconds * TimestampFrequency));
    }
}
