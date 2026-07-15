using System.Runtime.CompilerServices;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

[assembly: InternalsVisibleTo("OpenBlade.Service.Tests")]

namespace OpenBlade.Service;

public sealed class WindowsVerifiedBladeDeviceProvider : IVerifiedBladeDeviceProvider, IDisposable
{
    private readonly Func<BatteryValidationProbeResult> probe;
    private readonly ReadOnlyBladeDevice device;
    private int disposed;

    public WindowsVerifiedBladeDeviceProvider()
        : this(
            BatteryValidationFactory.ProbeWindows,
            BatteryValidationFactory.CreateWindowsQuerySequence())
    {
    }

    internal WindowsVerifiedBladeDeviceProvider(
        Func<BatteryValidationProbeResult> probe,
        IBatteryQuerySequenceRunFactory queryFactory)
    {
        this.probe = probe ?? throw new ArgumentNullException(nameof(probe));
        device = new ReadOnlyBladeDevice(
            queryFactory ?? throw new ArgumentNullException(nameof(queryFactory)));
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

            verifiedDevice = device;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            device.Dispose();
        }
    }

    private sealed class ReadOnlyBladeDevice : IBladeDevice, IDisposable
    {
        private static readonly DeviceCapabilities UnavailableCapabilities =
            DeviceCapabilities.Unavailable();

        private readonly IBatteryQuerySequenceRunFactory queryFactory;
        private readonly object lifecycleSync = new();
        private readonly SemaphoreSlim readGate = new(1, 1);
        private int admittedReadCount;
        private int disposed;

        internal ReadOnlyBladeDevice(IBatteryQuerySequenceRunFactory queryFactory)
        {
            this.queryFactory = queryFactory;
        }

        public Task<DeviceCapabilities> ReadCapabilitiesAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            return Task.FromResult(UnavailableCapabilities);
        }

        public async Task<ConfirmedDeviceState> ReadStateAsync(
            CancellationToken cancellationToken)
        {
            AdmitRead();
            try
            {
                await readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await QueryStateAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    readGate.Release();
                }
            }
            finally
            {
                ReleaseReadAdmission();
            }
        }

        public Task SetPerformanceAsync(
            PerformancePreset value,
            CancellationToken cancellationToken) => RejectWrite();

        public Task SetFanModeAsync(
            FanMode value,
            CancellationToken cancellationToken) => RejectWrite();

        public Task SetBatteryPolicyAsync(
            BatteryPolicy value,
            CancellationToken cancellationToken) => RejectWrite();

        public Task SetLightingAsync(
            LightingSettings value,
            CancellationToken cancellationToken) => RejectWrite();

        public void Dispose()
        {
            lock (lifecycleSync)
            {
                if (disposed != 0)
                {
                    return;
                }

                Volatile.Write(ref disposed, 1);
                while (admittedReadCount != 0)
                {
                    Monitor.Wait(lifecycleSync);
                }

                readGate.Dispose();
            }
        }

        private async Task<ConfirmedDeviceState> QueryStateAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                HidSessionResult<IBatteryQuerySequencePort> opened =
                    await queryFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
                if (opened.Value is not { } port)
                {
                    ThrowFailure(opened.Failure, opened.Diagnostic, cancellationToken);
                    throw new IOException("Battery state query did not provide a session.");
                }

                await using (port.ConfigureAwait(false))
                {
                    if (opened.Failure is not null)
                    {
                        ThrowFailure(opened.Failure, opened.Diagnostic, cancellationToken);
                    }

                    HidSessionResult<BatteryValidationSetting> queried =
                        await port.QueryAsync(cancellationToken).ConfigureAwait(false);
                    if (queried.Failure is not null)
                    {
                        ThrowFailure(queried.Failure, queried.Diagnostic, cancellationToken);
                    }

                    BatteryPolicy? battery = queried.Value switch
                    {
                        BatteryValidationSetting.ProtectionOff => null,
                        BatteryValidationSetting.Limit50 => BatteryPolicy.Create(50),
                        BatteryValidationSetting.Limit55 => BatteryPolicy.Create(55),
                        BatteryValidationSetting.Limit60 => BatteryPolicy.Create(60),
                        BatteryValidationSetting.Limit65 => BatteryPolicy.Create(65),
                        BatteryValidationSetting.Limit70 => BatteryPolicy.Create(70),
                        BatteryValidationSetting.Limit75 => BatteryPolicy.Create(75),
                        BatteryValidationSetting.Limit80 => BatteryPolicy.Create(80),
                        _ => throw new RazerProtocolException(
                            "Battery state query returned an undefined setting.",
                            HidProtocolValidationFailure.PayloadValidator),
                    };

                    return new ConfirmedDeviceState(null, null, battery, null);
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
                    "Battery state query failed protocol validation.",
                    exception.ValidationFailure);
            }
            catch (FailClosedDeviceException exception)
            {
                throw new IOException(exception.Message);
            }
            catch (Exception)
            {
                throw new IOException("Battery state query failed.");
            }
        }

        private static void ThrowFailure(
            HidSessionFailureKind? failure,
            HidSessionFailureDiagnostic? diagnostic,
            CancellationToken cancellationToken)
        {
            switch (failure)
            {
                case HidSessionFailureKind.Canceled when cancellationToken.IsCancellationRequested:
                case HidSessionFailureKind.QueuedCanceled when cancellationToken.IsCancellationRequested:
                    throw new OperationCanceledException(cancellationToken);
                case HidSessionFailureKind.Timeout:
                    throw new TimeoutException("Battery state query timed out.");
                case HidSessionFailureKind.ProtocolFailure:
                    throw new RazerProtocolException(
                        "Battery state query failed protocol validation.",
                        diagnostic?.ProtocolValidationFailure
                            ?? HidProtocolValidationFailure.PayloadValidator);
                case HidSessionFailureKind.IdentityBlocked:
                    throw new FailClosedDeviceException(
                        "Battery state query was blocked by exact device identity verification.");
                case HidSessionFailureKind.ConflictBlocked:
                    throw new FailClosedDeviceException(
                        "Battery state query was blocked by controller conflict verification.");
                case HidSessionFailureKind.ConflictDetectionFailed:
                    throw new FailClosedDeviceException(
                        "Battery state query was blocked because conflict verification failed.");
                default:
                    throw new FailClosedDeviceException("Battery state query failed closed.");
            }
        }

        private static Task RejectWrite() => Task.FromException(
            new UnvalidatedCommandException(
                "Hardware writes are not admitted for the verified read-only device."));

        private void AdmitRead()
        {
            lock (lifecycleSync)
            {
                ThrowIfDisposed();
                admittedReadCount++;
            }
        }

        private void ReleaseReadAdmission()
        {
            lock (lifecycleSync)
            {
                admittedReadCount--;
                if (disposed != 0 && admittedReadCount == 0)
                {
                    Monitor.PulseAll(lifecycleSync);
                }
            }
        }

        private void ThrowIfDisposed() =>
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        private sealed class FailClosedDeviceException(string message) : Exception(message);
    }
}
