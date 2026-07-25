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
            nameof(ServiceComposition.CreateProductionBatteryFactory),
            WindowsVerifiedBladeDeviceProvider.ProductionPortFactory.Method.Name);
        Assert.Equal(
            nameof(BatteryValidationFactory.CreateWindowsFanRpmQuery),
            WindowsVerifiedBladeDeviceProvider.ProductionFanRpmFactory.Method.Name);
        Assert.Equal(
            nameof(ServiceComposition.CreateProductionLightingFactory),
            WindowsVerifiedBladeDeviceProvider.ProductionLightingPortFactory.Method.Name);
    }

    [Fact]
    public async Task ReadFanRpm_ReturnsBothSelectorsAndDisposesTypedPort()
    {
        var fanPort = new FakeFanRpmPort(new(new FanRpmReadings(2700, 2600), null));
        var fanFactory = new FakeFanRpmFactory(new(fanPort, null));
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            fanFactory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        FanRpmReadings? rpm = await device.ReadFanRpmAsync(default);

        Assert.Equal(new FanRpmReadings(2700, 2600), rpm);
        Assert.Equal(1, fanFactory.OpenCount);
        Assert.Equal(1, fanPort.QueryCount);
        Assert.Equal(1, fanPort.DisposeCount);
    }

    [Fact]
    public async Task ReadFanRpm_QueryFailureDegradesToNullAndDisposes()
    {
        var fanPort = new FakeFanRpmPort(
            new(null, HidSessionFailureKind.ProtocolFailure));
        var fanFactory = new FakeFanRpmFactory(new(fanPort, null));
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            fanFactory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        FanRpmReadings? rpm = await device.ReadFanRpmAsync(default);

        Assert.Null(rpm);
        Assert.Equal(1, fanPort.DisposeCount);
    }

    [Fact]
    public async Task ReadFanRpm_SharesExistingSerializedHardwareGateWithBatteryRead()
    {
        var batteryPort = FakeQueryPort.Blocked(BatteryValidationSetting.Limit80);
        var batteryFactory = new FakeQueryFactory(batteryPort);
        var fanPort = new FakeFanRpmPort(new(new FanRpmReadings(2700, 2600), null));
        var fanFactory = new FakeFanRpmFactory(new(fanPort, null));
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(batteryFactory),
            fanFactory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        Task<ConfirmedDeviceState> batteryRead = device.ReadStateAsync(default);
        await batteryPort.QueryStarted.Task;
        Task<FanRpmReadings?> fanRead = device.ReadFanRpmAsync(default);
        await Task.Yield();

        Assert.Equal(0, fanFactory.OpenCount);
        batteryPort.Release();
        await batteryRead;
        Assert.Equal(new FanRpmReadings(2700, 2600), await fanRead);
        Assert.Equal(1, fanFactory.OpenCount);
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
            Adapt(factory),
            UnavailableFanRpmFactory());

        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice first));
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice second));
        Assert.Same(first, second);
        Assert.Equal(0, factory.OpenCount);
    }

    [Fact]
    public void ReadOnlyAdmissionReusesRecentIdentityButNormalAdmissionProbesAgain()
    {
        int probeCount = 0;
        var factory = new FakeQueryFactory();
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () =>
            {
                probeCount++;
                return Probe(BatteryValidationProbeClassification.Ready);
            },
            Adapt(factory),
            UnavailableFanRpmFactory());

        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice first));
        Assert.True(provider.TryGetRecentlyVerifiedReadOnlyDevice(out IBladeDevice cached));
        Assert.Same(first, cached);
        Assert.Equal(1, probeCount);

        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice freshlyVerified));
        Assert.Same(first, freshlyVerified);
        Assert.Equal(2, probeCount);
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
            Adapt(factory),
            UnavailableFanRpmFactory());

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
            Adapt(factory),
            UnavailableFanRpmFactory());

        Assert.False(provider.TryGetVerifiedDevice(out IBladeDevice device));
        Assert.Null(device);
        Assert.Equal(0, factory.OpenCount);
    }

    [Theory]
    [InlineData(BatteryValidationProbeClassification.EnumerationFailed)]
    [InlineData(BatteryValidationProbeClassification.CandidateOpenFailed)]
    public void TryGetVerifiedDevice_ReusesRecentReadyDeviceForTransientProbeFailure(
        BatteryValidationProbeClassification transientFailure)
    {
        int calls = 0;
        var factory = new FakeQueryFactory();
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () =>
            {
                calls++;
                return calls == 1
                    ? Probe(BatteryValidationProbeClassification.Ready)
                    : Probe(transientFailure);
            },
            Adapt(factory),
            UnavailableFanRpmFactory());

        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice first));
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice second));

        Assert.Same(first, second);
        Assert.Equal(0, factory.OpenCount);
    }

    [Fact]
    public void TryGetVerifiedDevice_ReusesRecentReadyDeviceWhenProbeThrows()
    {
        int calls = 0;
        var factory = new FakeQueryFactory();
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () =>
            {
                calls++;
                if (calls == 1)
                {
                    return Probe(BatteryValidationProbeClassification.Ready);
                }

                throw new InvalidOperationException("private probe detail");
            },
            Adapt(factory),
            UnavailableFanRpmFactory());

        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice first));
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice second));

        Assert.Same(first, second);
        Assert.Equal(0, factory.OpenCount);
    }

    [Fact]
    public void TryGetVerifiedDevice_ConcreteDeviceMissClearsRecentReadyDevice()
    {
        int calls = 0;
        var factory = new FakeQueryFactory();
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () =>
            {
                calls++;
                return calls switch
                {
                    1 => Probe(BatteryValidationProbeClassification.Ready),
                    2 => Probe(BatteryValidationProbeClassification.NoExactHandle),
                    _ => Probe(BatteryValidationProbeClassification.EnumerationFailed),
                };
            },
            Adapt(factory),
            UnavailableFanRpmFactory());

        Assert.True(provider.TryGetVerifiedDevice(out _));
        Assert.False(provider.TryGetVerifiedDevice(out IBladeDevice missing));
        Assert.Null(missing);
        Assert.False(provider.TryGetVerifiedDevice(out IBladeDevice stillMissing));
        Assert.Null(stillMissing);
        Assert.Equal(0, factory.OpenCount);
    }

    [Fact]
    public async Task ReadCapabilities_OpenFailureReturnsExactlyUnavailableAfterFreshOpen()
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
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task ReadCapabilities_BatteryUnavailableStillReportsIndependentLighting()
    {
        var batteryFactory = new FakeQueryFactory();
        var lightingPort = new FakeLightingPort();
        var lightingFactory = new FakeLightingFactory(lightingPort);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(batteryFactory),
            UnavailableFanRpmFactory(),
            lightingFactory);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        DeviceCapabilities capabilities = await device.ReadCapabilitiesAsync(default);

        Assert.Equal(
            Enum.GetValues<LightingEffect>().Order(),
            capabilities.LightingEffects.Order());
        Assert.Empty(capabilities.PerformancePresets);
        Assert.Empty(capabilities.FanModes);
        Assert.Empty(capabilities.ChargeLimits);
        Assert.Equal(1, batteryFactory.OpenCount);
        Assert.Equal(1, lightingFactory.OpenCount);
    }

    [Fact]
    public async Task ReadLightingState_DoesNotOpenBatteryPort()
    {
        var batteryFactory = new FakeQueryFactory();
        var lightingPort = new FakeLightingPort();
        var lightingFactory = new FakeLightingFactory(lightingPort);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(batteryFactory),
            UnavailableFanRpmFactory(),
            lightingFactory);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        LightingSettings expected = LightingSettingsFactory.Static(
            new RgbColor(0x47, 0xE1, 0x0C),
            50);
        await device.SetLightingAsync(expected, default);

        ConfirmedDeviceState state = await device.ReadLightingStateAsync(default);

        Assert.Equal(expected, state.Lighting);
        Assert.Null(state.Battery);
        Assert.Null(state.Performance);
        Assert.Null(state.Fans);
        Assert.Equal(0, batteryFactory.OpenCount);
        Assert.Equal(1, lightingFactory.OpenCount);
    }

    [Fact]
    public async Task DisposeWhileProbeIsBlocked_PreventsStaleDevicePublication()
    {
        using var probeStarted = new ManualResetEventSlim();
        using var releaseProbe = new ManualResetEventSlim();
        var factory = new FakeQueryFactory();
        var provider = new WindowsVerifiedBladeDeviceProvider(
            () =>
            {
                probeStarted.Set();
                releaseProbe.Wait();
                return Probe(BatteryValidationProbeClassification.Ready);
            },
            Adapt(factory),
            UnavailableFanRpmFactory());

        Task<(bool Available, IBladeDevice Device)> probing = Task.Run(() =>
        {
            bool available = provider.TryGetVerifiedDevice(out IBladeDevice device);
            return (available, device);
        });
        Assert.True(probeStarted.Wait(TimeSpan.FromSeconds(1)));

        provider.Dispose();
        releaseProbe.Set();
        (bool available, IBladeDevice device) = await probing;

        Assert.False(available);
        Assert.Null(device);
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
        Assert.Equal(new ConfirmedBatteryState(true, expectedLimit, null), state.Battery);
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
                new ConfirmedBatteryState(false, null, null),
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
    public async Task ReadState_QueryFailureDisposesPortRetriesAndMapsTimeout()
    {
        var port = FakeQueryPort.Failure(HidSessionFailureKind.Timeout);
        var factory = new FakeQueryFactory(port);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        await Assert.ThrowsAsync<TimeoutException>(() => device.ReadStateAsync(default));

        Assert.Equal(3, factory.OpenCount);
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

        Assert.Equal(
            "Battery state query failed protocol validation (Checksum).",
            error.Message);
        Assert.Equal(HidProtocolValidationFailure.Checksum, error.ValidationFailure);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("raw report", error.Message, StringComparison.Ordinal);
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
    public async Task Dispose_DrainsActiveReadAndRejectsQueuedOrLateReadBeforePortOpen()
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
        await disposing;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
        ObjectDisposedException lateError =
            await Assert.ThrowsAsync<ObjectDisposedException>(() => late);

        Assert.True(lateRejectedBeforeRelease);
        Assert.DoesNotContain("SemaphoreSlim", lateError.ObjectName ?? string.Empty);
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task SettersRejectBeforeGateFactoryOrIo()
    {
        var factory = new FakeQueryFactory();
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        LightingSettings lighting = ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting;

        BladeWriteException performance = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.SetPerformanceAsync(PerformancePreset.Silent, default));
        BladeWriteException fans = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.SetFanModeAsync(FanMode.Automatic, default));
        BladeWriteException limit = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.SetChargeLimitAsync(BatteryPolicy.Create(80), default));
        BladeWriteException protection = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.SetBatteryProtectionOffAsync(default));
        BladeWriteException temporary = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.SetTemporaryFullChargeAsync(true, default));
        Assert.All(
            [performance, fans, limit, protection, temporary],
            exception => Assert.Equal(
                WriteSubmissionState.KnownNotApplied,
                exception.SubmissionState));
        BladeWriteException lightingError = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.SetLightingAsync(lighting, default));
        Assert.Equal(WriteSubmissionState.KnownNotApplied, lightingError.SubmissionState);

        Assert.Equal(0, factory.OpenCount);
    }

    [Fact]
    public async Task LightingWriter_IsOpenedOnceKeptAliveAndDisposedWithProvider()
    {
        var lightingPort = new FakeLightingPort();
        var lightingFactory = new FakeLightingFactory(lightingPort);
        var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        LightingSettings first = LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50);
        LightingSettings second = LightingSettingsFactory.Reactive(
            new RgbColor(4, 5, 6),
            75,
            ReactiveLightingDuration.Medium);

        await device.SetLightingAsync(first, default);
        await device.SetLightingAsync(second, default);
        await device.PulseReactiveKeyAsync(new ReactiveKeyPulse(77), default);

        Assert.Equal(
            LightingRendererOwnershipState.Reactive,
            await device.ReadLightingRendererOwnershipAsync(default));

        Assert.Equal(1, lightingFactory.OpenCount);
        Assert.Equal([first, second], lightingPort.Writes);
        Assert.Equal([77], lightingPort.Pulses.Select(pulse => (int)pulse.MatrixSlot));
        Assert.Equal(0, lightingPort.DisposeCount);

        LightingRendererOwnershipState invalidated =
            await provider.InvalidateLightingRendererAsync(default);
        Assert.Equal(LightingRendererOwnershipState.Reactive, invalidated);
        Assert.Equal(1, lightingPort.DisposeCount);
        Assert.Equal(1, lightingFactory.OpenCount);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            await device.ReadLightingRendererOwnershipAsync(default));

        provider.Dispose();
        Assert.Equal(1, lightingPort.DisposeCount);
    }

    [Fact]
    public async Task ReactivePulse_DoesNotWaitBehindUnrelatedHardwareObservationGate()
    {
        var lightingPort = new FakeLightingPort();
        var lightingFactory = new FakeLightingFactory(lightingPort);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        await device.SetLightingAsync(
            LightingSettingsFactory.Reactive(
                new RgbColor(0, 255, 0),
                77,
                ReactiveLightingDuration.Medium),
            default);
        var gate = Assert.IsType<SemaphoreSlim>(device.GetType()
            .GetField(
                "hardwareOperationGate",
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(device));
        await gate.WaitAsync();
        try
        {
            await device.PulseReactiveKeyAsync(new ReactiveKeyPulse(77), default)
                .WaitAsync(TimeSpan.FromMilliseconds(250));
        }
        finally
        {
            gate.Release();
        }

        Assert.Equal([77], lightingPort.Pulses.Select(value => (int)value.MatrixSlot));
    }

    [Fact]
    public async Task OwnedReactiveRenderer_IsReturnedWithoutRepeatingDeviceProbePerKey()
    {
        int probeCount = 0;
        var lightingPort = new FakeLightingPort();
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () =>
            {
                probeCount++;
                return Probe(BatteryValidationProbeClassification.Ready);
            },
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            new FakeLightingFactory(lightingPort));
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        await device.SetLightingAsync(
            LightingSettingsFactory.Reactive(
                new RgbColor(0, 255, 0),
                77,
                ReactiveLightingDuration.Medium),
            default);

        Assert.True(provider.TryGetOwnedKeyTriggeredLightingDevice(out IBladeDevice owned));
        Assert.Same(device, owned);
        Assert.Equal(1, probeCount);
    }

    [Fact]
    public async Task EnsureDriverMode_ReusesTheSerializedLightingPort()
    {
        var lightingPort = new FakeLightingPort();
        var lightingFactory = new FakeLightingFactory(lightingPort);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        await device.EnsureDriverModeAsync(default);
        await device.EnsureDriverModeAsync(default);
        Assert.True(await device.ReadDriverModeAsync(default));

        Assert.Equal(1, lightingFactory.OpenCount);
        Assert.Equal(2, lightingPort.DriverModeEnsureCount);
        Assert.Equal(1, lightingPort.DriverModeReadCount);
    }

    [Fact]
    public async Task ReleaseDriverMode_ReusesTheSerializedLightingPort()
    {
        var lightingPort = new FakeLightingPort();
        var lightingFactory = new FakeLightingFactory(lightingPort);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        await device.EnsureDriverModeAsync(default);
        Assert.False(await device.ReleaseDriverModeAsync(default));

        Assert.Equal(1, lightingFactory.OpenCount);
        Assert.Equal(1, lightingPort.DriverModeEnsureCount);
        Assert.Equal(1, lightingPort.DriverModeReleaseCount);
    }

    [Fact]
    public async Task OwnedAmbientRenderer_ReusesPortWithoutProbeOrHardwareObservationGate()
    {
        int probeCount = 0;
        var lightingPort = new FakeLightingPort();
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () =>
            {
                probeCount++;
                return Probe(BatteryValidationProbeClassification.Ready);
            },
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            new FakeLightingFactory(lightingPort));
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        await device.SetLightingAsync(
            LightingSettingsFactory.AmbientAwareness(
                AmbientAwarenessRegion.Full,
                77),
            default);
        Assert.True(provider.TryGetOwnedAmbientLightingDevice(out IBladeDevice owned));
        Assert.Same(device, owned);

        var gate = Assert.IsType<SemaphoreSlim>(device.GetType()
            .GetField(
                "hardwareOperationGate",
                System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(device));
        await gate.WaitAsync();
        try
        {
            await owned.PushAmbientLightingFrameAsync(
                    new AmbientLightingFrame(
                        new RgbColor[AmbientLightingFrame.SlotCount]),
                    default)
                .WaitAsync(TimeSpan.FromMilliseconds(250));
        }
        finally
        {
            gate.Release();
        }

        Assert.Equal(1, probeCount);
        Assert.Single(lightingPort.AmbientFrames);
    }

    [Fact]
    public async Task LightingBrightnessReadFailure_PreservesHealthyRenderer()
    {
        var lightingPort = new FakeLightingPort
        {
            ReadFailure = new IOException("transient brightness read failure"),
        };
        var lightingFactory = new FakeLightingFactory(lightingPort);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory(
                FakeQueryPort.Success(BatteryValidationSetting.Limit80))),
            UnavailableFanRpmFactory(),
            lightingFactory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        LightingSettings requested = LightingSettingsFactory.SpectrumCycling(75);
        await device.SetLightingAsync(requested, default);

        ConfirmedDeviceState observed = await device.ReadStateAsync(default);

        Assert.Null(observed.Lighting);
        Assert.Equal(0, lightingPort.DisposeCount);
        Assert.Equal(
            LightingRendererOwnershipState.Spectrum,
            await device.ReadLightingRendererOwnershipAsync(default));
    }

    [Fact]
    public async Task InvalidateLightingRenderer_ReturnsLiveFaultBeforeRetiringPort()
    {
        var lightingPort = new FakeLightingPort();
        var lightingFactory = new FakeLightingFactory(lightingPort);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        await device.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50),
            default);
        lightingPort.OwnershipState = LightingRendererOwnershipState.Faulted;

        LightingRendererOwnershipState invalidated =
            await provider.InvalidateLightingRendererAsync(default);

        Assert.Equal(LightingRendererOwnershipState.Faulted, invalidated);
        Assert.Equal(1, lightingPort.DisposeCount);
        Assert.Equal(1, lightingFactory.OpenCount);
        Assert.Equal(
            LightingRendererOwnershipState.None,
            await device.ReadLightingRendererOwnershipAsync(default));
    }

    [Fact]
    public async Task FaultedLightingObservation_RetiresCachedPortAndNextWriteReopens()
    {
        var faultedPort = new FakeLightingPort();
        var replacementPort = new FakeLightingPort();
        var lightingFactory = new FakeLightingFactory(faultedPort, replacementPort);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            lightingFactory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        LightingSettings requested = LightingSettingsFactory.Static(
            new RgbColor(4, 5, 6),
            75);

        await device.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50),
            default);
        faultedPort.OwnershipState = LightingRendererOwnershipState.Faulted;

        LightingRendererOwnershipState observed =
            await device.ReadLightingRendererOwnershipAsync(default);
        await device.SetLightingAsync(requested, default);

        Assert.Equal(LightingRendererOwnershipState.Faulted, observed);
        Assert.Equal(1, faultedPort.DisposeCount);
        Assert.Equal(2, lightingFactory.OpenCount);
        Assert.Equal([requested], replacementPort.Writes);
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
            Adapt(factory),
            UnavailableFanRpmFactory());

    private static IFanRpmQueryRunFactory UnavailableFanRpmFactory() =>
        new FakeFanRpmFactory(new(null, HidSessionFailureKind.NativeIoFailure));

    private static IProductionBatteryPortFactory Adapt(
        IBatteryQuerySequenceRunFactory factory) => new QueryFactoryAdapter(factory);

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

    private sealed class QueryFactoryAdapter(IBatteryQuerySequenceRunFactory inner)
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
                : new(new QueryPortAdapter(opened.Value), opened.Failure)
                {
                    Diagnostic = opened.Diagnostic,
                };
        }
    }

    private sealed class QueryPortAdapter(IBatteryQuerySequencePort inner)
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
            CancellationToken cancellationToken) =>
            Task.FromResult(AdapterPowerState.FullPower280W);

        public Task SetPerformanceAsync(
            PerformancePreset value,
            CancellationToken cancellationToken) => Reject();

        public Task SetFanModeAsync(
            FanMode value,
            CancellationToken cancellationToken) => Reject();

        public Task SetChargeLimitAsync(int percent, CancellationToken cancellationToken) =>
            Reject();

        public Task SetProtectionOffAsync(CancellationToken cancellationToken) => Reject();

        public Task SetTemporaryFullChargeAsync(bool enabled, CancellationToken cancellationToken) =>
            Reject();

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private static Task Reject() => Task.FromException(
            new BladeWriteException(
                WriteSubmissionState.KnownNotApplied,
                "Write unavailable in query adapter."));
    }

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

    private sealed class FakeFanRpmFactory(
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

    private sealed class FakeFanRpmPort(
        HidSessionResult<FanRpmReadings> result) : IFanRpmQueryPort
    {
        public int QueryCount { get; private set; }
        public int DisposeCount { get; private set; }

        public Task<HidSessionResult<FanRpmReadings>> QueryAsync(
            CancellationToken cancellationToken)
        {
            QueryCount++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeLightingFactory(params FakeLightingPort[] ports)
        : IProductionLightingPortFactory
    {
        private int nextPort;

        public DeviceCapabilities Capabilities { get; } = new(
            new HashSet<PerformancePreset>(),
            new HashSet<FanMode>(),
            new HashSet<LightingEffect>(Enum.GetValues<LightingEffect>()),
            new HashSet<int>(),
            false,
            false);

        public int OpenCount { get; private set; }

        public Task<HidSessionResult<IProductionLightingPort>> OpenAsync(
            CancellationToken cancellationToken)
        {
            OpenCount++;
            FakeLightingPort port = ports[Math.Min(nextPort, ports.Length - 1)];
            nextPort++;
            return Task.FromResult(
                new HidSessionResult<IProductionLightingPort>(port, null));
        }
    }

    private sealed class FakeLightingPort : IProductionLightingPort
    {
        public DeviceCapabilities Capabilities { get; } = new(
            new HashSet<PerformancePreset>(),
            new HashSet<FanMode>(),
            new HashSet<LightingEffect>(Enum.GetValues<LightingEffect>()),
            new HashSet<int>(),
            false,
            false);

        public LightingRendererOwnershipState OwnershipState { get; set; }

        public List<LightingSettings> Writes { get; } = [];

        public List<ReactiveKeyPulse> Pulses { get; } = [];

        public List<AmbientLightingFrame> AmbientFrames { get; } = [];

        public int DisposeCount { get; private set; }

        public int DriverModeEnsureCount { get; private set; }

        public int DriverModeReadCount { get; private set; }

        public int DriverModeReleaseCount { get; private set; }

        public Exception? ReadFailure { get; init; }

        public Task EnsureDriverModeAsync(CancellationToken cancellationToken)
        {
            DriverModeEnsureCount++;
            return Task.CompletedTask;
        }

        public Task<bool?> ReadDriverModeAsync(CancellationToken cancellationToken)
        {
            DriverModeReadCount++;
            return Task.FromResult<bool?>(true);
        }

        public Task<bool?> ReleaseDriverModeAsync(CancellationToken cancellationToken)
        {
            DriverModeReleaseCount++;
            return Task.FromResult<bool?>(false);
        }

        public Task<LightingSettings?> ReadStateAsync(CancellationToken cancellationToken) =>
            ReadFailure is null
                ? Task.FromResult<LightingSettings?>(Writes.LastOrDefault())
                : Task.FromException<LightingSettings?>(ReadFailure);

        public Task SetLightingAsync(
            LightingSettings settings,
            CancellationToken cancellationToken)
        {
            Writes.Add(settings);
            OwnershipState = settings.Effect switch
            {
                LightingEffect.Off => LightingRendererOwnershipState.None,
                LightingEffect.Static => LightingRendererOwnershipState.Static,
                LightingEffect.Spectrum => LightingRendererOwnershipState.Spectrum,
                LightingEffect.Breathing => LightingRendererOwnershipState.Breathing,
                LightingEffect.Reactive => LightingRendererOwnershipState.Reactive,
                LightingEffect.AmbientAwareness =>
                    LightingRendererOwnershipState.AmbientAwareness,
                _ => throw new ArgumentOutOfRangeException(),
            };
            return Task.CompletedTask;
        }

        public Task PulseReactiveKeyAsync(
            ReactiveKeyPulse pulse,
            CancellationToken cancellationToken)
        {
            Pulses.Add(pulse);
            return Task.CompletedTask;
        }

        public Task PushAmbientLightingFrameAsync(
            AmbientLightingFrame frame,
            CancellationToken cancellationToken)
        {
            AmbientFrames.Add(frame);
            return Task.CompletedTask;
        }

        public async Task<LightingRendererOwnershipState> InvalidateAsync()
        {
            LightingRendererOwnershipState observed = OwnershipState;
            await DisposeAsync();
            return observed;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            OwnershipState = LightingRendererOwnershipState.None;
            return ValueTask.CompletedTask;
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
