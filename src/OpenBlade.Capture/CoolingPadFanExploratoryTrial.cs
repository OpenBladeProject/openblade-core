using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal sealed record CoolingPadFanExploratoryTrialResult
{
    public string? RawBaseline { get; init; }
    public string? RawCandidate { get; init; }
    public string? RawRestored { get; init; }
    public bool ModeAcquireAttempted { get; init; }
    public bool ModeAcquireAcknowledged { get; init; }
    public bool CandidateConfirmed { get; init; }
    public bool FixedAttempted { get; init; }
    public bool FixedAcknowledged { get; init; }
    public bool FixedPhysicalConfirmed { get; init; }
    public bool AutoAttempted { get; init; }
    public bool AutoAcknowledged { get; init; }
    public bool AutoPhysicalConfirmed { get; init; }
    public bool ModeRestoreAttempted { get; init; }
    public bool ModeRestoreAcknowledged { get; init; }
    public bool RawRestorationConfirmed { get; init; }
    public bool FinalPhysicalConfirmed { get; init; }
    public string? FailurePhase { get; init; }
    public string? Failure { get; init; }
    public bool ManualRecoveryRequired =>
        (ModeAcquireAttempted || FixedAttempted || AutoAttempted || ModeRestoreAttempted)
        && !(RawRestorationConfirmed && FinalPhysicalConfirmed);
    public bool ProductionAdmitted => false;
    public bool FanStateConfirmed => false;
    public bool OwnershipConfirmed => false;
    public bool HandbackConfirmed => false;
    internal bool Success => Failure is null && RawRestorationConfirmed && FinalPhysicalConfirmed;
}

// Developer-only exploratory trial; the command requires exact target and
// manual recovery acknowledgements before constructing the native actor.
// Captured echoes and operator observations do not establish fan readback or a
// reversible fan contract. Raw 0000 alone never proves fan restoration.
internal sealed class CoolingPadFanExploratoryTrialRunner
{
    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan exchangeTimeout;
    private readonly TimeSpan cleanupTimeout;
    private readonly TimeSpan operatorTimeout;
    private readonly PendingDisposalRegistry pendingOperations;
    private int started;

    internal CoolingPadFanExploratoryTrialRunner(
        Func<ValidationRunContext> createContext, IValidationActorFactory actorFactory,
        TimeProvider timeProvider, TimeSpan exchangeTimeout, TimeSpan cleanupTimeout,
        TimeSpan operatorTimeout, PendingDisposalRegistry? pendingOperations = null)
    {
        this.createContext = createContext ?? throw new ArgumentNullException(nameof(createContext));
        this.actorFactory = actorFactory ?? throw new ArgumentNullException(nameof(actorFactory));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        RazerCommandSessionTimeout.Validate(cleanupTimeout);
        RazerCommandSessionTimeout.Validate(operatorTimeout);
        this.exchangeTimeout = exchangeTimeout;
        this.cleanupTimeout = cleanupTimeout;
        this.operatorTimeout = operatorTimeout;
        this.pendingOperations = pendingOperations ?? PendingDisposalRegistry.Shared;
    }

    public async Task<CoolingPadFanExploratoryTrialResult> RunAsync(
        Func<CancellationToken, Task<bool>> confirmFixedPhysicalBehavior,
        Func<CancellationToken, Task<bool>> confirmAutoPhysicalReturnToBaseline,
        Func<CancellationToken, Task<bool>> confirmFinalFanGreenNoRedSleep,
        CancellationToken cancellationToken)
    {
        var result = new CoolingPadFanExploratoryTrialResult();
        string phase = "open";
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return result with { FailurePhase = phase, Failure = "Disposed" };
        }
        ValidationActor? actor = null;
        try
        {
            ArgumentNullException.ThrowIfNull(confirmFixedPhysicalBehavior);
            ArgumentNullException.ThrowIfNull(confirmAutoPhysicalReturnToBaseline);
            ArgumentNullException.ThrowIfNull(confirmFinalFanGreenNoRedSleep);
            cancellationToken.ThrowIfCancellationRequested();
            var context = createContext() ?? throw new InvalidOperationException();
            byte[] transactions = new byte[7];
            for (int index = 0; index < transactions.Length; index++)
            {
                if (!context.TryAllocateTransaction(out transactions[index]))
                {
                    return result with { FailurePhase = phase, Failure = "Exhausted" };
                }
            }
            var opened = await actorFactory.OpenAsync(context).ConfigureAwait(false);
            actor = opened.Value;
            if (opened.Failure is not null || actor is null)
            {
                return result with { FailurePhase = phase, Failure = (opened.Failure ?? HidSessionFailureKind.NativeIoFailure).ToString() };
            }

            async Task<HidSessionResult<ReadOnlyMemory<byte>>> Exchange(RazerCommandSpec command, int index)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return await actor.Session.ExecuteAsync(command,
                    () => TransactionIdAssignment.Valid(transactions[index]), exchangeTimeout,
                    cancellationToken).ConfigureAwait(false);
            }

            phase = "baselineRead";
            var baseline = await Exchange(Query(), 0).ConfigureAwait(false);
            if (!Valid(baseline, KnownRaw))
            {
                return Fail(result, phase, baseline);
            }
            result = result with { RawBaseline = Convert.ToHexString(baseline.Value.Span) };
            if (result.RawBaseline != "0000")
            {
                return result with { FailurePhase = phase, Failure = "BaselineNot0000" };
            }

            phase = "modeAcquire";
            cancellationToken.ThrowIfCancellationRequested();
            result = result with { ModeAcquireAttempted = true };
            var acquired = await Exchange(ExactEcho(0x00, 0x04, [0x03, 0x00]), 1).ConfigureAwait(false);
            if (!Valid(acquired, payload => Exact(payload, [0x03, 0x00])))
            {
                return Fail(result, phase, acquired);
            }
            result = result with { ModeAcquireAcknowledged = true };

            phase = "candidateRead";
            var candidate = await Exchange(Query(), 2).ConfigureAwait(false);
            if (!Valid(candidate, KnownRaw))
            {
                return Fail(result, phase, candidate);
            }
            result = result with { RawCandidate = Convert.ToHexString(candidate.Value.Span) };
            if (result.RawCandidate != "0300")
            {
                return result with { FailurePhase = phase, Failure = "CandidateMismatch" };
            }
            result = result with { CandidateConfirmed = true };

            phase = "fixed";
            cancellationToken.ThrowIfCancellationRequested();
            result = result with { FixedAttempted = true };
            var fixedResponse = await Exchange(ExactEcho(0x0D, 0x10, [0x01, 0x01, 0x2C]), 3).ConfigureAwait(false);
            if (!Valid(fixedResponse, payload => Exact(payload, [0x01, 0x01, 0x2C])))
            {
                return Fail(result, phase, fixedResponse);
            }
            result = result with { FixedAcknowledged = true };
            phase = "fixedPhysicalConfirmation";
            string? physicalFailure = await ConfirmAsync(confirmFixedPhysicalBehavior, cancellationToken).ConfigureAwait(false);
            if (physicalFailure is not null)
            {
                return result with { FailurePhase = phase, Failure = physicalFailure };
            }
            result = result with { FixedPhysicalConfirmed = true };

            // Auto historically caused red sleep. Any ambiguity or physical
            // refusal stops here; Auto is never a finally cleanup operation.
            phase = "auto";
            cancellationToken.ThrowIfCancellationRequested();
            result = result with { AutoAttempted = true };
            var autoResponse = await Exchange(ExactEcho(0x0D, 0x10, [0x00, 0x06, 0x00]), 4).ConfigureAwait(false);
            if (!Valid(autoResponse, payload => Exact(payload, [0x00, 0x06, 0x00])))
            {
                return Fail(result, phase, autoResponse);
            }
            result = result with { AutoAcknowledged = true };
            phase = "autoPhysicalConfirmation";
            physicalFailure = await ConfirmAsync(confirmAutoPhysicalReturnToBaseline, cancellationToken).ConfigureAwait(false);
            if (physicalFailure is not null)
            {
                return result with { FailurePhase = phase, Failure = physicalFailure };
            }
            result = result with { AutoPhysicalConfirmed = true };

            phase = "modeRestore";
            cancellationToken.ThrowIfCancellationRequested();
            result = result with { ModeRestoreAttempted = true };
            var restored = await Exchange(ExactEcho(0x00, 0x04, [0x00, 0x00]), 5).ConfigureAwait(false);
            if (!Valid(restored, payload => Exact(payload, [0x00, 0x00])))
            {
                return Fail(result, phase, restored);
            }
            result = result with { ModeRestoreAcknowledged = true };
            phase = "restorationRead";
            var final = await Exchange(Query(), 6).ConfigureAwait(false);
            if (!Valid(final, KnownRaw))
            {
                return Fail(result, phase, final);
            }
            result = result with { RawRestored = Convert.ToHexString(final.Value.Span) };
            if (result.RawRestored != "0000")
            {
                return result with { FailurePhase = phase, Failure = "RestorationMismatch" };
            }
            result = result with { RawRestorationConfirmed = true };
            phase = "finalPhysicalConfirmation";
            physicalFailure = await ConfirmAsync(confirmFinalFanGreenNoRedSleep, cancellationToken).ConfigureAwait(false);
            if (physicalFailure is not null)
            {
                return result with { FailurePhase = phase, Failure = physicalFailure };
            }
            return result with { FinalPhysicalConfirmed = true };
        }
        catch (OperationCanceledException)
        {
            return result with { FailurePhase = phase, Failure = "Canceled" };
        }
        catch
        {
            return result with { FailurePhase = phase, Failure = "NativeIoFailure" };
        }
        finally
        {
            if (actor is not null)
            {
                try
                {
                    await BoundedValidationActorDisposal.DisposeAsync(actor, timeProvider,
                        cleanupTimeout, pendingOperations).ConfigureAwait(false);
                }
                catch { }
            }
        }
    }

    private async Task<string?> ConfirmAsync(Func<CancellationToken, Task<bool>> confirm, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Do not link this token to the timer or caller: an observer may register
        // a blocking cancellation callback. WaitAsync owns the actual deadline.
        var observerCancellation = new CancellationTokenSource();
        long observationStarted = timeProvider.GetTimestamp();
        Task<bool>? observation = null;
        try
        {
            CancellationToken observationToken = observerCancellation.Token;
            // Isolate callback invocation as well as its returned task so a
            // synchronously blocked operator adapter cannot strand the runner.
            observation = Task.Run(() =>
            {
                observationToken.ThrowIfCancellationRequested();
                return confirm(observationToken) ?? throw new InvalidOperationException();
            }, CancellationToken.None);
            // WaitAsync also bounds callbacks that ignore the supplied token.
            bool confirmed = await observation.WaitAsync(operatorTimeout, timeProvider,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (timeProvider.GetElapsedTime(observationStarted) >= operatorTimeout)
            {
                return "OperatorTimeout";
            }
            return confirmed ? null : "OperatorRejected";
        }
        catch (TimeoutException)
        {
            return "OperatorTimeout";
        }
        catch (OperationCanceledException)
        {
            return "Canceled";
        }
        catch
        {
            return "OperatorException";
        }
        finally
        {
            // CancelAsync keeps a noncooperative token registration off the
            // runner's cleanup path. Retain its completion and any late fault.
            try { pendingOperations.Retain(CancelAndDisposeObserverAsync(observerCancellation)); }
            catch { }
            if (observation is not null)
            {
                // Retain and observe a late task, but never resume the sequence.
                pendingOperations.Retain(observation);
            }
        }
    }

    private static async Task CancelAndDisposeObserverAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            // Keep registrations alive until asynchronous notification completes.
            cancellation.Dispose();
        }
    }

    private static bool Exact(ReadOnlyMemory<byte> payload, ReadOnlySpan<byte> expected) => payload.Span.SequenceEqual(expected);
    private static bool KnownRaw(ReadOnlyMemory<byte> payload) => Exact(payload, [0x00, 0x00]) || Exact(payload, [0x03, 0x00]);
    private static RazerCommandSpec Query() => new(0x00, 0x84, [0x00, 0x00], 2, KnownRaw);
    private static RazerCommandSpec ExactEcho(byte operationClass, byte operationId, byte[] arguments) =>
        new(operationClass, operationId, arguments, arguments.Length, payload => Exact(payload, arguments));
    private static bool Valid(HidSessionResult<ReadOnlyMemory<byte>> response, Func<ReadOnlyMemory<byte>, bool> validator) =>
        response.Failure is null && validator(response.Value);
    private static CoolingPadFanExploratoryTrialResult Fail(CoolingPadFanExploratoryTrialResult result,
        string phase, HidSessionResult<ReadOnlyMemory<byte>> response) => result with
        { FailurePhase = phase, Failure = response.Failure?.ToString() ?? "UnexpectedPayload" };
}
