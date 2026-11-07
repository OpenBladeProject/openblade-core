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
    private readonly ServiceSessionPowerTransitionCoordinator? powerTransitions;
    // The singleton handler owns this managed-only semaphore for the service
    // lifetime; AvailableWaitHandle is never requested. Keep activation and
    // finite dispatch together so another client cannot replace their stores.
    private readonly SemaphoreSlim userRequestGate = new(1, 1);
    private ServiceUserRequestOwner? userRequestOwner;

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
        IInteractiveSessionAuthority sessionAuthority,
        ServiceSessionPowerTransitionCoordinator? powerTransitions = null)
    {
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        this.diagnosticArchiveProvider = diagnosticArchiveProvider
            ?? throw new ArgumentNullException(nameof(diagnosticArchiveProvider));
        this.shutdownSignal = shutdownSignal
            ?? throw new ArgumentNullException(nameof(shutdownSignal));
        this.sessionAuthority = sessionAuthority
            ?? throw new ArgumentNullException(nameof(sessionAuthority));
        this.powerTransitions = powerTransitions;
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
        Func<bool> stillControlsSession = () =>
            ServiceRequestAccessPolicy.GetAccess(client, sessionAuthority)
                == SessionControlAccess.Active;

        if (request is ReportSessionPowerTransitionRequest transition)
        {
            ServiceRequestAccessDecision transitionAccess = ServiceRequestAccessPolicy.Evaluate(
                request, client, sessionAuthority);
            if (transitionAccess.Rejection is not null)
            {
                return Failure(request.RequestId, transitionAccess.Rejection);
            }
            // Lifecycle ingress must latch admission before activation can load
            // a profile and perform unrelated hardware work.
            return powerTransitions is null
                ? Failure(request.RequestId, "Session power notification handling is unavailable.")
                : Result(request.RequestId, await powerTransitions.ReportAsync(
                    transition, client,
                    () => ServiceRequestAccessPolicy.IsActiveLocalInteractiveClient(
                        client, sessionAuthority),
                    cancellationToken).ConfigureAwait(false));
        }

        // The compatibility entry point admits status reads only and never
        // activates user configuration. Its cached view needs no dispatch gate.
        if (!activateUserConfiguration)
        {
            return await HandleUserRequestAsync(
                request, client, activateUserConfiguration, stillControlsSession, cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();

        // A completed current-user context can return its immutable cached
        // snapshot while a finite mutation or export is busy. A handoff or
        // deferred recovery still takes the guarded activation path below.
        if (request is GetStatusRequest or SubscribeStatusRequest
            && client.IsInteractive
            && runtime is IServiceUserAdmission statusAdmission
            && statusAdmission.TryReadCurrentUserStatus(client.UserSid, stillControlsSession, out ServiceStatusSnapshot? cached))
        {
            return Status(request.RequestId, client, cached);
        }

        // Preserve the runtime's early different-user barrier while another
        // finite request owns the dispatch gate. Cancellation releases the
        // pending count but never restores an older thermal authority epoch.
        using IDisposable? pendingAdmission = activateUserConfiguration
            && request is not ShutdownOpenBladeRequest
            && client.IsInteractive
            && runtime is IServiceUserAdmission userAdmission
                ? userAdmission.BeginUserAdmission(client, stillControlsSession, Volatile.Read(ref userRequestOwner))
                : null;
        await userRequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Release may win SemaphoreSlim's cancellation race. Observe the
            // canceled request before dispatch, while finally still owns the permit.
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref userRequestOwner,
                client.IsInteractive && request is not ShutdownOpenBladeRequest
                    ? new ServiceUserRequestOwner(client, stillControlsSession)
                    : null);
            // The prior request has finished. Activation below takes its own
            // admission before its first wait, so its writes do not see this
            // request's ingress lease as a competing pending user.
            pendingAdmission?.Dispose();
            return await HandleUserRequestAsync(
                request, client, activateUserConfiguration, stillControlsSession, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref userRequestOwner, null);
            userRequestGate.Release();
        }
    }

    private async Task<ServiceResponse> HandleUserRequestAsync(
        ServiceRequest request,
        ServiceClientContext client,
        bool activateUserConfiguration,
        Func<bool> stillControlsSession,
        CancellationToken cancellationToken)
    {
        ServiceRequestAccessDecision accessDecision = ServiceRequestAccessPolicy.Evaluate(
            request, client, sessionAuthority);
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
                    stillControlsSession,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ConfigurationQuarantinedException exception)
            {
                DiagnosticTrace.TraceException(nameof(ControlRequestHandler), nameof(HandleCoreAsync), exception,
                    "Active-user configuration transaction recovery is required.");
                return Failure(request.RequestId,
                    $"The active user's configuration requires recovery: {exception.Message}");
            }
            catch (Exception exception) when (exception is
                InvalidDataException or IOException or JsonException or UnauthorizedAccessException)
            {
                DiagnosticTrace.TraceException(nameof(ControlRequestHandler), nameof(HandleCoreAsync), exception,
                    "Active-user configuration load failed.");
                return Failure(
                    request.RequestId,
                    $"The active user's configuration could not be loaded: {exception.Message}");
            }
        }

        // Loading and applying a user's profile can await device or storage
        // work. Refuse stale mutations before dispatching through that context.
        // Status projects access freshly itself, including inactive read-only
        // views. Mutations need the same check before their runtime dispatch.
        if (request is not (GetStatusRequest or SubscribeStatusRequest)
            && ServiceRequestAccessPolicy.Evaluate(request, client, sessionAuthority).Rejection is not null)
        {
            return Result(request.RequestId, new ControlOperationResult(
                ControlOperationStatus.Blocked,
                "The active Windows session changed before the request could be dispatched."));
        }

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
            ReportApplicationContextRequest report => Result(
                request.RequestId,
                await runtime.ReportApplicationContextAsync(
                    report,
                    stillControlsActiveLocalSession,
                    cancellationToken)),
            SetPerformanceOverrideRequest set => Result(
                request.RequestId,
                await runtime.SetPerformanceConfigurationOverrideAsync(
                    set.Value,
                    stillControlsSession,
                    cancellationToken)),
            SetHyperBoostRequest set => Result(
                request.RequestId,
                await runtime.SetHyperBoostAsync(set, cancellationToken)),
            SetGpuClockTuningRequest set => Result(
                request.RequestId,
                await runtime.SetGpuClockTuningAsync(
                    set.Value,
                    set.Lifetime,
                    stillControlsSession,
                    cancellationToken)),
            ObserveCpuTuningRequest observe => Result(request.RequestId,
                await runtime.ObserveCpuTuningAsync(observe.UseInstalledBackend,
                    stillControlsSession, cancellationToken)),
            SetCpuTuningRequest set => Result(request.RequestId,
                await runtime.SetCpuTuningAsync(set.Target, set.Settings,
                    stillControlsSession, cancellationToken)),
            RestoreCpuTuningRequest restore => Result(request.RequestId,
                await runtime.RestoreCpuTuningAsync(restore.Target,
                    stillControlsSession, cancellationToken)),
            ValidatePadlessHyperBoostLifecycleRequest validate => Result(
                request.RequestId,
                await runtime.ValidatePadlessHyperBoostLifecycleAsync(
                    validate,
                    cancellationToken)),
            ValidateThermalExternalDriftRequest validate => Result(
                request.RequestId,
                await runtime.ValidateThermalExternalDriftAsync(
                    validate,
                    stillControlsSession,
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
            SetExperimentalFeaturesRequest set => Result(
                request.RequestId,
                await runtime.SetExperimentalFeaturesAsync(
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
            ResumeExperimentalWritesRequest => Result(
                request.RequestId,
                await runtime.ResumeExperimentalWritesAsync(stillControlsActiveLocalSession, cancellationToken)),
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
            PushUniformLightingFrameRequest uniform => Result(
                request.RequestId,
                await runtime.PushUniformLightingFrameAsync(
                    uniform.Frame,
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

    private ServiceResponse Status(Guid requestId, ServiceClientContext client,
        ServiceStatusSnapshot? snapshot = null) => new(
        OpenBladeIpc.ProtocolVersion,
        requestId,
        Success: true,
        Error: null,
        Status: ProjectStatus(snapshot ?? runtime.GetStatus(), client),
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
            AllowExperimentalFeatures = snapshot.IsExperimentalDevice,
            ExperimentalWritesPaused = snapshot.ExperimentalWritesPaused,
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
            DiagnosticTrace.TraceException(nameof(ControlRequestHandler), nameof(ExportDiagnosticsAsync), exception);
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
