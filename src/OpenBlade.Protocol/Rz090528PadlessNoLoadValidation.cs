using System.Security.Cryptography;

namespace OpenBlade.Protocol;

internal sealed record Rz090528NoLoadSnapshot(FanRpmReadings? Fans, string? Failure, bool PermitKnownStateRestore);
internal interface IRz090528NoLoadSnapshotSource
{
    Task<Rz090528NoLoadSnapshot> ReadAsync(bool baseline, CancellationToken token);
}
internal sealed record Rz090528NoLoadSample(DateTimeOffset ObservedAtUtc, double ElapsedMilliseconds,
    int? CpuCelsius, int? GpuCelsius, bool GpuInactive, FanRpmReadings? Fans);
internal sealed record Rz090528NoLoadResult(bool Completed, string? Failure, bool PermitKnownStateRestore,
    int? CpuAbortCelsius, int? GpuAbortCelsius, IReadOnlyList<Rz090528NoLoadSample> Baseline,
    IReadOnlyList<Rz090528NoLoadSample> Samples, double ElapsedMilliseconds);

// Experimental tripwires relative to this machine's fresh idle baseline, not hardware ratings.
internal sealed class Rz090528PadlessNoLoadValidation(
    IHyperBoostThermalTelemetrySource telemetry, TimeProvider? clock = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    internal const string PlanSha256 = "A47C96694A5B1A0CF69E72DC58DC25A225FEF8C99E1CD846C5713E2A06AC9B24";
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? Task.Delay;
    private readonly List<Rz090528NoLoadSample> baseline = [];
    private readonly List<Rz090528NoLoadSample> samples = [];
    private int? cpuAbort, gpuAbort;
    private bool gpuInactive;
    internal Rz090528NoLoadResult? Result { get; private set; }

    internal static byte[] ReadPlan()
    {
        using Stream stream = typeof(Rz090528PadlessNoLoadValidation).Assembly.GetManifestResourceStream(
            "OpenBlade.Protocol.Fixtures.Rz09_0528_02c6_2_02_PadlessHyperBoostNoLoadPlan.json")
            ?? throw new InvalidOperationException("The reviewed no-load plan is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        byte[] bytes = memory.ToArray();
        ValidatePlan(bytes);
        return bytes;
    }
    internal static void ValidatePlan(ReadOnlySpan<byte> bytes)
    {
        if (Convert.ToHexString(SHA256.HashData(bytes)) != PlanSha256)
        {
            throw new InvalidOperationException("No-load plan hash mismatch.");
        }
    }

    internal async Task<bool> PrepareAsync(IRz090528NoLoadSnapshotSource source, CancellationToken token)
    {
        _ = ReadPlan();
        long started = time.GetTimestamp();
        try
        {
            for (int index = 0; index < 3; index++)
            {
                await WaitUntilAsync(started, index * 2000, token).ConfigureAwait(false);
                if (index > 0 && Elapsed(started) - baseline[^1].ElapsedMilliseconds > 4000)
                {
                    return Finish(false, "BaselineSamplingStale", false, started).Completed;
                }

                var protocol = await SnapshotAsync(source, true, token).ConfigureAwait(false);
                if (protocol.Failure is not null)
                {
                    return Finish(false, protocol.Failure, false, started).Completed;
                }

                HyperBoostTemperatureObservation? value = await TemperatureAsync(token).ConfigureAwait(false);
                if (value is not { CpuCelsius: > 0 and <= 50 } temperature
                    || !(temperature.GpuCelsius is > 0 and <= 50 || temperature.GpuCelsius is null && temperature.GpuInactive))
                {
                    return Finish(false, "BaselineTelemetryUnavailableOrOutsideEntryEnvelope", false, started).Completed;
                }

                baseline.Add(new(time.GetUtcNow(), Elapsed(started), temperature.CpuCelsius,
                    temperature.GpuCelsius, temperature.GpuInactive, protocol.Fans));
            }
            int[] cpus = baseline.Select(value => value.CpuCelsius!.Value).Order().ToArray();
            gpuInactive = baseline.All(value => value.GpuCelsius is null && value.GpuInactive);
            if (cpus[^1] - cpus[0] > 3 || (!gpuInactive && baseline.Any(value => value.GpuCelsius is null)))
            {
                return Finish(false, "BaselineUnstable", false, started).Completed;
            }

            cpuAbort = Math.Min(cpus[1] + 10, 60);
            if (!gpuInactive)
            {
                int[] gpus = baseline.Select(value => value.GpuCelsius!.Value).Order().ToArray();
                if (gpus[^1] - gpus[0] > 3)
                {
                    return Finish(false, "GpuBaselineUnstable", false, started).Completed;
                }

                gpuAbort = Math.Min(gpus[1] + 10, 60);
            }
            Result = new(false, "AwaitingHyperBoostCandidate", false, cpuAbort, gpuAbort,
                baseline.ToArray(), [], 0);
            return true;
        }
        catch (OperationCanceledException) { Finish(false, "CanceledBeforeApply", false, started); return false; }
        catch { Finish(false, "BaselineObservationFailed", false, started); return false; }
    }

    internal async Task<Rz090528NoLoadResult> RunAsync(IRz090528NoLoadSnapshotSource source, CancellationToken token)
    {
        long started = time.GetTimestamp();
        if (cpuAbort is null || baseline.Count != 3)
        {
            return Finish(false, "BaselineNotPrepared", false, started);
        }

        try
        {
            for (int index = 0; index < 30; index++)
            {
                await WaitUntilAsync(started, index * 2000, token).ConfigureAwait(false);
                if (Elapsed(started) >= 60000 || (samples.Count > 0 && Elapsed(started) - samples[^1].ElapsedMilliseconds > 4000))
                {
                    return Finish(false, "SamplingDeadlineMissed", true, started);
                }

                var protocol = await SnapshotAsync(source, false, token, Math.Min(2000, 60000 - Elapsed(started))).ConfigureAwait(false);
                if (protocol.Failure is not null)
                {
                    return Finish(false, protocol.Failure, protocol.PermitKnownStateRestore, started);
                }

                HyperBoostTemperatureObservation? value = await TemperatureAsync(token,
                    Math.Min(2000, 60000 - Elapsed(started))).ConfigureAwait(false);
                if (value is not { CpuCelsius: > 0 } temperature)
                {
                    return Finish(false, "CpuTelemetryLost", true, started);
                }

                samples.Add(new(time.GetUtcNow(), Elapsed(started), temperature.CpuCelsius,
                    temperature.GpuCelsius, temperature.GpuInactive, protocol.Fans));
                if (Elapsed(started) >= 60000 || (samples.Count > 1 && samples[^1].ElapsedMilliseconds - samples[^2].ElapsedMilliseconds > 4000))
                {
                    return Finish(false, "SamplingDeadlineMissed", true, started);
                }

                if (temperature.CpuCelsius >= cpuAbort)
                {
                    return Finish(false, "CpuExperimentalTripwire", true, started);
                }

                if (gpuInactive && !(temperature.GpuCelsius is null && temperature.GpuInactive))
                {
                    return Finish(false, "GpuActivatedWithoutBaseline", true, started);
                }

                if (!gpuInactive && temperature.GpuCelsius is not > 0)
                {
                    return Finish(false, "GpuTelemetryLost", true, started);
                }

                if (!gpuInactive && temperature.GpuCelsius >= gpuAbort)
                {
                    return Finish(false, "GpuExperimentalTripwire", true, started);
                }
            }
            await WaitUntilAsync(started, 60000, token).ConfigureAwait(false);
            return Finish(true, null, true, started);
        }
        catch (OperationCanceledException) { return Finish(false, "Canceled", true, started); }
        catch { return Finish(false, "UnexpectedObservationFailure", false, started); }
    }

    private async Task<Rz090528NoLoadSnapshot> SnapshotAsync(IRz090528NoLoadSnapshotSource source, bool initial, CancellationToken token,
        double budgetMilliseconds = 2000)
    {
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, budgetMilliseconds)));
        try { return await source.ReadAsync(initial, timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch { return new(null, "ProtocolSnapshotUncertain", false); }
    }
    private async Task<HyperBoostTemperatureObservation?> TemperatureAsync(CancellationToken token, double budgetMilliseconds = 2000)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (budgetMilliseconds <= 0)
        {
            return null;
        }

        timeout.CancelAfter(TimeSpan.FromMilliseconds(budgetMilliseconds));
        try { return await telemetry.ReadAsync(timeout.Token).AsTask().WaitAsync(timeout.Token).ConfigureAwait(false); }
        catch { return null; }
    }
    private double Elapsed(long started) => time.GetElapsedTime(started).TotalMilliseconds;
    private async Task WaitUntilAsync(long started, double milliseconds, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        double remaining;
        while ((remaining = milliseconds - Elapsed(started)) > 0)
        {
            await wait(TimeSpan.FromMilliseconds(Math.Max(1, remaining)), token).ConfigureAwait(false);
        }
    }
    private Rz090528NoLoadResult Finish(bool complete, string? failure, bool restore, long started) =>
        Result = new(complete, failure, restore, cpuAbort, gpuAbort, baseline.ToArray(), samples.ToArray(), Elapsed(started));
}
