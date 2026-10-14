using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

[assembly: InternalsVisibleTo("OpenBlade.Service.Tests")]

namespace OpenBlade.Service;

public sealed class WindowsVerifiedBladeDeviceProvider :
    IVerifiedBladeDeviceProvider,
    IGpuClockOffsetConfirmationInvalidator,
    IDisposable
{
    private static readonly TimeSpan RecentReadyProbeGracePeriod =
        TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultShutdownBudget = TimeSpan.FromSeconds(5);

    internal static Func<BatteryValidationProbeResult> ProductionProbeFactory { get; } =
        BatteryValidationFactory.ProbeWindowsForService;

    internal static Func<IProductionBatteryPortFactory> ProductionPortFactory { get; } =
        ServiceComposition.CreateProductionBatteryFactory;

    internal static Func<IFanRpmQueryRunFactory> ProductionFanRpmFactory { get; } =
        BatteryValidationFactory.CreateWindowsFanRpmForService;

    internal static Func<IProductionLightingPortFactory> ProductionLightingPortFactory { get; } =
        ServiceComposition.CreateProductionLightingFactory;

    private readonly Func<BatteryValidationProbeResult> probe;
    private readonly ExperimentalBladeFactory? experimentalFactory;
    private readonly FirmwareCompatibilityPolicy compatibilityPolicy;
    private readonly SerializedBladeDevice device;
    private readonly object providerLifecycleSync = new();
    private DiagnosticDeviceVerification verificationDiagnostics =
        DiagnosticDeviceVerification.NotObserved;
    private long lastReadyProbeTimestamp;
    private int firmwareCompatibility;
    private int requiresUntestedFirmwareOverride;
    private int disposed;

    public WindowsVerifiedBladeDeviceProvider()
        : this(
            ProductionProbeFactory,
            ProductionPortFactory(),
            ProductionFanRpmFactory(),
            ProductionLightingPortFactory(),
            new NvidiaGpuClockOffsetController(),
            gpuActivitySource: new WindowsGpuActivitySource())
    {
    }

    internal WindowsVerifiedBladeDeviceProvider(
        Func<BatteryValidationProbeResult> probe,
        IProductionBatteryPortFactory batteryFactory,
        IFanRpmQueryRunFactory fanRpmFactory,
        IProductionLightingPortFactory? lightingFactory = null,
        IGpuClockOffsetController? gpuClockOffsets = null,
        ILogger<WindowsVerifiedBladeDeviceProvider>? logger = null,
        TimeSpan? shutdownBudget = null,
        Action? onClosing = null,
        FirmwareCompatibilityPolicy? compatibilityPolicy = null,
        IGpuActivitySource? gpuActivitySource = null,
        Func<TimeSpan, CancellationToken, Task>? preWriteRetryDelay = null,
        LowPowerGate? lowPowerGate = null)
    {
        TimeSpan effectiveShutdownBudget = shutdownBudget ?? DefaultShutdownBudget;
        if (effectiveShutdownBudget <= TimeSpan.Zero ||
            effectiveShutdownBudget.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(shutdownBudget));
        }

        this.probe = probe ?? throw new ArgumentNullException(nameof(probe));
        experimentalFactory = batteryFactory as ExperimentalBladeFactory;
        this.compatibilityPolicy = compatibilityPolicy
            ?? new FirmwareCompatibilityPolicy();
        device = new SerializedBladeDevice(
            batteryFactory ?? throw new ArgumentNullException(nameof(batteryFactory)),
            fanRpmFactory ?? throw new ArgumentNullException(nameof(fanRpmFactory)),
            lightingFactory ?? UnavailableProductionLightingFactory.Instance,
            gpuClockOffsets ?? UnavailableGpuClockOffsetController.Instance,
            gpuActivitySource ?? InactiveGpuActivitySource.Instance,
            logger,
            effectiveShutdownBudget,
            onClosing,
            preWriteRetryDelay ?? Task.Delay,
            lowPowerGate);
    }

    public void SetAllowUntestedFirmware(bool enabled)
    {
        compatibilityPolicy.AllowUntestedFirmware = enabled;
        if (!enabled && RequiresUntestedFirmwareOverride)
        {
            Volatile.Write(
                ref firmwareCompatibility,
                (int)FirmwareCompatibilityStatus.UntestedBlocked);
        }
    }

    public bool IsExperimentalDevice => experimentalFactory is not null;

    public string? ExperimentalSupportSource => experimentalFactory?.EvidenceSource;

    public void SetAllowExperimentalFeatures(bool enabled) => experimentalFactory?.SetEnabled(enabled);

    public DiagnosticExperimentalSupport GetExperimentalSupportDiagnostics() =>
        experimentalFactory?.GetDiagnostics() ?? DiagnosticExperimentalSupport.NotDetected;

    public void SetLightingRefreshRate(LightingRefreshRate refreshRate) =>
        device.SetDesiredLightingRefreshRate(refreshRate);

    public FirmwareCompatibilityStatus FirmwareCompatibility =>
        (FirmwareCompatibilityStatus)Volatile.Read(ref firmwareCompatibility);

    public bool RequiresUntestedFirmwareOverride =>
        Volatile.Read(ref requiresUntestedFirmwareOverride) != 0;

    public DiagnosticDeviceVerification GetCachedDeviceVerificationDiagnostics() =>
        Volatile.Read(ref verificationDiagnostics);

    public bool TryGetVerifiedDevice(out IBladeDevice verifiedDevice)
    {
        verifiedDevice = null!;
        if (Volatile.Read(ref disposed) != 0)
        {
            return false;
        }

        try
        {
            BatteryValidationProbeResult result = probe();
            UpdateFirmwareCompatibility(
                MapFirmwareCompatibility(result.FirmwareCompatibility));
            BatteryValidationProbeClassification classification = result.Classification;
            if (!IsReadyClassification(classification))
            {
                bool usedRecentlyVerifiedDevice =
                    IsTransientProbeClassification(classification)
                    && TryGetRecentlyVerifiedDevice(out verifiedDevice);
                RecordVerificationAttempt(DiagnosticDeviceVerificationAttempt.FromProbe(
                    result,
                    usedRecentlyVerifiedDevice));
                if (usedRecentlyVerifiedDevice)
                {
                    return true;
                }

                Volatile.Write(ref lastReadyProbeTimestamp, 0);
                return false;
            }

            lock (providerLifecycleSync)
            {
                RecordVerificationAttempt(DiagnosticDeviceVerificationAttempt.FromProbe(
                    result,
                    usedRecentlyVerifiedDevice: false));
                if (disposed != 0)
                {
                    return false;
                }

                Volatile.Write(
                    ref lastReadyProbeTimestamp,
                    Stopwatch.GetTimestamp());
                verifiedDevice = device;
                return true;
            }
        }
        catch (Exception exception)
        {
            bool usedRecentlyVerifiedDevice = TryGetRecentlyVerifiedDevice(out verifiedDevice);
            RecordVerificationAttempt(new DiagnosticDeviceVerificationAttempt(
                DateTimeOffset.UtcNow,
                "Probe",
                "ProbeException",
                "The device verification probe threw an exception before returning a result.",
                ProbeResult: null,
                usedRecentlyVerifiedDevice,
                exception.GetType().Name,
                exception.HResult));
            return usedRecentlyVerifiedDevice;
        }
    }

    private void RecordVerificationAttempt(DiagnosticDeviceVerificationAttempt attempt)
    {
        lock (providerLifecycleSync)
        {
            DiagnosticDeviceVerification previous = verificationDiagnostics;
            bool ready = attempt.ProbeResult is { } result
                && IsReadyClassification(result.Classification);
            Volatile.Write(ref verificationDiagnostics, new DiagnosticDeviceVerification(
                attempt,
                ready ? previous.LastFailure : attempt));
        }
    }

    public bool TryGetRecentlyVerifiedReadOnlyDevice(out IBladeDevice verifiedDevice) =>
        TryGetRecentlyVerifiedDevice(out verifiedDevice)
            || TryGetVerifiedDevice(out verifiedDevice);

    private bool TryGetRecentlyVerifiedDevice(out IBladeDevice verifiedDevice)
    {
        verifiedDevice = null!;
        long timestamp = Volatile.Read(ref lastReadyProbeTimestamp);
        if (timestamp == 0
            || Stopwatch.GetElapsedTime(timestamp) > RecentReadyProbeGracePeriod)
        {
            return false;
        }

        lock (providerLifecycleSync)
        {
            if (disposed != 0
                || Volatile.Read(ref lastReadyProbeTimestamp) != timestamp)
            {
                return false;
            }

            verifiedDevice = device;
            return true;
        }
    }

    private static bool IsReadyClassification(
        BatteryValidationProbeClassification classification) =>
        classification is
            BatteryValidationProbeClassification.Ready
            or BatteryValidationProbeClassification.ReadyWithAccessFallback;

    private static bool IsTransientProbeClassification(
        BatteryValidationProbeClassification classification) =>
        classification is
            BatteryValidationProbeClassification.EnumerationFailed
            or BatteryValidationProbeClassification.CandidateOpenFailed;

    private static FirmwareCompatibilityStatus MapFirmwareCompatibility(
        BatteryValidationFirmwareCompatibility compatibility) => compatibility switch
        {
            BatteryValidationFirmwareCompatibility.Tested =>
                FirmwareCompatibilityStatus.Tested,
            BatteryValidationFirmwareCompatibility.UntestedBlocked =>
                FirmwareCompatibilityStatus.UntestedBlocked,
            BatteryValidationFirmwareCompatibility.UntestedAllowed =>
                FirmwareCompatibilityStatus.UntestedAllowed,
            BatteryValidationFirmwareCompatibility.ModelScoped =>
                FirmwareCompatibilityStatus.ModelScoped,
            _ => FirmwareCompatibilityStatus.Unknown,
        };

    private void UpdateFirmwareCompatibility(FirmwareCompatibilityStatus compatibility)
    {
        // Unknown fails closed for renderer reuse, but it must not erase a
        // previously confirmed override dependency. Otherwise a transient
        // probe failure immediately before disabling could skip renderer cleanup.
        if (compatibility is FirmwareCompatibilityStatus.Tested
            or FirmwareCompatibilityStatus.ModelScoped)
        {
            Volatile.Write(ref requiresUntestedFirmwareOverride, 0);
        }
        else if (compatibility is FirmwareCompatibilityStatus.UntestedBlocked
            or FirmwareCompatibilityStatus.UntestedAllowed)
        {
            Volatile.Write(ref requiresUntestedFirmwareOverride, 1);
        }

        Volatile.Write(ref firmwareCompatibility, (int)compatibility);
    }

    private bool IsOwnedRendererFirmwareAccessAllowed()
    {
        FirmwareCompatibilityStatus compatibility = FirmwareCompatibility;
        return compatibility is FirmwareCompatibilityStatus.Tested
            or FirmwareCompatibilityStatus.ModelScoped
            || compatibility == FirmwareCompatibilityStatus.UntestedAllowed
                && compatibilityPolicy.AllowUntestedFirmware;
    }

    public bool TryGetOwnedKeyTriggeredLightingDevice(out IBladeDevice verifiedDevice)
    {
        verifiedDevice = null!;
        if (Volatile.Read(ref disposed) != 0
            || !IsOwnedRendererFirmwareAccessAllowed()
            || !device.HasOwnedKeyTriggeredRenderer)
        {
            return false;
        }

        lock (providerLifecycleSync)
        {
            if (disposed != 0
                || !IsOwnedRendererFirmwareAccessAllowed()
                || !device.HasOwnedKeyTriggeredRenderer)
            {
                return false;
            }

            // The renderer already owns an exact-device session admitted by the
            // normal verified lighting path. A pulse opens no handle and submits
            // no command, so re-enumerating HID for every key only adds latency.
            verifiedDevice = device;
            return true;
        }
    }

    public bool TryGetOwnedAmbientLightingDevice(out IBladeDevice verifiedDevice)
    {
        verifiedDevice = null!;
        if (Volatile.Read(ref disposed) != 0
            || !IsOwnedRendererFirmwareAccessAllowed()
            || !device.HasOwnedAmbientRenderer)
        {
            return false;
        }

        lock (providerLifecycleSync)
        {
            if (disposed != 0
                || !IsOwnedRendererFirmwareAccessAllowed()
                || !device.HasOwnedAmbientRenderer)
            {
                return false;
            }

            // Ambient Awareness already owns an exact-device lighting port.
            // Reusing it avoids a full HID discovery pass for every video frame.
            verifiedDevice = device;
            return true;
        }
    }

    public Task<LightingRendererInvalidationResult> InvalidateLightingRendererAsync(
        CancellationToken cancellationToken) =>
        Volatile.Read(ref disposed) != 0
            ? Task.FromResult(new LightingRendererInvalidationResult(
                LightingRendererOwnershipState.None,
                DriverModeRestoreOutcome.NotOwned))
            : device.InvalidateLightingRendererAsync(cancellationToken);

    public Task<LightingRendererInvalidationResult> InvalidateLightingRendererForLowPowerAsync(
        CancellationToken cancellationToken) =>
        device.InvalidateLightingRendererAsync(cancellationToken, lowPower: true);

    public Task<LightingRendererInvalidationResult> InvalidateLightingRendererForLowPowerAsync(
        Func<bool> stillRetiring,
        CancellationToken cancellationToken) =>
        device.InvalidateLightingRendererAsync(cancellationToken, lowPower: true, stillRetiring);

    public Task<LightingLowPowerOffResult?> TurnOffRetainedLightingForLowPowerAsync(
        Func<bool> stillSuspending,
        CancellationToken cancellationToken) =>
        device.TurnOffRetainedLightingForLowPowerAsync(
            () => Volatile.Read(ref disposed) == 0 && stillSuspending()
                && FreshLightingCleanupAdmission(),
            stillSuspending,
            cancellationToken);

    public Task<bool?> ReleaseRetainedDriverModeForLowPowerAsync(
        Func<bool> stillSuspending,
        CancellationToken cancellationToken) =>
        device.ReleaseRetainedDriverModeForLowPowerAsync(
            () => Volatile.Read(ref disposed) == 0 && stillSuspending()
                && FreshLightingCleanupAdmission(),
            stillSuspending,
            cancellationToken);

    private bool FreshLightingCleanupAdmission()
    {
        try
        {
            BatteryValidationProbeResult result = probe();
            UpdateFirmwareCompatibility(MapFirmwareCompatibility(result.FirmwareCompatibility));
            // Presentation grace does not authorize a new lifecycle write.
            return IsReadyClassification(result.Classification)
                && IsOwnedRendererFirmwareAccessAllowed();
        }
        catch
        {
            return false;
        }
    }

    public Task<LightingRendererOwnershipState> RelinquishLightingRendererAsync(
        CancellationToken cancellationToken) =>
        Volatile.Read(ref disposed) != 0
            ? Task.FromResult(LightingRendererOwnershipState.None)
            : device.RelinquishLightingRendererAsync(cancellationToken);

    Task<bool> IGpuClockOffsetConfirmationInvalidator.InvalidateGpuClockOffsetConfirmationAsync(
        CancellationToken cancellationToken) =>
        device.InvalidateGpuClockOffsetConfirmationAsync(cancellationToken);

    public void Dispose()
    {
        lock (providerLifecycleSync)
        {
            if (disposed != 0)
            {
                return;
            }

            Volatile.Write(ref disposed, 1);
            device.Dispose();
        }
    }

    private sealed class SerializedBladeDevice :
        IBladeDevice,
        IGpuClockOffsetManagementDevice,
        ISingleAttemptThermalDevice,
        IPadlessHyperBoostLifecycleValidationDevice,
        IThermalExternalDriftValidationDevice,
        IDisposable
    {
        private const int MaximumStateReadAttempts = 3;
        // A failed thermal pre-write exchange can leave the EC busy through the
        // full protocol timeout. Wait that window plus a bounded settling margin
        // before the only fresh-session retry.
        private static readonly TimeSpan PreWriteRetryDelay =
            TimeSpan.FromSeconds(5);

        private readonly IProductionBatteryPortFactory batteryFactory;
        private readonly IFanRpmQueryRunFactory fanRpmFactory;
        private readonly ProductionLightingSession lightingSession;
        private readonly IGpuClockOffsetController gpuClockOffsets;
        private readonly IGpuActivitySource gpuActivitySource;
        private readonly ILogger<WindowsVerifiedBladeDeviceProvider>? logger;
        private readonly TimeSpan shutdownBudget;
        private readonly Action? onClosing;
        private readonly Func<TimeSpan, CancellationToken, Task> preWriteRetryDelay;
        private readonly object lifecycleSync = new();
        private readonly object gpuClockOffsetManagementSync = new();
        private readonly SemaphoreSlim hardwareOperationGate = new(1, 1);
        private Task? deferredDisposal;
        private bool closing;
        private bool disposed;
        // Querying NVML can wake an otherwise idle discrete GPU. Management
        // intent survives lifecycle invalidation, but a confirmation does not.
        // Refresh an invalidated confirmation only while Windows already
        // reports NVIDIA GPU activity.
        private GpuClockOffsetManagementState gpuClockOffsetManagementState;
        private GpuClockOffsetCapabilities? confirmedManagedGpuClockOffsets;
        internal bool HasOwnedKeyTriggeredRenderer
        {
            get
            {
                lock (lifecycleSync)
                {
                    if (closing || disposed)
                    {
                        return false;
                    }
                }

                return lightingSession.HasOwnedKeyTriggeredRenderer;
            }
        }

        internal bool HasOwnedAmbientRenderer
        {
            get
            {
                lock (lifecycleSync)
                {
                    if (closing || disposed)
                    {
                        return false;
                    }
                }

                return lightingSession.HasOwnedAmbientRenderer;
            }
        }

        public GpuClockOffsetManagementState GpuClockOffsetManagementState
        {
            get
            {
                lock (gpuClockOffsetManagementSync)
                {
                    return gpuClockOffsetManagementState;
                }
            }
        }

        internal SerializedBladeDevice(
            IProductionBatteryPortFactory batteryFactory,
            IFanRpmQueryRunFactory fanRpmFactory,
            IProductionLightingPortFactory lightingFactory,
            IGpuClockOffsetController gpuClockOffsets,
            IGpuActivitySource gpuActivitySource,
            ILogger<WindowsVerifiedBladeDeviceProvider>? logger,
            TimeSpan shutdownBudget,
            Action? onClosing,
            Func<TimeSpan, CancellationToken, Task> preWriteRetryDelay,
            LowPowerGate? lowPowerGate)
        {
            this.batteryFactory = batteryFactory;
            this.fanRpmFactory = fanRpmFactory;
            this.gpuClockOffsets = gpuClockOffsets;
            this.gpuActivitySource = gpuActivitySource;
            this.logger = logger;
            this.shutdownBudget = shutdownBudget;
            this.onClosing = onClosing;
            this.preWriteRetryDelay = preWriteRetryDelay;
            lightingSession = new ProductionLightingSession(
                lightingFactory,
                logger,
                lowPowerGate,
                EnterHardwareOperationAsync,
                () => hardwareOperationGate.Release());
        }

        public async Task<DeviceCapabilities> ReadCapabilitiesAsync(
            CancellationToken cancellationToken)
        {
            await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                DeviceCapabilities batteryCapabilities =
                    await ReadBatteryCapabilitiesCoreAsync(cancellationToken)
                        .ConfigureAwait(false);

                IProductionLightingPort? lighting =
                    await lightingSession.GetLightingPortCoreAsync(
                        ProductionLightingSession.LightingPortOperation.CapabilityObservation,
                        cancellationToken).ConfigureAwait(false);
                return lighting is null
                    ? batteryCapabilities
                    : batteryCapabilities with
                    {
                        LightingEffects = lighting.Capabilities.LightingEffects,
                        ObservedLightingEffects =
                            lighting.Capabilities.ObservedLightingEffects,
                        SpecialKeyBindingsAccess =
                            lighting.Capabilities.SpecialKeyBindingsAccess,
                        WritableSpecialKeySlots =
                            lighting.Capabilities.WritableSpecialKeySlots,
                        LampArrayLightingAccess =
                            lighting.Capabilities.LampArrayLightingAccess,
                        LogoLightingModes = lighting.Capabilities.LogoLightingModes,
                        SupportsKeyboardBrightness =
                            lighting.Capabilities.SupportsKeyboardBrightness,
                        SupportsBreathingColorSelection = lighting.Capabilities.SupportsBreathingColorSelection,
                        SupportsWaveDirectionSelection = lighting.Capabilities.SupportsWaveDirectionSelection,
                        SupportsPlaybackMuteIndicator =
                            lighting.Capabilities.SupportsPlaybackMuteIndicator,
                        KeyboardLightingLayoutId =
                            lighting.Capabilities.KeyboardLightingLayoutId,
                        SupportedLightingRefreshRates =
                            lighting.Capabilities.SupportedLightingRefreshRates,
                    };
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        private async Task<DeviceCapabilities> ReadBatteryCapabilitiesCoreAsync(
            CancellationToken cancellationToken)
        {
            HidSessionResult<IProductionBatteryPort> opened;
            try
            {
                opened = await batteryFactory.OpenAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch
            {
                return DeviceCapabilities.Unavailable();
            }

            if (opened.Value is not { } port)
            {
                if (opened.Failure is (
                        HidSessionFailureKind.Canceled
                        or HidSessionFailureKind.QueuedCanceled)
                    && cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                return DeviceCapabilities.Unavailable();
            }

            await using (port.ConfigureAwait(false))
            {
                return opened.Failure is null
                    ? port.Capabilities
                    : DeviceCapabilities.Unavailable();
            }
        }

        public async Task<ConfirmedDeviceState> ReadStateAsync(
            CancellationToken cancellationToken)
        {
            PowerTransitionDeviceStateReadResult result =
                await ReadStateCoreAsync(
                    confirmTransientLightingOff: false,
                    cancellationToken).ConfigureAwait(false);
            return result.State;
        }

        public Task<PowerTransitionDeviceStateReadResult>
            ReadStateForPowerTransitionAsync(CancellationToken cancellationToken) =>
            ReadStateCoreAsync(
                confirmTransientLightingOff: true,
                cancellationToken);

        public async Task<ConfirmedDeviceState> ReadPerformanceFanStateAsync(
            CancellationToken cancellationToken)
        {
            await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                bool observedTimeout = false;
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        return await ReadPerformanceFanStateOnceAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (RazerProtocolException exception) when (
                        attempt < MaximumStateReadAttempts - 1 &&
                        exception.ValidationFailure is
                            HidProtocolValidationFailure.TransactionMismatch or
                            HidProtocolValidationFailure.CommandClassMismatch or
                            HidProtocolValidationFailure.CommandIdMismatch)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await Task.Delay(
                            TimeSpan.FromMilliseconds(25 * (attempt + 1)),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (
                        exception is IOException or TimeoutException)
                    {
                        observedTimeout |= exception is TimeoutException;
                        if (attempt >= MaximumStateReadAttempts - 1)
                        {
                            if (observedTimeout && exception is IOException)
                            {
                                throw new TimeoutException(
                                    "Performance and fan state query timed out.");
                            }

                            throw;
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        await Task.Delay(
                            TimeSpan.FromMilliseconds(25 * (attempt + 1)),
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException("Performance and fan state query timed out.");
            }
            catch (RazerProtocolException exception)
            {
                throw new RazerProtocolException(
                    DescribeStateQueryFailure(exception, "Performance and fan state query"),
                    exception.ValidationFailure)
                {
                    StateQuery = exception.StateQuery,
                };
            }
            catch (ObjectDisposedException)
            {
                throw;
            }
            catch (FailClosedDeviceException exception)
            {
                throw new IOException(exception.Message);
            }
            catch
            {
                throw new IOException("Performance and fan state query failed.");
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        private async Task<PowerTransitionDeviceStateReadResult> ReadStateCoreAsync(
            bool confirmTransientLightingOff,
            CancellationToken cancellationToken)
        {
            await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ProductionLightingSession.TransitionLightingReadResult? transitionLightingRead =
                    confirmTransientLightingOff
                        ? await lightingSession.ReadPowerTransitionLightingStateCoreAsync(cancellationToken)
                            .ConfigureAwait(false)
                        : null;
                bool observedTimeout = false;
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        return await ReadBatteryStateOnceAsync(
                            transitionLightingRead,
                            cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (RazerProtocolException exception) when (
                        attempt < MaximumStateReadAttempts - 1 &&
                        exception.ValidationFailure is
                            HidProtocolValidationFailure.TransactionMismatch or
                            HidProtocolValidationFailure.CommandClassMismatch or
                            HidProtocolValidationFailure.CommandIdMismatch)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await Task.Delay(
                            TimeSpan.FromMilliseconds(25 * (attempt + 1)),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (
                        exception is IOException or TimeoutException)
                    {
                        observedTimeout |= exception is TimeoutException;
                        if (attempt >= MaximumStateReadAttempts - 1)
                        {
                            if (observedTimeout && exception is IOException)
                            {
                                throw new TimeoutException(
                                    "Battery state query timed out.");
                            }

                            throw;
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        await Task.Delay(
                            TimeSpan.FromMilliseconds(25 * (attempt + 1)),
                            cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException("Battery state query timed out.");
            }
            catch (RazerProtocolException exception)
            {
                throw new RazerProtocolException(
                    DescribeStateQueryFailure(exception, "Device state query"),
                    exception.ValidationFailure)
                {
                    StateQuery = exception.StateQuery,
                };
            }
            catch (ObjectDisposedException)
            {
                throw;
            }
            catch (FailClosedDeviceException exception)
            {
                throw new IOException(exception.Message);
            }
            catch
            {
                throw new IOException("Battery state query failed.");
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        public async Task<ConfirmedDeviceState> ReadLightingStateAsync(
            CancellationToken cancellationToken)
        {
            await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                LightingSettings? lighting =
                    await lightingSession.ReadLightingStateCoreAsync(cancellationToken).ConfigureAwait(false);
                LogoLightingMode? logo =
                    await lightingSession.ReadLogoLightingStateCoreAsync(cancellationToken)
                        .ConfigureAwait(false);
                return new ConfirmedDeviceState(
                    Performance: null,
                    Fans: null,
                    Battery: null,
                    Lighting: lighting)
                {
                    LogoLighting = logo,
                };
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        private async Task<PowerTransitionDeviceStateReadResult>
            ReadBatteryStateOnceAsync(
            ProductionLightingSession.TransitionLightingReadResult? transitionLightingRead,
            CancellationToken cancellationToken)
        {
            HidSessionResult<IProductionBatteryPort> opened =
                await batteryFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (opened.Value is not { } port)
            {
                ThrowReadOpenFailure(opened.Failure, cancellationToken);
                throw new IOException("Battery state query did not provide a verified session.");
            }

            await using (port.ConfigureAwait(false))
            {
                if (opened.Failure is not null)
                {
                    ThrowReadOpenFailure(opened.Failure, cancellationToken);
                }

                ConfirmedBatteryState? protection = port.SupportsProtectionQuery
                    ? await ReadStateQueryAsync(
                        DeviceStateQuery.BatteryProtection,
                        () => port.QueryProtectionAsync(cancellationToken)).ConfigureAwait(false)
                    : null;
                bool? temporary = port.SupportsTemporaryFullChargeQuery
                    ? await ReadStateQueryAsync(
                        DeviceStateQuery.TemporaryFullCharge,
                        () => port.QueryTemporaryFullChargeAsync(cancellationToken))
                        .ConfigureAwait(false)
                    // The experimental model contract exposes no temporary override.
                    // Its BHO register is the complete admitted protection state.
                    : batteryFactory is ExperimentalBladeFactory ? false : null;
                ConfirmedBatteryState? battery = protection is null ? null : new(
                    protection.ProtectionEnabled,
                    protection.ChargeLimitPercent,
                    temporary);
                ProductionPerformanceFanState performanceFan =
                    await ReadStateQueryAsync(
                        DeviceStateQuery.PerformanceAndFans,
                        () => port.QueryPerformanceFanStateAsync(cancellationToken))
                        .ConfigureAwait(false);
                GpuClockOffsetCapabilities? gpuOffsets =
                    await ReadManagedGpuClockOffsetsAsync(cancellationToken)
                        .ConfigureAwait(false);
                CustomPerformanceSettings? customPerformance =
                    performanceFan.CustomPerformance;
                ProductionLightingSession.TransitionLightingReadResult lightingRead =
                    transitionLightingRead
                        ?? new(
                            await lightingSession.ReadLightingStateCoreAsync(cancellationToken)
                                .ConfigureAwait(false),
                            OffConfirmationUnavailable: false);
                LogoLightingMode? logo =
                    await lightingSession.ReadLogoLightingStateCoreAsync(cancellationToken)
                        .ConfigureAwait(false);
                return new(
                    new ConfirmedDeviceState(
                        performanceFan.Performance,
                        performanceFan.Fans,
                        battery,
                        lightingRead.State)
                    {
                        HyperBoostActive = performanceFan.HyperBoostActive,
                        CustomPerformance = customPerformance,
                        GpuClockOffsets = gpuOffsets,
                        LogoLighting = logo,
                    },
                    lightingRead.OffConfirmationUnavailable);
            }
        }

        private static async Task<T> ReadStateQueryAsync<T>(
            DeviceStateQuery query,
            Func<Task<T>> readAsync)
        {
            try
            {
                return await readAsync().ConfigureAwait(false);
            }
            catch (RazerProtocolException exception)
            {
                throw new RazerProtocolException(
                    "Device state query failed protocol validation.",
                    exception.ValidationFailure)
                {
                    StateQuery = exception.StateQuery ?? query,
                };
            }
        }

        private static string DescribeStateQueryFailure(
            RazerProtocolException exception,
            string fallback)
        {
            string query = exception.StateQuery switch
            {
                DeviceStateQuery.BatteryProtection => "Battery protection query (07/92)",
                DeviceStateQuery.TemporaryFullCharge => "Temporary full charge query (07/8F)",
                DeviceStateQuery.PerformanceAndFans => "Performance and fan state query",
                DeviceStateQuery.ThermalChannelOne => "Thermal channel 1 query (0D/82)",
                DeviceStateQuery.ThermalChannelTwo => "Thermal channel 2 query (0D/82)",
                DeviceStateQuery.ThermalChannelAgreement => "Thermal channel agreement (0D/82)",
                DeviceStateQuery.CustomCpuPerformance => "Custom CPU performance query (0D/87)",
                DeviceStateQuery.CustomGpuPerformance => "Custom GPU performance query (0D/87)",
                DeviceStateQuery.CustomPerformanceCombination => "Custom performance combination (0D/87)",
                _ => fallback,
            };
            return $"{query} failed protocol validation ({exception.ValidationFailure}).";
        }

        private async Task<ConfirmedDeviceState> ReadPerformanceFanStateOnceAsync(
            CancellationToken cancellationToken)
        {
            HidSessionResult<IProductionBatteryPort> opened =
                await batteryFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (opened.Value is not { } port)
            {
                ThrowReadOpenFailure(opened.Failure, cancellationToken);
                throw new IOException(
                    "Performance and fan state query did not provide a verified session.");
            }

            await using (port.ConfigureAwait(false))
            {
                if (opened.Failure is not null)
                {
                    ThrowReadOpenFailure(opened.Failure, cancellationToken);
                }

                ProductionPerformanceFanState performanceFan =
                    await port.QueryPerformanceFanStateAsync(cancellationToken)
                        .ConfigureAwait(false);
                GpuClockOffsetCapabilities? managedGpuClockOffsets =
                    await ReadManagedGpuClockOffsetsAsync(cancellationToken)
                        .ConfigureAwait(false);
                return new ConfirmedDeviceState(
                    performanceFan.Performance,
                    performanceFan.Fans,
                    Battery: null,
                    Lighting: null)
                {
                    HyperBoostActive = performanceFan.HyperBoostActive,
                    CustomPerformance = performanceFan.CustomPerformance,
                    GpuClockOffsets = managedGpuClockOffsets,
                };
            }
        }

        public async Task<FanRpmReadings?> ReadFanRpmAsync(
            CancellationToken cancellationToken)
        {
            await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                HidSessionResult<IFanRpmQueryPort> opened;
                try
                {
                    opened = await fanRpmFactory.OpenAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                catch
                {
                    return null;
                }

                if (opened.Value is not { } port)
                {
                    if (opened.Failure is (
                            HidSessionFailureKind.Canceled
                            or HidSessionFailureKind.QueuedCanceled)
                        && cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }

                    return null;
                }

                await using (port.ConfigureAwait(false))
                {
                    if (opened.Failure is not null)
                    {
                        return null;
                    }

                    HidSessionResult<FanRpmReadings> query;
                    try
                    {
                        query = await port.QueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }
                    catch
                    {
                        return null;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    return query.Failure is null ? query.Value : null;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                throw;
            }
            catch
            {
                return null;
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        public async Task<AdapterPowerState> ReadAdapterPowerStateAsync(
            CancellationToken cancellationToken)
        {
            await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                HidSessionResult<IProductionBatteryPort> opened =
                    await batteryFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
                if (opened.Value is not { } port)
                {
                    ThrowReadOpenFailure(opened.Failure, cancellationToken);
                    return AdapterPowerState.Unknown;
                }

                await using (port.ConfigureAwait(false))
                {
                    if (opened.Failure is not null)
                    {
                        ThrowReadOpenFailure(opened.Failure, cancellationToken);
                    }

                    return await port.QueryAdapterPowerStateAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        public async Task<string?> ReadMcuVersionAsync(
            CancellationToken cancellationToken)
        {
            await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // These queries have no implementation in the experimental port.
                // Returning unavailable avoids opening and probing HID for a known no-op.
                if (batteryFactory is ExperimentalBladeFactory)
                {
                    return null;
                }

                HidSessionResult<IProductionBatteryPort> opened =
                    await batteryFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
                if (opened.Value is not { } port)
                {
                    ThrowReadOpenFailure(opened.Failure, cancellationToken);
                    return null;
                }

                await using (port.ConfigureAwait(false))
                {
                    if (opened.Failure is not null)
                    {
                        ThrowReadOpenFailure(opened.Failure, cancellationToken);
                    }

                    return port.SupportsMcuVersionQuery
                        ? await port.QueryMcuVersionAsync(cancellationToken)
                            .ConfigureAwait(false)
                        : null;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                throw;
            }
            catch
            {
                return null;
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        public async Task<Rz090581CapabilityDiagnostics?>
            ReadRz090581CapabilityDiagnosticsAsync(
                CancellationToken cancellationToken)
        {
            await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (batteryFactory is ExperimentalBladeFactory)
                {
                    return null;
                }
                HidSessionResult<IProductionBatteryPort> opened =
                    await batteryFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
                if (opened.Value is not { } port)
                {
                    ThrowReadOpenFailure(opened.Failure, cancellationToken);
                    return null;
                }

                await using (port.ConfigureAwait(false))
                {
                    if (opened.Failure is not null)
                    {
                        ThrowReadOpenFailure(opened.Failure, cancellationToken);
                    }

                    return port.SupportsRz090581CapabilityDiagnosticsQuery
                        ? await port.QueryRz090581CapabilityDiagnosticsAsync(
                            cancellationToken).ConfigureAwait(false)
                        : null;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                throw;
            }
            catch
            {
                return null;
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        public async Task<bool?> ReadStartupAnimationAsync(
            CancellationToken cancellationToken)
        {
            await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // These queries have no implementation in the experimental port.
                // Returning unavailable avoids opening and probing HID for a known no-op.
                if (batteryFactory is ExperimentalBladeFactory)
                {
                    return null;
                }

                HidSessionResult<IProductionBatteryPort> opened =
                    await batteryFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
                if (opened.Value is not { } port)
                {
                    ThrowReadOpenFailure(opened.Failure, cancellationToken);
                    return null;
                }

                await using (port.ConfigureAwait(false))
                {
                    if (opened.Failure is not null)
                    {
                        ThrowReadOpenFailure(opened.Failure, cancellationToken);
                    }

                    return port.SupportsStartupAnimationQuery
                        ? await port.QueryStartupAnimationAsync(cancellationToken)
                            .ConfigureAwait(false)
                        : null;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                throw;
            }
            catch
            {
                return null;
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        public async Task<FunctionKeyPrimaryMode?> ReadFunctionKeyPrimaryAsync(
            CancellationToken cancellationToken)
        {
            await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // These queries have no implementation in the experimental port.
                // Returning unavailable avoids opening and probing HID for a known no-op.
                if (batteryFactory is ExperimentalBladeFactory)
                {
                    return null;
                }

                HidSessionResult<IProductionBatteryPort> opened =
                    await batteryFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
                if (opened.Value is not { } port)
                {
                    ThrowReadOpenFailure(opened.Failure, cancellationToken);
                    return null;
                }

                await using (port.ConfigureAwait(false))
                {
                    if (opened.Failure is not null)
                    {
                        ThrowReadOpenFailure(opened.Failure, cancellationToken);
                    }

                    return port.SupportsFunctionKeyPrimaryQuery
                        ? await port.QueryFunctionKeyPrimaryAsync(cancellationToken)
                            .ConfigureAwait(false)
                        : null;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                throw;
            }
            catch
            {
                return null;
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        public async Task<bool?> ReadGamingModeAsync(CancellationToken cancellationToken)
        {
            await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // These queries have no implementation in the experimental port.
                // Returning unavailable avoids opening and probing HID for a known no-op.
                if (batteryFactory is ExperimentalBladeFactory)
                {
                    return null;
                }

                HidSessionResult<IProductionBatteryPort> opened =
                    await batteryFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
                if (opened.Value is not { } port)
                {
                    ThrowReadOpenFailure(opened.Failure, cancellationToken);
                    return null;
                }

                await using (port.ConfigureAwait(false))
                {
                    if (opened.Failure is not null)
                    {
                        ThrowReadOpenFailure(opened.Failure, cancellationToken);
                    }

                    return port.SupportsGamingModeQuery
                        ? await port.QueryGamingModeAsync(cancellationToken)
                            .ConfigureAwait(false)
                        : null;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                throw;
            }
            catch
            {
                return null;
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        public Task SetPerformanceAsync(
            PerformancePreset value,
            CancellationToken cancellationToken) => SetPerformanceAsync(
                value,
                customPerformance: null,
                gpuClockOffsets: null,
                cancellationToken);

        public Task SetPerformanceAsync(
            PerformancePreset value,
            CustomPerformanceSettings? customPerformance,
            CancellationToken cancellationToken) => SetPerformanceAsync(
                value,
                customPerformance,
                gpuClockOffsets: null,
                cancellationToken);

        public async Task SetPerformanceAsync(
            PerformancePreset value,
            CustomPerformanceSettings? customPerformance,
            GpuClockOffsetSettings? gpuClockOffsets,
            CancellationToken cancellationToken) => await SetPerformanceAsync(
                value,
                customPerformance,
                gpuClockOffsets,
                retainGpuClockOffsetManagement: true,
                cancellationToken).ConfigureAwait(false);

        public async Task SetPerformanceAsync(
            PerformancePreset value,
            CustomPerformanceSettings? customPerformance,
            GpuClockOffsetSettings? gpuClockOffsets,
            bool retainGpuClockOffsetManagement,
            CancellationToken cancellationToken) => await SetPerformanceCoreAsync(
            value,
            customPerformance,
            gpuClockOffsets,
            retainGpuClockOffsetManagement,
            allowFreshSessionRetry: true,
            cancellationToken).ConfigureAwait(false);

        Task ISingleAttemptThermalDevice.SetPerformanceOnceAsync(
            PerformancePreset value,
            CustomPerformanceSettings? customPerformance,
            GpuClockOffsetSettings? gpuClockOffsets,
            bool retainGpuClockOffsetManagement,
            CancellationToken cancellationToken) => SetPerformanceCoreAsync(
            value,
            customPerformance,
            gpuClockOffsets,
            retainGpuClockOffsetManagement,
            allowFreshSessionRetry: false,
            cancellationToken);

        Task IThermalExternalDriftValidationDevice.InjectPerformanceDriftOnceAsync(
            PerformancePreset candidate,
            CancellationToken cancellationToken)
        {
            if (!ThermalExternalDriftValidationBuildIdentity.ReadCurrent().Enabled
                || !WindowsHostIdentitySource.IsExactRz090581WindowsHost())
            {
                return RejectProductionWrite(
                    "Thermal external-drift injection requires an exact RZ09-0581 validation build.");
            }

            return SetPerformanceCoreAsync(
                candidate,
                customPerformance: null,
                gpuClockOffsets: null,
                retainGpuClockOffsetManagement: true,
                allowFreshSessionRetry: false,
                cancellationToken);
        }

        private async Task SetPerformanceCoreAsync(
            PerformancePreset value,
            CustomPerformanceSettings? customPerformance,
            GpuClockOffsetSettings? gpuClockOffsets,
            bool retainGpuClockOffsetManagement,
            bool allowFreshSessionRetry,
            CancellationToken cancellationToken)
        {
            if (customPerformance?.GpuClockOffsets is { } legacyOffsets)
            {
                if (gpuClockOffsets is not null && gpuClockOffsets != legacyOffsets)
                {
                    await RejectProductionWrite(
                        "The GPU clock tuning request is inconsistent.")
                        .ConfigureAwait(false);
                    return;
                }

                customPerformance = customPerformance with { GpuClockOffsets = null };
                gpuClockOffsets = legacyOffsets;
            }

            if (!retainGpuClockOffsetManagement
                && gpuClockOffsets is not null
                && gpuClockOffsets != GpuClockOffsetSettings.Default)
            {
                await RejectProductionWrite(
                    "Relinquishing GPU clock tuning requires a default-offset reset.")
                    .ConfigureAwait(false);
                return;
            }

            if (!batteryFactory.Capabilities.PerformancePresets.Contains(value)
                || (value == PerformancePreset.Custom
                    ? !CustomPerformanceSettings.IsWellFormed(customPerformance)
                        || batteryFactory.Capabilities.CustomPerformance?.Supports(
                            customPerformance) == false
                    : customPerformance is not null))
            {
                await RejectProductionWrite(
                    "The requested performance preset is not admitted.")
                    .ConfigureAwait(false);
                return;
            }

            GpuClockOffsetSettings? offsetsToApply = gpuClockOffsets;
            bool reuseConfirmedDefaultReset = false;
            void PreflightGpuClockOffsets()
            {
                reuseConfirmedDefaultReset = !retainGpuClockOffsetManagement
                    && offsetsToApply == GpuClockOffsetSettings.Default
                    && HasConfirmedDefaultReset();
                if (reuseConfirmedDefaultReset)
                {
                    offsetsToApply = null;
                    return;
                }

                if (offsetsToApply is not { } preflightOffsets)
                {
                    return;
                }

                GpuClockOffsetCapabilities? driver = batteryFactory.Capabilities.GpuOverclock
                    is { Access: DeviceFeatureAccess.ReadWrite }
                    ? QueryGpuClockOffsets()
                    : null;
                if (!GpuClockOffsetsSupported(driver, preflightOffsets))
                {
                    throw new CapabilityUnavailableException(
                        driver is null
                            ? "GPU clock tuning is unavailable."
                            : "The requested GPU clock offsets are outside the ranges reported by the NVIDIA driver.");
                }
            }

            void CompleteGpuClockOffsets()
            {
                if (offsetsToApply is { } appliedOffsets)
                {
                    try
                    {
                        // The firmware power mode has already been committed.
                        // Complete the paired NVML operation under the same
                        // provider operation gate.
                        SetGpuClockOffsetsAndUpdateManagement(
                            appliedOffsets,
                            retainGpuClockOffsetManagement);
                    }
                    catch (BladeWriteException exception)
                    {
                        throw new BladeWriteException(
                            WriteSubmissionState.Indeterminate,
                            $"The power mode was applied, but GPU clock tuning failed. {exception.Message}",
                            exception);
                    }
                }
                else if (reuseConfirmedDefaultReset)
                {
                    SetGpuClockOffsetManagementState(
                        GpuClockOffsetManagementState.ResetConfirmed);
                }
            }

            await ExecuteProductionWriteAsync(
                port => value == PerformancePreset.Custom
                    ? port.SetCustomPerformanceAsync(customPerformance!, cancellationToken)
                    : port.SetPerformanceAsync(value, cancellationToken),
                cancellationToken,
                PreflightGpuClockOffsets,
                CompleteGpuClockOffsets,
                allowFreshSessionRetry: allowFreshSessionRetry).ConfigureAwait(false);
        }

        public Task SetHyperBoostAsync(
            bool enabled,
            PerformancePreset baseMode,
            CancellationToken cancellationToken) => SetHyperBoostCoreAsync(
                enabled,
                baseMode,
                allowFreshSessionRetry: true,
                cancellationToken);

        Task ISingleAttemptThermalDevice.SetHyperBoostOnceAsync(
            bool enabled,
            PerformancePreset baseMode,
            CancellationToken cancellationToken) => SetHyperBoostCoreAsync(
                enabled,
                baseMode,
                allowFreshSessionRetry: false,
                cancellationToken);

        Task ISingleAttemptThermalDevice.SetHyperBoostOnceAsync(
            bool enabled,
            PerformancePreset baseMode,
            bool requiresCoolingPad,
            CancellationToken cancellationToken) => SetHyperBoostCoreAsync(
                enabled,
                baseMode,
                allowFreshSessionRetry: false,
                cancellationToken,
                requiresCoolingPad);

        private Task SetHyperBoostCoreAsync(
            bool enabled,
            PerformancePreset baseMode,
            bool allowFreshSessionRetry,
            CancellationToken cancellationToken,
            bool? requiresCoolingPad = null) =>
            ExecuteProductionWriteAsync(
                port => requiresCoolingPad is { } journaledPath
                    ? port.SetHyperBoostAsync(
                        enabled,
                        baseMode,
                        journaledPath,
                        cancellationToken)
                    : port.SetHyperBoostAsync(
                        enabled,
                        baseMode,
                        cancellationToken),
                cancellationToken,
                retryHyperBoostAdapterPreflight: true,
                allowFreshSessionRetry: allowFreshSessionRetry);

        public Task EnablePadlessHyperBoostLifecycleValidationAsync(
            CancellationToken cancellationToken) =>
            EnablePadlessHyperBoostLifecycleValidationCoreAsync(
                allowFreshSessionRetry: true,
                cancellationToken);

        Task IPadlessHyperBoostLifecycleValidationDevice
            .EnablePadlessHyperBoostLifecycleValidationOnceAsync(
                CancellationToken cancellationToken) =>
            EnablePadlessHyperBoostLifecycleValidationCoreAsync(
                allowFreshSessionRetry: false,
                cancellationToken);

        private Task EnablePadlessHyperBoostLifecycleValidationCoreAsync(
            bool allowFreshSessionRetry,
            CancellationToken cancellationToken)
        {
            if (!PadlessHyperBoostLifecycleValidationBuildIdentity.ReadCurrent().Enabled)
            {
                return RejectProductionWrite(
                    "Pad-less HyperBoost lifecycle validation requires a validation-marked service build.");
            }

            return ExecuteProductionWriteAsync(
                port => port is IPadlessHyperBoostLifecycleValidationPort validationPort
                    ? validationPort.EnablePadlessHyperBoostLifecycleValidationAsync(
                        cancellationToken)
                    : Task.FromException(new CapabilityUnavailableException(
                        "The exact validation-only pad-less HyperBoost port is unavailable.")),
                cancellationToken,
                retryHyperBoostAdapterPreflight: true,
                allowFreshSessionRetry: allowFreshSessionRetry);
        }

        public async Task SetGpuClockOffsetsAsync(
            GpuClockOffsetSettings value,
            CancellationToken cancellationToken) => await SetGpuClockOffsetsAsync(
                value,
                retainManagement: true,
                cancellationToken).ConfigureAwait(false);

        public async Task SetGpuClockOffsetsAsync(
            GpuClockOffsetSettings value,
            bool retainManagement,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!GpuClockOffsetSettings.IsWellFormed(value))
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "GPU clock offsets are invalid.");
            }
            if (!retainManagement && value != GpuClockOffsetSettings.Default)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "Relinquishing GPU clock tuning requires a default-offset reset.");
            }

            try
            {
                await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "GPU clock tuning was canceled before the operation started.");
            }
            catch (ObjectDisposedException exception)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "GPU clock tuning was blocked because the device provider is closing.",
                    exception);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                GpuClockOffsetCapabilities? driver = batteryFactory.Capabilities.GpuOverclock
                    is { Access: DeviceFeatureAccess.ReadWrite }
                    ? QueryGpuClockOffsets()
                    : null;
                if (!GpuClockOffsetsSupported(driver, value))
                {
                    throw new CapabilityUnavailableException(
                        driver is null
                            ? "GPU clock tuning is unavailable."
                            : "The requested GPU clock offsets are outside the ranges reported by the NVIDIA driver.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                SetGpuClockOffsetsAndUpdateManagement(value, retainManagement);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "GPU clock tuning was canceled before the NVIDIA write started.");
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        public async Task RelinquishGpuClockOffsetManagementAsync(
            CancellationToken cancellationToken)
        {
            // An already-relinquished snapshot must still pass through this
            // gate: a paired firmware/NVML write may be in flight and publish
            // managed state afterward. The gate also preserves closing checks.
            await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (gpuClockOffsetManagementSync)
                {
                    bool defaultConfirmed = gpuClockOffsetManagementState is
                        GpuClockOffsetManagementState.ResetConfirmed
                        or GpuClockOffsetManagementState.RelinquishedDefaultConfirmed
                        || gpuClockOffsetManagementState ==
                            GpuClockOffsetManagementState.Managed
                            && confirmedManagedGpuClockOffsets?.Current ==
                                GpuClockOffsetSettings.Default;
                    gpuClockOffsetManagementState = defaultConfirmed
                        ? GpuClockOffsetManagementState.RelinquishedDefaultConfirmed
                        : GpuClockOffsetManagementState.Relinquished;
                    confirmedManagedGpuClockOffsets = null;
                }
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        public async Task<bool> InvalidateGpuClockOffsetConfirmationAsync(
            CancellationToken cancellationToken)
        {
            await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (gpuClockOffsetManagementSync)
                {
                    bool requiresRevalidation = gpuClockOffsetManagementState is
                        GpuClockOffsetManagementState.Managed
                        or GpuClockOffsetManagementState.ResetConfirmed;
                    if (gpuClockOffsetManagementState ==
                        GpuClockOffsetManagementState.Managed)
                    {
                        confirmedManagedGpuClockOffsets = null;
                    }
                    else if (gpuClockOffsetManagementState ==
                        GpuClockOffsetManagementState.ResetConfirmed)
                    {
                        gpuClockOffsetManagementState =
                            GpuClockOffsetManagementState.Unknown;
                    }
                    else if (gpuClockOffsetManagementState ==
                        GpuClockOffsetManagementState.RelinquishedDefaultConfirmed)
                    {
                        gpuClockOffsetManagementState =
                            GpuClockOffsetManagementState.Relinquished;
                    }
                    return requiresRevalidation;
                }
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        public Task SetFanModeAsync(
            FanMode value,
            CancellationToken cancellationToken) =>
            batteryFactory.Capabilities.FanModes.Contains(value)
                ? ExecuteProductionWriteAsync(
                    port => port.SetFanModeAsync(value, cancellationToken),
                    cancellationToken)
                : RejectProductionWrite("The requested fan mode is not admitted.");

        private GpuClockOffsetCapabilities? ApplyGpuPolicy(
            GpuClockOffsetCapabilities? driver)
        {
            GpuOverclockCapabilities? policy = batteryFactory.Capabilities.GpuOverclock;
            if (driver is null || policy?.IsWellFormed != true)
            {
                return driver;
            }

            int coreMinimum = Math.Max(
                driver.Core.MinimumMegahertz,
                policy.CoreOffsetMegahertz.Minimum);
            int coreMaximum = Math.Min(
                driver.Core.MaximumMegahertz,
                policy.CoreOffsetMegahertz.Maximum);
            int memoryMinimum = Math.Max(
                driver.Memory.MinimumMegahertz,
                policy.MemoryOffsetMegahertz.Minimum);
            int memoryMaximum = Math.Min(
                driver.Memory.MaximumMegahertz,
                policy.MemoryOffsetMegahertz.Maximum);
            if (coreMinimum > coreMaximum
                || memoryMinimum > memoryMaximum
                || driver.Core.CurrentMegahertz < coreMinimum
                || driver.Core.CurrentMegahertz > coreMaximum
                || driver.Memory.CurrentMegahertz < memoryMinimum
                || driver.Memory.CurrentMegahertz > memoryMaximum)
            {
                return null;
            }

            return driver with
            {
                Core = driver.Core with
                {
                    MinimumMegahertz = coreMinimum,
                    MaximumMegahertz = coreMaximum,
                },
                Memory = driver.Memory with
                {
                    MinimumMegahertz = memoryMinimum,
                    MaximumMegahertz = memoryMaximum,
                },
            };
        }

        private async ValueTask<GpuClockOffsetCapabilities?> ReadManagedGpuClockOffsetsAsync(
            CancellationToken cancellationToken)
        {
            GpuClockOffsetCapabilities? initialConfirmed;
            lock (gpuClockOffsetManagementSync)
            {
                if (gpuClockOffsetManagementState !=
                    GpuClockOffsetManagementState.Managed)
                {
                    return null;
                }

                initialConfirmed = confirmedManagedGpuClockOffsets;
            }

            bool? gpuActive = await gpuActivitySource.IsActiveAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            lock (gpuClockOffsetManagementSync)
            {
                if (gpuClockOffsetManagementState !=
                    GpuClockOffsetManagementState.Managed)
                {
                    return null;
                }

                GpuClockOffsetCapabilities? confirmed =
                    confirmedManagedGpuClockOffsets;
                if (confirmed != initialConfirmed || gpuActive is not true)
                {
                    return confirmed;
                }

                GpuClockOffsetCapabilities? observed = gpuClockOffsets.Query();
                if (observed is null)
                {
                    return confirmed;
                }

                GpuClockOffsetCapabilities? refreshed = ApplyGpuPolicy(observed);
                confirmedManagedGpuClockOffsets = refreshed;
                return refreshed;
            }
        }

        private GpuClockOffsetCapabilities? QueryGpuClockOffsets()
        {
            lock (gpuClockOffsetManagementSync)
            {
                return gpuClockOffsets.Query();
            }
        }

        private void SetGpuClockOffsetsAndUpdateManagement(
            GpuClockOffsetSettings value,
            bool retainManagement)
        {
            lock (gpuClockOffsetManagementSync)
            {
                GpuClockOffsetCapabilities confirmed = gpuClockOffsets.Set(value);
                gpuClockOffsetManagementState = retainManagement
                    ? GpuClockOffsetManagementState.Managed
                    : GpuClockOffsetManagementState.ResetConfirmed;
                confirmedManagedGpuClockOffsets = retainManagement
                    ? ApplyGpuPolicy(confirmed)
                    : null;
            }
        }

        private bool HasConfirmedDefaultReset()
        {
            lock (gpuClockOffsetManagementSync)
            {
                return gpuClockOffsetManagementState is
                    GpuClockOffsetManagementState.ResetConfirmed
                    or GpuClockOffsetManagementState.RelinquishedDefaultConfirmed;
            }
        }

        private void SetGpuClockOffsetManagementState(
            GpuClockOffsetManagementState state)
        {
            lock (gpuClockOffsetManagementSync)
            {
                gpuClockOffsetManagementState = state;
                confirmedManagedGpuClockOffsets = null;
            }
        }

        private bool GpuClockOffsetsSupported(
            GpuClockOffsetCapabilities? driver,
            GpuClockOffsetSettings requested)
        {
            if (driver?.Supports(requested) != true)
            {
                return false;
            }

            GpuOverclockCapabilities? policy = batteryFactory.Capabilities.GpuOverclock;
            return policy?.IsWellFormed == true
                && policy.Access == DeviceFeatureAccess.ReadWrite
                && requested.CoreMegahertz >= policy.CoreOffsetMegahertz.Minimum
                && requested.CoreMegahertz <= policy.CoreOffsetMegahertz.Maximum
                && requested.MemoryMegahertz >= policy.MemoryOffsetMegahertz.Minimum
                && requested.MemoryMegahertz <= policy.MemoryOffsetMegahertz.Maximum;
        }

        public Task SetFanControlAsync(
            FanControlSettings value,
            CancellationToken cancellationToken) => SetFanControlCoreAsync(
                value,
                allowFreshSessionRetry: true,
                allowSecondManualEntryContinuation: true,
                cancellationToken);

        Task ISingleAttemptThermalDevice.SetFanControlOnceAsync(
            FanControlSettings value,
            CancellationToken cancellationToken) => SetFanControlCoreAsync(
                value,
                allowFreshSessionRetry: false,
                allowSecondManualEntryContinuation: false,
                cancellationToken);

        private Task SetFanControlCoreAsync(
            FanControlSettings value,
            bool allowFreshSessionRetry,
            bool allowSecondManualEntryContinuation,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(value);
            DeviceCapabilities capabilities = batteryFactory.Capabilities;
            Func<IProductionBatteryPort, Task>? continueAfterSecondFanManualEntry = null;
            if (allowSecondManualEntryContinuation
                && DeviceSubsystemOrder.IsManualFanMode(value.Mode)
                && capabilities.FanSpeedRange is { } fanSpeedRange)
            {
                int targetRpm = value.Mode == FanMode.Fixed
                    ? value.FixedSpeedRpm
                    : fanSpeedRange.MaximumRpm;
                continueAfterSecondFanManualEntry = port => port.SetFanTargetsAsync(
                    targetRpm,
                    targetRpm,
                    cancellationToken);
            }

            return capabilities.FanModes.Contains(value.Mode)
                && (value.Mode != FanMode.Fixed
                    || capabilities.FanSpeedRange?.Supports(value.FixedSpeedRpm) == true)
                ? ExecuteProductionWriteAsync(
                    port => port.SetFanControlAsync(value, cancellationToken),
                    cancellationToken,
                    continueAfterSecondFanManualEntry:
                        continueAfterSecondFanManualEntry,
                    allowFreshSessionRetry: allowFreshSessionRetry)
                : RejectProductionWrite("The requested fan control is not admitted.");
        }

        public Task SetFanTargetsAsync(
            int fan1Rpm,
            int fan2Rpm,
            CancellationToken cancellationToken) => SetFanTargetsCoreAsync(
                fan1Rpm,
                fan2Rpm,
                allowFreshSessionRetry: true,
                isManualEntryContinuation: false,
                cancellationToken);

        Task ISingleAttemptThermalDevice.SetFanTargetsOnceAsync(
            int fan1Rpm,
            int fan2Rpm,
            CancellationToken cancellationToken) => SetFanTargetsCoreAsync(
                fan1Rpm,
                fan2Rpm,
                allowFreshSessionRetry: false,
                isManualEntryContinuation: false,
                cancellationToken);

        Task ISingleAttemptThermalDevice.ContinueFanTargetsAfterManualEntryOnceAsync(
            int fan1Rpm,
            int fan2Rpm,
            CancellationToken cancellationToken) => SetFanTargetsCoreAsync(
                fan1Rpm,
                fan2Rpm,
                allowFreshSessionRetry: false,
                isManualEntryContinuation: true,
                cancellationToken);

        private Task SetFanTargetsCoreAsync(
            int fan1Rpm,
            int fan2Rpm,
            bool allowFreshSessionRetry,
            bool isManualEntryContinuation,
            CancellationToken cancellationToken)
        {
            DeviceCapabilities capabilities = batteryFactory.Capabilities;
            bool targetsAdmitted = isManualEntryContinuation
                ? capabilities.FanModes.Any(
                    mode => DeviceSubsystemOrder.IsManualFanMode(mode))
                    && capabilities.FanSpeedRange?.Supports(fan1Rpm) == true
                    && capabilities.FanSpeedRange.Supports(fan2Rpm)
                : capabilities.FanModes.Contains(FanMode.ManualCurve);
            return targetsAdmitted
                ? ExecuteProductionWriteAsync(
                    port => port.SetFanTargetsAsync(
                        fan1Rpm,
                        fan2Rpm,
                        cancellationToken),
                    cancellationToken,
                    allowFreshSessionRetry: allowFreshSessionRetry,
                    isManualEntryContinuation: isManualEntryContinuation)
                : RejectProductionWrite("Manual fan targets are not admitted.");
        }

        public Task SetChargeLimitAsync(
            BatteryPolicy value,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!batteryFactory.Capabilities.ChargeLimits.Contains(value.ChargeLimitPercent))
            {
                return RejectProductionWrite("The requested charge limit is not admitted.");
            }

            return ExecuteProductionWriteAsync(
                port => port.SetChargeLimitAsync(value.ChargeLimitPercent, cancellationToken),
                cancellationToken);
        }

        public Task SetBatteryProtectionOffAsync(CancellationToken cancellationToken) =>
            batteryFactory.Capabilities.SupportsBatteryProtectionOff
                ? ExecuteProductionWriteAsync(
                    port => port.SetProtectionOffAsync(cancellationToken),
                    cancellationToken)
                : RejectProductionWrite("Battery protection-off is not admitted.");

        public Task SetTemporaryFullChargeAsync(
            bool enabled,
            CancellationToken cancellationToken) =>
            batteryFactory.Capabilities.SupportsTemporaryFullCharge
                ? ExecuteProductionWriteAsync(
                    port => port.SetTemporaryFullChargeAsync(enabled, cancellationToken),
                    cancellationToken)
                : RejectProductionWrite("Temporary full charge is not admitted.");

        public Task ApplyOneShotLaptopControlAsync(
            ApplyOneShotLaptopControl value,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!value.IsWellFormed()
                || !batteryFactory.Capabilities.OneShotLaptopControls.Contains(value.Control))
            {
                return RejectProductionWrite(
                    "The requested one-shot laptop control is not admitted.");
            }

            return ExecuteProductionWriteAsync(
                port => port.ApplyOneShotLaptopControlAsync(value, cancellationToken),
                cancellationToken);
        }

        public Task SetLightingAsync(
            LightingSettings value,
            CancellationToken cancellationToken) =>
            lightingSession.SetLightingAsync(value, cancellationToken);

        internal void SetDesiredLightingRefreshRate(LightingRefreshRate refreshRate) =>
            lightingSession.SetDesiredLightingRefreshRate(refreshRate);

        public Task SetLightingRefreshRateAsync(
            LightingRefreshRate refreshRate,
            CancellationToken cancellationToken) =>
            lightingSession.SetLightingRefreshRateAsync(refreshRate, cancellationToken);

        public Task<byte> StepKeyboardBrightnessAsync(
            KeyboardBrightnessDirection direction,
            CancellationToken cancellationToken) =>
            lightingSession.StepKeyboardBrightnessAsync(direction, cancellationToken);

        public Task PulseReactiveKeyAsync(
            ReactiveKeyPulse pulse,
            CancellationToken cancellationToken) =>
            lightingSession.PulseReactiveKeyAsync(pulse, cancellationToken);

        public Task PushAmbientLightingFrameAsync(
            AmbientLightingFrame frame,
            CancellationToken cancellationToken) =>
            lightingSession.PushAmbientLightingFrameAsync(frame, cancellationToken);

        public Task SetPlaybackMuteIndicatorAsync(
            bool muted,
            CancellationToken cancellationToken) =>
            lightingSession.SetPlaybackMuteIndicatorAsync(muted, cancellationToken);

        public Task<LightingRendererOwnershipState> ReadLightingRendererOwnershipAsync(
            CancellationToken cancellationToken) =>
            lightingSession.ReadLightingRendererOwnershipAsync(cancellationToken);

        internal Task<LightingLowPowerOffResult?> TurnOffRetainedLightingForLowPowerAsync(
            Func<bool> freshAdmission,
            Func<bool> stillSuspending,
            CancellationToken cancellationToken) =>
            lightingSession.TurnOffRetainedLightingForLowPowerAsync(
                freshAdmission, stillSuspending, cancellationToken);

        internal Task<LightingRendererInvalidationResult> InvalidateLightingRendererAsync(
            CancellationToken cancellationToken,
            bool lowPower = false,
            Func<bool>? stillRetiring = null) =>
            lightingSession.InvalidateLightingRendererAsync(
                cancellationToken, lowPower, stillRetiring);

        internal Task<LightingRendererOwnershipState> RelinquishLightingRendererAsync(
            CancellationToken cancellationToken) =>
            lightingSession.RelinquishLightingRendererAsync(cancellationToken);

        // The service retains responsibility for Driver-mode acquisition,
        // maintenance and release; the lighting session only supplies its port.
        public async Task EnsureDriverModeAsync(CancellationToken cancellationToken)
        {
            await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                IProductionLightingPort? port =
                    await lightingSession.GetLightingPortCoreAsync(
                        ProductionLightingSession.LightingPortOperation.KeyboardDriverModeAcquisition,
                        cancellationToken).ConfigureAwait(false);
                if (port is not null)
                {
                    lightingSession.ThrowIfOrdinaryLightingWriteBlocked();
                    await port.EnsureDriverModeAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        public async Task<bool?> ReadDriverModeAsync(CancellationToken cancellationToken)
        {
            await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                IProductionLightingPort? port =
                    await lightingSession.GetLightingPortCoreAsync(
                        ProductionLightingSession.LightingPortOperation.KeyboardDriverModeRead,
                        cancellationToken).ConfigureAwait(false);
                return port is null
                    ? null
                    : await port.ReadDriverModeAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        public async Task<bool?> MaintainDriverModeAsync(
            CancellationToken cancellationToken)
        {
            await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                IProductionLightingPort? port =
                    await lightingSession.GetLightingPortCoreAsync(
                        ProductionLightingSession.LightingPortOperation.KeyboardDriverModeMaintenance,
                        cancellationToken).ConfigureAwait(false);
                lightingSession.ThrowIfOrdinaryLightingWriteBlocked();
                return port is null
                    ? null
                    : await port.MaintainDriverModeAsync(cancellationToken)
                        .ConfigureAwait(false);
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        internal async Task<bool?> ReleaseRetainedDriverModeForLowPowerAsync(
            Func<bool> freshAdmission,
            Func<bool> stillSuspending,
            CancellationToken cancellationToken)
        {
            await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                IProductionLightingPort? port = lightingSession.RetainedPort;
                if (port is null || !freshAdmission() || !stillSuspending())
                {
                    return null;
                }
                cancellationToken.ThrowIfCancellationRequested();
                return await port.ReleaseDriverModeForLowPowerAsync(stillSuspending, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        public async Task<bool?> ReleaseDriverModeAsync(
            CancellationToken cancellationToken)
        {
            await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                IProductionLightingPort? port =
                    await lightingSession.GetLightingPortCoreAsync(
                        ProductionLightingSession.LightingPortOperation.KeyboardDriverModeRelease,
                        cancellationToken).ConfigureAwait(false);
                return port is null
                    ? null
                    : await port.ReleaseDriverModeAsync(cancellationToken)
                        .ConfigureAwait(false);
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        public void Dispose()
        {
            lock (lifecycleSync)
            {
                if (closing)
                {
                    return;
                }

                closing = true;
            }

            onClosing?.Invoke();
            if (!hardwareOperationGate.Wait(shutdownBudget))
            {
                lock (lifecycleSync)
                {
                    disposed = true;
                    deferredDisposal = DisposeAfterHardwareOperationAsync();
                }
                logger?.LogError(
                    "Device-provider shutdown abandoned synchronous cleanup after waiting {ShutdownBudgetMilliseconds} ms for an active hardware operation. The handle was not force-closed; cleanup will continue if the operation drains.",
                    shutdownBudget.TotalMilliseconds);
                return;
            }

            try
            {
                lightingSession.DisposeLightingPortWithinDeadlineAsync().GetAwaiter().GetResult();
                DisposeGpuActivitySource();

                lock (lifecycleSync)
                {
                    disposed = true;
                }
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        private async Task ExecuteProductionWriteAsync(
            Func<IProductionBatteryPort, Task> write,
            CancellationToken cancellationToken,
            Action? beforeWrite = null,
            Action? afterWrite = null,
            bool retryHyperBoostAdapterPreflight = false,
            Func<IProductionBatteryPort, Task>? continueAfterSecondFanManualEntry = null,
            bool allowFreshSessionRetry = true,
            bool isManualEntryContinuation = false)
        {
            ArgumentNullException.ThrowIfNull(write);
            BladeWriteException? afterWriteFailure = null;
            BladeWriteException? exhaustedRetriablePreWrite = null;
            BladeWriteException? canceledRetriablePreWrite = null;
            BladeWriteException? manualEntryContinuationFailure = null;
            BladeWriteFailurePhase retryFailurePhase = BladeWriteFailurePhase.Unspecified;
            bool retryPreWriteOpen = false;
            bool continuingAfterManualEntry = isManualEntryContinuation;
            Func<IProductionBatteryPort, Task> attemptWrite = write;
            try
            {
                await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (isManualEntryContinuation)
                {
                    throw new BladeWriteException(
                        WriteSubmissionState.Indeterminate,
                        BladeWriteFailurePhase.AdjustableFanSecondManualEntrySubmission,
                        "Adjustable fan manual entry may have applied, but the fresh target-only continuation was canceled before a verified session opened.");
                }

                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "The hardware write was canceled before a verified session opened.");
            }
            catch (ObjectDisposedException exception)
            {
                if (isManualEntryContinuation)
                {
                    throw new BladeWriteException(
                        WriteSubmissionState.Indeterminate,
                        BladeWriteFailurePhase.AdjustableFanSecondManualEntrySubmission,
                        "Adjustable fan manual entry may have applied, but the fresh target-only continuation was blocked because the device provider is closing.",
                        exception);
                }

                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "The hardware write was blocked because the device provider is closing.",
                    exception);
            }

            try
            {
                int attemptCount = allowFreshSessionRetry ? 2 : 1;
                for (int attempt = 0; attempt < attemptCount; attempt++)
                {
                    bool isFreshPreWriteRetry = attempt > 0;
                    retryPreWriteOpen = isFreshPreWriteRetry;
                    try
                    {
                        if (isFreshPreWriteRetry)
                        {
                            await preWriteRetryDelay(
                                    PreWriteRetryDelay,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }

                        beforeWrite?.Invoke();
                        cancellationToken.ThrowIfCancellationRequested();
                        HidSessionResult<IProductionBatteryPort> opened =
                            await batteryFactory.OpenAsync(cancellationToken)
                                .ConfigureAwait(false);
                        if (opened.Value is not { } port)
                        {
                            throw MapWriteOpenFailure(opened.Failure);
                        }

                        await using (port.ConfigureAwait(false))
                        {
                            if (opened.Failure is not null)
                            {
                                throw MapWriteOpenFailure(opened.Failure);
                            }

                            cancellationToken.ThrowIfCancellationRequested();
                            retryPreWriteOpen = false;
                            await attemptWrite(port).ConfigureAwait(false);
                            try
                            {
                                afterWrite?.Invoke();
                            }
                            catch (BladeWriteException exception)
                            {
                                // Preserve only the paired post-write failure.
                                // Other port failures retain the provider's
                                // conservative indeterminate mapping unless an
                                // exact pre-submission phase is handled below.
                                afterWriteFailure = exception;
                                throw;
                            }
                        }

                        return;
                    }
                    catch (BladeWriteException exception) when (
                        continuingAfterManualEntry
                        && exception.SubmissionState == WriteSubmissionState.KnownNotApplied)
                    {
                        throw new BladeWriteException(
                            WriteSubmissionState.Indeterminate,
                            BladeWriteFailurePhase.AdjustableFanSecondManualEntrySubmission,
                            "Adjustable fan manual entry may have applied, but the fresh target-only continuation stopped before any fan target was submitted. "
                            + exception.Message,
                            exception);
                    }
                    catch (BladeWriteException exception) when (
                        exception.SubmissionState == WriteSubmissionState.KnownNotApplied
                        && (exception.FailurePhase
                                == BladeWriteFailurePhase.ThermalPreWriteRead
                            || retryHyperBoostAdapterPreflight
                                && exception.FailurePhase
                                    == BladeWriteFailurePhase.HyperBoostAdapterEligibilityRead))
                    {
                        if (attempt + 1 < attemptCount)
                        {
                            retryFailurePhase = exception.FailurePhase;
                            logger?.LogInformation(
                                "{PreWritePhase} failed before submission; retrying once on a fresh verified session.",
                                exception.FailurePhase);
                            continue;
                        }

                        exhaustedRetriablePreWrite = exception;
                        throw;
                    }
                    catch (BladeWriteException exception) when (
                        exception.SubmissionState == WriteSubmissionState.Indeterminate
                        && exception.FailurePhase
                            == BladeWriteFailurePhase.AdjustableFanSecondManualEntrySubmission
                        && continueAfterSecondFanManualEntry is not null
                        && attempt + 1 < attemptCount)
                    {
                        retryFailurePhase = exception.FailurePhase;
                        manualEntryContinuationFailure = exception;
                        continuingAfterManualEntry = true;
                        // The fresh port's target-only setter first proves that
                        // both channel reads agree on manual mode. It cannot
                        // replay either possibly submitted entry command.
                        attemptWrite = continueAfterSecondFanManualEntry;
                        logger?.LogInformation(
                            "The second adjustable-fan manual-entry submission was indeterminate; opening one fresh verified session to reconfirm both manual channels before continuing with fan targets only.");
                        continue;
                    }
                    catch (BladeWriteException exception) when (
                        isFreshPreWriteRetry
                        && retryPreWriteOpen
                        && cancellationToken.IsCancellationRequested
                        && exception.SubmissionState
                            == WriteSubmissionState.KnownNotApplied)
                    {
                        canceledRetriablePreWrite = new BladeWriteException(
                            WriteSubmissionState.KnownNotApplied,
                            retryFailurePhase,
                            "The fresh pre-write read retry was canceled before any hardware command was submitted.",
                            exception);
                        throw canceledRetriablePreWrite;
                    }
                }
            }
            catch (CapabilityUnavailableException exception) when (
                continuingAfterManualEntry)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    BladeWriteFailurePhase.AdjustableFanSecondManualEntrySubmission,
                    "Adjustable fan manual entry may have applied, but the fresh target-only continuation was refused before any fan target was submitted.",
                    exception);
            }
            catch (CapabilityUnavailableException)
            {
                throw;
            }
            catch (BladeWriteException exception)
                when (ReferenceEquals(exception, afterWriteFailure)
                    || ReferenceEquals(
                        exception,
                        exhaustedRetriablePreWrite)
                    || ReferenceEquals(exception, canceledRetriablePreWrite))
            {
                throw;
            }
            catch (BladeWriteException exception)
                when (exception.SubmissionState
                    == WriteSubmissionState.KnownNotApplied)
            {
                // The port owns the exact submission boundary. Preserve its
                // definitive pre-submission classification instead of making a
                // failed open, query, or precondition look like an uncertain write.
                throw;
            }
            catch (BladeWriteException)
            {
                // The production port owns the exact submission boundary and
                // emits sanitized failure details. Preserve its indeterminate
                // result without retrying or replacing the diagnostic.
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (continuingAfterManualEntry)
                {
                    throw new BladeWriteException(
                        WriteSubmissionState.Indeterminate,
                        BladeWriteFailurePhase.AdjustableFanSecondManualEntrySubmission,
                        "Adjustable fan manual entry may have applied, but the fresh target-only continuation was canceled before a fan target submission was confirmed.",
                        manualEntryContinuationFailure);
                }

                if (retryPreWriteOpen)
                {
                    throw new BladeWriteException(
                        WriteSubmissionState.KnownNotApplied,
                        retryFailurePhase,
                        "The fresh pre-write read retry was canceled before any hardware command was submitted.");
                }

                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "The hardware write was canceled after a verified session opened.");
            }
            catch (Exception exception)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "The hardware write outcome is indeterminate.",
                    exception);
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        private async Task DisposeAfterHardwareOperationAsync()
        {
            await hardwareOperationGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await lightingSession.DisposeLightingPortWithinDeadlineAsync().ConfigureAwait(false);
                DisposeGpuActivitySource();
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        private void DisposeGpuActivitySource()
        {
            if (gpuActivitySource is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        private async Task EnterHardwareOperationAsync(CancellationToken cancellationToken)
        {
            Task waitForHardware;
            lock (lifecycleSync)
            {
                ObjectDisposedException.ThrowIf(closing || disposed, this);
                waitForHardware = hardwareOperationGate.WaitAsync(cancellationToken);
            }

            await waitForHardware.ConfigureAwait(false);
            try
            {
                ThrowIfClosingOrDisposed();
            }
            catch
            {
                hardwareOperationGate.Release();
                throw;
            }
        }

        private void ThrowIfClosingOrDisposed()
        {
            lock (lifecycleSync)
            {
                ObjectDisposedException.ThrowIf(closing || disposed, this);
            }
        }

        private static void ThrowReadOpenFailure(
            HidSessionFailureKind? failure,
            CancellationToken cancellationToken)
        {
            switch (failure)
            {
                case HidSessionFailureKind.Canceled when cancellationToken.IsCancellationRequested:
                case HidSessionFailureKind.QueuedCanceled when cancellationToken.IsCancellationRequested:
                    throw new OperationCanceledException(cancellationToken);
                case HidSessionFailureKind.Timeout:
                    throw new TimeoutException("Battery state query timed out.");
                case HidSessionFailureKind.IdentityBlocked:
                    throw new FailClosedDeviceException(
                        "Battery state query was blocked by exact device identity verification.");
                default:
                    throw new IOException("Battery state query failed closed.");
            }
        }

        private static BladeWriteException MapWriteOpenFailure(
            HidSessionFailureKind? failure)
        {
            WriteSubmissionState submissionState = failure is
                HidSessionFailureKind.Timeout
                or HidSessionFailureKind.Poisoned
                or HidSessionFailureKind.Disposed
                ? WriteSubmissionState.Indeterminate
                : WriteSubmissionState.KnownNotApplied;
            return new BladeWriteException(
                submissionState,
                submissionState == WriteSubmissionState.KnownNotApplied
                    ? "The hardware write was blocked before submission."
                    : "The hardware write could not establish an unambiguous verified session.");
        }

        private static Task RejectProductionWrite(string message) => Task.FromException(
            new CapabilityUnavailableException(message));

        private sealed class FailClosedDeviceException(string message) : Exception(message);
    }

    private sealed class UnavailableProductionLightingFactory : IProductionLightingPortFactory
    {
        internal static UnavailableProductionLightingFactory Instance { get; } = new();

        public DeviceCapabilities Capabilities => DeviceCapabilities.Unavailable();

        public Task<HidSessionResult<IProductionLightingPort>> OpenAsync(
            CancellationToken cancellationToken) => Task.FromResult(
                new HidSessionResult<IProductionLightingPort>(
                    null,
                    cancellationToken.IsCancellationRequested
                        ? HidSessionFailureKind.Canceled
                        : HidSessionFailureKind.IdentityBlocked));
    }
}
