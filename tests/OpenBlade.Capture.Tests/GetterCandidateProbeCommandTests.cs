using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class GetterCandidateProbeCommandTests
{
    [Fact]
    public async Task FunctionKeyCommand_RendersSanitizedNonProductionJson()
    {
        var output = new StringWriter();
        var command = new FunctionKeyPrimaryGetterCandidateProbeCommand(
            _ => Task.FromResult(new HidSessionResult<GetterCandidateProbeSnapshot>(
                new("0101"),
                null)),
            output,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync([], default);

        Assert.Equal(0, exitCode);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement root = document.RootElement;
        Assert.Equal("function-key-primary-readback", root.GetProperty("candidate").GetString());
        Assert.Equal("02", root.GetProperty("request").GetProperty("classIdHex").GetString());
        Assert.Equal("86", root.GetProperty("request").GetProperty("commandIdHex").GetString());
        Assert.Equal("0100", root.GetProperty("request").GetProperty("payloadHex").GetString());
        Assert.Equal(2, root.GetProperty("request").GetProperty("expectedPayloadLength").GetInt32());
        Assert.True(root.GetProperty("readOnlySemanticIntent").GetBoolean());
        Assert.True(root.GetProperty("featureReportSubmissionRequired").GetBoolean());
        Assert.False(root.GetProperty("productionAdmitted").GetBoolean());
        Assert.False(root.GetProperty("typedReadbackAdmitted").GetBoolean());
        Assert.False(root.GetProperty("semanticMappingConfirmed").GetBoolean());
        Assert.True(root.GetProperty("transportSuccess").GetBoolean());
        Assert.Equal("0101", root.GetProperty("responsePayloadHex").GetString());
        Assert.DoesNotContain("transaction", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("devicePath", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("serial", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("envelope", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GamingModeCommand_UsesItsOwnFixedMetadata()
    {
        var output = new StringWriter();
        var command = new GamingModeGetterCandidateProbeCommand(
            _ => Task.FromResult(new HidSessionResult<GetterCandidateProbeSnapshot>(
                new("00000000"),
                null)),
            output,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync([], default);

        Assert.Equal(0, exitCode);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        JsonElement root = document.RootElement;
        Assert.Equal("gaming-mode-readback", root.GetProperty("candidate").GetString());
        Assert.Equal("00", root.GetProperty("request").GetProperty("classIdHex").GetString());
        Assert.Equal("88", root.GetProperty("request").GetProperty("commandIdHex").GetString());
        Assert.Equal("00000000", root.GetProperty("request").GetProperty("payloadHex").GetString());
        Assert.Equal(4, root.GetProperty("request").GetProperty("expectedPayloadLength").GetInt32());
    }

    [Theory]
    [InlineData("02/86")]
    [InlineData("--class")]
    [InlineData("--payload=0100")]
    [InlineData("0100")]
    public async Task Commands_RejectEveryRawOrSelectorArgument(string argument)
    {
        bool invoked = false;
        var output = new StringWriter();
        var command = new FunctionKeyPrimaryGetterCandidateProbeCommand(
            _ =>
            {
                invoked = true;
                return Task.FromResult(default(
                    HidSessionResult<GetterCandidateProbeSnapshot>));
            },
            output,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunAsync([argument], default);

        Assert.Equal(GetterCandidateProbeCommand.UsageExitCode, exitCode);
        Assert.False(invoked);
        Assert.Equal(
            "Usage: OpenBlade.Capture probe-function-key-primary-getter" +
            Environment.NewLine,
            output.ToString());
    }

    private sealed class NoopDisposable : IDisposable
    {
        internal static NoopDisposable Instance { get; } = new();
        public void Dispose() { }
    }
}
