using OpenBlade.Contracts;
using OpenBlade.Service;

namespace OpenBlade.Service.Tests;

public sealed class FanCurveCalculatorTests
{
    [Fact]
    public void NativeStatusObservation_RecordsOnlyChangesAndOneRecovery()
    {
        List<string> warnings = [];
        var observer = new NativeStatusChangeObserver("Sensor", "Read", warnings.Add);

        observer.ObserveFailure("Collect", 0x800007D5);
        observer.ObserveFailure("Collect", 0x800007D5);
        observer.ObserveFailure("Format", 5);
        observer.ObserveRecovery();
        observer.ObserveRecovery();

        Assert.Equal(3, warnings.Count);
        Assert.Contains("component=Sensor operation=Read state=failed reason=Collect", warnings[0], StringComparison.Ordinal);
        Assert.Contains("status=2147485653 statusHex=0x800007D5", warnings[0], StringComparison.Ordinal);
        Assert.Contains("reason=Format status=5 statusHex=0x00000005", warnings[1], StringComparison.Ordinal);
        Assert.Contains("state=recovered status=0 statusHex=0x00000000", warnings[2], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(40, 2600)]
    [InlineData(80, 5000)]
    [InlineData(90, 5000)]
    public void ActiveRangeProjectsEffectiveTargetsWithoutChangingSavedCurve(int temperature, int expected)
    {
        var curve = new ManualFanCurve([new(40, 2200), new(80, 5200)],
            [new(40, 2200), new(80, 5200)], true);
        FanTargetSpeeds targets = FanCurveCalculator.Calculate(curve,
            new FanTemperatureReadings(temperature, temperature), new FanSpeedRange(2600, 5000, 100));
        Assert.Equal(expected, targets.Fan1Rpm);
        Assert.Equal(expected, targets.Fan2Rpm);
        Assert.Equal(5200, curve.Cpu[^1].SpeedRpm);
        Assert.Equal(2200, curve.Cpu[0].SpeedRpm);
    }

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
    public void PoweredDownGpuUsesCpuTemperatureWithoutFailSafe()
    {
        FanTargetSpeeds targets = FanCurveCalculator.Calculate(
            ManualFanCurve.Default,
            new FanTemperatureReadings(
                CpuCelsius: 60,
                GpuCelsius: null,
                GpuInactive: true,
                GpuPoweredDown: true));

        Assert.False(targets.FailSafe);
        Assert.Equal(ManualFanCurve.Default.InterpolateCpu(60), targets.Fan1Rpm);
        Assert.Equal(ManualFanCurve.Default.InterpolateGpu(60), targets.Fan2Rpm);
    }

    [Fact]
    public async Task PoweredDownGpuDoesNotInvokeNvmlTemperatureReader()
    {
        int gpuReads = 0;
        using var source = new SafeWindowsFanTemperatureSource(
            new FixedGpuActivitySource(IsActive: false),
            cpuTemperatureReader: () => 48,
            gpuTemperatureReader: () =>
            {
                gpuReads++;
                return 52;
            },
            gpuPowerState: () => FanStopGpuPowerState.PoweredDown);

        FanTemperatureReadings readings = await source.ReadForFanCurveAsync(
            includeCpu: true,
            includeGpu: true,
            CancellationToken.None);

        Assert.Equal(48, readings.CpuCelsius);
        Assert.Null(readings.GpuCelsius);
        Assert.True(readings.GpuInactive);
        Assert.True(readings.GpuPoweredDown);
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
            },
            gpuPowerState: () => FanStopGpuPowerState.PoweredOn);

        FanTemperatureReadings readings = await source.ReadForFanCurveAsync(
            includeCpu: false,
            includeGpu: true,
            CancellationToken.None);

        Assert.Null(readings.CpuCelsius);
        Assert.Equal(52, readings.GpuCelsius);
        Assert.False(readings.GpuInactive);
        Assert.Equal(1, gpuReads);
    }

    [Fact]
    public async Task UnknownGpuPowerDoesNotInvokeNvmlAndRetainsFanFailSafe()
    {
        int gpuReads = 0;
        using var source = new SafeWindowsFanTemperatureSource(
            new FixedGpuActivitySource(IsActive: null),
            cpuTemperatureReader: () => 48,
            gpuTemperatureReader: () =>
            {
                gpuReads++;
                return 52;
            },
            gpuPowerState: () => FanStopGpuPowerState.Unknown);

        FanTemperatureReadings readings = await source.ReadForFanCurveAsync(
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

    [Theory]
    [InlineData(false, 5400)]
    [InlineData(true, 5000)]
    public async Task ConstantBackedThermalZonesCannotDriveFanCurves(bool gpuInactive, int maximumRpm)
    {
        using var zone = new SafeWindowsFanTemperatureSource.WindowsThermalZone();
        Assert.Empty(SafeWindowsFanTemperatureSource.WindowsThermalZone.RazerEcCounterPaths);
        int gpuReads = 0;
        using var source = new SafeWindowsFanTemperatureSource(
            new FixedGpuActivitySource(IsActive: !gpuInactive),
            cpuTemperatureReader: zone.TryRead,
            gpuTemperatureReader: () =>
            {
                gpuReads++;
                return 52;
            },
            gpuPowerState: () => gpuInactive ? FanStopGpuPowerState.PoweredDown : FanStopGpuPowerState.PoweredOn);

        FanTemperatureReadings readings = await source.ReadForFanCurveAsync(CancellationToken.None);
        Assert.Null(readings.CpuCelsius);
        Assert.Equal(gpuInactive ? null : (int?)52, readings.GpuCelsius);
        Assert.Equal(gpuInactive, readings.GpuInactive);
        Assert.Equal(gpuInactive ? 0 : 1, gpuReads);
        Assert.Equal(
            new FanTargetSpeeds(maximumRpm, maximumRpm, FailSafe: true),
            FanCurveCalculator.Calculate(ManualFanCurve.Default, readings, maximumRpm));
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
            time,
            gpuPowerState: () => FanStopGpuPowerState.PoweredOn);

        FanTemperatureReadings first = await source.ReadForFanCurveAsync(CancellationToken.None);
        FanTemperatureReadings second = await source.ReadForFanCurveAsync(CancellationToken.None);

        Assert.Equal(52, first.GpuCelsius);
        Assert.Equal(52, second.GpuCelsius);
        Assert.Equal(2, session.Reads);
        Assert.Equal(0, session.Releases);
        Assert.Equal(
            SafeWindowsFanTemperatureSource.GpuSessionIdleTimeout,
            time.LastDueTime);

        activity.Active = false;
        FanTemperatureReadings inactive = await source.ReadForFanCurveAsync(CancellationToken.None);

        Assert.Equal(52, inactive.GpuCelsius);
        Assert.True(inactive.GpuInactive);
        Assert.Equal(3, session.Reads);
        Assert.Equal(1, session.Releases);
        Assert.Equal(Timeout.InfiniteTimeSpan, time.LastDueTime);
    }

    [Fact]
    public async Task FanStopBorrowsActiveCurveSessionWithoutExtendingItsIdleDeadline()
    {
        var activity = new SettableGpuActivitySource { Active = true };
        var session = new CountingGpuTemperatureSession(52);
        var time = new ManualTimeProvider();
        using var source = new SafeWindowsFanTemperatureSource(activity, () => 48, session, time,
            gpuPowerState: () => FanStopGpuPowerState.PoweredOn);

        await source.ReadForFanCurveAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        FanTemperatureReadings reading = await source.ReadForFanStopAsync(CancellationToken.None);

        Assert.Equal(52, reading.GpuCelsius);
        Assert.Equal(2, session.Reads);
        Assert.Equal(0, session.Releases);
        Assert.Equal(SafeWindowsFanTemperatureSource.GpuSessionIdleTimeout, time.LastDueTime);
        time.Advance(TimeSpan.FromSeconds(4));
        time.FireTimers(advanceToDueTime: false);
        Assert.Equal(1, session.Releases); // Original ordinary-reader deadline.
    }

    [Fact]
    public async Task FanStopAloneNeverRetainsAnActiveGpuSession()
    {
        var session = new CountingGpuTemperatureSession(52);
        var time = new ManualTimeProvider();
        using var source = new SafeWindowsFanTemperatureSource(new SettableGpuActivitySource { Active = true },
            () => 48, session, time, gpuPowerState: () => FanStopGpuPowerState.PoweredOn);

        await source.ReadForFanStopAsync(CancellationToken.None);
        await source.ReadForFanStopAsync(CancellationToken.None);

        Assert.Equal(2, session.Reads);
        Assert.Equal(2, session.Releases);
        Assert.Equal(Timeout.InfiniteTimeSpan, time.LastDueTime);
    }

    [Fact]
    public async Task FanStopReleasesBorrowedSessionAsSoonAsGpuWorkloadEnds()
    {
        var activity = new SettableGpuActivitySource { Active = true };
        var session = new CountingGpuTemperatureSession(52);
        var time = new ManualTimeProvider();
        using var source = new SafeWindowsFanTemperatureSource(activity, () => 48, session, time,
            gpuPowerState: () => FanStopGpuPowerState.PoweredOn);

        await source.ReadForFanCurveAsync(CancellationToken.None);
        activity.Active = false;
        Assert.Equal(52, (await source.ReadForFanStopAsync(CancellationToken.None)).GpuCelsius);
        Assert.Equal(1, session.Releases);
        Assert.Equal(Timeout.InfiniteTimeSpan, time.LastDueTime);
    }

    [Fact]
    public async Task FanStopCannotRenewAnExpiredCurveSession()
    {
        var session = new CountingGpuTemperatureSession(52);
        var time = new ManualTimeProvider();
        using var source = new SafeWindowsFanTemperatureSource(new SettableGpuActivitySource { Active = true },
            () => 48, session, time, gpuPowerState: () => FanStopGpuPowerState.PoweredOn);

        await source.ReadForFanCurveAsync(CancellationToken.None);
        time.Advance(SafeWindowsFanTemperatureSource.GpuSessionIdleTimeout);
        await source.ReadForFanStopAsync(CancellationToken.None);
        Assert.Equal(1, session.Releases);
        Assert.Equal(Timeout.InfiniteTimeSpan, time.LastDueTime);
    }

    [Fact]
    public async Task PowerTransitionInvalidatesBorrowedSessionAndTemperature()
    {
        var session = new CountingGpuTemperatureSession(52);
        var time = new ManualTimeProvider();
        bool transitionDuringRead = false;
        int powerReads = 0;
        using var source = new SafeWindowsFanTemperatureSource(new SettableGpuActivitySource { Active = true },
            () => 48, session, time, gpuPowerState: () => !transitionDuringRead || ++powerReads == 1
                ? FanStopGpuPowerState.PoweredOn : FanStopGpuPowerState.PoweredDown);

        await source.ReadForFanCurveAsync(CancellationToken.None);
        transitionDuringRead = true;
        FanTemperatureReadings reading = await source.ReadForFanStopAsync(CancellationToken.None);
        Assert.Null(reading.GpuCelsius);
        Assert.False(reading.GpuPoweredDown);
        Assert.Equal(1, session.Releases);
        Assert.Equal(Timeout.InfiniteTimeSpan, time.LastDueTime);
    }

    [Fact]
    public async Task UnknownGpuActivityStillReadsAwakeTemperatureAndReleasesTheDriverSession()
    {
        var activity = new SettableGpuActivitySource { Active = true };
        var session = new CountingGpuTemperatureSession(52);
        using var source = new SafeWindowsFanTemperatureSource(
            activity,
            cpuTemperatureReader: () => 48,
            session,
            new ManualTimeProvider(),
            gpuPowerState: () => FanStopGpuPowerState.PoweredOn);

        await source.ReadForFanCurveAsync(CancellationToken.None);
        activity.Active = null;
        FanTemperatureReadings unknown = await source.ReadForFanCurveAsync(CancellationToken.None);

        Assert.Equal(52, unknown.GpuCelsius);
        Assert.False(unknown.GpuInactive);
        Assert.Equal(2, session.Reads);
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
            time,
            gpuPowerState: () => FanStopGpuPowerState.PoweredOn);

        await source.ReadForFanCurveAsync(CancellationToken.None);
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
            new ManualTimeProvider(),
            gpuPowerState: () => throw new InvalidOperationException("CPU-only samples must not inspect GPU power."));

        FanTemperatureReadings readings = await source.ReadForFanCurveAsync(
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
    public async Task IdleTimeoutDuringCpuOnlyReadRetriesAfterTheGateIsReleased()
    {
        var session = new CountingGpuTemperatureSession(52);
        var time = new ManualTimeProvider();
        bool expireDuringRead = false;
        using var source = new SafeWindowsFanTemperatureSource(
            new SettableGpuActivitySource { Active = true },
            () =>
            {
                if (expireDuringRead)
                {
                    time.FireTimers();
                }
                return 48;
            },
            session,
            time,
            gpuPowerState: () => FanStopGpuPowerState.PoweredOn);
        await source.ReadForFanCurveAsync(CancellationToken.None);
        expireDuringRead = true;

        await source.ReadForFanCurveAsync(true, false, CancellationToken.None);

        Assert.Equal(0, session.Releases);
        Assert.Equal(SafeWindowsFanTemperatureSource.GpuSessionIdleRetry, time.LastDueTime);
        time.FireTimers();
        Assert.Equal(1, session.Releases);
        Assert.Equal(1, session.Reads);
    }

    [Fact]
    public async Task StaleIdleCallbackDoesNotReleaseARefreshedGpuSession()
    {
        var session = new CountingGpuTemperatureSession(52);
        var time = new ManualTimeProvider();
        using var source = new SafeWindowsFanTemperatureSource(
            new SettableGpuActivitySource { Active = true }, () => 48, session, time,
            gpuPowerState: () => FanStopGpuPowerState.PoweredOn);
        await source.ReadForFanCurveAsync(CancellationToken.None);
        time.Advance(SafeWindowsFanTemperatureSource.GpuSessionIdleTimeout);
        await source.ReadForFanCurveAsync(CancellationToken.None);

        // The original expiry was already queued when the newer sample rearmed it.
        time.FireTimers(advanceToDueTime: false);

        Assert.Equal(0, session.Releases);
        Assert.Equal(SafeWindowsFanTemperatureSource.GpuSessionIdleTimeout, time.LastDueTime);
        time.FireTimers();
        Assert.Equal(1, session.Releases);
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
            cpuTemperatureOwner: owner,
            gpuPowerState: () => FanStopGpuPowerState.PoweredOn);
        await source.ReadForFanCurveAsync(CancellationToken.None);

        source.Dispose();
        source.Dispose();

        Assert.Equal(1, session.Releases);
        Assert.True(owner.Disposed);
        Assert.True(activity.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => source.ReadForFanCurveAsync(CancellationToken.None).AsTask());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HotGpuKeepsDrivingCurvesAfterWorkloadEndsUntilItsMeasuredTemperatureFalls(bool useHigherTemperature)
    {
        ManualFanCurve curve = new(
            [new(40, 2000), new(80, 4000)],
            [new(40, 2000), new(80, 5000)],
            useHigherTemperature);
        var activity = new SettableGpuActivitySource { Active = true };
        var session = new CountingGpuTemperatureSession(80);
        var time = new ManualTimeProvider();
        FanStopGpuPowerState power = FanStopGpuPowerState.PoweredOn;
        using var source = new SafeWindowsFanTemperatureSource(activity, () => 40, session, time,
            gpuPowerState: () => power);

        FanTargetSpeeds hotTargets = new(useHigherTemperature ? 4000 : 2000, 5000, FailSafe: false);
        Assert.Equal(hotTargets, FanCurveCalculator.Calculate(curve, await source.ReadForFanCurveAsync(default)));
        Assert.Equal(0, session.Releases);

        activity.Active = false;
        FanTemperatureReadings idle = await source.ReadForFanCurveAsync(default);
        Assert.True(idle.GpuInactive);
        Assert.False(idle.GpuPoweredDown);
        Assert.Equal(80, idle.GpuCelsius);
        Assert.Equal(hotTargets, FanCurveCalculator.Calculate(curve, idle));
        Assert.Equal(1, session.Releases);
        Assert.Equal(Timeout.InfiniteTimeSpan, time.LastDueTime);

        session.Temperature = 60;
        Assert.Equal(new FanTargetSpeeds(useHigherTemperature ? 3000 : 2000, 3500, FailSafe: false),
            FanCurveCalculator.Calculate(curve, await source.ReadForFanCurveAsync(default)));
        session.Temperature = 40;
        Assert.Equal(new FanTargetSpeeds(2000, 2000, FailSafe: false),
            FanCurveCalculator.Calculate(curve, await source.ReadForFanCurveAsync(default)));
        Assert.Equal(4, session.Reads);

        // Power-down ends GPU sampling immediately, without a retained hot
        // reading or a timed fan hold. Returning to D0 obtains a fresh sample.
        power = FanStopGpuPowerState.PoweredDown;
        session.Temperature = 80;
        FanTemperatureReadings sleeping = await source.ReadForFanCurveAsync(default);
        Assert.True(sleeping.GpuPoweredDown);
        Assert.Null(sleeping.GpuCelsius);
        Assert.Equal(4, session.Reads);
        Assert.Equal(new FanTargetSpeeds(2000, 2000, FailSafe: false), FanCurveCalculator.Calculate(curve, sleeping));
        power = FanStopGpuPowerState.PoweredOn;
        Assert.Equal(hotTargets, FanCurveCalculator.Calculate(curve, await source.ReadForFanCurveAsync(default)));
        Assert.Equal(5, session.Reads);
        Assert.Equal(5, session.Releases);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 9)]
    [InlineData(true, 9)]
    [InlineData(false, 126)]
    [InlineData(true, 126)]
    [InlineData(false, 87)]
    [InlineData(true, 87)]
    public async Task IdleAwakeGpuRetainsMissingTemperatureAndEmergencyFailSafe(bool fanStopSample, int? gpuCelsius)
    {
        var session = new CountingGpuTemperatureSession(gpuCelsius);
        using var source = new SafeWindowsFanTemperatureSource(new FixedGpuActivitySource(false),
            () => 40, session, new ManualTimeProvider(), gpuPowerState: () => FanStopGpuPowerState.PoweredOn);

        FanTemperatureReadings reading = fanStopSample
            ? await source.ReadForFanStopAsync(default) : await source.ReadForFanCurveAsync(default);

        Assert.True(reading.GpuInactive);
        Assert.False(reading.GpuPoweredDown);
        Assert.Equal(gpuCelsius is >= 10 and <= 125 ? gpuCelsius : null, reading.GpuCelsius);
        Assert.Equal(new FanTargetSpeeds(5000, 5000, FailSafe: true),
            FanCurveCalculator.Calculate(ManualFanCurve.Default, reading, maximumRpm: 5000));
        Assert.Equal(1, session.Reads);
        Assert.Equal(1, session.Releases);
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(false, null, 0)]
    [InlineData(false, false, 2)]
    [InlineData(false, true, 2)]
    [InlineData(false, null, 2)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 0)]
    [InlineData(true, null, 0)]
    [InlineData(true, false, 2)]
    [InlineData(true, true, 2)]
    [InlineData(true, null, 2)]
    public async Task UnknownOrPoweredDownGpuNeverReadsTemperatureRegardlessOfWorkload(
        bool fanStopSample, bool? active, int power)
    {
        var session = new CountingGpuTemperatureSession(80);
        using var source = new SafeWindowsFanTemperatureSource(new FixedGpuActivitySource(active),
            () => 40, session, new ManualTimeProvider(), gpuPowerState: () => (FanStopGpuPowerState)power);

        FanTemperatureReadings reading = fanStopSample
            ? await source.ReadForFanStopAsync(default) : await source.ReadForFanCurveAsync(default);

        bool confirmedPowerDown = power == (int)FanStopGpuPowerState.PoweredDown && active is not true;
        Assert.Equal(confirmedPowerDown, reading.GpuPoweredDown);
        Assert.Null(reading.GpuCelsius);
        Assert.Equal(0, session.Reads);
        Assert.Equal(1, session.Releases);
        Assert.Equal(!confirmedPowerDown, FanCurveCalculator.Calculate(ManualFanCurve.Default, reading).FailSafe);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GpuPowerTransitionDuringSamplingInvalidatesTemperatureAndReleasesSession(
        bool fanStopSample, bool active)
    {
        var session = new CountingGpuTemperatureSession(80);
        var time = new ManualTimeProvider();
        int powerReads = 0;
        using var source = new SafeWindowsFanTemperatureSource(new FixedGpuActivitySource(active),
            () => 40, session, time, gpuPowerState: () => ++powerReads == 1
                ? FanStopGpuPowerState.PoweredOn : FanStopGpuPowerState.PoweredDown);

        FanTemperatureReadings reading = fanStopSample
            ? await source.ReadForFanStopAsync(default) : await source.ReadForFanCurveAsync(default);

        Assert.Null(reading.GpuCelsius);
        Assert.False(reading.GpuPoweredDown);
        Assert.True(FanCurveCalculator.Calculate(ManualFanCurve.Default, reading).FailSafe);
        Assert.Equal(1, session.Reads);
        Assert.Equal(1, session.Releases);
        Assert.Equal(Timeout.InfiniteTimeSpan, time.LastDueTime);
    }

    [Fact]
    public async Task PowerDownBetweenSamplesReleasesActiveSessionWithoutAnotherTemperatureRead()
    {
        var activity = new SettableGpuActivitySource { Active = true };
        var session = new CountingGpuTemperatureSession(80);
        var time = new ManualTimeProvider();
        FanStopGpuPowerState power = FanStopGpuPowerState.PoweredOn;
        using var source = new SafeWindowsFanTemperatureSource(activity, () => 40, session, time,
            gpuPowerState: () => power);
        await source.ReadForFanCurveAsync(default);

        activity.Active = false;
        power = FanStopGpuPowerState.PoweredDown;
        FanTemperatureReadings sleeping = await source.ReadForFanCurveAsync(default);

        Assert.True(sleeping.GpuPoweredDown);
        Assert.Null(sleeping.GpuCelsius);
        Assert.Equal(1, session.Reads);
        Assert.Equal(1, session.Releases);
        Assert.Equal(Timeout.InfiniteTimeSpan, time.LastDueTime);
        time.FireTimers();
        Assert.Equal(1, session.Reads);
        Assert.Equal(1, session.Releases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanceledIdleTemperatureReadReleasesSessionAndSamplingGate(bool fanStopSample)
    {
        var activity = new SettableGpuActivitySource { Active = true };
        var session = new CountingGpuTemperatureSession(80);
        var time = new ManualTimeProvider();
        using var source = new SafeWindowsFanTemperatureSource(activity, () => 40, session, time,
            gpuPowerState: () => FanStopGpuPowerState.PoweredOn);
        await source.ReadForFanCurveAsync(default);

        using var cancellation = new CancellationTokenSource();
        activity.Active = false;
        session.BeforeRead = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => (fanStopSample
            ? source.ReadForFanStopAsync(cancellation.Token) : source.ReadForFanCurveAsync(cancellation.Token)).AsTask());

        Assert.Equal(2, session.Reads);
        Assert.Equal(1, session.Releases);
        Assert.Equal(Timeout.InfiniteTimeSpan, time.LastDueTime);
        session.BeforeRead = null;
        FanTemperatureReadings recovered = await source.ReadForFanCurveAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(80, recovered.GpuCelsius);
        Assert.Equal(2, session.Releases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringPowerRecheckReleasesSessionAndRejectsTheSample(bool fanStopSample)
    {
        var session = new CountingGpuTemperatureSession(80);
        using var cancellation = new CancellationTokenSource();
        int powerReads = 0;
        using var source = new SafeWindowsFanTemperatureSource(new FixedGpuActivitySource(false),
            () => 40, session, new ManualTimeProvider(), gpuPowerState: () =>
            {
                if (++powerReads == 2)
                {
                    cancellation.Cancel();
                }
                return FanStopGpuPowerState.PoweredOn;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => (fanStopSample
            ? source.ReadForFanStopAsync(cancellation.Token) : source.ReadForFanCurveAsync(cancellation.Token)).AsTask());
        Assert.Equal(1, session.Reads);
        Assert.Equal(1, session.Releases);
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
        public int? Temperature { get; set; } = temperature;

        public Action? BeforeRead { get; set; }

        public int Reads { get; private set; }

        public int Releases { get; private set; }

        public int? TryRead()
        {
            Reads++;
            BeforeRead?.Invoke();
            return Temperature;
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
        private long timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => timestamp;

        public void Advance(TimeSpan elapsed) => timestamp += elapsed.Ticks;

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

        public void FireTimers(bool advanceToDueTime = true)
        {
            if (advanceToDueTime && LastDueTime > TimeSpan.Zero)
            {
                Advance(LastDueTime);
            }
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
