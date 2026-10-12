using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadHostDemand2200TrialCommandTests
{
    private static string[] Admission => ["--confirm-target", CoolingPadHostDemand2200TrialCommand.ExactTargetConfirmation,
        "--confirm-recovery", CoolingPadHostDemand2200TrialCommand.ExactRecoveryConfirmation];

    [Theory]
    [InlineData("ManualPowerCycleAndVendorRecovery")]
    [InlineData("manualpowercycleonly")]
    [InlineData("")]
    public async Task ExactAdmissionRejectsVendorRecoveryAndAdditionalArgumentsBeforeRunner(string recovery)
    {
        int calls = 0;
        var command = new CoolingPadHostDemand2200TrialCommand((_, _, _, _, _) =>
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
    [InlineData("NO\n")]
    [InlineData("yes\n")]
    [InlineData("")]
    public async Task InitialPhysicalRefusalOrEofPreventsNativeRunnerConstruction(string response)
    {
        int calls = 0;
        var output = new StringWriter();
        var command = new CoolingPadHostDemand2200TrialCommand((_, _, _, _, _) =>
        { calls++; throw new InvalidOperationException(); }, new StringReader(response), output, new StringWriter());
        Assert.Equal(2, await command.RunAsync(Admission, default));
        Assert.Equal(0, calls);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("nativeRunnerInvoked").GetBoolean());
        Assert.False(json.RootElement.GetProperty("submissionStateUnknown").GetBoolean());
        Assert.False(json.RootElement.GetProperty("manualRecoveryRequired").GetBoolean());
        Assert.False(json.RootElement.GetProperty("trialSequenceCompleted").GetBoolean());
        Assert.Equal("unconfirmed", json.RootElement.GetProperty("initialPhysicalState").GetString());
    }

    [Fact]
    public async Task InitialQuietDemandResponseAndRequestedQuietRemainSeparateInPromptsAndOutput()
    {
        var input = new CountingReader("YES\nYES\nYES\nYES\nYES\n");
        var output = new StringWriter();
        var prompts = new StringWriter();
        int calls = 0;
        var command = new CoolingPadHostDemand2200TrialCommand(async (demandCheck, fixedCheck, autoCheck, finalCheck, token) =>
        {
            Assert.Equal(1, input.ReadCount);
            calls++;
            Assert.True(await demandCheck(token));
            Assert.True(await fixedCheck(token));
            Assert.True(await autoCheck(token));
            Assert.True(await finalCheck(token));
            return new()
            {
                RawBaseline = "0000",
                RawCandidate = "0300",
                RawRestored = "0000",
                HostDemandPhysicalConfirmed = true,
                FixedPhysicalConfirmed = true,
                AutoPhysicalConfirmed = true,
                RawRestorationConfirmed = true,
                FinalPhysicalConfirmed = true,
            };
        }, input, output, prompts);
        Assert.Equal(0, await command.RunAsync(Admission, default));
        Assert.Equal(1, calls);
        Assert.Equal(5, input.ReadCount);
        Assert.Contains("requested preferred quiet Auto", prompts.ToString());
        Assert.Contains("HyperBoost is inactive", prompts.ToString());
        Assert.Contains("perceived response, not measured RPM", prompts.ToString());
        Assert.Contains("does not guarantee quietness", prompts.ToString());
        Assert.DoesNotContain("returned to the quiet physical baseline", prompts.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Equal("operator-confirmed-quiet-normal-green-no-red-sleep-HyperBoost-inactive", root.GetProperty("initialPhysicalState").GetString());
        Assert.Equal("preferred-quiet-Auto-normal-green-no-red-sleep", root.GetProperty("requestedPhysicalTarget").GetString());
        Assert.True(root.GetProperty("preferredAutoPhysicalConfirmed").GetBoolean());
        Assert.True(root.GetProperty("preferredAutoFinalPhysicalConfirmed").GetBoolean());
        Assert.True(root.GetProperty("trialSequenceCompleted").GetBoolean());
        Assert.True(root.GetProperty("rawControlStateRestored").GetBoolean());
        Assert.True(root.GetProperty("biosIsCaptureProvenanceOnly").GetBoolean());
        foreach (string name in new[] { "rawControlStateIsFanReadback", "fanStateConfirmed", "ownershipConfirmed",
            "handbackConfirmed", "productionAdmitted", "automaticRecovery", "measuredRpmConfirmed",
            "compoundRestorationPreviouslyValidated", "manualPowerCycleGuaranteesQuiet" })
        { Assert.False(root.GetProperty(name).GetBoolean()); }
        Assert.Equal("ManualPowerCycleOnly", root.GetProperty("recoveryAcknowledgement").GetString());
        Assert.Equal(9, root.GetProperty("capturedSequence").GetArrayLength());
    }

    [Fact]
    public async Task WrapperPreservesActualNegativeObservationAndRunnerFailure()
    {
        var output = new StringWriter();
        var prompts = new StringWriter();
        var command = new CoolingPadHostDemand2200TrialCommand(async (_, fixedCheck, _, _, token) =>
        {
            Assert.False(await fixedCheck(token));
            return new() { FixedAttempted = true, FailurePhase = "fixedPhysicalConfirmation", Failure = "OperatorRejected" };
        }, new StringReader("YES\nNO\n"), output, prompts);
        Assert.Equal(2, await command.RunAsync(Admission, default));
        Assert.DoesNotContain("PREFERRED_AUTO_CONFIRM:", prompts.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("OperatorRejected", json.RootElement.GetProperty("result").GetProperty("failure").GetString());
        Assert.True(json.RootElement.GetProperty("result").GetProperty("manualRecoveryRequired").GetBoolean());
        Assert.False(json.RootElement.GetProperty("trialSequenceCompleted").GetBoolean());
    }

    [Fact]
    public async Task LateInitialYesCannotInvokeRunnerAfterTimeout()
    {
        var late = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var output = new StringWriter();
        var command = new CoolingPadHostDemand2200TrialCommand((_, _, _, _, _) =>
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
        var command = new CoolingPadHostDemand2200TrialCommand((_, _, _, _, _) =>
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
        Assert.False(root.GetProperty("trialSequenceCompleted").GetBoolean());
    }

    [Fact]
    public async Task AlreadyCanceledAdmissionCannotInvokeRunnerOrAskForConfirmation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int calls = 0;
        var prompts = new StringWriter();
        var command = new CoolingPadHostDemand2200TrialCommand((_, _, _, _, _) =>
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
