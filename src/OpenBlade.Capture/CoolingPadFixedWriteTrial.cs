using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal sealed record CoolingPadFixedWriteTrialResult(
    bool BaselineRead,
    byte? BaselineOpaqueValue,
    bool WriteAttempted,
    bool WriteAcknowledged,
    bool PostWriteRead,
    byte? PostWriteOpaqueValue,
    bool ActiveStateReadbackConfirmed,
    bool SynapseRestoreRequired,
    string Failure);

internal sealed class CoolingPadFixedWriteTrialRunner
{
    internal const int FixedTargetRpm = 2_200;
    internal const byte FixedTargetRaw = FixedTargetRpm / 50;
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;
    private readonly TimeProvider timeProvider;
    private readonly PendingDisposalRegistry pendingDisposals;
    private int used;

    internal CoolingPadFixedWriteTrialRunner(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        TimeProvider? timeProvider = null,
        PendingDisposalRegistry? pendingDisposals = null)
    {
        this.createContext = createContext
            ?? throw new ArgumentNullException(nameof(createContext));
        this.actorFactory = actorFactory
            ?? throw new ArgumentNullException(nameof(actorFactory));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.pendingDisposals = pendingDisposals ?? PendingDisposalRegistry.Shared;
    }

    internal static CoolingPadFixedWriteTrialRunner CreateWindows() => new(
        static () => new ValidationRunContext(),
        BatteryValidationFactory
            .CreateWindowsRz090581Bios400CoolingPadFanGetterActorFactory());

    internal async Task<CoolingPadFixedWriteTrialResult> RunAsync(
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref used, 1) != 0)
        {
            return Failure("disposed");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Failure("canceled");
        }

        ValidationRunContext context;
        try
        {
            context = createContext();
        }
        catch
        {
            return Failure("context");
        }

        HidSessionResult<ValidationActor> opened =
            await actorFactory.OpenAsync(context).ConfigureAwait(false);
        if (opened.Failure is not null || opened.Value is null)
        {
            return Failure($"open:{opened.Failure}");
        }

        ValidationActor actor = opened.Value;
        bool baselineRead = false;
        byte? baselineOpaqueValue = null;
        bool writeAttempted = false;
        bool writeAcknowledged = false;
        try
        {
            HidSessionResult<ReadOnlyMemory<byte>> baseline = await ExecuteAsync(
                context,
                actor.Session,
                CoolingPadFanObservedRawProtocol.CreateQuery(),
                cancellationToken).ConfigureAwait(false);
            if (baseline.Failure is not null
                || !CoolingPadFanObservedRawProtocol.TryDecodeResponse(
                    baseline.Value.Span,
                    out CoolingPadFanObservedRawByte before))
            {
                return Failure($"baseline:{baseline.Failure}");
            }

            baselineOpaqueValue = before.RawByte;
            baselineRead = true;

            // Zero is only a conservative compatibility prerequisite for this
            // bounded experiment. It does not prove Auto or any active mode.
            if (before.RawByte != 0)
            {
                return new CoolingPadFixedWriteTrialResult(
                    BaselineRead: true,
                    baselineOpaqueValue,
                    WriteAttempted: false,
                    WriteAcknowledged: false,
                    PostWriteRead: false,
                    PostWriteOpaqueValue: null,
                    ActiveStateReadbackConfirmed: false,
                    SynapseRestoreRequired: false,
                    Failure: "baseline-not-opaque-zero");
            }

            writeAttempted = true;
            HidSessionResult<ReadOnlyMemory<byte>> write = await ExecuteAsync(
                context,
                actor.Session,
                CreateFixed2200Command(),
                cancellationToken).ConfigureAwait(false);
            if (write.Failure is not null)
            {
                return new CoolingPadFixedWriteTrialResult(
                    BaselineRead: true,
                    baselineOpaqueValue,
                    WriteAttempted: true,
                    WriteAcknowledged: false,
                    PostWriteRead: false,
                    PostWriteOpaqueValue: null,
                    ActiveStateReadbackConfirmed: false,
                    SynapseRestoreRequired: true,
                    Failure: $"write:{write.Failure}");
            }
            writeAcknowledged = true;

            HidSessionResult<ReadOnlyMemory<byte>> post = await ExecuteAsync(
                context,
                actor.Session,
                CoolingPadFanObservedRawProtocol.CreateQuery(),
                CancellationToken.None).ConfigureAwait(false);
            if (post.Failure is not null
                || !CoolingPadFanObservedRawProtocol.TryDecodeResponse(
                    post.Value.Span,
                    out CoolingPadFanObservedRawByte after))
            {
                return new CoolingPadFixedWriteTrialResult(
                    BaselineRead: true,
                    baselineOpaqueValue,
                    WriteAttempted: true,
                    WriteAcknowledged: true,
                    PostWriteRead: false,
                    PostWriteOpaqueValue: null,
                    ActiveStateReadbackConfirmed: false,
                    SynapseRestoreRequired: true,
                    Failure: $"post-read:{post.Failure}");
            }

            return new CoolingPadFixedWriteTrialResult(
                BaselineRead: true,
                baselineOpaqueValue,
                WriteAttempted: true,
                WriteAcknowledged: true,
                PostWriteRead: true,
                PostWriteOpaqueValue: after.RawByte,
                ActiveStateReadbackConfirmed: false,
                SynapseRestoreRequired: true,
                Failure: "none");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new CoolingPadFixedWriteTrialResult(
                baselineRead,
                baselineOpaqueValue,
                writeAttempted,
                writeAcknowledged,
                PostWriteRead: false,
                PostWriteOpaqueValue: null,
                ActiveStateReadbackConfirmed: false,
                SynapseRestoreRequired: writeAttempted,
                Failure: "canceled");
        }
        finally
        {
            await BatteryValidationRun.DisposeBoundedAsync(
                actor,
                timeProvider,
                CleanupTimeout,
                pendingDisposals).ConfigureAwait(false);
        }
    }

    internal static RazerCommandSpec CreateFixed2200Command() => new(
        classId: 0x0D,
        commandId: 0x10,
        arguments: [0x01, 0x02, FixedTargetRaw],
        expectedPayloadLength: 3,
        payload => payload.Span.SequenceEqual(
            new byte[] { 0x01, 0x02, FixedTargetRaw }));

    private static CoolingPadFixedWriteTrialResult Failure(string failure) => new(
        BaselineRead: false,
        BaselineOpaqueValue: null,
        WriteAttempted: false,
        WriteAcknowledged: false,
        PostWriteRead: false,
        PostWriteOpaqueValue: null,
        ActiveStateReadbackConfirmed: false,
        SynapseRestoreRequired: false,
        Failure: failure);

    private static async Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
        ValidationRunContext context,
        IRazerCommandSession session,
        RazerCommandSpec command,
        CancellationToken cancellationToken)
    {
        if (!context.TryAllocateTransaction(out byte transactionId))
        {
            return new(default, HidSessionFailureKind.Exhausted);
        }

        return await session.ExecuteAsync(
            command,
            () => TransactionIdAssignment.Valid(transactionId),
            ExchangeTimeout,
            cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class CoolingPadFixedWriteTrialCommand(
    Func<CancellationToken, Task<CoolingPadFixedWriteTrialResult>> run,
    TextWriter output)
{
    internal const string Confirmation =
        "RZ09-0581:4.00:1532:0F43:OPAQUE-ZERO:FIXED2200:SYNAPSE-RESTORE";

    internal async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args is not ["--confirm-target", Confirmation])
        {
            await output.WriteLineAsync(
                "Usage: OpenBlade.Capture validate-cooling-pad-fixed-write-once " +
                "--confirm-target " + Confirmation).ConfigureAwait(false);
            return 2;
        }

        CoolingPadFixedWriteTrialResult result = await run(cancellationToken)
            .ConfigureAwait(false);
        await output.WriteLineAsync(JsonSerializer.Serialize(
            result,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
            })).ConfigureAwait(false);
        if (result.WriteAttempted)
        {
            await output.WriteLineAsync(
                "Do not send an OpenBlade Auto command. Open Synapse and restore the cooling pad to Auto now.")
                .ConfigureAwait(false);
        }
        return result.WriteAcknowledged
            && result.PostWriteRead
            && result.Failure == "none"
            ? 0
            : 2;
    }
}
