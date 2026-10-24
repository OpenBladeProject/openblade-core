using OpenBlade.Contracts;
using OpenBlade.Display;
using OpenBlade.Session;

namespace OpenBlade.SessionAgent;

public sealed class SessionDisplayRequestDispatcher
{
    private readonly IDisplayProfileApplier applier;
    private readonly Func<DisplayProfileSettings, bool>? manualOperationStarting;
    private readonly Action<SessionDisplayOperationStatus, DisplaySettingsSnapshot>?
        manualOperationCompleted;
    private readonly Action<DisplaySettingsSnapshot>? successfulExplicitRead;

    public SessionDisplayRequestDispatcher(
        IDisplayProfileApplier applier,
        Func<DisplayProfileSettings, bool>? manualOperationStarting = null,
        Action<SessionDisplayOperationStatus, DisplaySettingsSnapshot>?
            manualOperationCompleted = null,
        Action<DisplaySettingsSnapshot>? successfulExplicitRead = null)
    {
        this.applier = applier ?? throw new ArgumentNullException(nameof(applier));
        this.manualOperationStarting = manualOperationStarting;
        this.manualOperationCompleted = manualOperationCompleted;
        this.successfulExplicitRead = successfulExplicitRead;
    }

    public async Task<SessionDisplayResponse> DispatchAsync(
        SessionDisplayRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        SessionDisplayExecutionResult result;
        bool manualOperationAdmitted = false;
        try
        {
            DisplayProfileSettings? affectedSettings = AffectedSettings(request);
            if (affectedSettings is not null)
            {
                if (manualOperationStarting?.Invoke(affectedSettings) == false)
                {
                    result = await TryQueryFailureAsync(
                        SessionDisplayOperationStatus.Blocked,
                        "The display operation was blocked because its recovery checkpoint could not be saved.",
                        cancellationToken).ConfigureAwait(false);
                    return Response(request, result);
                }
                manualOperationAdmitted = true;
            }
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
                SetBrightnessRequest value => await applier.SetBrightnessAsync(
                    value.ExpectedPowerSource,
                    value.Percent,
                    cancellationToken).ConfigureAwait(false),
                ApplyDisplayProfileRequest value => await applier.ApplyProfileAsync(
                    value.ExpectedPowerSource,
                    value.Settings,
                    cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidDataException("Session display request is unsupported."),
            };
            if (request is QueryDisplayRequest && result.Succeeded)
            {
                successfulExplicitRead?.Invoke(result.State);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            result = await TryQueryFailureAsync(
                manualOperationAdmitted
                    ? SessionDisplayOperationStatus.Indeterminate
                    : SessionDisplayOperationStatus.Blocked,
                "The display operation became stale after the power source changed.",
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            DiagnosticTrace.TraceException(
                "OpenBlade.SessionAgent.SessionDisplayRequestDispatcher",
                "DispatchRequest",
                exception);
            result = await TryQueryFailureAsync(
                manualOperationAdmitted
                    ? SessionDisplayOperationStatus.Indeterminate
                    : SessionDisplayOperationStatus.Failed,
                exception.Message,
                cancellationToken).ConfigureAwait(false);
        }

        if (manualOperationAdmitted)
        {
            manualOperationCompleted?.Invoke(result.Status, result.State);
        }

        return Response(request, result);
    }

    private static SessionDisplayResponse Response(
        SessionDisplayRequest request,
        SessionDisplayExecutionResult result) => new(
            SessionDisplayIpc.ProtocolVersion,
            request.RequestId,
            result.Status,
            result.Error,
            result.State);

    private static DisplayProfileSettings? AffectedSettings(SessionDisplayRequest request) =>
        request switch
        {
            QueryDisplayRequest => null,
            SetRefreshRateRequest value => new DisplayProfileSettings(
                value.RefreshRateKey, null, null),
            SetHdrRequest value => new DisplayProfileSettings(null, value.Enabled, null),
            SetColorProfileRequest value => new DisplayProfileSettings(
                null, null, value.ColorProfileKey),
            SetBrightnessRequest value => DisplayProfileSettings.Unconfigured() with
            {
                BrightnessPercent = value.Percent,
            },
            ApplyDisplayProfileRequest value => value.Settings,
            _ => throw new InvalidDataException("Session display request is unsupported."),
        };

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
            DiagnosticTrace.TraceException(
                "OpenBlade.SessionAgent.SessionDisplayRequestDispatcher",
                "RefreshStateAfterFailure",
                exception);
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
