using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class WindowsBatteryObservationCommandTests
{
    [Theory]
    [InlineData("0", "5", "idle-light")]
    [InlineData("901", "5", "idle-light")]
    [InlineData("30", "0", "idle-light")]
    [InlineData("30", "61", "idle-light")]
    [InlineData("30", "5", "guess")]
    [InlineData("1e2", "5", "idle-light")]
    public void RejectsUnboundedOrAmbiguousRecording(string seconds, string interval, string workload)
    {
        Assert.Throws<ArgumentException>(() => WindowsBatteryObservationCommand.Parse(
            [WindowsBatteryObservationCommand.Name, "capture.jsonl", seconds, interval, workload]));
    }

    [Fact]
    public async Task LimitsWorkerOutputAndPreservesValidInput()
    {
        using StringReader valid = new("1234");
        Assert.Equal("1234", await WindowsBatteryObservationCommand.ReadBoundedAsync(valid, 4, CancellationToken.None));
        using StringReader tooLarge = new("12345");
        await Assert.ThrowsAsync<InvalidDataException>(() => WindowsBatteryObservationCommand.ReadBoundedAsync(tooLarge, 4, CancellationToken.None));
    }

    [Fact]
    public void RejectsIncompleteWorkerSnapshot()
    {
        Assert.Throws<InvalidDataException>(() => WindowsBatteryObservationCommand.ParseSnapshot("{}"));
        Assert.Throws<InvalidDataException>(() => WindowsBatteryObservationCommand.ParseSnapshot(
            "{\"Enumeration\":{\"Outcome\":\"Read\"},\"Batteries\":[null]}"));
    }

    [Fact]
    public void UnsupportedOrPartialEnumerationDoesNotContinueRecording()
    {
        var battery = new WindowsBatteryObservation(1, true, new("Read", "QueryInformation"),
            new(new("Read", "QueryTemperature"), 2982, 25.05), new(new("Read", "QueryStatus")));
        Assert.True(WindowsBatteryObservationCommand.HasTemperature(new(new("Read", "EnumerateInterfaces"), [battery])));
        Assert.False(WindowsBatteryObservationCommand.HasTemperature(new(new("InterfaceLimitReached", "EnumerateInterfaces"), [battery])));
        Assert.False(WindowsBatteryObservationCommand.HasTemperature(new(new("Read", "EnumerateInterfaces"),
            [battery with { Temperature = new(new("Unsupported", "QueryTemperature", 1)) }])));
    }
}
