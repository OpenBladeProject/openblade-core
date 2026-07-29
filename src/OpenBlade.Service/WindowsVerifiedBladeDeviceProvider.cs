using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

[assembly: InternalsVisibleTo("OpenBlade.Service.Tests")]

namespace OpenBlade.Service;

public sealed class WindowsVerifiedBladeDeviceProvider : IVerifiedBladeDeviceProvider, IDisposable
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
    private readonly SerializedBladeDevice device;
    private readonly object providerLifecycleSync = new();
    private long lastReadyProbeTimestamp;
    private int disposed;

    public WindowsVerifiedBladeDeviceProvider()
        : this(
            ProductionProbeFactory,
            ProductionPortFactory(),
            ProductionFanRpmFactory(),
            ProductionLightingPortFactory(),
            new NvidiaGpuClockOffsetController())
    {
    }

    internal WindowsVerifiedBladeDeviceProvider(
        Func<BatteryValidationProbeResult> probe,
        IProductionBatteryPortFactory batteryFactory,
        IFanRpmQueryRunFactory fanRpmFactory,
        IProductionLightingPortFactory? lightingFactory = null,
        IGpuClockOffsetController? gpuClockOffsets = null,
        ILogger<WindowsVerifiedBladeDeviceProvider>? logger = null,
        TimeSpan? shutdownBudget = null)
    {
        TimeSpan effectiveShutdownBudget = shutdownBudget ?? DefaultShutdownBudget;
        if (effectiveShutdownBudget <= TimeSpan.Zero ||
            effectiveShutdownBudget.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(shutdownBudget));
        }

        this.probe = probe ?? throw new ArgumentNullException(nameof(probe));
        device = new SerializedBladeDevice(
            batteryFactory ?? throw new ArgumentNullException(nameof(batteryFactory)),
            fanRpmFactory ?? throw new ArgumentNullException(nameof(fanRpmFactory)),
            lightingFactory ?? UnavailableProductionLightingFactory.Instance,
            gpuClockOffsets ?? UnavailableGpuClockOffsetController.Instance,
            logger,
            effectiveShutdownBudget);
    }

    public bool TryGetVerifiedDevice(out IBladeDevice verifiedDevice)
    {
        verifiedDevice = null!;
        if (Volatile.Read(ref disposed) != 0)
        {
            return false;
        }

        try
        {
            BatteryValidationProbeClassification classification = probe().Classification;
            if (!IsReadyClassification(classification))
            {
                if (IsTransientProbeClassification(classification)
                    && TryGetRecentlyVerifiedDevice(out verifiedDevice))
                {
                    return true;
                }

                Volatile.Write(ref lastReadyProbeTimestamp, 0);
                return false;
            }

            lock (providerLifecycleSync)
            {
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
        catch
        {
            return TryGetRecentlyVerifiedDevice(out verifiedDevice);
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

    public bool TryGetOwnedKeyTriggeredLightingDevice(out IBladeDevice verifiedDevice)
    {
        verifiedDevice = null!;
        if (Volatile.Read(ref disposed) != 0
            || !device.HasOwnedKeyTriggeredRenderer)
        {
            return false;
        }

        lock (providerLifecycleSync)
        {
            if (disposed != 0 || !device.HasOwnedKeyTriggeredRenderer)
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
            || !device.HasOwnedAmbientRenderer)
        {
            return false;
        }

        lock (providerLifecycleSync)
        {
            if (disposed != 0 || !device.HasOwnedAmbientRenderer)
            {
                return false;
            }

            // Ambient Awareness already owns an exact-device lighting port.
            // Reusing it avoids a full HID discovery pass for every video frame.
            verifiedDevice = device;
            return true;
        }
    }

    public Task<LightingRendererOwnershipState> InvalidateLightingRendererAsync(
        CancellationToken cancellationToken) =>
        Volatile.Read(ref disposed) != 0
            ? Task.FromResult(LightingRendererOwnershipState.None)
            : device.InvalidateLightingRendererAsync(cancellationToken);

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

    private sealed class SerializedBladeDevice : IBladeDevice, IDisposable
    {
        private const int MaximumBatteryReadAttempts = 3;

        private readonly IProductionBatteryPortFactory batteryFactory;
        private readonly IFanRpmQueryRunFactory fanRpmFactory;
        private readonly IProductionLightingPortFactory lightingFactory;
        private readonly IGpuClockOffsetController gpuClockOffsets;
        private readonly ILogger<WindowsVerifiedBladeDeviceProvider>? logger;
        private readonly TimeSpan shutdownBudget;
        private readonly object lifecycleSync = new();
        private readonly SemaphoreSlim hardwareOperationGate = new(1, 1);
        private Task? deferredDisposal;
        private bool closing;
        private bool disposed;
        private IProductionLightingPort? lightingPort;
        // Desired Windows playback state, retained independently of any one
        // renderer port so a recycled exact-device session can reapply it.
        private int desiredPlaybackMuteState = -1;

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

                return Volatile.Read(ref lightingPort)?.OwnershipState is
                    LightingRendererOwnershipState.Reactive or
                    LightingRendererOwnershipState.Ripple;
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

                return Volatile.Read(ref lightingPort)?.OwnershipState ==
                    LightingRendererOwnershipState.AmbientAwareness;
            }
        }

        internal SerializedBladeDevice(
            IProductionBatteryPortFactory batteryFactory,
            IFanRpmQueryRunFactory fanRpmFactory,
            IProductionLightingPortFactory lightingFactory,
            IGpuClockOffsetController gpuClockOffsets,
            ILogger<WindowsVerifiedBladeDeviceProvider>? logger,
            TimeSpan shutdownBudget)
        {
            this.batteryFactory = batteryFactory;
            this.fanRpmFactory = fanRpmFactory;
            this.lightingFactory = lightingFactory;
            this.gpuClockOffsets = gpuClockOffsets;
            this.logger = logger;
            this.shutdownBudget = shutdownBudget;
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
                    await GetLightingPortCoreAsync(cancellationToken).ConfigureAwait(false);
                return lighting is null
                    ? batteryCapabilities
                    : batteryCapabilities with
                    {
                        LightingEffects = lighting.Capabilities.LightingEffects,
                        LogoLightingModes = lighting.Capabilities.LogoLightingModes,
                        SupportsKeyboardBrightness =
                            lighting.Capabilities.SupportsKeyboardBrightness,
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
            await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                bool observedTimeout = false;
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        return await ReadBatteryStateOnceAsync(cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (RazerProtocolException exception) when (
                        attempt < MaximumBatteryReadAttempts - 1 &&
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
                        if (attempt >= MaximumBatteryReadAttempts - 1)
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
                    $"Battery state query failed protocol validation ({exception.ValidationFailure}).",
                    exception.ValidationFailure);
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
                    await ReadLightingStateCoreAsync(cancellationToken).ConfigureAwait(false);
                LogoLightingMode? logo =
                    await ReadLogoLightingStateCoreAsync(cancellationToken)
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

        private async Task<ConfirmedDeviceState> ReadBatteryStateOnceAsync(
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

                ConfirmedBatteryState protection =
                    await port.QueryProtectionAsync(cancellationToken).ConfigureAwait(false);
                bool? temporary = port.SupportsTemporaryFullChargeQuery
                    ? await port.QueryTemporaryFullChargeAsync(cancellationToken)
                        .ConfigureAwait(false)
                    : null;
                ConfirmedBatteryState battery = new(
                    protection.ProtectionEnabled,
                    protection.ChargeLimitPercent,
                    temporary);
                ProductionPerformanceFanState performanceFan =
                    await port.QueryPerformanceFanStateAsync(cancellationToken)
                        .ConfigureAwait(false);
                GpuClockOffsetCapabilities? gpuOffsets =
                    performanceFan.Performance == PerformancePreset.Custom
                        ? ApplyGpuPolicy(gpuClockOffsets.Query())
                        : null;
                CustomPerformanceSettings? customPerformance =
                    performanceFan.CustomPerformance;
                if (customPerformance is not null && gpuOffsets is not null)
                {
                    customPerformance = customPerformance with
                    {
                        GpuClockOffsets = gpuOffsets.Current,
                    };
                }
                LightingSettings? lighting =
                    await ReadLightingStateCoreAsync(cancellationToken).ConfigureAwait(false);
                LogoLightingMode? logo =
                    await ReadLogoLightingStateCoreAsync(cancellationToken)
                        .ConfigureAwait(false);
                return new ConfirmedDeviceState(
                    performanceFan.Performance,
                    performanceFan.Fans,
                    battery,
                    lighting)
                {
                    CustomPerformance = customPerformance,
                    GpuClockOffsets = gpuOffsets,
                    LogoLighting = logo,
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
            catch (ObjectDisposedException)
            {
                throw;
            }
            catch
            {
                return AdapterPowerState.Unknown;
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

        public async Task<bool?> ReadStartupAnimationAsync(
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
                cancellationToken);

        public async Task SetPerformanceAsync(
            PerformancePreset value,
            CustomPerformanceSettings? customPerformance,
            CancellationToken cancellationToken)
        {
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

            if (value == PerformancePreset.Custom
                && customPerformance?.GpuClockOffsets is { } preflightOffsets)
            {
                GpuClockOffsetCapabilities? capabilities =
                    batteryFactory.Capabilities.GpuOverclock is null
                        or { Access: DeviceFeatureAccess.ReadWrite }
                        ? ApplyGpuPolicy(gpuClockOffsets.Query())
                        : null;
                if (capabilities is null || !capabilities.Supports(preflightOffsets))
                {
                    await RejectProductionWrite(
                        capabilities is null
                            ? "GPU clock tuning is unavailable."
                            : "The requested GPU clock offsets are outside the ranges reported by the NVIDIA driver.")
                        .ConfigureAwait(false);
                    return;
                }
            }

            await ExecuteProductionWriteAsync(
                port => value == PerformancePreset.Custom
                    ? port.SetCustomPerformanceAsync(customPerformance!, cancellationToken)
                    : port.SetPerformanceAsync(value, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            if (value == PerformancePreset.Custom
                && customPerformance?.GpuClockOffsets is { } appliedOffsets)
            {
                try
                {
                    // The firmware power mode has already been committed. Complete
                    // the paired NVML operation even if shutdown cancellation raced
                    // with the successful HID write.
                    gpuClockOffsets.Set(appliedOffsets);
                }
                catch (BladeWriteException exception)
                {
                    throw new BladeWriteException(
                        WriteSubmissionState.Indeterminate,
                        $"Custom power mode was applied, but GPU clock tuning failed. {exception.Message}",
                        exception);
                }
            }
        }

        public async Task SetGpuClockOffsetsAsync(
            GpuClockOffsetSettings value,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!GpuClockOffsetSettings.IsWellFormed(value))
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "GPU clock offsets are invalid.");
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
                GpuClockOffsetCapabilities? capabilities =
                    batteryFactory.Capabilities.GpuOverclock is null
                        or { Access: DeviceFeatureAccess.ReadWrite }
                        ? ApplyGpuPolicy(gpuClockOffsets.Query())
                        : null;
                if (capabilities is null || !capabilities.Supports(value))
                {
                    throw new CapabilityUnavailableException(
                        capabilities is null
                            ? "GPU clock tuning is unavailable."
                            : "The requested GPU clock offsets are outside the ranges reported by the NVIDIA driver.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                gpuClockOffsets.Set(value);
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

        public Task SetFanControlAsync(
            FanControlSettings value,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(value);
            DeviceCapabilities capabilities = batteryFactory.Capabilities;
            return capabilities.FanModes.Contains(value.Mode)
                && (value.Mode != FanMode.Fixed
                    || capabilities.FanSpeedRange?.Supports(value.FixedSpeedRpm) == true)
                ? ExecuteProductionWriteAsync(
                    port => port.SetFanControlAsync(value, cancellationToken),
                    cancellationToken)
                : RejectProductionWrite("The requested fan control is not admitted.");
        }

        public Task SetFanTargetsAsync(
            int fan1Rpm,
            int fan2Rpm,
            CancellationToken cancellationToken) =>
            batteryFactory.Capabilities.FanModes.Contains(FanMode.ManualCurve)
                ? ExecuteProductionWriteAsync(
                    port => port.SetFanTargetsAsync(
                        fan1Rpm,
                        fan2Rpm,
                        cancellationToken),
                    cancellationToken)
                : RejectProductionWrite("Manual fan targets are not admitted.");

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
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!LightingSettingsValidation.IsWellFormed(value)
                || !lightingFactory.Capabilities.LightingEffects.Contains(value.Effect)
                || value.LogoMode is { } logoMode
                    && !lightingFactory.Capabilities.LogoLightingModes.Contains(logoMode))
            {
                return RejectProductionWrite("The requested lighting setting is not admitted.");
            }

            return ExecuteProductionLightingWriteAsync(value, cancellationToken);
        }

        public Task<byte> StepKeyboardBrightnessAsync(
            KeyboardBrightnessDirection direction,
            CancellationToken cancellationToken)
        {
            if (!Enum.IsDefined(direction))
            {
                return Task.FromException<byte>(
                    new ArgumentOutOfRangeException(nameof(direction)));
            }

            return ExecuteProductionKeyboardBrightnessStepAsync(
                direction,
                cancellationToken);
        }

        public Task PulseReactiveKeyAsync(
            ReactiveKeyPulse pulse,
            CancellationToken cancellationToken) =>
            lightingFactory.Capabilities.LightingEffects.Contains(LightingEffect.Reactive)
                || lightingFactory.Capabilities.LightingEffects.Contains(LightingEffect.Ripple)
                ? ExecuteProductionLightingPulseAsync(pulse, cancellationToken)
                : RejectProductionWrite("Key-triggered lighting is not admitted.");

        public Task PushAmbientLightingFrameAsync(
            AmbientLightingFrame frame,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(frame);
            return frame.IsWellFormed()
                && lightingFactory.Capabilities.LightingEffects.Contains(
                    LightingEffect.AmbientAwareness)
                    ? ExecuteProductionAmbientLightingFrameAsync(frame.Copy(), cancellationToken)
                    : RejectProductionWrite("Ambient Awareness input is not admitted.");
        }

        public Task SetPlaybackMuteIndicatorAsync(
            bool muted,
            CancellationToken cancellationToken) =>
            lightingFactory.Capabilities.LightingEffects.Count > 0
                ? ExecuteProductionPlaybackMuteIndicatorAsync(
                    muted,
                    cancellationToken)
                : RejectProductionWrite("Playback mute indication is not admitted.");

        public async Task<LightingRendererOwnershipState>
            ReadLightingRendererOwnershipAsync(CancellationToken cancellationToken)
        {
            await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                IProductionLightingPort? current = lightingPort;
                LightingRendererOwnershipState observed = current?.OwnershipState
                    ?? LightingRendererOwnershipState.None;
                if (observed == LightingRendererOwnershipState.Faulted)
                {
                    // Preserve Faulted for this observation, but retire the poisoned
                    // writer before a later explicit lighting action opens a new one.
                    await ResetLightingPortCoreAsync(current).ConfigureAwait(false);
                }

                return observed;
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        public async Task EnsureDriverModeAsync(CancellationToken cancellationToken)
        {
            await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                IProductionLightingPort? port =
                    await GetLightingPortCoreAsync(cancellationToken).ConfigureAwait(false);
                if (port is not null)
                {
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
                    await GetLightingPortCoreAsync(cancellationToken).ConfigureAwait(false);
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
                    await GetLightingPortCoreAsync(cancellationToken).ConfigureAwait(false);
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

        public async Task<bool?> ReleaseDriverModeAsync(
            CancellationToken cancellationToken)
        {
            await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                IProductionLightingPort? port =
                    await GetLightingPortCoreAsync(cancellationToken).ConfigureAwait(false);
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

        internal async Task<LightingRendererOwnershipState> InvalidateLightingRendererAsync(
            CancellationToken cancellationToken)
        {
            await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                IProductionLightingPort? current = lightingPort;
                if (current is null)
                {
                    return LightingRendererOwnershipState.None;
                }

                lightingPort = null;
                try
                {
                    return await current.InvalidateAsync().ConfigureAwait(false);
                }
                catch
                {
                    return LightingRendererOwnershipState.Faulted;
                }
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
                DisposeLightingPortWithinDeadlineAsync().GetAwaiter().GetResult();

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
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(write);
            try
            {
                await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "The hardware write was canceled before a verified session opened.");
            }
            catch (ObjectDisposedException exception)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "The hardware write was blocked because the device provider is closing.",
                    exception);
            }

            try
            {
                HidSessionResult<IProductionBatteryPort> opened =
                    await batteryFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
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

                    await write(port).ConfigureAwait(false);
                }
            }
            catch (CapabilityUnavailableException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
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

        private async Task ExecuteProductionLightingWriteAsync(
            LightingSettings value,
            CancellationToken cancellationToken)
        {
            IProductionLightingPort? port = null;
            try
            {
                await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "The lighting write was canceled before a verified session opened.");
            }
            catch (ObjectDisposedException exception)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "The lighting write was blocked because the device provider is closing.",
                    exception);
            }

            try
            {
                port = await GetLightingPortCoreAsync(cancellationToken).ConfigureAwait(false);
                if (port is null
                    || !port.Capabilities.LightingEffects.Contains(value.Effect)
                    || value.LogoMode is { } logoMode
                        && !port.Capabilities.LogoLightingModes.Contains(logoMode))
                {
                    throw new BladeWriteException(
                        WriteSubmissionState.KnownNotApplied,
                        "The lighting writer could not open an exact-device session.");
                }

                await port.SetLightingAsync(value, cancellationToken).ConfigureAwait(false);
            }
            catch (CapabilityUnavailableException)
            {
                throw;
            }
            catch (BladeWriteException exception)
            {
                if (exception.SubmissionState == WriteSubmissionState.Indeterminate
                    && port?.OwnershipState is
                        LightingRendererOwnershipState.None or
                        LightingRendererOwnershipState.Faulted)
                {
                    await ResetLightingPortCoreAsync(port).ConfigureAwait(false);
                }

                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "The lighting write was canceled after a verified session opened.");
            }
            catch (Exception exception)
            {
                await ResetLightingPortCoreAsync(port).ConfigureAwait(false);
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "The lighting write outcome is indeterminate.",
                    exception);
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        private async Task<byte> ExecuteProductionKeyboardBrightnessStepAsync(
            KeyboardBrightnessDirection direction,
            CancellationToken cancellationToken)
        {
            IProductionLightingPort? port = null;
            try
            {
                await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "The keyboard brightness change was canceled before a verified session opened.");
            }
            catch (ObjectDisposedException exception)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "The keyboard brightness change was blocked because the device provider is closing.",
                    exception);
            }

            try
            {
                port = await GetLightingPortCoreAsync(cancellationToken).ConfigureAwait(false);
                if (port is null
                    || !port.Capabilities.SupportsKeyboardBrightness)
                {
                    throw new BladeWriteException(
                        WriteSubmissionState.KnownNotApplied,
                        "The lighting writer could not open an exact-device session.");
                }

                return await port.StepBrightnessAsync(direction, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CapabilityUnavailableException)
            {
                throw;
            }
            catch (BladeWriteException exception)
            {
                if (exception.SubmissionState == WriteSubmissionState.Indeterminate)
                {
                    await ResetLightingPortCoreAsync(port).ConfigureAwait(false);
                }

                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "The keyboard brightness change was canceled after a verified session opened.");
            }
            catch (Exception exception)
            {
                await ResetLightingPortCoreAsync(port).ConfigureAwait(false);
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "The keyboard brightness change outcome is indeterminate.",
                    exception);
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        private async Task ExecuteProductionLightingPulseAsync(
            ReactiveKeyPulse pulse,
            CancellationToken cancellationToken)
        {
            // A pulse now changes only the in-memory key-triggered effect and
            // wakes its already-running matrix stream. It performs no HID I/O,
            // so it must not wait behind unrelated firmware observations on the
            // global hardware gate. The lighting port still serializes this with
            // effect replacement and safely rejects a concurrent close.
            IProductionLightingPort? port = Volatile.Read(ref lightingPort);
            try
            {
                if (port is null
                    || port.OwnershipState is not (
                        LightingRendererOwnershipState.Reactive or
                        LightingRendererOwnershipState.Ripple))
                {
                    throw new BladeWriteException(
                        WriteSubmissionState.KnownNotApplied,
                        "The key-triggered lighting renderer is not active.");
                }

                await port.PulseReactiveKeyAsync(pulse, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CapabilityUnavailableException)
            {
                throw;
            }
            catch (BladeWriteException)
            {
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "The key-triggered lighting input was canceled before it was queued.");
            }
            catch (Exception exception)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "The key-triggered lighting input could not be queued.",
                    exception);
            }
        }

        private async Task ExecuteProductionAmbientLightingFrameAsync(
            AmbientLightingFrame frame,
            CancellationToken cancellationToken)
        {
            IProductionLightingPort? ownedPort = Volatile.Read(ref lightingPort);
            if (ownedPort?.OwnershipState == LightingRendererOwnershipState.AmbientAwareness)
            {
                try
                {
                    // The exact-device port is already admitted and owns its
                    // own writer gate. Avoid re-entering the provider-wide HID
                    // gate on every 20 Hz frame.
                    await ownedPort.PushAmbientLightingFrameAsync(frame, cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }
                catch (CapabilityUnavailableException)
                {
                    throw;
                }
                catch (BladeWriteException)
                {
                    throw;
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    throw new BladeWriteException(
                        WriteSubmissionState.KnownNotApplied,
                        "Ambient Awareness was canceled before the next frame was queued.");
                }
                catch (Exception exception)
                {
                    throw new BladeWriteException(
                        WriteSubmissionState.Indeterminate,
                        "The Ambient Awareness frame outcome is indeterminate.",
                        exception);
                }
            }

            IProductionLightingPort? port = null;
            try
            {
                await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "Ambient Awareness was canceled before a verified session opened.");
            }
            catch (ObjectDisposedException exception)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "Ambient Awareness was blocked because the device provider is closing.",
                    exception);
            }

            try
            {
                port = await GetLightingPortCoreAsync(cancellationToken).ConfigureAwait(false);
                if (port is null
                    || !port.Capabilities.LightingEffects.Contains(
                        LightingEffect.AmbientAwareness))
                {
                    throw new BladeWriteException(
                        WriteSubmissionState.KnownNotApplied,
                        "The lighting writer could not open an exact-device session.");
                }

                await port.PushAmbientLightingFrameAsync(frame, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CapabilityUnavailableException)
            {
                throw;
            }
            catch (BladeWriteException exception)
            {
                if (exception.SubmissionState == WriteSubmissionState.Indeterminate)
                {
                    await ResetLightingPortCoreAsync(port).ConfigureAwait(false);
                }

                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "Ambient Awareness was canceled after a verified session opened.");
            }
            catch (Exception exception)
            {
                await ResetLightingPortCoreAsync(port).ConfigureAwait(false);
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "The Ambient Awareness input outcome is indeterminate.",
                    exception);
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        private async Task ExecuteProductionPlaybackMuteIndicatorAsync(
            bool muted,
            CancellationToken cancellationToken)
        {
            Volatile.Write(ref desiredPlaybackMuteState, muted ? 1 : 0);
            IProductionLightingPort? port = null;
            try
            {
                await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "Playback mute indication was canceled before a verified session opened.");
            }
            catch (ObjectDisposedException exception)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "Playback mute indication was blocked because the device provider is closing.",
                    exception);
            }

            try
            {
                port = await GetLightingPortCoreAsync(cancellationToken).ConfigureAwait(false);
                if (port is null || port.Capabilities.LightingEffects.Count == 0)
                {
                    throw new BladeWriteException(
                        WriteSubmissionState.KnownNotApplied,
                        "The lighting writer could not open an exact-device session.");
                }

                await port.SetPlaybackMuteIndicatorAsync(muted, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (CapabilityUnavailableException)
            {
                throw;
            }
            catch (BladeWriteException exception)
            {
                if (exception.SubmissionState == WriteSubmissionState.Indeterminate
                    || port?.OwnershipState is
                        LightingRendererOwnershipState.None or
                        LightingRendererOwnershipState.Faulted)
                {
                    await ResetLightingPortCoreAsync(port).ConfigureAwait(false);
                }

                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "Playback mute indication was canceled after a verified session opened.");
            }
            catch (Exception exception)
            {
                await ResetLightingPortCoreAsync(port).ConfigureAwait(false);
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "The playback mute indicator outcome is indeterminate.",
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
                await DisposeLightingPortWithinDeadlineAsync().ConfigureAwait(false);
            }
            finally
            {
                hardwareOperationGate.Release();
            }
        }

        private async Task DisposeLightingPortWithinDeadlineAsync()
        {
            IProductionLightingPort? currentLighting = lightingPort;
            lightingPort = null;
            if (currentLighting is null)
            {
                return;
            }

            try
            {
                bool drained = await currentLighting
                    .DisposeWithinCleanupDeadlineAsync()
                    .ConfigureAwait(false);
                if (!drained)
                {
                    logger?.LogError(
                        "The production lighting port did not drain within its cleanup deadline. Native cleanup remains retained and the handle was not force-closed.");
                }
            }
            catch (Exception exception)
            {
                logger?.LogError(
                    exception,
                    "The production lighting port failed during device-provider shutdown.");
            }
        }

        private async Task<IProductionLightingPort?> GetLightingPortCoreAsync(
            CancellationToken cancellationToken)
        {
            if (lightingPort is not null)
            {
                return lightingPort;
            }

            HidSessionResult<IProductionLightingPort> opened;
            try
            {
                opened = await lightingFactory.OpenAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }

            IProductionLightingPort? port = opened.Value;
            if (port is null || opened.Failure is not null)
            {
                if (port is not null)
                {
                    await port.DisposeAsync().ConfigureAwait(false);
                }

                return null;
            }

            int muted = Volatile.Read(ref desiredPlaybackMuteState);
            if (muted >= 0)
            {
                try
                {
                    await port.SetPlaybackMuteIndicatorAsync(
                        muted != 0,
                        cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await port.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }

            lightingPort = port;
            return port;
        }

        private async Task<LightingSettings?> ReadLightingStateCoreAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                IProductionLightingPort? port =
                    await GetLightingPortCoreAsync(cancellationToken).ConfigureAwait(false);
                return port is null
                    ? null
                    : await port.ReadStateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ObjectDisposedException)
            {
                throw;
            }
            catch
            {
                // Brightness uses the command interface while the host renderer
                // writes the matrix through a separate interface. A transient
                // brightness read must not dispose an otherwise healthy renderer.
                // Retire only a port whose renderer has independently reported
                // that it is no longer usable.
                IProductionLightingPort? current = lightingPort;
                if (current?.OwnershipState is
                    LightingRendererOwnershipState.None or
                    LightingRendererOwnershipState.Faulted)
                {
                    await ResetLightingPortCoreAsync(current).ConfigureAwait(false);
                }
                return null;
            }
        }

        private async Task<LogoLightingMode?> ReadLogoLightingStateCoreAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                IProductionLightingPort? port =
                    await GetLightingPortCoreAsync(cancellationToken).ConfigureAwait(false);
                return port is null
                    ? null
                    : await port.ReadLogoStateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ObjectDisposedException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        private async Task ResetLightingPortCoreAsync(IProductionLightingPort? port)
        {
            if (port is null || !ReferenceEquals(lightingPort, port))
            {
                return;
            }

            lightingPort = null;
            try
            {
                await port.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
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
                HidSessionFailureKind.IdentityBlocked
                or HidSessionFailureKind.Timeout
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
