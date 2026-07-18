using OpenBlade.Display;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

public sealed class SessionDisplayRequestDispatcher
{
    private readonly IDisplayProfileApplier applier;

    public SessionDisplayRequestDispatcher(IDisplayProfileApplier applier)
    {
        this.applier = applier ?? throw new ArgumentNullException(nameof(applier));
    }

    public async Task<SessionDisplayResponse> DispatchAsync(
        SessionDisplayRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        SessionDisplayExecutionResult result;
        try
        {
            result = request switch
            {
                QueryDisplayRequest => await applier.QueryAsync(cancellationToken)
                    .ConfigureAwait(false),
                SetRefreshRateRequest value => await applier.SetRefreshRateAsync(
                    value.ExpectedPowerSource,
                    value.RefreshRateKey,
                    cancellationToken).ConfigureAwait(false),
                SetHdrRequest value => await applier.SetHdrAsync(
                    value.ExpectedPowerSource,
                    value.Enabled,
                    cancellationToken).ConfigureAwait(false),
                SetColorProfileRequest value => await applier.SetColorProfileAsync(
                    value.ExpectedPowerSource,
                    value.ColorProfileKey,
                    cancellationToken).ConfigureAwait(false),
                ApplyDisplayProfileRequest value => await applier.ApplyProfileAsync(
                    value.ExpectedPowerSource,
                    value.Settings,
                    cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidDataException("Session display request is unsupported."),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = await TryQueryFailureAsync(
                SessionDisplayOperationStatus.Blocked,
                "The display operation became stale after the power source changed.",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            result = await TryQueryFailureAsync(
                SessionDisplayOperationStatus.Failed,
                exception.Message,
                cancellationToken).ConfigureAwait(false);
        }

        return new SessionDisplayResponse(
            SessionDisplayIpc.ProtocolVersion,
            request.RequestId,
            result.Status,
            result.Error,
            result.State);
    }

    private async Task<SessionDisplayExecutionResult> TryQueryFailureAsync(
        SessionDisplayOperationStatus status,
        string error,
        CancellationToken cancellationToken)
    {
        try
        {
            SessionDisplayExecutionResult query = await applier.QueryAsync(cancellationToken)
                .ConfigureAwait(false);
            return new SessionDisplayExecutionResult(status, error, query.State);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new SessionDisplayExecutionResult(
                status,
                error,
                Unavailable($"{error} Display state could not be refreshed: {exception.Message}"));
        }
    }

    private static DisplaySettingsSnapshot Unavailable(string message) => new(
        IsAvailable: false,
        DisplayName: null,
        Width: 0,
        Height: 0,
        CurrentRefreshKey: null,
        RefreshOptions: [],
        HdrSupported: false,
        HdrEnabled: false,
        ColorProfilesEnabled: false,
        ActiveColorProfileId: null,
        ColorProfiles: [],
        Message: message);
}
