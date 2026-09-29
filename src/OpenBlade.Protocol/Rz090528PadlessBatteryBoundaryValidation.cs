using System.Security.Cryptography;
using OpenBlade.Contracts;

namespace OpenBlade.Protocol;

internal sealed record Rz090528BatteryBoundaryExternal(DateTimeOffset CpuSourceTimestampUtc,
    double CpuSourceAgeMilliseconds, double? CpuCelsius, int? GpuCelsius, uint? GpuUtilizationPercent,
    bool? WindowsAcOnline, bool NoWorkloadConfirmed, bool GpuZeroConfirmed, string? Failure);
internal enum Rz090528BatteryBoundarySignal { ReadyToUnplug, BatteryObservedReconnectNow, AbortReconnectAc }
internal sealed record Rz090528BatteryBoundarySnapshot(string? PowerBefore, PerformanceFanThermalPair? Pair,
    string? PowerAfter, bool PadAbsent, string? Failure);
internal interface IRz090528BatteryBoundarySource
{
    Task<Rz090528BatteryBoundarySnapshot> ReadBatteryBoundaryAsync(CancellationToken token);
    void MarkSnapshotUncertain();
}
internal sealed record Rz090528BatteryBoundaryObservation(DateTimeOffset ObservedAtUtc, double ElapsedMilliseconds,
    Rz090528BatteryBoundaryExternal Before, Rz090528BatteryBoundarySnapshot Snapshot,
    Rz090528BatteryBoundaryExternal After, string? Failure);
internal sealed record Rz090528BatteryBoundaryResult(bool Completed, string? Failure, bool BatteryObserved,
    bool AutomaticReversionObserved, PerformanceFanThermalPair? FinalPerformancePair,
    bool RestoreWriteAttempted, bool RestoreWriteAcknowledged, bool Restored, bool ManualRecoveryRequired,
    bool HardwareQuiesced, bool SnapshotQuiesced, bool ExternalReadQuiesced,
    IReadOnlyList<Rz090528BatteryBoundaryObservation> Observations);
internal sealed record Rz090528BatteryBoundaryDecision(bool TransitionPassed, bool PermitRestore,
    bool AlreadyPerformance, PerformanceFanThermalPair? Pair);

// The observed non-full-AC destination. Full-AC entry, setters and cleanup are identical for every destination.
// AbortPower is a coherent but unplanned off-AC state: it aborts to full-AC cleanup instead of poisoning.
internal sealed record Rz090528PowerBoundaryDestination(string Name, string Power, bool WindowsAcOnline,
    byte[] ObservedModes, string PlanResource, string PlanSha256, string? AbortPower = null)
{
    internal static readonly Rz090528PowerBoundaryDestination Battery = new("Battery", "0011", false, [2, 3, 7],
        "OpenBlade.Protocol.Fixtures.Rz09_0528_02c6_2_02_PadlessBatteryBoundaryPlan.json",
        Rz090528PadlessBatteryBoundaryValidation.PlanSha256);
    internal static readonly Rz090528PowerBoundaryDestination UsbC100W = new("UsbC100W", "0711", true, [0, 2, 3, 7],
        "OpenBlade.Protocol.Fixtures.Rz09_0528_02c6_2_02_PadlessUsbCBoundaryPlan.json",
        Rz090528PadlessBatteryBoundaryValidation.UsbCPlanSha256, AbortPower: "0011");
}

internal sealed class Rz090528PadlessBatteryBoundaryValidation(
    Func<CancellationToken, ValueTask<Rz090528BatteryBoundaryExternal>> readExternal,
    Action<Rz090528BatteryBoundarySignal> signal, TimeProvider? clock = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<bool>? externalQuiesced = null, Func<bool?>? readWindowsAc = null,
    Rz090528PowerBoundaryDestination? destination = null)
{
    internal const string PlanSha256 = "FAEAE270DA1DB0E520A256BD67AE8C1112107D5886D585B22A0B5B4CABECC0E7";
    internal const string UsbCPlanSha256 = "D6E14BCFF2FF64DB6B4548066E2580E0CF217B3D6B7F9B2A7E2EC857FC648B79";
    internal const string Transitional = "PowerTransitionInProgress";
    private readonly Rz090528PowerBoundaryDestination target = destination ?? Rz090528PowerBoundaryDestination.Battery;
    internal Rz090528PowerBoundaryDestination Destination => target;
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? Task.Delay;
    private readonly List<Rz090528BatteryBoundaryObservation> observations = [];
    private Task<Rz090528BatteryBoundarySnapshot>? snapshotOperation;
    private Task<Rz090528BatteryBoundaryExternal>? externalOperation;
    private bool poisoned, externalClosed, prepared, batteryObserved, autoReversion, runArmed, abortSignaled;
    private string? failure;
    private long started;
    internal Rz090528BatteryBoundaryResult? Result { get; private set; }
    private bool SnapshotQuiesced => snapshotOperation?.IsCompleted != false;
    private bool ExternalQuiesced => externalOperation?.IsCompleted != false && (externalQuiesced?.Invoke() ?? true);

    internal static byte[] ReadPlan(Rz090528PowerBoundaryDestination? destination = null)
    {
        destination ??= Rz090528PowerBoundaryDestination.Battery;
        using Stream stream = typeof(Rz090528PadlessBatteryBoundaryValidation).Assembly.GetManifestResourceStream(
            destination.PlanResource) ?? throw new InvalidOperationException("Power boundary plan missing.");
        using var bytes = new MemoryStream(); stream.CopyTo(bytes);
        byte[] value = bytes.ToArray();
        if (Convert.ToHexString(SHA256.HashData(value)) != destination.PlanSha256) { throw new InvalidOperationException("Power boundary plan hash mismatch."); }
        return value;
    }

    internal async Task<bool> PrepareAsync(IRz090528BatteryBoundarySource source, CancellationToken token)
    {
        if (prepared) { return false; }
        prepared = true; started = time.GetTimestamp();
        try
        {
            _ = ReadPlan(target);
            DateTimeOffset? previousCpu = null;
            for (int index = 0; index < 2; index++)
            {
                if (index > 0) { await wait(TimeSpan.FromSeconds(2), token).ConfigureAwait(false); }
                var value = await ObserveAsync(source, token).ConfigureAwait(false);
                string? invalid = value.Failure ?? ThermalFailure(value.Before, true) ?? ThermalFailure(value.After, true);
                if (invalid is not null || value.Snapshot.PowerAfter != "1111" || Mode(value.Snapshot.Pair) != 2
                    || !value.Snapshot.PadAbsent || previousCpu is { } previous && value.After.CpuSourceTimestampUtc <= previous)
                {
                    failure = invalid ?? "EntryPerformanceFullAcRequired"; Update(false, false); return false;
                }
                previousCpu = value.After.CpuSourceTimestampUtc;
            }
            Update(false, false); return true;
        }
        catch { failure ??= "EntryObservationFailed"; Update(false, false); return false; }
    }

    internal async Task<Rz090528BatteryBoundaryDecision> RunAsync(IRz090528BatteryBoundarySource source, CancellationToken token)
    {
        started = time.GetTimestamp();
        if (!prepared || poisoned) { failure = "BoundaryNotPrepared"; return Finish(false, false, false, null); }
        long? fullAcAt = null;
        byte? fullAcMode = null;
        bool recoveryObservation = false;
        try
        {
            runArmed = true; signal(Rz090528BatteryBoundarySignal.ReadyToUnplug);
            while (Elapsed() < 30000)
            {
                var value = await ObserveAsync(source, token, recoveryObservation, boundary: true).ConfigureAwait(false);
                if (value.Failure == Transitional)
                {
                    // Discard the sample and break any stable full-AC streak; the deadline still bounds retries.
                    fullAcAt = null; fullAcMode = null;
                    string? thermal = recoveryObservation ? null : ThermalFailure(value.Before, false) ?? ThermalFailure(value.After, false);
                    if (thermal is not null)
                    {
                        failure ??= thermal;
                        recoveryObservation = true; SignalAbort();
                        if (!ExternalQuiesced) { return Finish(false, false, false, null); }
                    }
                    if (!batteryObserved && Elapsed() >= 10000) { failure ??= "NoPowerTransition"; break; }
                    await wait(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                    continue;
                }
                if (value.Failure is not null) { failure ??= value.Failure; return Finish(false, false, false, null); }
                if (Elapsed() >= 30000) { failure ??= "BoundaryDeadlineExceeded"; break; }
                byte mode = Mode(value.Snapshot.Pair)!.Value;
                bool battery = value.Snapshot.PowerAfter == target.Power;
                bool offAc = battery || value.Snapshot.PowerAfter == target.AbortPower;
                // Reconnect immediately on the first coherent battery observation; no deliberate dwell.
                if (battery && !batteryObserved)
                {
                    batteryObserved = true;
                    signal(Rz090528BatteryBoundarySignal.BatteryObservedReconnectNow);
                }
                // Only destination samples can show a firmware reversion; full-AC return modes are cleanup inputs.
                autoReversion |= battery && mode != 7;
                string? invalid = (recoveryObservation ? null : ThermalFailure(value.Before, false) ?? ThermalFailure(value.After, false))
                    ?? (offAc && !battery ? "UnexpectedPowerState" : null);
                if (invalid is not null || !value.Snapshot.PadAbsent)
                {
                    failure ??= invalid ?? "PadAbsenceLost";
                    recoveryObservation = true; SignalAbort();
                    if (!ExternalQuiesced) { return Finish(false, false, false, null); }
                    if (!offAc) { break; }
                }
                // A tripwire first seen on a transitional sample still exits from full AC without waiting.
                else if (recoveryObservation && !offAc && !batteryObserved) { break; }
                if (offAc) { fullAcAt = null; fullAcMode = null; }
                else if (batteryObserved)
                {
                    if (fullAcAt is { } previous && time.GetElapsedTime(previous).TotalMilliseconds >= 1000)
                    {
                        if (mode != fullAcMode) { poisoned = true; source.MarkSnapshotUncertain(); failure ??= "FullAcModeChanged"; return Finish(false, false, false, null); }
                        return Finish(failure is null, mode == 7, mode == 2, value.Snapshot.Pair);
                    }
                    fullAcAt ??= time.GetTimestamp();
                    fullAcMode ??= mode;
                }
                // While off AC the operator is reconnecting; only the overall deadline applies.
                if (!batteryObserved && !offAc && Elapsed() >= 10000) { failure ??= "NoPowerTransition"; break; }
                await wait(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }
            failure ??= "BoundaryDeadlineExceeded";
        }
        catch (OperationCanceledException) { failure ??= token.IsCancellationRequested ? "CanceledDuringBoundary" : "BoundaryDeadlineExceeded"; }
        catch { failure ??= "BoundaryObservationFailed"; }
        // Ordinary temperature/cancellation aborts can only escape through freshly proven full AC.
        SignalAbort();
        return await CleanupDecisionAsync(source).ConfigureAwait(false);
    }

    private async Task<Rz090528BatteryBoundaryDecision> CleanupDecisionAsync(IRz090528BatteryBoundarySource source)
    {
        if (poisoned || !SnapshotQuiesced || !ExternalQuiesced || readWindowsAc is null) { return Finish(false, false, false, null); }
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var first = await ObserveAsync(source, cleanup.Token, true).ConfigureAwait(false);
            if (first.Failure is not null || first.Snapshot.PowerAfter != "1111") { return Finish(false, false, false, null); }
            byte? mode = Mode(first.Snapshot.Pair);
            if (mode is not (2 or 7)) { return Finish(false, false, false, null); }
            await wait(TimeSpan.FromSeconds(1), cleanup.Token).ConfigureAwait(false);
            var second = await ObserveAsync(source, cleanup.Token, true).ConfigureAwait(false);
            return second.Failure is null && second.Snapshot.PowerAfter == "1111" && Mode(second.Snapshot.Pair) == mode
                ? Finish(false, mode == 7, mode == 2, second.Snapshot.Pair) : Finish(false, false, false, null);
        }
        catch { return Finish(false, false, false, null); }
    }

    private async Task<Rz090528BatteryBoundaryObservation> ObserveAsync(IRz090528BatteryBoundarySource source, CancellationToken token, bool windowsOnly = false, bool boundary = false)
    {
        token.ThrowIfCancellationRequested();
        TimeSpan budget = ReadBudget(boundary);
        Rz090528BatteryBoundaryExternal before = windowsOnly ? WindowsOnly() : await ExternalAsync(token, budget).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!ExternalQuiesced) { return new(time.GetUtcNow(), Elapsed(), before, new(null, null, null, false, "ExternalWorkerNotQuiesced"), before, "ExternalWorkerNotQuiesced"); }
        Rz090528BatteryBoundarySnapshot snapshot;
        if (poisoned || !SnapshotQuiesced)
        {
            snapshot = new(null, null, null, false, "SnapshotLaneClosed");
        }
        else
        {
            budget = ReadBudget(boundary);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(budget);
            CancellationToken boundedToken = timeout.Token;
            try
            {
                snapshotOperation = Task.Run(() => source.ReadBatteryBoundaryAsync(boundedToken));
                snapshot = await snapshotOperation.WaitAsync(boundedToken).ConfigureAwait(false);
                if (timeout.IsCancellationRequested) { throw new OperationCanceledException(); }
            }
            catch { poisoned = true; source.MarkSnapshotUncertain(); snapshot = new(null, null, null, false, "SnapshotReadUncertain"); }
        }
        Rz090528BatteryBoundaryExternal after = before;
        if (snapshot.Failure is null)
        {
            budget = ReadBudget(boundary);
            after = windowsOnly ? WindowsOnly() : await ExternalAsync(token, budget).ConfigureAwait(false);
        }
        string? invalid = snapshot.Failure;
        if (invalid is null && (!KnownPower(snapshot.PowerBefore) || !KnownPower(snapshot.PowerAfter)
            || snapshot.Pair is null || !KnownMode(snapshot.Pair.Thermal1Hex) || !KnownMode(snapshot.Pair.Thermal2Hex)))
        {
            invalid = "PowerThermalOrExternalStateConflict";
        }
        else if (invalid is null && (snapshot.PowerBefore != snapshot.PowerAfter
            || snapshot.Pair!.Thermal1Hex[4..] != snapshot.Pair.Thermal2Hex[4..]
            || before.WindowsAcOnline is null || after.WindowsAcOnline is null
            || before.WindowsAcOnline != after.WindowsAcOnline || after.WindowsAcOnline != ExpectedWindowsAc(snapshot.PowerAfter)))
        {
            // A physical plug event can land inside one bracket; this is not native uncertainty.
            invalid = Transitional;
        }
        else if (invalid is null && (Mode(snapshot.Pair) is null
            || snapshot.PowerAfter == "1111" && Mode(snapshot.Pair) is not (2 or 7)))
        {
            invalid = "PowerThermalOrExternalStateConflict";
        }
        if (invalid is not null and not Transitional) { poisoned = true; source.MarkSnapshotUncertain(); }
        var value = new Rz090528BatteryBoundaryObservation(time.GetUtcNow(), Elapsed(), before, snapshot, after, invalid);
        observations.Add(value);
        return value;
    }

    private TimeSpan ReadBudget(bool boundary)
    {
        double remaining = boundary ? 30000 - Elapsed() : 2000;
        if (remaining <= 0) { throw new OperationCanceledException("Boundary deadline reached before dispatch."); }
        return TimeSpan.FromMilliseconds(Math.Min(2000, remaining));
    }

    private async Task<Rz090528BatteryBoundaryExternal> ExternalAsync(CancellationToken token, TimeSpan budget)
    {
        if (externalClosed || !ExternalQuiesced) { return FailedExternal(); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(budget);
        CancellationToken boundedToken = timeout.Token;
        try
        {
            externalOperation = Task.Run(() => readExternal(boundedToken).AsTask());
            var value = await externalOperation.WaitAsync(boundedToken).ConfigureAwait(false);
            if (timeout.IsCancellationRequested) { throw new OperationCanceledException(); }
            return value;
        }
        catch
        {
            externalClosed = true;
            return ExternalQuiesced ? FailedExternal() with { WindowsAcOnline = readWindowsAc?.Invoke() } : FailedExternal();
        }
    }

    private Rz090528BatteryBoundaryExternal WindowsOnly() =>
        new(default, 0, null, null, null, readWindowsAc?.Invoke(), false, false, "RecoveryPowerOnly");
    private static Rz090528BatteryBoundaryExternal FailedExternal() =>
        new(default, double.NaN, null, null, null, null, false, false, "ExternalReadUncertain");
    private string? ThermalFailure(Rz090528BatteryBoundaryExternal value, bool entry)
    {
        if (value.Failure is not null) { return value.Failure; }
        double age = (time.GetUtcNow() - value.CpuSourceTimestampUtc).TotalMilliseconds;
        if (!double.IsFinite(value.CpuSourceAgeMilliseconds) || value.CpuSourceAgeMilliseconds is < 0 or > 4000 || age is < 0 or > 4000)
        { return "CpuSourceNotFresh"; }
        if (value.CpuCelsius is not { } cpu || !double.IsFinite(cpu) || cpu <= 0 || value.GpuCelsius is not > 0
            || !value.NoWorkloadConfirmed || !value.GpuZeroConfirmed || value.GpuUtilizationPercent is not <= 10) { return "NoLoadTelemetryRequired"; }
        return entry ? cpu > 70 || value.GpuCelsius > 72 ? "EntryTemperatureEnvelopeExceeded" : null
            : cpu >= 75 || value.GpuCelsius >= 75 ? "ExperimentalTemperatureTripwire" : null;
    }
    private static byte? Mode(PerformanceFanThermalPair? pair) => pair switch
    {
        { Thermal1Hex: "01010200", Thermal2Hex: "01020200" } => 2,
        { Thermal1Hex: "01010700", Thermal2Hex: "01020700" } => 7,
        { Thermal1Hex: "01010300", Thermal2Hex: "01020300" } => 3,
        { Thermal1Hex: "01010000", Thermal2Hex: "01020000" } => 0,
        _ => null,
    };
    private bool KnownPower(string? power) => power == "1111" || power == target.Power || power is not null && power == target.AbortPower;
    private bool ExpectedWindowsAc(string? power) => power == "1111" || power == target.Power && target.WindowsAcOnline;
    private bool KnownMode(string hex) => hex.Length == 8 && hex[..4] is "0101" or "0102" && hex[6..] == "00"
        && target.ObservedModes.Any(mode => hex[4..6] == mode.ToString("X2"));
    private void SignalAbort()
    {
        // Tell the present operator at once to keep or restore full AC; emitted at most once after arming.
        if (!runArmed || abortSignaled) { return; }
        abortSignaled = true;
        try { signal(Rz090528BatteryBoundarySignal.AbortReconnectAc); } catch { }
    }
    private double Elapsed() => time.GetElapsedTime(started).TotalMilliseconds;
    private Rz090528BatteryBoundaryDecision Finish(bool passed, bool restore, bool already, PerformanceFanThermalPair? pair)
    {
        if (!passed) { SignalAbort(); }
        Update(passed, !restore && !already);
        return new(passed, restore, already, pair);
    }
    private void Update(bool passed, bool manual) => Result = new(passed, failure, batteryObserved, autoReversion,
        null, false, false, false, manual, false, SnapshotQuiesced, ExternalQuiesced, observations.ToArray());

    internal void CompleteFromRunner(PerformanceFanSetterValidationResult result, bool hardwareDisposed)
    {
        Result ??= new(false, "BoundaryNotReached", false, false, null, false, false, false,
            result.ManualRecoveryRequired, false, SnapshotQuiesced, ExternalQuiesced, observations.ToArray());
        Result = Result with
        {
            Completed = Result.Completed && result.Success,
            FinalPerformancePair = result.Restored ? result.Restoration : null,
            RestoreWriteAttempted = result.RestoreAttempted,
            RestoreWriteAcknowledged = result.RestoreAcknowledged,
            Restored = result.Restored,
            ManualRecoveryRequired = Result.ManualRecoveryRequired || result.ManualRecoveryRequired || result.IndeterminatePartialWrite,
            HardwareQuiesced = hardwareDisposed && SnapshotQuiesced && ExternalQuiesced,
            SnapshotQuiesced = SnapshotQuiesced,
            ExternalReadQuiesced = ExternalQuiesced
        };
    }
}
