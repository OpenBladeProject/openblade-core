using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class WindowsVerifiedBladeDeviceProviderProductionTests
{
    [Fact]
    public async Task EmptyCatalogProviderRetainsProtectionReadAndNeverFabricatesTemporaryFalse()
    {
        var capabilityPort = new FakeProductionPort(
            DeviceCapabilities.Unavailable(),
            BatteryValidationSetting.Limit80,
            temporary: null);
        var statePort = new FakeProductionPort(
            DeviceCapabilities.Unavailable(),
            BatteryValidationSetting.Limit80,
            temporary: null);
        var factory = new FakeProductionFactory(
            DeviceCapabilities.Unavailable(),
            capabilityPort,
            statePort);
        using var provider = ReadyProvider(factory);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        DeviceCapabilities capabilities = await device.ReadCapabilitiesAsync(default);
        ConfirmedDeviceState state = await device.ReadStateAsync(default);

        Assert.Empty(capabilities.ChargeLimits);
        Assert.False(capabilities.SupportsBatteryProtectionOff);
        Assert.False(capabilities.SupportsTemporaryFullCharge);
        Assert.Equal(new ConfirmedBatteryState(true, 80, null), state.Battery);
        Assert.Equal(2, factory.OpenCount);
        Assert.Equal(0, capabilityPort.ProtectionQueryCount);
        Assert.Equal(1, capabilityPort.DisposeCount);
        Assert.Equal(1, statePort.ProtectionQueryCount);
        Assert.Equal(0, statePort.TemporaryQueryCount);
        Assert.Equal(1, statePort.DisposeCount);
    }

    [Fact]
    public async Task CapabilitiesComeFromFreshIdentityGatedPortInsteadOfFactoryCatalog()
    {
        DeviceCapabilities staleCatalog = BatteryCapabilities(
            new HashSet<int> { 80 },
            protectionOff: true);
        DeviceCapabilities freshCapabilities = DeviceCapabilities.Unavailable();
        var port = new FakeProductionPort(
            freshCapabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var factory = new FakeProductionFactory(staleCatalog, port);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        DeviceCapabilities capabilities = await device.ReadCapabilitiesAsync(default);

        Assert.Equal(freshCapabilities, capabilities);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, port.DisposeCount);
        Assert.Equal(0, port.ProtectionQueryCount);
    }

    [Theory]
    [InlineData(HidSessionFailureKind.IdentityBlocked)]
    [InlineData(HidSessionFailureKind.NativeIoFailure)]
    [InlineData(HidSessionFailureKind.Poisoned)]
    public async Task FreshCapabilityOpenFailurePublishesNoAdmittedCapabilities(
        HidSessionFailureKind failure)
    {
        DeviceCapabilities staleCatalog = BatteryCapabilities(
            new HashSet<int> { 80 },
            protectionOff: true);
        var factory = new FailingProductionFactory(staleCatalog, failure);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        DeviceCapabilities capabilities = await device.ReadCapabilitiesAsync(default);

        Assert.Empty(capabilities.ChargeLimits);
        Assert.False(capabilities.SupportsBatteryProtectionOff);
        Assert.False(capabilities.SupportsTemporaryFullCharge);
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task AcceptedTemporaryReadEvidenceProducesTypedFalseInsteadOfNull()
    {
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 }, protectionOff: true);
        var port = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.ProtectionOff,
            temporary: false);
        var factory = new FakeProductionFactory(capabilities, port);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        ConfirmedDeviceState state = await device.ReadStateAsync(default);

        Assert.Equal(new ConfirmedBatteryState(false, null, false), state.Battery);
        Assert.Equal(1, port.TemporaryQueryCount);
    }

    [Fact]
    public async Task ProviderSerializesReadAndWriteAcrossOneHardwareOperationGate()
    {
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        var blockedRead = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null,
            blockProtectionQuery: true);
        var writePort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var factory = new FakeProductionFactory(capabilities, blockedRead, writePort);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        Task<ConfirmedDeviceState> read = device.ReadStateAsync(default);
        await blockedRead.ProtectionQueryStarted.Task;
        Task write = device.SetChargeLimitAsync(BatteryPolicy.Create(80), default);
        await Task.Yield();

        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(0, writePort.SetLimitCount);

        blockedRead.Release();
        await read;
        await write;

        Assert.Equal(2, factory.OpenCount);
        Assert.Equal(1, writePort.SetLimitCount);
        Assert.Equal(1, blockedRead.DisposeCount);
        Assert.Equal(1, writePort.DisposeCount);
    }

    [Fact]
    public async Task ActiveWriteBlocksReadUntilWriteCompletes()
    {
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        var blockedWrite = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null,
            blockWrite: true);
        var readPort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var factory = new FakeProductionFactory(capabilities, blockedWrite, readPort);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        Task write = device.SetChargeLimitAsync(BatteryPolicy.Create(80), default);
        await blockedWrite.WriteStarted.Task;
        Task<ConfirmedDeviceState> read = device.ReadStateAsync(default);

        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(0, readPort.ProtectionQueryCount);

        blockedWrite.ReleaseWrite();
        await write;
        Assert.Equal(80, (await read).Battery!.ChargeLimitPercent);
        Assert.Equal(2, factory.OpenCount);
    }

    [Fact]
    public async Task ActiveWriteBlocksSecondWriteUntilFirstCompletes()
    {
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        var blockedWrite = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null,
            blockWrite: true);
        var secondWrite = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var factory = new FakeProductionFactory(capabilities, blockedWrite, secondWrite);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        Task first = device.SetChargeLimitAsync(BatteryPolicy.Create(80), default);
        await blockedWrite.WriteStarted.Task;
        Task second = device.SetChargeLimitAsync(BatteryPolicy.Create(80), default);

        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(0, secondWrite.SetLimitCount);

        blockedWrite.ReleaseWrite();
        await first;
        await second;
        Assert.Equal(2, factory.OpenCount);
        Assert.Equal(1, secondWrite.SetLimitCount);
    }

    [Fact]
    public async Task DisposalDrainsActiveWriteAndRejectsPrecheckedQueuedWriteWithoutOpeningPort()
    {
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        var blockedWrite = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null,
            blockWrite: true);
        var neverOpened = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var factory = new FakeProductionFactory(capabilities, blockedWrite, neverOpened);
        var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        Task active = device.SetChargeLimitAsync(BatteryPolicy.Create(80), default);
        await blockedWrite.WriteStarted.Task;

        // Async methods run through their first incomplete await synchronously, so this
        // operation has deterministically passed the lifetime precheck and is queued.
        Task queued = device.SetChargeLimitAsync(BatteryPolicy.Create(80), default);
        Assert.False(queued.IsCompleted);

        Task disposing = Task.Run(provider.Dispose);
        Assert.True(SpinWait.SpinUntil(
            () => !provider.TryGetVerifiedDevice(out _),
            TimeSpan.FromSeconds(1)));
        blockedWrite.ReleaseWrite();

        await active;
        await disposing;
        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(() => queued);
        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(0, neverOpened.SetLimitCount);
    }

    [Fact]
    public async Task DisposalDrainsActiveOperationAndQueuedAfterClosingPerformsNoPortOpen()
    {
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        var blockedRead = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null,
            blockProtectionQuery: true);
        var neverOpened = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var factory = new FakeProductionFactory(capabilities, blockedRead, neverOpened);
        var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        Task<ConfirmedDeviceState> active = device.ReadStateAsync(default);
        await blockedRead.ProtectionQueryStarted.Task;
        Task<ConfirmedDeviceState> queued = device.ReadStateAsync(default);

        Task disposing = Task.Run(provider.Dispose);
        Assert.True(SpinWait.SpinUntil(
            () => !provider.TryGetVerifiedDevice(out _),
            TimeSpan.FromSeconds(1)));
        blockedRead.Release();

        await active;
        await disposing;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(0, neverOpened.ProtectionQueryCount);
    }

    [Fact]
    public async Task SetterDelegatesExactlyOnceWithoutSemanticReadbackInsideProtocolOrProvider()
    {
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 }, protectionOff: true);
        var port = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: false);
        var factory = new FakeProductionFactory(capabilities, port);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        await device.SetBatteryProtectionOffAsync(default);

        Assert.Equal(1, port.SetProtectionOffCount);
        Assert.Equal(0, port.ProtectionQueryCount);
        Assert.Equal(0, port.TemporaryQueryCount);
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task IndeterminatePortFailurePreservesSubmissionClassification()
    {
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        var port = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null)
        {
            WriteFailure = new BladeWriteException(
                WriteSubmissionState.Indeterminate,
                "ambiguous submission"),
        };
        var factory = new FakeProductionFactory(capabilities, port);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.SetChargeLimitAsync(BatteryPolicy.Create(80), default));

        Assert.Equal(WriteSubmissionState.Indeterminate, error.SubmissionState);
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task IdentityLossPoisonsNoFutureOperationAndFreshOpenRechecksBeforeWriting()
    {
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        var freshPort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var factory = new IdentityThenPortFactory(capabilities, freshPort);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        BladeWriteException first = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.SetChargeLimitAsync(BatteryPolicy.Create(80), default));
        await device.SetChargeLimitAsync(BatteryPolicy.Create(80), default);

        Assert.Equal(WriteSubmissionState.Indeterminate, first.SubmissionState);
        Assert.Equal(2, factory.OpenCount);
        Assert.Equal(1, freshPort.SetLimitCount);
    }

    private static WindowsVerifiedBladeDeviceProvider ReadyProvider(
        IProductionBatteryPortFactory factory) => new(
            () => Probe(BatteryValidationProbeClassification.Ready),
            factory);

    private static DeviceCapabilities BatteryCapabilities(
        IReadOnlySet<int> limits,
        bool protectionOff = false) => new(
            new HashSet<PerformancePreset>(),
            new HashSet<FanMode>(),
            new HashSet<LightingEffect>(),
            limits,
            protectionOff,
            SupportsTemporaryFullCharge: false);

    private static BatteryValidationProbeResult Probe(
        BatteryValidationProbeClassification classification) => new(
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
            ExactMatchCount: 1);

    private sealed class FakeProductionFactory(
        DeviceCapabilities capabilities,
        params FakeProductionPort[] ports) : IProductionBatteryPortFactory
    {
        private readonly Queue<FakeProductionPort> ports = new(ports);

        public DeviceCapabilities Capabilities { get; } = capabilities;

        public bool SupportsTemporaryFullChargeQuery =>
            ports.Any(port => port.SupportsTemporaryFullChargeQuery);

        public int OpenCount { get; private set; }

        public Task<HidSessionResult<IProductionBatteryPort>> OpenAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            return Task.FromResult(ports.Count == 0
                ? new HidSessionResult<IProductionBatteryPort>(
                    null,
                    HidSessionFailureKind.NativeIoFailure)
                : new HidSessionResult<IProductionBatteryPort>(ports.Dequeue(), null));
        }
    }

    private sealed class IdentityThenPortFactory(
        DeviceCapabilities capabilities,
        IProductionBatteryPort freshPort) : IProductionBatteryPortFactory
    {
        public DeviceCapabilities Capabilities { get; } = capabilities;
        public bool SupportsTemporaryFullChargeQuery => false;
        public int OpenCount { get; private set; }

        public Task<HidSessionResult<IProductionBatteryPort>> OpenAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            return Task.FromResult(OpenCount == 1
                ? new HidSessionResult<IProductionBatteryPort>(
                    null,
                    HidSessionFailureKind.IdentityBlocked)
                : new HidSessionResult<IProductionBatteryPort>(freshPort, null));
        }
    }

    private sealed class FailingProductionFactory(
        DeviceCapabilities capabilities,
        HidSessionFailureKind failure) : IProductionBatteryPortFactory
    {
        public DeviceCapabilities Capabilities { get; } = capabilities;
        public bool SupportsTemporaryFullChargeQuery => false;
        public int OpenCount { get; private set; }

        public Task<HidSessionResult<IProductionBatteryPort>> OpenAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            return Task.FromResult(
                new HidSessionResult<IProductionBatteryPort>(null, failure));
        }
    }

    private sealed class FakeProductionPort(
        DeviceCapabilities capabilities,
        BatteryValidationSetting protection,
        bool? temporary,
        bool blockProtectionQuery = false,
        bool blockWrite = false) : IProductionBatteryPort
    {
        private readonly TaskCompletionSource? protectionRelease = blockProtectionQuery
            ? new(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
        private readonly TaskCompletionSource? writeRelease = blockWrite
            ? new(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;

        public DeviceCapabilities Capabilities { get; } = capabilities;
        public bool SupportsTemporaryFullChargeQuery => temporary.HasValue;
        public BladeWriteException? WriteFailure { get; set; }
        public TaskCompletionSource ProtectionQueryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ProtectionQueryCount { get; private set; }
        public int TemporaryQueryCount { get; private set; }
        public int SetLimitCount { get; private set; }
        public int SetProtectionOffCount { get; private set; }
        public int DisposeCount { get; private set; }

        public async Task<ConfirmedBatteryState> QueryProtectionAsync(
            CancellationToken cancellationToken)
        {
            ProtectionQueryCount++;
            ProtectionQueryStarted.TrySetResult();
            if (protectionRelease is not null)
            {
                await protectionRelease.Task.WaitAsync(cancellationToken);
            }

            return protection switch
            {
                BatteryValidationSetting.ProtectionOff => new(false, null, null),
                BatteryValidationSetting.Limit80 => new(true, 80, null),
                _ => throw new InvalidOperationException(),
            };
        }

        public Task<bool?> QueryTemporaryFullChargeAsync(CancellationToken cancellationToken)
        {
            TemporaryQueryCount++;
            return Task.FromResult(temporary);
        }

        public async Task SetChargeLimitAsync(int percent, CancellationToken cancellationToken)
        {
            SetLimitCount++;
            WriteStarted.TrySetResult();
            if (writeRelease is not null)
            {
                await writeRelease.Task.WaitAsync(cancellationToken);
            }

            if (WriteFailure is not null)
            {
                throw WriteFailure;
            }
        }

        public Task SetProtectionOffAsync(CancellationToken cancellationToken)
        {
            SetProtectionOffCount++;
            return WriteFailure is null ? Task.CompletedTask : Task.FromException(WriteFailure);
        }

        public Task SetTemporaryFullChargeAsync(bool enabled, CancellationToken cancellationToken) =>
            WriteFailure is null ? Task.CompletedTask : Task.FromException(WriteFailure);

        public void Release() => protectionRelease!.TrySetResult();

        public void ReleaseWrite() => writeRelease!.TrySetResult();

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
