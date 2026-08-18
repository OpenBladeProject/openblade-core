using System.Text.Json;
using OpenBlade.Protocol;

namespace OpenBlade.Capture;

internal enum CoolingPadFanButtonCorrelationStage
{
    LowBaseline,
    Medium,
    High,
    LowReturn,
}

internal enum CoolingPadFanButtonCorrelationFailure
{
    None,
    AlreadyRun,
    ConfirmationRejected,
    TransactionBudgetUnavailable,
    OpenFailed,
    QueryFailed,
    Canceled,
    UnexpectedFailure,
}

internal sealed record CoolingPadFanButtonSnapshot(
    CoolingPadFanButtonCorrelationStage Stage,
    int SequenceIndex,
    double ElapsedMilliseconds,
    string OpaqueObservationPayloadHex,
    byte OpaqueObservation,
    string ConfigurationBank0PayloadHex,
    string ConfigurationBank1PayloadHex);

internal sealed record CoolingPadFanButtonCorrelationResult(
    IReadOnlyList<CoolingPadFanButtonSnapshot> Snapshots,
    int QueryCount,
    bool OneSessionConfirmed,
    bool ReadOnly,
    int OpenBladeWriteCount,
    bool ActiveModeReadbackConfirmed,
    bool TachometerReadbackConfirmed,
    bool ProductionWriteAdmitted,
    HidSessionFailureKind? SessionFailure,
    CoolingPadFanButtonCorrelationFailure Failure)
{
    internal bool Success => Failure == CoolingPadFanButtonCorrelationFailure.None
        && Snapshots.Count == CoolingPadFanButtonCorrelationRunner.Stages.Count
        && QueryCount == CoolingPadFanButtonCorrelationRunner.RequiredTransactionCount
        && OneSessionConfirmed
        && ReadOnly
        && OpenBladeWriteCount == 0
        && !ActiveModeReadbackConfirmed
        && !TachometerReadbackConfirmed
        && !ProductionWriteAdmitted
        && SessionFailure is null;
}

internal sealed class CoolingPadFanButtonCorrelationRunner
{
    internal const int QueriesPerStage = 3;
    internal const int RequiredTransactionCount = 12;

    internal static IReadOnlyList<CoolingPadFanButtonCorrelationStage> Stages { get; } =
        Array.AsReadOnly(
        [
            CoolingPadFanButtonCorrelationStage.LowBaseline,
            CoolingPadFanButtonCorrelationStage.Medium,
            CoolingPadFanButtonCorrelationStage.High,
            CoolingPadFanButtonCorrelationStage.LowReturn,
        ]);

    private readonly Func<ValidationRunContext> createContext;
    private readonly IValidationActorFactory actorFactory;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan exchangeTimeout;
    private readonly TimeSpan cleanupTimeout;
    private readonly PendingDisposalRegistry pendingDisposals;
    private int started;

    internal CoolingPadFanButtonCorrelationRunner(
        Func<ValidationRunContext> createContext,
        IValidationActorFactory actorFactory,
        TimeProvider timeProvider,
        TimeSpan exchangeTimeout,
        TimeSpan cleanupTimeout,
        PendingDisposalRegistry? pendingDisposals = null)
    {
        this.createContext = createContext
            ?? throw new ArgumentNullException(nameof(createContext));
        this.actorFactory = actorFactory
            ?? throw new ArgumentNullException(nameof(actorFactory));
        this.timeProvider = timeProvider
            ?? throw new ArgumentNullException(nameof(timeProvider));
        RazerCommandSessionTimeout.Validate(exchangeTimeout);
        RazerCommandSessionTimeout.Validate(cleanupTimeout);
        this.exchangeTimeout = exchangeTimeout;
        this.cleanupTimeout = cleanupTimeout;
        this.pendingDisposals = pendingDisposals ?? PendingDisposalRegistry.Shared;
    }

    internal static CoolingPadFanButtonCorrelationRunner CreateWindows() => new(
        static () => new ValidationRunContext(),
        BatteryValidationFactory
            .CreateWindowsRz090581Bios400CoolingPadFanGetterActorFactory(),
        TimeProvider.System,
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(5));

    internal async Task<CoolingPadFanButtonCorrelationResult> RunAsync(
        Func<CoolingPadFanButtonCorrelationStage, CancellationToken, Task<bool>> confirm,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            return Empty(CoolingPadFanButtonCorrelationFailure.AlreadyRun);
        }

        try
        {
            if (!await confirm(Stages[0], cancellationToken).ConfigureAwait(false))
            {
                return Empty(cancellationToken.IsCancellationRequested
                    ? CoolingPadFanButtonCorrelationFailure.Canceled
                    : CoolingPadFanButtonCorrelationFailure.ConfirmationRejected);
            }

            ValidationRunContext context = createContext()
                ?? throw new InvalidOperationException(
                    "The validation context factory returned null.");
            byte[] transactionIds = new byte[RequiredTransactionCount];
            for (int index = 0; index < transactionIds.Length; index++)
            {
                if (!context.TryAllocateTransaction(out transactionIds[index]))
                {
                    return Empty(
                        CoolingPadFanButtonCorrelationFailure.TransactionBudgetUnavailable);
                }
            }

            ValidationActor? actor = null;
            try
            {
                HidSessionResult<ValidationActor> opened =
                    await actorFactory.OpenAsync(context).ConfigureAwait(false);
                actor = opened.Value;
                if (opened.Failure is not null || actor is null)
                {
                    return Empty(
                        cancellationToken.IsCancellationRequested
                            ? CoolingPadFanButtonCorrelationFailure.Canceled
                            : CoolingPadFanButtonCorrelationFailure.OpenFailed,
                        opened.Failure);
                }

                long startedAt = timeProvider.GetTimestamp();
                var snapshots = new List<CoolingPadFanButtonSnapshot>(Stages.Count);
                int transactionIndex = 0;
                for (int stageIndex = 0; stageIndex < Stages.Count; stageIndex++)
                {
                    CoolingPadFanButtonCorrelationStage stage = Stages[stageIndex];
                    if (stageIndex > 0
                        && !await confirm(stage, cancellationToken).ConfigureAwait(false))
                    {
                        return Result(
                            snapshots,
                            transactionIndex,
                            cancellationToken.IsCancellationRequested
                                ? CoolingPadFanButtonCorrelationFailure.Canceled
                                : CoolingPadFanButtonCorrelationFailure
                                    .ConfirmationRejected);
                    }

                    HidSessionResult<ReadOnlyMemory<byte>> opaque = await ExecuteAsync(
                            actor.Session,
                            CoolingPadFanGetterCommands.CreateOpaqueObservation(),
                            transactionIds[transactionIndex++],
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (opaque.Failure is not null)
                    {
                        return Result(
                            snapshots,
                            transactionIndex,
                            CoolingPadFanButtonCorrelationFailure.QueryFailed,
                            opaque.Failure);
                    }

                    HidSessionResult<ReadOnlyMemory<byte>> bank0 = await ExecuteAsync(
                            actor.Session,
                            CoolingPadFanGetterCommands.CreateConfigurationBank0(),
                            transactionIds[transactionIndex++],
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (bank0.Failure is not null)
                    {
                        return Result(
                            snapshots,
                            transactionIndex,
                            CoolingPadFanButtonCorrelationFailure.QueryFailed,
                            bank0.Failure);
                    }

                    HidSessionResult<ReadOnlyMemory<byte>> bank1 = await ExecuteAsync(
                            actor.Session,
                            CoolingPadFanGetterCommands.CreateConfigurationBank1(),
                            transactionIds[transactionIndex++],
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (bank1.Failure is not null)
                    {
                        return Result(
                            snapshots,
                            transactionIndex,
                            CoolingPadFanButtonCorrelationFailure.QueryFailed,
                            bank1.Failure);
                    }

                    snapshots.Add(new CoolingPadFanButtonSnapshot(
                        stage,
                        stageIndex,
                        timeProvider.GetElapsedTime(startedAt).TotalMilliseconds,
                        Convert.ToHexString(opaque.Value.Span),
                        opaque.Value.Span[2],
                        Convert.ToHexString(bank0.Value.Span),
                        Convert.ToHexString(bank1.Value.Span)));
                }

                return Result(
                    snapshots,
                    transactionIndex,
                    CoolingPadFanButtonCorrelationFailure.None);
            }
            finally
            {
                if (actor is not null)
                {
                    await BoundedValidationActorDisposal.DisposeAsync(
                            actor,
                            timeProvider,
                            cleanupTimeout,
                            pendingDisposals)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Empty(CoolingPadFanButtonCorrelationFailure.Canceled);
        }
        catch
        {
            return Empty(CoolingPadFanButtonCorrelationFailure.UnexpectedFailure);
        }
    }

    private Task<HidSessionResult<ReadOnlyMemory<byte>>> ExecuteAsync(
        IRazerCommandSession session,
        RazerCommandSpec command,
        byte transactionId,
        CancellationToken cancellationToken) => session.ExecuteAsync(
        command,
        () => TransactionIdAssignment.Valid(transactionId),
        exchangeTimeout,
        cancellationToken);

    private static CoolingPadFanButtonCorrelationResult Empty(
        CoolingPadFanButtonCorrelationFailure failure,
        HidSessionFailureKind? sessionFailure = null) =>
        Result([], 0, failure, sessionFailure, oneSessionConfirmed: false);

    private static CoolingPadFanButtonCorrelationResult Result(
        IReadOnlyList<CoolingPadFanButtonSnapshot> snapshots,
        int queryCount,
        CoolingPadFanButtonCorrelationFailure failure,
        HidSessionFailureKind? sessionFailure = null,
        bool oneSessionConfirmed = true) => new(
        snapshots,
        queryCount,
        oneSessionConfirmed,
        ReadOnly: true,
        OpenBladeWriteCount: 0,
        ActiveModeReadbackConfirmed: false,
        TachometerReadbackConfirmed: false,
        ProductionWriteAdmitted: false,
        sessionFailure,
        failure);
}

internal sealed class CoolingPadFanButtonCorrelationCommand(
    Func<Func<CoolingPadFanButtonCorrelationStage, CancellationToken, Task<bool>>,
        CancellationToken,
        Task<CoolingPadFanButtonCorrelationResult>> run,
    TextReader input,
    TextWriter output)
{
    internal const string Confirmation =
        "RZ09-0581:BIOS-4.00:PAD-0F43-0200:READ-ONLY:PHYSICAL-FAN-BUTTON";

    internal async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        if (args is not ["--confirm-target", Confirmation])
        {
            await output.WriteLineAsync(
                "Usage: OpenBlade.Capture correlate-cooling-pad-fan-button " +
                "--confirm-target " + Confirmation).ConfigureAwait(false);
            return 2;
        }

        CoolingPadFanButtonCorrelationResult result = await run(
                ConfirmAsync,
                cancellationToken)
            .ConfigureAwait(false);
        await output.WriteLineAsync(JsonSerializer.Serialize(
            new
            {
                schemaVersion = 1,
                device = new
                {
                    model = TargetBladeDevice.Model,
                    vendorId = "1532",
                    productId = "0F43",
                    revision = "0200",
                    biosVersion = "4.00",
                },
                readOnly = true,
                openBladeWriteCount = 0,
                observationSemantics = "OpaqueByteAndStoredConfigurationBanks",
                configuredTargetReadbackConfirmed = false,
                activeModeReadbackConfirmed = false,
                tachometerReadbackConfirmed = false,
                productionWriteAdmitted = false,
                result,
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
            })).ConfigureAwait(false);
        return result.Success ? 0 : 2;
    }

    private async Task<bool> ConfirmAsync(
        CoolingPadFanButtonCorrelationStage stage,
        CancellationToken cancellationToken)
    {
        string expected = stage switch
        {
            CoolingPadFanButtonCorrelationStage.LowBaseline =>
                "LOW-BASELINE-CONFIRMED",
            CoolingPadFanButtonCorrelationStage.Medium => "MEDIUM-AFTER-PRESS-1",
            CoolingPadFanButtonCorrelationStage.High => "HIGH-AFTER-PRESS-2",
            CoolingPadFanButtonCorrelationStage.LowReturn => "LOW-AFTER-PRESS-3",
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
        string instruction = stage switch
        {
            CoolingPadFanButtonCorrelationStage.LowBaseline =>
                "Use the physical fan button to select Low, independently confirm the indicator, then type",
            CoolingPadFanButtonCorrelationStage.Medium =>
                "Press the physical fan button once, independently confirm Medium, then type",
            CoolingPadFanButtonCorrelationStage.High =>
                "Press the physical fan button once, independently confirm High, then type",
            CoolingPadFanButtonCorrelationStage.LowReturn =>
                "Press the physical fan button once, independently confirm Low, then type",
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
        await output.WriteLineAsync($"{instruction} {expected}").ConfigureAwait(false);
        string? response = await input.ReadLineAsync(cancellationToken)
            .ConfigureAwait(false);
        return response?.Equals(expected, StringComparison.Ordinal) == true;
    }
}
