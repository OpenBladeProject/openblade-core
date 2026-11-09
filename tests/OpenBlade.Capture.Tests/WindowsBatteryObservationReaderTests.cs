using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class WindowsBatteryObservationReaderTests
{
    [Theory]
    [InlineData(2982u, 25.05)]
    [InlineData(2732u, 0.05)]
    [InlineData(2682u, -4.95)]
    public void DecodesPackTemperatureFromTenthsOfKelvin(uint raw, double expectedCelsius)
    {
        var observation = WindowsBatteryObservationReader.DecodeTemperature(true, 0, 4, raw);

        Assert.Equal("Read", observation.Result.Outcome);
        Assert.Equal(raw, observation.RawDeciKelvin);
        Assert.Equal(expectedCelsius, Assert.IsType<double>(observation.Celsius), precision: 6);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    [InlineData(5000u)]
    public void RecordsImplausibleNativeValueWithoutPresentingACelsiusReading(uint raw)
    {
        var observation = WindowsBatteryObservationReader.DecodeTemperature(true, 0, 4, raw);

        Assert.Equal("ImplausibleTemperature", observation.Result.Outcome);
        Assert.Equal(raw, observation.RawDeciKelvin);
        Assert.Null(observation.Celsius);
    }

    [Theory]
    [InlineData(false, 1, 0u, "Unsupported")]
    [InlineData(false, 50, 0u, "Unsupported")]
    [InlineData(false, 2, 0u, "BatteryTagChanged")]
    [InlineData(false, 433, 0u, "BatteryTagChanged")]
    [InlineData(false, 5, 0u, "NativeFailure")]
    [InlineData(true, 0, 3u, "UnexpectedByteCount")]
    [InlineData(true, 0, 8u, "UnexpectedByteCount")]
    public void DistinguishesUnavailableTemperatureFromValidReading(bool success, int error, uint bytes, string outcome)
    {
        var observation = WindowsBatteryObservationReader.DecodeTemperature(success, error, bytes, 2982);

        Assert.Equal(outcome, observation.Result.Outcome);
        Assert.Equal("QueryTemperature", observation.Result.Stage);
        Assert.Equal(success ? (int?)null : error, observation.Result.NativeError);
        Assert.Equal(bytes, observation.Result.ReturnedBytes);
        Assert.Null(observation.RawDeciKelvin);
        Assert.Null(observation.Celsius);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesSignedDischargeAndCapacityUnits(bool relative)
    {
        var observation = WindowsBatteryObservationReader.DecodeStatus(true, 0, 16, 2, 40000, 12000, -12000, relative);

        Assert.Equal("Read", observation.Result.Outcome);
        Assert.Equal(2u, observation.PowerStateFlags);
        Assert.Equal(40000u, observation.Capacity);
        Assert.Equal(12000u, observation.VoltageMillivolts);
        Assert.Equal(-12000, observation.Rate);
        Assert.Equal(relative, observation.CapacityAndRateAreRelative);
    }

    [Fact]
    public void SeparatesUnknownStatusFieldsFromAValidZeroRate()
    {
        var unknown = WindowsBatteryObservationReader.DecodeStatus(true, 0, 16, 1,
            uint.MaxValue, uint.MaxValue, int.MinValue, false);
        var idle = WindowsBatteryObservationReader.DecodeStatus(true, 0, 16, 1, 100, 12000, 0, false);

        Assert.Equal(1u, unknown.PowerStateFlags);
        Assert.Null(unknown.Capacity);
        Assert.Null(unknown.VoltageMillivolts);
        Assert.Null(unknown.Rate);
        Assert.Equal(0, idle.Rate);
    }

    [Theory]
    [InlineData(true, 0, 12u, "UnexpectedByteCount")]
    [InlineData(false, 433, 0u, "BatteryTagChanged")]
    public void NeverExposesFieldsFromAnIncompleteOrStaleStatus(bool success, int error, uint bytes, string outcome)
    {
        var observation = WindowsBatteryObservationReader.DecodeStatus(success, error, bytes, 1, 100, 12000, 0, false);

        Assert.Equal(outcome, observation.Result.Outcome);
        Assert.Null(observation.PowerStateFlags);
        Assert.Null(observation.Capacity);
        Assert.Null(observation.VoltageMillivolts);
        Assert.Null(observation.Rate);
        Assert.Null(observation.CapacityAndRateAreRelative);
    }
}
