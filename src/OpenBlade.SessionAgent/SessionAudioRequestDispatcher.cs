using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

public sealed class SessionAudioRequestDispatcher
{
    private const string SpatialPropertyId = "spatial_enabled";
    private const string EqualizerPropertyId = "eq_enabled";

    private readonly IThxAudioAdmissionSource admission;
    private readonly IThxAudioWorker worker;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<ThxAudioEndpoint, ThxAudioState> confirmed = [];
    private bool mutationsPaused;

    internal event Func<CancellationToken, Task>? ExplicitReadSucceeded;

    internal event Action? ManualOperationStarting;

    internal event Func<CancellationToken, Task>? ManualOperationSucceeded;

    internal Func<bool>? ApplicationAutomationPausedProvider { get; set; }

    internal SessionAudioRequestDispatcher(
        IThxAudioAdmissionSource admission,
        IThxAudioWorker worker)
    {
        this.admission = admission ?? throw new ArgumentNullException(nameof(admission));
        this.worker = worker ?? throw new ArgumentNullException(nameof(worker));
    }

    public async Task<SessionAudioResponse> DispatchAsync(
        SessionAudioRequest request,
        CancellationToken cancellationToken)
    {
        bool manualOperation = IsThxNativeManualOperation(request);
        if (manualOperation)
        {
            ManualOperationStarting?.Invoke();
        }
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SessionAudioResponse response = await DispatchCoreAsync(
                request,
                isExplicitRead: true,
                cancellationToken).ConfigureAwait(false);
            if (IsThxNativeMutation(request)
                && response.Status == ThxAudioOperationStatus.Succeeded)
            {
                await InvokeAsync(ManualOperationSucceeded, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (response.State is { } state)
            {
                response = response with
                {
                    State = state with
                    {
                        ApplicationAutomationPaused = ReadAutomationPaused(),
                    },
                };
            }
            return response;
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal async Task<T> RunAutomationAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal Task<SessionAudioResponse> DispatchAutomationAsync(
        SessionAudioRequest request,
        CancellationToken cancellationToken) =>
        DispatchCoreAsync(request, isExplicitRead: false, cancellationToken);

    private async Task<SessionAudioResponse> DispatchCoreAsync(
        SessionAudioRequest request,
        bool isExplicitRead,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThxAudioAdmission current = admission.Check();
            if (!current.IsAvailable)
            {
                confirmed.Clear();
                return Response(
                    request,
                    ThxAudioOperationStatus.Unavailable,
                    current.Availability,
                    current.Error,
                    null);
            }

            if (request is DiscoverThxAudioRequest)
            {
                return Response(
                    request,
                    ThxAudioOperationStatus.Succeeded,
                    ThxAudioAvailability.Available,
                    null,
                    null);
            }

            if (request is ReadThxAudioRequest read)
            {
                if (!isExplicitRead && mutationsPaused)
                {
                    return Response(
                        request,
                        ThxAudioOperationStatus.Unavailable,
                        ThxAudioAvailability.Available,
                        "An explicit THX read is required after an uncertain write.",
                        null);
                }
                return await ExecuteReadAsync(
                        request,
                        read.Endpoint,
                        clearPause: isExplicitRead,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            ThxAudioEndpoint endpoint = GetEndpoint(request);
            if (mutationsPaused || !confirmed.TryGetValue(endpoint, out ThxAudioState? prior))
            {
                return Response(
                    request,
                    ThxAudioOperationStatus.Unavailable,
                    ThxAudioAvailability.Available,
                    "Read THX state before changing it.",
                    null);
            }
            if (request is SelectThxPresetRequest preset
                && !prior.Presets.Any(value => string.Equals(
                    value.Id,
                    preset.PresetId,
                    StringComparison.Ordinal)))
            {
                return Response(
                    request,
                    ThxAudioOperationStatus.Failed,
                    ThxAudioAvailability.Available,
                    "The preset ID was not returned by the latest THX read.",
                    null);
            }
            if (request is ApplyThxApplicationProfileRequest application
                && !SupportsApplicationProfile(prior, application))
            {
                return Response(
                    request,
                    ThxAudioOperationStatus.Failed,
                    ThxAudioAvailability.Available,
                    "The application profile was not returned by the latest THX read.",
                    null);
            }

            ThxAudioHelperRequest helperRequest = request switch
            {
                SetThxSpatialRequest value => new ThxHelperSetPropertyRequest(
                    ThxAudioHelperProtocol.Version,
                    value.Endpoint,
                    SpatialPropertyId,
                    value.Enabled),
                SetThxEqualizerRequest value => new ThxHelperSetPropertyRequest(
                    ThxAudioHelperProtocol.Version,
                    value.Endpoint,
                    EqualizerPropertyId,
                    value.Enabled),
                SelectThxPresetRequest value => new ThxHelperSelectPresetRequest(
                    ThxAudioHelperProtocol.Version,
                    value.Endpoint,
                    value.PresetId,
                    value.GainsDb),
                SetThxEnhancementRequest value => new ThxHelperSetEnhancementRequest(
                    ThxAudioHelperProtocol.Version,
                    value.Endpoint,
                    value.Kind,
                    value.Enabled,
                    value.LevelPercent),
                ApplyThxApplicationProfileRequest value =>
                    new ThxHelperApplyApplicationProfileRequest(
                        ThxAudioHelperProtocol.Version,
                        value.Endpoint,
                        value.ExecutableName,
                        value.Mode),
                _ => throw new InvalidDataException("Session audio request is unsupported."),
            };
            ThxAudioWorkerResult result;
            try
            {
                result = await worker.ExecuteAsync(helperRequest, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                MarkIndeterminate(endpoint);
                throw;
            }
            catch (Exception exception)
            {
                MarkIndeterminate(endpoint);
                return Response(
                    request,
                    ThxAudioOperationStatus.Indeterminate,
                    ThxAudioAvailability.Available,
                    $"The THX helper ended after a possible mutation: {exception.GetType().Name}.",
                    null);
            }
            return Complete(request, endpoint, result);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Response(
                request,
                ThxAudioOperationStatus.Failed,
                ThxAudioAvailability.Available,
                $"THX audio request failed: {exception.GetType().Name}.",
                null);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<SessionAudioResponse> ExecuteReadAsync(
        SessionAudioRequest request,
        ThxAudioEndpoint endpoint,
        bool clearPause,
        CancellationToken cancellationToken)
    {
        ThxAudioWorkerResult result;
        try
        {
            result = await worker.ExecuteAsync(
                new ThxHelperReadRequest(ThxAudioHelperProtocol.Version, endpoint),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            confirmed.Remove(endpoint);
            return Response(
                request,
                ThxAudioOperationStatus.Failed,
                ThxAudioAvailability.Available,
                $"The THX helper read failed: {exception.GetType().Name}.",
                null);
        }
        if (result.Status == ThxAudioOperationStatus.Succeeded
            && result.State is { } state
            && state.Endpoint == endpoint)
        {
            confirmed[endpoint] = state;
            if (clearPause)
            {
                mutationsPaused = false;
                await InvokeAsync(ExplicitReadSucceeded, cancellationToken)
                    .ConfigureAwait(false);
            }
            return Response(
                request,
                result.Status,
                result.Availability,
                null,
                state);
        }

        confirmed.Remove(endpoint);
        return Response(
            request,
            result.Status == ThxAudioOperationStatus.Indeterminate
                ? ThxAudioOperationStatus.Failed
                : result.Status,
            result.Availability,
            result.Error ?? "THX state could not be read.",
            null);
    }

    private SessionAudioResponse Complete(
        SessionAudioRequest request,
        ThxAudioEndpoint endpoint,
        ThxAudioWorkerResult result)
    {
        if (result.Status == ThxAudioOperationStatus.Succeeded
            && result.State is { } state
            && state.Endpoint == endpoint
            && Matches(request, state))
        {
            confirmed[endpoint] = state;
            return Response(
                request,
                ThxAudioOperationStatus.Succeeded,
                result.Availability,
                null,
                state);
        }

        if (result.Status == ThxAudioOperationStatus.Indeterminate
            || result.Status == ThxAudioOperationStatus.Succeeded)
        {
            MarkIndeterminate(endpoint);
            return Response(
                request,
                ThxAudioOperationStatus.Indeterminate,
                result.Availability,
                result.Error ?? "The THX mutation could not be confirmed by readback.",
                null);
        }

        confirmed.Remove(endpoint);
        return Response(
            request,
            result.Status,
            result.Availability,
            result.Error ?? "The THX mutation failed.",
            null);
    }

    private void MarkIndeterminate(ThxAudioEndpoint endpoint)
    {
        confirmed.Remove(endpoint);
        mutationsPaused = true;
    }

    private static bool Matches(SessionAudioRequest request, ThxAudioState state) => request switch
    {
        SetThxSpatialRequest value => state.SpatialEnabled == value.Enabled,
        SetThxEqualizerRequest value => state.EqualizerEnabled == value.Enabled,
        SelectThxPresetRequest value => string.Equals(
            state.SelectedPresetId,
            value.PresetId,
            StringComparison.Ordinal)
            && GainsMatch(state.EqualizerBands, value.GainsDb),
        SetThxEnhancementRequest value => MatchesEnhancement(value, state),
        ApplyThxApplicationProfileRequest value =>
            state.SpatialEnabled
            && ThxAudioApplicationProfileCatalog.TryResolve(
                value.ExecutableName,
                value.Mode,
                out string nativePresetId)
            && string.Equals(
                state.SelectedNativeProfileId,
                nativePresetId,
                StringComparison.Ordinal),
        _ => false,
    };

    private static bool GainsMatch(
        IReadOnlyList<ThxAudioEqualizerBand> bands,
        IReadOnlyList<double> requested) =>
        bands.Count == requested.Count
        && bands.Select((band, index) => Math.Abs(band.GainDb - requested[index]))
            .All(difference => difference <= 0.0001);

    private static bool MatchesEnhancement(
        SetThxEnhancementRequest request,
        ThxAudioState state)
    {
        ThxAudioEnhancement? actual = request.Kind switch
        {
            ThxAudioEnhancementKind.SoundNormalization => state.SoundNormalization,
            ThxAudioEnhancementKind.VoiceClarity => state.VoiceClarity,
            _ => null,
        };
        return actual is not null
            && actual.Enabled == request.Enabled
            && (!request.Enabled
                || request.LevelPercent is null
                || actual.LevelPercent == request.LevelPercent);
    }

    private static bool SupportsApplicationProfile(
        ThxAudioState state,
        ApplyThxApplicationProfileRequest request)
    {
        ThxAudioApplicationProfile? profile = state.ApplicationProfileCatalog.FirstOrDefault(
            value => string.Equals(
                value.ExecutableName,
                request.ExecutableName,
                StringComparison.Ordinal));
        return request.Mode switch
        {
            ThxAudioApplicationMode.Immersive => profile?.SupportsImmersive == true,
            ThxAudioApplicationMode.Competitive => profile?.SupportsCompetitive == true,
            _ => false,
        };
    }

    private static ThxAudioEndpoint GetEndpoint(SessionAudioRequest request) => request switch
    {
        SetThxSpatialRequest value => value.Endpoint,
        SetThxEqualizerRequest value => value.Endpoint,
        SelectThxPresetRequest value => value.Endpoint,
        SetThxEnhancementRequest value => value.Endpoint,
        ApplyThxApplicationProfileRequest value => value.Endpoint,
        _ => throw new InvalidDataException("Session audio request is unsupported."),
    };

    private static bool IsThxNativeManualOperation(SessionAudioRequest request) =>
        request is ReadThxAudioRequest || IsThxNativeMutation(request);

    private static bool IsThxNativeMutation(SessionAudioRequest request) => request is
        SetThxSpatialRequest
        or SetThxEqualizerRequest
        or SelectThxPresetRequest
        or SetThxEnhancementRequest
        or ApplyThxApplicationProfileRequest;

    private static SessionAudioResponse Response(
        SessionAudioRequest request,
        ThxAudioOperationStatus status,
        ThxAudioAvailability availability,
        string? error,
        ThxAudioState? state) => new(
            SessionAudioIpc.ProtocolVersion,
            request.RequestId,
            status,
            availability,
            error,
            state);

    private bool ReadAutomationPaused()
    {
        try
        {
            return ApplicationAutomationPausedProvider?.Invoke() == true;
        }
        catch
        {
            // A UI diagnostic must not obscure confirmed native readback.
            return true;
        }
    }

    private static async Task InvokeAsync(
        Func<CancellationToken, Task>? handlers,
        CancellationToken cancellationToken)
    {
        if (handlers is null)
        {
            return;
        }
        foreach (Func<CancellationToken, Task> handler in handlers.GetInvocationList())
        {
            await handler(cancellationToken).ConfigureAwait(false);
        }
    }
}
