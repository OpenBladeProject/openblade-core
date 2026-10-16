using OpenBlade.Session;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SessionAudioRequestDispatcherTests
{
    [Fact]
    public async Task ObservedOnlyApplicationCatalogCannotAuthorizeAProfileMutation()
    {
        var worker = new RecordingWorker(State() with { ApplicationProfiles = ThxAudioFeatureAccess.ObservedOnly });
        var dispatcher = new SessionAudioRequestDispatcher(Available(), worker);
        await dispatcher.DispatchAsync(new ReadThxAudioRequest(SessionAudioIpc.ProtocolVersion,
            Guid.NewGuid(), ThxAudioEndpoint.Speakers), CancellationToken.None);
        SessionAudioResponse response = await dispatcher.DispatchAsync(new ApplyThxApplicationProfileRequest(
            SessionAudioIpc.ProtocolVersion, Guid.NewGuid(), ThxAudioEndpoint.Speakers,
            "r5apex.exe", ThxAudioApplicationMode.Immersive), CancellationToken.None);
        Assert.Equal(ThxAudioOperationStatus.Failed, response.Status);
        Assert.Single(worker.Requests);
    }

    [Fact]
    public async Task MutationsPreserveAdvisoryFromLastReadAndExplicitReadRefreshesIt()
    {
        var worker = new RecordingWorker(State() with { SystemEffectsDisabled = true });
        var dispatcher = new SessionAudioRequestDispatcher(Available(), worker);
        await dispatcher.DispatchAsync(new ReadThxAudioRequest(SessionAudioIpc.ProtocolVersion,
            Guid.NewGuid(), ThxAudioEndpoint.Speakers), CancellationToken.None);
        worker.Next = new ThxAudioWorkerResult(ThxAudioOperationStatus.Succeeded,
            ThxAudioAvailability.Available, null, State(spatial: true));
        SessionAudioResponse changed = await dispatcher.DispatchAsync(new SetThxSpatialRequest(
            SessionAudioIpc.ProtocolVersion, Guid.NewGuid(), ThxAudioEndpoint.Speakers, true), CancellationToken.None);
        Assert.True(changed.State!.SpatialEnabled);
        Assert.True(changed.State.SystemEffectsDisabled);
        worker.Next = new ThxAudioWorkerResult(ThxAudioOperationStatus.Succeeded,
            ThxAudioAvailability.Available, null, State(spatial: true) with { SystemEffectsDisabled = false });
        SessionAudioResponse read = await dispatcher.DispatchAsync(new ReadThxAudioRequest(
            SessionAudioIpc.ProtocolVersion, Guid.NewGuid(), ThxAudioEndpoint.Speakers), CancellationToken.None);
        Assert.False(read.State!.SystemEffectsDisabled);
    }

    [Fact]
    public async Task HelperReadOnlyAdmissionIsPreservedAndDeniesMutation()
    {
        var worker = new RecordingWorker(State())
        {
            Next = new ThxAudioWorkerResult(ThxAudioOperationStatus.Succeeded,
                ThxAudioAvailability.AvailableReadOnly, null, State()),
        };
        var dispatcher = new SessionAudioRequestDispatcher(Available(), worker);
        SessionAudioResponse read = await dispatcher.DispatchAsync(
            new ReadThxAudioRequest(SessionAudioIpc.ProtocolVersion, Guid.NewGuid(), ThxAudioEndpoint.Speakers),
            CancellationToken.None);
        Assert.Equal(ThxAudioOperationStatus.Succeeded, read.Status);
        Assert.Equal(ThxAudioAvailability.AvailableReadOnly, read.Availability);
        // The helper's fresh admission rejects the mutation before constructing
        // native code, as covered by ThxAudioHelperCommandTests.
        worker.Next = new ThxAudioWorkerResult(ThxAudioOperationStatus.Unavailable,
            ThxAudioAvailability.AvailableReadOnly, "This stack is read-only.", null);
        SessionAudioResponse write = await dispatcher.DispatchAsync(
            new SetThxSpatialRequest(SessionAudioIpc.ProtocolVersion, Guid.NewGuid(), ThxAudioEndpoint.Speakers, true),
            CancellationToken.None);
        Assert.Equal(ThxAudioOperationStatus.Unavailable, write.Status);
        Assert.Equal(ThxAudioAvailability.AvailableReadOnly, write.Availability);
        Assert.Null(write.State);
        Assert.Equal(2, worker.Requests.Count);
    }

    [Fact]
    public async Task Discover_PerformsAdmissionWithoutStartingNativeWorker()
    {
        var worker = new RecordingWorker(State());
        var admission = Available();
        var dispatcher = new SessionAudioRequestDispatcher(admission, worker);

        SessionAudioResponse response = await dispatcher.DispatchAsync(
            new DiscoverThxAudioRequest(SessionAudioIpc.ProtocolVersion, Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(ThxAudioOperationStatus.Succeeded, response.Status);
        Assert.Equal(ThxAudioAvailability.Available, response.Availability);
        Assert.Null(response.State);
        Assert.Empty(worker.Requests);
        Assert.Equal(1, admission.Checks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeRequests_RelyOnFreshHelperAdmissionWithoutDuplicateDiscovery(bool automation)
    {
        var admission = Available();
        var worker = new RecordingWorker(State());
        var dispatcher = new SessionAudioRequestDispatcher(admission, worker);
        var read = new ReadThxAudioRequest(SessionAudioIpc.ProtocolVersion,
            Guid.NewGuid(), ThxAudioEndpoint.Speakers);
        SessionAudioResponse readResult = automation
            ? await dispatcher.DispatchAutomationAsync(read, CancellationToken.None)
            : await dispatcher.DispatchAsync(read, CancellationToken.None);
        Assert.Equal(ThxAudioOperationStatus.Succeeded, readResult.Status);
        worker.Next = new ThxAudioWorkerResult(ThxAudioOperationStatus.Succeeded,
            ThxAudioAvailability.Available, null, State(spatial: true));
        var write = new SetThxSpatialRequest(SessionAudioIpc.ProtocolVersion,
            Guid.NewGuid(), ThxAudioEndpoint.Speakers, true);
        SessionAudioResponse writeResult = automation
            ? await dispatcher.DispatchAutomationAsync(write, CancellationToken.None)
            : await dispatcher.DispatchAsync(write, CancellationToken.None);

        Assert.Equal(ThxAudioOperationStatus.Succeeded, writeResult.Status);
        Assert.Equal(2, worker.Requests.Count);
        Assert.Equal(0, admission.Checks);
    }

    [Theory]
    [InlineData(false, ThxAudioAvailability.ControllerActive)]
    [InlineData(true, ThxAudioAvailability.ControllerActive)]
    [InlineData(false, ThxAudioAvailability.ComponentsUnavailable)]
    [InlineData(true, ThxAudioAvailability.ComponentsUnavailable)]
    [InlineData(true, ThxAudioAvailability.AvailableReadOnly)]
    public async Task HelperAdmissionFailure_InvalidatesBothOutputs(bool mutation, ThxAudioAvailability availability)
    {
        var worker = new RecordingWorker(State());
        var dispatcher = new SessionAudioRequestDispatcher(Available(), worker);
        await ReadAsync(dispatcher);
        worker.Next = new ThxAudioWorkerResult(ThxAudioOperationStatus.Succeeded,
            ThxAudioAvailability.Available, null, State() with { Endpoint = ThxAudioEndpoint.Headphones });
        SessionAudioResponse headphones = await dispatcher.DispatchAsync(
            new ReadThxAudioRequest(SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(), ThxAudioEndpoint.Headphones), CancellationToken.None);
        Assert.Equal(ThxAudioOperationStatus.Succeeded, headphones.Status);

        worker.Next = new ThxAudioWorkerResult(ThxAudioOperationStatus.Unavailable,
            availability, "Fresh helper admission failed.", null);
        SessionAudioRequest failedRequest = mutation
            ? new SetThxSpatialRequest(SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(), ThxAudioEndpoint.Speakers, true)
            : new ReadThxAudioRequest(SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(), ThxAudioEndpoint.Speakers);
        SessionAudioResponse failed = await dispatcher.DispatchAsync(failedRequest, CancellationToken.None);
        Assert.Equal(ThxAudioOperationStatus.Unavailable, failed.Status);
        Assert.Equal(availability, failed.Availability);

        int submitted = worker.Requests.Count;
        foreach (ThxAudioEndpoint endpoint in Enum.GetValues<ThxAudioEndpoint>())
        {
            SessionAudioResponse blocked = await dispatcher.DispatchAsync(
                new SetThxSpatialRequest(SessionAudioIpc.ProtocolVersion,
                    Guid.NewGuid(), endpoint, true), CancellationToken.None);
            Assert.Equal(ThxAudioOperationStatus.Unavailable, blocked.Status);
            Assert.Equal("Read THX state before changing it.", blocked.Error);
        }
        Assert.Equal(submitted, worker.Requests.Count);
    }

    [Fact]
    public async Task Connect_ReadsStateWithoutManualOperationOrReconciliationEvents()
    {
        var admission = Available();
        var worker = new RecordingWorker(State());
        var dispatcher = new SessionAudioRequestDispatcher(admission, worker);
        int manualOperations = 0;
        int explicitReads = 0;
        dispatcher.ManualOperationStarting += () => manualOperations++;
        dispatcher.ExplicitReadSucceeded += _ =>
        {
            explicitReads++;
            return Task.CompletedTask;
        };

        SessionAudioResponse connected = await dispatcher.DispatchAsync(
            new ConnectThxAudioRequest(SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(), ThxAudioEndpoint.Speakers), CancellationToken.None);

        Assert.Equal(ThxAudioOperationStatus.Succeeded, connected.Status);
        Assert.NotNull(connected.State);
        Assert.IsType<ThxHelperReadRequest>(Assert.Single(worker.Requests));
        Assert.Equal(0, admission.Checks);
        Assert.Equal(0, manualOperations);
        Assert.Equal(0, explicitReads);
    }

    [Fact]
    public async Task Connect_CannotReadOrClearPauseAfterUncertainMutation()
    {
        var worker = new RecordingWorker(State());
        var dispatcher = new SessionAudioRequestDispatcher(Available(), worker);
        await ReadAsync(dispatcher);
        worker.Exception = new IOException("helper pipe ended");
        SessionAudioResponse uncertain = await dispatcher.DispatchAsync(
            new SetThxSpatialRequest(SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(), ThxAudioEndpoint.Speakers, true), CancellationToken.None);
        Assert.Equal(ThxAudioOperationStatus.Indeterminate, uncertain.Status);
        worker.Exception = null;
        int submitted = worker.Requests.Count;

        SessionAudioResponse connect = await dispatcher.DispatchAsync(
            new ConnectThxAudioRequest(SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(), ThxAudioEndpoint.Speakers), CancellationToken.None);
        Assert.Equal(ThxAudioOperationStatus.Unavailable, connect.Status);
        Assert.Null(connect.State);
        SessionAudioResponse blocked = await dispatcher.DispatchAsync(
            new SetThxSpatialRequest(SessionAudioIpc.ProtocolVersion,
                Guid.NewGuid(), ThxAudioEndpoint.Speakers, true), CancellationToken.None);
        Assert.Equal(ThxAudioOperationStatus.Unavailable, blocked.Status);
        Assert.Equal(submitted, worker.Requests.Count);

        await ReadAsync(dispatcher);
        Assert.Equal(submitted + 1, worker.Requests.Count);
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

    private static FixedAdmission Available() => new(
        new ThxAudioAdmission(ThxAudioAvailability.Available, null) { ProductId = "rznb12", ModelNumber = "RZ09-0581", MutationAllowed = true });

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
        public int Checks { get; private set; }

        public ThxAudioAdmission Check()
        {
            Checks++;
            return value;
        }
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
