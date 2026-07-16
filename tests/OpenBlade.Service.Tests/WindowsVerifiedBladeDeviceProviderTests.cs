using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class WindowsVerifiedBladeDeviceProviderTests
{
    [Fact]
    public void ProductionCompositionUsesOnlyServiceAdvisoryFactories()
    {
        Assert.Equal(
            nameof(BatteryValidationFactory.ProbeWindowsForService),
            WindowsVerifiedBladeDeviceProvider.ProductionProbeFactory.Method.Name);
        Assert.Equal(
            nameof(BatteryValidationFactory.CreateWindowsQuerySequenceForService),
            WindowsVerifiedBladeDeviceProvider.ProductionQuerySequenceFactory.Method.Name);
    }

    [Theory]
    [InlineData(BatteryValidationProbeClassification.Ready)]
    [InlineData(BatteryValidationProbeClassification.ReadyWithAccessFallback)]
    public void TryGetVerifiedDevice_ReturnsSameDeviceOnlyForReadyClassifications(
        BatteryValidationProbeClassification classification)
    {
        var factory = new FakeQueryFactory();
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(classification),
            factory);

        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice first));
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice second));
        Assert.Same(first, second);
        Assert.Equal(0, factory.OpenCount);
    }

    [Theory]
    [InlineData(BatteryValidationProbeClassification.HostIdentityBlocked)]
    [InlineData(BatteryValidationProbeClassification.ConflictBlocked)]
    [InlineData(BatteryValidationProbeClassification.ConflictDetectionFailed)]
    [InlineData(BatteryValidationProbeClassification.EnumerationFailed)]
    [InlineData(BatteryValidationProbeClassification.CandidateOpenFailed)]
    [InlineData(BatteryValidationProbeClassification.HandleIdentityFailed)]
    [InlineData(BatteryValidationProbeClassification.NoExactHandle)]
    [InlineData(BatteryValidationProbeClassification.AmbiguousExactHandles)]
    public void TryGetVerifiedDevice_BlocksEveryNonReadyClassificationWithoutOpeningQuery(
        BatteryValidationProbeClassification classification)
    {
        var factory = new FakeQueryFactory();
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(classification),
            factory);

        Assert.False(provider.TryGetVerifiedDevice(out IBladeDevice device));
        Assert.Null(device);
        Assert.Equal(0, factory.OpenCount);
    }

    [Fact]
    public void TryGetVerifiedDevice_ContainsProbeFailureWithoutOpeningQuery()
    {
        var factory = new FakeQueryFactory();
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => throw new InvalidOperationException("private probe detail"),
            factory);

        Assert.False(provider.TryGetVerifiedDevice(out IBladeDevice device));
        Assert.Null(device);
        Assert.Equal(0, factory.OpenCount);
    }

    [Fact]
    public async Task ReadCapabilities_ReturnsExactlyUnavailableWithoutOpeningQuery()
    {
        var factory = new FakeQueryFactory();
        using var provider = ReadyProvider(factory);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        DeviceCapabilities capabilities = await device.ReadCapabilitiesAsync(default);

        Assert.Empty(capabilities.PerformancePresets);
        Assert.Empty(capabilities.FanModes);
        Assert.Empty(capabilities.LightingEffects);
        Assert.Empty(capabilities.ChargeLimits);
        Assert.False(capabilities.SupportsBatteryProtectionOff);
        Assert.False(capabilities.SupportsTemporaryFullCharge);
        Assert.Equal(0, factory.OpenCount);
    }

    [Theory]
    [InlineData(BatteryValidationSetting.Limit50, 50)]
    [InlineData(BatteryValidationSetting.Limit55, 55)]
    [InlineData(BatteryValidationSetting.Limit60, 60)]
    [InlineData(BatteryValidationSetting.Limit65, 65)]
    [InlineData(BatteryValidationSetting.Limit70, 70)]
    [InlineData(BatteryValidationSetting.Limit75, 75)]
    [InlineData(BatteryValidationSetting.Limit80, 80)]
    public async Task ReadState_MapsLimitWithOneOpenQueryAndDispose(
        BatteryValidationSetting setting,
        int expectedLimit)
    {
        var port = FakeQueryPort.Success(setting);
        var factory = new FakeQueryFactory(port);
        using var provider = ReadyProvider(factory);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        ConfirmedDeviceState state = await device.ReadStateAsync(default);

        Assert.Null(state.Performance);
        Assert.Null(state.Fans);
        Assert.Equal(new ConfirmedBatteryState(true, expectedLimit, false), state.Battery);
        Assert.Null(state.Lighting);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, port.QueryCount);
        Assert.Equal(1, port.DisposeCount);
    }

    [Fact]
    public async Task ReadState_MapsProtectionOffWithoutInventingLimit()
    {
        var port = FakeQueryPort.Success(BatteryValidationSetting.ProtectionOff);
        var factory = new FakeQueryFactory(port);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        ConfirmedDeviceState state = await device.ReadStateAsync(default);

        Assert.Equal(
            new ConfirmedDeviceState(
                null,
                null,
                new ConfirmedBatteryState(false, null, false),
                null),
            state);
        Assert.Equal(1, port.DisposeCount);
    }

    [Fact]
    public async Task ReadState_OpenFailureFailsClosedWithoutPortOrFabricatedState()
    {
        var factory = new FakeQueryFactory(HidSessionFailureKind.IdentityBlocked);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        IOException error = await Assert.ThrowsAsync<IOException>(
            () => device.ReadStateAsync(default));

        Assert.Contains("identity", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("path", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task ReadState_QueryFailureDisposesPortAndMapsTimeout()
    {
        var port = FakeQueryPort.Failure(HidSessionFailureKind.Timeout);
        var factory = new FakeQueryFactory(port);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        await Assert.ThrowsAsync<TimeoutException>(() => device.ReadStateAsync(default));

        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, port.QueryCount);
        Assert.Equal(1, port.DisposeCount);
    }

    [Fact]
    public async Task ReadState_UnexpectedQueryFailureDoesNotExposeRawDetail()
    {
        var port = FakeQueryPort.Throwing("device path and raw report detail");
        var factory = new FakeQueryFactory(port);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        IOException error = await Assert.ThrowsAsync<IOException>(
            () => device.ReadStateAsync(default));

        Assert.Equal("Battery state query failed.", error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(1, port.DisposeCount);
    }

    [Fact]
    public async Task ReadState_UnexpectedIoFailureDoesNotExposeRawDetail()
    {
        var port = FakeQueryPort.ThrowingIOException("raw path and report detail");
        var factory = new FakeQueryFactory(port);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        IOException error = await Assert.ThrowsAsync<IOException>(
            () => device.ReadStateAsync(default));

        Assert.Equal("Battery state query failed.", error.Message);
        Assert.Null(error.InnerException);
        Assert.Equal(1, port.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadState_RedactsDirectTimeoutFromFactoryOrQueryPort(
        bool throwFromFactory)
    {
        var raw = new TimeoutException("raw path and timeout detail");
        var factory = throwFromFactory
            ? new FakeQueryFactory(raw)
            : new FakeQueryFactory(FakeQueryPort.Throwing(raw));
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        TimeoutException error = await Assert.ThrowsAsync<TimeoutException>(
            () => device.ReadStateAsync(default));

        Assert.Equal("Battery state query timed out.", error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadState_RedactsDirectProtocolFailureAndPreservesOnlyCategory(
        bool throwFromFactory)
    {
        var raw = new RazerProtocolException(
            "raw report and payload detail",
            HidProtocolValidationFailure.Checksum);
        var factory = throwFromFactory
            ? new FakeQueryFactory(raw)
            : new FakeQueryFactory(FakeQueryPort.Throwing(raw));
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        RazerProtocolException error = await Assert.ThrowsAsync<RazerProtocolException>(
            () => device.ReadStateAsync(default));

        Assert.Equal("Battery state query failed protocol validation.", error.Message);
        Assert.Equal(HidProtocolValidationFailure.Checksum, error.ValidationFailure);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task ReadState_CallerCancellationPropagatesAndDisposesPort()
    {
        var port = FakeQueryPort.Cancellable();
        var factory = new FakeQueryFactory(port);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        using var cancellation = new CancellationTokenSource();

        Task<ConfirmedDeviceState> read = device.ReadStateAsync(cancellation.Token);
        await port.QueryStarted.Task;
        cancellation.Cancel();

        OperationCanceledException error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => read);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(1, port.DisposeCount);
    }

    [Fact]
    public async Task ReadState_SerializesConcurrentReads()
    {
        var firstPort = FakeQueryPort.Blocked(BatteryValidationSetting.Limit65);
        var secondPort = FakeQueryPort.Success(BatteryValidationSetting.Limit70);
        var factory = new FakeQueryFactory(firstPort, secondPort);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        Task<ConfirmedDeviceState> first = device.ReadStateAsync(default);
        await firstPort.QueryStarted.Task;
        Task<ConfirmedDeviceState> second = device.ReadStateAsync(default);
        await Task.Yield();

        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(0, secondPort.QueryCount);

        firstPort.Release();
        Assert.Equal(65, (await first).Battery!.ChargeLimitPercent);
        Assert.Equal(70, (await second).Battery!.ChargeLimitPercent);
        Assert.Equal(2, factory.OpenCount);
    }

    [Fact]
    public async Task ReadState_QueuedCancellationPerformsNoAdditionalOpenOrQuery()
    {
        var firstPort = FakeQueryPort.Blocked(BatteryValidationSetting.Limit65);
        var secondPort = FakeQueryPort.Success(BatteryValidationSetting.Limit70);
        var factory = new FakeQueryFactory(firstPort, secondPort);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        Task<ConfirmedDeviceState> first = device.ReadStateAsync(default);
        await firstPort.QueryStarted.Task;
        using var cancellation = new CancellationTokenSource();
        Task<ConfirmedDeviceState> queued = device.ReadStateAsync(cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(0, secondPort.QueryCount);
        firstPort.Release();
        await first;
    }

    [Fact]
    public async Task Dispose_AllowsAdmittedReadsToDrainAndRejectsLateReadBeforeSemaphoreWait()
    {
        var firstPort = FakeQueryPort.Blocked(BatteryValidationSetting.Limit65);
        var secondPort = FakeQueryPort.Success(BatteryValidationSetting.Limit70);
        var factory = new FakeQueryFactory(firstPort, secondPort);
        var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        Task<ConfirmedDeviceState> first = device.ReadStateAsync(default);
        await firstPort.QueryStarted.Task;
        Task<ConfirmedDeviceState> queued = device.ReadStateAsync(default);
        await Task.Yield();

        Task disposing = Task.Run(provider.Dispose);
        Assert.True(SpinWait.SpinUntil(
            () => !provider.TryGetVerifiedDevice(out _),
            TimeSpan.FromSeconds(1)));
        Task<ConfirmedDeviceState> late = device.ReadStateAsync(default);
        bool lateRejectedBeforeRelease = late.IsCompleted;

        firstPort.Release();
        Assert.Equal(65, (await first).Battery!.ChargeLimitPercent);
        Assert.Equal(70, (await queued).Battery!.ChargeLimitPercent);
        await disposing;
        ObjectDisposedException lateError =
            await Assert.ThrowsAsync<ObjectDisposedException>(() => late);

        Assert.True(lateRejectedBeforeRelease);
        Assert.DoesNotContain("SemaphoreSlim", lateError.ObjectName ?? string.Empty);
        Assert.Equal(2, factory.OpenCount);
    }

    [Fact]
    public async Task SettersRejectBeforeGateFactoryOrIo()
    {
        var factory = new FakeQueryFactory();
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        LightingSettings lighting = ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting;

        await Assert.ThrowsAsync<UnvalidatedCommandException>(
            () => device.SetPerformanceAsync(PerformancePreset.Silent, default));
        await Assert.ThrowsAsync<UnvalidatedCommandException>(
            () => device.SetFanModeAsync(FanMode.Automatic, default));
        BladeWriteException limit = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.SetChargeLimitAsync(BatteryPolicy.Create(80), default));
        BladeWriteException protection = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.SetBatteryProtectionOffAsync(default));
        BladeWriteException temporary = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.SetTemporaryFullChargeAsync(true, default));
        Assert.All(
            [limit, protection, temporary],
            exception => Assert.Equal(
                WriteSubmissionState.KnownNotApplied,
                exception.SubmissionState));
        await Assert.ThrowsAsync<UnvalidatedCommandException>(
            () => device.SetLightingAsync(lighting, default));

        Assert.Equal(0, factory.OpenCount);
    }

    [Fact]
    public async Task Dispose_IsIdempotentAndPreventsFurtherDeviceUse()
    {
        var factory = new FakeQueryFactory(FakeQueryPort.Success(BatteryValidationSetting.Limit80));
        var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        provider.Dispose();
        provider.Dispose();

        Assert.False(provider.TryGetVerifiedDevice(out _));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => device.ReadStateAsync(default));
        Assert.Equal(0, factory.OpenCount);
    }

    private static WindowsVerifiedBladeDeviceProvider ReadyProvider(
        IBatteryQuerySequenceRunFactory factory) => new(
            () => Probe(BatteryValidationProbeClassification.Ready),
            factory);

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

    private sealed class FakeQueryFactory : IBatteryQuerySequenceRunFactory
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

    private sealed class FakeQueryPort : IBatteryQuerySequencePort
    {
        private readonly BatteryValidationSetting setting;
        private readonly HidSessionFailureKind? failure;
        private readonly TaskCompletionSource? release;
        private readonly bool observeCancellation;
        private readonly Exception? queryException;

        private FakeQueryPort(
            BatteryValidationSetting setting,
            HidSessionFailureKind? failure = null,
            bool blocked = false,
            bool observeCancellation = false,
            Exception? queryException = null)
        {
            this.setting = setting;
            this.failure = failure;
            this.observeCancellation = observeCancellation;
            this.queryException = queryException;
            release = blocked
                ? new(TaskCreationOptions.RunContinuationsAsynchronously)
                : null;
        }

        public TaskCompletionSource QueryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int QueryCount { get; private set; }

        public int DisposeCount { get; private set; }

        public static FakeQueryPort Success(BatteryValidationSetting setting) => new(setting);

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
