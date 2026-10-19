using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed partial class WindowsVerifiedBladeDeviceProviderProductionTests
{
    public static TheoryData<int, int> IsolatedGetterFailures
    {
        get
        {
            var data = new TheoryData<int, int>();
            foreach (int getter in Enumerable.Range(0, 3))
            {
                foreach (int failure in Enumerable.Range(0, 6))
                {
                    data.Add(getter, failure);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(IsolatedGetterFailures))]
    public async Task ReadState_ExchangeFailureLeavesOnlyDependentFieldsUnknown(
        int getter,
        int failure)
    {
        DeviceCapabilities capabilities = ThermalCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Balanced },
            new HashSet<FanMode> { FanMode.Automatic });
        Exception injected = GetterExchangeFailure(failure);
        var rejected = new FakeProductionPort(
            capabilities, BatteryValidationSetting.Limit80, temporary: false,
            performanceFan: new(PerformancePreset.Balanced, FanMode.Automatic))
        {
            ProtectionFailure = getter == 0 ? injected : null,
            TemporaryFailure = getter == 1 ? injected : null,
            PerformanceFanFailure = getter == 2 ? injected : null,
        };
        var fresh = new FakeProductionPort(
            capabilities, BatteryValidationSetting.Limit80, temporary: false,
            performanceFan: new(PerformancePreset.Balanced, FanMode.Automatic));
        var factory = new FakeProductionFactory(capabilities, rejected, fresh);
        using var provider = ReadyProvider(factory);
        Assert.True(provider.TryGetVerifiedDevice(out IBladeDevice device));

        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => device.ReadStateAsync(default));
        PartialDeviceStateRead partial = Assert.IsType<PartialDeviceStateRead>(
            DeviceStateReadIsolation.PartialRead(error));

        Assert.Equal(getter == 2, partial.State.Battery is not null);
        Assert.Equal(getter != 2, partial.State.Performance is not null);
        Assert.Equal(getter != 2, partial.State.Fans is not null);
        Assert.Equal(getter != 2, partial.State.HyperBoostActive.HasValue);
        Assert.Equal(getter == 2 ? 1 : 2, factory.OpenCount);
        Assert.Equal(1, rejected.DisposeCount);
        Assert.Equal(getter == 2 ? 0 : 1, fresh.DisposeCount);
        Assert.Equal(0, rejected.SetLimitCount + fresh.SetLimitCount
            + rejected.SetPerformanceCount + fresh.SetPerformanceCount
            + rejected.SetFanModeCount + fresh.SetFanModeCount);
        Assert.DoesNotContain("raw device path", error.Message);
        Assert.Single(partial.Failures);
        Assert.Equal(getter switch
        {
            0 => DeviceStateQuery.BatteryProtection,
            1 => DeviceStateQuery.TemporaryFullCharge,
            _ => DeviceStateQuery.PerformanceAndFans,
        }, partial.Failures[0].Query);
        if (getter == 0)
        {
            Assert.Equal(0, rejected.TemporaryQueryCount + fresh.TemporaryQueryCount);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ReadState_UnretiredNativeReadNeverOpensAnotherSession(int retirementState)
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task retirement = retirementState switch
        {
            1 => Task.FromException(new IOException("native cleanup failed")),
            2 => Task.FromCanceled(new CancellationToken(true)),
            _ => pending.Task,
        };
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        var rejected = new FakeProductionPort(capabilities, BatteryValidationSetting.Limit80, false)
        {
            ProtectionFailure = new NativeDeviceReadException(
                HidSessionFailureKind.NativeIoFailure,
                new(HidSessionFailurePhase.GetCompletion, HidSessionFailureKind.NativeIoFailure, 31, 0),
                null, retirement),
        };
        var factory = new FakeProductionFactory(capabilities, rejected);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => device.ReadStateAsync(default));

        Assert.Null(DeviceStateReadIsolation.PartialRead(error));
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, rejected.DisposeCount);
        Assert.Equal(0, rejected.TemporaryQueryCount + rejected.PerformanceFanQueryCount);
    }

    [Fact]
    public async Task ReadState_DisposalFailureBlocksRecoveryAndFurtherQueries()
    {
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        var rejected = new FakeProductionPort(capabilities, BatteryValidationSetting.Limit80, false)
        {
            ProtectionFailure = GetterExchangeFailure(0),
            DisposeFailure = new IOException("raw device path"),
        };
        var factory = new FakeProductionFactory(capabilities, rejected);
        using var provider = ReadyProvider(factory);
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => device.ReadStateAsync(default));

        Assert.Null(DeviceStateReadIsolation.PartialRead(failure));
        Assert.Equal(1, factory.OpenCount);
        Assert.Equal(1, rejected.DisposeCount);
        Assert.Equal(0, rejected.TemporaryQueryCount + rejected.PerformanceFanQueryCount);
        Assert.DoesNotContain("raw device path", failure.Message);
    }

    [Theory]
    [InlineData(HidSessionFailureKind.NativeIoFailure, HidSessionFailurePhase.Guard)]
    [InlineData(HidSessionFailureKind.Timeout, HidSessionFailurePhase.SharedGateAdmission)]
    [InlineData(HidSessionFailureKind.Poisoned, HidSessionFailurePhase.GetCompletion)]
    [InlineData(HidSessionFailureKind.Exhausted, HidSessionFailurePhase.GetSubmit)]
    [InlineData(HidSessionFailureKind.QueuedCanceled, HidSessionFailurePhase.GetSubmit)]
    public async Task ReadState_AdmissionAndSessionFailuresNeverProducePartialState(
        HidSessionFailureKind kind, HidSessionFailurePhase phase)
    {
        DeviceCapabilities capabilities = BatteryCapabilities(new HashSet<int> { 80 });
        var rejected = new FakeProductionPort(capabilities, BatteryValidationSetting.Limit80, false)
        {
            ProtectionFailure = new NativeDeviceReadException(kind, new(phase, kind, null, null), null),
        };
        using var provider = ReadyProvider(new FakeProductionFactory(capabilities, rejected));
        provider.TryGetVerifiedDevice(out IBladeDevice device);

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => device.ReadStateAsync(default));

        Assert.Null(DeviceStateReadIsolation.PartialRead(failure));
        Assert.Equal(0, rejected.TemporaryQueryCount + rejected.PerformanceFanQueryCount);
        Assert.Equal(0, rejected.SetLimitCount + rejected.SetFanModeCount + rejected.SetPerformanceCount);
    }
    [Fact]
    public async Task ScopedRead_UsesIndependentStateAndBlocksDependentWriteBaseline()
    {
        var state = new ConfirmedDeviceState(PerformancePreset.Balanced, FanMode.Automatic, null, null);
        var partial = new PartialDeviceStateRead(state, false,
            [new(DeviceStateQuery.TemporaryFullCharge,
                new HashSet<DeviceSubsystem> { DeviceSubsystem.Battery }, "Battery unavailable.")]);
        Task<ConfirmedDeviceState> Read(CancellationToken _) => Task.FromException<ConfirmedDeviceState>(
            new PartialDeviceStateReadException(partial));

        Assert.Same(state, await DeviceStateReadIsolation.ReadAsync(Read,
            DeviceStateReadIsolation.DependenciesOf(DeviceSubsystem.Performance), default));
        Exception error = await Assert.ThrowsAnyAsync<Exception>(() => DeviceStateReadIsolation.ReadAsync(
            Read, DeviceStateReadIsolation.Battery, default));
        Assert.Same(partial, DeviceStateReadIsolation.PartialRead(error));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ManagedGpu_OptionalObservationFailurePreservesConfirmedThermalAndOffsets(int source)
    {
        DeviceCapabilities capabilities = WritableGpuCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Balanced },
            new HashSet<FanMode> { FanMode.Automatic });
        var port = new FakeProductionPort(capabilities, BatteryValidationSetting.Limit80, null,
            performanceFan: new(PerformancePreset.Balanced, FanMode.Automatic));
        var gpu = new FakeGpuClockOffsetController();
        var activity = new ControlledGpuActivitySource(_ => source switch
        {
            0 => ValueTask.FromException<bool?>(new IOException("activity unavailable")),
            1 => ValueTask.FromException<bool?>(new TimeoutException("activity timed out")),
            _ => new(true),
        });
        var factory = new FakeProductionFactory(capabilities, port);
        using var provider = ReadyProvider(factory, gpu, activity);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        var requested = new GpuClockOffsetSettings(125, 500);
        await device.SetGpuClockOffsetsAsync(requested, default);
        if (source == 2)
        {
            gpu.QueryFailure = new IOException("offset query unavailable");
        }

        ConfirmedDeviceState state = await device.ReadPerformanceFanStateAsync(default);

        Assert.Equal(PerformancePreset.Balanced, state.Performance);
        Assert.Equal(FanMode.Automatic, state.Fans);
        Assert.Equal(requested, state.GpuClockOffsets?.Current);
        Assert.Equal(1, port.PerformanceFanQueryCount);
        Assert.Equal(1, factory.OpenCount);
    }

    [Fact]
    public async Task ManagedGpu_FailedObservationDoesNotPreventQueuedRelinquishment()
    {
        DeviceCapabilities capabilities = WritableGpuCapabilities(
            new HashSet<PerformancePreset> { PerformancePreset.Balanced },
            new HashSet<FanMode> { FanMode.Automatic });
        var port = new FakeProductionPort(capabilities, BatteryValidationSetting.Limit80, null,
            performanceFan: new(PerformancePreset.Balanced, FanMode.Automatic));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observation = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activity = new ControlledGpuActivitySource(_ =>
        {
            entered.TrySetResult();
            return new(observation.Task);
        });
        using var provider = ReadyProvider(new FakeProductionFactory(capabilities, port),
            new FakeGpuClockOffsetController(), activity);
        provider.TryGetVerifiedDevice(out IBladeDevice device);
        await device.SetGpuClockOffsetsAsync(new GpuClockOffsetSettings(125, 500), default);
        Task<ConfirmedDeviceState> read = device.ReadPerformanceFanStateAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var managed = Assert.IsAssignableFrom<IGpuClockOffsetManagementDevice>(device);
        Task relinquish = managed.RelinquishGpuClockOffsetManagementAsync(default);
        Assert.False(relinquish.IsCompleted);
        observation.SetException(new IOException("activity unavailable"));
        await read.WaitAsync(TimeSpan.FromSeconds(5));
        await relinquish.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(GpuClockOffsetManagementState.Relinquished, managed.GpuClockOffsetManagementState);
        Assert.Equal(1, port.PerformanceFanQueryCount);
    }

    private sealed class ControlledGpuActivitySource(Func<CancellationToken, ValueTask<bool?>> read)
        : IGpuActivitySource
    {
        public ValueTask<bool?> IsActiveAsync(CancellationToken cancellationToken = default) => read(cancellationToken);
    }

    private static Exception GetterExchangeFailure(int failure)
    {
        if (failure < 4)
        {
            HidProtocolValidationFailure validation = failure switch
            {
                0 => HidProtocolValidationFailure.Status,
                1 => HidProtocolValidationFailure.Checksum,
                2 => HidProtocolValidationFailure.PayloadLength,
                _ => HidProtocolValidationFailure.TransactionMismatch,
            };
            return new RazerProtocolException("raw device path", validation)
            {
                Diagnostic = new(HidSessionFailurePhase.ProtocolValidation,
                    HidSessionFailureKind.ProtocolFailure, null, 90)
                {
                    ProtocolValidationFailure = validation,
                },
            };
        }
        var diagnostic = new HidSessionFailureDiagnostic(HidSessionFailurePhase.GetCompletion,
            failure == 4 ? HidSessionFailureKind.NativeIoFailure : HidSessionFailureKind.Timeout, 31, 0);
        return failure == 4
            ? new NativeDeviceReadException(HidSessionFailureKind.NativeIoFailure, diagnostic, null)
            : new NativeDeviceReadTimeoutException(diagnostic, null, Task.CompletedTask);
    }
}
