using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class FanCurveCalculatorTests
{
    [Fact]
    public void SeparateCurvesUseTheirOwnSensors()
    {
        ManualFanCurve curve = new(
            [new(40, 2000), new(80, 4000)],
            [new(40, 2000), new(80, 5000)],
            useHigherTemperatureForBoth: false);

        FanTargetSpeeds targets = FanCurveCalculator.Calculate(
            curve,
            new FanTemperatureReadings(CpuCelsius: 60, GpuCelsius: 70));

        Assert.Equal(new FanTargetSpeeds(3000, 4300, FailSafe: false), targets);
    }

    [Fact]
    public void HotterSensorOptionFeedsBothCurves()
    {
        ManualFanCurve curve = new(
            [new(40, 2000), new(80, 4000)],
            [new(40, 2000), new(80, 5000)],
            useHigherTemperatureForBoth: true);

        FanTargetSpeeds targets = FanCurveCalculator.Calculate(
            curve,
            new FanTemperatureReadings(CpuCelsius: 60, GpuCelsius: 70));

        Assert.Equal(new FanTargetSpeeds(3500, 4300, FailSafe: false), targets);
    }

    [Theory]
    [InlineData(null, 60)]
    [InlineData(60, null)]
    [InlineData(95, 40)]
    [InlineData(40, 87)]
    public void MissingOrCriticalTemperatureForcesMaximum(
        int? cpu,
        int? gpu)
    {
        Assert.Equal(
            new FanTargetSpeeds(5400, 5400, FailSafe: true),
            FanCurveCalculator.Calculate(
                ManualFanCurve.Default,
                new FanTemperatureReadings(cpu, gpu)));
    }

    [Fact]
    public void MissingTemperatureUsesTheAdmittedDeviceMaximum()
    {
        FanTargetSpeeds targets = FanCurveCalculator.Calculate(
            ManualFanCurve.Default,
            new FanTemperatureReadings(null, null),
            maximumRpm: 5000);

        Assert.Equal(new FanTargetSpeeds(5000, 5000, FailSafe: true), targets);
    }

    [Fact]
    public void InactiveGpuUsesCpuTemperatureWithoutFailSafe()
    {
        FanTargetSpeeds targets = FanCurveCalculator.Calculate(
            ManualFanCurve.Default,
            new FanTemperatureReadings(
                CpuCelsius: 60,
                GpuCelsius: null,
                GpuInactive: true));

        Assert.False(targets.FailSafe);
        Assert.Equal(ManualFanCurve.Default.InterpolateCpu(60), targets.Fan1Rpm);
        Assert.Equal(ManualFanCurve.Default.InterpolateGpu(60), targets.Fan2Rpm);
    }

    [Fact]
    public async Task InactiveGpuDoesNotInvokeNvmlTemperatureReader()
    {
        int gpuReads = 0;
        using var source = new SafeWindowsFanTemperatureSource(
            new FixedGpuActivitySource(IsActive: false),
            cpuTemperatureReader: () => 48,
            gpuTemperatureReader: () =>
            {
                gpuReads++;
                return 52;
            });

        FanTemperatureReadings readings = await source.ReadAsync(
            includeCpu: true,
            includeGpu: true,
            CancellationToken.None);

        Assert.Equal(48, readings.CpuCelsius);
        Assert.Null(readings.GpuCelsius);
        Assert.True(readings.GpuInactive);
        Assert.Equal(0, gpuReads);
    }

    [Fact]
    public async Task ActiveGpuInvokesNvmlTemperatureReader()
    {
        int gpuReads = 0;
        using var source = new SafeWindowsFanTemperatureSource(
            new FixedGpuActivitySource(IsActive: true),
            cpuTemperatureReader: () => 48,
            gpuTemperatureReader: () =>
            {
                gpuReads++;
                return 52;
            });

        FanTemperatureReadings readings = await source.ReadAsync(
            includeCpu: false,
            includeGpu: true,
            CancellationToken.None);

        Assert.Null(readings.CpuCelsius);
        Assert.Equal(52, readings.GpuCelsius);
        Assert.False(readings.GpuInactive);
        Assert.Equal(1, gpuReads);
    }

    [Fact]
    public async Task UnknownGpuDoesNotInvokeNvmlAndRetainsFanFailSafe()
    {
        int gpuReads = 0;
        using var source = new SafeWindowsFanTemperatureSource(
            new FixedGpuActivitySource(IsActive: null),
            cpuTemperatureReader: () => 48,
            gpuTemperatureReader: () =>
            {
                gpuReads++;
                return 52;
            });

        FanTemperatureReadings readings = await source.ReadAsync(
            includeCpu: true,
            includeGpu: true,
            CancellationToken.None);
        FanTargetSpeeds targets = FanCurveCalculator.Calculate(
            ManualFanCurve.Default,
            readings,
            maximumRpm: 5000);

        Assert.Equal(48, readings.CpuCelsius);
        Assert.Null(readings.GpuCelsius);
        Assert.False(readings.GpuInactive);
        Assert.Equal(0, gpuReads);
        Assert.Equal(new FanTargetSpeeds(5000, 5000, FailSafe: true), targets);
    }

    [Theory]
    [InlineData(2732, 0)]
    [InlineData(3010, 28)]
    [InlineData(3182, 45)]
    [InlineData(3881, 115)]
    public void RazerThermalZoneConvertsTenthsKelvinToCelsius(
        double highPrecisionKelvin,
        int expectedCelsius)
    {
        Assert.Equal(
            expectedCelsius,
            SafeWindowsFanTemperatureSource.WindowsThermalZone
                .TryConvertHighPrecisionKelvin(highPrecisionKelvin));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(2500)]
    [InlineData(3890)]
    public void RazerThermalZoneRejectsInvalidTemperature(double value)
    {
        Assert.Null(
            SafeWindowsFanTemperatureSource.WindowsThermalZone
                .TryConvertHighPrecisionKelvin(value));
    }

    [Fact]
    public void RazerThermalZoneRetainsBothExactModelCounterPaths()
    {
        Assert.Equal(
            [
                @"\Thermal Zone Information(\_SB.PCI0.SBRG.EC0.TZRZ)\High Precision Temperature",
                @"\Thermal Zone Information(\_SB.PC00.LPCB.EC0.TZRZ)\High Precision Temperature",
            ],
            SafeWindowsFanTemperatureSource.WindowsThermalZone.RazerEcCounterPaths);
    }

    [Fact]
    public async Task ActiveGpuKeepsTheDriverSessionOpenAcrossReads()
    {
        var activity = new SettableGpuActivitySource { Active = true };
        var session = new CountingGpuTemperatureSession(52);
        var time = new ManualTimeProvider();
        using var source = new SafeWindowsFanTemperatureSource(
            activity,
            cpuTemperatureReader: () => 48,
            session,
            time);

        FanTemperatureReadings first = await source.ReadAsync(CancellationToken.None);
        FanTemperatureReadings second = await source.ReadAsync(CancellationToken.None);

        Assert.Equal(52, first.GpuCelsius);
        Assert.Equal(52, second.GpuCelsius);
        Assert.Equal(2, session.Reads);
        Assert.Equal(0, session.Releases);
        Assert.Equal(
            SafeWindowsFanTemperatureSource.GpuSessionIdleTimeout,
            time.LastDueTime);

        activity.Active = false;
        FanTemperatureReadings inactive = await source.ReadAsync(CancellationToken.None);

        Assert.Null(inactive.GpuCelsius);
        Assert.True(inactive.GpuInactive);
        Assert.Equal(2, session.Reads);
        Assert.Equal(1, session.Releases);
        Assert.Equal(Timeout.InfiniteTimeSpan, time.LastDueTime);
    }

    [Fact]
    public async Task UnknownGpuActivityReleasesTheDriverSession()
    {
        var activity = new SettableGpuActivitySource { Active = true };
        var session = new CountingGpuTemperatureSession(52);
        using var source = new SafeWindowsFanTemperatureSource(
            activity,
            cpuTemperatureReader: () => 48,
            session,
            new ManualTimeProvider());

        await source.ReadAsync(CancellationToken.None);
        activity.Active = null;
        FanTemperatureReadings unknown = await source.ReadAsync(CancellationToken.None);

        Assert.Null(unknown.GpuCelsius);
        Assert.False(unknown.GpuInactive);
        Assert.Equal(1, session.Reads);
        Assert.Equal(1, session.Releases);
    }

    [Fact]
    public async Task IdleTimeoutReleasesTheDriverSessionWithoutAnotherRead()
    {
        var activity = new SettableGpuActivitySource { Active = true };
        var session = new CountingGpuTemperatureSession(52);
        var time = new ManualTimeProvider();
        using var source = new SafeWindowsFanTemperatureSource(
            activity,
            cpuTemperatureReader: () => 48,
            session,
            time);

        await source.ReadAsync(CancellationToken.None);
        Assert.Equal(0, session.Releases);

        time.FireTimers();

        Assert.Equal(1, session.Releases);
        Assert.Equal(1, session.Reads);
    }

    [Fact]
    public async Task CpuOnlyReadsLeaveTheGpuGateAndSessionUntouched()
    {
        var activity = new SettableGpuActivitySource { Active = true };
        var session = new CountingGpuTemperatureSession(52);
        using var source = new SafeWindowsFanTemperatureSource(
            activity,
            cpuTemperatureReader: () => 48,
            session,
            new ManualTimeProvider());

        FanTemperatureReadings readings = await source.ReadAsync(
            includeCpu: true,
            includeGpu: false,
            CancellationToken.None);

        Assert.Equal(48, readings.CpuCelsius);
        Assert.Null(readings.GpuCelsius);
        Assert.Equal(0, activity.Calls);
        Assert.Equal(0, session.Reads);
        Assert.Equal(0, session.Releases);
    }

    [Fact]
    public async Task DisposeReleasesTheDriverSessionAndOwnedSensors()
    {
        var activity = new SettableGpuActivitySource { Active = true };
        var session = new CountingGpuTemperatureSession(52);
        var owner = new DisposableFlag();
        var source = new SafeWindowsFanTemperatureSource(
            activity,
            cpuTemperatureReader: () => 48,
            session,
            new ManualTimeProvider(),
            cpuTemperatureOwner: owner);
        await source.ReadAsync(CancellationToken.None);

        source.Dispose();
        source.Dispose();

        Assert.Equal(1, session.Releases);
        Assert.True(owner.Disposed);
        Assert.True(activity.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => source.ReadAsync(CancellationToken.None).AsTask());
    }

    private sealed record FixedGpuActivitySource(bool? IsActive) : IGpuActivitySource
    {
        ValueTask<bool?> IGpuActivitySource.IsActiveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(IsActive);
        }
    }

    private sealed class SettableGpuActivitySource : IGpuActivitySource, IDisposable
    {
        public bool? Active { get; set; }

        public int Calls { get; private set; }

        public bool Disposed { get; private set; }

        public ValueTask<bool?> IsActiveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return new(Active);
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class CountingGpuTemperatureSession(int? temperature) : IGpuTemperatureSession
    {
        public int Reads { get; private set; }

        public int Releases { get; private set; }

        public int? TryRead()
        {
            Reads++;
            return temperature;
        }

        public void Release() => Releases++;
    }

    private sealed class DisposableFlag : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> timers = [];

        public TimeSpan LastDueTime { get; private set; } = Timeout.InfiniteTimeSpan;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        public void FireTimers()
        {
            foreach (ManualTimer timer in timers.ToArray())
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            private TimeSpan dueTime = Timeout.InfiniteTimeSpan;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                this.dueTime = dueTime;
                owner.LastDueTime = dueTime;
                return true;
            }

            public void Fire()
            {
                if (dueTime != Timeout.InfiniteTimeSpan)
                {
                    dueTime = Timeout.InfiniteTimeSpan;
                    callback(state);
                }
            }

            public void Dispose() => owner.timers.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
