using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;
using PadWrite = OpenBlade.Service.CoolingPadWriteResult;

namespace OpenBlade.Capture;

internal sealed record ServiceCurveReading(int Index, FanTemperatureReadings Temperatures, double ReadDurationMilliseconds);
internal sealed record ServiceCurveDecision(string Phase, int Index, CoolingPadControlStatus Status, bool UnchangedDemandSuppressed);
internal sealed record ServiceCurveOperation(string Name, int? ConfiguredDemandRpm, object? Result, int? InferredSuccessfulExchanges);
internal sealed record CoolingPadServiceCurveTrialResult
{
    public bool Completed { get; init; }
    public string? FailurePhase { get; init; }
    public string? Failure { get; init; }
    public bool ManualAttentionRequired { get; init; }
    public bool SubmissionStateUnknown { get; init; }
    public bool SourceDisposed { get; init; }
    public bool ResourceCleanupCompleted { get; init; }
    public CoolingPadControlStatus? FinalStatus { get; init; }
    public IReadOnlyList<string> ConfirmedStages { get; init; } = [];
    public IReadOnlyList<ServiceCurveReading> Readings { get; init; } = [];
    public IReadOnlyList<ServiceCurveDecision> Decisions { get; init; } = [];
    public IReadOnlyList<ServiceCurveOperation> Operations { get; init; } = [];
    public CoolingPadDeveloperRestorationResult? Restoration { get; init; }
    public int? InferredLogicalExchangeCount => Completed && !SubmissionStateUnknown && ResourceCleanupCompleted
        && Operations.All(x => x.InferredSuccessfulExchanges.HasValue)
        ? Operations.Sum(x => x.InferredSuccessfulExchanges!.Value) : null;
}

// Finite developer composition of the actual service coordinator and protocol port.
internal sealed class CoolingPadServiceCurveTrialRunner
{
    internal static CoolingPadControlSettings Settings { get; } = new(CoolingPadControlMode.HostCurve,
        cpuCurve: new CoolingPadDemandCurve(new[] { Point(40, 1000), Point(90, 2200) }),
        gpuCurve: new CoolingPadDemandCurve(new[] { Point(30, 1000), Point(80, 2200) }));
    private static CoolingPadCurvePoint Point(int temperature, int rpm) => new(temperature, new(rpm));
    private readonly Func<IFanTemperatureSource> createSource;
    private readonly Func<ICoolingPadControlPortFactory> createPorts;
    private readonly TimeProvider clock;
    private readonly Func<CancellationToken, Task> delay;
    private readonly TimeSpan operatorTimeout;
    private readonly ICoolingPadMutationEvidenceStore evidenceStore;
    private int started;

    internal CoolingPadServiceCurveTrialRunner(Func<IFanTemperatureSource>? createSource = null,
        Func<ICoolingPadControlPortFactory>? createPorts = null, TimeProvider? clock = null,
        Func<CancellationToken, Task>? delay = null, TimeSpan? operatorTimeout = null,
        ICoolingPadMutationEvidenceStore? evidenceStore = null)
    {
        this.createSource = createSource ?? (() => new SafeWindowsFanTemperatureSource());
        this.createPorts = createPorts ?? CoolingPadDeveloperValidation.CreatePortFactory;
        this.clock = clock ?? TimeProvider.System;
        this.delay = delay ?? (token => Task.Delay(TimeSpan.FromSeconds(2), this.clock, token));
        this.operatorTimeout = operatorTimeout ?? TimeSpan.FromMinutes(10);
        this.evidenceStore = evidenceStore ?? new CoolingPadMutationEvidenceStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OpenBlade"));
    }

    internal static CoolingPadServiceCurveTrialRunner ForTesting(Func<IFanTemperatureSource> source,
        Func<CancellationToken, Task<CoolingPadSessionOpenResult>> open,
        Func<CancellationToken, Task>? delay = null, TimeSpan? operatorTimeout = null) =>
        new(source, () => new CoolingPadProtocolPortFactory(open), delay: delay ?? (_ => Task.CompletedTask),
            operatorTimeout: operatorTimeout, evidenceStore: new InMemoryCoolingPadMutationEvidenceStore());

    internal async Task<CoolingPadServiceCurveTrialResult> RunAsync(
        Func<string, CancellationToken, Task<bool>> confirm, CancellationToken token)
    {
        if (Interlocked.Exchange(ref started, 1) != 0) { throw new InvalidOperationException("SingleUseTrial"); }
        string phase = "warmLoadReady";
        var confirmations = new List<string>();
        var decisions = new List<ServiceCurveDecision>();
        var operations = new System.Collections.Concurrent.ConcurrentQueue<ServiceCurveOperation>();
        var readings = new System.Collections.Concurrent.ConcurrentQueue<ServiceCurveReading>();
        CoolingPadControlRuntime? runtime = null;
        IFanTemperatureSource? source = null;
        Task<IFanTemperatureSource>? opening = null;
        bool disposed = false, cleanupCompleted = false;
        var result = new CoolingPadServiceCurveTrialResult();
        async Task Observe(string stage)
        {
            phase = stage;
            if (!await CoolingPadDeveloperObservation.ConfirmAsync(confirm, stage, token, operatorTimeout, clock).ConfigureAwait(false))
            { throw new InvalidOperationException("OperatorRejected"); }
            confirmations.Add(stage);
        }
        void RequireRunning(CoolingPadControlStatus status)
        {
            if (status.State != CoolingPadControlState.Running)
            { throw new InvalidOperationException(status.PauseReason.ToString()); }
        }
        try
        {
            await Observe("warmLoadReady").ConfigureAwait(false);
            phase = "sourceConstruction";
            opening = Task.Run(createSource, CancellationToken.None);
            source = await opening.WaitAsync(TimeSpan.FromSeconds(5), clock, token).ConfigureAwait(false);
            if (source is not IDisposable) { throw new InvalidOperationException("SourceMustBeDisposable"); }
            phase = "initialServiceAdmission";
            runtime = new CoolingPadControlRuntime(evidenceStore, new RecordingFactory(createPorts(), operations),
                new RecordingSource(source, readings, clock), clock);
            await runtime.ApplySettingsAsync(Settings, token).ConfigureAwait(false);
            var initial = await runtime.ResumeAsync(token).ConfigureAwait(false);
            decisions.Add(new("initial", 0, initial, false));
            RequireRunning(initial);
            await Observe("firstDemandPhysicalConfirmation").ConfigureAwait(false);
            foreach (string interval in new[] { "warm", "cool" })
            {
                if (interval == "cool") { await Observe("coolPhaseReady").ConfigureAwait(false); }
                for (int i = 1; i <= 30; i++)
                {
                    phase = interval + "Sample";
                    await delay(token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    int before = operations.Count;
                    var status = await runtime.TickAsync(token).ConfigureAwait(false);
                    decisions.Add(new(interval, i, status, status.State == CoolingPadControlState.Running && operations.Count == before));
                    RequireRunning(status);
                }
            }
            await Observe("intervalPhysicalConfirmation").ConfigureAwait(false);
            phase = "restoration";
            var restoration = await runtime.RestoreForDeveloperValidationAsync(confirm, token, operatorTimeout).ConfigureAwait(false);
            result = result with
            {
                Restoration = restoration,
                Completed = restoration.Completed,
                FailurePhase = restoration.FailurePhase,
                Failure = restoration.Failure
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
            // Drain first closes the actor independently of a blocked telemetry read.
            // Late cleanup owns the source; it has no route back to planning or writes.
            var cleanup = Task.Run(async () =>
            {
                if (runtime is not null) { await runtime.DrainAsync().ConfigureAwait(false); }
                IFanTemperatureSource? owned = source;
                if (owned is null && opening is not null) { try { owned = await opening.ConfigureAwait(false); } catch { } }
                if (owned is IDisposable disposable) { disposable.Dispose(); disposed = true; }
            });
            PendingDisposalRegistry.Shared.Retain(cleanup);
            try { await cleanup.WaitAsync(TimeSpan.FromSeconds(5), clock).ConfigureAwait(false); cleanupCompleted = true; }
            catch { }
        }
        var final = runtime?.GetStatus();
        var recorded = operations.ToArray();
        bool unknown = final?.SubmissionStateUnknown == true || recorded.Any(x => x.Result switch
        {
            null => x.Name != "query",
            PadWrite write => write.Attempted && !write.Acknowledged,
            CoolingPadEntryResult entry => entry.Entry.Attempted && !entry.Entry.Acknowledged
                || entry.Demand.Attempted && !entry.Demand.Acknowledged,
            _ => false,
        });
        if (!cleanupCompleted && result.Failure is null)
        { result = result with { Completed = false, FailurePhase = "resourceCleanup", Failure = "CleanupIncomplete" }; }
        return result with
        {
            SourceDisposed = disposed,
            ResourceCleanupCompleted = cleanupCompleted,
            FinalStatus = final,
            SubmissionStateUnknown = unknown,
            ManualAttentionRequired = !result.Completed && (runtime is not null || opening is not null),
            ConfirmedStages = confirmations.ToArray(),
            Readings = readings.ToArray(),
            Decisions = decisions.ToArray(),
            Operations = recorded
        };
    }

    private sealed class RecordingSource(IFanTemperatureSource source,
        System.Collections.Concurrent.ConcurrentQueue<ServiceCurveReading> readings, TimeProvider clock) : IFanTemperatureSource
    {
        public async ValueTask<FanTemperatureReadings> ReadAsync(CancellationToken token)
        {
            long start = clock.GetTimestamp();
            var value = await source.ReadAsync(token).ConfigureAwait(false);
            readings.Enqueue(new(readings.Count + 1, value, clock.GetElapsedTime(start).TotalMilliseconds));
            return value;
        }
    }

    private sealed class RecordingFactory(ICoolingPadControlPortFactory factory,
        System.Collections.Concurrent.ConcurrentQueue<ServiceCurveOperation> log) : ICoolingPadControlPortFactory
    {
        public async Task<CoolingPadPortOpenResult> OpenAsync(CancellationToken token)
        {
            var opened = await factory.OpenAsync(token).ConfigureAwait(false);
            return opened with { Port = opened.Port is null ? null : new RecordingPort(opened.Port, log) };
        }
    }

    private sealed class RecordingPort(ICoolingPadControlPort port,
        System.Collections.Concurrent.ConcurrentQueue<ServiceCurveOperation> log) : ICoolingPadControlPort, ICoolingPadDeveloperRestorationPort
    {
        private async Task<T> Record<T>(string name, int? demand, int exchanges, Func<Task<T>> operation, Func<T, bool> success)
        {
            try { var result = await operation().ConfigureAwait(false); log.Enqueue(new(name, demand, result, success(result) ? exchanges : null)); return result; }
            catch { log.Enqueue(new(name, demand, null, null)); throw; }
        }
        private static bool Ack(PadWrite value) => value.Attempted && value.Acknowledged && value.Fault is null;
        public Task<CoolingPadQueryResult> QueryControlStateAsync(CancellationToken token) =>
            Record("query", null, 1, () => port.QueryControlStateAsync(token), value => value.Fault is null);
        public Task<PadWrite> AcquireAsync(Func<bool> allowed, CancellationToken token) =>
            Record("acquireAndInternalQuery", null, 2, () => port.AcquireAsync(allowed, token), Ack);
        public Task<CoolingPadEntryResult> EnterDemandAsync(CoolingPadConfiguredRpmDemand demand, Func<bool> allowed, CancellationToken token) =>
            Record("entryAndDemand", demand.Rpm, 2, () => port.EnterDemandAsync(demand, allowed, token), value => Ack(value.Entry) && Ack(value.Demand));
        public Task<PadWrite> SetDemandAsync(CoolingPadConfiguredRpmDemand demand, Func<bool> allowed, CancellationToken token) =>
            Record("updateDemand", demand.Rpm, 1, () => port.SetDemandAsync(demand, allowed, token), Ack);
        private ICoolingPadDeveloperRestorationPort Restoration => (ICoolingPadDeveloperRestorationPort)port;
        public Task<PadWrite> ReassertMediumAsync(Func<bool> allowed, CancellationToken token) =>
            Record("medium", null, 1, () => Restoration.ReassertMediumAsync(allowed, token), Ack);
        public Task<PadWrite> ReassertQuietAsync(Func<bool> allowed, CancellationToken token) =>
            Record("quiet", null, 1, () => Restoration.ReassertQuietAsync(allowed, token), Ack);
        public Task<PadWrite> RestoreRawAsync(Func<bool> allowed, CancellationToken token) =>
            Record("restoreRawAndQuery", null, 2, () => Restoration.RestoreRawAsync(allowed, token), Ack);
        public ValueTask DisposeAsync() => port.DisposeAsync();
    }
}
