using System.Text.Json;
using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class DirectTransitionValidationCommandTests
{
    [Theory]
    [InlineData("manual-targets", "RZ09-0581:1532:02E0:3.01")]
    [InlineData("raw-payload", "RZ09-0581:1532:02E0:4.01")]
    public async Task RunAsync_InvalidConfirmationOrScenarioNeverInvokesRunner(
        string scenario, string confirmation)
    {
        bool invoked = false;
        var output = new StringWriter();
        var command = new DirectTransitionValidationCommand("validate-direct-thermal-transition",
            new HashSet<string>(["manual-targets"], StringComparer.Ordinal), "4.01",
            (_, _) => { invoked = true; return Task.FromResult(((object)new { success = true }, true)); },
            output, _ => new NoopDisposable());

        int exit = await command.RunAsync([scenario, "--confirm-target", confirmation], default);

        Assert.Equal(1, exit);
        Assert.False(invoked);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 2)]
    public async Task RunAsync_RecordsActualProvenanceAndPreservesFailure(bool success, int expected)
    {
        var output = new StringWriter();
        var command = new DirectTransitionValidationCommand("validate-direct-thermal-transition",
            new HashSet<string>(["manual-targets"], StringComparer.Ordinal), "4.01",
            (_, _) => Task.FromResult(((object)new { success, restored = success }, success)),
            output, _ => new NoopDisposable());

        int exit = await command.RunAsync(
            ["manual-targets", "--confirm-target", "RZ09-0581:1532:02E0:4.01"], default);

        Assert.Equal(expected, exit);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.Equal("4.01", document.RootElement.GetProperty("device").GetProperty("biosVersion").GetString());
        Assert.Equal(success, document.RootElement.GetProperty("result").GetProperty("success").GetBoolean());
        Assert.False(document.RootElement.GetProperty("productionAdmitted").GetBoolean());
    }

    private sealed class NoopDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
