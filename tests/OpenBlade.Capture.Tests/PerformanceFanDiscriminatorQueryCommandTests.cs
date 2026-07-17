using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class PerformanceFanDiscriminatorQueryCommandTests
{
    [Fact]
    public async Task RunAsync_RendersSanitizedStructuredJsonForFixedQueries()
    {
        var snapshot = new PerformanceFanDiscriminatorSnapshot(
        [
            new("0D/8E", "00", "00"),
            new("0D/8F", "00", "01"),
            new("0D/80", new string('0', 160), new string('0', 160)),
            new("0D/89", "0000", "0202"),
            new("0D/8B", "0000", "0201"),
            new("0D/8A", "0000", "0206"),
            new("0F/98", "00", "00"),
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
        Assert.Equal(["0D/8E", "0D/8F", "0D/80", "0D/89", "0D/8B", "0D/8A", "0F/98"],
            responses.Select(response => response.GetProperty("operation").GetString()!).ToArray());
        Assert.Equal(["00", "00", new string('0', 160), "0000", "0000", "0000", "00"],
            responses.Select(response =>
                response.GetProperty("requestPayloadHex").GetString()!).ToArray());
        Assert.All(responses, response =>
            Assert.Equal("02", response.GetProperty("expectedStatusHex").GetString()));
        Assert.Equal(160, responses[2].GetProperty("responsePayloadHex").GetString()!.Length);
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
