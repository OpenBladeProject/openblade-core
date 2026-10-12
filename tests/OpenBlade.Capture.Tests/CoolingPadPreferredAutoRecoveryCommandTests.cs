using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadPreferredAutoRecoveryCommandTests
{
    private static string[] Admission => ["--confirm-target", CoolingPadPreferredAutoRecoveryCommand.ExactTargetConfirmation,
        "--confirm-recovery", CoolingPadPreferredAutoRecoveryCommand.ExactRecoveryConfirmation];

    [Theory]
    [InlineData("ManualPowerCycleAndVendorRecovery")]
    [InlineData("manualpowercycleonly")]
    [InlineData("")]
    public async Task ExactAdmissionRejectsVendorRecoveryAndAdditionalArgumentsBeforeRunner(string recovery)
    {
        int calls = 0;
        var command = new CoolingPadPreferredAutoRecoveryCommand((_, _, _, _) =>
        { calls++; throw new InvalidOperationException(); }, new StringReader("YES"), new StringWriter(), new StringWriter());
        var args = Admission;
        args[3] = recovery;
        Assert.Equal(1, await command.RunAsync(args, default));
        Assert.Equal(1, await command.RunAsync([.. Admission, "--payload", "000600"], default));
        args = Admission;
        args[1] = CoolingPadFanExploratoryTrialCommand.ExactTargetConfirmation;
        Assert.Equal(1, await command.RunAsync(args, default));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("NO\n", false)]
    [InlineData("NO\n", true)]
    [InlineData("yes\n", false)]
    [InlineData("yes\n", true)]
    [InlineData("", false)]
    [InlineData("", true)]
    public async Task InitialPhysicalRefusalOrEofPreventsNativeRunnerConstruction(string response, bool fromHostDemand)
    {
        int calls = 0;
        var output = new StringWriter();
        var command = new CoolingPadPreferredAutoRecoveryCommand((_, _, _, _) =>
        { calls++; throw new InvalidOperationException(); }, new StringReader(response), output, new StringWriter());
        var admission = Admission;
        if (fromHostDemand) { admission[1] = CoolingPadPreferredAutoRecoveryCommand.ExactHostDemand1500TargetConfirmation; }
        Assert.Equal(2, await command.RunAsync(admission, default));
        Assert.Equal(0, calls);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("nativeRunnerInvoked").GetBoolean());
        Assert.False(json.RootElement.GetProperty("submissionStateUnknown").GetBoolean());
        Assert.False(json.RootElement.GetProperty("manualRecoveryRequired").GetBoolean());
        Assert.False(json.RootElement.GetProperty("requestedRecoverySequenceCompleted").GetBoolean());
        Assert.Equal("unconfirmed", json.RootElement.GetProperty("initialPhysicalState").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartingStateAndRequestedQuietAutoRemainSeparateInPromptsAndOutput(bool fromHostDemand)
    {
        var input = new CountingReader("YES\nYES\nYES\nYES\n");
        var output = new StringWriter();
        var prompts = new StringWriter();
        int calls = 0;
        var command = new CoolingPadPreferredAutoRecoveryCommand(async (fixedCheck, autoCheck, finalCheck, token) =>
        {
            Assert.Equal(1, input.ReadCount);
            calls++;
            Assert.True(await fixedCheck(token));
            Assert.True(await autoCheck(token));
            Assert.True(await finalCheck(token));
            return new()
            {
                RawBaseline = "0000",
                RawCandidate = "0300",
                RawRestored = "0000",
                FixedPhysicalConfirmed = true,
                AutoPhysicalConfirmed = true,
                RawRestorationConfirmed = true,
                FinalPhysicalConfirmed = true,
            };
        }, input, output, prompts, initialConfirmationTimeout: TimeSpan.FromMinutes(10));
        var admission = Admission;
        if (fromHostDemand) { admission[1] = CoolingPadPreferredAutoRecoveryCommand.ExactHostDemand1500TargetConfirmation; }
        Assert.Equal(0, await command.RunAsync(admission, default));
        Assert.Equal(1, calls);
        Assert.Equal(4, input.ReadCount);
        Assert.Contains("requested preferred quiet Auto", prompts.ToString());
        Assert.DoesNotContain("returned to the quiet physical baseline", prompts.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Contains("finite: 10 minutes", prompts.ToString());
        if (fromHostDemand)
        {
            Assert.Contains("INITIAL_CURRENT_FAN_CONFIRM", prompts.ToString());
            Assert.Contains("HyperBoost inactive", prompts.ToString());
            Assert.DoesNotContain("currently runs Medium", prompts.ToString());
            Assert.Equal("operator-confirmed-normal-current-fan-green-no-red-sleep-HyperBoost-inactive",
                root.GetProperty("initialPhysicalState").GetString());
            Assert.Equal(1500, root.GetProperty("previousConfiguredDemandRpm").GetInt32());
        }
        else
        {
            Assert.Contains("INITIAL_MEDIUM_CONFIRM", prompts.ToString());
            Assert.Contains("currently runs Medium", prompts.ToString());
            Assert.Equal("operator-confirmed-Medium-normal-green-no-red-sleep", root.GetProperty("initialPhysicalState").GetString());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("previousConfiguredDemandRpm").ValueKind);
        }
        Assert.False(root.GetProperty("previousConfiguredDemandIsFirmwareReadback").GetBoolean());
        Assert.False(root.GetProperty("measuredRpmConfirmed").GetBoolean());
        Assert.Equal("preferred-quiet-Auto-normal-green-no-red-sleep", root.GetProperty("requestedPhysicalTarget").GetString());
        Assert.True(root.GetProperty("preferredAutoPhysicalConfirmed").GetBoolean());
        Assert.True(root.GetProperty("preferredAutoFinalPhysicalConfirmed").GetBoolean());
        Assert.True(root.GetProperty("requestedRecoverySequenceCompleted").GetBoolean());
        Assert.True(root.GetProperty("rawControlStateRestored").GetBoolean());
        Assert.True(root.GetProperty("biosIsCaptureProvenanceOnly").GetBoolean());
        foreach (string name in new[] { "rawControlStateIsFanReadback", "fanStateConfirmed", "ownershipConfirmed",
            "handbackConfirmed", "productionAdmitted", "automaticRecovery" })
        { Assert.False(root.GetProperty(name).GetBoolean()); }
        Assert.Equal("ManualPowerCycleOnly", root.GetProperty("recoveryAcknowledgement").GetString());
        Assert.Equal(7, root.GetProperty("capturedSequence").GetArrayLength());
    }

    [Fact]
    public async Task WrapperPreservesActualNegativeObservationAndRunnerFailure()
    {
        var output = new StringWriter();
        var prompts = new StringWriter();
        var command = new CoolingPadPreferredAutoRecoveryCommand(async (fixedCheck, _, _, token) =>
        {
            Assert.False(await fixedCheck(token));
            return new() { FixedAttempted = true, FailurePhase = "fixedPhysicalConfirmation", Failure = "OperatorRejected" };
        }, new StringReader("YES\nNO\n"), output, prompts);
        Assert.Equal(2, await command.RunAsync(Admission, default));
        Assert.DoesNotContain("PREFERRED_AUTO_CONFIRM:", prompts.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("OperatorRejected", json.RootElement.GetProperty("result").GetProperty("failure").GetString());
        Assert.True(json.RootElement.GetProperty("result").GetProperty("manualRecoveryRequired").GetBoolean());
        Assert.False(json.RootElement.GetProperty("requestedRecoverySequenceCompleted").GetBoolean());
    }

    [Fact]
    public async Task LateInitialYesCannotInvokeRunnerAfterTimeout()
    {
        var late = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var output = new StringWriter();
        var command = new CoolingPadPreferredAutoRecoveryCommand((_, _, _, _) =>
        { calls++; throw new InvalidOperationException(); }, new LateReader(late.Task), output, new StringWriter(),
            initialConfirmationTimeout: TimeSpan.FromMilliseconds(50));
        Assert.Equal(2, await command.RunAsync(Admission, default).WaitAsync(TimeSpan.FromSeconds(2)));
        late.SetResult("YES");
        await PendingDisposalRegistry.Shared.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, calls);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("OperatorTimeout", json.RootElement.GetProperty("result").GetProperty("failure").GetString());
    }

    [Fact]
    public async Task InvokedRunnerWithoutResultReportsUnknownSubmissionAndManualAttention()
    {
        int calls = 0;
        var output = new StringWriter();
        var command = new CoolingPadPreferredAutoRecoveryCommand((_, _, _, _) =>
        {
            calls++;
            throw new IOException("No returned command evidence.");
        }, new StringReader("YES\n"), output, new StringWriter());
        Assert.Equal(2, await command.RunAsync(Admission, default));
        Assert.Equal(1, calls);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.True(root.GetProperty("runnerResultUnavailable").GetBoolean());
        Assert.True(root.GetProperty("submissionStateUnknown").GetBoolean());
        Assert.True(root.GetProperty("manualRecoveryRequired").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("result").ValueKind);
        Assert.Equal("runnerResultUnavailable", root.GetProperty("failurePhase").GetString());
        Assert.Equal("NativeIoFailure", root.GetProperty("failure").GetString());
        Assert.False(root.GetProperty("requestedRecoverySequenceCompleted").GetBoolean());
    }

    [Theory]
    [InlineData("query", false)]
    [InlineData("acquire", true)]
    [InlineData("fixed", true)]
    [InlineData("auto", true)]
    [InlineData("restore", true)]
    [InlineData("acknowledged", false)]
    public async Task ReturnedWriteAttemptsWithoutAcknowledgementRemainUnknown(string stage, bool expectedUnknown)
    {
        var result = new CoolingPadFanExploratoryTrialResult
        {
            ModeAcquireAttempted = stage is "acquire" or "acknowledged",
            ModeAcquireAcknowledged = stage == "acknowledged",
            FixedAttempted = stage == "fixed",
            AutoAttempted = stage == "auto",
            ModeRestoreAttempted = stage == "restore",
            FailurePhase = stage,
            Failure = "NativeIoFailure",
        };
        var output = new StringWriter();
        var command = new CoolingPadPreferredAutoRecoveryCommand((_, _, _, _) => Task.FromResult(result),
            new StringReader("YES\n"), output, new StringWriter());
        var admission = Admission;
        admission[1] = CoolingPadPreferredAutoRecoveryCommand.ExactHostDemand1500TargetConfirmation;
        Assert.Equal(2, await command.RunAsync(admission, default));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(expectedUnknown, json.RootElement.GetProperty("submissionStateUnknown").GetBoolean());
        Assert.False(json.RootElement.GetProperty("runnerResultUnavailable").GetBoolean());
        Assert.Equal(JsonValueKind.Object, json.RootElement.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task AlreadyCanceledAdmissionCannotInvokeRunnerOrAskForConfirmation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int calls = 0;
        var prompts = new StringWriter();
        var command = new CoolingPadPreferredAutoRecoveryCommand((_, _, _, _) =>
        { calls++; throw new InvalidOperationException(); }, new StringReader("YES"), new StringWriter(), prompts);
        Assert.Equal(4, await command.RunAsync(Admission, cancellation.Token));
        Assert.Equal(0, calls);
        Assert.Equal("", prompts.ToString());
    }

    private sealed class CountingReader(string content) : StringReader(content)
    {
        internal int ReadCount;
        public override ValueTask<string?> ReadLineAsync(CancellationToken token)
        {
            Interlocked.Increment(ref ReadCount);
            return base.ReadLineAsync(token);
        }
    }
    private sealed class LateReader(Task<string?> response) : TextReader
    {
        public override ValueTask<string?> ReadLineAsync(CancellationToken token) => new(response);
    }
}
