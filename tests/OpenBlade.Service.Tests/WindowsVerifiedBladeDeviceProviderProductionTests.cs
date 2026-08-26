using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class WindowsVerifiedBladeDeviceProviderProductionTests
{
    [Fact]
    public async Task ReadPerformanceFanState_SkipsUnrelatedBatteryQueries()
    {
        DeviceCapabilities capabilities = ThermalCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Performance },
            new HashSet<FanMode> { FanMode.Automatic });
        var port = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: true,
            performanceFan: new ProductionPerformanceFanState(
                PerformancePreset.Performance,
                FanMode.Automatic))
        {
            ProtectionFailure = new IOException("unrelated protection failure"),
            TemporaryFailure = new IOException("unrelated temporary-charge failure"),
        };
        var factory = new FakeProductionFactory(capabilities, port);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        ConfirmedDeviceState state = await device.ReadPerformanceFanStateAsync(default);

        Assert.Equal(PerformancePreset.Performance, state.Performance);
        Assert.Equal(FanMode.Automatic, state.Fans);
        Assert.Null(state.Battery);
        Assert.Null(state.Lighting);
        Assert.Equal(0, port.ProtectionQueryCount);
        Assert.Equal(0, port.TemporaryQueryCount);
        Assert.Equal(1, port.PerformanceFanQueryCount);
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task ReadPerformanceFanState_RetriesTransientFailureWithFreshSession()
    {
        DeviceCapabilities capabilities = ThermalCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Balanced },
            new HashSet<FanMode> { FanMode.Automatic });
        var failedPort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null)
        {
            PerformanceFanFailure = new IOException("temporary thermal read failure"),
        };
        var recoveredPort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null,
            performanceFan: new ProductionPerformanceFanState(
                PerformancePreset.Balanced,
                FanMode.Automatic));
        var factory = new FakeProductionFactory(
            capabilities,
            failedPort,
            recoveredPort);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        ConfirmedDeviceState state = await device.ReadPerformanceFanStateAsync(default);

        Assert.Equal(PerformancePreset.Balanced, state.Performance);
        Assert.Equal(FanMode.Automatic, state.Fans);
        Assert.Equal(2, factory.OpenCount);
        Assert.Equal(1, failedPort.DisposeCount);
        Assert.Equal(1, recoveredPort.DisposeCount);
    }

    [Fact]
    public async Task ReadPerformanceFanState_DoesNotRetrySemanticMismatch()
    {
        DeviceCapabilities capabilities = ThermalCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Balanced },
            new HashSet<FanMode> { FanMode.Automatic });
        var failedPort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null)
        {
            PerformanceFanFailure = new RazerProtocolException(
                "unrecognized thermal state",
                HidProtocolValidationFailure.PayloadValidator),
        };
        var unusedPort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null,
            performanceFan: new ProductionPerformanceFanState(
                PerformancePreset.Balanced,
                FanMode.Automatic));
        var factory = new FakeProductionFactory(
            capabilities,
            failedPort,
            unusedPort);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        RazerProtocolException exception = await Assert.ThrowsAsync<RazerProtocolException>(
            () => device.ReadPerformanceFanStateAsync(default));

        Assert.Contains("PayloadValidator", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, failedPort.DisposeCount);
        Assert.Equal(0, unusedPort.DisposeCount);
    }

    [Fact]
    public async Task ReadCustomPerformance_DoesNotQueryUnmanagedGpuClockOffsets()
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
        Assert.Equal(0, gpu.QueryCount);
        Assert.Null(state.GpuClockOffsets);
        Assert.Null(state.CustomPerformance?.GpuClockOffsets);
    }

    [Fact]
    public async Task ReadBalancedPerformance_DoesNotWakeGpuForClockOffsetQuery()
    {
        DeviceCapabilities capabilities = WritableGpuCapabilities(
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
    public async Task InactiveGpu_ReadAfterManagedWriteUsesConfirmedOffsetsWithoutQuery()
    {
        DeviceCapabilities capabilities = WritableGpuCapabilities(
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
        var activity = new FixedGpuActivitySource(IsActive: false);
        using var provider = ReadyProvider(
            new FakeProductionFactory(capabilities, port),
            gpu,
            activity);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        var requested = new GpuClockOffsetSettings(125, 500);

        await device.SetGpuClockOffsetsAsync(requested, default);
        ConfirmedDeviceState state = await device.ReadStateAsync(default);

        Assert.Equal(1, gpu.QueryCount);
        Assert.Equal([requested], gpu.Writes);
        Assert.Equal(PerformancePreset.Balanced, state.Performance);
        Assert.Equal(requested, state.GpuClockOffsets?.Current);
        Assert.Equal(
            new GpuClockOffsetRange(-1000, 400, requested.CoreMegahertz),
            state.GpuClockOffsets?.Core);
        Assert.Equal(
            new GpuClockOffsetRange(-1000, 3000, requested.MemoryMegahertz),
            state.GpuClockOffsets?.Memory);
        Assert.Null(state.CustomPerformance);
    }

    [Fact]
    public async Task ActiveGpu_ReadAfterManagedWriteRefreshesConfirmedOffsets()
    {
        DeviceCapabilities capabilities = WritableGpuCapabilities(
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
        var activity = new FixedGpuActivitySource(IsActive: true);
        using var provider = ReadyProvider(
            new FakeProductionFactory(capabilities, port),
            gpu,
            activity);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        var requested = new GpuClockOffsetSettings(125, 500);

        await device.SetGpuClockOffsetsAsync(requested, default);
        gpu.SetCurrent(new GpuClockOffsetSettings(100, 400));
        ConfirmedDeviceState state = await device.ReadStateAsync(default);

        Assert.Equal(2, gpu.QueryCount);
        Assert.Equal(new GpuClockOffsetSettings(100, 400), state.GpuClockOffsets?.Current);
    }

    [Fact]
    public async Task ActiveGpu_UnavailableQueryPreservesConfirmedOffsets()
    {
        DeviceCapabilities capabilities = WritableGpuCapabilities(
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
        var activity = new FixedGpuActivitySource(IsActive: true);
        using var provider = ReadyProvider(
            new FakeProductionFactory(capabilities, port),
            gpu,
            activity);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        var requested = new GpuClockOffsetSettings(125, 500);

        await device.SetGpuClockOffsetsAsync(requested, default);
        gpu.Capabilities = null;
        ConfirmedDeviceState state = await device.ReadStateAsync(default);

        Assert.Equal(2, gpu.QueryCount);
        Assert.Equal(requested, state.GpuClockOffsets?.Current);
        Assert.Equal(
            GpuClockOffsetManagementState.Managed,
            ((IGpuClockOffsetManagementDevice)device).GpuClockOffsetManagementState);
    }

    [Fact]
    public async Task ActiveGpu_OutOfPolicyLiveReadClearsConfirmedOffsets()
    {
        DeviceCapabilities capabilities = WritableGpuCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Balanced },
            new HashSet<FanMode> { FanMode.Automatic });
        FakeProductionPort[] ports = Enumerable.Range(0, 2)
            .Select(_ => new FakeProductionPort(
                capabilities,
                BatteryValidationSetting.Limit80,
                temporary: null,
                performanceFan: new ProductionPerformanceFanState(
                    PerformancePreset.Balanced,
                    FanMode.Automatic)))
            .ToArray();
        var gpu = new FakeGpuClockOffsetController();
        var activity = new FixedGpuActivitySource(IsActive: true);
        using var provider = ReadyProvider(
            new FakeProductionFactory(capabilities, ports),
            gpu,
            activity);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        await device.SetGpuClockOffsetsAsync(
            new GpuClockOffsetSettings(125, 500),
            default);
        gpu.SetCurrent(new GpuClockOffsetSettings(500, 500));
        ConfirmedDeviceState active = await device.ReadStateAsync(default);
        activity.Active = false;
        ConfirmedDeviceState inactive = await device.ReadStateAsync(default);

        Assert.Equal(2, gpu.QueryCount);
        Assert.Null(active.GpuClockOffsets);
        Assert.Null(inactive.GpuClockOffsets);
        Assert.Equal(
            GpuClockOffsetManagementState.Managed,
            ((IGpuClockOffsetManagementDevice)device).GpuClockOffsetManagementState);
    }

    [Fact]
    public async Task ResumeInvalidation_DropsStaleConfirmationAndRefreshesWhenGpuActivates()
    {
        DeviceCapabilities capabilities = WritableGpuCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Balanced },
            new HashSet<FanMode> { FanMode.Automatic });
        var firstReadPort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null,
            performanceFan: new ProductionPerformanceFanState(
                PerformancePreset.Balanced,
                FanMode.Automatic));
        var secondReadPort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null,
            performanceFan: new ProductionPerformanceFanState(
                PerformancePreset.Balanced,
                FanMode.Automatic));
        var gpu = new FakeGpuClockOffsetController();
        var activity = new FixedGpuActivitySource(IsActive: false);
        using var provider = ReadyProvider(
            new FakeProductionFactory(capabilities, firstReadPort, secondReadPort),
            gpu,
            activity);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        IGpuClockOffsetConfirmationInvalidator invalidator = provider;
        var requested = new GpuClockOffsetSettings(125, 500);

        await device.SetGpuClockOffsetsAsync(requested, default);
        await invalidator.InvalidateGpuClockOffsetConfirmationAsync(default);
        gpu.SetCurrent(GpuClockOffsetSettings.Default);

        ConfirmedDeviceState inactive = await device.ReadStateAsync(default);
        activity.Active = true;
        ConfirmedDeviceState active = await device.ReadStateAsync(default);

        Assert.Equal(2, gpu.QueryCount);
        Assert.Null(inactive.GpuClockOffsets);
        Assert.Equal(GpuClockOffsetSettings.Default, active.GpuClockOffsets?.Current);
    }

    [Fact]
    public async Task RelinquishedOffsets_AreNotQueriedEvenWhenGpuIsActive()
    {
        DeviceCapabilities capabilities = WritableGpuCapabilities(
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
        var activity = new FixedGpuActivitySource(IsActive: true);
        using var provider = ReadyProvider(
            new FakeProductionFactory(capabilities, port),
            gpu,
            activity);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        IGpuClockOffsetManagementDevice managedDevice =
            Assert.IsAssignableFrom<IGpuClockOffsetManagementDevice>(device);
        var requested = new GpuClockOffsetSettings(125, 500);

        await device.SetGpuClockOffsetsAsync(requested, default);
        await managedDevice.RelinquishGpuClockOffsetManagementAsync(default);
        await ((IGpuClockOffsetConfirmationInvalidator)provider)
            .InvalidateGpuClockOffsetConfirmationAsync(default);
        ConfirmedDeviceState state = await device.ReadStateAsync(default);

        Assert.Equal(1, gpu.QueryCount);
        Assert.Equal([requested], gpu.Writes);
        Assert.Null(state.GpuClockOffsets);
    }

    [Fact]
    public void Dispose_ReleasesGpuActivitySource()
    {
        DeviceCapabilities capabilities = WritableGpuCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Balanced },
            new HashSet<FanMode> { FanMode.Automatic });
        var activity = new FixedGpuActivitySource(IsActive: false);
        WindowsVerifiedBladeDeviceProvider provider = ReadyProvider(
            new FakeProductionFactory(capabilities),
            new FakeGpuClockOffsetController(),
            activity);

        provider.Dispose();

        Assert.True(activity.Disposed);
    }

    [Fact]
    public async Task SetCustomPerformance_PreflightsThenAppliesFirmwareAndGpuOffsets()
    {
        DeviceCapabilities capabilities = WritableGpuCapabilities(
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
        Assert.Equal(
            requested with { GpuClockOffsets = null },
            port.LastCustomPerformance);
        Assert.Equal([requested.GpuClockOffsets], gpu.Writes);
    }

    [Fact]
    public async Task SetCustomPerformance_UnavailableGpuTuningRejectsBeforeFirmwareWrite()
    {
        DeviceCapabilities capabilities = WritableGpuCapabilities(
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

        CapabilityUnavailableException error =
            await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
                device.SetPerformanceAsync(
                    PerformancePreset.Custom,
                    requested,
                    default));

        Assert.Contains("unavailable", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, port.SetCustomPerformanceCount);
        Assert.Empty(gpu.Writes);
    }

    [Fact]
    public async Task ExactModelObservedOnlyGpuPolicy_BlocksOffsetWritesBeforeFirmwareWrite()
    {
        DeviceCapabilities capabilities = ThermalCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Custom },
            new HashSet<FanMode> { FanMode.Automatic }) with
        {
            GpuOverclock = new(
                new DeviceNumericRange(-1000, 400, null),
                new DeviceNumericRange(-1000, 3000, null),
                DeviceFeatureAccess.ObservedOnly),
        };
        var port = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var factory = new FakeProductionFactory(capabilities, port);
        var gpu = new FakeGpuClockOffsetController();
        using var provider = ReadyProvider(factory, gpu);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        var requested = new CustomPerformanceSettings(
            CustomPerformanceLevel.Medium,
            CustomPerformanceLevel.High)
        {
            GpuClockOffsets = new GpuClockOffsetSettings(125, 500),
        };

        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            device.SetPerformanceAsync(
                PerformancePreset.Custom,
                requested,
                default));

        Assert.Equal(0, factory.OpenCount);
        Assert.Equal(0, gpu.QueryCount);
        Assert.Empty(gpu.Writes);
    }

    [Fact]
    public async Task ExactModelCombinationPolicy_BlocksUnsupportedPairBeforeOpeningDevice()
    {
        DeviceCapabilities capabilities = ThermalCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Custom },
            new HashSet<FanMode> { FanMode.Automatic }) with
        {
            CustomPerformance = new(
                new HashSet<CustomPerformanceCombination>
                {
                    new(CustomPerformanceLevel.High, CustomPerformanceLevel.Low),
                }),
        };
        var factory = new FakeProductionFactory(capabilities);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            device.SetPerformanceAsync(
                PerformancePreset.Custom,
                new CustomPerformanceSettings(
                    CustomPerformanceLevel.High,
                    CustomPerformanceLevel.Medium),
                default));

        Assert.Equal(0, factory.OpenCount);
    }

    [Fact]
    public async Task SetCustomPerformance_GpuWriteFailureReportsPartialOperationAsIndeterminate()
    {
        DeviceCapabilities capabilities = WritableGpuCapabilities(
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
            "power mode was applied",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetBalancedPerformance_CompletesGpuWriteAfterCancellationRacesWithFirmware()
    {
        DeviceCapabilities capabilities = WritableGpuCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Balanced },
            new HashSet<FanMode> { FanMode.Automatic });
        using var cancellation = new CancellationTokenSource();
        var port = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null)
        {
            AfterPerformanceWrite = cancellation.Cancel,
        };
        var gpu = new FakeGpuClockOffsetController();
        var requested = new GpuClockOffsetSettings(125, 500);
        using var provider = ReadyProvider(
            new FakeProductionFactory(capabilities, port),
            gpu);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        await device.SetPerformanceAsync(
            PerformancePreset.Balanced,
            customPerformance: null,
            requested,
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(1, port.SetPerformanceCount);
        Assert.Equal([requested], gpu.Writes);
    }

    [Fact]
    public async Task SetBalancedPerformance_GpuFailureAfterFirmwareIsIndeterminate()
    {
        DeviceCapabilities capabilities = WritableGpuCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Balanced },
            new HashSet<FanMode> { FanMode.Automatic });
        var port = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var gpu = new FakeGpuClockOffsetController { FailWrites = true };
        var requested = new GpuClockOffsetSettings(125, 500);
        using var provider = ReadyProvider(
            new FakeProductionFactory(capabilities, port),
            gpu);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(() =>
            device.SetPerformanceAsync(
                PerformancePreset.Balanced,
                customPerformance: null,
                requested,
                default));

        Assert.Equal(1, port.SetPerformanceCount);
        Assert.Equal(WriteSubmissionState.Indeterminate, error.SubmissionState);
    }

    [Fact]
    public async Task SetBalancedPerformance_UnavailableGpuTuningRejectsBeforeFirmwareWrite()
    {
        DeviceCapabilities capabilities = ThermalCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Balanced },
            new HashSet<FanMode> { FanMode.Automatic });
        var port = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var gpu = new FakeGpuClockOffsetController { Capabilities = null };
        using var provider = ReadyProvider(
            new FakeProductionFactory(capabilities, port),
            gpu);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            device.SetPerformanceAsync(
                PerformancePreset.Balanced,
                customPerformance: null,
                GpuClockOffsetSettings.Default,
                default));

        Assert.Equal(0, port.SetPerformanceCount);
        Assert.Empty(gpu.Writes);
    }

    [Fact]
    public async Task SetGpuClockOffsets_EnabledDefaultOffsetsRemainManaged()
    {
        DeviceCapabilities capabilities = ThermalCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Balanced },
            new HashSet<FanMode> { FanMode.Automatic }) with
        {
            GpuOverclock = new(
                new DeviceNumericRange(-100, 400, 5),
                new DeviceNumericRange(-200, 1000, 5),
                DeviceFeatureAccess.ReadWrite),
        };
        var gpu = new FakeGpuClockOffsetController
        {
            Capabilities = new GpuClockOffsetCapabilities(
                "Test NVIDIA GPU",
                new GpuClockOffsetRange(-1000, 1000, 500),
                new GpuClockOffsetRange(-2000, 6000, 1500)),
        };
        var port = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null,
            performanceFan: new ProductionPerformanceFanState(
                PerformancePreset.Balanced,
                FanMode.Automatic));
        using var provider = ReadyProvider(
            new FakeProductionFactory(capabilities, port),
            gpu);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        await device.SetGpuClockOffsetsAsync(GpuClockOffsetSettings.Default, default);
        ConfirmedDeviceState state = await device.ReadStateAsync(default);

        Assert.Equal([GpuClockOffsetSettings.Default], gpu.Writes);
        Assert.Equal(1, gpu.QueryCount);
        Assert.Equal(GpuClockOffsetSettings.Default, state.GpuClockOffsets?.Current);
    }

    [Fact]
    public async Task SetGpuClockOffsets_DisabledResetRelinquishesAfterConfirmation()
    {
        DeviceCapabilities capabilities = WritableGpuCapabilities(
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
        IGpuClockOffsetManagementDevice managedDevice =
            Assert.IsAssignableFrom<IGpuClockOffsetManagementDevice>(device);
        var requested = new GpuClockOffsetSettings(125, 500);

        await device.SetGpuClockOffsetsAsync(requested, default);
        await managedDevice.SetGpuClockOffsetsAsync(
            GpuClockOffsetSettings.Default,
            retainManagement: false,
            default);
        ConfirmedDeviceState state = await device.ReadStateAsync(default);

        Assert.Equal(2, gpu.QueryCount);
        Assert.Equal([requested, GpuClockOffsetSettings.Default], gpu.Writes);
        Assert.Null(state.GpuClockOffsets);
    }

    [Fact]
    public async Task SetPerformance_DisabledResetRelinquishesAfterCombinedWrite()
    {
        DeviceCapabilities capabilities = WritableGpuCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Balanced },
            new HashSet<FanMode> { FanMode.Automatic });
        var port = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null,
            performanceFan: new ProductionPerformanceFanState(
                PerformancePreset.Silent,
                FanMode.Automatic));
        var statePort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null,
            performanceFan: new ProductionPerformanceFanState(
                PerformancePreset.Balanced,
                FanMode.Automatic));
        var gpu = new FakeGpuClockOffsetController();
        using var provider = ReadyProvider(
            new FakeProductionFactory(capabilities, port, statePort),
            gpu);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        IGpuClockOffsetManagementDevice managedDevice =
            Assert.IsAssignableFrom<IGpuClockOffsetManagementDevice>(device);

        await managedDevice.SetPerformanceAsync(
            PerformancePreset.Balanced,
            customPerformance: null,
            GpuClockOffsetSettings.Default,
            retainGpuClockOffsetManagement: false,
            default);
        ConfirmedDeviceState state = await device.ReadStateAsync(default);

        Assert.Equal(1, port.SetPerformanceCount);
        Assert.Equal(1, gpu.QueryCount);
        Assert.Equal([GpuClockOffsetSettings.Default], gpu.Writes);
        Assert.Null(state.GpuClockOffsets);
    }

    [Fact]
    public async Task DisabledUnknownOffsets_ResetOnceThenRemainWakeFree()
    {
        DeviceCapabilities capabilities = WritableGpuCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Balanced },
            new HashSet<FanMode> { FanMode.Automatic });
        FakeProductionPort[] ports = Enumerable.Range(0, 5)
            .Select(_ => new FakeProductionPort(
                capabilities,
                BatteryValidationSetting.Limit80,
                temporary: null,
                performanceFan: new ProductionPerformanceFanState(
                    PerformancePreset.Balanced,
                    FanMode.Automatic)))
            .ToArray();
        var gpu = new FakeGpuClockOffsetController();
        gpu.SetCurrent(new GpuClockOffsetSettings(125, 500));
        using var provider = ReadyProvider(
            new FakeProductionFactory(capabilities, ports),
            gpu);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        var coordinator = new ControlCoordinator(device);

        ScopedApplyOutcome first = await coordinator.ApplyPerformanceOutcomeAsync(
            PerformancePreset.Balanced,
            customPerformance: null,
            GpuClockTuningSettings.Disabled,
            default);
        ScopedApplyOutcome second = await coordinator.ApplyPerformanceOutcomeAsync(
            PerformancePreset.Balanced,
            customPerformance: null,
            GpuClockTuningSettings.Disabled,
            default);

        Assert.Equal(ControlResultStatus.Applied, first.Result.Status);
        Assert.Equal(ControlResultStatus.Unchanged, second.Result.Status);
        Assert.Equal(1, gpu.QueryCount);
        Assert.Equal([GpuClockOffsetSettings.Default], gpu.Writes);
        Assert.Equal(
            GpuClockOffsetManagementState.ResetConfirmed,
            ((IGpuClockOffsetManagementDevice)device).GpuClockOffsetManagementState);
    }

    [Fact]
    public async Task ConfirmedDefaultReset_SkipsNvmlAcrossPresetAndProfileTransitions()
    {
        DeviceCapabilities capabilities = WritableGpuCapabilities(
            new HashSet<PerformancePreset>
            {
                PerformancePreset.Balanced,
                PerformancePreset.Performance,
            },
            new HashSet<FanMode> { FanMode.Automatic });
        var performancePort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var balancedPort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var gpu = new FakeGpuClockOffsetController();
        using var provider = ReadyProvider(
            new FakeProductionFactory(capabilities, performancePort, balancedPort),
            gpu);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        IGpuClockOffsetManagementDevice managedDevice =
            Assert.IsAssignableFrom<IGpuClockOffsetManagementDevice>(device);

        await managedDevice.SetGpuClockOffsetsAsync(
            GpuClockOffsetSettings.Default,
            retainManagement: false,
            default);
        await managedDevice.SetPerformanceAsync(
            PerformancePreset.Performance,
            customPerformance: null,
            GpuClockOffsetSettings.Default,
            retainGpuClockOffsetManagement: false,
            default);
        await managedDevice.RelinquishGpuClockOffsetManagementAsync(default);
        await managedDevice.RelinquishGpuClockOffsetManagementAsync(default);
        await managedDevice.SetPerformanceAsync(
            PerformancePreset.Balanced,
            customPerformance: null,
            GpuClockOffsetSettings.Default,
            retainGpuClockOffsetManagement: false,
            default);

        Assert.Equal(1, gpu.QueryCount);
        Assert.Equal([GpuClockOffsetSettings.Default], gpu.Writes);
        Assert.Equal(1, performancePort.SetPerformanceCount);
        Assert.Equal(1, balancedPort.SetPerformanceCount);
        Assert.Equal(
            GpuClockOffsetManagementState.ResetConfirmed,
            managedDevice.GpuClockOffsetManagementState);
    }

    [Fact]
    public async Task SetGpuClockOffsets_StepLessReadWritePolicyAdmitsInRangeWrite()
    {
        DeviceCapabilities capabilities = ThermalCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Balanced },
            new HashSet<FanMode> { FanMode.Automatic }) with
        {
            GpuOverclock = new(
                new DeviceNumericRange(-1000, 400, null),
                new DeviceNumericRange(-1000, 3000, null),
                DeviceFeatureAccess.ReadWrite),
        };
        var gpu = new FakeGpuClockOffsetController();
        var requested = new GpuClockOffsetSettings(125, 500);
        using var provider = ReadyProvider(
            new FakeProductionFactory(capabilities),
            gpu);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        await device.SetGpuClockOffsetsAsync(requested, default);

        Assert.Equal(1, gpu.QueryCount);
        Assert.Equal([requested], gpu.Writes);
    }

    [Fact]
    public async Task SetGpuClockOffsets_PreflightsAndWritesWithoutOpeningFirmwarePort()
    {
        DeviceCapabilities capabilities = WritableGpuCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Custom },
            new HashSet<FanMode> { FanMode.Automatic });
        var factory = new FakeProductionFactory(capabilities);
        var gpu = new FakeGpuClockOffsetController();
        var requested = new GpuClockOffsetSettings(5, 0);
        using var provider = ReadyProvider(factory, gpu);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        await device.SetGpuClockOffsetsAsync(requested, default);

        Assert.Equal(1, gpu.QueryCount);
        Assert.Equal([requested], gpu.Writes);
        Assert.Equal(0, factory.OpenCount);
    }

    [Fact]
    public async Task SetGpuClockOffsets_NullModelPolicyRejectsWithoutDriverQuery()
    {
        DeviceCapabilities capabilities = ThermalCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Balanced },
            new HashSet<FanMode> { FanMode.Automatic });
        var factory = new FakeProductionFactory(capabilities);
        var gpu = new FakeGpuClockOffsetController();
        using var provider = ReadyProvider(factory, gpu);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        await Assert.ThrowsAsync<CapabilityUnavailableException>(() =>
            device.SetGpuClockOffsetsAsync(new GpuClockOffsetSettings(5, 0), default));

        Assert.Equal(0, gpu.QueryCount);
        Assert.Empty(gpu.Writes);
        Assert.Equal(0, factory.OpenCount);
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

    [Fact]
    public async Task ReadRz090581CapabilityDiagnostics_UsesFreshIdentityGatedReadOnlyPort()
    {
        DeviceCapabilities capabilities = DeviceCapabilities.Unavailable();
        var expected = new Rz090581CapabilityDiagnostics(0x01, 0x00, [0x01, 0x02]);
        var port = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null,
            rz090581CapabilityDiagnostics: expected);
        var factory = new FakeProductionFactory(capabilities, port);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        Rz090581CapabilityDiagnostics? actual =
            await device.ReadRz090581CapabilityDiagnosticsAsync(default);

        Assert.Same(expected, actual);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, port.Rz090581CapabilityDiagnosticsQueryCount);
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
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransientReadFailureRetriesWithFreshVerifiedSessionAndRecovers(
        bool timeout)
    {
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        var failed = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null)
        {
            ProtectionFailure = timeout
                ? new TimeoutException("temporary timeout")
                : new IOException("temporary native I/O failure"),
        };
        var recovered = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var factory = new FakeProductionFactory(capabilities, failed, recovered);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        ConfirmedDeviceState state = await device.ReadStateAsync(default);

        Assert.Equal(new ConfirmedBatteryState(true, 80, null), state.Battery);
        Assert.Equal(2, factory.OpenCount);
        Assert.Equal(1, failed.DisposeCount);
        Assert.Equal(1, recovered.DisposeCount);
    }

    [Fact]
    public async Task RepeatedTransientReadFailureStopsAfterTwoFreshSessionRetries()
    {
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        FakeProductionPort[] failed = Enumerable.Range(0, 3)
            .Select(_ => new FakeProductionPort(
                capabilities,
                BatteryValidationSetting.Limit80,
                temporary: null)
            {
                ProtectionFailure = new IOException("temporary native I/O failure"),
            })
            .ToArray();
        var neverOpened = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var factory = new FakeProductionFactory(
            capabilities,
            [.. failed, neverOpened]);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        IOException error = await Assert.ThrowsAsync<IOException>(
            () => device.ReadStateAsync(default));

        Assert.Equal("Battery state query failed.", error.Message);
        Assert.Equal(3, factory.OpenCount);
        Assert.All(failed, port => Assert.Equal(1, port.DisposeCount));
        Assert.Equal(0, neverOpened.ProtectionQueryCount);
    }

    [Fact]
    public async Task IdentityBlockedReadDoesNotRetry()
    {
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        var neverOpened = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var factory = new IdentityThenPortFactory(capabilities, neverOpened);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        IOException error = await Assert.ThrowsAsync<IOException>(
            () => device.ReadStateAsync(default));

        Assert.Equal(
            "Battery state query was blocked by exact device identity verification.",
            error.Message);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(0, neverOpened.ProtectionQueryCount);
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
    public async Task RelinquishWaitsForPairedGpuWriteAndWinsManagementOrdering()
    {
        DeviceCapabilities capabilities = WritableGpuCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Balanced },
            new HashSet<FanMode> { FanMode.Automatic }) with
        {
            ChargeLimits = new HashSet<int> { 80 },
        };
        var blockedWrite = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null,
            blockPerformanceWrite: true);
        using var provider = ReadyProvider(
            new FakeProductionFactory(capabilities, blockedWrite),
            new FakeGpuClockOffsetController());
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        IGpuClockOffsetManagementDevice managedDevice =
            Assert.IsAssignableFrom<IGpuClockOffsetManagementDevice>(device);

        Task write = managedDevice.SetPerformanceAsync(
            PerformancePreset.Balanced,
            customPerformance: null,
            new GpuClockOffsetSettings(100, 500),
            retainGpuClockOffsetManagement: true,
            default);
        await blockedWrite.WriteStarted.Task;
        Task relinquish = managedDevice.RelinquishGpuClockOffsetManagementAsync(default);

        Assert.False(relinquish.IsCompleted);

        blockedWrite.ReleaseWrite();
        await write;
        await relinquish;

        Assert.Equal(
            GpuClockOffsetManagementState.Relinquished,
            managedDevice.GpuClockOffsetManagementState);
    }

    [Fact]
    public async Task RelinquishAfterDisposeIsRejected()
    {
        DeviceCapabilities capabilities = WritableGpuCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Balanced },
            new HashSet<FanMode> { FanMode.Automatic });
        var provider = ReadyProvider(new FakeProductionFactory(capabilities));
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        IGpuClockOffsetManagementDevice managedDevice =
            Assert.IsAssignableFrom<IGpuClockOffsetManagementDevice>(device);
        provider.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => managedDevice.RelinquishGpuClockOffsetManagementAsync(default));
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
    public async Task KnownNotAppliedPortFailureRetainsSubmissionClassification()
    {
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        var portFailure = new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            "port rejected write");
        var port = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null)
        {
            WriteFailure = portFailure,
        };
        var factory = new FakeProductionFactory(capabilities, port);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.SetChargeLimitAsync(BatteryPolicy.Create(80), default));

        Assert.Same(portFailure, error);
        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Equal("port rejected write", error.Message);
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task ThermalPreWriteReadFailureRetriesOneFreshVerifiedSession()
    {
        DeviceCapabilities capabilities = ThermalCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Balanced },
            new HashSet<FanMode> { FanMode.Automatic });
        var firstPort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null)
        {
            WriteFailure = ThermalPreWriteReadFailure(),
        };
        var secondPort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var factory = new FakeProductionFactory(capabilities, firstPort, secondPort);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        await device.SetPerformanceAsync(PerformancePreset.Balanced, default);

        Assert.Equal(2, factory.OpenCount);
        Assert.Equal(1, firstPort.SetPerformanceCount);
        Assert.Equal(1, secondPort.SetPerformanceCount);
        Assert.Equal(1, firstPort.DisposeCount);
        Assert.Equal(1, secondPort.DisposeCount);
    }

    [Fact]
    public async Task RepeatedThermalPreWriteReadFailureStopsAfterFreshSession()
    {
        DeviceCapabilities capabilities = ThermalCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Balanced },
            new HashSet<FanMode> { FanMode.Automatic });
        var firstPort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null)
        {
            WriteFailure = ThermalPreWriteReadFailure(),
        };
        BladeWriteException secondFailure = ThermalPreWriteReadFailure();
        var secondPort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null)
        {
            WriteFailure = secondFailure,
        };
        var factory = new FakeProductionFactory(capabilities, firstPort, secondPort);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.SetPerformanceAsync(PerformancePreset.Balanced, default));

        Assert.Same(secondFailure, error);
        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Equal(BladeWriteFailurePhase.ThermalPreWriteRead, error.FailurePhase);
        Assert.Equal(2, factory.OpenCount);
        Assert.Equal(1, firstPort.SetPerformanceCount);
        Assert.Equal(1, secondPort.SetPerformanceCount);
    }

    [Fact]
    public async Task HyperBoostTransientAdapterPreflightRetriesOneFreshVerifiedSession()
    {
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        var firstPort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null)
        {
            WriteFailure = HyperBoostAdapterPreflightFailure(),
        };
        var secondPort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var factory = new FakeProductionFactory(
            capabilities,
            firstPort,
            secondPort);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        await device.SetHyperBoostAsync(
            enabled: true,
            PerformancePreset.Balanced,
            default);

        Assert.Equal(2, factory.OpenCount);
        Assert.Equal(1, firstPort.SetHyperBoostCount);
        Assert.Equal(1, secondPort.SetHyperBoostCount);
        Assert.Equal(1, firstPort.DisposeCount);
        Assert.Equal(1, secondPort.DisposeCount);
    }

    [Fact]
    public async Task HyperBoostRepeatedAdapterPreflightFailureStopsAfterFreshSession()
    {
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        var firstPort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null)
        {
            WriteFailure = HyperBoostAdapterPreflightFailure(),
        };
        var secondFailure = HyperBoostAdapterPreflightFailure();
        var secondPort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null)
        {
            WriteFailure = secondFailure,
        };
        var factory = new FakeProductionFactory(
            capabilities,
            firstPort,
            secondPort);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.SetHyperBoostAsync(
                enabled: true,
                PerformancePreset.Balanced,
                default));

        Assert.Same(secondFailure, error);
        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Equal(2, factory.OpenCount);
        Assert.Equal(1, firstPort.SetHyperBoostCount);
        Assert.Equal(1, secondPort.SetHyperBoostCount);
    }

    [Fact]
    public async Task HyperBoostCanceledAfterAdapterPreflightDoesNotOpenFreshSession()
    {
        using var cancellation = new CancellationTokenSource();
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        var firstPort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null)
        {
            WriteFailure = HyperBoostAdapterPreflightFailure(),
            BeforeHyperBoostFailure = cancellation.Cancel,
        };
        var unusedPort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var factory = new FakeProductionFactory(
            capabilities,
            firstPort,
            unusedPort);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.SetHyperBoostAsync(
                enabled: true,
                PerformancePreset.Balanced,
                cancellation.Token));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Contains("canceled", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, firstPort.SetHyperBoostCount);
        Assert.Equal(0, unusedPort.SetHyperBoostCount);
    }

    [Fact]
    public async Task HyperBoostCanceledAfterFreshSessionOpenDoesNotInvokeSetter()
    {
        using var cancellation = new CancellationTokenSource();
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        var firstPort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null)
        {
            WriteFailure = HyperBoostAdapterPreflightFailure(),
        };
        var unusedPort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var factory = new FakeProductionFactory(
            capabilities,
            firstPort,
            unusedPort)
        {
            AfterOpen = openCount =>
            {
                if (openCount == 2)
                {
                    cancellation.Cancel();
                }
            },
        };
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.SetHyperBoostAsync(
                enabled: true,
                PerformancePreset.Balanced,
                cancellation.Token));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Contains("canceled", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, factory.OpenCount);
        Assert.Equal(1, firstPort.SetHyperBoostCount);
        Assert.Equal(0, unusedPort.SetHyperBoostCount);
        Assert.Equal(1, firstPort.DisposeCount);
        Assert.Equal(1, unusedPort.DisposeCount);
    }

    [Fact]
    public async Task HyperBoostKnownNotAppliedFreshSetterRetainsSubmissionClassification()
    {
        using var cancellation = new CancellationTokenSource();
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        var firstPort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null)
        {
            WriteFailure = HyperBoostAdapterPreflightFailure(),
        };
        var setterFailure = new BladeWriteException(
            WriteSubmissionState.KnownNotApplied,
            "Injected setter refusal after adapter preflight.");
        var secondPort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null)
        {
            WriteFailure = setterFailure,
            BeforeHyperBoostFailure = cancellation.Cancel,
        };
        var factory = new FakeProductionFactory(
            capabilities,
            firstPort,
            secondPort);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.SetHyperBoostAsync(
                enabled: true,
                PerformancePreset.Balanced,
                cancellation.Token));

        Assert.Same(setterFailure, error);
        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Equal("Injected setter refusal after adapter preflight.", error.Message);
        Assert.Equal(2, factory.OpenCount);
        Assert.Equal(1, firstPort.SetHyperBoostCount);
        Assert.Equal(1, secondPort.SetHyperBoostCount);
        Assert.Equal(1, firstPort.DisposeCount);
        Assert.Equal(1, secondPort.DisposeCount);
    }

    [Fact]
    public async Task HyperBoostIndeterminatePortFailureIsNeverRetried()
    {
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        var firstFailure = new BladeWriteException(
            WriteSubmissionState.Indeterminate,
            BladeWriteFailurePhase.ThermalFinalConfirmationRead,
            "ambiguous HyperBoost submission");
        var firstPort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null)
        {
            WriteFailure = firstFailure,
        };
        var unusedPort = new FakeProductionPort(
            capabilities,
            BatteryValidationSetting.Limit80,
            temporary: null);
        var factory = new FakeProductionFactory(
            capabilities,
            firstPort,
            unusedPort);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.SetHyperBoostAsync(
                enabled: true,
                PerformancePreset.Balanced,
                default));

        Assert.Same(firstFailure, error);
        Assert.Equal(WriteSubmissionState.Indeterminate, error.SubmissionState);
        Assert.Equal(
            BladeWriteFailurePhase.ThermalFinalConfirmationRead,
            error.FailurePhase);
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, firstPort.SetHyperBoostCount);
        Assert.Equal(0, unusedPort.SetHyperBoostCount);
    }

    [Fact]
    public async Task KnownNotAppliedOpenFailureRetainsSubmissionClassification()
    {
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        var factory = new FailingProductionFactory(
            capabilities,
            HidSessionFailureKind.NativeIoFailure);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        BladeWriteException error = await Assert.ThrowsAsync<BladeWriteException>(
            () => device.SetChargeLimitAsync(BatteryPolicy.Create(80), default));

        Assert.Equal(WriteSubmissionState.KnownNotApplied, error.SubmissionState);
        Assert.Equal("The hardware write was blocked before submission.", error.Message);
        Assert.Null(error.InnerException);
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
        IGpuClockOffsetController? gpuClockOffsets = null,
        IGpuActivitySource? gpuActivitySource = null) => new(
            () => Probe(BatteryValidationProbeClassification.Ready),
            factory,
            new UnavailableFanRpmFactory(),
            gpuClockOffsets: gpuClockOffsets,
            gpuActivitySource: gpuActivitySource);

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

    private static DeviceCapabilities WritableGpuCapabilities(
        IReadOnlySet<PerformancePreset> presets,
        IReadOnlySet<FanMode> fanModes) => ThermalCapabilities(presets, fanModes) with
        {
            GpuOverclock = new(
                new DeviceNumericRange(-1000, 400, null),
                new DeviceNumericRange(-1000, 3000, null),
                DeviceFeatureAccess.ReadWrite),
        };

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

    private static BladeWriteException HyperBoostAdapterPreflightFailure() => new(
        WriteSubmissionState.KnownNotApplied,
        BladeWriteFailurePhase.HyperBoostAdapterEligibilityRead,
        "The HyperBoost adapter preflight read failed before any HyperBoost command was submitted.");

    private static BladeWriteException ThermalPreWriteReadFailure() => new(
        WriteSubmissionState.KnownNotApplied,
        BladeWriteFailurePhase.ThermalPreWriteRead,
        "Injected thermal pre-write read failure.");

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

        public Action<int>? AfterOpen { get; init; }

        public Task<HidSessionResult<IProductionBatteryPort>> OpenAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCount++;
            HidSessionResult<IProductionBatteryPort> result = ports.Count == 0
                ? new HidSessionResult<IProductionBatteryPort>(
                    null,
                    HidSessionFailureKind.NativeIoFailure)
                : new HidSessionResult<IProductionBatteryPort>(ports.Dequeue(), null);
            AfterOpen?.Invoke(OpenCount);
            return Task.FromResult(result);
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
        bool? startupAnimation = null,
        bool blockPerformanceWrite = false,
        Rz090581CapabilityDiagnostics? rz090581CapabilityDiagnostics = null)
        : IProductionBatteryPort
    {
        private readonly TaskCompletionSource? protectionRelease = blockProtectionQuery
            ? new(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
        private readonly TaskCompletionSource? writeRelease =
            blockWrite || blockPerformanceWrite
            ? new(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;

        public DeviceCapabilities Capabilities { get; } = capabilities;
        public bool SupportsTemporaryFullChargeQuery => temporary.HasValue;
        public bool SupportsMcuVersionQuery => mcuVersion is not null;
        public bool SupportsStartupAnimationQuery => startupAnimation.HasValue;
        public bool SupportsRz090581CapabilityDiagnosticsQuery =>
            rz090581CapabilityDiagnostics is not null;
        public BladeWriteException? WriteFailure { get; set; }
        public Exception? ProtectionFailure { get; init; }
        public Exception? TemporaryFailure { get; init; }
        public Exception? PerformanceFanFailure { get; init; }
        public Exception? StartupAnimationFailure { get; init; }
        public Action? AfterPerformanceWrite { get; init; }
        public Action? BeforeHyperBoostFailure { get; init; }
        public TaskCompletionSource ProtectionQueryStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ProtectionQueryCount { get; private set; }
        public int TemporaryQueryCount { get; private set; }
        public int PerformanceFanQueryCount { get; private set; }
        public int McuVersionQueryCount { get; private set; }
        public int StartupAnimationQueryCount { get; private set; }
        public int Rz090581CapabilityDiagnosticsQueryCount { get; private set; }
        public int SetPerformanceCount { get; private set; }
        public int SetCustomPerformanceCount { get; private set; }
        public int SetHyperBoostCount { get; private set; }
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
            return PerformanceFanFailure is null
                ? Task.FromResult(
                    performanceFan ?? new ProductionPerformanceFanState(null, null))
                : Task.FromException<ProductionPerformanceFanState>(
                    PerformanceFanFailure);
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

        public Task<Rz090581CapabilityDiagnostics?>
            QueryRz090581CapabilityDiagnosticsAsync(
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Rz090581CapabilityDiagnosticsQueryCount++;
            return Task.FromResult(rz090581CapabilityDiagnostics);
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

        public async Task SetPerformanceAsync(
            PerformancePreset value,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetPerformanceCount++;
            LastPerformance = value;
            AfterPerformanceWrite?.Invoke();
            if (blockPerformanceWrite)
            {
                WriteStarted.TrySetResult();
                await writeRelease!.Task.WaitAsync(cancellationToken);
            }
            if (WriteFailure is not null)
            {
                throw WriteFailure;
            }
        }

        public async Task SetCustomPerformanceAsync(
            CustomPerformanceSettings value,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetCustomPerformanceCount++;
            LastCustomPerformance = value;
            AfterPerformanceWrite?.Invoke();
            if (blockPerformanceWrite)
            {
                WriteStarted.TrySetResult();
                await writeRelease!.Task.WaitAsync(cancellationToken);
            }
            if (WriteFailure is not null)
            {
                throw WriteFailure;
            }
        }

        public Task SetHyperBoostAsync(
            bool enabled,
            PerformancePreset baseMode,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetHyperBoostCount++;
            BeforeHyperBoostFailure?.Invoke();
            return WriteFailure is null
                ? Task.CompletedTask
                : Task.FromException(WriteFailure);
        }

        public Task SetFanModeAsync(FanMode value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetFanModeCount++;
            LastFanMode = value;
            return WriteFailure is null ? Task.CompletedTask : Task.FromException(WriteFailure);
        }

        public Task SetFanControlAsync(
            FanControlSettings value,
            CancellationToken cancellationToken) =>
            SetFanModeAsync(value.Mode, cancellationToken);

        public Task SetFanTargetsAsync(
            int fan1Rpm,
            int fan2Rpm,
            CancellationToken cancellationToken) =>
            WriteFailure is null ? Task.CompletedTask : Task.FromException(WriteFailure);

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

        public Task ApplyOneShotLaptopControlAsync(
            ApplyOneShotLaptopControl value,
            CancellationToken cancellationToken) =>
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

        public GpuClockOffsetCapabilities Set(GpuClockOffsetSettings settings)
        {
            Writes.Add(settings);
            if (FailWrites)
            {
                throw new BladeWriteException(
                    WriteSubmissionState.KnownNotApplied,
                    "NVML write failed.");
            }

            if (Capabilities is { } capabilities)
            {
                SetCurrent(settings);
                return Capabilities!;
            }

            throw new InvalidOperationException("GPU capabilities are unavailable.");
        }

        public void SetCurrent(GpuClockOffsetSettings settings)
        {
            if (Capabilities is not { } capabilities)
            {
                throw new InvalidOperationException("GPU capabilities are unavailable.");
            }

            Capabilities = capabilities with
            {
                Core = capabilities.Core with
                {
                    CurrentMegahertz = settings.CoreMegahertz,
                },
                Memory = capabilities.Memory with
                {
                    CurrentMegahertz = settings.MemoryMegahertz,
                },
            };
        }
    }

    private sealed class FixedGpuActivitySource(bool IsActive)
        : IGpuActivitySource, IDisposable
    {
        public bool Active { get; set; } = IsActive;

        public bool Disposed { get; private set; }

        bool IGpuActivitySource.IsActive() => Active;

        public void Dispose() => Disposed = true;
    }
}
