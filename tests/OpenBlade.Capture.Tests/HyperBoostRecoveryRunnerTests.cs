using OpenBlade.Capture;
using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class HyperBoostRecoveryRunnerTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task QueryAsync_ReturnsOnlyStrictSemanticStates(
        bool hyperBoostActive,
        int expectedValue)
    {
        HyperBoostRecoveryState expected = (HyperBoostRecoveryState)expectedValue;
        var port = new FakePort(State(hyperBoostActive));
        var runner = new HyperBoostRecoveryRunner(new FakeFactory(port));

        HyperBoostRecoveryResult result = await runner.QueryAsync(default);

        Assert.True(result.Success);
        Assert.Equal(expected, result.InitialState);
        Assert.Equal(1, port.QueryCalls);
        Assert.Equal(0, port.SetCalls);
        Assert.Equal(1, port.DisposeCalls);
    }

    [Fact]
    public async Task RestoreBalancedAsync_WritesOnceOnlyFromConfirmedHyperBoost()
    {
        var port = new FakePort(State(hyperBoostActive: true));
        var runner = new HyperBoostRecoveryRunner(new FakeFactory(port));

        HyperBoostRecoveryResult result = await runner.RestoreBalancedAsync(default);

        Assert.True(result.Success);
        Assert.Equal(HyperBoostRecoveryState.HyperBoostActive, result.InitialState);
        Assert.True(result.RestoreAttempted);
        Assert.True(result.BalancedRestorationConfirmed);
        Assert.Equal(1, port.SetCalls);
        Assert.False(port.LastSetEnabled);
        Assert.Equal(1, port.QueryCalls);
    }

    [Fact]
    public async Task RestoreBalancedAsync_AlreadyBalancedPerformsNoWrite()
    {
        var port = new FakePort(State(hyperBoostActive: false));
        var runner = new HyperBoostRecoveryRunner(new FakeFactory(port));

        HyperBoostRecoveryResult result = await runner.RestoreBalancedAsync(default);

        Assert.True(result.Success);
        Assert.False(result.RestoreAttempted);
        Assert.True(result.BalancedRestorationConfirmed);
        Assert.Equal(0, port.SetCalls);
    }

    [Fact]
    public async Task RestoreBalancedAsync_UnknownPairPerformsNoWrite()
    {
        var port = new FakePort(new ProductionPerformanceFanState(
            PerformancePreset.Performance,
            FanMode.Automatic));
        var runner = new HyperBoostRecoveryRunner(new FakeFactory(port));

        HyperBoostRecoveryResult result = await runner.RestoreBalancedAsync(default);

        Assert.False(result.Success);
        Assert.Equal(HyperBoostRecoveryState.Unknown, result.InitialState);
        Assert.Equal(HyperBoostRecoveryFailure.StateNotRecoverable, result.Failure);
        Assert.True(result.ManualRecoveryRequired);
        Assert.Equal(0, port.SetCalls);
    }

    [Theory]
    [InlineData(WriteSubmissionState.KnownNotApplied, false)]
    [InlineData(WriteSubmissionState.Indeterminate, true)]
    public async Task RestoreBalancedAsync_NeverRetriesFailedRestore(
        WriteSubmissionState submissionState,
        bool manualRecoveryRequired)
    {
        var port = new FakePort(State(hyperBoostActive: true))
        {
            SetException = new BladeWriteException(submissionState, "scripted"),
        };
        var runner = new HyperBoostRecoveryRunner(new FakeFactory(port));

        HyperBoostRecoveryResult result = await runner.RestoreBalancedAsync(default);

        Assert.False(result.Success);
        Assert.Equal(1, port.SetCalls);
        Assert.False(result.BalancedRestorationConfirmed);
        Assert.Equal(manualRecoveryRequired, result.ManualRecoveryRequired);
        Assert.Equal(
            submissionState == WriteSubmissionState.Indeterminate
                ? HyperBoostRecoveryFailure.RestoreIndeterminate
                : HyperBoostRecoveryFailure.RestoreKnownNotApplied,
            result.Failure);
    }

    [Fact]
    public async Task RestoreBalancedAsync_ReadOnlyCapabilityPerformsNoQueryOrWrite()
    {
        var port = new FakePort(State(hyperBoostActive: true))
        {
            Capabilities = DeviceCapabilities.SafeTargetDefaults() with
            {
                HyperBoostAccess = DeviceFeatureAccess.ReadOnly,
            },
        };
        var runner = new HyperBoostRecoveryRunner(new FakeFactory(port));

        HyperBoostRecoveryResult result = await runner.RestoreBalancedAsync(default);

        Assert.Equal(HyperBoostRecoveryFailure.CapabilityUnavailable, result.Failure);
        Assert.Equal(0, port.QueryCalls);
        Assert.Equal(0, port.SetCalls);
    }

    [Fact]
    public async Task QueryAdapterPowerStateAsync_ReturnsTypedStateAndDisposes()
    {
        var port = new FakePort(State(hyperBoostActive: false))
        {
            AdapterPowerState = AdapterPowerState.FullPower280W,
        };
        var runner = new HyperBoostRecoveryRunner(new FakeFactory(port));

        AdapterPowerState result = await runner.QueryAdapterPowerStateAsync(default);

        Assert.Equal(AdapterPowerState.FullPower280W, result);
        Assert.Equal(1, port.AdapterQueryCalls);
        Assert.Equal(1, port.DisposeCalls);
    }

    private static ProductionPerformanceFanState State(bool hyperBoostActive) =>
        hyperBoostActive
            ? new ProductionPerformanceFanState(null, FanMode.Automatic)
            {
                HyperBoostActive = true,
            }
            : new ProductionPerformanceFanState(
                PerformancePreset.Balanced,
                FanMode.Automatic);

    private sealed class FakeFactory(IProductionBatteryPort port)
        : IProductionBatteryPortFactory
    {
        public DeviceCapabilities Capabilities => port.Capabilities;

        public Task<HidSessionResult<IProductionBatteryPort>> OpenAsync(
            CancellationToken cancellationToken) => Task.FromResult(
            new HidSessionResult<IProductionBatteryPort>(port, null));
    }

    private sealed class FakePort(ProductionPerformanceFanState state)
        : IProductionBatteryPort
    {
        public DeviceCapabilities Capabilities { get; set; } =
            DeviceCapabilities.SafeTargetDefaults() with
            {
                HyperBoostAccess = DeviceFeatureAccess.ReadWrite,
            };

        public bool SupportsTemporaryFullChargeQuery => false;

        public int QueryCalls { get; private set; }

        public int SetCalls { get; private set; }

        public int DisposeCalls { get; private set; }

        public bool? LastSetEnabled { get; private set; }

        public AdapterPowerState AdapterPowerState { get; init; } =
            AdapterPowerState.Unknown;

        public int AdapterQueryCalls { get; private set; }

        public Exception? SetException { get; init; }

        public Task<ProductionPerformanceFanState> QueryPerformanceFanStateAsync(
            CancellationToken cancellationToken)
        {
            QueryCalls++;
            return Task.FromResult(state);
        }

        public Task SetHyperBoostAsync(bool enabled, CancellationToken cancellationToken)
        {
            SetCalls++;
            LastSetEnabled = enabled;
            return SetException is null
                ? Task.CompletedTask
                : Task.FromException(SetException);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }

        public Task<ConfirmedBatteryState> QueryProtectionAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool?> QueryTemporaryFullChargeAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<AdapterPowerState> QueryAdapterPowerStateAsync(
            CancellationToken cancellationToken)
        {
            AdapterQueryCalls++;
            return Task.FromResult(AdapterPowerState);
        }

        public Task SetPerformanceAsync(
            PerformancePreset value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetCustomPerformanceAsync(
            CustomPerformanceSettings value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetFanModeAsync(
            FanMode value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetFanControlAsync(
            FanControlSettings value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetFanTargetsAsync(
            int fan1Rpm,
            int fan2Rpm,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetChargeLimitAsync(
            int percent,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetProtectionOffAsync(
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetTemporaryFullChargeAsync(
            bool enabled,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ApplyOneShotLaptopControlAsync(
            ApplyOneShotLaptopControl value,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
