using System.Diagnostics;
using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;
using static OpenBlade.Service.Tests.ProductionLightingSessionFixture;
using static OpenBlade.Service.Tests.VerifiedBladeProviderFixture;

namespace OpenBlade.Service.Tests;

public sealed class WindowsVerifiedBladeDeviceProviderTests
{
    private static readonly TimeSpan TestDeadlockGuard = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan TestShutdownBudget = TimeSpan.FromMilliseconds(50);

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
            "CreateWindowsFanRpmForService",
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
    public async Task ReadAdapterPowerState_PropagatesTransientQueryFailureAndDisposes()
    {
        FakeQueryPort port = FakeQueryPort.Success(BatteryValidationSetting.Limit80);
        var factory = new FakeQueryFactory(port);
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(factory, new IOException("temporary adapter access failure")),
            UnavailableFanRpmFactory());
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        IOException failure = await Assert.ThrowsAsync<IOException>(
            () => device.ReadAdapterPowerStateAsync(CancellationToken.None));

        Assert.Contains("temporary adapter access failure", failure.Message);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, port.DisposeCount);
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

    [Fact]
    public void TryGetVerifiedDevice_ReportsAuthoritativeFirmwareCompatibility()
    {
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(
                BatteryValidationProbeClassification.HostIdentityBlocked,
                BatteryValidationFirmwareCompatibility.UntestedBlocked),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory());

        Assert.False(provider.TryGetVerifiedDevice(out _));
        Assert.Equal(
            FirmwareCompatibilityStatus.UntestedBlocked,
            provider.FirmwareCompatibility);
    }

    [Fact]
    public async Task ModelScopedFirmware_AllowsVerifiedDeviceAndOwnedRendererWithoutOverride()
    {
        using var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(
                BatteryValidationProbeClassification.Ready,
                BatteryValidationFirmwareCompatibility.ModelScoped),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            new FakeLightingFactory(new FakeLightingPort()));

        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        await device.SetLightingAsync(
            LightingSettingsFactory.Reactive(
                new RgbColor(0, 255, 0),
                77,
                ReactiveLightingDuration.Medium),
            default);

        Assert.Equal(FirmwareCompatibilityStatus.ModelScoped, provider.FirmwareCompatibility);
        Assert.False(provider.RequiresUntestedFirmwareOverride);
        Assert.True(provider.TryGetOwnedKeyTriggeredLightingDevice(out _));
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
        Assert.True(capabilities.ObservedLightingEffects.SetEquals(
            [LightingEffect.Ripple, LightingEffect.Wheel]));
        Assert.Equal(
            DeviceFeatureAccess.ReadWrite,
            capabilities.SpecialKeyBindingsAccess);
        Assert.Equal(
            SpecialKeySlotSet.M3 | SpecialKeySlotSet.M4 | SpecialKeySlotSet.M5,
            capabilities.WritableSpecialKeySlots);
        Assert.Equal(
            DeviceFeatureAccess.ReadOnly,
            capabilities.LampArrayLightingAccess);
        Assert.True(capabilities.SupportsPlaybackMuteIndicator);
        Assert.Empty(capabilities.PerformancePresets);
        Assert.Empty(capabilities.FanModes);
        Assert.Empty(capabilities.ChargeLimits);
        Assert.Equal(1, batteryFactory.OpenCount);
        Assert.Equal(1, lightingFactory.OpenCount);
    }

    [Fact]
    public async Task DisposeWhileProbeIsBlocked_PreventsStaleDevicePublication()
    {
        var probeStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseProbe = new ManualResetEventSlim();
        var factory = new FakeQueryFactory();
        var provider = new WindowsVerifiedBladeDeviceProvider(
            () =>
            {
                probeStarted.TrySetResult();
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
        await probeStarted.Task.WaitAsync(TestDeadlockGuard);

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
        var providerClosing = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(factory),
            UnavailableFanRpmFactory(),
            onClosing: () => providerClosing.TrySetResult());
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        Task<ConfirmedDeviceState> first = device.ReadStateAsync(default);
        await firstPort.QueryStarted.Task.WaitAsync(TestDeadlockGuard);
        Task<ConfirmedDeviceState> queued = device.ReadStateAsync(default);

        Task disposing = Task.Run(provider.Dispose);
        await providerClosing.Task.WaitAsync(TestDeadlockGuard);
        Assert.False(provider.TryGetVerifiedDevice(out _));
        Task<ConfirmedDeviceState> late = device.ReadStateAsync(default);
        bool lateRejectedBeforeRelease = late.IsCompleted;

        firstPort.Release();
        Assert.Equal(65, (await first).Battery!.ChargeLimitPercent);
        await disposing.WaitAsync(TestDeadlockGuard);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => queued);
        ObjectDisposedException lateError =
            await Assert.ThrowsAsync<ObjectDisposedException>(() => late);

        Assert.True(lateRejectedBeforeRelease);
        Assert.DoesNotContain("SemaphoreSlim", lateError.ObjectName ?? string.Empty);
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task UnavailableSettersRefuseBeforeGateFactoryOrIo()
    {
        var factory = new FakeQueryFactory();
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        LightingSettings lighting = ProfileDefaults.CreateSafeDefaults().PluggedIn.Lighting;

        CapabilityUnavailableException performance =
            await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => device.SetPerformanceAsync(PerformancePreset.Silent, default));
        CapabilityUnavailableException fans =
            await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => device.SetFanModeAsync(FanMode.Automatic, default));
        CapabilityUnavailableException limit =
            await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => device.SetChargeLimitAsync(BatteryPolicy.Create(80), default));
        CapabilityUnavailableException protection =
            await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => device.SetBatteryProtectionOffAsync(default));
        CapabilityUnavailableException temporary =
            await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => device.SetTemporaryFullChargeAsync(true, default));
        Assert.All(
            [performance, fans, limit, protection, temporary],
            exception => Assert.Contains(
                "not admitted",
                exception.Message,
                StringComparison.OrdinalIgnoreCase));
        CapabilityUnavailableException lightingError =
            await Assert.ThrowsAsync<CapabilityUnavailableException>(
            () => device.SetLightingAsync(lighting, default));
        Assert.Contains("not admitted", lightingError.Message, StringComparison.OrdinalIgnoreCase);

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

    [Fact]
    public async Task Dispose_ReturnsWithinBudgetWhenTheHardwareGateIsHeld()
    {
        BlockedShutdownScenario scenario = await StartBlockedShutdownScenarioAsync();
        var elapsed = Stopwatch.StartNew();

        scenario.Provider.Dispose();
        elapsed.Stop();

        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(1),
            $"Provider disposal took {elapsed.Elapsed}.");
        Assert.Equal(0, scenario.LightingPort.DrainCount);
        await scenario.ReleaseAndDrainAsync();
    }

    [Fact]
    public async Task Dispose_RejectsNewOperationsImmediately()
    {
        BlockedShutdownScenario scenario = await StartBlockedShutdownScenarioAsync();

        Task disposal = Task.Run(scenario.Provider.Dispose);
        await scenario.ProviderClosing.WaitAsync(TestDeadlockGuard);

        Assert.False(scenario.Provider.TryGetVerifiedDevice(out _));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => scenario.Device.ReadStateAsync(default));
        await disposal.WaitAsync(TestDeadlockGuard);
        await scenario.ReleaseAndDrainAsync();
    }

    [Fact]
    public async Task Dispose_DoesNotDisposeThePortWhenTheGateTimesOut()
    {
        BlockedShutdownScenario scenario = await StartBlockedShutdownScenarioAsync();

        scenario.Provider.Dispose();

        Assert.Equal(0, scenario.LightingPort.DrainCount);
        Assert.Equal(0, scenario.LightingPort.DisposeCount);
        Assert.Equal(0, scenario.GpuActivitySource.DisposeCount);
        await scenario.ReleaseAndDrainAsync();
    }

    [Fact]
    public async Task Dispose_EventuallyDisposesThePortExactlyOnceAfterTheGateFrees()
    {
        BlockedShutdownScenario scenario = await StartBlockedShutdownScenarioAsync();
        scenario.Provider.Dispose();

        await scenario.ReleaseAndDrainAsync();

        Assert.Equal(1, scenario.LightingPort.DrainCount);
        Assert.Equal(1, scenario.LightingPort.DisposeCount);
        Assert.Equal(1, scenario.GpuActivitySource.DisposeCount);
    }

    [Fact]
    public async Task Dispose_PermanentlyStuckCleanupDoesNotThrowOrHang()
    {
        BlockedShutdownScenario scenario = await StartBlockedShutdownScenarioAsync();

        await Task.Run(scenario.Provider.Dispose).WaitAsync(TestDeadlockGuard);

        Assert.Equal(0, scenario.LightingPort.DrainCount);
        await scenario.ReleaseAndDrainAsync();
    }

    [Fact]
    public async Task Dispose_NormalPathDisposesThePortAndIsIdempotent()
    {
        var lightingPort = new FakeLightingPort();
        var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory()),
            UnavailableFanRpmFactory(),
            new FakeLightingFactory(lightingPort),
            shutdownBudget: TestShutdownBudget);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        await device.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50),
            default);

        provider.Dispose();
        provider.Dispose();

        Assert.Equal(1, lightingPort.DrainCount);
        Assert.Equal(1, lightingPort.DisposeCount);
        Assert.False(provider.TryGetVerifiedDevice(out _));
    }

    private static async Task<BlockedShutdownScenario> StartBlockedShutdownScenarioAsync()
    {
        FakeQueryPort blockedPort =
            FakeQueryPort.Blocked(BatteryValidationSetting.Limit80);
        var lightingPort = new FakeLightingPort();
        var gpuActivitySource = new CountingGpuActivitySource();
        var providerClosing = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new WindowsVerifiedBladeDeviceProvider(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(new FakeQueryFactory(blockedPort)),
            UnavailableFanRpmFactory(),
            new FakeLightingFactory(lightingPort),
            shutdownBudget: TestShutdownBudget,
            onClosing: () => providerClosing.TrySetResult(),
            gpuActivitySource: gpuActivitySource);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));
        await device.SetLightingAsync(
            LightingSettingsFactory.Static(new RgbColor(1, 2, 3), 50),
            default);
        Task<ConfirmedDeviceState> hardwareOperation = device.ReadStateAsync(default);
        await blockedPort.QueryStarted.Task.WaitAsync(TestDeadlockGuard);
        return new(
            provider,
            device,
            lightingPort,
            gpuActivitySource,
            blockedPort,
            providerClosing.Task,
            hardwareOperation);
    }

    private static WindowsVerifiedBladeDeviceProvider ReadyProvider(
        IBatteryQuerySequenceRunFactory factory) => new(
            () => Probe(BatteryValidationProbeClassification.Ready),
            Adapt(factory),
            UnavailableFanRpmFactory());

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

    private sealed record BlockedShutdownScenario(
        WindowsVerifiedBladeDeviceProvider Provider,
        IBladeDevice Device,
        FakeLightingPort LightingPort,
        CountingGpuActivitySource GpuActivitySource,
        FakeQueryPort BlockedPort,
        Task ProviderClosing,
        Task<ConfirmedDeviceState> HardwareOperation)
    {
        internal async Task ReleaseAndDrainAsync()
        {
            BlockedPort.Release();
            await Task.WhenAll(HardwareOperation, LightingPort.DrainStarted.Task)
                .WaitAsync(TestDeadlockGuard);
        }
    }

    private sealed class CountingGpuActivitySource : IGpuActivitySource, IDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask<bool?> IsActiveAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(false);
        }

        public void Dispose() => DisposeCount++;
    }
}
