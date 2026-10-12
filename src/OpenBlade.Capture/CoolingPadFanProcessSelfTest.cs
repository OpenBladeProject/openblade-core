using System.Diagnostics;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

// Reachable only through the explicit hardware-free child command. This factory
// has no native dependency and validates the fixed encodings and transactions.
internal sealed class PadProcessFakeFactory(int role, string fault) : IValidationActorFactory
{
    public ValueTask<HidSessionResult<ValidationActor>> OpenAsync(ValidationRunContext context)
    {
        if (role == 2 && fault == "usb-identity-failure")
        { return ValueTask.FromResult(new HidSessionResult<ValidationActor>(null, HidSessionFailureKind.IdentityBlocked)); }
        if (!context.TryAcquireActor(out var lease)) { throw new InvalidOperationException("Fake actor lease failed."); }
        var session = new FakeSession(role, fault, lease);
        return ValueTask.FromResult(new HidSessionResult<ValidationActor>(new(session, new UnusedBattery()), null));
    }
    private sealed class FakeSession(int role, string fault, ValidationRunContext.ActorLease lease) : IRazerCommandSession
    {
        private readonly PadProcessStep[] exchanges = PadProcessSequence.ForRole(role).Where(x => x.Transaction != 0).ToArray();
        private int index;
        public RazerCommandSessionDisposition Disposition { get; private set; } = RazerCommandSessionDisposition.Healthy;
        public Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(RazerCommandSpec command,
            Func<TransactionIdAssignment> assignTransactionId, TimeSpan exchangeTimeout, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Disposition != RazerCommandSessionDisposition.Healthy || index >= exchanges.Length)
            { throw new InvalidOperationException("Fake exchange outside active role."); }
            var expected = exchanges[index++];
            if (command.ClassId != expected.Class || command.CommandId != expected.Command
                || Convert.ToHexString(command.Arguments.Span) != expected.Arguments
                || assignTransactionId() != TransactionIdAssignment.Valid(checked((byte)expected.Transaction)))
            { throw new InvalidOperationException("Fake exchange/transaction rejected."); }
            string result = fault == "wrong-baseline" && expected.Name == "baseline" ? "0300" : expected.Result;
            if (role == 2 && expected.Name == "intermediate")
            {
                if (fault == "usb-raw-zero") { result = "0000"; }
                if (fault == "usb-raw-other") { result = "0100"; }
                if (fault == "usb-raw-failure")
                { return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(default, HidSessionFailureKind.NativeIoFailure)); }
            }
            return Task.FromResult(new HidSessionResult<ReadOnlyMemory<byte>>(Convert.FromHexString(result), null));
        }
        public ValueTask DisposeAsync()
        {
            lease.Dispose();
            if (fault == "close-failure") { throw new IOException("Injected close failure."); }
            if (fault != "incomplete-close") { Disposition = RazerCommandSessionDisposition.FullyDisposed; }
            return ValueTask.CompletedTask;
        }
    }
    private sealed class UnusedBattery : IBatteryCommandClient
    {
        public Task<HidSessionResult<BatteryValidationSetting>> QueryAsync(byte transactionId, CancellationToken token) => throw new NotSupportedException();
        public Task<HidSessionResult<HidUnit>> SetAsync(byte transactionId, BatteryValidationSetting setting, CancellationToken token) => throw new NotSupportedException();
    }
}

internal static class PadProcessSelfTest
{
    internal static async Task<int> RunAsync(TextWriter output, CancellationToken token, PadProcessTrial trial = PadProcessTrial.NormalExit)
    {
        // All actual subprocesses in this harness use FakeChildName. A live
        // native factory is unreachable from every injected-fault scenario.
        string[] scenarios = trial == PadProcessTrial.UsbOnlyReconnect
            ? ["none", "wrong-trial", "stale-nonce", "wrong-build", "wrong-pid", "early-receipt", "nonzero-exit",
                "close-failure", "incomplete-close", "channel-loss", "usb-premature-reconnect", "usb-observation-failure",
                "usb-no-absence", "usb-no-reconnect", "usb-disconnect-refusal", "usb-reconnect-refusal", "usb-physical-refusal",
                "usb-cancel-disconnect", "usb-cancel-reconnect", "usb-cancel-physical",
                "usb-raw-zero", "usb-raw-other", "usb-raw-failure", "usb-identity-failure"]
            : trial == PadProcessTrial.CooperativeCancellation
            ? ["none", "wrong-trial", "stale-nonce", "wrong-build", "wrong-pid", "wrong-phase", "early-receipt",
                "cancellation-not-observed", "normal-receipt", "zero-after-cancel", "nonzero-exit",
                "cancel-channel-loss", "close-failure", "incomplete-close", "delayed-exit", "hung-exit",
                "post-cancel-refusal", "supervisor-cancel-before", "supervisor-cancel-after"]
            : ["none", "stale-nonce", "wrong-build", "wrong-role", "malformed", "wrong-pid",
            "wrong-phase", "early-receipt", "wrong-baseline", "channel-loss", "close-failure", "incomplete-close",
            "nonzero-exit", "delayed-exit", "hung-exit", "delayed-launch", "operator-refusal", "orphan"];
        foreach (string scenario in scenarios)
        {
            Task<int>? lateExit = null;
            using var supervisorCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var result = await CoolingPadFanProcessContinuityCommand.SuperviseAsync(true, scenario,
                (step, _) =>
                {
                    if (scenario == "supervisor-cancel-before" && step == "fixedPhysical"
                        || scenario == "supervisor-cancel-after" && step == "postCancellationPhysical"
                        || scenario == "usb-cancel-disconnect" && step == "disconnectUsbOnly"
                        || scenario == "usb-cancel-reconnect" && step == "reconnectUsbSamePort"
                        || scenario == "usb-cancel-physical" && step == "postReconnectPhysical")
                    { supervisorCancellation.Cancel(); }
                    return Task.FromResult(!(scenario == "operator-refusal" && step == "fixedPhysical"
                        || scenario == "post-cancel-refusal" && step == "postCancellationPhysical"
                        || scenario == "usb-disconnect-refusal" && step == "disconnectUsbOnly"
                        || scenario == "usb-reconnect-refusal" && step == "reconnectUsbSamePort"
                        || scenario == "usb-physical-refusal" && step == "postReconnectPhysical"));
                }, supervisorCancellation.Token, observation => lateExit = observation, trial).ConfigureAwait(false);
            bool passed = scenario == "none"
                ? result.Success && result.ChildProcessIds.Count == 2 && result.OriginalBaseline == "0000"
                    && result.Evidence.IndexOf($"EXIT:1:{PadProcessSequence.ExitCode(1, trial)}") < result.Evidence.IndexOf("DONE:2:open:opened")
                    && result.Evidence.Contains("DONE:2:intermediate:0300")
                    && result.Evidence.Contains("DONE:2:restore:0000")
                : !result.Success && result.ChildProcessIds.Count == (scenario == "delayed-launch" ? 0
                    : scenario.StartsWith("usb-raw-", StringComparison.Ordinal) || scenario == "usb-identity-failure" ? 2 : 1);
            if (scenario == "delayed-launch")
            {
                passed &= result.ChildStillRunning && !result.MutationPermitted;
                // Assert this observation succeeded; an empty retention registry
                // alone could also mean its timeout/fault was merely observed.
                passed &= lateExit is not null
                    && await lateExit.WaitAsync(TimeSpan.FromSeconds(25), token).ConfigureAwait(false) == 2;
                await PendingDisposalRegistry.Shared.WhenEmptyAsync().WaitAsync(TimeSpan.FromSeconds(25), token).ConfigureAwait(false);
            }
            else if (scenario == "hung-exit")
            {
                passed &= result.ChildStillRunning && result.ManualRecoveryRequired;
                // The supervisor returned manual attention without killing or
                // launching child 2. Observe the fake child's later normal exit.
                using var lingering = Process.GetProcessById(result.ChildProcessIds[0]);
                await lingering.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                passed &= lingering.ExitCode == PadProcessSequence.ExitCode(1, trial);
            }
            else { passed &= !result.ChildStillRunning; }
            if (trial == PadProcessTrial.CooperativeCancellation && scenario == "none")
            {
                int observed = result.Evidence.IndexOf("DONE:1:cancelWork:ObservedCancellation");
                int closed = result.Evidence.IndexOf("DONE:1:close:FullyDisposed");
                int exited = result.Evidence.IndexOf("EXIT:1:4");
                int physical = result.Evidence.IndexOf("PHYSICAL:postCancellation:YES");
                int reopened = result.Evidence.IndexOf("DONE:2:open:opened");
                passed &= observed >= 0 && observed < closed && closed < exited && exited < physical && physical < reopened;
            }
            if (scenario.StartsWith("supervisor-cancel-", StringComparison.Ordinal)) { passed &= result.Failure == "Canceled"; }
            if (scenario.StartsWith("usb-cancel-", StringComparison.Ordinal)) { passed &= result.Failure == "Canceled"; }
            if (trial == PadProcessTrial.UsbOnlyReconnect && scenario == "none")
            {
                int exited = result.Evidence.IndexOf("EXIT:1:0");
                int physical = result.Evidence.IndexOf("PHYSICAL:postReconnect:YES");
                int reopened = result.Evidence.IndexOf("DONE:2:open:opened");
                passed &= result.UsbReconnect?.Success == true && exited >= 0 && exited < physical && physical < reopened
                    && result.Evidence.Contains("READ:2:intermediate:0300");
            }
            if (scenario.StartsWith("usb-raw-", StringComparison.Ordinal) || scenario == "usb-identity-failure")
            {
                passed &= !result.Evidence.Contains("PERMIT:2:auto") && result.OriginalBaseline == "0000";
                if (scenario == "usb-raw-zero")
                { passed &= result.RawIntermediate == "0000" && result.Evidence.Contains("READ:2:intermediate:0000"); }
                else { passed &= result.RawIntermediate is null; }
            }
            await output.WriteLineAsync($"{(passed ? "PASS" : "FAIL")} {scenario}: children={result.ChildProcessIds.Count}; failure={result.Failure ?? "none"}").ConfigureAwait(false);
            if (!passed) { return 2; }
        }
        var start = new ProcessStartInfo(PadProcessBuild.Read().AppHost)
        { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(CoolingPadFanProcessContinuityCommand.ChildFor(trial, false));
        using var direct = await CoolingPadFanProcessContinuityCommand.AwaitChildLaunchAsync(
            Task.Run(() => Process.Start(start) ?? throw new IOException("Direct-child test launch failed.")),
            TimeSpan.FromSeconds(5), token, PendingDisposalRegistry.Shared).ConfigureAwait(false);
        await direct.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        if (direct.ExitCode != 2) { await output.WriteLineAsync("FAIL direct-child rejection").ConfigureAwait(false); return 2; }
        await output.WriteLineAsync("PASS direct-child invocation rejected before any native factory.").ConfigureAwait(false);
        await output.WriteLineAsync($"PASS: hardware-free actual-subprocess {trial} checks; no native factory or hardware used.").ConfigureAwait(false);
        return 0;
    }

    private static int IndexOf(this IReadOnlyList<string> source, string value)
    {
        for (int i = 0; i < source.Count; i++) { if (source[i] == value) { return i; } }
        return -1;
    }
}

// Hardware-free sampled presence and virtual elapsed time for subprocess tests.
// The production branch constructs only the passive Windows path enumerator.
internal sealed class PadUsbReconnectFake(string fault) : TimeProvider, ICoolingPadPhysicalPresenceSource
{
    private int stage;
    private long timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref timestamp);
    public CoolingPadPhysicalPresenceObservation Observe()
    {
        int current = Volatile.Read(ref stage);
        if (current == 0) { return CoolingPadPhysicalPresenceObservation.Present; }
        if (fault == "usb-observation-failure") { return CoolingPadPhysicalPresenceObservation.ObservationFailed; }
        if (current == 1)
        {
            return fault == "usb-no-absence" || fault == "usb-premature-reconnect" && GetTimestamp() >= TimeSpan.TicksPerSecond
                ? CoolingPadPhysicalPresenceObservation.Present : CoolingPadPhysicalPresenceObservation.Absent;
        }
        return fault == "usb-no-reconnect" ? CoolingPadPhysicalPresenceObservation.Absent : CoolingPadPhysicalPresenceObservation.Present;
    }
    internal Task<PadUsbReconnectObservation> RunAsync(Func<string, CancellationToken, Task<bool>> confirm, CancellationToken token)
    {
        var boundary = new CoolingPadUsbReconnectBoundary(this, this, (duration, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Add(ref timestamp, duration.Ticks);
            return Task.CompletedTask;
        });
        return boundary.RunAsync(async (name, ct) =>
        {
            bool confirmed = await confirm(name, ct).ConfigureAwait(false);
            if (confirmed && name == "disconnectUsbOnly") { Volatile.Write(ref stage, 1); }
            if (confirmed && name == "reconnectUsbSamePort") { Volatile.Write(ref stage, 2); }
            return confirmed;
        }, token);
    }
}
