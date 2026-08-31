using System.Text.Json;
using OpenBlade.Contracts;

namespace OpenBlade.Service;

public sealed class ControlRequestHandler
{
    private static readonly ProfileSet InactiveSessionProfiles =
        ProfileDefaults.CreateSafeDefaults();
    private readonly IServiceControlRuntime runtime;
    private readonly IDiagnosticArchiveProvider diagnosticArchiveProvider;
    private readonly IServiceShutdownSignal shutdownSignal;
    private readonly IInteractiveSessionAuthority sessionAuthority;

    internal ControlRequestHandler(
        IServiceControlRuntime runtime,
        IDiagnosticArchiveProvider diagnosticArchiveProvider)
        : this(
            runtime,
            diagnosticArchiveProvider,
            NullServiceShutdownSignal.Instance,
            new AllowAllInteractiveSessionAuthority())
    {
    }

    internal ControlRequestHandler(
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

    internal async Task<ServiceResponse> HandleAsync(
        ServiceRequest request,
        CancellationToken cancellationToken)
        => await HandleAsync(
            request,
            ServiceClientContext.TrustedTestClient,
            cancellationToken);

    public async Task<ServiceResponse> HandleAsync(
        ServiceRequest request,
        ServiceClientContext client,
        CancellationToken cancellationToken) => await HandleCoreAsync(
            request,
            client,
            activateUserConfiguration: true,
            cancellationToken);

    internal async Task<ServiceResponse> HandleProtocol51ReadOnlyAsync(
        ServiceRequest request,
        ServiceClientContext client,
        CancellationToken cancellationToken)
    {
        if (request is not (GetStatusRequest or SubscribeStatusRequest))
        {
            throw new ArgumentException(
                "Protocol 51 compatibility admits read-only status requests only.",
                nameof(request));
        }

        return await HandleCoreAsync(
            request,
            client,
            activateUserConfiguration: false,
            cancellationToken);
    }

    private async Task<ServiceResponse> HandleCoreAsync(
        ServiceRequest request,
        ServiceClientContext client,
        bool activateUserConfiguration,
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

        ServiceRequestAccessDecision accessDecision = ServiceRequestAccessPolicy.Evaluate(
            request,
            client,
            sessionAuthority);
        if (accessDecision.Rejection is not null)
        {
            return Failure(request.RequestId, accessDecision.Rejection);
        }

        if (activateUserConfiguration
            && request is not ShutdownOpenBladeRequest
            && client.IsInteractive
            && accessDecision.Access == SessionControlAccess.Active)
        {
            try
            {
                await runtime.ActivateUserAsync(
                    client.UserSid,
                    () => ServiceRequestAccessPolicy.GetAccess(client, sessionAuthority)
                        == SessionControlAccess.Active,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is
                InvalidDataException or IOException or JsonException or UnauthorizedAccessException)
            {
                return Failure(
                    request.RequestId,
                    $"The active user's configuration could not be loaded: {exception.Message}");
            }
        }

        Func<bool> stillControlsSession = () =>
            ServiceRequestAccessPolicy.GetAccess(client, sessionAuthority)
                == SessionControlAccess.Active;
        Func<bool> stillControlsActiveLocalSession = () =>
            ServiceRequestAccessPolicy.IsActiveLocalInteractiveClient(
                client,
                sessionAuthority);

        return request switch
        {
            GetStatusRequest or SubscribeStatusRequest => Status(
                request.RequestId,
                client),
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
                    stillControlsSession,
                    cancellationToken)),
            SetHyperBoostRequest set => Result(
                request.RequestId,
                await runtime.SetHyperBoostAsync(set, cancellationToken)),
            ValidatePadlessHyperBoostLifecycleRequest validate => Result(
                request.RequestId,
                await runtime.ValidatePadlessHyperBoostLifecycleAsync(
                    validate,
                    cancellationToken)),
            CyclePerformanceShortcutRequest cycle => Result(
                request.RequestId,
                await runtime.CyclePerformanceShortcutAsync(
                    cycle.Source,
                    stillControlsSession,
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
                    stillControlsSession,
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
            SetDisplayBrightnessHidUsageRequest set => Result(
                request.RequestId,
                await runtime.SetDisplayBrightnessHidUsageAsync(
                    set.Usage,
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
            SetLightingRefreshRateRequest set => Result(
                request.RequestId,
                await runtime.SetLightingRefreshRateAsync(
                    set.RefreshRate,
                    cancellationToken)),
            SetFirmwareCompatibilityOverrideRequest set => Result(
                request.RequestId,
                await runtime.SetFirmwareCompatibilityOverrideAsync(
                    set.Enabled,
                    cancellationToken)),
            SetThermalControlPolicyRequest set => Result(
                request.RequestId,
                await runtime.SetThermalControlPolicyAsync(
                    set.Policy,
                    stillControlsActiveLocalSession,
                    cancellationToken)),
            AcknowledgeThermalPolicyUpgradeDisclosureRequest => Result(
                request.RequestId,
                await runtime.AcknowledgeThermalPolicyUpgradeDisclosureAsync(
                    stillControlsActiveLocalSession,
                    cancellationToken)),
            ReapplyChangedSettingsRequest => Result(
                request.RequestId,
                await runtime.ReapplyChangedSettingsAsync(
                    stillControlsSession,
                    cancellationToken)),
            RecoverHyperBoostRestoreRequest => Result(
                request.RequestId,
                await runtime.RecoverHyperBoostRestoreAsync(cancellationToken)),
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

    internal IDisposable RegisterProtocolCompatibilityClient() =>
        runtime.RegisterProtocolCompatibilityClient();

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

    internal ServiceStatusSnapshot ProjectStatus(
        ServiceStatusSnapshot snapshot,
        ServiceClientContext client)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return ProjectStatus(
            snapshot,
            ServiceRequestAccessPolicy.GetAccess(client, sessionAuthority));
    }

    private static ServiceStatusSnapshot ProjectStatus(
        ServiceStatusSnapshot snapshot,
        SessionControlAccess access)
    {
        if (access == SessionControlAccess.Active)
        {
            return snapshot with { ControlAccess = access };
        }

        var inactiveAutomation = new ProfileAutomationState(
            InactiveSessionProfiles,
            new BatteryAutomationPreferences(),
            snapshot.ActivePowerSource);
        ConfirmedDeviceState confirmed = snapshot.ConfirmedState
            ?? new ConfirmedDeviceState(null, null, null, null);
        return snapshot with
        {
            Message = access == SessionControlAccess.InactiveSession
                ? "Another Windows session currently controls OpenBlade hardware."
                : "OpenBlade accepts requests only from an interactive Windows session.",
            Profiles = InactiveSessionProfiles,
            DesiredState = inactiveAutomation.Resolve(confirmed),
            PerformanceOverride = null,
            FanOverride = null,
            FanControlOverride = null,
            LightingOverride = null,
            FullChargeOverride = false,
            AlwaysOverrideExternalLightingChanges = false,
            LightingRefreshRate = LightingRefreshRate.ThirtyHz,
            AllowUntestedFirmware = false,
            KeyboardLightingControlMode = KeyboardLightingControlMode.OpenBlade,
            PerformanceShortcutPreview = null,
            ThermalConvergence = null,
            ControlAccess = access,
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
