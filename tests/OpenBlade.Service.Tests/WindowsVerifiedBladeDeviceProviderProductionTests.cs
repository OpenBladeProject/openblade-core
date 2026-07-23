using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class WindowsVerifiedBladeDeviceProviderProductionTests
{
    [Fact]
    public async Task ReadCustomPerformance_MergesNvmlClockOffsetsIntoConfirmedState()
    {
        DeviceCapabilities capabilities = ThermalCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Custom },
            new HashSet<FanMode> { FanMode.Automatic });
        var custom = new CustomPerformanceSettings(
            CustomPerformanceLevel.High,
            CustomPerformanceLevel.Medium);
        var port = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null,
            performanceFan: new ProductionPerformanceFanState(
                PerformancePreset.Custom,
                FanMode.Automatic)
            {
                CustomPerformance = custom,
            });
        var gpu = new FakeGpuClockOffsetController();
        using var provider = ReadyProvider(
            new FakeProductionFactory(capabilities, port),
            gpu);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        ConfirmedDeviceState state = await device.ReadStateAsync(default);
        GpuClockOffsetCapabilities expected = Assert.IsType<GpuClockOffsetCapabilities>(
            gpu.Capabilities);

        Assert.Equal(1, gpu.QueryCount);
        Assert.Equal(expected, state.GpuClockOffsets);
        Assert.Equal(expected.Current, state.CustomPerformance?.GpuClockOffsets);
    }

    [Fact]
    public async Task ReadBalancedPerformance_DoesNotWakeGpuForClockOffsetQuery()
    {
        DeviceCapabilities capabilities = ThermalCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Balanced },
            new HashSet<FanMode> { FanMode.Automatic });
        var port = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null,
            performanceFan: new ProductionPerformanceFanState(
                PerformancePreset.Balanced,
                FanMode.Automatic));
        var gpu = new FakeGpuClockOffsetController();
        using var provider = ReadyProvider(
            new FakeProductionFactory(capabilities, port),
            gpu);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        _ = await device.ReadStateAsync(default);

        Assert.Equal(0, gpu.QueryCount);
    }

    [Fact]
    public async Task SetCustomPerformance_PreflightsThenAppliesFirmwareAndGpuOffsets()
    {
        DeviceCapabilities capabilities = ThermalCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Custom },
            new HashSet<FanMode> { FanMode.Automatic });
        var port = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var gpu = new FakeGpuClockOffsetController();
        var requested = new CustomPerformanceSettings(
            CustomPerformanceLevel.High,
            CustomPerformanceLevel.Medium)
        {
            GpuClockOffsets = new GpuClockOffsetSettings(125, 500),
        };
        using var provider = ReadyProvider(
            new FakeProductionFactory(capabilities, port),
            gpu);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        await device.SetPerformanceAsync(
            PerformancePreset.Custom,
            requested,
            default);

        Assert.Equal(1, gpu.QueryCount);
        Assert.Equal(1, port.SetCustomPerformanceCount);
        Assert.Equal(requested, port.LastCustomPerformance);
        Assert.Equal([requested.GpuClockOffsets], gpu.Writes);
    }

    [Fact]
    public async Task SetCustomPerformance_UnavailableGpuTuningRejectsBeforeFirmwareWrite()
    {
        DeviceCapabilities capabilities = ThermalCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Custom },
            new HashSet<FanMode> { FanMode.Automatic });
        var port = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var gpu = new FakeGpuClockOffsetController { Capabilities = null };
        var requested = new CustomPerformanceSettings(
            CustomPerformanceLevel.High,
            CustomPerformanceLevel.Medium)
        {
            GpuClockOffsets = new GpuClockOffsetSettings(125, 500),
        };
        using var provider = ReadyProvider(
            new FakeProductionFactory(capabilities, port),
            gpu);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(() =>
            device.SetPerformanceAsync(
                PerformancePreset.Custom,
                requested,
                default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Equal(0, port.SetCustomPerformanceCount);
        Assert.Empty(gpu.Writes);
    }

    [Fact]
    public async Task SetCustomPerformance_GpuWriteFailureReportsPartialOperationAsIndeterminate()
    {
        DeviceCapabilities capabilities = ThermalCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Custom },
            new HashSet<FanMode> { FanMode.Automatic });
        var port = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var gpu = new FakeGpuClockOffsetController { FailWrites = true };
        var requested = new CustomPerformanceSettings(
            CustomPerformanceLevel.High,
            CustomPerformanceLevel.Medium)
        {
            GpuClockOffsets = new GpuClockOffsetSettings(125, 500),
        };
        using var provider = ReadyProvider(
            new FakeProductionFactory(capabilities, port),
            gpu);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(() =>
            device.SetPerformanceAsync(
                PerformancePreset.Custom,
                requested,
                default));

        Assert.Equal(1, port.SetCustomPerformanceCount);
        Assert.Equal(WriteSubmissionState.Indeterminate, error.SubmissionState);
        Assert.Contains(
            "Custom power mode was applied",
            error.Message,
            StringComparison.Ordinal);
    }

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
    public async Task ReadMcuVersion_UsesFreshIdentityGatedReadOnlyPort()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable();
        var port = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null,
            mcuVersion: "1.5.0.0");
        var factory = new FakeProductionFactory(capabilities, port);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        string? version = await device.ReadMcuVersionAsync(default);

        Assert.Equal("1.5.0.0", version);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, port.McuVersionQueryCount);
        Assert.Equal(1, port.DisposeCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadStartupAnimation_UsesFreshIdentityGatedReadOnlyPort(bool enabled)
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable();
        var port = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null,
            startupAnimation: enabled);
        var factory = new FakeProductionFactory(capabilities, port);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        bool? actual = await device.ReadStartupAnimationAsync(default);

        Assert.Equal(enabled, actual);
        Assert.Equal(1, port.StartupAnimationQueryCount);
        Assert.Equal(1, port.DisposeCount);
    }

    [Fact]
    public async Task ReadStartupAnimation_QueryFailureReturnsUnavailable()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable();
        var port = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null,
            startupAnimation: true)
        {
            StartupAnimationFailure = new IOException("query failed"),
        };
        var factory = new FakeProductionFactory(capabilities, port);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        bool? actual = await device.ReadStartupAnimationAsync(default);

        Assert.Null(actual);
        Assert.Equal(1, port.StartupAnimationQueryCount);
    }

    [Theory]
    [InlineData(PerformancePreset.Balanced)]
    [InlineData(PerformancePreset.Silent)]
    [InlineData(PerformancePreset.Performance)]
    [InlineData(PerformancePreset.BatterySaver)]
    [InlineData(PerformancePreset.BatteryBalanced)]
    public async Task ReadState_PublishesMatchingAutomaticThermalStateWithBattery(
        PerformancePreset performance)
    {
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        var port = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null,
            performanceFan: new(performance, FanMode.Automatic));
        var factory = new FakeProductionFactory(capabilities, port);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        ConfirmedDeviceState state = await device.ReadStateAsync(default);

        Assert.Equal(performance, state.Performance);
        Assert.Equal(FanMode.Automatic, state.Fans);
        Assert.Equal(new ConfirmedBatteryState(true, 80, null), state.Battery);
        Assert.Equal(1, port.PerformanceFanQueryCount);
        Assert.Equal(1, port.DisposeCount);
    }

    [Fact]
    public async Task ReadState_FixedMaximumDoesNotInventPerformancePreset()
    {
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        var port = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null,
            performanceFan: new(null, FanMode.Maximum));
        var factory = new FakeProductionFactory(capabilities, port);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        ConfirmedDeviceState state = await device.ReadStateAsync(default);

        Assert.Null(state.Performance);
        Assert.Equal(FanMode.Maximum, state.Fans);
        Assert.Equal(new ConfirmedBatteryState(true, 80, null), state.Battery);
        Assert.Equal(1, port.PerformanceFanQueryCount);
    }

    [Theory]
    [InlineData(PerformancePreset.Balanced)]
    [InlineData(PerformancePreset.Silent)]
    [InlineData(PerformancePreset.Performance)]
    public async Task AdmittedPerformanceSetterDelegatesExactlyOnceToFreshPort(
        PerformancePreset preset)
    {
        DeviceCapabilities capabilities = ThermalCapabilities(
            new HashSet<PerformancePreset> { preset },
            new HashSet<FanMode>());
        var port = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var factory = new FakeProductionFactory(capabilities, port);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        await device.SetPerformanceAsync(preset, default);

        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, port.SetPerformanceCount);
        Assert.Equal(preset, port.LastPerformance);
        Assert.Equal(0, port.SetFanModeCount);
        Assert.Equal(1, port.DisposeCount);
    }

    [Theory]
    [InlineData(FanMode.Automatic)]
    [InlineData(FanMode.Maximum)]
    public async Task AdmittedFanSetterDelegatesExactlyOnceToFreshPort(FanMode mode)
    {
        DeviceCapabilities capabilities = ThermalCapabilities(
            new HashSet<PerformancePreset>(),
            new HashSet<FanMode> { mode });
        var port = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var factory = new FakeProductionFactory(capabilities, port);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        await device.SetFanModeAsync(mode, default);

        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, port.SetFanModeCount);
        Assert.Equal(mode, port.LastFanMode);
        Assert.Equal(0, port.SetPerformanceCount);
        Assert.Equal(1, port.DisposeCount);
    }

    [Theory]
    [InlineData(HidProtocolValidationFailure.TransactionMismatch)]
    [InlineData(HidProtocolValidationFailure.CommandClassMismatch)]
    [InlineData(HidProtocolValidationFailure.CommandIdMismatch)]
    public async Task ResponseIdentityMismatchRetriesReadWithFreshVerifiedSession(
        HidProtocolValidationFailure failure)
    {
        DeviceCapabilities capabilities = BatteryCapabilities(
            new HashSet<int> { 80 },
            protectionOff: true);
        var collided = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: false)
        {
            ProtectionFailure = new RazerProtocolException("interleaved response", failure),
        };
        var recovered = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: false);
        var factory = new FakeProductionFactory(capabilities, collided, recovered);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        ConfirmedDeviceState state = await device.ReadStateAsync(default);

        Assert.Equal(new ConfirmedBatteryState(true, 80, false), state.Battery);
        Assert.Equal(2, factory.OpenCount);
        Assert.Equal(1, collided.DisposeCount);
        Assert.Equal(1, recovered.DisposeCount);
    }

    [Fact]
    public async Task TwoTransactionMismatchesRecoverOnThirdFreshSession()
    {
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        var first = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null)
        {
            ProtectionFailure = TransactionMismatch(),
        };
        var second = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null)
        {
            ProtectionFailure = TransactionMismatch(),
        };
        var recovered = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var factory = new FakeProductionFactory(capabilities, first, second, recovered);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        ConfirmedDeviceState state = await device.ReadStateAsync(default);

        Assert.Equal(new ConfirmedBatteryState(true, 80, null), state.Battery);
        Assert.Equal(3, factory.OpenCount);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        Assert.Equal(1, recovered.DisposeCount);
    }

    [Theory]
    [InlineData(HidProtocolValidationFailure.TransactionMismatch)]
    [InlineData(HidProtocolValidationFailure.CommandClassMismatch)]
    [InlineData(HidProtocolValidationFailure.CommandIdMismatch)]
    public async Task TemporaryQueryResponseIdentityMismatchRetriesBothQueriesOnFreshSession(
        HidProtocolValidationFailure failure)
    {
        DeviceCapabilities capabilities = BatteryCapabilities(
            new HashSet<int> { 80 },
            protectionOff: true);
        var collided = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.ProtectionOff,
            temporary: false)
        {
            TemporaryFailure = new RazerProtocolException("interleaved response", failure),
        };
        var recovered = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: false);
        var factory = new FakeProductionFactory(capabilities, collided, recovered);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        ConfirmedDeviceState state = await device.ReadStateAsync(default);

        Assert.Equal(new ConfirmedBatteryState(true, 80, false), state.Battery);
        Assert.Equal(1, collided.ProtectionQueryCount);
        Assert.Equal(1, collided.TemporaryQueryCount);
        Assert.Equal(1, recovered.ProtectionQueryCount);
        Assert.Equal(1, recovered.TemporaryQueryCount);
        Assert.Equal(2, factory.OpenCount);
    }

    [Fact]
    public async Task RepeatedTransactionMismatchStopsAfterTwoFreshSessionRetries()
    {
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        var first = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null)
        {
            ProtectionFailure = TransactionMismatch(),
        };
        var second = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null)
        {
            ProtectionFailure = TransactionMismatch(),
        };
        var third = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null)
        {
            ProtectionFailure = TransactionMismatch(),
        };
        var factory = new FakeProductionFactory(capabilities, first, second, third);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        RazerProtocolException error = await Assert.ThrowsAsync<RazerProtocolException>(
            () => device.ReadStateAsync(default));

        Assert.Equal(HidProtocolValidationFailure.TransactionMismatch, error.ValidationFailure);
        Assert.Equal(
            "Battery state query failed protocol validation (TransactionMismatch).",
            error.Message);
        Assert.Equal(3, factory.OpenCount);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        Assert.Equal(1, third.DisposeCount);
    }

    [Theory]
    [InlineData(HidProtocolValidationFailure.Checksum)]
    [InlineData(HidProtocolValidationFailure.PayloadValidator)]
    public async Task OtherProtocolFailuresRemainSingleAttemptAndFailClosed(
        HidProtocolValidationFailure failure)
    {
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        var rejected = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null)
        {
            ProtectionFailure = new RazerProtocolException("rejected", failure),
        };
        var neverOpened = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var factory = new FakeProductionFactory(capabilities, rejected, neverOpened);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        RazerProtocolException error = await Assert.ThrowsAsync<RazerProtocolException>(
            () => device.ReadStateAsync(default));

        Assert.Equal(failure, error.ValidationFailure);
        Assert.Equal(
            $"Battery state query failed protocol validation ({failure}).",
            error.Message);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, rejected.DisposeCount);
        Assert.Equal(0, neverOpened.ProtectionQueryCount);
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
        IProductionBatteryPortFactory factory,
        IGpuClockOffsetController? gpuClockOffsets = null) => new(
            () => Probe(BatteryValidationProbeClassification.Ready),
            factory,
            new UnavailableFanRpmFactory(),
            gpuClockOffsets: gpuClockOffsets);

    private static DeviceCapabilities BatteryCapabilities(
        IReadOnlySet<int> limits,
        bool protectionOff = false) => new(
            new HashSet<PerformancePreset>(),
            new HashSet<FanMode>(),
            new HashSet<LightingEffect>(),
            limits,
            protectionOff,
            SupportsTemporaryFullCharge: false);

    private static DeviceCapabilities ThermalCapabilities(
        IReadOnlySet<PerformancePreset> presets,
        IReadOnlySet<FanMode> fanModes) => new(
            presets,
            fanModes,
            new HashSet<LightingEffect>(),
            new HashSet<int>(),
            SupportsBatteryProtectionOff: false,
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

    private static RazerProtocolException TransactionMismatch() => new(
        "interleaved response",
        HidProtocolValidationFailure.TransactionMismatch);

    private sealed class UnavailableFanRpmFactory : IFanRpmQueryRunFactory
    {
        public Task<HidSessionResult<IFanRpmQueryPort>> OpenAsync(
            CancellationToken cancellationToken) => Task.FromResult(
                new HidSessionResult<IFanRpmQueryPort>(
                    null,
                    HidSessionFailureKind.NativeIoFailure));
    }

    private sealed class FakeProductionFactory(
        DeviceCapabilities capabilities,
        params FakeProductionPort[] ports) : IProductionBatteryPortFactory
    {
        private readonly Queue<FakeProductionPort> ports = new(ports);

        public DeviceCapabilities Capabilities { get; } = capabilities;

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
        bool blockWrite = false,
        ProductionPerformanceFanState? performanceFan = null,
        string? mcuVersion = null,
        bool? startupAnimation = null) : IProductionBatteryPort
    {
        private readonly TaskCompletionSource? protectionRelease = blockProtectionQuery
            ? new(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
        private readonly TaskCompletionSource? writeRelease = blockWrite
            ? new(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;

        public DeviceCapabilities Capabilities { get; } = capabilities;
        public bool SupportsTemporaryFullChargeQuery => temporary.HasValue;
        public bool SupportsMcuVersionQuery => mcuVersion is not null;
        public bool SupportsStartupAnimationQuery => startupAnimation.HasValue;
        public BladeWriteException? WriteFailure { get; set; }
        public Exception? ProtectionFailure { get; init; }
        public Exception? TemporaryFailure { get; init; }
        public Exception? StartupAnimationFailure { get; init; }
        public TaskCompletionSource ProtectionQueryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ProtectionQueryCount { get; private set; }
        public int TemporaryQueryCount { get; private set; }
        public int PerformanceFanQueryCount { get; private set; }
        public int McuVersionQueryCount { get; private set; }
        public int StartupAnimationQueryCount { get; private set; }
        public int SetPerformanceCount { get; private set; }
        public int SetCustomPerformanceCount { get; private set; }
        public int SetFanModeCount { get; private set; }
        public PerformancePreset? LastPerformance { get; private set; }
        public CustomPerformanceSettings? LastCustomPerformance { get; private set; }
        public FanMode? LastFanMode { get; private set; }
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

            if (ProtectionFailure is not null)
            {
                throw ProtectionFailure;
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
            return TemporaryFailure is null
                ? Task.FromResult(temporary)
                : Task.FromException<bool?>(TemporaryFailure);
        }

        public Task<ProductionPerformanceFanState> QueryPerformanceFanStateAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PerformanceFanQueryCount++;
            return Task.FromResult(
                performanceFan ?? new ProductionPerformanceFanState(null, null));
        }

        public Task<AdapterPowerState> QueryAdapterPowerStateAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(AdapterPowerState.FullPower280W);

        public Task<string?> QueryMcuVersionAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            McuVersionQueryCount++;
            return Task.FromResult(mcuVersion);
        }

        public Task<bool?> QueryStartupAnimationAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartupAnimationQueryCount++;
            return StartupAnimationFailure is null
                ? Task.FromResult(startupAnimation)
                : Task.FromException<bool?>(StartupAnimationFailure);
        }

        public Task SetPerformanceAsync(
            PerformancePreset value,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetPerformanceCount++;
            LastPerformance = value;
            return WriteFailure is null ? Task.CompletedTask : Task.FromException(WriteFailure);
        }

        public Task SetCustomPerformanceAsync(
            CustomPerformanceSettings value,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetCustomPerformanceCount++;
            LastCustomPerformance = value;
            return WriteFailure is null ? Task.CompletedTask : Task.FromException(WriteFailure);
        }

        public Task SetFanModeAsync(FanMode value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetFanModeCount++;
            LastFanMode = value;
            return WriteFailure is null ? Task.CompletedTask : Task.FromException(WriteFailure);
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

    private sealed class FakeGpuClockOffsetController : IGpuClockOffsetController
    {
        public GpuClockOffsetCapabilities? Capabilities { get; set; } = new(
            "Test NVIDIA GPU",
            new GpuClockOffsetRange(-1000, 1000, 0),
            new GpuClockOffsetRange(-2000, 6000, 0));

        public int QueryCount { get; private set; }

        public List<GpuClockOffsetSettings> Writes { get; } = [];

        public bool FailWrites { get; init; }

        public GpuClockOffsetCapabilities? Query()
        {
            QueryCount++;
            return Capabilities;
        }

        public void Set(GpuClockOffsetSettings settings)
        {
            Writes.Add(settings);
            if (FailWrites)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "NVML write failed.");
            }
        }
    }
}
