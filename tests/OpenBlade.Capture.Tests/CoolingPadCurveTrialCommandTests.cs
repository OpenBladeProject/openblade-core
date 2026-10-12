using System.Text.Json;
using OpenBlade.Capture;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadCurveTrialCommandTests
{
    private static string[] Admission => ["--confirm-target", CoolingPadCurveTrialCommand.ExactTargetConfirmation,
        "--confirm-recovery", CoolingPadCurveTrialCommand.ExactRecoveryConfirmation];

    [Fact]
    public async Task ExactAdmissionAndInitialQuietConfirmationPrecedeAllFactories()
    {
        int calls = 0;
        var command = new CoolingPadCurveTrialCommand((_, _, _, _, _, _) =>
        { calls++; throw new Exception(); }, new StringReader("NO\n"), new StringWriter(), new StringWriter());
        var args = Admission;
        args[1] = CoolingPadHostDemand2200TrialCommand.ExactTargetConfirmation;
        Assert.Equal(1, await command.RunAsync(args, default));
        args = Admission;
        args[3] = "ManualPowerCycleAndVendorRecovery";
        Assert.Equal(1, await command.RunAsync(args, default));
        Assert.Equal(1, await command.RunAsync([.. Admission, "--samples", "10"], default));
        Assert.Equal(2, await command.RunAsync(Admission, default));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task SuccessfulOutputSeparatesPlannedPhysicalAndMeasuredClaims()
    {
        var reader = new CountingReader("YES\nYES\nYES\nYES\nYES\nYES\n");
        var output = new StringWriter();
        var prompts = new StringWriter();
        var command = new CoolingPadCurveTrialCommand(async (first, curve, fixedCheck, quiet, final, token) =>
        {
            Assert.Equal(1, reader.Reads);
            foreach (var observer in new[] { first, curve, fixedCheck, quiet, final }) { Assert.True(await observer(token)); }
            return new()
            {
                FirstDemandPhysicalConfirmed = true,
                CurvePhysicalConfirmed = true,
                AutoPhysicalConfirmed = true,
                RawRestorationConfirmed = true,
                FinalPhysicalConfirmed = true,
            };
        }, reader, output, prompts);
        Assert.Equal(0, await command.RunAsync(Admission, default));
        Assert.Equal(6, reader.Reads);
        Assert.Contains("HyperBoost is inactive", prompts.ToString());
        Assert.Contains("may be modest compared with 2200", prompts.ToString());
        Assert.Contains("program starts no workload", prompts.ToString());
        Assert.Contains("Confirmation pauses hold the last target", prompts.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Equal(10, root.GetProperty("updateSampleCount").GetInt32());
        Assert.True(root.GetProperty("trialSequenceCompleted").GetBoolean());
        Assert.True(root.GetProperty("illustrativeOpenBladePolicy").GetBoolean());
        Assert.True(root.GetProperty("restorationSequencePreviouslyObservedAt2200").GetBoolean());
        foreach (string name in new[] { "productionAdmitted", "measuredRpmConfirmed", "ownershipConfirmed",
            "handbackConfirmed", "stopIsSafeHandback", "synapsePreset", "sourceOriginTimestampAvailable",
            "restorationFromThisDemandPreviouslyValidated", "fullDemandRangePhysicallyValidated", "manualPowerCycleGuaranteesQuiet" })
        { Assert.False(root.GetProperty(name).GetBoolean()); }
    }

    [Fact]
    public async Task InvokedRunnerExceptionDoesNotFabricateSubmissionEvidence()
    {
        var output = new StringWriter();
        var command = new CoolingPadCurveTrialCommand((_, _, _, _, _, _) => throw new IOException(),
            new StringReader("YES\n"), output, new StringWriter());
        Assert.Equal(2, await command.RunAsync(Admission, default));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.True(json.RootElement.GetProperty("submissionStateUnknown").GetBoolean());
        Assert.True(json.RootElement.GetProperty("manualRecoveryRequired").GetBoolean());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task CancellationBeforeBaselineCreatesNoRunner()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var prompts = new StringWriter();
        var command = new CoolingPadCurveTrialCommand((_, _, _, _, _, _) => throw new Exception(),
            new StringReader("YES\n"), new StringWriter(), prompts);
        Assert.Equal(4, await command.RunAsync(Admission, cancellation.Token));
        Assert.Equal("", prompts.ToString());
    }

    private sealed class CountingReader(string content) : StringReader(content)
    {
        internal int Reads;
        public override ValueTask<string?> ReadLineAsync(CancellationToken token)
        { Interlocked.Increment(ref Reads); return base.ReadLineAsync(token); }
    }
}
