using OpenBlade.Session;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SessionAudioRequestDispatcherTests
{
    [Fact]
    public async Task Discover_PerformsAdmissionWithoutStartingNativeWorker()
    {
        var worker = new RecordingWorker(State());
        var dispatcher = new SessionAudioRequestDispatcher(Available(), worker);

        SessionAudioResponse response = await dispatcher.DispatchAsync(
            new DiscoverThxAudioRequest(SessionAudioIpc.ProtocolVersion, Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(ThxAudioOperationStatus.Succeeded, response.Status);
        Assert.Equal(ThxAudioAvailability.Available, response.Availability);
        Assert.Null(response.State);
        Assert.Empty(worker.Requests);
    }

    [Fact]
    public async Task Discover_ReportsActiveControllerWithoutStartingNativeWorker()
    {
        var worker = new RecordingWorker(State());
        var dispatcher = new SessionAudioRequestDispatcher(
            new FixedAdmission(new ThxAudioAdmission(
                ThxAudioAvailability.ControllerActive,
                "Razer Synapse is active.")),
            worker);

        SessionAudioResponse response = await dispatcher.DispatchAsync(
            new DiscoverThxAudioRequest(SessionAudioIpc.ProtocolVersion, Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(ThxAudioOperationStatus.Unavailable, response.Status);
        Assert.Equal(ThxAudioAvailability.ControllerActive, response.Availability);
        Assert.Empty(worker.Requests);
    }

    [Fact]
    public async Task Read_PropagatesHelperFreshControllerConflict()
    {
        var worker = new RecordingWorker(State())
        {
            Next = new ThxAudioWorkerResult(
                ThxAudioOperationStatus.Unavailable,
                ThxAudioAvailability.ControllerActive,
                "Razer Synapse is active.",
                null),
        };
        var dispatcher = new SessionAudioRequestDispatcher(Available(), worker);

        SessionAudioResponse response = await dispatcher.DispatchAsync(
            new ReadThxAudioRequest(
                SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(),
                ThxAudioEndpoint.Speakers),
            CancellationToken.None);

        Assert.Equal(ThxAudioOperationStatus.Unavailable, response.Status);
        Assert.Equal(ThxAudioAvailability.ControllerActive, response.Availability);
        Assert.Null(response.State);
    }

    [Fact]
    public async Task Mutation_RequiresExplicitReadAndVendorReturnedPreset()
    {
        var worker = new RecordingWorker(State());
        var dispatcher = new SessionAudioRequestDispatcher(Available(), worker);

        SessionAudioResponse beforeRead = await dispatcher.DispatchAsync(
            new SetThxSpatialRequest(
                SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(),
                ThxAudioEndpoint.Speakers,
                true),
            CancellationToken.None);
        Assert.Equal(ThxAudioOperationStatus.Unavailable, beforeRead.Status);
        Assert.Empty(worker.Requests);

        await ReadAsync(dispatcher);
        SessionAudioResponse unknownPreset = await dispatcher.DispatchAsync(
            new SelectThxPresetRequest(
                SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(),
                ThxAudioEndpoint.Speakers,
                "not-returned"),
            CancellationToken.None);

        Assert.Equal(ThxAudioOperationStatus.Failed, unknownPreset.Status);
        Assert.Single(worker.Requests);
    }

    [Fact]
    public async Task Mutation_ReturnsOnlyConfirmedReadback()
    {
        var worker = new RecordingWorker(State())
        {
            Next = new ThxAudioWorkerResult(
                ThxAudioOperationStatus.Succeeded,
                ThxAudioAvailability.Available,
                null,
                State(spatial: true)),
        };
        var dispatcher = new SessionAudioRequestDispatcher(Available(), worker);
        await ReadAsync(dispatcher);

        SessionAudioResponse response = await dispatcher.DispatchAsync(
            new SetThxSpatialRequest(
                SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(),
                ThxAudioEndpoint.Speakers,
                true),
            CancellationToken.None);

        Assert.Equal(ThxAudioOperationStatus.Succeeded, response.Status);
        Assert.True(response.State!.SpatialEnabled);
        Assert.IsType<ThxHelperSetPropertyRequest>(worker.Requests[1]);
    }

    [Fact]
    public async Task WorkerExceptionAfterPossibleMutation_PausesUntilSuccessfulRead()
    {
        var worker = new RecordingWorker(State());
        var dispatcher = new SessionAudioRequestDispatcher(Available(), worker);
        await ReadAsync(dispatcher);
        worker.Exception = new IOException("helper pipe ended");

        SessionAudioResponse uncertain = await dispatcher.DispatchAsync(
            new SetThxEqualizerRequest(
                SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(),
                ThxAudioEndpoint.Speakers,
                true),
            CancellationToken.None);

        Assert.Equal(ThxAudioOperationStatus.Indeterminate, uncertain.Status);
        Assert.Null(uncertain.State);
        worker.Exception = null;
        int callsAfterFailure = worker.Requests.Count;
        SessionAudioResponse blocked = await dispatcher.DispatchAsync(
            new SetThxEqualizerRequest(
                SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(),
                ThxAudioEndpoint.Speakers,
                true),
            CancellationToken.None);
        Assert.Equal(ThxAudioOperationStatus.Unavailable, blocked.Status);
        Assert.Equal(callsAfterFailure, worker.Requests.Count);

        await ReadAsync(dispatcher);
        worker.Next = new ThxAudioWorkerResult(
            ThxAudioOperationStatus.Succeeded,
            ThxAudioAvailability.Available,
            null,
            State(equalizer: true));
        SessionAudioResponse recovered = await dispatcher.DispatchAsync(
            new SetThxEqualizerRequest(
                SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(),
                ThxAudioEndpoint.Speakers,
                true),
            CancellationToken.None);
        Assert.Equal(ThxAudioOperationStatus.Succeeded, recovered.Status);
    }

    [Fact]
    public async Task Requests_AreSerializedAcrossReadAndMutation()
    {
        var worker = new BlockingWorker();
        var dispatcher = new SessionAudioRequestDispatcher(Available(), worker);
        Task<SessionAudioResponse> first = dispatcher.DispatchAsync(
            new ReadThxAudioRequest(
                SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(),
                ThxAudioEndpoint.Speakers),
            CancellationToken.None);
        await worker.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<SessionAudioResponse> second = dispatcher.DispatchAsync(
            new ReadThxAudioRequest(
                SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(),
                ThxAudioEndpoint.Headphones),
            CancellationToken.None);

        Assert.Equal(1, worker.Started);
        worker.Release.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, worker.MaximumConcurrent);
    }

    private static async Task ReadAsync(SessionAudioRequestDispatcher dispatcher)
    {
        SessionAudioResponse response = await dispatcher.DispatchAsync(
            new ReadThxAudioRequest(
                SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(),
                ThxAudioEndpoint.Speakers),
            CancellationToken.None);
        Assert.Equal(ThxAudioOperationStatus.Succeeded, response.Status);
    }

    private static IThxAudioAdmissionSource Available() => new FixedAdmission(
        new ThxAudioAdmission(ThxAudioAvailability.Available, null));

    private static ThxAudioState State(bool spatial = false, bool equalizer = false) => new(
        ThxAudioEndpoint.Speakers,
        spatial,
        equalizer,
        "music",
        [new ThxAudioPreset("music", "music"), new ThxAudioPreset("game", "game")]);

    private sealed class FixedAdmission(ThxAudioAdmission value) : IThxAudioAdmissionSource
    {
        public ThxAudioAdmission Check() => value;
    }

    private sealed class RecordingWorker(ThxAudioState state) : IThxAudioWorker
    {
        public List<ThxAudioHelperRequest> Requests { get; } = [];
        public ThxAudioWorkerResult? Next { get; set; }
        public Exception? Exception { get; set; }

        public Task<ThxAudioWorkerResult> ExecuteAsync(
            ThxAudioHelperRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (Exception is not null)
            {
                throw Exception;
            }
            return Task.FromResult(Next ?? new ThxAudioWorkerResult(
                ThxAudioOperationStatus.Succeeded,
                ThxAudioAvailability.Available,
                null,
                state));
        }
    }

    private sealed class BlockingWorker : IThxAudioWorker
    {
        private int concurrent;
        public int Started { get; private set; }
        public int MaximumConcurrent { get; private set; }
        public TaskCompletionSource FirstStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ThxAudioWorkerResult> ExecuteAsync(
            ThxAudioHelperRequest request,
            CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref concurrent);
            Started++;
            MaximumConcurrent = Math.Max(MaximumConcurrent, active);
            FirstStarted.TrySetResult();
            if (Started == 1)
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            Interlocked.Decrement(ref concurrent);
            return new ThxAudioWorkerResult(
                ThxAudioOperationStatus.Succeeded,
                ThxAudioAvailability.Available,
                null,
                State() with { Endpoint = request.Endpoint });
        }
    }
}
