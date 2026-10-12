using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal sealed record CoolingPadControlStateRoundTripResult
{
    public string? RawBaseline { get; init; }
    public string? RawCandidate { get; init; }
    public string? RawRestored { get; init; }
    public bool ApplyAttempted { get; init; }
    public bool ApplyAcknowledged { get; init; }
    public bool CandidateConfirmed { get; init; }
    public bool RestoreAttempted { get; init; }
    public bool RestoreAcknowledged { get; init; }
    public bool Restored { get; init; }
    public bool ManualRecoveryRequired => ApplyAttempted && !Restored;
    public string? FailurePhase { get; init; }
    public string? Failure { get; init; }
    internal bool Success => Failure is null && CandidateConfirmed && Restored;
}

// These are raw pad observations only. They do not establish fan or ownership semantics.
internal sealed class CoolingPadControlStateRoundTripRunner
{
    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan exchangeTimeout;
    private readonly TimeSpan cleanupTimeout;
    private readonly PendingDisposalRegistry pendingDisposals;
    private int started;

    internal CoolingPadControlStateRoundTripRunner(
        Func<ValidationRunContext> createContext, IValidationActorFactory actorFactory,
        TimeProvider timeProvider, TimeSpan exchangeTimeout, TimeSpan cleanupTimeout,
        PendingDisposalRegistry? pendingDisposals = null)
    {
        this.createContext = createContext ?? throw new ArgumentNullException(nameof(createContext));
        this.actorFactory = actorFactory ?? throw new ArgumentNullException(nameof(actorFactory));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        RazerCommandSessionTimeout.Validate(cleanupTimeout);
        this.exchangeTimeout = exchangeTimeout;
        this.cleanupTimeout = cleanupTimeout;
        this.pendingDisposals = pendingDisposals ?? PendingDisposalRegistry.Shared;
    }

    public async Task<CoolingPadControlStateRoundTripResult> RunAsync(CancellationToken cancellationToken)
    {
        var result = new CoolingPadControlStateRoundTripResult();
        string phase = "open";
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return result with { FailurePhase = phase, Failure = "Disposed" };
        }

        ValidationActor? actor = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidationRunContext context = createContext() ?? throw new InvalidOperationException();
            // Reserve the entire bounded sequence before submitting anything.
            byte[] transactions = new byte[5];
            for (int i = 0; i < transactions.Length; i++)
            {
                if (!context.TryAllocateTransaction(out transactions[i]))
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

            async Task<HidSessionResult<ReadOnlyMemory<byte>>> Exchange(
                RazerCommandSpec command, int index, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                return await actor.Session.ExecuteAsync(command,
                    () => TransactionIdAssignment.Valid(transactions[index]), exchangeTimeout, token).ConfigureAwait(false);
            }

            phase = "baselineRead";
            var baseline = await Exchange(Query(), 0, cancellationToken).ConfigureAwait(false);
            if (!Valid(baseline, IsKnownRaw))
            {
                return Fail(result, phase, baseline);
            }

            result = result with { RawBaseline = Convert.ToHexString(baseline.Value.Span) };
            if (result.RawBaseline != "0000")
            {
                return result with { FailurePhase = phase, Failure = "BaselineNot0000" };
            }

            phase = "apply";
            cancellationToken.ThrowIfCancellationRequested();
            result = result with { ApplyAttempted = true };
            var apply = await Exchange(Set(0x03), 1, cancellationToken).ConfigureAwait(false);
            if (!Valid(apply, payload => IsExactRaw(payload, 0x03)))
            {
                return Fail(result, phase, apply);
            }

            result = result with { ApplyAcknowledged = true };

            phase = "candidateRead";
            var candidate = await Exchange(Query(), 2, cancellationToken).ConfigureAwait(false);
            if (!Valid(candidate, IsKnownRaw))
            {
                return Fail(result, phase, candidate);
            }

            result = result with { RawCandidate = Convert.ToHexString(candidate.Value.Span) };
            if (result.RawCandidate != "0300")
            {
                return result with { FailurePhase = phase, Failure = "CandidateMismatch" };
            }

            result = result with { CandidateConfirmed = true };

            // Only an unambiguous candidate read permits restoration. There is no
            // finally write, retry, dwell, or input wait. A cancellation received
            // after confirmation permits this one bounded restore and readback.
            using var restoration = new CancellationTokenSource(cleanupTimeout, timeProvider);
            phase = "restore";
            result = result with { RestoreAttempted = true };
            var restore = await Exchange(Set(0x00), 3, restoration.Token).ConfigureAwait(false);
            if (!Valid(restore, payload => IsExactRaw(payload, 0x00)))
            {
                return Fail(result, phase, restore);
            }

            result = result with { RestoreAcknowledged = true };

            phase = "restorationRead";
            var final = await Exchange(Query(), 4, restoration.Token).ConfigureAwait(false);
            if (!Valid(final, IsKnownRaw))
            {
                return Fail(result, phase, final);
            }

            result = result with { RawRestored = Convert.ToHexString(final.Value.Span) };
            if (result.RawRestored != "0000")
            {
                return result with { FailurePhase = phase, Failure = "RestorationMismatch" };
            }

            result = result with { Restored = true };
            return cancellationToken.IsCancellationRequested
                ? result with { FailurePhase = "completed", Failure = "Canceled" }
                : result;
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
                // Cleanup failures must neither escape nor fabricate restoration.
                try
                {
                    await BoundedValidationActorDisposal.DisposeAsync(actor, timeProvider,
                        cleanupTimeout, pendingDisposals).ConfigureAwait(false);
                }
                catch { }
            }
        }
    }

    private static bool IsExactRaw(ReadOnlyMemory<byte> payload, byte value) =>
        payload.Length == 2 && payload.Span[0] == value && payload.Span[1] == 0;
    private static bool IsKnownRaw(ReadOnlyMemory<byte> payload) =>
        IsExactRaw(payload, 0) || IsExactRaw(payload, 3);
    private static RazerCommandSpec Query() => new(0x00, 0x84, [0x00, 0x00], 2, IsKnownRaw);
    private static RazerCommandSpec Set(byte value) => new(0x00, 0x04, [value, 0x00], 2,
        payload => IsExactRaw(payload, value));
    // The shared session validates status 02, framing, checksum, transaction and
    // operation before returning a successful payload. Recheck raw shape here.
    private static bool Valid(HidSessionResult<ReadOnlyMemory<byte>> response,
        Func<ReadOnlyMemory<byte>, bool> validator) => response.Failure is null && validator(response.Value);
    private static CoolingPadControlStateRoundTripResult Fail(CoolingPadControlStateRoundTripResult result,
        string phase, HidSessionResult<ReadOnlyMemory<byte>> response) => result with
        { FailurePhase = phase, Failure = response.Failure?.ToString() ?? "UnexpectedPayload" };
}

internal sealed class CoolingPadControlStateRoundTripCommand(
    Func<CancellationToken, Task<CoolingPadControlStateRoundTripResult>> run,
    TextWriter output, Func<Action, IDisposable>? registerCancellation = null)
{
    internal const string Name = "validate-rz09-0528-cooling-pad-control-state-round-trip";
    internal const string ExactTargetConfirmation = "RZ09-05289EN4:1532:0F43:0200:0000-0300-0000";
    private static readonly JsonSerializerOptions JsonOptions = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    internal async Task<int> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 2 || arguments[0] != "--confirm-target" || arguments[1] != ExactTargetConfirmation)
        {
            await output.WriteLineAsync($"Usage: OpenBlade.Capture {Name} --confirm-target {ExactTargetConfirmation}").ConfigureAwait(false);
            return 1;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable registration = (registerCancellation ?? ConsoleCancellation.Register)(
            () => ConsoleCancellation.Cancel(cancellation));
        var result = await run(cancellation.Token).ConfigureAwait(false);
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            command = Name,
            device = new
            {
                model = "RZ09-0528",
                sku = "RZ09-05289EN4",
                vendorId = "1532",
                productId = "0F43",
                revision = "0200",
                usagePage = "000C",
                usage = "0001",
                featureReportByteLength = 91,
                reportPrefix = "00"
            },
            readOnly = false,
            productionAdmitted = false,
            fanStateConfirmed = false,
            ownershipConfirmed = false,
            handbackConfirmed = false,
            semanticMappingConfirmed = false,
            hardwareTrialPreviouslyValidated = false,
            result,
        }, JsonOptions)).ConfigureAwait(false);
        return result.Success ? 0 : result.Failure == "Canceled" ? 4 : 2;
    }
}
