using System.Text.Json;
using OpenBlade.Capture;
using OpenBlade.Protocol;

namespace OpenBlade.Capture.Tests;

public sealed class CoolingPadFanExploratoryTrialCommandTests
{
    private static string[] Admission =>
        ["--confirm-target", CoolingPadFanExploratoryTrialCommand.ExactTargetConfirmation,
            "--confirm-recovery", CoolingPadFanExploratoryTrialCommand.ExactRecoveryConfirmation];

    public static IEnumerable<object[]> InvalidArguments()
    {
        yield return [Array.Empty<string>()];
        yield return [Admission[..2]];
        yield return [new[] { "--confirm-target", "RZ09-05289EN4:1532:0F43:0200:Medium2200-Auto", "--confirm-recovery" }];
        yield return [new[] { "--confirm-target", "RZ09-05289EN4:1532:0F43:0200:Medium2200-Auto", "--confirm-recovery", "NO" }];
        yield return [new[] { "--CONFIRM-TARGET", Admission[1], Admission[2], Admission[3] }];
        yield return [new[] { Admission[0], "RZ09-05289EN4:1532:0F43:0200:Low1500-Auto", Admission[2], Admission[3] }];
        yield return [new[] { Admission[0], "RZ09-05289EN4:1532:02C6:0200:Medium2200-Auto", Admission[2], Admission[3] }];
        yield return [new[] { Admission[0], "RZ09-05289EN4:1532:0F43:0201:Medium2200-Auto", Admission[2], Admission[3] }];
        yield return [new[] { Admission[0], Admission[1], Admission[2], "manualpowercycleandvendorrecovery" }];
        yield return [new[] { Admission[2], Admission[3], Admission[0], Admission[1] }];
        yield return [Admission.Concat(new[] { "--report-id", "05" }).ToArray()];
        yield return [Admission.Concat(new[] { "--payload", "01013C" }).ToArray()];
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task InvalidAdmission_DoesNotInvokeRunOrProduceHardwareResult(string[] arguments)
    {
        int calls = 0;
        var output = new StringWriter();
        var prompts = new StringWriter();
        var command = new CoolingPadFanExploratoryTrialCommand((_, _, _, _) =>
        {
            calls++;
            throw new InvalidOperationException("No runner or native factory may be constructed.");
        }, new StringReader("YES\nYES\nYES\n"), output, prompts);
        Assert.Equal(1, await command.RunAsync(arguments, default));
        Assert.Equal(0, calls);
        Assert.Equal("", output.ToString());
        Assert.Contains("Usage:", prompts.ToString());
    }

    [Fact]
    public async Task ExactAdmission_RunsSevenFixedCommandsWithPromptsSeparateFromJson()
    {
        var session = new Session();
        var output = new StringWriter();
        var prompts = new StringWriter();
        var command = Command(session, new StringReader(" YES \nYES\nYES\n"), output, prompts);
        Assert.Equal(0, await command.RunAsync(Admission, default));
        Assert.Equal(new[] { "00:84:0000", "00:04:0300", "00:84:0000", "0D:10:01012C", "0D:10:000600", "00:04:0000", "00:84:0000" }, session.Commands);
        Assert.Equal(7, session.Transactions.Distinct().Count());
        string promptText = prompts.ToString();
        Assert.Contains("FIXED_CONFIRM", promptText);
        Assert.Contains("AUTO_CONFIRM", promptText);
        Assert.Contains("FINAL_CONFIRM", promptText);
        Assert.Contains("Manual recovery may be needed; no automatic restart.", promptText);
        Assert.DoesNotContain("FIXED_CONFIRM", output.ToString());
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Equal("RZ09-05289EN4", root.GetProperty("device").GetProperty("sku").GetString());
        Assert.Equal("0200", root.GetProperty("device").GetProperty("revision").GetString());
        Assert.Equal(91, root.GetProperty("device").GetProperty("featureReportByteLength").GetInt32());
        Assert.Equal("00", root.GetProperty("device").GetProperty("reportPrefix").GetString());
        Assert.Equal(CoolingPadFanExploratoryTrialCommand.ExactRecoveryConfirmation, root.GetProperty("recoveryAcknowledgement").GetString());
        Assert.False(root.GetProperty("recoveryPerformedByCommand").GetBoolean());
        Assert.False(root.GetProperty("automaticRecovery").GetBoolean());
        Assert.False(root.GetProperty("productionAdmitted").GetBoolean());
        Assert.False(root.GetProperty("fanStateConfirmed").GetBoolean());
        Assert.False(root.GetProperty("ownershipConfirmed").GetBoolean());
        Assert.False(root.GetProperty("handbackConfirmed").GetBoolean());
        Assert.True(root.GetProperty("result").GetProperty("finalPhysicalConfirmed").GetBoolean());
        Assert.False(root.GetProperty("result").GetProperty("manualRecoveryRequired").GetBoolean());
        Assert.Equal(7, root.GetProperty("capturedSequence").GetArrayLength());
        Assert.Equal(1, session.DisposeCount);
    }

    [Theory]
    [InlineData("NO\n", 4, "fixedPhysicalConfirmation")]
    [InlineData("", 4, "fixedPhysicalConfirmation")]
    [InlineData("yes\n", 4, "fixedPhysicalConfirmation")]
    [InlineData("YES\nNO\n", 5, "autoPhysicalConfirmation")]
    [InlineData("YES\n", 5, "autoPhysicalConfirmation")]
    [InlineData("YES\nYES\nNO\n", 7, "finalPhysicalConfirmation")]
    [InlineData("YES\nYES\n", 7, "finalPhysicalConfirmation")]
    public async Task RejectedOrMissingPhysicalResponse_StopsFutureWrites(string responses, int count, string phase)
    {
        var session = new Session();
        var output = new StringWriter();
        Assert.Equal(2, await Command(session, new StringReader(responses), output,
            new StringWriter()).RunAsync(Admission, default));
        Assert.Equal(count, session.Commands.Count);
        using var json = JsonDocument.Parse(output.ToString());
        var result = json.RootElement.GetProperty("result");
        Assert.Equal("OperatorRejected", result.GetProperty("failure").GetString());
        Assert.Equal(phase, result.GetProperty("failurePhase").GetString());
        Assert.True(result.GetProperty("manualRecoveryRequired").GetBoolean());
        Assert.False(result.GetProperty("finalPhysicalConfirmed").GetBoolean());
    }

    [Fact]
    public async Task CancellationBeforeRun_ReportsJsonWithNoOpenOrWrite()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var session = new Session();
        var output = new StringWriter();
        Assert.Equal(4, await Command(session, new StringReader(""), output,
            new StringWriter()).RunAsync(Admission, cancellation.Token));
        Assert.False(session.Opened);
        Assert.Empty(session.Commands);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("Canceled", json.RootElement.GetProperty("result").GetProperty("failure").GetString());
        Assert.False(json.RootElement.GetProperty("result").GetProperty("manualRecoveryRequired").GetBoolean());
    }

    [Fact]
    public async Task ConsoleCancellationDuringPhysicalRead_ReportsJsonAndStopsBeforeAuto()
    {
        Action? cancel = null;
        var session = new Session();
        var output = new StringWriter();
        var input = new CancelingReader(() => cancel!());
        var command = Command(session, input, output, new StringWriter(), callback =>
        {
            cancel = callback;
            return new Registration();
        });
        Assert.Equal(4, await command.RunAsync(Admission, default));
        Assert.Equal(4, session.Commands.Count);
        using var json = JsonDocument.Parse(output.ToString());
        var result = json.RootElement.GetProperty("result");
        Assert.Equal("Canceled", result.GetProperty("failure").GetString());
        Assert.Equal("fixedPhysicalConfirmation", result.GetProperty("failurePhase").GetString());
        Assert.True(result.GetProperty("manualRecoveryRequired").GetBoolean());
        Assert.False(result.GetProperty("autoAttempted").GetBoolean());
    }

    private static CoolingPadFanExploratoryTrialCommand Command(Session session,
        TextReader input, TextWriter output, TextWriter prompts, Func<Action, IDisposable>? register = null) =>
        new((fixedConfirmation, autoConfirmation, finalConfirmation, token) =>
            new CoolingPadFanExploratoryTrialRunner(() => new ValidationRunContext(),
                new ActorFactory(session), TimeProvider.System, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(180)).RunAsync(fixedConfirmation, autoConfirmation, finalConfirmation, token),
            input, output, prompts, register);

    private sealed class CancelingReader(Action cancel) : TextReader
    {
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            cancel();
            return ValueTask.FromCanceled<string?>(cancellationToken);
        }
    }
    private sealed class Registration : IDisposable
    {
        public void Dispose() { }
    }
    private sealed class ActorFactory(Session session) : IValidationActorFactory
    {
        public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context)
        {
            session.Opened = true;
            return ValueTask.FromResult(new HidSessionResult<ValidationActor>(new ValidationActor(session, new Battery()), null));
        }
    }
    private sealed class Battery : IBatteryCommandClient
    {
        public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(byte transactionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<HidSessionResult<HidUnit>> SetAsync(byte transactionId, BatteryValidationSetting setting, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class Session : IRazerCommandSession
    {
        public RazerCommandSessionDisposition Disposition => RazerCommandSessionDisposition.Healthy;
        internal bool Opened { get; set; }
        internal List<string> Commands { get; } = [];
        internal List<TransactionIdAssignment> Transactions { get; } = [];
        internal int DisposeCount { get; private set; }
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId, TimeSpan exchangeTimeout, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add($"{command.ClassId:X2}:{command.CommandId:X2}:{Convert.ToHexString(command.Arguments.Span)}");
            Transactions.Add(assignTransactionId());
            byte[] response = command.CommandId == 0x84
                ? Commands.Count == 3 ? [0x03, 0x00] : [0x00, 0x00]
                : command.Arguments.ToArray();
            Assert.True(command.PayloadValidator!(response));
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(response, null));
        }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
