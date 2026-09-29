using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal sealed record CoolingPadControlStateRecoveryResult
{
    public string? RawBaseline { get; init; }
    public string? RawRestored { get; init; }
    public bool RestoreAttempted { get; init; }
    public bool RestoreAcknowledged { get; init; }
    public bool RawRestorationConfirmed { get; init; }
    public bool PhysicalConfirmed { get; init; }
    public string? FailurePhase { get; init; }
    public string? Failure { get; init; }
    // This recovery command cannot declare the earlier trial resolved until the
    // raw target and the present operator's physical observation both agree.
    public bool ManualRecoveryRequired => !Success;
    internal bool Success => Failure is null && RawRestorationConfirmed && PhysicalConfirmed;
}

// Raw mode recovery only: no fan, lighting, service, or vendor recovery commands.
internal sealed class CoolingPadControlStateRecoveryRunner(
    Func<ValidationRunContext> createContext, IValidationActorFactory actorFactory,
    TimeProvider timeProvider, TimeSpan exchangeTimeout, TimeSpan cleanupTimeout,
    TimeSpan operatorTimeout, PendingDisposalRegistry? pendingOperations = null)
{
    private readonly PendingDisposalRegistry pending = pendingOperations ?? PendingDisposalRegistry.Shared;
    private int started;

    public async Task<CoolingPadControlStateRecoveryResult> RunAsync(
        Func<CancellationToken, Task<bool>> confirmPhysicalBehavior, CancellationToken cancellationToken)
    {
        var result = new CoolingPadControlStateRecoveryResult();
        string phase = "open";
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return result with { FailurePhase = phase, Failure = "Disposed" };
        }
        ValidationActor? actor = null;
        try
        {
            ArgumentNullException.ThrowIfNull(confirmPhysicalBehavior);
            RazerCommandSessionTimeout.Validate(exchangeTimeout);
            RazerCommandSessionTimeout.Validate(cleanupTimeout);
            RazerCommandSessionTimeout.Validate(operatorTimeout);
            cancellationToken.ThrowIfCancellationRequested();
            var context = createContext() ?? throw new InvalidOperationException();
            byte[] transactions = new byte[3];
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
            if (result.RawBaseline == "0300")
            {
                phase = "restore";
                cancellationToken.ThrowIfCancellationRequested();
                result = result with { RestoreAttempted = true };
                var restored = await Exchange(new RazerCommandSpec(0x00, 0x04, [0x00, 0x00], 2, Is0000), 1).ConfigureAwait(false);
                if (!Valid(restored, Is0000))
                {
                    return Fail(result, phase, restored);
                }
                result = result with { RestoreAcknowledged = true };
                phase = "restorationRead";
                var final = await Exchange(Query(), 2).ConfigureAwait(false);
                if (!Valid(final, KnownRaw))
                {
                    return Fail(result, phase, final);
                }
                result = result with { RawRestored = Convert.ToHexString(final.Value.Span) };
                if (result.RawRestored != "0000")
                {
                    return result with { FailurePhase = phase, Failure = "RestorationMismatch" };
                }
            }
            else
            {
                // A fresh 0000 already matches the target. Do not submit a setter.
                result = result with { RawRestored = result.RawBaseline };
            }
            result = result with { RawRestorationConfirmed = true };
            phase = "physicalConfirmation";
            string? physicalFailure = await ConfirmAsync(confirmPhysicalBehavior, cancellationToken).ConfigureAwait(false);
            return physicalFailure is null
                ? result with { PhysicalConfirmed = true }
                : result with { FailurePhase = phase, Failure = physicalFailure };
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
                        cleanupTimeout, pending).ConfigureAwait(false);
                }
                catch { }
            }
        }
    }

    private async Task<string?> ConfirmAsync(Func<CancellationToken, Task<bool>> confirm, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observationCancellation = new CancellationTokenSource();
        long observationStarted = timeProvider.GetTimestamp();
        Task<bool>? observation = null;
        try
        {
            CancellationToken token = observationCancellation.Token;
            observation = Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return confirm(token) ?? throw new InvalidOperationException();
            }, CancellationToken.None);
            bool confirmed = await observation.WaitAsync(operatorTimeout, timeProvider, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (timeProvider.GetElapsedTime(observationStarted) >= operatorTimeout)
            {
                return "OperatorTimeout";
            }
            return confirmed ? null : "OperatorRejected";
        }
        catch (TimeoutException) { return "OperatorTimeout"; }
        catch (OperationCanceledException) { return "Canceled"; }
        catch { return "OperatorException"; }
        finally
        {
            try { pending.Retain(CancelAndDisposeAsync(observationCancellation)); }
            catch { }
            if (observation is not null)
            {
                pending.Retain(observation);
            }
        }
    }
    private static async Task CancelAndDisposeAsync(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync().ConfigureAwait(false); }
        finally { cancellation.Dispose(); }
    }
    private static bool Is0000(ReadOnlyMemory<byte> payload) => payload.Span.SequenceEqual(new byte[] { 0, 0 });
    private static bool KnownRaw(ReadOnlyMemory<byte> payload) => Is0000(payload) || payload.Span.SequenceEqual(new byte[] { 3, 0 });
    private static RazerCommandSpec Query() => new(0x00, 0x84, [0x00, 0x00], 2, KnownRaw);
    private static bool Valid(HidSessionResult<ReadOnlyMemory<byte>> response, Func<ReadOnlyMemory<byte>, bool> validator) =>
        response.Failure is null && validator(response.Value);
    private static CoolingPadControlStateRecoveryResult Fail(CoolingPadControlStateRecoveryResult result,
        string phase, HidSessionResult<ReadOnlyMemory<byte>> response) => result with
        { FailurePhase = phase, Failure = response.Failure?.ToString() ?? "UnexpectedPayload" };
}

internal sealed class CoolingPadControlStateRecoveryCommand(
    Func<Func<CancellationToken, Task<bool>>, CancellationToken, Task<CoolingPadControlStateRecoveryResult>> run,
    TextReader input, TextWriter output, TextWriter prompts, Func<Action, IDisposable>? registerCancellation = null)
{
    internal const string Name = "restore-rz09-0528-cooling-pad-control-state";
    internal const string ExactTargetConfirmation = "RZ09-05289EN4:1532:0F43:0200:0300-0000";
    private static readonly JsonSerializerOptions JsonOptions = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    internal async Task<int> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != 2 || arguments[0] != "--confirm-target" || arguments[1] != ExactTargetConfirmation)
        {
            await prompts.WriteLineAsync($"Usage: OpenBlade.Capture {Name} --confirm-target {ExactTargetConfirmation}").ConfigureAwait(false);
            return 1;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IDisposable registration = (registerCancellation ?? ConsoleCancellation.Register)(
            () => ConsoleCancellation.Cancel(cancellation));
        var result = await run(ConfirmAsync, cancellation.Token).ConfigureAwait(false);
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
                reportPrefix = "00",
            },
            rawRecoveryTarget = "0000",
            biosIsCaptureProvenanceOnly = true,
            productionAdmitted = false,
            fanStateConfirmed = false,
            ownershipConfirmed = false,
            handbackConfirmed = false,
            fanCommandsSubmitted = false,
            lightingCommandsSubmitted = false,
            vendorRecoveryPerformed = false,
            result,
        }, JsonOptions)).ConfigureAwait(false);
        return result.Success ? 0 : result.Failure == "Canceled" ? 4 : 2;
    }
    private async Task<bool> ConfirmAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await prompts.WriteLineAsync("RECOVERY_CONFIRM: Confirm the fan remains at the physical baseline and full green lighting is unchanged, with no red sleep.").ConfigureAwait(false);
        await prompts.WriteLineAsync("Raw 0000 confirms control state only. No fan, lighting, or Synapse recovery is performed. Type YES only if physically confirmed.").ConfigureAwait(false);
        return string.Equals((await input.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim(), "YES", StringComparison.Ordinal);
    }
}
