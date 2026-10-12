using OpenBlade.Contracts;
using OpenBlade.Protocol;
using OpenBlade.Service;

namespace OpenBlade.Capture;

internal sealed record CoolingPadCurveTrialSample(int Index, string Stage, int? CpuCelsius,
    int? GpuCelsius, bool GpuInactive, double HostAcquisitionAgeMilliseconds,
    int? PlannedConfiguredDemandRpm, string Reason)
{
    public double? AgeBeforeSubmissionMilliseconds { get; init; }
    public bool SubmissionAttempted { get; init; }
    public bool Acknowledged { get; init; }
    public bool SuppressedUnchanged { get; init; }
}

internal sealed record CoolingPadCurveTrialResult
{
    public IReadOnlyList<CoolingPadCurveTrialSample> Samples { get; init; } = [];
    public string? RawBaseline { get; init; }
    public string? RawCandidate { get; init; }
    public string? RawRestored { get; init; }
    public bool ModeAcquireAttempted { get; init; }
    public bool ModeAcquireAcknowledged { get; init; }
    public bool DemandEntryAttempted { get; init; }
    public bool DemandEntryAcknowledged { get; init; }
    public bool FirstDemandPhysicalConfirmed { get; init; }
    public bool CurvePhysicalConfirmed { get; init; }
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
    public bool TemperatureSourceConstructionStarted { get; init; }
    public bool TemperatureSourceDisposalConfirmed { get; init; }
    public string? FailurePhase { get; init; }
    public string? Failure { get; init; }
    public bool SubmissionStateUnknown => ModeAcquireAttempted && !ModeAcquireAcknowledged
        || DemandEntryAttempted && !DemandEntryAcknowledged || FixedAttempted && !FixedAcknowledged
        || AutoAttempted && !AutoAcknowledged || ModeRestoreAttempted && !ModeRestoreAcknowledged
        || Samples.Any(sample => sample.SubmissionAttempted && !sample.Acknowledged);
    public bool ManualRecoveryRequired => (ModeAcquireAttempted || DemandEntryAttempted || FixedAttempted
        || AutoAttempted || ModeRestoreAttempted || Samples.Any(sample => sample.SubmissionAttempted))
        && !(RawRestorationConfirmed && FinalPhysicalConfirmed);
    public bool ProductionAdmitted => false;
    public bool MeasuredRpmConfirmed => false;
    public bool OwnershipConfirmed => false;
    public bool HandbackConfirmed => false;
    internal bool Success => Failure is null && RawRestorationConfirmed && FinalPhysicalConfirmed;
}

// One attended, finite experiment. Source age is host acquisition age, not a
// sensor-origin timestamp. No retry, automatic handback, or firmware cleanup.
internal sealed class CoolingPadCurveTrialRunner(
    Func<HostIdentityReadResult> readHost,
    Func<IFanTemperatureSource> createSource,
    Func<IValidationActorFactory> createActorFactory,
    Func<ValidationRunContext> createContext,
    TimeProvider clock,
    PendingDisposalRegistry? pendingOperations = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    TimeSpan? physicalTimeout = null)
{
    internal const int UpdateSamples = 10;
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan Cadence = TimeSpan.FromSeconds(2);
    private readonly PendingDisposalRegistry pending = pendingOperations ?? PendingDisposalRegistry.Shared;
    private int started;
    internal static CoolingPadDemandCurve CreateCpuCurve() => new([
        new CoolingPadCurvePoint(40, new CoolingPadConfiguredRpmDemand(1000)),
        new CoolingPadCurvePoint(90, new CoolingPadConfiguredRpmDemand(2200))]);
    internal static CoolingPadDemandCurve CreateGpuCurve() => new([
        new CoolingPadCurvePoint(30, new CoolingPadConfiguredRpmDemand(1000)),
        new CoolingPadCurvePoint(80, new CoolingPadConfiguredRpmDemand(2200))]);

    internal async Task<CoolingPadCurveTrialResult> RunAsync(
        Func<CancellationToken, Task<bool>> confirmFirstDemand,
        Func<CancellationToken, Task<bool>> confirmCurve,
        Func<CancellationToken, Task<bool>> confirmFixed,
        Func<CancellationToken, Task<bool>> confirmQuiet,
        Func<CancellationToken, Task<bool>> confirmFinal,
        CancellationToken token)
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
        { return new() { FailurePhase = "open", Failure = "Disposed" }; }
        var samples = new List<CoolingPadCurveTrialSample>();
        var result = new CoolingPadCurveTrialResult { Samples = samples.AsReadOnly() };
        var cpu = CreateCpuCurve();
        var gpu = CreateGpuCurve();
        var sourceCancellation = new CancellationTokenSource();
        Task<IFanTemperatureSource>? opening = null;
        Task<FanTemperatureReadings>? reading = null;
        ValidationActor? actor = null;
        string phase = "hostIdentity";
        try
        {
            token.ThrowIfCancellationRequested();
            Task<HostIdentityReadResult> identity = Task.Run(readHost, CancellationToken.None);
            pending.Retain(identity);
            HostIdentityReadResult host = await identity.WaitAsync(MaximumAge, clock, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (host.Failure is not null || host.Evidence is not
                { Manufacturer: "Razer", ProductName: "Blade 16 - RZ09-0528", Sku: "RZ09-05289EN4" })
            { throw new TrialFailure("HostIdentityBlocked"); }

            phase = "sourceOpen";
            result = result with { TemperatureSourceConstructionStarted = true };
            opening = Task.Run(() =>
            {
                sourceCancellation.Token.ThrowIfCancellationRequested();
                return createSource() ?? throw new InvalidOperationException();
            }, CancellationToken.None);
            IFanTemperatureSource source = await opening.WaitAsync(MaximumAge, clock, token).ConfigureAwait(false);
            if (source is not IDisposable) { throw new TrialFailure("DisposableSourceRequired"); }

            async Task<(int Index, long Started, FanTemperatureReadings Readings)> Read(string stage)
            {
                phase = stage;
                token.ThrowIfCancellationRequested();
                long timestamp = clock.GetTimestamp();
                reading = Task.Run(async () =>
                {
                    sourceCancellation.Token.ThrowIfCancellationRequested();
                    return await source.ReadAsync(sourceCancellation.Token).ConfigureAwait(false);
                }, CancellationToken.None);
                FanTemperatureReadings temperatures = await reading.WaitAsync(MaximumAge, clock, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                TimeSpan age = clock.GetElapsedTime(timestamp);
                CoolingPadCurveDecision plan = CoolingPadCurvePlanner.Plan(cpu, gpu, temperatures, age, MaximumAge);
                int index = samples.Count;
                samples.Add(new(index, stage, temperatures.CpuCelsius, temperatures.GpuCelsius,
                    temperatures.GpuInactive, age.TotalMilliseconds, plan.Demand?.Rpm, plan.Reason.ToString()));
                if (plan.Demand is null) { throw new TrialFailure(plan.Reason.ToString()); }
                return (index, timestamp, temperatures);
            }

            await Read("preflightTelemetry").ConfigureAwait(false);
            phase = "open";
            token.ThrowIfCancellationRequested();
            ValidationRunContext context = createContext();
            byte[] transactions = new byte[19];
            for (int index = 0; index < transactions.Length; index++)
            {
                if (!context.TryAllocateTransaction(out transactions[index])) { throw new TrialFailure("Exhausted"); }
            }
            var opened = await createActorFactory().OpenAsync(context).ConfigureAwait(false);
            actor = opened.Value;
            if (opened.Failure is not null || actor is null)
            { throw new TrialFailure((opened.Failure ?? HidSessionFailureKind.NativeIoFailure).ToString()); }

            async Task<ReadOnlyMemory<byte>> Exchange(RazerCommandSpec command, int transactionIndex)
            {
                token.ThrowIfCancellationRequested();
                var response = await actor.Session.ExecuteAsync(command,
                    () => TransactionIdAssignment.Valid(transactions[transactionIndex]), TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                if (response.Failure is not null) { throw new TrialFailure(response.Failure.Value.ToString()); }
                if (command.PayloadValidator?.Invoke(response.Value) != true) { throw new TrialFailure("UnexpectedPayload"); }
                return response.Value;
            }
            async Task Observe(string name, Func<CancellationToken, Task<bool>> observer)
            {
                phase = name;
                try
                {
                    bool confirmed = await CoolingPadFanProcessContinuityCommand.ConfirmAsync(
                        (_, observationToken) => observer(observationToken), name, token,
                        physicalTimeout ?? TimeSpan.FromSeconds(180), clock, pending).ConfigureAwait(false);
                    if (!confirmed) { throw new TrialFailure("OperatorRejected"); }
                }
                catch (TimeoutException) { throw new TrialFailure("OperatorTimeout"); }
                catch (OperationCanceledException) { throw; }
                catch (TrialFailure) { throw; }
                catch { throw new TrialFailure("OperatorException"); }
            }
            CoolingPadConfiguredRpmDemand Fresh((int Index, long Started, FanTemperatureReadings Readings) sample)
            {
                token.ThrowIfCancellationRequested();
                TimeSpan age = clock.GetElapsedTime(sample.Started);
                CoolingPadCurveDecision plan = CoolingPadCurvePlanner.Plan(cpu, gpu, sample.Readings, age, MaximumAge);
                samples[sample.Index] = samples[sample.Index] with { AgeBeforeSubmissionMilliseconds = age.TotalMilliseconds };
                if (plan.Demand is not { } demand)
                {
                    samples[sample.Index] = samples[sample.Index] with { PlannedConfiguredDemandRpm = null, Reason = plan.Reason.ToString() };
                    throw new TrialFailure(plan.Reason.ToString());
                }
                return demand;
            }
            async Task Demand((int Index, long Started, FanTemperatureReadings Readings) sample, int transactionIndex)
            {
                CoolingPadConfiguredRpmDemand demand = Fresh(sample);
                samples[sample.Index] = samples[sample.Index] with { SubmissionAttempted = true };
                await Exchange(CoolingPadHostDemandProtocol.CreateSpec(demand) with
                { SubmissionAllowed = () => AgeAllowsSubmission(sample.Started) }, transactionIndex).ConfigureAwait(false);
                samples[sample.Index] = samples[sample.Index] with { Acknowledged = true };
            }
            bool AgeAllowsSubmission(long timestamp)
            {
                TimeSpan age = clock.GetElapsedTime(timestamp);
                return !token.IsCancellationRequested && age >= TimeSpan.Zero && age < MaximumAge;
            }

            phase = "baselineRead";
            result = result with { RawBaseline = Convert.ToHexString((await Exchange(Query(), 0).ConfigureAwait(false)).Span) };
            if (result.RawBaseline != "0000") { throw new TrialFailure("BaselineNot0000"); }
            phase = "modeAcquire";
            token.ThrowIfCancellationRequested();
            result = result with { ModeAcquireAttempted = true };
            await Exchange(Echo(0x00, 0x04, [0x03, 0x00]), 1).ConfigureAwait(false);
            result = result with { ModeAcquireAcknowledged = true };
            phase = "candidateRead";
            result = result with { RawCandidate = Convert.ToHexString((await Exchange(Query(), 2).ConfigureAwait(false)).Span) };
            if (result.RawCandidate != "0300") { throw new TrialFailure("CandidateMismatch"); }

            var initial = await Read("initialDemandTelemetry").ConfigureAwait(false);
            phase = "demandEntry";
            Fresh(initial);
            result = result with { DemandEntryAttempted = true };
            await Exchange(Echo(0x0D, 0x10, [0x00, 0x06, 0x00]) with
            { SubmissionAllowed = () => AgeAllowsSubmission(initial.Started) }, 3).ConfigureAwait(false);
            result = result with { DemandEntryAcknowledged = true };
            phase = "initialDemand";
            // No human pause; recheck acquisition age after entry acknowledgement.
            await Demand(initial, 4).ConfigureAwait(false);
            await Observe("firstDemandPhysicalConfirmation", confirmFirstDemand).ConfigureAwait(false);
            result = result with { FirstDemandPhysicalConfirmed = true };
            int lastAcknowledgedDemand = samples[initial.Index].PlannedConfiguredDemandRpm!.Value;
            for (int update = 0; update < UpdateSamples; update++)
            {
                phase = "curveCadence";
                token.ThrowIfCancellationRequested();
                await (delay is null ? Task.Delay(Cadence, clock, token) : delay(Cadence, token)).ConfigureAwait(false);
                var sample = await Read("curveTelemetry").ConfigureAwait(false);
                phase = "curveDemand";
                CoolingPadConfiguredRpmDemand demand = Fresh(sample);
                if (demand.Rpm == lastAcknowledgedDemand)
                { samples[sample.Index] = samples[sample.Index] with { SuppressedUnchanged = true }; }
                else
                {
                    await Demand(sample, 5 + update).ConfigureAwait(false);
                    lastAcknowledgedDemand = demand.Rpm;
                }
            }
            await Observe("curvePhysicalConfirmation", confirmCurve).ConfigureAwait(false);
            result = result with { CurvePhysicalConfirmed = true };
            phase = "fixed";
            token.ThrowIfCancellationRequested();
            result = result with { FixedAttempted = true };
            await Exchange(Echo(0x0D, 0x10, [0x01, 0x01, 0x2C]), 15).ConfigureAwait(false);
            result = result with { FixedAcknowledged = true };
            await Observe("fixedPhysicalConfirmation", confirmFixed).ConfigureAwait(false);
            result = result with { FixedPhysicalConfirmed = true };
            phase = "auto";
            token.ThrowIfCancellationRequested();
            result = result with { AutoAttempted = true };
            await Exchange(Echo(0x0D, 0x10, [0x00, 0x06, 0x00]), 16).ConfigureAwait(false);
            result = result with { AutoAcknowledged = true };
            await Observe("autoPhysicalConfirmation", confirmQuiet).ConfigureAwait(false);
            result = result with { AutoPhysicalConfirmed = true };
            phase = "modeRestore";
            token.ThrowIfCancellationRequested();
            result = result with { ModeRestoreAttempted = true };
            await Exchange(Echo(0x00, 0x04, [0x00, 0x00]), 17).ConfigureAwait(false);
            result = result with { ModeRestoreAcknowledged = true };
            phase = "restorationRead";
            result = result with { RawRestored = Convert.ToHexString((await Exchange(Query(), 18).ConfigureAwait(false)).Span) };
            if (result.RawRestored != "0000") { throw new TrialFailure("RestorationMismatch"); }
            result = result with { RawRestorationConfirmed = true };
            await Observe("finalPhysicalConfirmation", confirmFinal).ConfigureAwait(false);
            result = result with { FinalPhysicalConfirmed = true };
        }
        catch (TrialFailure ex) { result = result with { FailurePhase = phase, Failure = ex.Message }; }
        catch (OperationCanceledException) { result = result with { FailurePhase = phase, Failure = "Canceled" }; }
        catch (TimeoutException) { result = result with { FailurePhase = phase, Failure = "ObservationTimeout" }; }
        catch { result = result with { FailurePhase = phase, Failure = "OperationFailed" }; }
        finally
        {
            if (actor is not null)
            {
                try { await BoundedValidationActorDisposal.DisposeAsync(actor, clock, MaximumAge, pending).ConfigureAwait(false); }
                catch { }
            }
            Task cleanup = Task.Run(async () =>
            {
                Task notification = sourceCancellation.CancelAsync();
                try
                {
                    if (reading is not null) { try { await reading.ConfigureAwait(false); } catch { } }
                    if (opening is not null)
                    {
                        IFanTemperatureSource source = await opening.ConfigureAwait(false);
                        if (source is IDisposable disposable) { disposable.Dispose(); }
                        else { throw new InvalidOperationException(); }
                    }
                }
                finally
                {
                    try { await notification.ConfigureAwait(false); }
                    finally { sourceCancellation.Dispose(); }
                }
            }, CancellationToken.None);
            try
            {
                await cleanup.WaitAsync(MaximumAge, clock, CancellationToken.None).ConfigureAwait(false);
                result = result with { TemperatureSourceDisposalConfirmed = opening is not null };
            }
            catch
            {
                pending.Retain(cleanup);
                if (result.Failure is null)
                { result = result with { FailurePhase = "sourceCleanup", Failure = "SourceCleanupIncomplete" }; }
            }
        }
        return result;
    }

    private sealed class TrialFailure(string reason) : Exception(reason);
    private static RazerCommandSpec Query() => new(0x00, 0x84, [0x00, 0x00], 2,
        payload => payload.Span.SequenceEqual(new byte[] { 0, 0 }) || payload.Span.SequenceEqual(new byte[] { 3, 0 }));
    private static RazerCommandSpec Echo(byte commandClass, byte commandId, byte[] payload) =>
        new(commandClass, commandId, payload, payload.Length, response => response.Span.SequenceEqual(payload));
}
