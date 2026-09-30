using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Capture;

internal sealed record CoolingPadCounterValidationResult
{
    public bool Completed { get; init; }
    public string? FailurePhase { get; init; }
    public string? Failure { get; init; }
    public bool ManualAttentionRequired { get; init; }
    public bool SubmissionStateUnknown { get; init; }
    public bool ResourceCleanupCompleted { get; init; }
    public bool RepeatedDemandIdAndTwoSuccessorsAcknowledged { get; init; }
    public IReadOnlyList<CoolingPadCounterExchange> Exchanges { get; init; } = [];
    public IReadOnlyList<CoolingPadControlStatus> Decisions { get; init; } = [];
    public IReadOnlyList<string> ConfirmedStages { get; init; } = [];
    public CoolingPadDeveloperRestorationResult? Restoration { get; init; }
    public int? CompletedLogicalExchangeCount => Completed && ResourceCleanupCompleted && !SubmissionStateUnknown
        && Exchanges.All(x => x.OutcomeAvailable && x.Acknowledged) ? Exchanges.Count : null;
}

// Same core coordinator, durable write-ahead evidence and explicit attended restoration.
// It does not exercise outer service IPC/admission or establish cross-process exclusion.
internal sealed class CoolingPadCounterValidationRunner
{
    private readonly CoolingPadSessionFactory factory;
    private readonly ICoolingPadMutationEvidenceStore evidenceStore;
    private readonly Func<CancellationToken, Task> delay;
    private readonly TimeSpan operatorTimeout;
    private readonly TimeSpan cleanupTimeout;
    private int started;

    internal CoolingPadCounterValidationRunner(CoolingPadSessionFactory? factory = null,
        ICoolingPadMutationEvidenceStore? evidenceStore = null, Func<CancellationToken, Task>? delay = null,
        TimeSpan? operatorTimeout = null, TimeSpan? cleanupTimeout = null)
    {
        this.cleanupTimeout = cleanupTimeout ?? TimeSpan.FromSeconds(5);
        this.factory = factory ?? CoolingPadSessionFactory.CreateDeveloperCounterWindows();
        this.evidenceStore = evidenceStore ?? new CoolingPadMutationEvidenceStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenBlade"));
        this.delay = delay ?? (token => Task.Delay(TimeSpan.FromSeconds(2), token));
        this.operatorTimeout = operatorTimeout ?? TimeSpan.FromMinutes(10);
    }

    internal static CoolingPadCounterValidationRunner ForTesting(CoolingPadSessionFactory factory,
        Func<CancellationToken, Task>? delay = null, TimeSpan? cleanupTimeout = null) =>
        new(factory, new InMemoryCoolingPadMutationEvidenceStore(), delay ?? (_ => Task.CompletedTask), cleanupTimeout: cleanupTimeout);

    internal async Task<CoolingPadCounterValidationResult> RunAsync(
        Func<string, CancellationToken, Task<bool>> confirm, CancellationToken token)
    {
        if (Interlocked.Exchange(ref started, 1) != 0) { throw new InvalidOperationException("SingleUseTrial"); }
        var runtime = new CoolingPadControlRuntime(evidenceStore, new CoolingPadProtocolPortFactory(factory.OpenAsync));
        var decisions = new List<CoolingPadControlStatus>();
        var confirmations = new List<string>();
        string phase = "initialServiceAdmission";
        bool cleanup = false;
        var result = new CoolingPadCounterValidationResult();
        async Task Observe(string stage)
        {
            phase = stage;
            if (!await CoolingPadDeveloperObservation.ConfirmAsync(confirm, stage, token, operatorTimeout, TimeProvider.System).ConfigureAwait(false))
            { throw new InvalidOperationException("OperatorRejected"); }
            confirmations.Add(stage);
        }
        void RequireRunning(CoolingPadControlStatus status)
        {
            decisions.Add(status);
            if (status.State != CoolingPadControlState.Running)
            { throw new InvalidOperationException(status.PauseReason.ToString()); }
        }
        try
        {
            await runtime.ApplySettingsAsync(Settings(2200), token).ConfigureAwait(false);
            RequireRunning(await runtime.ResumeAsync(token).ConfigureAwait(false));
            await Observe("firstDemandPhysicalConfirmation").ConfigureAwait(false);
            // 34 changed demands total. With six startup exchanges, demand ID06 repeats,
            // followed by acknowledged demand IDs07 and08. Derive success from actual assignments.
            for (int i = 1; i <= 33; i++)
            {
                phase = "demandUpdate";
                await delay(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await runtime.ApplySettingsAsync(Settings(i % 2 == 1 ? 1000 : 2200), token).ConfigureAwait(false);
                RequireRunning(await runtime.TickAsync(token).ConfigureAwait(false));
            }
            phase = "counterEvidence";
            if (!HasRepeatedDemandAndSuccessors(factory.CounterEvidence?.Snapshot() ?? []))
            { throw new InvalidOperationException("RepeatedDemandAndSuccessorsNotAcknowledged"); }
            await Observe("counterIntervalPhysicalConfirmation").ConfigureAwait(false);
            phase = "restoration";
            var restored = await runtime.RestoreForDeveloperValidationAsync(confirm, token, operatorTimeout).ConfigureAwait(false);
            result = result with
            {
                Completed = restored.Completed,
                Restoration = restored,
                FailurePhase = restored.FailurePhase,
                Failure = restored.Failure
            };
        }
        catch (Exception exception)
        {
            result = result with
            {
                FailurePhase = phase,
                Failure = exception is OperationCanceledException ? "Canceled"
                : exception is TimeoutException ? "Timeout" : exception is InvalidOperationException ? exception.Message : "OperationFailed"
            };
        }
        finally
        {
            // Resources only. An uncertain exchange or rejected observation never schedules restoration.
            Task drain = Task.Run(runtime.DrainAsync);
            PendingDisposalRegistry.Shared.Retain(drain);
            try { await drain.WaitAsync(cleanupTimeout).ConfigureAwait(false); cleanup = factory.DeveloperCounterResourcesReleased; } catch { }
        }
        var exchanges = factory.CounterEvidence?.Snapshot() ?? [];
        bool unknown = runtime.GetStatus().SubmissionStateUnknown || exchanges.Any(x => !x.OutcomeAvailable)
            || result.Restoration?.SubmissionStateUnknown == true;
        if (!cleanup && result.Failure is null)
        { result = result with { Completed = false, FailurePhase = "resourceCleanup", Failure = "CleanupIncomplete" }; }
        return result with
        {
            ResourceCleanupCompleted = cleanup,
            Exchanges = exchanges,
            Decisions = decisions.ToArray(),
            ConfirmedStages = confirmations.ToArray(),
            RepeatedDemandIdAndTwoSuccessorsAcknowledged = HasRepeatedDemandAndSuccessors(exchanges),
            SubmissionStateUnknown = unknown,
            ManualAttentionRequired = !result.Completed && (runtime.HasRetainedMutationEvidence || !cleanup)
        };
    }

    private static CoolingPadControlSettings Settings(int rpm) => new(CoolingPadControlMode.FixedDemand, new(rpm));

    internal static bool HasRepeatedDemandAndSuccessors(IReadOnlyList<CoolingPadCounterExchange> exchanges)
    {
        var previous = new HashSet<byte>();
        for (int i = 0; i + 2 < exchanges.Count; i++)
        {
            var current = exchanges[i];
            if (!DemandAck(current)) { continue; }
            if (!previous.Add(current.WireId) && DemandAck(exchanges[i + 1]) && DemandAck(exchanges[i + 2])
                && exchanges[i + 1].Sequence == current.Sequence + 1 && exchanges[i + 2].Sequence == current.Sequence + 2
                && exchanges[i + 1].WireId == Next(current.WireId) && exchanges[i + 2].WireId == Next(Next(current.WireId)))
            { return true; }
        }
        return false;
    }
    private static byte Next(byte id) => id == 0x1E ? (byte)0 : (byte)(id + 1);
    private static bool DemandAck(CoolingPadCounterExchange item) => item.ClassId == 0x0D && item.CommandId == 1
        && item.OutcomeAvailable && item.Acknowledged && item.Failure is null;
}
