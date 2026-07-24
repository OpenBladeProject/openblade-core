using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed class ControlRequestHandler
{
    private readonly IServiceControlRuntime runtime;
    private readonly IDiagnosticArchiveProvider diagnosticArchiveProvider;
    private readonly IServiceShutdownSignal shutdownSignal;

    public ControlRequestHandler(
        IServiceControlRuntime runtime,
        IDiagnosticArchiveProvider diagnosticArchiveProvider)
        : this(runtime, diagnosticArchiveProvider, NullServiceShutdownSignal.Instance)
    {
    }

    public ControlRequestHandler(
        IServiceControlRuntime runtime,
        IDiagnosticArchiveProvider diagnosticArchiveProvider,
        IServiceShutdownSignal shutdownSignal)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.diagnosticArchiveProvider = diagnosticArchiveProvider
            ?? throw new ArgumentNullException(nameof(diagnosticArchiveProvider));
        this.shutdownSignal = shutdownSignal
            ?? throw new ArgumentNullException(nameof(shutdownSignal));
    }

    public async Task<ServiceResponse> HandleAsync(
        ServiceRequest request,
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

        return request switch
        {
            GetStatusRequest or SubscribeStatusRequest => Status(request.RequestId),
            ObserveStateRequest observe => await ObserveStatusAsync(
                observe.RequestId,
                observe.EffectiveLiveReadings,
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
            SetExternalLightingOverridePolicyRequest set => Result(
                request.RequestId,
                await runtime.SetExternalLightingOverridePolicyAsync(
                    set.Enabled,
                    cancellationToken)),
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
                    ambient.Frame,
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
        CancellationToken cancellationToken)
    {
        await runtime.ObserveAsync(requestedLiveReadings, cancellationToken);
        return Status(requestId);
    }

    private ServiceResponse Status(Guid requestId) => new(
        OpenBladeIpc.ProtocolVersion,
        requestId,
        Success: true,
        Error: null,
        Status: runtime.GetStatus(),
        Result: null,
        Archive: null);

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
}
