using System.Runtime.CompilerServices;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

[assembly: InternalsVisibleTo("OpenBlade.Service.Tests")]

namespace OpenBlade.Service;

public sealed class WindowsVerifiedBladeDeviceProvider : IVerifiedBladeDeviceProvider, IDisposable
{
    internal static Func<BatteryValidationProbeResult> ProductionProbeFactory { get; } =
        BatteryValidationFactory.ProbeWindowsForService;

    internal static Func<IProductionBatteryPortFactory> ProductionPortFactory { get; } =
        ServiceComposition.CreateProductionBatteryFactory;

    internal static Func<IFanRpmQueryRunFactory> ProductionFanRpmFactory { get; } =
        BatteryValidationFactory.CreateWindowsFanRpmQuery;

    internal static Func<IProductionLightingPortFactory> ProductionLightingPortFactory { get; } =
        ServiceComposition.CreateProductionLightingFactory;

    private readonly Func<BatteryValidationProbeResult> probe;
    private readonly SerializedBladeDevice device;
    private readonly object providerLifecycleSync = new();
    private int disposed;

    public WindowsVerifiedBladeDeviceProvider()
        : this(
            ProductionProbeFactory,
            ProductionPortFactory(),
            ProductionFanRpmFactory(),
            ProductionLightingPortFactory())
    {
    }

    internal WindowsVerifiedBladeDeviceProvider(
        Func<BatteryValidationProbeResult> probe,
        IProductionBatteryPortFactory batteryFactory,
        IFanRpmQueryRunFactory fanRpmFactory,
        IProductionLightingPortFactory? lightingFactory = null)
    {
        this.probe = probe ?? throw new ArgumentNullException(nameof(probe));
        device = new SerializedBladeDevice(
            batteryFactory ?? throw new ArgumentNullException(nameof(batteryFactory)),
            fanRpmFactory ?? throw new ArgumentNullException(nameof(fanRpmFactory)),
            lightingFactory ?? UnavailableProductionLightingFactory.Instance);
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
            if (classification is not (
                BatteryValidationProbeClassification.Ready or
                BatteryValidationProbeClassification.ReadyWithAccessFallback))
            {
                return false;
            }

            lock (providerLifecycleSync)
            {
                if (disposed != 0)
                {
                    return false;
                }

                verifiedDevice = device;
                return true;
            }
        }
        catch
        {
            return false;
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
        private readonly object lifecycleSync = new();
        private readonly SemaphoreSlim hardwareOperationGate = new(1, 1);
        private bool closing;
        private bool disposed;
        private IProductionLightingPort? lightingPort;

        internal SerializedBladeDevice(
            IProductionBatteryPortFactory batteryFactory,
            IFanRpmQueryRunFactory fanRpmFactory,
            IProductionLightingPortFactory lightingFactory)
        {
            this.batteryFactory = batteryFactory;
            this.fanRpmFactory = fanRpmFactory;
            this.lightingFactory = lightingFactory;
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

        public Task SetPerformanceAsync(
            PerformancePreset value,
            CancellationToken cancellationToken) =>
            batteryFactory.Capabilities.PerformancePresets.Contains(value)
                ? ExecuteProductionWriteAsync(
                    port => port.SetPerformanceAsync(value, cancellationToken),
                    cancellationToken)
                : RejectProductionWrite("The requested performance preset is not admitted.");

        public Task SetFanModeAsync(
            FanMode value,
            CancellationToken cancellationToken) =>
            batteryFactory.Capabilities.FanModes.Contains(value)
                ? ExecuteProductionWriteAsync(
                    port => port.SetFanModeAsync(value, cancellationToken),
                    cancellationToken)
                : RejectProductionWrite("The requested fan mode is not admitted.");

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

        public Task PulseReactiveKeyAsync(
            ReactiveKeyPulse pulse,
            CancellationToken cancellationToken) =>
            lightingFactory.Capabilities.LightingEffects.Contains(LightingEffect.Reactive)
                ? ExecuteProductionLightingPulseAsync(pulse, cancellationToken)
                : RejectProductionWrite("Reactive lighting is not admitted.");

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

            hardwareOperationGate.Wait();
            try
            {
                IProductionLightingPort? currentLighting = lightingPort;
                lightingPort = null;
                if (currentLighting is not null)
                {
                    currentLighting.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }

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
            catch (BladeWriteException)
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

        private async Task ExecuteProductionLightingPulseAsync(
            ReactiveKeyPulse pulse,
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
                    "The reactive lighting input was canceled before a verified session opened.");
            }
            catch (ObjectDisposedException exception)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "The reactive lighting input was blocked because the device provider is closing.",
                    exception);
            }

            try
            {
                port = await GetLightingPortCoreAsync(cancellationToken).ConfigureAwait(false);
                if (port is null
                    || !port.Capabilities.LightingEffects.Contains(LightingEffect.Reactive))
                {
                    throw new BladeWriteException(
                        WriteSubmissionState.KnownNotApplied,
                        "The lighting writer could not open an exact-device session.");
                }

                await port.PulseReactiveKeyAsync(pulse, cancellationToken)
                    .ConfigureAwait(false);
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
                    "The reactive lighting input was canceled after a verified session opened.");
            }
            catch (Exception exception)
            {
                await ResetLightingPortCoreAsync(port).ConfigureAwait(false);
                throw new BladeWriteException(
                    WriteSubmissionState.Indeterminate,
                    "The reactive lighting input outcome is indeterminate.",
                    exception);
            }
            finally
            {
                hardwareOperationGate.Release();
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
            new BladeWriteException(WriteSubmissionState.KnownNotApplied, message));

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
