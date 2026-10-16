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
                "not-returned",
                [0, 0, 0, 0, 0, 0, 0]),
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
    public async Task CustomEqualizer_UsesTypedWorkerRequestAndConfirmedBands()
    {
        double[] gains = [-3, -2, -1, 0, 1, 2, 3];
        ThxAudioState confirmed = State() with
        {
            SelectedPresetId = "custom",
            SelectedNativeProfileId = "custom",
            EqualizerBands = SpeakerBands(gains),
        };
        var worker = new RecordingWorker(State())
        {
            Next = new ThxAudioWorkerResult(
                ThxAudioOperationStatus.Succeeded,
                ThxAudioAvailability.Available,
                null,
                confirmed),
        };
        var dispatcher = new SessionAudioRequestDispatcher(Available(), worker);
        await ReadAsync(dispatcher);

        SessionAudioResponse response = await dispatcher.DispatchAsync(
            new SelectThxPresetRequest(
                SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(),
                ThxAudioEndpoint.Speakers,
                "custom",
                gains),
            CancellationToken.None);

        Assert.Equal(ThxAudioOperationStatus.Succeeded, response.Status);
        ThxHelperSelectPresetRequest request = Assert.IsType<
            ThxHelperSelectPresetRequest>(worker.Requests[1]);
        Assert.Equal("custom", request.PresetId);
        Assert.Equal(gains, request.GainsDb);
    }

    [Fact]
    public async Task ApplicationProfile_RequiresLatestCatalogAndUsesSemanticRequest()
    {
        ThxAudioState applied = State(spatial: true) with
        {
            SelectedPresetId = null,
            SelectedNativeProfileId = "ApexLegends_cmm",
        };
        var worker = new RecordingWorker(State())
        {
            Next = new ThxAudioWorkerResult(
                ThxAudioOperationStatus.Succeeded,
                ThxAudioAvailability.Available,
                null,
                applied),
        };
        var dispatcher = new SessionAudioRequestDispatcher(Available(), worker);
        await ReadAsync(dispatcher);

        SessionAudioResponse response = await dispatcher.DispatchAsync(
            new ApplyThxApplicationProfileRequest(
                SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(),
                ThxAudioEndpoint.Speakers,
                "r5apex.exe",
                ThxAudioApplicationMode.Competitive),
            CancellationToken.None);

        Assert.Equal(ThxAudioOperationStatus.Succeeded, response.Status);
        ThxHelperApplyApplicationProfileRequest request = Assert.IsType<
            ThxHelperApplyApplicationProfileRequest>(worker.Requests[1]);
        Assert.Equal("r5apex.exe", request.ExecutableName);
        Assert.Equal(ThxAudioApplicationMode.Competitive, request.Mode);
    }

    [Fact]
    public async Task Enhancement_UsesTypedWorkerRequestAndConfirmedReadback()
    {
        var worker = new RecordingWorker(State())
        {
            Next = new ThxAudioWorkerResult(
                ThxAudioOperationStatus.Succeeded,
                ThxAudioAvailability.Available,
                null,
                State() with
                {
                    SoundNormalization = new ThxAudioEnhancement(true, 60),
                }),
        };
        var dispatcher = new SessionAudioRequestDispatcher(Available(), worker);
        await ReadAsync(dispatcher);

        SessionAudioResponse response = await dispatcher.DispatchAsync(
            new SetThxEnhancementRequest(
                SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(),
                ThxAudioEndpoint.Speakers,
                ThxAudioEnhancementKind.SoundNormalization,
                true,
                60),
            CancellationToken.None);

        Assert.Equal(ThxAudioOperationStatus.Succeeded, response.Status);
        Assert.IsType<ThxHelperSetEnhancementRequest>(worker.Requests[1]);
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

    [Fact]
    public async Task AutomationRead_DoesNotClearIndeterminatePause()
    {
        var worker = new RecordingWorker(State());
        var dispatcher = new SessionAudioRequestDispatcher(Available(), worker);
        int explicitReads = 0;
        dispatcher.ExplicitReadSucceeded += _ =>
        {
            explicitReads++;
            return Task.CompletedTask;
        };
        await ReadAsync(dispatcher);
        Assert.Equal(1, explicitReads);

        worker.Exception = new IOException("helper pipe ended");
        SessionAudioResponse uncertain = await dispatcher.DispatchAsync(
            new SetThxSpatialRequest(
                SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(),
                ThxAudioEndpoint.Speakers,
                true),
            CancellationToken.None);
        Assert.Equal(ThxAudioOperationStatus.Indeterminate, uncertain.Status);

        worker.Exception = null;
        int callsAfterFailure = worker.Requests.Count;
        SessionAudioResponse blockedRead = await dispatcher.DispatchAutomationAsync(
            new ReadThxAudioRequest(
                SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(),
                ThxAudioEndpoint.Speakers),
            CancellationToken.None);
        Assert.Equal(ThxAudioOperationStatus.Unavailable, blockedRead.Status);
        Assert.Equal(callsAfterFailure, worker.Requests.Count);
        Assert.Equal(1, explicitReads);

        await ReadAsync(dispatcher);
        Assert.Equal(2, explicitReads);
    }

    [Fact]
    public async Task PublicDispatch_NotifiesAutomationBeforeManualOperation()
    {
        var worker = new RecordingWorker(State());
        var dispatcher = new SessionAudioRequestDispatcher(Available(), worker);
        int manualOperations = 0;
        int successfulMutations = 0;
        dispatcher.ManualOperationStarting += () => manualOperations++;
        dispatcher.ManualOperationSucceeded += _ =>
        {
            successfulMutations++;
            return Task.CompletedTask;
        };

        await dispatcher.DispatchAsync(
            new DiscoverThxAudioRequest(
                SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid()),
            CancellationToken.None);
        Assert.Equal(0, manualOperations);

        await dispatcher.DispatchAutomationAsync(
            new ReadThxAudioRequest(
                SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(),
                ThxAudioEndpoint.Speakers),
            CancellationToken.None);
        Assert.Equal(0, manualOperations);

        await dispatcher.DispatchAsync(
            new ReadThxAudioRequest(
                SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(),
                ThxAudioEndpoint.Speakers),
            CancellationToken.None);
        Assert.Equal(1, manualOperations);
        Assert.Equal(0, successfulMutations);

        worker.Next = new ThxAudioWorkerResult(
            ThxAudioOperationStatus.Succeeded,
            ThxAudioAvailability.Available,
            null,
            State(spatial: true));
        await dispatcher.DispatchAsync(
            new SetThxSpatialRequest(
                SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(),
                ThxAudioEndpoint.Speakers,
                true),
            CancellationToken.None);
        Assert.Equal(2, manualOperations);
        Assert.Equal(1, successfulMutations);
    }

    [Fact]
    public async Task PublicConfirmedState_AnnotatesPauseAfterAsyncBarrier()
    {
        var worker = new RecordingWorker(State());
        var dispatcher = new SessionAudioRequestDispatcher(Available(), worker);
        bool paused = true;
        dispatcher.ApplicationAutomationPausedProvider = () => paused;
        dispatcher.ExplicitReadSucceeded += async cancellationToken =>
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            paused = false;
        };

        SessionAudioResponse response = await dispatcher.DispatchAsync(
            new ReadThxAudioRequest(
                SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(),
                ThxAudioEndpoint.Speakers),
            CancellationToken.None);

        Assert.Equal(ThxAudioOperationStatus.Succeeded, response.Status);
        Assert.False(response.State!.ApplicationAutomationPaused);
    }

    [Fact]
    public async Task AutomationLease_PreventsManualInterleavingAcrossMultipleRequests()
    {
        var worker = new RecordingWorker(State());
        var dispatcher = new SessionAudioRequestDispatcher(Available(), worker);
        var firstStepCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondStep = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<int> automation = dispatcher.RunAutomationAsync(
            async cancellationToken =>
            {
                SessionAudioResponse first = await dispatcher.DispatchAutomationAsync(
                    new ReadThxAudioRequest(
                        SessionAudioIpc.ProtocolVersion,
                        Guid.NewGuid(),
                        ThxAudioEndpoint.Speakers),
                    cancellationToken);
                Assert.Equal(ThxAudioOperationStatus.Succeeded, first.Status);
                firstStepCompleted.SetResult();
                await releaseSecondStep.Task.WaitAsync(cancellationToken);
                return 42;
            },
            CancellationToken.None);
        await firstStepCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task<SessionAudioResponse> manual = dispatcher.DispatchAsync(
            new DiscoverThxAudioRequest(
                SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid()),
            CancellationToken.None);
        Assert.False(manual.IsCompleted);

        releaseSecondStep.SetResult();
        Assert.Equal(42, await automation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(
            ThxAudioOperationStatus.Succeeded,
            (await manual.WaitAsync(TimeSpan.FromSeconds(2))).Status);
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
        [
            new ThxAudioPreset("music", "Music"),
            new ThxAudioPreset("game", "Game"),
            new ThxAudioPreset("custom", "Custom"),
        ])
    {
        SelectedNativeProfileId = "music",
        EqualizerBands = SpeakerBands([0, 0, 0, 0, 0, 0, 0]),
        SoundNormalization = new ThxAudioEnhancement(false, 55),
        VoiceClarity = new ThxAudioEnhancement(false, 55),
        ApplicationProfiles = ThxAudioFeatureAccess.Available,
        ApplicationProfileCatalog =
        [
            new ThxAudioApplicationProfile(
                "r5apex.exe",
                SupportsImmersive: true,
                SupportsCompetitive: true),
        ],
    };

    private static IReadOnlyList<ThxAudioEqualizerBand> SpeakerBands(
        IReadOnlyList<double> gains)
    {
        int[] frequencies = [250, 500, 1000, 2000, 4000, 8000, 16000];
        return frequencies.Select((frequency, index) =>
            new ThxAudioEqualizerBand(frequency, gains[index])).ToArray();
    }

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
