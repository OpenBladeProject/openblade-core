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

        if (request is ValidatePadlessHyperBoostLifecycleRequest validation
            && !PadlessHyperBoostLifecycleValidationBuildIdentity.ReadCurrent().Matches(
                validation.ExpectedSourceRevision,
                validation.ExpectedServiceAssemblySha256,
                validation.ExpectedAdmissionManifestSha256))
        {
            return Failure(
                request.RequestId,
                "The installed service is not the exact validation-marked build requested by FirmwareSmoke.");
        }

        ServiceRequestAccessDecision accessDecision = ServiceRequestAccessPolicy.Evaluate(
            request,
            client,
            sessionAuthority);
        if (accessDecision.Rejection is not null)
        {
            return Failure(request.RequestId, accessDecision.Rejection);
        }

        if (request is not ShutdownOpenBladeRequest
            && client.IsInteractive
            && accessDecision.Access == SessionControlAccess.Active)
        {
            try
            {
                await runtime.ActivateUserAsync(
                    client.UserSid,
                    client.SessionId,
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

        return request switch
        {
            GetStatusRequest or SubscribeStatusRequest => Status(
                request.RequestId,
                accessDecision.Access),
            ObserveStateRequest observe => await ObserveStatusAsync(
                observe.RequestId,
                observe.EffectiveLiveReadings,
                accessDecision.Access,
                cancellationToken),
            UpdateProfilesRequest update => Result(
                request.RequestId,
                await runtime.UpdateProfilesAsync(update.Profiles, cancellationToken)),
            SetPerformanceOverrideRequest set => Result(
                request.RequestId,
                await runtime.SetPerformanceConfigurationOverrideAsync(
                    set.Value,
                    cancellationToken)),
            SetHyperBoostRequest set => Result(
                request.RequestId,
                await runtime.SetHyperBoostAsync(set.Enabled, cancellationToken)),
            ValidatePadlessHyperBoostLifecycleRequest validate => Result(
                request.RequestId,
                await runtime.ValidatePadlessHyperBoostLifecycleAsync(
                    validate,
                    cancellationToken)),
            SetCoolingPadFanIntentRequest set => Result(
                request.RequestId,
                await runtime.SetCoolingPadFanIntentAsync(set.Intent, cancellationToken)),
            SetCoolingDemandLeaseRequest set => Result(
                request.RequestId,
                await runtime.SetCoolingDemandLeaseAsync(
                    client.UserSid.Value,
                    client.SessionId,
                    set.LeaseId,
                    set.Demand,
                    set.TtlMilliseconds,
                    cancellationToken)),
            ReleaseCoolingDemandLeaseRequest release => Result(
                request.RequestId,
                await runtime.ReleaseCoolingDemandLeaseAsync(
                    client.UserSid.Value,
                    client.SessionId,
                    release.LeaseId,
                    cancellationToken)),
            SetCoolingDemandApiEnabledRequest set => Result(
                request.RequestId,
                await runtime.SetCoolingDemandApiEnabledAsync(
                    set.Enabled,
                    cancellationToken)),
            SetCoolingPadLightingSyncRequest set => Result(
                request.RequestId,
                set.AcknowledgeRecovery
                    ? await runtime.AcknowledgeCoolingPadLightingRecoveryAsync(
                        cancellationToken)
                    : await runtime.SetCoolingPadLightingSyncAsync(
                        set.Enabled,
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
        SessionControlAccess access,
        CancellationToken cancellationToken)
    {
        await runtime.ObserveAsync(requestedLiveReadings, cancellationToken);
        return Status(requestId, access);
    }

    private ServiceResponse Status(Guid requestId, SessionControlAccess access) => new(
        OpenBladeIpc.ProtocolVersion,
        requestId,
        Success: true,
        Error: null,
        Status: ProjectStatus(runtime.GetStatus(), access),
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
            CoolingPad = null,
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
