using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class PerformanceFanDiscriminatorQueryCommandTests
{
    [Fact]
    public async Task RunAsync_UsesSuppliedDeviceIdentity()
    {
        var snapshot = new PerformanceFanDiscriminatorSnapshot([]);
        using var output = new StringWriter();
        var command = new PerformanceFanDiscriminatorQueryCommand(
            _ => Task.FromResult(
                new HidSessionResult<PerformanceFanDiscriminatorSnapshot>(snapshot, null)),
            output,
            new DeviceIdentity("RZ09-0528", 0x1532, 0x02C6, "2.02"),
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync([], default);

        Assert.Equal(0, exitCode);
        Assert.Contains("\"model\": \"RZ09-0528\"", output.ToString());
        Assert.Contains("\"productId\": \"02C6\"", output.ToString());
        Assert.Contains("\"biosVersion\": \"2.02\"", output.ToString());
    }

    [Fact]
    public async Task RunAsync_RendersSanitizedStructuredJsonForFixedQueries()
    {
        var snapshot = new PerformanceFanDiscriminatorSnapshot(
        [
            new("07/8F power control", "00", "00"),
            new("0D/82 class1 thermal1", "01010000", "01010000"),
            new("0D/82 class1 thermal2", "01020000", "01020000"),
            new("0D/87 custom CPU component", "010100", "010101"),
            new("0D/87 custom GPU component", "010200", "010202"),
            new("0F/98 startup animation candidate", "00", "00"),
            new("00/87 MCU version", "00000000", "01020304"),
        ]);
        var output = new StringWriter();
        var command = new PerformanceFanDiscriminatorQueryCommand(
            _ => Task.FromResult(
                new HidSessionResult<PerformanceFanDiscriminatorSnapshot>(snapshot, null)),
            output,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync([], default);

        Assert.Equal(0, exitCode);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("RZ09-0581", root.GetProperty("device").GetProperty("model").GetString());
        Assert.Equal("1532", root.GetProperty("device").GetProperty("vendorId").GetString());
        Assert.Equal("02E0", root.GetProperty("device").GetProperty("productId").GetString());
        Assert.Equal("3.01", root.GetProperty("device").GetProperty("biosVersion").GetString());
        Assert.True(root.GetProperty("readOnly").GetBoolean());
        Assert.False(root.GetProperty("productionAdmitted").GetBoolean());
        Assert.True(root.GetProperty("success").GetBoolean());
        JsonElement[] responses = root.GetProperty("responses").EnumerateArray().ToArray();
        Assert.Equal(
            [
                "07/8F power control",
                "0D/82 class1 thermal1",
                "0D/82 class1 thermal2",
                "0D/87 custom CPU component",
                "0D/87 custom GPU component",
                "0F/98 startup animation candidate",
                "00/87 MCU version",
            ],
            responses.Select(response => response.GetProperty("operation").GetString()!).ToArray());
        Assert.Equal(
            ["00", "01010000", "01020000", "010100", "010200", "00", "00000000"],
            responses.Select(response =>
                response.GetProperty("requestPayloadHex").GetString()!).ToArray());
        Assert.All(responses, response =>
            Assert.Equal("02", response.GetProperty("expectedStatusHex").GetString()));
        Assert.Equal(8, responses[1].GetProperty("responsePayloadHex").GetString()!.Length);
        Assert.DoesNotContain("transaction", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("devicePath", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("requestEnvelope", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("0D/8E")]
    [InlineData("8E")]
    [InlineData("00")]
    public async Task RunAsync_RejectsEveryRawOrSelectorArgument(string argument)
    {
        bool invoked = false;
        var output = new StringWriter();
        var command = new PerformanceFanDiscriminatorQueryCommand(
            _ =>
            {
                invoked = true;
                return Task.FromResult(
                    default(HidSessionResult<PerformanceFanDiscriminatorSnapshot>));
            },
            output,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync([argument], default);

        Assert.Equal(PerformanceFanDiscriminatorQueryCommand.UsageExitCode, exitCode);
        Assert.False(invoked);
        Assert.Equal(
            "Usage: OpenBlade.Capture query-performance-fan-discriminator" +
            Environment.NewLine,
            output.ToString());
    }

    private sealed class NoopDisposable : IDisposable
    {
        internal static NoopDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}
