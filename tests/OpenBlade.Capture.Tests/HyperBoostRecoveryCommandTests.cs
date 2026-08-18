using System.Text.Json;
using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class HyperBoostRecoveryCommandTests
{
    [Fact]
    public async Task RunQueryAsync_EmitsSemanticReadOnlyResult()
    {
        int queryCalls = 0;
        var output = new StringWriter();
        var command = new HyperBoostRecoveryCommand(
            _ =>
            {
                queryCalls++;
                return Task.FromResult(Result(HyperBoostRecoveryState.HyperBoostActive));
            },
            _ => throw new InvalidOperationException(),
            TextReader.Null,
            output,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunQueryAsync([], default);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, queryCalls);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        Assert.False(document.RootElement.GetProperty("productionAdmitted").GetBoolean());
        Assert.Equal(
            "hyperBoostActive",
            document.RootElement.GetProperty("result")
                .GetProperty("initialState").GetString());
        Assert.DoesNotContain("payload", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transaction", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("devicePath", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunRestoreAsync_RequiresArgumentAndInteractiveConfirmation()
    {
        int restoreCalls = 0;
        var output = new StringWriter();
        var command = new HyperBoostRecoveryCommand(
            _ => throw new InvalidOperationException(),
            _ =>
            {
                restoreCalls++;
                return Task.FromResult(new HyperBoostRecoveryResult(
                    HyperBoostRecoveryState.HyperBoostActive,
                    RestoreAttempted: true,
                    BalancedRestorationConfirmed: true,
                    ManualRecoveryRequired: false,
                    HyperBoostRecoveryFailure.None));
            },
            new StringReader(HyperBoostRecoveryCommand.InteractiveApproval),
            output,
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunRestoreAsync(
            ["--confirm-target", HyperBoostRecoveryCommand.RestoreConfirmation],
            default);

        Assert.Equal(0, exitCode);
        Assert.Equal(1, restoreCalls);
        string json = ExtractJson(output.ToString());
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.True(
            document.RootElement.GetProperty("result")
                .GetProperty("balancedRestorationConfirmed").GetBoolean());
    }

    [Fact]
    public async Task RunRestoreAsync_InexactApprovalPerformsNoOperation()
    {
        bool restored = false;
        var command = new HyperBoostRecoveryCommand(
            _ => throw new InvalidOperationException(),
            _ =>
            {
                restored = true;
                return Task.FromResult(Result(HyperBoostRecoveryState.HyperBoostActive));
            },
            new StringReader("yes"),
            new StringWriter(),
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunRestoreAsync(
            ["--confirm-target", HyperBoostRecoveryCommand.RestoreConfirmation],
            default);

        Assert.Equal(HyperBoostRecoveryCommand.ApprovalRejectedExitCode, exitCode);
        Assert.False(restored);
    }

    [Fact]
    public async Task RunRestoreAsync_InexactArgumentsDoNotPromptOrOperate()
    {
        bool restored = false;
        var command = new HyperBoostRecoveryCommand(
            _ => throw new InvalidOperationException(),
            _ =>
            {
                restored = true;
                return Task.FromResult(Result(HyperBoostRecoveryState.HyperBoostActive));
            },
            new StringReader(HyperBoostRecoveryCommand.InteractiveApproval),
            new StringWriter(),
            _ => NoopDisposable.Instance);

        int exitCode = await command.RunRestoreAsync(
            ["--confirm-target", "yes"],
            default);

        Assert.Equal(HyperBoostRecoveryCommand.UsageExitCode, exitCode);
        Assert.False(restored);
    }

    private static HyperBoostRecoveryResult Result(HyperBoostRecoveryState state) => new(
        state,
        RestoreAttempted: false,
        BalancedRestorationConfirmed:
            state == HyperBoostRecoveryState.BalancedAutomatic,
        ManualRecoveryRequired: false,
        HyperBoostRecoveryFailure.None);

    private static string ExtractJson(string output)
    {
        int start = output.IndexOf("{\r\n", StringComparison.Ordinal);
        return start >= 0 ? output[start..] : output;
    }

    private sealed class NoopDisposable : IDisposable
    {
        internal static NoopDisposable Instance { get; } = new();

        public void Dispose() { }
    }
}
