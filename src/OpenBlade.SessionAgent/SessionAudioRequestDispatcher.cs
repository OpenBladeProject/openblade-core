using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

public sealed class SessionAudioRequestDispatcher
{
    private const string SpatialPropertyId = "spatial_enabled";
    private const string EqualizerPropertyId = "eq_enabled";

    private readonly IThxAudioAdmissionSource admission;
    private readonly IThxAudioWorker worker;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<ThxAudioEndpoint, ThxAudioState> confirmed = [];
    private bool mutationsPaused;

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
                return await ExecuteReadAsync(request, read.Endpoint, cancellationToken)
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
                    value.PresetId),
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
            mutationsPaused = false;
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
            StringComparison.Ordinal),
        _ => false,
    };

    private static ThxAudioEndpoint GetEndpoint(SessionAudioRequest request) => request switch
    {
        SetThxSpatialRequest value => value.Endpoint,
        SetThxEqualizerRequest value => value.Endpoint,
        SelectThxPresetRequest value => value.Endpoint,
        _ => throw new InvalidDataException("Session audio request is unsupported."),
    };

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
}
