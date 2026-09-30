using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal sealed record PadProcessResult(bool Success, string? Failure, string? OriginalBaseline,
    bool MutationPermitted, IReadOnlyList<string> Evidence, IReadOnlyList<int> ChildProcessIds,
    bool ChildStillRunning)
{
    public bool ManualRecoveryRequired => MutationPermitted && !Success;
    public bool ProductionAdmitted => false;
    public bool FanStateConfirmed => false;
    public bool OwnershipConfirmed => false;
    public bool HandbackConfirmed => false;
}

internal static class CoolingPadFanProcessContinuityCommand
{
    internal const string Name = "validate-rz09-0528-cooling-pad-fan-process-continuity";
    internal const string SelfTestName = "selftest-cooling-pad-fan-process-continuity";
    internal const string ChildName = "__cooling-pad-fan-process-child";
    internal const string FakeChildName = "__cooling-pad-fan-process-fake-child";
    internal const string CooperativeName = "validate-rz09-0528-cooling-pad-fan-cooperative-cancellation";
    internal const string CooperativeSelfTestName = "selftest-cooling-pad-fan-cooperative-cancellation";
    internal const string CooperativeChildName = "__cooling-pad-fan-cooperative-child";
    internal const string CooperativeFakeChildName = "__cooling-pad-fan-cooperative-fake-child";
    internal const string ExactTarget = "RZ09-05289EN4:1532:0F43:0200:Medium2200-NormalExitNewProcess-Auto";
    internal const string CooperativeTarget = "RZ09-05289EN4:1532:0F43:0200:Medium2200-CooperativeCancel-NewProcess-Auto";
    internal const string ExactRecovery = "ManualPowerCycleOnly";
    internal static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan PhysicalTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);

    internal static bool Admitted(IReadOnlyList<string> args, PadProcessTrial trial = PadProcessTrial.NormalExit) => args.Count == 4
        && args[0] == "--confirm-target" && args[1] == (trial == PadProcessTrial.NormalExit ? ExactTarget : CooperativeTarget)
        && args[2] == "--confirm-recovery" && args[3] == ExactRecovery;

    internal static async Task<int> RunAsync(IReadOnlyList<string> args, TextReader input,
        TextWriter output, TextWriter prompts, CancellationToken token, PadProcessTrial trial = PadProcessTrial.NormalExit)
    {
        bool cooperative = trial == PadProcessTrial.CooperativeCancellation;
        if (!Admitted(args, trial))
        {
            await prompts.WriteLineAsync($"Usage: OpenBlade.Capture {(cooperative ? CooperativeName : Name)} --confirm-target {(cooperative ? CooperativeTarget : ExactTarget)} --confirm-recovery {ExactRecovery}").ConfigureAwait(false);
            return 1;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var registration = ConsoleCancellation.Register(() => ConsoleCancellation.Cancel(cancellation));
        var result = await SuperviseAsync(false, "none", async (step, ct) =>
        {
            string observation = step switch
            {
                "baselinePhysical" => "Confirm the pad is at the quiet Auto baseline, with full normal green lighting and no red sleep, before this trial starts.",
                "fixedPhysical" when cooperative => "Confirm vendor Medium 2200 physically responded with full normal green lighting and no red sleep. YES allows the planned child work-token cancellation between HID operations and orderly disposal/exit.",
                "fixedPhysical" => "Confirm vendor Medium 2200 physically responded with full normal green lighting and no red sleep. YES allows normal child exit followed by the new-process Auto step.",
                "postCancellationPhysical" => "After the child canceled, disposed and exited, confirm Medium still behaves normally with full normal green lighting and no red sleep. YES permits the new-process Auto step.",
                "autoPhysical" => "Confirm the fan returned to the ORIGINAL quiet physical baseline with full normal green lighting and no red sleep.",
                _ => "Confirm the fan remains at the original quiet baseline with full normal green lighting and no red sleep.",
            };
            await prompts.WriteLineAsync($"{step}: {observation}").ConfigureAwait(false);
            await prompts.WriteLineAsync("Type YES only if confirmed. Any other answer, EOF, timeout or cancellation stops further writes. Manual power cycle may be required; no automatic recovery.").ConfigureAwait(false);
            return string.Equals((await input.ReadLineAsync(ct).ConfigureAwait(false))?.Trim(), "YES", StringComparison.Ordinal);
        }, cancellation.Token, trial: trial).ConfigureAwait(false);
        await output.WriteLineAsync(SerializeResult(result, trial)).ConfigureAwait(false);
        return result.Success ? 0 : result.Failure == "Canceled" ? 4 : 2;
    }

    internal static string SerializeResult(PadProcessResult result, PadProcessTrial trial)
    {
        bool cooperative = trial == PadProcessTrial.CooperativeCancellation;
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            command = cooperative ? CooperativeName : Name,
            device = new { model = "RZ09-0528", sku = "RZ09-05289EN4", vendorId = "1532", productId = "0F43", revision = "0200", interfaceNumber = "00", usagePage = "000C", usage = "0001", featureReportByteLength = 91, reportPrefix = "00" },
            biosIsCaptureProvenanceOnly = true,
            readOnly = false,
            exploratory = true,
            continuityBoundary = cooperative ? "cooperative-cancellation-between-HID-operations-and-new-process" : "normal-process-exit-and-new-process",
            cooperativeCancellationTested = cooperative && result.Success,
            cancellationWorkKind = cooperative ? "developer-armed-idle-wait" : null,
            productionWorkLoopCancellationTested = false,
            inFlightIoCancellationTested = false,
            newProcessTested = result.Success,
            processCrashTested = false,
            productionAdmitted = false,
            fanStateConfirmed = false,
            rpmReadbackConfirmed = false,
            ownershipConfirmed = false,
            handbackConfirmed = false,
            recoveryAcknowledgement = ExactRecovery,
            recoveryPerformedByCommand = false,
            automaticRecovery = false,
            result,
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true });
    }

    // Only the fixed apphost can be launched. No executable, journal, payload,
    // transaction offset, or restoration target is accepted from the caller.
    internal static async Task<PadProcessResult> SuperviseAsync(bool fake, string fault,
        Func<string, CancellationToken, Task<bool>> confirm, CancellationToken token,
        Action<Task<int>>? observeLateLaunch = null, PadProcessTrial trial = PadProcessTrial.NormalExit)
    {
        var state = new PadProcessState(trial);
        List<int> children = [];
        bool childStillRunning = false;
        string? failure = null;
        try
        {
            if (!fake && (fault != "none" || observeLateLaunch is not null)) { throw new InvalidDataException("Fault injection is hardware-free only."); }
            var build = PadProcessBuild.Read();
            if (!await ConfirmAsync(confirm, "baselinePhysical", token).ConfigureAwait(false))
            { throw new InvalidDataException("Original physical baseline rejected."); }
            state.Evidence.Add("PHYSICAL:baseline:YES");
            state.Evidence.Add("BUILD:sha256-apphost-dll:" + build.Digest);
            for (int role = 1; role <= 2; role++)
            {
                token.ThrowIfCancellationRequested();
                state.Begin(role);
                string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                string pipeName = "OpenBlade.PadProcess." + nonce;
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                Task<Process> launching = Task.Run(async () =>
                {
                    if (fake && fault == "delayed-launch") { await Task.Delay(1000).ConfigureAwait(false); }
                    return StartChild(build, fake, pipeName, nonce, role, role == 1 ? fault : "none", trial);
                });
                using var child = await AwaitChildLaunchAsync(launching,
                    fake && fault == "delayed-launch" ? TimeSpan.FromMilliseconds(100) : MessageTimeout,
                    token, PendingDisposalRegistry.Shared, observeLateLaunch).ConfigureAwait(false);
                children.Add(child.Id);
                bool completed = false;
                try
                {
                    using var exchange = Deadline(token, MessageTimeout);
                    await pipe.WaitForConnectionAsync(exchange.Token).ConfigureAwait(false);
                    uint expectedPid = checked((uint)child.Id);
                    if (fake && fault == "wrong-pid") { expectedPid++; }
                    if (PadProcessBuild.ClientPid(pipe) != expectedPid) { throw new IOException("Native child PID rejected."); }
                    build.VerifyPeer(child);
                    var channel = new PadProcessChannel(pipe);
                    await channel.ExpectAsync(Hello(fake, role, nonce, build.Digest, trial), exchange.Token).ConfigureAwait(false);
                    await channel.WriteAsync(Admission(role, nonce, build.Digest, trial), exchange.Token).ConfigureAwait(false);
                    foreach (var expected in PadProcessSequence.ForRole(role, trial))
                    {
                        using var phase = Deadline(token, MessageTimeout);
                        var step = state.Request(await channel.ReadAsync(phase.Token).ConfigureAwait(false));
                        if (step != expected) { throw new InvalidDataException("Unexpected phase."); }
                        if (step.Physical && !await ConfirmAsync(confirm, step.Name, token).ConfigureAwait(false))
                        { throw new InvalidDataException("OperatorRejected"); }
                        token.ThrowIfCancellationRequested();
                        build.VerifyPeer(child);
                        state.Permit(step);
                        // The original 0000 is retained in this supervisor only.
                        // Intermediate 0300 is never eligible as a restore target.
                        using var response = Deadline(token, MessageTimeout);
                        await channel.WriteAsync($"GO:{role}:{step.Name}:0000", response.Token).ConfigureAwait(false);
                        if (fake && fault == "channel-loss" && step.Name == "candidate")
                        { pipe.Dispose(); throw new IOException("Injected channel loss."); }
                        if (fake && fault == "cancel-channel-loss" && step.Name == "cancelWork")
                        { pipe.Dispose(); throw new IOException("Injected cancellation channel loss."); }
                        state.Complete(await channel.ReadAsync(response.Token).ConfigureAwait(false));
                    }
                    using var receiptDeadline = Deadline(token, MessageTimeout);
                    state.Receipt(await channel.ReadAsync(receiptDeadline.Token).ConfigureAwait(false));
                    // A receipt alone never admits the next child.
                    await channel.WriteAsync($"RECEIVED:{role}", receiptDeadline.Token).ConfigureAwait(false);
                    using var exitDeadline = Deadline(token, fake && fault is "delayed-exit" or "hung-exit"
                        ? TimeSpan.FromMilliseconds(100) : CloseTimeout);
                    await child.WaitForExitAsync(exitDeadline.Token).ConfigureAwait(false);
                    state.Exited(child.ExitCode);
                    completed = true;
                }
                finally
                {
                    // Closing the channel cooperatively revokes future permits.
                    // Never kill a child: that would turn this into a crash trial.
                    pipe.Dispose();
                    if (!completed && !child.HasExited)
                    {
                        try { await child.WaitForExitAsync().WaitAsync(CloseTimeout).ConfigureAwait(false); }
                        catch { childStillRunning = true; }
                    }
                }
                if (trial == PadProcessTrial.CooperativeCancellation && role == 1)
                {
                    if (!await ConfirmAsync(confirm, "postCancellationPhysical", token).ConfigureAwait(false))
                    { throw new InvalidDataException("PostCancellationOperatorRejected"); }
                    token.ThrowIfCancellationRequested();
                    state.ConfirmPostCancellationPhysical();
                }
            }
        }
        catch (PadProcessLaunchUncertainException)
        { childStillRunning = true; failure = token.IsCancellationRequested ? "Canceled" : "ChildLaunchUncertain"; }
        catch (OperationCanceledException) { failure = token.IsCancellationRequested ? "Canceled" : "PhaseTimeout"; }
        catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }
        return new(state.Success && failure is null, failure, state.OriginalBaseline,
            state.MutationPermitted, state.Evidence.ToArray(), children.ToArray(), childStillRunning);
    }

    private static Process StartChild(PadProcessBuild build, bool fake, string pipe,
        string nonce, int role, string fault, PadProcessTrial trial = PadProcessTrial.NormalExit)
    {
        var start = new ProcessStartInfo(build.AppHost)
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(build.AppHost)! };
        start.ArgumentList.Add(trial == PadProcessTrial.CooperativeCancellation
            ? fake ? CooperativeFakeChildName : CooperativeChildName : fake ? FakeChildName : ChildName);
        start.ArgumentList.Add(pipe);
        start.ArgumentList.Add(nonce);
        start.ArgumentList.Add(role.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Only the explicit fake child accepts a fault argument.
        if (fake) { start.ArgumentList.Add(fault); }
        return Process.Start(start) ?? throw new IOException("Child launch failed.");
    }

    internal static async Task<Process> AwaitChildLaunchAsync(Task<Process> launching, TimeSpan timeout,
        CancellationToken token, PendingDisposalRegistry pending, Action<Task<int>>? observeLateLaunch = null)
    {
        try { return await launching.WaitAsync(timeout, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // Caller unwinds and closes the server immediately. Any late child
            // cannot obtain admission. Observe its bounded normal exit, no kill.
            Task<int> observation = ObserveLateLaunchAsync(launching);
            pending.Retain(observation);
            observeLateLaunch?.Invoke(observation);
            throw new PadProcessLaunchUncertainException();
        }
    }
    private static async Task<int> ObserveLateLaunchAsync(Task<Process> launching)
    {
        using var late = await launching.ConfigureAwait(false);
        await late.WaitForExitAsync().WaitAsync(MessageTimeout + CloseTimeout).ConfigureAwait(false);
        return late.ExitCode;
    }
    private sealed class PadProcessLaunchUncertainException : IOException;

    internal static async Task<int> ChildAsync(bool fake, IReadOnlyList<string> args, CancellationToken token,
        PadProcessTrial trial = PadProcessTrial.NormalExit)
    {
        ValidationActor? actor = null;
        try
        {
            if (args.Count != (fake ? 4 : 3) || args[1].Length != 64
                || !args[1].All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F')
                || args[0] != "OpenBlade.PadProcess." + args[1]
                || args[2] is not ("1" or "2")) { return 2; }
            int role = args[2] == "1" ? 1 : 2;
            string fault = fake ? args[3] : "none";
            var build = PadProcessBuild.Read();
            if (fake && fault == "orphan")
            {
                // Hardware-free relay exits normally. Its child's pipe server
                // is a grandparent, which must fail OS-parent authentication.
                using var orphan = await AwaitChildLaunchAsync(
                    Task.Run(() => StartChild(build, true, args[0], args[1], role, "none", trial)),
                    MessageTimeout, token, PendingDisposalRegistry.Shared).ConfigureAwait(false);
                return 0;
            }
            using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var admission = Deadline(token, MessageTimeout);
            await pipe.ConnectAsync(admission.Token).ConfigureAwait(false);
            using var parent = PadProcessBuild.VerifyParent(pipe, build);
            var channel = new PadProcessChannel(pipe);
            string hello = Hello(fake, fault == "wrong-role" ? 2 : role,
                fault == "stale-nonce" ? new string('0', 64) : args[1],
                fault == "wrong-build" ? new string('0', 128) : build.Digest,
                fault == "wrong-trial" ? PadProcessTrial.NormalExit : trial);
            if (fault == "malformed")
            {
                await pipe.WriteAsync(new byte[] { 255, 255, 255, 127 }, admission.Token).ConfigureAwait(false);
                return 2;
            }
            await channel.WriteAsync(hello, admission.Token).ConfigureAwait(false);
            await channel.ExpectAsync(Admission(role, args[1], build.Digest, trial), admission.Token).ConfigureAwait(false);
            var context = new ValidationRunContext();
            // Each fresh context reserves the same complete fixed set. Role 2
            // can execute only its compile-time 5..8 sequence.
            for (byte expected = 1; expected <= 8; expected++)
            {
                if (!context.TryAllocateTransaction(out byte allocated) || allocated != expected)
                { throw new InvalidDataException("Transaction reservation rejected."); }
            }
            foreach (var step in PadProcessSequence.ForRole(role, trial))
            {
                using var phase = Deadline(token, step.Physical ? PhysicalTimeout + MessageTimeout : MessageTimeout);
                // Arm the actual pending developer idle wait before advertising this
                // fixed cancellation phase. Control-channel cancellation is separate.
                using var workCancellation = step.Name == "cancelWork" ? new CoolingPadCooperativeCancellation() : null;
                if (workCancellation is not null && !workCancellation.Armed) { throw new IOException("Cancellation wait not armed."); }
                if (fault == "early-receipt" && step.Name == "open")
                { await channel.WriteAsync($"COMPLETE:{role}:FullyDisposed", phase.Token).ConfigureAwait(false); return 2; }
                string request = fault == "wrong-phase" && step.Name == "open"
                    ? $"READY:{role}:fixed" : $"READY:{role}:{step.Name}";
                await channel.WriteAsync(request, phase.Token).ConfigureAwait(false);
                await channel.ExpectAsync($"GO:{role}:{step.Name}:0000", phase.Token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                build.VerifyPeer(parent);
                string result;
                if (step.Name == "open")
                {
                    // This is the only native factory call. Authentication and
                    // the supervisor's live opening permit precede construction.
                    Task<HidSessionResult<ValidationActor>> opening = Task.Run(async () =>
                    {
                        var factory = fake ? (IValidationActorFactory)new PadProcessFakeFactory(role, fault)
                            : GetterCandidateProbeFactory.CreateCoolingPadProcessActorFactory();
                        return await factory.OpenAsync(context).ConfigureAwait(false);
                    });
                    HidSessionResult<ValidationActor> opened;
                    try { opened = await opening.WaitAsync(MessageTimeout, token).ConfigureAwait(false); }
                    catch
                    {
                        // Observe and close a late handle; it receives no command.
                        PendingDisposalRegistry.Shared.Retain(CloseLateOpenAsync(opening));
                        throw;
                    }
                    actor = opened.Value;
                    if (opened.Failure is not null || actor is null) { throw new IOException("Strict native opening failed: " + opened.Failure); }
                    result = "opened";
                }
                else if (step.Name == "close")
                {
                    var closing = actor ?? throw new IOException("Missing actor.");
                    actor = null;
                    await CloseActorAsync(closing).ConfigureAwait(false);
                    result = "FullyDisposed";
                }
                else if (step.Name == "cancelWork")
                {
                    if (fault == "cancellation-not-observed") { result = "CancellationNotObserved"; }
                    else
                    {
                        await workCancellation!.ObserveAsync(phase.Token).ConfigureAwait(false);
                        result = "ObservedCancellation";
                    }
                }
                else if (step.Physical) { result = "YES"; }
                else
                {
                    var response = await actor!.Session.ExecuteAsync(step.Spec(),
                        () => TransactionIdAssignment.Valid(checked((byte)step.Transaction)),
                        TimeSpan.FromSeconds(3), phase.Token).ConfigureAwait(false);
                    if (response.Failure is not null || Convert.ToHexString(response.Value.Span) != step.Result)
                    { throw new IOException("Exchange rejected: " + (response.Failure?.ToString() ?? "UnexpectedPayload")); }
                    result = Convert.ToHexString(response.Value.Span);
                }
                await channel.WriteAsync($"DONE:{role}:{step.Name}:{result}", phase.Token).ConfigureAwait(false);
            }
            using var completion = Deadline(token, MessageTimeout);
            await channel.WriteAsync(fault == "normal-receipt" ? $"COMPLETE:{role}:FullyDisposed"
                : PadProcessSequence.Receipt(role, trial), completion.Token).ConfigureAwait(false);
            await channel.ExpectAsync($"RECEIVED:{role}", completion.Token).ConfigureAwait(false);
            if (fault == "delayed-exit") { await Task.Delay(1000, token).ConfigureAwait(false); }
            if (fault == "hung-exit") { await Task.Delay(7000, token).ConfigureAwait(false); }
            return fault == "nonzero-exit" ? 2 : fault == "zero-after-cancel" ? 0 : PadProcessSequence.ExitCode(role, trial);
        }
        catch { return 2; }
        finally
        {
            // Host-handle cleanup only; absolutely no firmware writes here.
            if (actor is not null) { try { await CloseActorAsync(actor).ConfigureAwait(false); } catch { } }
        }
    }

    private static async Task CloseActorAsync(ValidationActor actor)
    {
        Task closing = Task.Run(async () => await actor.DisposeAsync().ConfigureAwait(false));
        try
        {
            await closing.WaitAsync(CloseTimeout).ConfigureAwait(false);
            if (actor.Session.Disposition != RazerCommandSessionDisposition.FullyDisposed)
            { throw new IOException("CloseNotFullyDisposed"); }
        }
        finally { PendingDisposalRegistry.Shared.Retain(closing); }
    }
    private static async Task CloseLateOpenAsync(Task<HidSessionResult<ValidationActor>> opening)
    {
        var opened = await opening.ConfigureAwait(false);
        if (opened.Value is not null) { await CloseActorAsync(opened.Value).ConfigureAwait(false); }
    }
    private static string Hello(bool fake, int role, string nonce, string digest, PadProcessTrial trial) =>
        $"HELLO:1:{(fake ? "fake" : "live")}:{role}:{nonce}:{digest}"
        + (trial == PadProcessTrial.CooperativeCancellation ? ":cooperative-cancellation" : "");
    private static string Admission(int role, string nonce, string digest, PadProcessTrial trial) =>
        $"ADMIT:{role}:{nonce}:{digest}"
        + (trial == PadProcessTrial.CooperativeCancellation ? ":cooperative-cancellation" : "");
    private static CancellationTokenSource Deadline(CancellationToken token, TimeSpan timeout)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        return deadline;
    }
    internal static async Task<bool> ConfirmAsync(Func<string, CancellationToken, Task<bool>> confirm,
        string name, CancellationToken token, TimeSpan? timeout = null,
        TimeProvider? clock = null, PendingDisposalRegistry? pending = null)
    {
        token.ThrowIfCancellationRequested();
        TimeSpan limit = timeout ?? PhysicalTimeout;
        var time = clock ?? TimeProvider.System;
        var retained = pending ?? PendingDisposalRegistry.Shared;
        var observerCancellation = new CancellationTokenSource();
        Task<bool>? observation = null;
        long started = time.GetTimestamp();
        try
        {
            CancellationToken observerToken = observerCancellation.Token;
            observation = Task.Run(() =>
            {
                observerToken.ThrowIfCancellationRequested();
                return confirm(name, observerToken) ?? throw new InvalidOperationException("Missing observation.");
            }, CancellationToken.None);
            bool confirmed = await observation.WaitAsync(limit, time, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (time.GetElapsedTime(started) >= limit) { throw new TimeoutException("OperatorTimeout"); }
            return confirmed;
        }
        finally
        {
            // Observer callbacks cannot synchronously block the supervisor's
            // timeout/cancellation path or authorize a late result.
            retained.Retain(CancelObserverAsync(observerCancellation));
            if (observation is not null) { retained.Retain(observation); }
        }
    }
    private static async Task CancelObserverAsync(CancellationTokenSource observer)
    {
        try { await observer.CancelAsync().ConfigureAwait(false); }
        finally { observer.Dispose(); }
    }
}
