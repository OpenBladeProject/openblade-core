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

    private readonly Func<BatteryValidationProbeResult> probe;
    private readonly SerializedBladeDevice device;
    private readonly object providerLifecycleSync = new();
    private int disposed;

    public WindowsVerifiedBladeDeviceProvider()
        : this(ProductionProbeFactory, ProductionPortFactory(), ProductionFanRpmFactory())
    {
    }

    internal WindowsVerifiedBladeDeviceProvider(
        Func<BatteryValidationProbeResult> probe,
        IProductionBatteryPortFactory batteryFactory,
        IFanRpmQueryRunFactory fanRpmFactory)
    {
        this.probe = probe ?? throw new ArgumentNullException(nameof(probe));
        device = new SerializedBladeDevice(
            batteryFactory ?? throw new ArgumentNullException(nameof(batteryFactory)),
            fanRpmFactory ?? throw new ArgumentNullException(nameof(fanRpmFactory)));
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
        private readonly object lifecycleSync = new();
        private readonly SemaphoreSlim hardwareOperationGate = new(1, 1);
        private bool closing;
        private bool disposed;

        internal SerializedBladeDevice(
            IProductionBatteryPortFactory batteryFactory,
            IFanRpmQueryRunFactory fanRpmFactory)
        {
            this.batteryFactory = batteryFactory;
            this.fanRpmFactory = fanRpmFactory;
        }

        public async Task<DeviceCapabilities> ReadCapabilitiesAsync(
            CancellationToken cancellationToken)
        {
            await EnterHardwareOperationAsync(cancellationToken).ConfigureAwait(false);
            try
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
            finally
            {
                hardwareOperationGate.Release();
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
                return new ConfirmedDeviceState(
                    performanceFan.Performance,
                    performanceFan.Fans,
                    battery,
                    null);
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

        public Task SetLightingAsync(
            LightingSettings value,
            CancellationToken cancellationToken) => RejectUnvalidatedWrite();

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

        private static Task RejectUnvalidatedWrite() => Task.FromException(
            new UnvalidatedCommandException(
                "Hardware writes are not admitted for this subsystem."));

        private static Task RejectProductionWrite(string message) => Task.FromException(
            new BladeWriteException(WriteSubmissionState.KnownNotApplied, message));

        private sealed class FailClosedDeviceException(string message) : Exception(message);
    }
}
