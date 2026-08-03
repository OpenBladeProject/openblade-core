using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed class ControlRequestHandler
{
    private readonly IServiceControlRuntime runtime;
    private readonly IDiagnosticArchiveProvider diagnosticArchiveProvider;
    private readonly IServiceShutdownSignal shutdownSignal;
    private readonly IInteractiveSessionAuthority sessionAuthority;

    public ControlRequestHandler(
        IServiceControlRuntime runtime,
        IDiagnosticArchiveProvider diagnosticArchiveProvider)
        : this(
            runtime,
            diagnosticArchiveProvider,
            NullServiceShutdownSignal.Instance,
            new AllowAllInteractiveSessionAuthority())
    {
    }

    public ControlRequestHandler(
        IServiceControlRuntime runtime,
        IDiagnosticArchiveProvider diagnosticArchiveProvider,
        IServiceShutdownSignal shutdownSignal)
        : this(
            runtime,
            diagnosticArchiveProvider,
            shutdownSignal,
            new AllowAllInteractiveSessionAuthority())
    {
    }

    public ControlRequestHandler(
        IServiceControlRuntime runtime,
        IDiagnosticArchiveProvider diagnosticArchiveProvider,
        IServiceShutdownSignal shutdownSignal,
        IInteractiveSessionAuthority sessionAuthority)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.diagnosticArchiveProvider = diagnosticArchiveProvider
            ?? throw new ArgumentNullException(nameof(diagnosticArchiveProvider));
        this.shutdownSignal = shutdownSignal
            ?? throw new ArgumentNullException(nameof(shutdownSignal));
        this.sessionAuthority = sessionAuthority
            ?? throw new ArgumentNullException(nameof(sessionAuthority));
    }

    public async Task<ServiceResponse> HandleAsync(
        ServiceRequest request,
        CancellationToken cancellationToken)
        => await HandleAsync(
            request,
            ServiceClientContext.TrustedTestClient,
            cancellationToken);

    public async Task<ServiceResponse> HandleAsync(
        ServiceRequest request,
        ServiceClientContext client,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Failure(Guid.Empty, "IPC request is missing.");
        }

        if (request.Version != OpenBladeIpc.ProtocolVersion)
        {
            return Failure(request.RequestId, "Unsupported IPC protocol version.");
        }

        if (request.RequestId == Guid.Empty)
        {
            return Failure(Guid.Empty, "IPC request ID is missing.");
        }

        string? rejection = ServiceRequestAccessPolicy.GetRejection(
            request,
            client,
            sessionAuthority);
        if (rejection is not null)
        {
            return Failure(request.RequestId, rejection);
        }

        if (client.IsInteractive
            && ServiceRequestAccessPolicy.GetAccess(client, sessionAuthority)
                == SessionControlAccess.Active)
        {
            await runtime.ActivateUserAsync(client.UserSid, cancellationToken);
        }

        return request switch
        {
            GetStatusRequest or SubscribeStatusRequest => Status(request.RequestId, client),
            ObserveStateRequest observe => await ObserveStatusAsync(
                observe.RequestId,
                observe.EffectiveLiveReadings,
                client,
                cancellationToken),
            UpdateProfilesRequest update => Result(
                request.RequestId,
                await runtime.UpdateProfilesAsync(update.Profiles, cancellationToken)),
            SetPerformanceOverrideRequest set => Result(
                request.RequestId,
                await runtime.SetPerformanceConfigurationOverrideAsync(
                    set.Value,
                    cancellationToken)),
            CyclePerformanceShortcutRequest cycle => Result(
                request.RequestId,
                await runtime.CyclePerformanceShortcutAsync(
                    cycle.Source,
                    cancellationToken)),
            SetPerformanceShortcutPreviewRequest preview => Result(
                request.RequestId,
                await runtime.SetPerformanceShortcutPreviewAsync(
                    preview.SessionId,
                    preview.Generation,
                    preview.Preset,
                    cancellationToken)),
            SetFanOverrideRequest set => Result(
                request.RequestId,
                await runtime.SetFanControlOverrideAsync(
                    set.Value,
                    set.Settings,
                    cancellationToken)),
            SetLightingOverrideRequest set => Result(
                request.RequestId,
                await runtime.SetLightingOverrideAsync(set.Value, cancellationToken)),
            StepKeyboardBrightnessRequest step => Result(
                request.RequestId,
                await runtime.StepKeyboardBrightnessAsync(
                    step.Direction,
                    step.Lifetime,
                    cancellationToken)),
            SetKeyboardLightingControlModeRequest set => Result(
                request.RequestId,
                await runtime.SetKeyboardLightingControlModeAsync(
                    set.Mode,
                    cancellationToken)),
            SetExternalLightingOverridePolicyRequest set => Result(
                request.RequestId,
                await runtime.SetExternalLightingOverridePolicyAsync(
                    set.Enabled,
                    cancellationToken)),
            ReapplyChangedSettingsRequest => Result(
                request.RequestId,
                await runtime.ReapplyChangedSettingsAsync(cancellationToken)),
            SetLightingSuppressionRequest set => Result(
                request.RequestId,
                await runtime.SetLightingSuppressionAsync(
                    set.Reason,
                    set.Suppressed,
                    cancellationToken)),
            PulseReactiveKeyRequest pulse => Result(
                request.RequestId,
                await runtime.PulseReactiveKeyAsync(pulse.Pulse, cancellationToken)),
            PushAmbientLightingFrameRequest ambient => Result(
                request.RequestId,
                await runtime.PushAmbientLightingFrameAsync(
                    AmbientLightingFrame.FromRgbBytes(ambient.RgbBytes),
                    cancellationToken)),
            SetPlaybackMuteIndicatorRequest mute => Result(
                request.RequestId,
                await runtime.SetPlaybackMuteIndicatorAsync(
                    mute.Muted,
                    cancellationToken)),
            SetBatteryPolicyRequest set => Result(
                request.RequestId,
                await runtime.SetBatteryPolicyAsync(set.Value, cancellationToken)),
            SetBatteryProtectionRequest set => Result(
                request.RequestId,
                await runtime.SetBatteryProtectionAsync(set.Enabled, cancellationToken)),
            SetFullChargeOverrideRequest set => Result(
                request.RequestId,
                await runtime.SetFullChargeOverrideAsync(set.Enabled, cancellationToken)),
            ApplyOneShotLaptopControlRequest apply => Result(
                request.RequestId,
                await runtime.ApplyOneShotLaptopControlAsync(
                    apply.Value,
                    cancellationToken)),
            ExportDiagnosticsRequest export => await ExportDiagnosticsAsync(
                export.RequestId,
                export.LiveReadingsEnabled,
                cancellationToken),
            ShutdownOpenBladeRequest => RequestShutdown(request.RequestId),
            _ => Failure(request.RequestId, "IPC request operation is unsupported."),
        };
    }

    private ServiceResponse RequestShutdown(Guid requestId)
    {
        shutdownSignal.RequestShutdownAfterResponse();
        return Result(
            requestId,
            new ControlOperationResult(
                ControlOperationStatus.Stored,
                "OpenBlade is shutting down."));
    }

    private async Task<ServiceResponse> ObserveStatusAsync(
        Guid requestId,
        LiveReadingSensors requestedLiveReadings,
        ServiceClientContext client,
        CancellationToken cancellationToken)
    {
        await runtime.ObserveAsync(requestedLiveReadings, cancellationToken);
        return Status(requestId, client);
    }

    private ServiceResponse Status(Guid requestId, ServiceClientContext client) => new(
        OpenBladeIpc.ProtocolVersion,
        requestId,
        Success: true,
        Error: null,
        Status: ProjectStatus(runtime.GetStatus(), client),
        Result: null,
        Archive: null);

    public ServiceStatusSnapshot ProjectStatus(
        ServiceStatusSnapshot snapshot,
        ServiceClientContext client)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot with
        {
            ControlAccess = ServiceRequestAccessPolicy.GetAccess(client, sessionAuthority),
        };
    }

    private static ServiceResponse Result(Guid requestId, ControlOperationResult result) => new(
        OpenBladeIpc.ProtocolVersion,
        requestId,
        Success: true,
        Error: null,
        Status: null,
        Result: result,
        Archive: null);

    private static ServiceResponse Archive(Guid requestId, DiagnosticArchive archive) => new(
        OpenBladeIpc.ProtocolVersion,
        requestId,
        Success: true,
        Error: null,
        Status: null,
        Result: null,
        Archive: archive);

    private async Task<ServiceResponse> ExportDiagnosticsAsync(
        Guid requestId,
        bool liveReadingsEnabled,
        CancellationToken cancellationToken)
    {
        try
        {
            DiagnosticArchive archive = await diagnosticArchiveProvider
                .CreateAsync(liveReadingsEnabled, cancellationToken);
            return Archive(requestId, archive);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return Failure(requestId, $"Diagnostic export failed: {exception.Message}");
        }
    }

    private static ServiceResponse Failure(Guid requestId, string error) => new(
        OpenBladeIpc.ProtocolVersion,
        requestId,
        Success: false,
        Error: error,
        Status: null,
        Result: null,
        Archive: null);

    private sealed class AllowAllInteractiveSessionAuthority : IInteractiveSessionAuthority
    {
        public int? GetControlSessionId() =>
            ServiceClientContext.TrustedTestClient.SessionId;
    }
}
