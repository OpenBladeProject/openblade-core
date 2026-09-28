namespace OpenBlade.Protocol;

internal sealed record Rz090528LoadedReading(DateTimeOffset CpuSourceTimestampUtc,
    double CpuSourceAgeMilliseconds, double? CpuCelsius, int? GpuCelsius,
    bool RunningExact, string? WorkloadIdentity, uint? GpuUtilizationPercent, string? Failure);
internal sealed record Rz090528LoadedSample(DateTimeOffset ObservedAtUtc, double ElapsedMilliseconds,
    Rz090528LoadedReading Reading, FanRpmReadings? Fans);
internal sealed record Rz090528LoadedResult(bool Completed, string? Failure, bool PermitKnownStateRestore,
    IReadOnlyList<Rz090528LoadedSample> Baseline, IReadOnlyList<Rz090528LoadedSample> Qualification,
    Rz090528LoadedSample? FinalAdmission, IReadOnlyList<Rz090528LoadedSample> Samples,
    double ObservationElapsedMilliseconds, double? CandidateApplyMilliseconds,
    double? ConfirmedCandidateToRestorationReadbackMilliseconds, bool ObservationTimingUncertain, bool SnapshotQuiesced);

// A reviewed observation length, workload requirement and experimental tripwire. The ten-second profile is the original plan.
internal sealed record Rz090528LoadedProfile(string Name, string PlanResource, string PlanSha256,
    int ObservationMilliseconds, bool RequiresWorkload, double AbortCpuCelsius, int AbortGpuCelsius)
{
    internal static readonly Rz090528LoadedProfile TenSecondLoaded = new("Loaded10",
        "OpenBlade.Protocol.Fixtures.Rz09_0528_02c6_2_02_PadlessHyperBoostLoadedPlan.json",
        Rz090528PadlessLoadedValidation.PlanSha256, 10000, true, 75, 75);
    internal static readonly Rz090528LoadedProfile SixtySecondLoaded = new("Loaded60",
        "OpenBlade.Protocol.Fixtures.Rz09_0528_02c6_2_02_PadlessHyperBoostLoaded60Plan.json",
        Rz090528PadlessLoadedValidation.Loaded60PlanSha256, 60000, true, 85, 80);
    internal static readonly Rz090528LoadedProfile SixtySecondNoLoad = new("NoLoad60",
        "OpenBlade.Protocol.Fixtures.Rz09_0528_02c6_2_02_PadlessHyperBoostNoLoad60Plan.json",
        Rz090528PadlessLoadedValidation.NoLoad60PlanSha256, 60000, false, 85, 80);
}

// Exact-device developer experiment. These entry/abort values are experimental tripwires, not ratings.
internal sealed class Rz090528PadlessLoadedValidation(
    Func<CancellationToken, ValueTask<Rz090528LoadedReading>> read,
    Func<DateTimeOffset, string?> arm, Action<DateTimeOffset>? armed = null,
    TimeProvider? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null,
    Rz090528LoadedProfile? profile = null)
{
    internal const string PlanSha256 = "FF5258CEDB14E09DFD9BF9C0F33030044E7F646F2088DAA3635240FABA37A029";
    internal const string Loaded60PlanSha256 = "AA1C670D89373C3823236BDFB6458747F2002DB8175EBE325D6845C9529ABDF7";
    internal const string NoLoad60PlanSha256 = "D56C277FD3B284EF8229435F16B5DEDFF3D3A8E00E41675A6B5E516ED11AC2FD";
    private readonly Rz090528LoadedProfile plan = profile ?? Rz090528LoadedProfile.TenSecondLoaded;
    internal Rz090528LoadedProfile Profile => plan;
    internal static byte[] ReadPlan(Rz090528LoadedProfile? profile = null)
    {
        profile ??= Rz090528LoadedProfile.TenSecondLoaded;
        using Stream resource = typeof(Rz090528PadlessLoadedValidation).Assembly.GetManifestResourceStream(
            profile.PlanResource) ?? throw new InvalidOperationException("Loaded plan missing.");
        using var bytes = new MemoryStream();
        resource.CopyTo(bytes);
        byte[] value = bytes.ToArray();
        ValidatePlan(value, profile);
        return value;
    }
    internal static void ValidatePlan(ReadOnlySpan<byte> value, Rz090528LoadedProfile? profile = null)
    {
        if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(value)) != (profile ?? Rz090528LoadedProfile.TenSecondLoaded).PlanSha256)
        {
            throw new InvalidOperationException("Loaded plan hash mismatch.");
        }
    }
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? Task.Delay;
    private readonly List<Rz090528LoadedSample> baseline = [];
    private readonly List<Rz090528LoadedSample> qualification = [];
    private readonly List<Rz090528LoadedSample> samples = [];
    private Rz090528LoadedSample? admission;
    private string? bound;
    private long? applyStarted, candidateConfirmed;
    private double? applyMilliseconds, restoredMilliseconds;
    private int prepared;
    private bool timingUncertain;
    private Task<Rz090528NoLoadSnapshot>? pendingSnapshot;
    private bool snapshotClosed;
    private Rz090528LoadedResult? result;
    internal bool SnapshotQuiesced => pendingSnapshot is null || pendingSnapshot.IsCompleted;
    internal Rz090528LoadedResult? Result
    {
        get => result is null ? null : result with { SnapshotQuiesced = SnapshotQuiesced };
        private set => result = value;
    }

    internal void MarkApplyStarted() => applyStarted = time.GetTimestamp();
    internal void MarkCandidateConfirmed()
    {
        candidateConfirmed = time.GetTimestamp();
        applyMilliseconds = applyStarted is { } started ? Elapsed(started) : null;
    }
    internal void MarkRestorationReadback()
    {
        restoredMilliseconds = candidateConfirmed is { } started ? Elapsed(started) : null;
        if (Result is { } result)
        {
            Result = result with
            {
                CandidateApplyMilliseconds = applyMilliseconds,
                ConfirmedCandidateToRestorationReadbackMilliseconds = restoredMilliseconds
            };
        }
    }

    internal async Task<bool> PrepareAsync(IRz090528NoLoadSnapshotSource source, CancellationToken token)
    {
        _ = ReadPlan(plan);
        long start = time.GetTimestamp();
        if (Interlocked.Exchange(ref prepared, 1) != 0)
        {
            return Finish(false, "AlreadyPrepared", false, start).Completed;
        }
        try
        {
            // Prove that the specifically identified CPU source advances before inviting a workload.
            for (int index = 0; index < 2; index++)
            {
                if (index > 0) { await wait(TimeSpan.FromSeconds(2), token).ConfigureAwait(false); }
                var value = await ReadAsync(token).ConfigureAwait(false);
                string? failure = TelemetryFailure(value, entry: true) ?? (plan.RequiresWorkload ? null : NoLoadFailure(value));
                if (failure is not null) { return Finish(false, failure, false, start).Completed; }
                if (index > 0 && value.CpuSourceTimestampUtc <= baseline[0].Reading.CpuSourceTimestampUtc)
                {
                    return Finish(false, "CpuSourceDidNotAdvance", false, start).Completed;
                }
                baseline.Add(new(time.GetUtcNow(), Elapsed(start), value, null));
            }
            if (!plan.RequiresWorkload)
            {
                // No workload is invited: admit directly from a fresh paired snapshot and a fresh idle reading.
                var idleProtocol = await SnapshotAsync(source, true, token, 2000).ConfigureAwait(false);
                // Idle fans may be stopped; a valid zero-RPM readback admits entry. Every snapshot in mode 7 still requires positive fans.
                if (idleProtocol.Failure is not null || idleProtocol.Fans is not { Selector1Rpm: >= 0, Selector2Rpm: >= 0 })
                {
                    return Finish(false, idleProtocol.Failure ?? "EntryFanReadbackInvalid", false, start).Completed;
                }
                var idle = await ReadAsync(token).ConfigureAwait(false);
                string? idleFailure = TelemetryFailure(idle, entry: true) ?? NoLoadFailure(idle);
                if (idleFailure is not null) { return Finish(false, idleFailure, false, start).Completed; }
                admission = new(time.GetUtcNow(), Elapsed(start), idle, idleProtocol.Fans);
                Finish(false, "AwaitingPairedCandidate", false, start);
                return true;
            }
            string? armFailure = arm(time.GetUtcNow());
            if (armFailure is not null) { return Finish(false, armFailure, false, start).Completed; }
            armed?.Invoke(time.GetUtcNow());
            long armingStarted = time.GetTimestamp();
            int consecutive = 0;
            double previousCompleted = -2000;
            while (Elapsed(armingStarted) < 180000)
            {
                await WaitUntil(armingStarted, previousCompleted + 2000, token).ConfigureAwait(false);
                if (Elapsed(armingStarted) >= 180000) { break; }
                var value = await ReadAsync(token).ConfigureAwait(false);
                if (Elapsed(armingStarted) >= 180000) { return Finish(false, "WorkloadQualificationTimedOut", false, start).Completed; }
                string? failure = TelemetryFailure(value, entry: true);
                bool outsideEntryEnvelope = failure == "ExperimentalEntryEnvelopeExceeded";
                if (failure is not null && !outsideEntryEnvelope) { return Finish(false, failure, false, start).Completed; }
                // Stock-mode warmup may cool within the arming window, but cannot conceal a lost bound process.
                if (bound is not null && (!value.RunningExact || value.WorkloadIdentity != bound))
                {
                    return Finish(false, "BoundWorkloadLost", false, start).Completed;
                }
                if (!value.RunningExact || value.GpuUtilizationPercent is not (>= 80 and <= 100) || outsideEntryEnvelope)
                {
                    if (bound is not null && (!value.RunningExact || value.WorkloadIdentity != bound))
                    {
                        return Finish(false, "BoundWorkloadLost", false, start).Completed;
                    }
                    consecutive = 0;
                    previousCompleted = Elapsed(armingStarted);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(value.WorkloadIdentity) || bound is not null && bound != value.WorkloadIdentity)
                {
                    return Finish(false, "WorkloadIdentityChanged", false, start).Completed;
                }
                bound ??= value.WorkloadIdentity;
                qualification.Add(new(time.GetUtcNow(), Elapsed(armingStarted), value, null));
                consecutive++;
                previousCompleted = Elapsed(armingStarted);
                if (consecutive == 3) { break; }
            }
            if (consecutive != 3) { return Finish(false, "WorkloadQualificationTimedOut", false, start).Completed; }
            // This is the only thermal/workload admission hook for the bounded paired write phase.
            var finalProtocol = await SnapshotAsync(source, true, token, 2000).ConfigureAwait(false);
            if (finalProtocol.Failure is not null || !PositiveFans(finalProtocol.Fans))
            {
                return Finish(false, finalProtocol.Failure ?? "EntryFanNotPositive", false, start).Completed;
            }
            var final = await ReadAsync(token).ConfigureAwait(false);
            string? finalFailure = TelemetryFailure(final, entry: true) ?? WorkloadFailure(final);
            if (finalFailure is not null) { return Finish(false, finalFailure, false, start).Completed; }
            if (Elapsed(armingStarted) >= 180000) { return Finish(false, "WorkloadQualificationTimedOut", false, start).Completed; }
            admission = new(time.GetUtcNow(), Elapsed(start), final, finalProtocol.Fans);
            Finish(false, "AwaitingPairedCandidate", false, start);
            return true;
        }
        catch (OperationCanceledException) { Finish(false, "CanceledBeforeApply", false, start); return false; }
        catch { Finish(false, "EntryObservationFailed", false, start); return false; }
    }

    internal async Task<Rz090528LoadedResult> RunAsync(IRz090528NoLoadSnapshotSource source, CancellationToken token)
    {
        long start = candidateConfirmed ?? time.GetTimestamp();
        if (admission is null || candidateConfirmed is null)
        {
            return Finish(false, "CandidateNotPrepared", false, start);
        }
        try
        {
            int window = plan.ObservationMilliseconds;
            // Paired snapshots every two seconds, then one external-only sample two seconds before the deadline.
            for (int index = 0; index < window / 2000 - 1; index++)
            {
                await WaitUntil(start, index * 2000, token).ConfigureAwait(false);
                double remaining = window - Elapsed(start);
                if (remaining < 2000 || samples.Count > 0 && Elapsed(start) - samples[^1].ElapsedMilliseconds > 4000)
                {
                    return Finish(false, "ObservationDeadlineMissed", true, start);
                }
                // Read external telemetry first: a hot/stale sample must not wait behind optional HID queries.
                var value = await ReadAsync(token, Math.Min(2000, remaining)).ConfigureAwait(false);
                samples.Add(new(time.GetUtcNow(), Elapsed(start), value, null));
                string? failure = TelemetryFailure(value, entry: false) ?? LoadFailure(value);
                if (failure is not null) { return Finish(false, failure, true, start); }
                remaining = window - Elapsed(start);
                if (remaining < 2000) { return Finish(false, "InsufficientSnapshotBudget", true, start); }
                var protocol = await SnapshotAsync(source, false, token, 2000).ConfigureAwait(false);
                if (protocol.Failure is not null)
                {
                    return Finish(false, protocol.Failure, protocol.PermitKnownStateRestore, start);
                }
                samples[^1] = new(time.GetUtcNow(), Elapsed(start), value, protocol.Fans);
                if (!PositiveFans(protocol.Fans)) { return Finish(false, "FanNotPositive", true, start); }
                failure = TelemetryFailure(value, entry: false);
                if (failure is not null) { return Finish(false, failure, true, start); }
                if (Elapsed(start) >= window) { return Finish(false, "ObservationDeadlineMissed", true, start); }
            }
            // Two seconds before the deadline observe external thermal/workload state without spending more HID budget.
            await WaitUntil(start, window - 2000, token).ConfigureAwait(false);
            double finalBudget = window - Elapsed(start);
            if (finalBudget <= 0) { return Finish(false, "ObservationDeadlineMissed", true, start); }
            var finalExternal = await ReadAsync(token, Math.Min(2000, finalBudget)).ConfigureAwait(false);
            samples.Add(new(time.GetUtcNow(), Elapsed(start), finalExternal, null));
            string? finalFailure = TelemetryFailure(finalExternal, entry: false) ?? LoadFailure(finalExternal);
            if (finalFailure is not null) { return Finish(false, finalFailure, true, start); }
            if (Elapsed(start) >= window) { return Finish(false, "ObservationDeadlineMissed", true, start); }
            await WaitUntil(start, window, token).ConfigureAwait(false);
            return Elapsed(start) <= window + 250 ? Finish(true, null, true, start)
                : Finish(false, "ObservationDeadlineMissed", true, start);
        }
        catch (OperationCanceledException) { return Finish(false, "CanceledDuringObservation", true, start); }
        catch { return Finish(false, "UnexpectedObservationFailure", false, start); }
    }

    private string? TelemetryFailure(Rz090528LoadedReading value, bool entry)
    {
        if (value.Failure is not null) { return value.Failure; }
        double actualAge = (time.GetUtcNow() - value.CpuSourceTimestampUtc).TotalMilliseconds;
        if (!double.IsFinite(value.CpuSourceAgeMilliseconds) || value.CpuSourceAgeMilliseconds is < 0 or > 4000
            || actualAge is < 0 or > 4000) { return "CpuJsonNotFresh"; }
        if (value.CpuCelsius is not { } cpu || !double.IsFinite(cpu) || cpu <= 0 || value.GpuCelsius is not > 0)
        {
            return "CpuOrGpuTelemetryUnavailable";
        }
        if (entry && (cpu > 70 || value.GpuCelsius > 72)) { return "ExperimentalEntryEnvelopeExceeded"; }
        if (!entry && (cpu >= plan.AbortCpuCelsius || value.GpuCelsius >= plan.AbortGpuCelsius)) { return "ExperimentalTemperatureTripwire"; }
        return null;
    }
    private string? LoadFailure(Rz090528LoadedReading value) => plan.RequiresWorkload ? WorkloadFailure(value) : NoLoadFailure(value);
    // No-load: no benchmark process may appear and the GPU must stay idle.
    private static string? NoLoadFailure(Rz090528LoadedReading value) =>
        value.RunningExact || value.GpuUtilizationPercent is not <= 10 ? "NoLoadConditionLost" : null;
    private string? WorkloadFailure(Rz090528LoadedReading value) => !value.RunningExact
        || string.IsNullOrWhiteSpace(bound) || value.WorkloadIdentity != bound ? "BoundWorkloadLost"
        : value.GpuUtilizationPercent is not (>= 80 and <= 100) ? "QualifyingGpuLoadLost" : null;
    private static bool PositiveFans(FanRpmReadings? value) => value is { Selector1Rpm: > 0, Selector2Rpm: > 0 };
    private async Task<Rz090528LoadedReading> ReadAsync(CancellationToken token, double milliseconds = 2000)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, milliseconds)));
        long started = time.GetTimestamp();
        try
        {
            var value = await read(bounded.Token).AsTask().WaitAsync(bounded.Token).ConfigureAwait(false);
            return Elapsed(started) > milliseconds || bounded.IsCancellationRequested
                ? new(default, double.NaN, null, null, false, null, null, "ExternalTelemetryReadTimedOut") : value;
        }
        catch { return new(default, double.NaN, null, null, false, null, null, "ExternalTelemetryReadFailed"); }
    }
    private async Task<Rz090528NoLoadSnapshot> SnapshotAsync(IRz090528NoLoadSnapshotSource source,
        bool baseline, CancellationToken token, double milliseconds)
    {
        if (snapshotClosed || !SnapshotQuiesced)
        {
            return new(null, "SnapshotLaneClosed", false);
        }
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, milliseconds)));
        CancellationToken snapshotToken = bounded.Token;
        long started = time.GetTimestamp();
        // Own one operation across the synchronous native-guard prefix. Never dispatch another after uncertainty.
        pendingSnapshot = Task.Run(async () =>
        {
            snapshotToken.ThrowIfCancellationRequested();
            return await source.ReadAsync(baseline, snapshotToken).ConfigureAwait(false);
        }, CancellationToken.None);
        _ = pendingSnapshot.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            var observed = await pendingSnapshot.WaitAsync(snapshotToken).ConfigureAwait(false);
            if (Elapsed(started) > milliseconds || bounded.IsCancellationRequested)
            {
                timingUncertain = true;
                snapshotClosed = true;
                if (source is Rz090528PadlessHyperBoostValidation.GuardedSession guarded) { guarded.MarkSnapshotUncertain(); }
                return new(null, "ProtocolSnapshotDeadlineExceeded", false);
            }
            snapshotClosed |= observed.Failure is not null;
            return observed;
        }
        catch
        {
            snapshotClosed = true;
            timingUncertain |= bounded.IsCancellationRequested || Elapsed(started) > milliseconds;
            if (source is Rz090528PadlessHyperBoostValidation.GuardedSession guarded) { guarded.MarkSnapshotUncertain(); }
            return new(null, "ProtocolSnapshotUncertain", false);
        }
    }
    private async Task WaitUntil(long start, double milliseconds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        double remaining;
        while ((remaining = milliseconds - Elapsed(start)) > 0)
        {
            await wait(TimeSpan.FromMilliseconds(remaining), token).ConfigureAwait(false);
        }
    }
    private double Elapsed(long start) => time.GetElapsedTime(start).TotalMilliseconds;
    private Rz090528LoadedResult Finish(bool completed, string? failure, bool restore, long start) => Result = new(
        completed, failure, restore, baseline.ToArray(), qualification.ToArray(), admission, samples.ToArray(),
        candidateConfirmed is null ? 0 : Elapsed(start), applyMilliseconds, restoredMilliseconds,
        timingUncertain || candidateConfirmed is not null && Elapsed(start) > plan.ObservationMilliseconds + 250, SnapshotQuiesced);
}
