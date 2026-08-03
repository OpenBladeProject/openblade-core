namespace OpenBlade.Capture.Tests;

public sealed class LampArrayDiscoveryCommandTests
{
    [Fact]
    public async Task RunAsync_PrintsSanitizedExactDeviceGeometry()
    {
        using var writer = new StringWriter();
        var snapshot = new LampArrayProbeSnapshot(
            1,
            [
                new LampArrayDeviceSnapshot(
                    0x1532,
                    0x02C6,
                    "Keyboard",
                    1,
                    IsAvailable: true,
                    IsConnected: true,
                    IsEnabled: true,
                    SupportsVirtualKeys: true,
                    BrightnessLevel: 0.5,
                    MinUpdateIntervalMicroseconds: 16_667,
                    BoundingBoxWidth: 0.3f,
                    BoundingBoxHeight: 0.1f,
                    BoundingBoxDepth: 0.01f,
                    Lamps:
                    [
                        new(
                            0,
                            0.01f,
                            0.02f,
                            0.03f,
                            "Control",
                            255,
                            255,
                            255,
                            1,
                            1000),
                    ],
                    VirtualKeys:
                    [
                        new("A", [0]),
                    ]),
            ],
            null);
        var command = new LampArrayDiscoveryCommand(
            new StubProbe(snapshot),
            writer);

        int exitCode = await command.RunAsync([], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("\"vendorId\": 5426", writer.ToString());
        Assert.Contains("\"productId\": 710", writer.ToString());
        Assert.Contains("\"kind\": \"Keyboard\"", writer.ToString());
        Assert.Contains("\"key\": \"A\"", writer.ToString());
        Assert.Matches(
            "\"geometrySha256\": \"[0-9A-F]{64}\"",
            writer.ToString());
        Assert.DoesNotContain("deviceId", writer.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_RejectsUnexpectedArguments()
    {
        using var writer = new StringWriter();
        var command = new LampArrayDiscoveryCommand(
            new StubProbe(new(0, [], null)),
            writer);

        int exitCode = await command.RunAsync(["unexpected"], CancellationToken.None);

        Assert.Equal(LampArrayDiscoveryCommand.UsageExitCode, exitCode);
        Assert.Contains(
            "probe-rz09-0528-02c6-lamp-array",
            writer.ToString());
    }

    [Fact]
    public async Task RunAsync_DistinguishesFailureFromNoExactDevice()
    {
        using var failureWriter = new StringWriter();
        using var absentWriter = new StringWriter();
        var failure = new LampArrayDiscoveryCommand(
            new StubProbe(new(0, [], "FileNotFoundException: unavailable")),
            failureWriter);
        var absent = new LampArrayDiscoveryCommand(
            new StubProbe(new(2, [], null)),
            absentWriter);

        int failureExit = await failure.RunAsync([], CancellationToken.None);
        int absentExit = await absent.RunAsync([], CancellationToken.None);

        Assert.Equal(LampArrayDiscoveryCommand.ProbeFailureExitCode, failureExit);
        Assert.Equal(LampArrayDiscoveryCommand.ExactDeviceNotFoundExitCode, absentExit);
    }

    private sealed class StubProbe(LampArrayProbeSnapshot snapshot)
        : ILampArrayDiscoveryProbe
    {
        public Task<LampArrayProbeSnapshot> RunAsync(
            CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }
}
