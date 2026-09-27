using OpenBlade.Contracts;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class Rz090528GpuModeMatrixCommandTests
{
    [Fact]
    public async Task Run_ConfirmsEveryModeAndRestoresBaselineAfterEachCase()
    {
        var port = new Port();
        var gpu = new Gpu();
        var output = new StringWriter();
        Assert.Equal(0, await Rz090528GpuModeMatrixCommand.RunAsync(port, gpu, gpu, () => true, output));
        Assert.Equal(8, port.Writes.Count);
        Assert.Equal(new[] { PerformancePreset.Balanced, PerformancePreset.Silent, PerformancePreset.Performance, PerformancePreset.Custom }, port.Writes.Where((_, i) => i % 2 == 0));
        Assert.All(port.Writes.Where((_, i) => i % 2 == 1), p => Assert.Equal(PerformancePreset.Performance, p));
        Assert.Equal(8, gpu.Writes);
        Assert.Equal(0, gpu.Current);
        Assert.Contains("MatrixSuccess=True", output.ToString());
    }

    [Theory]
    [InlineData("power")]
    [InlineData("custom")]
    [InlineData("gpu")]
    [InlineData("isolation")]
    public async Task Run_RejectsUnsafeBaselineWithoutMutation(string scenario)
    {
        var port = new Port();
        var gpu = new Gpu();
        if (scenario == "power")
        {
            port.Power = AdapterPowerState.Battery;
        }
        if (scenario == "custom")
        {
            port.State = new(PerformancePreset.Custom, FanMode.Automatic);
        }
        if (scenario == "gpu")
        {
            gpu.Current = 1;
        }
        Assert.Equal(2, await Rz090528GpuModeMatrixCommand.RunAsync(port, gpu, gpu, () => scenario != "isolation", new StringWriter()));
        Assert.Empty(port.Writes);
        Assert.Equal(0, gpu.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Run_FailedModeApplyRestoresOnceAndStops(bool rejectRestore)
    {
        var port = new Port { FailFirstSet = true, RejectRestore = rejectRestore };
        var gpu = new Gpu();
        var output = new StringWriter();
        Assert.Equal(2, await Rz090528GpuModeMatrixCommand.RunAsync(port, gpu, gpu, () => true, output));
        Assert.Equal(2, port.Writes.Count);
        Assert.Equal(0, gpu.Writes);
        Assert.Contains($"MatrixThermalRestored={!rejectRestore}", output.ToString());
    }

    [Fact]
    public async Task Run_GpuFailureRestoresBothOffsetsAndThermalBaselineThenStops()
    {
        var port = new Port();
        var gpu = new Gpu { FailFirstSet = true };
        Assert.Equal(2, await Rz090528GpuModeMatrixCommand.RunAsync(port, gpu, gpu, () => true, new StringWriter()));
        Assert.Equal(2, gpu.Writes);
        Assert.Equal(0, gpu.Current);
        Assert.Equal(2, port.Writes.Count);
        Assert.Equal(PerformancePreset.Performance, port.State.Performance);
    }

    [Fact]
    public async Task Run_ThermalDriftAllowsGuardedZeroRestorationButStopsMatrix()
    {
        var port = new Port();
        var gpu = new Gpu { OnFirstSet = () => port.State = new(PerformancePreset.Silent, FanMode.Automatic) };
        var output = new StringWriter();
        Assert.Equal(2, await Rz090528GpuModeMatrixCommand.RunAsync(port, gpu, gpu, () => true, output));
        Assert.Equal(2, gpu.Writes);
        Assert.Equal(0, gpu.Current);
        Assert.Equal(2, port.Writes.Count);
        Assert.Contains("MatrixModeBeforeGpuRestoreConfirmed=False", output.ToString());
    }

    [Fact]
    public async Task Run_PowerLossBlocksFurtherGpuAndThermalWrites()
    {
        var port = new Port();
        var gpu = new Gpu { OnFirstSet = () => port.Power = AdapterPowerState.Battery };
        var output = new StringWriter();
        Assert.Equal(2, await Rz090528GpuModeMatrixCommand.RunAsync(port, gpu, gpu, () => true, output));
        Assert.Equal(1, gpu.Writes);
        Assert.Single(port.Writes);
        Assert.Contains("MatrixThermalRestored=False", output.ToString());
    }

    [Fact]
    public async Task Run_FinalReconciliationDriftInvalidatesRestorationAndStops()
    {
        var port = new Port { DriftOnReconciliation = true };
        var gpu = new Gpu();
        var output = new StringWriter();
        Assert.Equal(2, await Rz090528GpuModeMatrixCommand.RunAsync(port, gpu, gpu, () => true, output));
        Assert.Equal(2, port.Writes.Count);
        Assert.Contains("MatrixThermalRestored=False", output.ToString());
    }

    private sealed class Gpu : IGpuClockOffsetValidationController, IGpuClockOffsetProbe
    {
        internal int Current;
        internal int Writes;
        internal bool FailFirstSet;
        internal Action? OnFirstSet;
        public GpuClockOffsetCapabilities Query() => new("NVIDIA GeForce RTX 5090 Laptop GPU", new(-1000, 1000, Current), new(-1000, 3000, Current));
        public void Set(GpuClockOffsetValidationSettings settings)
        {
            Writes++;
            Current = settings.CoreMegahertz;
            if (Writes == 1)
            {
                OnFirstSet?.Invoke();
                if (FailFirstSet)
                {
                    throw new InvalidOperationException("scripted GPU failure after mutation");
                }
            }
        }
    }

    private sealed class Port : IProductionBatteryPort
    {
        internal ProductionPerformanceFanState State = new(PerformancePreset.Performance, FanMode.Automatic);
        internal AdapterPowerState Power = AdapterPowerState.FullPower280W;
        internal List<PerformancePreset> Writes = [];
        internal bool FailFirstSet;
        internal bool RejectRestore;
        internal bool DriftOnReconciliation;
        private int restoredQueries;
        public DeviceCapabilities Capabilities => throw new NotSupportedException();
        public bool SupportsTemporaryFullChargeQuery => false;
        public Task<ProductionPerformanceFanState> QueryPerformanceFanStateAsync(CancellationToken cancellationToken)
        {
            if (DriftOnReconciliation && Writes.Count == 2 && ++restoredQueries == 2)
            {
                State = new(PerformancePreset.Silent, FanMode.Automatic);
            }
            return Task.FromResult(State);
        }
        public Task<AdapterPowerState> QueryAdapterPowerStateAsync(CancellationToken cancellationToken) => Task.FromResult(Power);
        public Task SetPerformanceAsync(PerformancePreset value, CancellationToken cancellationToken)
        {
            Writes.Add(value);
            if (RejectRestore && Writes.Count > 1)
            {
                throw new InvalidOperationException("scripted restoration failure");
            }
            State = new(value, FanMode.Automatic);
            if (FailFirstSet && Writes.Count == 1)
            {
                throw new InvalidOperationException("scripted thermal failure after mutation");
            }
            return Task.CompletedTask;
        }
        public Task SetCustomPerformanceAsync(CustomPerformanceSettings value, CancellationToken cancellationToken)
        {
            Writes.Add(PerformancePreset.Custom);
            State = new(PerformancePreset.Custom, FanMode.Automatic) { CustomPerformance = value };
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<ConfirmedBatteryState> QueryProtectionAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool?> QueryTemporaryFullChargeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetFanModeAsync(FanMode value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetFanControlAsync(FanControlSettings value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetFanTargetsAsync(int fan1Rpm, int fan2Rpm, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetChargeLimitAsync(int percent, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetProtectionOffAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetTemporaryFullChargeAsync(bool enabled, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task ApplyOneShotLaptopControlAsync(ApplyOneShotLaptopControl value, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
