using OpenBlade.Contracts;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent.Tests;

public sealed class SessionThxAudioAutomationControllerTests
{
    [Theory]
    [InlineData(ThxAudioEndpoint.Speakers, 7)]
    [InlineData(ThxAudioEndpoint.Headphones, 10)]
    public async Task AppliesCompleteStoredCurveOnlyToObservedOutput(ThxAudioEndpoint endpoint, int bands)
    {
        var backend = new Backend(State(endpoint));
        var controller = new SessionThxAudioAutomationController(backend.DispatchAsync, _ => Task.FromResult(true), () => true, () => backend.Endpoint);
        ThxAudioOperationStatus result = await controller.ApplyAsync(Target(endpoint), CancellationToken.None);
        Assert.Equal(ThxAudioOperationStatus.Succeeded, result);
        Assert.Equal(endpoint, Assert.IsType<ReadThxAudioRequest>(backend.Requests[0]).Endpoint);
        SelectThxPresetRequest preset = Assert.Single(backend.Requests.OfType<SelectThxPresetRequest>());
        Assert.Equal(endpoint, preset.Endpoint);
        Assert.Equal("music", preset.PresetId);
        Assert.Equal(Enumerable.Repeat(1d, bands), preset.GainsDb);
        Assert.Equal(2, backend.Requests.Count);
    }

    [Fact]
    public async Task SpecialRuleUsesTypedExecutableMode()
    {
        var backend = new Backend(State(ThxAudioEndpoint.Headphones));
        var controller = new SessionThxAudioAutomationController(backend.DispatchAsync, _ => Task.FromResult(true), () => true, () => backend.Endpoint);
        var rule = new ThxApplicationAudioRule("GAME.EXE", ThxApplicationAudioOutput.Competitive);
        ThxAudioOperationStatus result = await controller.ApplyAsync(Target(ThxAudioEndpoint.Headphones) with
        { ApplicationRule = rule }, CancellationToken.None);
        Assert.Equal(ThxAudioOperationStatus.Succeeded, result);
        ApplyThxApplicationProfileRequest request = Assert.Single(backend.Requests.OfType<ApplyThxApplicationProfileRequest>());
        Assert.Equal("game.exe", request.ExecutableName);
        Assert.Equal(ThxAudioApplicationMode.Competitive, request.Mode);
        Assert.Equal(ThxAudioEndpoint.Headphones, request.Endpoint);
        Assert.Empty(backend.Requests.OfType<SelectThxPresetRequest>());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task GlobalOptOutOrInactiveSessionDoesNotEnterBackend(bool enabled, bool active)
    {
        var backend = new Backend(State(ThxAudioEndpoint.Speakers));
        var controller = new SessionThxAudioAutomationController(backend.DispatchAsync, _ => Task.FromResult(enabled), () => active, () => backend.Endpoint);
        Assert.Equal(ThxAudioOperationStatus.Unavailable,
            await controller.ApplyAsync(Target(ThxAudioEndpoint.Speakers), CancellationToken.None));
        Assert.Empty(backend.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FreshSessionAndPreferenceGuardsStopBeforeNextRequest(bool sessionLost)
    {
        var backend = new Backend(State(ThxAudioEndpoint.Speakers));
        bool allowed = true;
        backend.AfterRequest = _ => allowed = false;
        var controller = new SessionThxAudioAutomationController(backend.DispatchAsync,
            _ => Task.FromResult(sessionLost || allowed), () => !sessionLost || allowed, () => backend.Endpoint);
        Assert.Equal(ThxAudioOperationStatus.Unavailable,
            await controller.ApplyAsync(Target(ThxAudioEndpoint.Speakers), CancellationToken.None));
        Assert.IsType<ReadThxAudioRequest>(Assert.Single(backend.Requests));
    }

    [Fact]
    public async Task FailedMutationStopsCompositeWithoutRetry()
    {
        var backend = new Backend(State(ThxAudioEndpoint.Speakers)) { FailMutation = true };
        var controller = new SessionThxAudioAutomationController(backend.DispatchAsync, _ => Task.FromResult(true), () => true, () => backend.Endpoint);
        Assert.Equal(ThxAudioOperationStatus.Indeterminate,
            await controller.ApplyAsync(Target(ThxAudioEndpoint.Speakers), CancellationToken.None));
        Assert.Equal(2, backend.Requests.Count);
    }

    [Fact]
    public async Task PreferenceLostAfterConfirmedFirstWritePausesPartialComposite()
    {
        var backend = new Backend(State(ThxAudioEndpoint.Speakers));
        bool allowed = true;
        backend.AfterRequest = request => { if (request is SelectThxPresetRequest) { allowed = false; } };
        var controller = new SessionThxAudioAutomationController(backend.DispatchAsync, _ => Task.FromResult(allowed), () => true, () => backend.Endpoint);
        ThxAudioAutomationTarget target = Target(ThxAudioEndpoint.Speakers);
        target = target with { Settings = target.Settings with { Speakers = target.Settings.Speakers with { SpatialEnabled = true } } };
        Assert.Equal(ThxAudioOperationStatus.Indeterminate, await controller.ApplyAsync(target, CancellationToken.None));
        Assert.Equal(2, backend.Requests.Count);
    }

    [Fact]
    public async Task DefaultOutputChangeAfterReadStopsBeforeAnyNativeMutation()
    {
        var backend = new Backend(State(ThxAudioEndpoint.Speakers));
        ThxAudioEndpoint? active = ThxAudioEndpoint.Speakers;
        backend.AfterRequest = _ => active = ThxAudioEndpoint.Headphones;
        var controller = new SessionThxAudioAutomationController(backend.DispatchAsync,
            _ => Task.FromResult(true), () => true, () => active);
        Assert.Equal(ThxAudioOperationStatus.Unavailable,
            await controller.ApplyAsync(Target(ThxAudioEndpoint.Speakers), CancellationToken.None));
        Assert.IsType<ReadThxAudioRequest>(Assert.Single(backend.Requests));
    }

    [Fact]
    public async Task DisabledEnhancementsIgnoreStoredLevelDifferences()
    {
        var backend = new Backend(State(ThxAudioEndpoint.Speakers) with
        {
            VoiceClarity = new(false, 37),
            SoundNormalization = new(false, 63),
        });
        var controller = new SessionThxAudioAutomationController(
            backend.DispatchAsync,
            _ => Task.FromResult(true),
            () => true,
            () => backend.Endpoint);

        Assert.Equal(
            ThxAudioOperationStatus.Succeeded,
            await controller.ApplyAsync(Target(ThxAudioEndpoint.Speakers), CancellationToken.None));
        Assert.Empty(backend.Requests.OfType<SetThxEnhancementRequest>());
    }

    [Fact]
    public async Task DisablingEnhancementsUsesNullLevelToPreserveNativeValue()
    {
        var backend = new Backend(State(ThxAudioEndpoint.Speakers) with
        {
            VoiceClarity = new(true, 37),
            SoundNormalization = new(true, 63),
        });
        var controller = new SessionThxAudioAutomationController(
            backend.DispatchAsync,
            _ => Task.FromResult(true),
            () => true,
            () => backend.Endpoint);

        Assert.Equal(
            ThxAudioOperationStatus.Succeeded,
            await controller.ApplyAsync(Target(ThxAudioEndpoint.Speakers), CancellationToken.None));
        SetThxEnhancementRequest[] requests =
            [.. backend.Requests.OfType<SetThxEnhancementRequest>()];
        Assert.Equal(2, requests.Length);
        Assert.All(requests, request =>
        {
            Assert.False(request.Enabled);
            Assert.Null(request.LevelPercent);
        });
    }

    private static ThxAudioAutomationTarget Target(ThxAudioEndpoint endpoint) => new(
        new(true, Settings(7, true), Settings(10, false), []), null, endpoint, "desktop.exe");

    private static ThxAudioEndpointSettings Settings(int count, bool normalization)
    {
        double[] gains = Enumerable.Repeat(1d, count).ToArray();
        return new(false, false, ThxEqualizerPreset.Music,
            new(gains, gains, gains, gains, gains), new(false, 50), normalization ? new(false, 50) : null);
    }

    private static ThxAudioState State(ThxAudioEndpoint endpoint) => new(endpoint, false, false,
        "game", [new("game", "Game"), new("music", "Music")])
    {
        EqualizerBands = Enumerable.Range(0, endpoint == ThxAudioEndpoint.Speakers ? 7 : 10)
            .Select(index => new ThxAudioEqualizerBand(index, 0)).ToArray(),
        VoiceClarity = new(false, 50),
        SoundNormalization = endpoint == ThxAudioEndpoint.Speakers ? new(false, 50) : null,
        ApplicationProfileCatalog = [new("game.exe", true, true)],
    };

    private sealed class Backend(ThxAudioState state)
    {
        internal List<SessionAudioRequest> Requests { get; } = [];
        internal ThxAudioEndpoint Endpoint => state.Endpoint;
        internal bool FailMutation { get; init; }
        internal Action<SessionAudioRequest>? AfterRequest { get; set; }

        internal Task<SessionAudioResponse> DispatchAsync(SessionAudioRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (FailMutation && request is not ReadThxAudioRequest)
            {
                return Task.FromResult(new SessionAudioResponse(SessionAudioIpc.ProtocolVersion, request.RequestId,
                    ThxAudioOperationStatus.Failed, ThxAudioAvailability.Available, "failed", null));
            }
            state = request switch
            {
                SelectThxPresetRequest preset => state with
                {
                    SelectedPresetId = preset.PresetId,
                    EqualizerBands = preset.GainsDb.Select((gain, index) => new ThxAudioEqualizerBand(index, gain)).ToArray(),
                },
                ApplyThxApplicationProfileRequest => state with { SpatialEnabled = true, SelectedPresetId = null },
                SetThxSpatialRequest spatial => state with { SpatialEnabled = spatial.Enabled },
                SetThxEnhancementRequest enhancement
                    when enhancement.Kind == ThxAudioEnhancementKind.VoiceClarity => state with
                    {
                        VoiceClarity = new(
                            enhancement.Enabled,
                            enhancement.LevelPercent ?? state.VoiceClarity!.LevelPercent),
                    },
                SetThxEnhancementRequest enhancement => state with
                {
                    SoundNormalization = new(
                        enhancement.Enabled,
                        enhancement.LevelPercent ?? state.SoundNormalization!.LevelPercent),
                },
                _ => state,
            };
            AfterRequest?.Invoke(request);
            return Task.FromResult(new SessionAudioResponse(SessionAudioIpc.ProtocolVersion, request.RequestId,
                ThxAudioOperationStatus.Succeeded, ThxAudioAvailability.Available, null, state));
        }
    }
}
