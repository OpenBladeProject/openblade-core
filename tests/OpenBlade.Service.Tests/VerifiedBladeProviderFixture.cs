using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

internal static class VerifiedBladeProviderFixture
{
    internal static IFanRpmQueryRunFactory UnavailableFanRpmFactory() =>
        new FakeFanRpmFactory(new(null, HidSessionFailureKind.NativeIoFailure));

    internal static IProductionBatteryPortFactory Adapt(
        IBatteryQuerySequenceRunFactory factory,
        Exception? adapterPowerFailure = null) =>
        new QueryFactoryAdapter(factory, adapterPowerFailure);

    internal static BatteryValidationProbeResult Probe(
        BatteryValidationProbeClassification classification,
        BatteryValidationFirmwareCompatibility firmwareCompatibility =
            BatteryValidationFirmwareCompatibility.Tested) => new(
            BatteryValidationProbeStage.Ready,
            classification,
            HostIdentityMatched: true,
            ConflictCheckPassed: true,
            EnumeratedCandidateCount: 1,
            CandidateOpenAttemptCount: 1,
            CandidateOpenSuccessCount: 1,
            CandidateOpenWin32Error: null,
            CandidateAccessFallbackCount: 0,
            IdentityReadCount: 1,
            VendorMatchCount: 1,
            ProductMatchCount: 1,
            UsagePageMatchCount: 1,
            UsageMatchCount: 1,
            FeatureLengthMatchCount: 1,
            ExactMatchCount: 1,
            FirmwareCompatibility: firmwareCompatibility);

    internal sealed class QueryFactoryAdapter(
        IBatteryQuerySequenceRunFactory inner,
        Exception? adapterPowerFailure)
        : IProductionBatteryPortFactory
    {
        public DeviceCapabilities Capabilities { get; } = DeviceCapabilities.Unavailable();

        public async Task<HidSessionResult<IProductionBatteryPort>> OpenAsync(
            CancellationToken cancellationToken)
        {
            HidSessionResult<IBatteryQuerySequencePort> opened =
                await inner.OpenAsync(cancellationToken);
            return opened.Value is null
                ? new(null, opened.Failure) { Diagnostic = opened.Diagnostic }
                : new(
                    new QueryPortAdapter(opened.Value, adapterPowerFailure),
                    opened.Failure)
                {
                    Diagnostic = opened.Diagnostic,
                };
        }
    }

    internal sealed class QueryPortAdapter(
        IBatteryQuerySequencePort inner,
        Exception? adapterPowerFailure)
        : IProductionBatteryPort
    {
        public DeviceCapabilities Capabilities { get; } = DeviceCapabilities.Unavailable();

        public bool SupportsTemporaryFullChargeQuery => false;

        public async Task<ConfirmedBatteryState> QueryProtectionAsync(
            CancellationToken cancellationToken)
        {
            HidSessionResult<BatteryValidationSetting> result =
                await inner.QueryAsync(cancellationToken);
            if (result.Failure is null)
            {
                return result.Value switch
                {
                    BatteryValidationSetting.ProtectionOff => new(false, null, null),
                    BatteryValidationSetting.Limit50 => new(true, 50, null),
                    BatteryValidationSetting.Limit55 => new(true, 55, null),
                    BatteryValidationSetting.Limit60 => new(true, 60, null),
                    BatteryValidationSetting.Limit65 => new(true, 65, null),
                    BatteryValidationSetting.Limit70 => new(true, 70, null),
                    BatteryValidationSetting.Limit75 => new(true, 75, null),
                    BatteryValidationSetting.Limit80 => new(true, 80, null),
                    _ => throw new InvalidOperationException(),
                };
            }

            switch (result.Failure)
            {
                case HidSessionFailureKind.Canceled when cancellationToken.IsCancellationRequested:
                case HidSessionFailureKind.QueuedCanceled when cancellationToken.IsCancellationRequested:
                    throw new OperationCanceledException(cancellationToken);
                case HidSessionFailureKind.Timeout:
                    throw new TimeoutException("Battery state query timed out.");
                case HidSessionFailureKind.ProtocolFailure:
                    throw new RazerProtocolException(
                        "Battery query protocol failure.",
                        result.Diagnostic?.ProtocolValidationFailure
                            ?? HidProtocolValidationFailure.PayloadValidator);
                case HidSessionFailureKind.IdentityBlocked:
                    throw new IOException("Battery query identity verification failed.");
                default:
                    throw new IOException("Battery query failed closed.");
            }
        }

        public Task<bool?> QueryTemporaryFullChargeAsync(CancellationToken cancellationToken) =>
            Task.FromResult<bool?>(null);

        public Task<ProductionPerformanceFanState> QueryPerformanceFanStateAsync(
            CancellationToken cancellationToken) => Task.FromResult(
                new ProductionPerformanceFanState(null, null));

        public Task<AdapterPowerState> QueryAdapterPowerStateAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return adapterPowerFailure is null
                ? Task.FromResult(AdapterPowerState.FullPower280W)
                : Task.FromException<AdapterPowerState>(adapterPowerFailure);
        }

        public Task SetPerformanceAsync(
            PerformancePreset value,
            CancellationToken cancellationToken) => Reject();

        public Task SetCustomPerformanceAsync(
            CustomPerformanceSettings value,
            CancellationToken cancellationToken) => Reject();

        public Task SetFanModeAsync(
            FanMode value,
            CancellationToken cancellationToken) => Reject();

        public Task SetFanControlAsync(
            FanControlSettings value,
            CancellationToken cancellationToken) => Reject();

        public Task SetFanTargetsAsync(
            int fan1Rpm,
            int fan2Rpm,
            CancellationToken cancellationToken) => Reject();

        public Task SetChargeLimitAsync(int percent, CancellationToken cancellationToken) =>
            Reject();

        public Task SetProtectionOffAsync(CancellationToken cancellationToken) => Reject();

        public Task SetTemporaryFullChargeAsync(bool enabled, CancellationToken cancellationToken) =>
            Reject();

        public Task ApplyOneShotLaptopControlAsync(
            ApplyOneShotLaptopControl value,
            CancellationToken cancellationToken) => Reject();

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private static Task Reject() => Task.FromException(
            new CapabilityUnavailableException("Write unavailable in query adapter."));
    }

    internal sealed class FakeQueryFactory : IBatteryQuerySequenceRunFactory
    {
        private readonly Queue<HidSessionResult<IBatteryQuerySequencePort>> results = [];
        private readonly Exception? openException;

        public FakeQueryFactory(params FakeQueryPort[] ports)
        {
            foreach (FakeQueryPort port in ports)
            {
                results.Enqueue(new(port, null));
            }
        }

        public FakeQueryFactory(HidSessionFailureKind failure)
        {
            results.Enqueue(new(null, failure));
        }

        public FakeQueryFactory(Exception openException)
        {
            this.openException = openException;
        }

        public int OpenCount { get; private set; }

        public Task<HidSessionResult<IBatteryQuerySequencePort>> OpenAsync(
            CancellationToken cancellationToken)
        {
            OpenCount++;
            cancellationToken.ThrowIfCancellationRequested();
            if (openException is not null)
            {
                throw openException;
            }

            return Task.FromResult(results.Count == 0
                ? new HidSessionResult<IBatteryQuerySequencePort>(
                    null,
                    HidSessionFailureKind.NativeIoFailure)
                : results.Dequeue());
        }
    }

    internal sealed class FakeFanRpmFactory(
        HidSessionResult<IFanRpmQueryPort> result) : IFanRpmQueryRunFactory
    {
        public int OpenCount { get; private set; }

        public Task<HidSessionResult<IFanRpmQueryPort>> OpenAsync(
            CancellationToken cancellationToken)
        {
            OpenCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    internal sealed class FakeQueryPort : IBatteryQuerySequencePort
    {
        private readonly BatteryValidationSetting setting;
        private readonly HidSessionFailureKind? failure;
        private readonly TaskCompletionSource? release;
        private readonly bool observeCancellation;
        private readonly Exception? queryException;
        private readonly List<string>? readOrder;

        private FakeQueryPort(
            BatteryValidationSetting setting,
            HidSessionFailureKind? failure = null,
            bool blocked = false,
            bool observeCancellation = false,
            Exception? queryException = null,
            List<string>? readOrder = null)
        {
            this.setting = setting;
            this.failure = failure;
            this.observeCancellation = observeCancellation;
            this.queryException = queryException;
            this.readOrder = readOrder;
            release = blocked
                ? new(TaskCreationOptions.RunContinuationsAsynchronously)
                : null;
        }

        public TaskCompletionSource QueryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int QueryCount { get; private set; }

        public int DisposeCount { get; private set; }

        public static FakeQueryPort Success(
            BatteryValidationSetting setting,
            List<string>? readOrder = null) => new(setting, readOrder: readOrder);

        public static FakeQueryPort Failure(HidSessionFailureKind failure) =>
            new(default, failure);

        public static FakeQueryPort Blocked(BatteryValidationSetting setting) =>
            new(setting, blocked: true);

        public static FakeQueryPort Cancellable() =>
            new(default, observeCancellation: true);

        public static FakeQueryPort Throwing(string message) =>
            new(default, queryException: new InvalidOperationException(message));

        public static FakeQueryPort ThrowingIOException(string message) =>
            new(default, queryException: new IOException(message));

        public static FakeQueryPort Throwing(Exception exception) =>
            new(default, queryException: exception);

        public async Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(
            CancellationToken cancellationToken)
        {
            readOrder?.Add("Battery");
            QueryCount++;
            QueryStarted.TrySetResult();
            if (queryException is not null)
            {
                throw queryException;
            }

            if (observeCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (release is not null)
            {
                await release.Task.WaitAsync(cancellationToken);
            }

            return new(setting, failure);
        }

        public void Release() => release!.TrySetResult();

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
