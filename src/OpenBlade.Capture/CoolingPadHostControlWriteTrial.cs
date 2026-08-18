using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal sealed record CoolingPadHostControlWriteTrialResult(
    bool HostControlPreparationAttempted,
    bool HostControlPreparationAcknowledged,
    bool HostDemandWriteAttempted,
    bool HostDemandAcknowledged,
    int HostDemandTargetRpm,
    bool OperatorPhysicalConfirmationRequired,
    bool SynapseRestoreRequired,
    string Failure);

internal sealed class CoolingPadHostControlWriteTrialRunner
{
    internal const int HostDemandTargetRpm = CoolingPadHostControlPlan.TargetRpm;
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;
    private readonly TimeProvider timeProvider;
    private readonly PendingDisposalRegistry pendingDisposals;
    private int used;

    internal CoolingPadHostControlWriteTrialRunner(
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

    internal static CoolingPadHostControlWriteTrialRunner CreateWindows() => new(
        static () => new ValidationRunContext(),
        BatteryValidationFactory
            .CreateWindowsRz090581Bios400CoolingPadFanGetterActorFactory());

    internal async Task<CoolingPadHostControlWriteTrialResult> RunAsync(
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
        var plan = CoolingPadHostControlPlan.CreateValidated2200();
        if (plan.Count != 2
            || !context.TryAllocateTransaction(out byte preparationTransaction)
            || !context.TryAllocateTransaction(out byte demandTransaction))
        {
            await BatteryValidationRun.DisposeBoundedAsync(
                actor,
                timeProvider,
                CleanupTimeout,
                pendingDisposals).ConfigureAwait(false);
            return Failure("transaction-budget");
        }

        bool preparationAttempted = false;
        bool preparationAcknowledged = false;
        bool demandAttempted = false;
        bool demandAcknowledged = false;
        try
        {
            preparationAttempted = true;
            HidSessionResult<ReadOnlyMemory<byte>> preparation = await ExecuteAsync(
                actor.Session,
                plan.GetCommand(0),
                preparationTransaction,
                cancellationToken).ConfigureAwait(false);
            if (preparation.Failure is not null)
            {
                return Result(
                    preparationAttempted,
                    preparationAcknowledged: false,
                    demandAttempted: false,
                    demandAcknowledged: false,
                    failure: $"host-control-preparation:{preparation.Failure}");
            }
            preparationAcknowledged = true;

            demandAttempted = true;
            HidSessionResult<ReadOnlyMemory<byte>> demand = await ExecuteAsync(
                actor.Session,
                plan.GetCommand(1),
                demandTransaction,
                CancellationToken.None).ConfigureAwait(false);
            if (demand.Failure is not null)
            {
                return Result(
                    preparationAttempted,
                    preparationAcknowledged,
                    demandAttempted,
                    demandAcknowledged: false,
                    failure: $"host-demand:{demand.Failure}");
            }
            demandAcknowledged = true;

            return Result(
                preparationAttempted,
                preparationAcknowledged,
                demandAttempted,
                demandAcknowledged,
                failure: "none");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Result(
                preparationAttempted,
                preparationAcknowledged,
                demandAttempted,
                demandAcknowledged,
                failure: "canceled");
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

    private static CoolingPadHostControlWriteTrialResult Result(
        bool preparationAttempted,
        bool preparationAcknowledged,
        bool demandAttempted,
        bool demandAcknowledged,
        string failure) => new(
        preparationAttempted,
        preparationAcknowledged,
        demandAttempted,
        demandAcknowledged,
        HostDemandTargetRpm,
        OperatorPhysicalConfirmationRequired: demandAcknowledged,
        SynapseRestoreRequired: preparationAttempted,
        failure);

    private static CoolingPadHostControlWriteTrialResult Failure(string failure) =>
        Result(
            preparationAttempted: false,
            preparationAcknowledged: false,
            demandAttempted: false,
            demandAcknowledged: false,
            failure);

    private static async Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
        IRazerCommandSession session,
        RazerCommandSpec command,
        byte transactionId,
        CancellationToken cancellationToken)
    {
        return await session.ExecuteAsync(
            command,
            () => TransactionIdAssignment.Valid(transactionId),
            ExchangeTimeout,
            cancellationToken).ConfigureAwait(false);
    }
}

internal sealed class CoolingPadHostControlWriteTrialCommand(
    Func<CancellationToken, Task<CoolingPadHostControlWriteTrialResult>> run,
    TextWriter output)
{
    internal const string Confirmation =
        "RZ09-0581:4.00:1532:0F43:HOST-CONTROL-PREPARE:DEMAND-2200:ONCE:SYNAPSE-RESTORE";

    internal async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args is not ["--confirm-target", Confirmation])
        {
            await output.WriteLineAsync(
                "Usage: OpenBlade.Capture validate-cooling-pad-host-control-once " +
                "--confirm-target " + Confirmation).ConfigureAwait(false);
            return 2;
        }

        CoolingPadHostControlWriteTrialResult result = await run(cancellationToken)
            .ConfigureAwait(false);
        await output.WriteLineAsync(JsonSerializer.Serialize(
            result,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
            })).ConfigureAwait(false);
        if (result.HostControlPreparationAttempted)
        {
            await output.WriteLineAsync(
                "Do not send another OpenBlade command. Physically assess the fan, then open Synapse and restore its Auto UI state.")
                .ConfigureAwait(false);
        }
        return result.HostControlPreparationAcknowledged
            && result.HostDemandAcknowledged
            ? 0
            : 2;
    }
}
